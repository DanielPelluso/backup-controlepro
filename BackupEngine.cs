using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BackupControlePro;

public sealed class BackupResult
{
    public bool Success { get; set; }
    public string Folder { get; set; } = "";
    public string Stamp { get; set; } = "";
    public string? Error { get; set; }
    public TimeSpan Elapsed { get; set; }
    public long SchemaBytes { get; set; }
    public long DataBytes { get; set; }
    public long CustomBytes { get; set; }
    public bool StorageIncluded { get; set; }
    public int StorageFiles { get; set; }
    public long StorageBytes { get; set; }
}

/// <summary>Backup completo: banco (pg_dump) + arquivos do Storage.</summary>
public static class BackupEngine
{
    public static string NewStamp() => DateTime.Now.ToString(AppConfig.StampFormat);

    public static BackupResult Run(bool includeStorage, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        var stamp = NewStamp();
        var folder = Path.Combine(AppConfig.BackupRoot, stamp);
        var result = new BackupResult
        {
            Stamp = stamp,
            Folder = folder,
            StorageIncluded = includeStorage,
        };
        var errors = new List<string>();

        try
        {
            Directory.CreateDirectory(folder);
            log($"-> backup {stamp}{(includeStorage ? " (banco + fotos)" : " (banco)")}");

            var schemaFile = Path.Combine(folder, $"schema-{stamp}.sql");
            var dataFile = Path.Combine(folder, $"data-{stamp}.sql");
            var customFile = Path.Combine(folder, $"dump-{stamp}.backup");

            RunPgDump(log, "--format=plain", schemaFile);
            RunPgDump(log, "--format=plain --data-only", dataFile);
            RunPgDump(log, "--format=custom", customFile);

            result.SchemaBytes = Require(schemaFile, "schema");
            result.DataBytes = Require(dataFile, "dados");
            result.CustomBytes = Require(customFile, "custom");

            // Privilégios (GRANTs/default privileges) e dependentes fora do public
            // (policies do storage) — sem isso a restauração fica incompleta.
            PrivilegeEngine.Write(folder, log);

            if (includeStorage)
            {
                var storage = StorageBackupEngine.Run(folder, log);
                if (!storage.Success)
                    throw new InvalidOperationException($"backup de fotos falhou: {storage.Error}");
                result.StorageFiles = storage.Files;
                result.StorageBytes = storage.Bytes;
            }

            result.Success = true;
            WriteMeta(folder, result, errors);
            sw.Stop();
            result.Elapsed = sw.Elapsed;
            log(
                $"   OK: schema {Kb(result.SchemaBytes)} | dados {Kb(result.DataBytes)} | " +
                $"custom {Kb(result.CustomBytes)}" +
                (includeStorage ? $" | fotos {result.StorageFiles} ({Kb(result.StorageBytes)})" : "") +
                $" ({sw.Elapsed.TotalSeconds:F1}s)");
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            sw.Stop();
            result.Success = false;
            result.Error = ex.Message;
            result.Elapsed = sw.Elapsed;
            log($"   FALHA: {ex.Message}");
            try { WriteMeta(folder, result, errors); }
            catch { /* meta é melhor esforço no caminho de falha */ }
        }

        return result;
    }

    private static void WriteMeta(string folder, BackupResult result, List<string> errors)
    {
        var meta = new
        {
            stamp = result.Stamp,
            createdAt = DateTime.Now.ToString("o"),
            host = Environment.MachineName,
            user = Environment.UserName,
            status = result.Success ? "ok" : "failed",
            schemaBytes = result.SchemaBytes,
            dataBytes = result.DataBytes,
            customBytes = result.CustomBytes,
            privileges = File.Exists(Path.Combine(folder, "privileges.sql")),
            externalDeps = File.Exists(Path.Combine(folder, "external-deps.sql")),
            storage = result.StorageIncluded
                ? new
                {
                    included = true,
                    files = result.StorageFiles,
                    bytes = result.StorageBytes,
                    manifest = File.Exists(Path.Combine(folder, "storage", "storage-manifest.json")),
                }
                : (object?)new { included = false },
            errors,
        };
        File.WriteAllText(
            Path.Combine(folder, "meta.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static void RunPgDump(Action<string> log, string extraArgs, string file)
    {
        var args = new List<string>
        {
            "--host", AppConfig.PoolerHost,
            "--port", AppConfig.PoolerPort.ToString(),
            "--username", AppConfig.DbUser,
            "--dbname", AppConfig.DbName,
            "--schema=public", "--schema=auth", "--schema=storage", "--schema=supabase_migrations",
            "--no-owner", "--no-privileges",
        };
        args.AddRange(extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        args.Add("--file");
        args.Add(file);

        var (exit, output) = Run(AppConfig.PgDumpExe, args);
        if (exit != 0)
            throw new InvalidOperationException($"pg_dump exit {exit}: {LastLines(output)}");
        if (!string.IsNullOrWhiteSpace(output))
            log("   " + LastLines(output, 2));
    }

    private static long Require(string path, string label)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"arquivo ausente ({label}): {path}");
        var len = new FileInfo(path).Length;
        if (len <= 0)
            throw new InvalidOperationException($"arquivo vazio ({label}): {path}");
        return len;
    }

    internal static (int Exit, string Output) Run(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment["PGPASSWORD"] = AppConfig.DbPassword;
        psi.Environment["PGSSLMODE"] = AppConfig.PgSslMode;
        psi.Environment["PGGSSENCMODE"] = "disable";

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"não foi possível iniciar {exe}");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        return (p.ExitCode, (stdoutTask.Result + stderrTask.Result).Trim());
    }

    /// <summary>psql com as credenciais/destino configurados em AppConfig.</summary>
    internal static (int Exit, string Output) RunPsql(IEnumerable<string> args) =>
        Run(AppConfig.PsqlExe,
            new List<string>
            {
                "--host", AppConfig.PoolerHost,
                "--port", AppConfig.PoolerPort.ToString(),
                "--username", AppConfig.DbUser,
                "--dbname", AppConfig.DbName,
            }.Concat(args).ToList());

    private static string LastLines(string s, int n = 3)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - n)).Select(l => l.Trim()));
    }

    private static string Kb(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):F1} MB" : $"{bytes / 1024} KB";
}
