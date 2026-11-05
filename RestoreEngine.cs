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
    public static void RestoreData(BackupEntry entry, Action<string> log, bool dryRun = false)
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

        var runFile = WriteRunFile($"controlepro-restore-{DateTime.Now:yyyyMMddHHmmss}.sql", sql, dryRun);
        try
        {
            log($"   {tables.Count} tabelas; executando psql{(dryRun ? " (dry-run)" : "")}...");
            var (exit, output) = RunPsql(dryRun
                ? ["-v", "ON_ERROR_STOP=1", "-f", runFile]
                : ["-1", "-v", "ON_ERROR_STOP=1", "-f", runFile]);
            if (exit != 0)
                throw new InvalidOperationException($"psql exit {exit}: {Tail(output)}");
            log(dryRun ? "   OK (dry-run): transação revertida, nada aplicado" : "   OK: dados restaurados");
        }
        finally
        {
            try { File.Delete(runFile); } catch { /* ignora */ }
        }
    }

    /// <summary>
    /// Restaura TUDO (schema + dados do public) com rebuild atômico:
    /// DROP SCHEMA public CASCADE → recria schema/extensões → dump (pg_restore
    /// sem --clean) → privileges.sql → external-deps.sql, tudo em UMA transação
    /// (psql -1). Qualquer erro reverte inteiro — o banco fica como estava.
    /// Corrige os problemas da versão anterior com --clean: dependências fora do
    /// public (policies do storage), search_path='' (quebra unaccent/pg_trgm) e
    /// permissões ausentes.
    /// </summary>
    public static void RestoreFull(BackupEntry entry, Action<string> log, bool dryRun = false)
    {
        var custom = Directory.GetFiles(entry.Folder, "dump-*.backup").FirstOrDefault()
            ?? throw new InvalidOperationException("dump-*.backup ausente neste backup");
        log(dryRun
            ? "-> DRY-RUN: rebuild do schema public (será revertido)"
            : "-> restaurando TUDO (rebuild atômico do schema public)");

        // 1) dump → SQL puro (sem conexão; filtro de TOC em --schema=public)
        var dumpSql = Path.Combine(Path.GetTempPath(),
            $"controlepro-dumpsql-{DateTime.Now:yyyyMMddHHmmss}.sql");
        var (dexit, doutput) = BackupEngine.Run(AppConfig.PgRestoreExe,
            ["--schema=public", "--no-owner", "--no-privileges", "-f", dumpSql, custom]);
        if (dexit != 0)
        {
            try { File.Delete(dumpSql); } catch { /* ignora */ }
            throw new InvalidOperationException($"pg_restore exit {dexit}: {Tail(doutput)}");
        }

        string body;
        try { body = File.ReadAllText(dumpSql); }
        finally { try { File.Delete(dumpSql); } catch { /* ignora */ } }

        // 2) correções no SQL do dump
        //    a) search_path='' esconde unaccent/pg_trgm (extensões em public) e
        //       quebra a criação de f_unaccent/índices de busca;
        //    b) o schema já foi criado no prelúdio.
        body = body.Replace(
            "set_config('search_path', '', false)",
            "set_config('search_path', 'public, extensions, pg_catalog', false)");
        body = Regex.Replace(
            body, @"(?m)^CREATE SCHEMA public;$", "CREATE SCHEMA IF NOT EXISTS public;");

        // 3) monta o script completo
        var script = new StringBuilder();
        script.AppendLine("-- BackupControlePro: rebuild atômico do schema public");
        script.AppendLine("DROP SCHEMA public CASCADE;");
        script.AppendLine("CREATE SCHEMA public;");
        script.AppendLine("ALTER SCHEMA public OWNER TO pg_database_owner;");
        script.AppendLine("CREATE EXTENSION IF NOT EXISTS unaccent WITH SCHEMA public;");
        script.AppendLine("CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;");

        var privPath = Path.Combine(entry.Folder, "privileges.sql");
        if (File.Exists(privPath))
        {
            script.AppendLine(Section(File.ReadAllText(privPath), "@pre"));
        }
        else
        {
            log("   AVISO: privileges.sql ausente (backup antigo) — " +
                "GRANTs/default privileges NÃO serão restaurados");
        }

        script.AppendLine(body);

        if (File.Exists(privPath))
            script.AppendLine(Section(File.ReadAllText(privPath), "@post"));

        var extPath = Path.Combine(entry.Folder, "external-deps.sql");
        if (File.Exists(extPath))
        {
            script.AppendLine(File.ReadAllText(extPath));
        }
        else
        {
            log("   AVISO: external-deps.sql ausente (backup antigo) — " +
                "policies fora do public (ex.: fotos) não serão recriadas");
        }

        // 4) executa: transação única (ou BEGIN/ROLLBACK no dry-run)
        var runFile = WriteRunFile(
            $"controlepro-full-{DateTime.Now:yyyyMMddHHmmss}.sql", script.ToString(), dryRun);
        try
        {
            var (exit, output) = RunPsql(dryRun
                ? ["-v", "ON_ERROR_STOP=1", "-f", runFile]
                : ["-1", "-v", "ON_ERROR_STOP=1", "-f", runFile]);
            if (exit != 0)
            {
                var hint = dryRun
                    ? " (dry-run revertido — banco intacto)"
                    : " (transação revertida — banco intacto)";
                throw new InvalidOperationException($"psql exit {exit}: {Tail(output)}{hint}");
            }
            log(dryRun
                ? "   DRY-RUN OK: transação revertida (nenhuma alteração aplicada)"
                : "   OK: schema public reconstruído (transação única)");
        }
        finally
        {
            try { File.Delete(runFile); } catch { /* ignora */ }
        }
    }

    /// <summary>Grava o SQL para execução; dry-run embrulha em BEGIN ... ROLLBACK.</summary>
    private static string WriteRunFile(string name, string sql, bool dryRun)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        var body = dryRun ? "BEGIN;\n" + sql + "\nROLLBACK;\n" : sql;
        File.WriteAllText(path, body, new UTF8Encoding(false));
        return path;
    }

    /// <summary>Extrai a seção "-- @pre"/"-- @post" de privileges.sql.</summary>
    private static string Section(string content, string marker)
    {
        var lines = content.Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == $"-- {marker}") { start = i + 1; break; }
        }
        if (start < 0) return "";

        var sb = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("-- @", StringComparison.Ordinal)) break;
            sb.AppendLine(lines[i].TrimEnd('\r'));
        }
        return sb.ToString();
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
        BackupEngine.RunPsql(args);

    private static string Tail(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.TakeLast(3).Select(l => l.Trim()));
    }
}
