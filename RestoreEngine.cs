using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BackupControlePro;

public sealed class BackupEntry
{
    public string Folder { get; init; } = "";
    public string Stamp { get; init; } = "";
    public DateTime When { get; init; }
    public long DataBytes { get; init; }
    public long CustomBytes { get; init; }
    public bool HasData { get; init; }
    public bool HasCustom { get; init; }
    public bool HasStorage { get; init; }
    public int StorageFiles { get; init; }
    public long StorageBytes { get; init; }

    public string DisplayWhen => When.ToString("dd/MM/yyyy HH:mm:ss");

    public override string ToString() =>
        $"{Stamp}  ·  {DisplayWhen}  ·  {DataBytes / 1024} KB" +
        (HasCustom ? " + custom" : "") +
        (HasStorage ? $" + fotos ({StorageFiles})" : "");
}

/// <summary>Restauração a partir das pastas de backup ao lado do EXE.</summary>
public static class RestoreEngine
{
    private static readonly Regex StampDir = new(@"^\d{12}$", RegexOptions.Compiled);

    public static List<BackupEntry> ListAll()
    {
        var root = AppConfig.BackupRoot;
        if (!Directory.Exists(root)) return [];

        return Directory.GetDirectories(root)
            .Select(d => Path.GetFileName(d))
            .Where(name => name != null && StampDir.IsMatch(name))
            .Select(name => BuildEntry(Path.Combine(root, name!), name!))
            .Where(e => e != null)
            .Select(e => e!)
            .Where(e => e.HasData || e.HasCustom)
            .OrderByDescending(e => e.When)
            .ToList();
    }

    public static List<BackupEntry> ListRecent(int count) =>
        ListAll().Take(count).ToList();

    private static BackupEntry? BuildEntry(string dir, string stamp)
    {
        DateTime when;
        try
        {
            when = DateTime.ParseExact(stamp, AppConfig.StampFormat,
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }

        // Backups com status=failed (ex.: falha no Storage) não entram na lista.
        var metaPath = Path.Combine(dir, "meta.json");
        if (File.Exists(metaPath))
        {
            try
            {
                using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (meta.RootElement.TryGetProperty("status", out var status) &&
                    string.Equals(status.GetString(), "failed", StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            catch { /* meta ilegível não bloqueia dumps legítimos */ }
        }

        var data = Directory.GetFiles(dir, "data-*.sql").FirstOrDefault();
        var custom = Directory.GetFiles(dir, "dump-*.backup").FirstOrDefault();

        var storageManifest = Path.Combine(dir, "storage", "storage-manifest.json");
        var hasStorage = File.Exists(storageManifest);
        var storageFiles = 0;
        long storageBytes = 0;
        if (hasStorage)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(storageManifest));
                if (doc.RootElement.TryGetProperty("totals", out var totals) ||
                    doc.RootElement.TryGetProperty("Totals", out totals))
                {
                    if (totals.TryGetProperty("files", out var f) ||
                        totals.TryGetProperty("Files", out f))
                        storageFiles = f.GetInt32();
                    if (totals.TryGetProperty("bytes", out var b) ||
                        totals.TryGetProperty("Bytes", out b))
                        storageBytes = b.GetInt64();
                }
            }
            catch { /* manifesto ilegível: lista sem totais */ }
        }

        return new BackupEntry
        {
            Folder = dir,
            Stamp = stamp,
            When = when,
            HasData = data != null,
            DataBytes = data != null ? new FileInfo(data).Length : 0,
            HasCustom = custom != null,
            CustomBytes = custom != null ? new FileInfo(custom).Length : 0,
            HasStorage = hasStorage,
            StorageFiles = storageFiles,
            StorageBytes = storageBytes,
        };
    }

    /// <summary>Restaura DADOS (truncate + reload com FKs desligadas).</summary>
    public static void RestoreData(BackupEntry entry, Action<string> log)
    {
        var dataFile = Directory.GetFiles(entry.Folder, "data-*.sql").FirstOrDefault()
            ?? throw new InvalidOperationException("data-*.sql ausente neste backup");
        log("-> restaurando DADOS");

        var dump = File.ReadAllText(dataFile);
        var skip = new HashSet<string>
        {
            "supabase_migrations.schema_migrations",
            "auth.schema_migrations",
        };

        var tables = Regex.Matches(dump, @"^COPY ([\w.]+) \(", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Where(t => !skip.Contains(t) && !t.StartsWith("storage.", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        if (tables.Count == 0)
            throw new InvalidOperationException("nenhuma tabela COPY encontrada no dump");

        var body = Regex.Replace(
            dump,
            @"^COPY (supabase_migrations\.schema_migrations|auth\.schema_migrations|storage\.[\w]+) \([\s\S]*?^\\\.\s*$",
            "", RegexOptions.Multiline);
        body = body.Replace(
            "set_config('search_path', '', false)",
            "set_config('search_path', 'public, extensions', false)");

        var sql = new StringBuilder()
            .AppendLine("set session_replication_role = replica;")
            .AppendLine("set search_path = public, extensions, pg_catalog;")
            .AppendLine($"truncate table {string.Join(", ", tables)};")
            .Append(body)
            .AppendLine()
            .AppendLine("set session_replication_role = origin;")
            .ToString();

        var tmp = Path.Combine(Path.GetTempPath(),
            $"controlepro-restore-{DateTime.Now:yyyyMMddHHmmss}.sql");
        File.WriteAllText(tmp, sql, new UTF8Encoding(false));
        try
        {
            log($"   {tables.Count} tabelas; executando psql...");
            var (exit, output) = RunPsql(["-v", "ON_ERROR_STOP=1", "-f", tmp]);
            if (exit != 0)
                throw new InvalidOperationException($"psql exit {exit}: {Tail(output)}");
            log("   OK: dados restaurados");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignora */ }
        }
    }

    /// <summary>
    /// Restaura TUDO (schema + dados do public) via pg_restore --clean.
    /// Só o schema public: auth/storage/supabase_migrations são do plataforma
    /// Supabase (sem permissão de dono).
    /// </summary>
    public static void RestoreFull(BackupEntry entry, Action<string> log)
    {
        var custom = Directory.GetFiles(entry.Folder, "dump-*.backup").FirstOrDefault()
            ?? throw new InvalidOperationException("dump-*.backup ausente neste backup");
        log("-> restaurando TUDO (schema + dados, public)");

        var args = new[]
        {
            "--host", AppConfig.PoolerHost,
            "--port", AppConfig.PoolerPort.ToString(),
            "--username", AppConfig.DbUser,
            "--dbname", AppConfig.DbName,
            "--schema=public",
            "--clean", "--if-exists", "--no-owner", "--no-privileges", "--disable-triggers",
            custom,
        };
        var (exit, output) = BackupEngine.Run(AppConfig.PgRestoreExe, args);
        if (exit != 0)
        {
            var errs = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains("error:", StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .Select(l => l.Trim());
            var detail = errs.Any() ? string.Join(" | ", errs) : Tail(output);
            throw new InvalidOperationException($"pg_restore exit {exit}: {detail}");
        }
        log("   OK: schema+dados restaurados (public)");
    }

    /// <summary>Confere sequences (last_value &gt;= max) — evita duplicate key após restore.</summary>
    public static void CheckSequences(Action<string> log)
    {
        log("-> verificando sequences...");
        var listSql = """
            select ns.nspname as seq_schema, cls.relname as seq_name,
                   tns.nspname as tab_schema, tbl.relname as tab_name, att.attname as col_name
              from pg_class cls
              join pg_namespace ns on ns.oid = cls.relnamespace
              join pg_depend dep on dep.objid = cls.oid
                   and dep.classid = 'pg_class'::regclass and dep.deptype in ('a','i')
              join pg_class tbl on tbl.oid = dep.refobjid
              join pg_namespace tns on tns.oid = tbl.relnamespace
              join pg_attribute att on att.attrelid = tbl.oid and att.attnum = dep.refobjsubid
             where cls.relkind = 'S' and ns.nspname in ('public','auth','storage')
             order by 1,2
            """;

        var (lexit, lout) = RunPsql(["-At", "-c", listSql]);
        if (lexit != 0)
            throw new InvalidOperationException($"psql (lista sequences) exit {lexit}: {Tail(lout)}");

        var seqs = lout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split('|'))
            .Where(p => p.Length == 5)
            .ToList();
        if (seqs.Count == 0) { log("   nenhuma sequence encontrada"); return; }

        var qi = (string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var checks = new StringBuilder();
        foreach (var p in seqs)
        {
            checks.AppendLine(
                $"select 'CHK|{p[0]}.{p[1]}|' || " +
                $"(select last_value from {qi(p[0])}.{qi(p[1])}) || '|' || " +
                $"(select coalesce(max({qi(p[4])}),0) from {qi(p[2])}.{qi(p[3])});");
        }

        var tmp = Path.Combine(Path.GetTempPath(),
            $"controlepro-seqcheck-{DateTime.Now:yyyyMMddHHmmss}.sql");
        File.WriteAllText(tmp, checks.ToString(), new UTF8Encoding(false));
        try
        {
            var (cexit, cout) = RunPsql(["-At", "-v", "ON_ERROR_STOP=1", "-f", tmp]);
            if (cexit != 0)
                throw new InvalidOperationException($"psql (check) exit {cexit}: {Tail(cout)}");

            var atrasadas = new List<string>();
            foreach (var line in cout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('|');
                if (parts.Length != 4 || parts[0] != "CHK") continue;
                if (!long.TryParse(parts[2], out var last)) continue;
                if (!long.TryParse(parts[3], out var max)) continue;
                if (last < max)
                    atrasadas.Add($"{parts[1]}: last_value={last} < max={max}");
            }

            if (atrasadas.Count > 0)
                throw new InvalidOperationException(
                    "sequences atrasadas (risco de duplicate key): " +
                    string.Join("; ", atrasadas));
            log($"   OK: {seqs.Count} sequences verificadas");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignora */ }
        }
    }

    private static (int Exit, string Output) RunPsql(IEnumerable<string> args) =>
        BackupEngine.Run(AppConfig.PsqlExe,
            new List<string>
            {
                "--host", AppConfig.PoolerHost,
                "--port", AppConfig.PoolerPort.ToString(),
                "--username", AppConfig.DbUser,
                "--dbname", AppConfig.DbName,
            }.Concat(args).ToList());

    private static string Tail(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.TakeLast(3).Select(l => l.Trim()));
    }
}
