using System.Text;
using System.Text.Json;

namespace BackupControlePro;

/// <summary>
/// Gera, junto do backup, dois arquivos que tornam a restauração autocontida:
///   privileges.sql  — GRANTs + default privileges do schema public
///                     (seções -- @pre / -- @post, aplicadas antes/depois do dump);
///   external-deps.sql — objetos FORA do public que dependem do public
///                     (as policies do storage que referenciam funções do app).
/// Sem eles, um rebuild do schema public perde permissões e quebra as policies
/// de fotos (elas são derrubadas em cascata quando as funções somem).
/// </summary>
public static class PrivilegeEngine
{
    // ------------------------------------------------------------
    // Consultas
    // ------------------------------------------------------------

    private const string DefaultPrivilegesSql = """
        select format('alter default privileges for role %I in schema public grant %s on %s to %s;',
               r.rolname,
               string_agg(distinct a.privilege_type, ', ' order by a.privilege_type),
               case d.defaclobjtype when 'r' then 'tables' when 'f' then 'functions'
                    when 'S' then 'sequences' when 'T' then 'types' when 'n' then 'schemas' end,
               case when a.grantee = 0 then 'public' else quote_ident(g.rolname) end)
          from pg_default_acl d
          join pg_roles r on r.oid = d.defaclrole
          cross join lateral aclexplode(d.defaclacl) a
          left join pg_roles g on g.oid = a.grantee
         where d.defaclnamespace = 'public'::regnamespace
           and d.defaclrole = (select oid from pg_roles where rolname = current_user)
         group by d.defaclrole, d.defaclobjtype, r.rolname, a.grantee, g.rolname
         order by 1
        """;

    private const string SkippedDefaultPrivilegesSql = """
        select format('alter default privileges for role %I in schema public grant %s on %s to %s;',
               r.rolname,
               string_agg(distinct a.privilege_type, ', ' order by a.privilege_type),
               case d.defaclobjtype when 'r' then 'tables' when 'f' then 'functions'
                    when 'S' then 'sequences' when 'T' then 'types' when 'n' then 'schemas' end,
               case when a.grantee = 0 then 'public' else quote_ident(g.rolname) end)
          from pg_default_acl d
          join pg_roles r on r.oid = d.defaclrole
          cross join lateral aclexplode(d.defaclacl) a
          left join pg_roles g on g.oid = a.grantee
         where d.defaclnamespace = 'public'::regnamespace
           and d.defaclrole <> (select oid from pg_roles where rolname = current_user)
         group by d.defaclrole, d.defaclobjtype, r.rolname, a.grantee, g.rolname
         order by 1
        """;

    private const string SchemaGrantsSql = """
        select format('grant %s on schema public to %s;',
               string_agg(distinct a.privilege_type, ', ' order by a.privilege_type),
               case when a.grantee = 0 then 'public' else quote_ident(g.rolname) end)
          from pg_namespace n
          cross join lateral aclexplode(n.nspacl) a
          left join pg_roles g on g.oid = a.grantee
         where n.nspname = 'public' and n.nspacl is not null
         group by a.grantee, g.rolname
         order by 1
        """;

    private const string TableGrantsSql = """
        select format('grant %s on table %I.%I to %s;',
               string_agg(distinct a.privilege_type, ', ' order by a.privilege_type),
               n.nspname, c.relname,
               case when a.grantee = 0 then 'public' else quote_ident(g.rolname) end)
          from pg_class c
          join pg_namespace n on n.oid = c.relnamespace
          cross join lateral aclexplode(c.relacl) a
          left join pg_roles g on g.oid = a.grantee
         where n.nspname = 'public'
           and c.relkind in ('r','v','m','f','S')
           and c.relacl is not null
         group by n.nspname, c.relname, a.grantee, g.rolname
         order by n.nspname, c.relname, 1
        """;

    private const string ColumnGrantsSql = """
        select format('grant %s on table %I.%I (%s) to %s;',
               a.privilege_type, n.nspname, c.relname,
               string_agg(att.attname, ', ' order by att.attnum),
               case when a.grantee = 0 then 'public' else quote_ident(g.rolname) end)
          from pg_attribute att
          join pg_class c on c.oid = att.attrelid
          join pg_namespace n on n.oid = c.relnamespace
          cross join lateral aclexplode(att.attacl) a
          left join pg_roles g on g.oid = a.grantee
         where n.nspname = 'public'
           and att.attacl is not null
           and att.attnum > 0 and not att.attisdropped
         group by n.nspname, c.relname, a.privilege_type, a.grantee, g.rolname
         order by n.nspname, c.relname, 1
        """;

    private const string FunctionGrantsSql = """
        select format('grant %s on function %I.%I(%s) to %s;',
               string_agg(distinct a.privilege_type, ', ' order by a.privilege_type),
               n.nspname, p.proname, pg_get_function_identity_arguments(p.oid),
               case when a.grantee = 0 then 'public' else quote_ident(g.rolname) end)
          from pg_proc p
          join pg_namespace n on n.oid = p.pronamespace
          cross join lateral aclexplode(p.proacl) a
          left join pg_roles g on g.oid = a.grantee
         where n.nspname = 'public' and p.proacl is not null
         group by n.nspname, p.proname, p.oid, a.grantee, g.rolname
         order by n.nspname, p.proname, 1
        """;

    /// <summary>
    /// Policies fora do public cuja definição referencia funções do public
    /// (ex.: photos_org_* em storage.objects) — são derrubadas em cascata
    /// no DROP SCHEMA public e precisam ser recriadas depois.
    /// </summary>
    private const string ExternalDepsSql = """
        select coalesce(json_agg(json_build_object(
                 'schema', p.schemaname, 'table', p.tablename, 'policy', p.policyname,
                 'permissive', p.permissive, 'cmd', p.cmd, 'roles', p.roles,
                 'qual', p.qual, 'with_check', p.with_check)), '[]'::json)::text
          from pg_policies p
         where exists (
                 select 1
                   from pg_policy pol
                   join pg_class c on c.oid = pol.polrelid
                   join pg_namespace n on n.oid = c.relnamespace
                  where n.nspname = p.schemaname
                    and c.relname = p.tablename
                    and pol.polname = p.policyname
                    and n.nspname <> 'public'
                    and exists (
                        select 1 from pg_depend d
                         where d.classid = 'pg_policy'::regclass
                           and d.objid = pol.oid
                           and d.refclassid = 'pg_proc'::regclass
                           and d.refobjid in (select oid from pg_proc
                                               where pronamespace = 'public'::regnamespace)))
        """;

    // ------------------------------------------------------------
    // Geração
    // ------------------------------------------------------------

    public static void Write(string folder, Action<string> log)
    {
        var privileges = BuildPrivileges();
        File.WriteAllText(
            Path.Combine(folder, "privileges.sql"),
            privileges, new UTF8Encoding(false));

        var external = BuildExternalDeps();
        File.WriteAllText(
            Path.Combine(folder, "external-deps.sql"),
            external, new UTF8Encoding(false));

        log(
            $"   privileges.sql ({CountStatements(privileges)} instruções) + " +
            $"external-deps.sql ({CountStatements(external)})");
    }

    private static string BuildPrivileges()
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- BackupControlePro: privilégios do schema public (gerado no backup).");
        sb.AppendLine("-- @pre  : executar ANTES do dump (default privileges valem p/ objetos novos).");
        sb.AppendLine("-- @post : executar DEPOIS do dump (GRANTs explícitos).");
        sb.AppendLine("-- @pre");

        foreach (var stmt in Query(DefaultPrivilegesSql))
            sb.AppendLine(stmt);
        foreach (var stmt in Query(SkippedDefaultPrivilegesSql))
            sb.AppendLine("-- IGNORADO (sem permissão para alterar de outra role): " + stmt);

        sb.AppendLine("-- @post");
        foreach (var sql in new[] { SchemaGrantsSql, TableGrantsSql, ColumnGrantsSql, FunctionGrantsSql })
        {
            foreach (var stmt in Query(sql))
                sb.AppendLine(stmt);
        }

        return sb.ToString();
    }

    private static string BuildExternalDeps()
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- BackupControlePro: objetos FORA do public que dependem do public.");
        sb.AppendLine("-- Recriados após o dump (o DROP SCHEMA public os derruba em cascata).");

        var json = QueryScalar(ExternalDepsSql) ?? "[]";
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var schema = item.GetProperty("schema").GetString()!;
            var table = item.GetProperty("table").GetString()!;
            var policy = item.GetProperty("policy").GetString()!;
            // pg_policies renders "PERMISSIVE"/"RESTRICTIVE" (texto) e cmd em
            // maiúsculas ("SELECT", "DELETE", ...); aceita também bool/"*".
            var permissive = !string.Equals(
                item.GetProperty("permissive").ToString(), "RESTRICTIVE",
                StringComparison.OrdinalIgnoreCase);
            var cmd = item.GetProperty("cmd").GetString() ?? "ALL";
            var roles = item.GetProperty("roles").EnumerateArray()
                .Select(r => r.GetString() ?? "").ToList();
            var qual = item.GetProperty("qual").GetString();
            var withCheck = item.GetProperty("with_check").GetString();

            sb.AppendLine($"drop policy if exists {Q(policy)} on {Q(schema)}.{Q(table)};");
            var create = new StringBuilder();
            create.Append($"create policy {Q(policy)} on {Q(schema)}.{Q(table)}");
            create.Append(permissive ? " as permissive" : " as restrictive");
            create.Append($" for {MapCmd(cmd)}");
            create.Append($" to {MapRoles(roles)}");
            if (!string.IsNullOrWhiteSpace(qual)) create.Append($" using ({qual})");
            if (!string.IsNullOrWhiteSpace(withCheck)) create.Append($" with check ({withCheck})");
            create.Append(';');
            sb.AppendLine(create.ToString());
        }

        return sb.ToString();
    }

    private static string MapCmd(string cmd) => cmd.Trim().ToLowerInvariant() switch
    {
        "*" or "all" => "all",
        "select" or "insert" or "update" or "delete" => cmd.ToLowerInvariant(),
        _ => "all",
    };

    private static string MapRoles(List<string> roles)
    {
        if (roles.Count == 0 || roles.Contains("public")) return "public";
        return string.Join(", ", roles.Select(Q));
    }

    private static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    private static int CountStatements(string sql) =>
        sql.Split('\n').Count(l =>
        {
            var t = l.Trim();
            return t.Length > 0 && !t.StartsWith("--", StringComparison.Ordinal);
        });

    // ------------------------------------------------------------
    // psql
    // ------------------------------------------------------------

    private static List<string> Query(string sql)
    {
        var (exit, output) = BackupEngine.RunPsql(["-A", "-t", "-c", sql]);
        if (exit != 0)
            throw new InvalidOperationException($"psql (privilégios) exit {exit}: {Tail(output)}");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static string? QueryScalar(string sql)
    {
        var (exit, output) = BackupEngine.RunPsql(["-A", "-t", "-c", sql]);
        if (exit != 0)
            throw new InvalidOperationException($"psql (deps externos) exit {exit}: {Tail(output)}");
        return output.Trim().Length == 0 ? null : output.Trim();
    }

    private static string Tail(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.TakeLast(3).Select(l => l.Trim()));
    }
}
