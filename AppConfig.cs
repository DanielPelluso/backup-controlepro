using System.Security.Cryptography;
using System.Text;

namespace BackupControlePro;

/// <summary>
/// Configuração de conexão do ControlePro.
/// Banco: variável de ambiente ou arquivo DPAPI ao lado do EXE.
/// Token Storage: idem, via Management API (service_role buscada em runtime).
/// Ferramentas pg: subpasta &lt;exe&gt;\pg\ (portátil) → pgenv legado → PATH.
/// </summary>
public static class AppConfig
{
    public const string ProjectRef = "gfacmccunzgcgrhbadtf";
    public const string PoolerHost = "aws-1-sa-east-1.pooler.supabase.com";
    public const int PoolerPort = 5432;
    public const string DbName = "postgres";

    /// <summary>Formato da pasta de backup: DDMMAAHHMMSS (ddMMyyHHmmss).</summary>
    public const string StampFormat = "ddMMyyHHmmss";

    private const string DbPasswordEnv = "CONTROLEPRO_SUPABASE_DB_PASSWORD";
    private const string DbPasswordFallbackEnv = "SUPABASE_DB_PASSWORD";
    private const string DbPasswordFileName = ".controlepro-dbpass";

    private const string AccessTokenEnv = "CONTROLEPRO_SUPABASE_ACCESS_TOKEN";
    private const string AccessTokenFallbackEnv = "SUPABASE_ACCESS_TOKEN";
    private const string AccessTokenFileName = ".controlepro-access-token";

    public static string DbUser => $"postgres.{ProjectRef}";

    /// <summary>Raiz dos backups = pasta onde o EXE está.</summary>
    public static string BackupRoot => AppContext.BaseDirectory;

    public static string ManagementApiBase => $"https://api.supabase.com/v1/projects/{ProjectRef}";
    public static string StorageApiBase => $"https://{ProjectRef}.supabase.co/storage/v1";

    private static string DbPasswordFilePath => Path.Combine(AppContext.BaseDirectory, DbPasswordFileName);
    private static string AccessTokenFilePath => Path.Combine(AppContext.BaseDirectory, AccessTokenFileName);

    // ------------------------------------------------------------
    // Ferramentas PostgreSQL (pg_dump / pg_restore / psql)
    // ------------------------------------------------------------

    private static string? _pgDump;
    private static string? _pgRestore;
    private static string? _psql;

    public static string PgDumpExe => _pgDump ?? ResolveTool("pg_dump.exe");
    public static string PgRestoreExe => _pgRestore ?? ResolveTool("pg_restore.exe");
    public static string PsqlExe => _psql ?? ResolveTool("psql.exe");

    /// <summary>Procura a ferramenta: &lt;exe&gt;\pg\ → pgenv legado → PATH.</summary>
    private static string ResolveTool(string name)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "pg", name);
        if (File.Exists(local)) return local;

        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "tools", "pgenv", "Library", "bin", name);
        if (File.Exists(legacy)) return legacy;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* caminho inválido no PATH — ignora */ }
        }

        throw new FileNotFoundException(
            $"Ferramenta {name} não encontrada.\n\n" +
            $"Copie os arquivos .exe/.dll do PostgreSQL para:\n" +
            $"  {Path.Combine(AppContext.BaseDirectory, "pg")}\\{name}\n" +
            "ou instale o pgenv em %USERPROFILE%\\tools\\pgenv, ou adicione o bin ao PATH.");
    }

    private static void CacheTools()
    {
        _pgDump = ResolveTool("pg_dump.exe");
        _pgRestore = ResolveTool("pg_restore.exe");
        _psql = ResolveTool("psql.exe");
    }

    // ------------------------------------------------------------
    // Segredos (banco e token do Management API)
    // ------------------------------------------------------------

    private static string? _cachedDbPassword;
    private static string? _cachedAccessToken;

    private static string? GetEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        if (string.IsNullOrEmpty(value))
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string ReadSecret(string env, string fallbackEnv, string file, string label)
    {
        var value = GetEnv(env) ?? GetEnv(fallbackEnv);
        if (!string.IsNullOrEmpty(value)) return value;

        if (File.Exists(file))
        {
            try
            {
                var enc = File.ReadAllBytes(file);
                var plain = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                var text = Encoding.UTF8.GetString(plain);
                CryptographicOperations.ZeroMemory(plain);
                return text;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Não foi possível ler {label} salva ({Path.GetFileName(file)}): {ex.Message}");
            }
        }

        throw new InvalidOperationException($"{label} ausente. Informe-a na primeira execução.");
    }

    private static void SaveSecret(string value, string file)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            var enc = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(file, enc);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static bool HasDbPassword
    {
        get
        {
            try { _ = DbPassword; return true; }
            catch { return false; }
        }
    }

    public static bool HasAccessToken
    {
        get
        {
            try { _ = AccessToken; return true; }
            catch { return false; }
        }
    }

    /// <summary>Senha do banco: env → arquivo DPAPI ao lado do EXE.</summary>
    public static string DbPassword =>
        _cachedDbPassword ??= ReadSecret(DbPasswordEnv, DbPasswordFallbackEnv, DbPasswordFilePath, "Senha do banco");

    /// <summary>Token do Management API: env → arquivo DPAPI ao lado do EXE.</summary>
    public static string AccessToken =>
        _cachedAccessToken ??= ReadSecret(AccessTokenEnv, AccessTokenFallbackEnv, AccessTokenFilePath, "Token do Management API");

    public static void SaveDbPassword(string password)
    {
        SaveSecret(password, DbPasswordFilePath);
        _cachedDbPassword = password;
    }

    public static void SaveAccessToken(string token)
    {
        SaveSecret(token, AccessTokenFilePath);
        _cachedAccessToken = token;
    }

    // ------------------------------------------------------------
    // Validação
    // ------------------------------------------------------------

    public static void ValidateTools() => CacheTools();

    public static void ValidateDbPassword() => _ = DbPassword;

    public static void ValidateAccessToken() => _ = AccessToken;
}
