using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BackupControlePro;

public sealed class BucketInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Public { get; init; }
    public long? FileSizeLimit { get; init; }
    public string[]? AllowedMimeTypes { get; init; }
    public string? CreatedAt { get; init; }
}

public sealed class StorageObjectRef
{
    public string Bucket { get; init; } = "";
    public string Path { get; init; } = "";
}

public sealed class StorageFileEntry
{
    public string Bucket { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string? ContentType { get; set; }
}

public sealed class StorageTotals
{
    public int Buckets { get; set; }
    public int Files { get; set; }
    public long Bytes { get; set; }
}

public sealed class StorageManifest
{
    public string Project { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Tag { get; set; } = "";
    public List<BucketInfo> Buckets { get; set; } = new();
    public List<StorageFileEntry> Files { get; set; } = new();
    public StorageTotals Totals { get; set; } = new();
}

/// <summary>
/// Cliente HTTP do Supabase para o Storage.
/// O token do Management API (nunca logado) é usado para listar buckets/objetos
/// e obter a service_role em runtime; os downloads/uploads usam a service_role.
/// </summary>
public static class SupabaseStorageClient
{
    private const int PageSize = 5000;

    private static readonly HttpClient Http = new(
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static string? _serviceRole;

    public static string ServiceRole
    {
        get
        {
            if (_serviceRole != null) return _serviceRole;

            using var doc = ManagementJson(() => ManagementRequest(HttpMethod.Get, "/api-keys"));
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [];
            string? found = null;
            foreach (var k in arr)
            {
                if (k.ValueKind != JsonValueKind.Object) continue;
                var name = k.TryGetProperty("name", out var n) ? n.GetString() : null;
                var type = k.TryGetProperty("type", out var t) ? t.GetString() : null;
                var apiKey = k.TryGetProperty("api_key", out var a) ? a.GetString() : null;
                if (string.IsNullOrEmpty(apiKey)) continue;

                if (type == "legacy" && name == "service_role") { found = apiKey; break; }
                if (name == "service_role") { found = apiKey; break; }
                if (type == "secret" || name == "secret") { found = apiKey; }
            }

            if (string.IsNullOrEmpty(found))
                throw new InvalidOperationException("service_role key não encontrada em /api-keys");

            _serviceRole = found;
            return found;
        }
    }

    // ------------------------------------------------------------
    // Segurança de caminhos
    // ------------------------------------------------------------

    public static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            throw new InvalidOperationException("segmento vazio");
        if (segment.Contains('/') || segment.Contains('\\') || segment.Contains('\0') ||
            segment.Contains("..") || segment.Contains(':') ||
            segment.StartsWith('.') && segment.Length > 1 && segment[1] == '.')
            throw new InvalidOperationException($"segmento inseguro: {segment}");
        return segment;
    }

    public static string SanitizeRelativePath(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel))
            throw new InvalidOperationException("path vazio");
        var norm = rel.Replace('\\', '/').TrimStart('/');
        var segs = norm.Split('/');
        foreach (var s in segs)
        {
            if (s == "" || s == "." || s == ".." ||
                s.Contains('\\') || s.Contains(':') || s.Contains('\0'))
                throw new InvalidOperationException($"path inseguro: {rel}");
        }
        return norm;
    }

    private static string BuildObjectUrl(string bucket, string path)
    {
        var safeBucket = Uri.EscapeDataString(SanitizeSegment(bucket));
        var safePath = SanitizeRelativePath(path);
        var segs = string.Join("/", safePath.Split('/').Select(Uri.EscapeDataString));
        return $"{AppConfig.StorageApiBase}/object/{safeBucket}/{segs}";
    }

    // ------------------------------------------------------------
    // Management API (buckets/objetos/service_role)
    // ------------------------------------------------------------

    private static HttpRequestMessage ManagementRequest(HttpMethod method, string path, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, AppConfig.ManagementApiBase + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AppConfig.AccessToken);
        if (content != null) req.Content = content;
        return req;
    }

    private static JsonDocument ManagementJson(Func<HttpRequestMessage> factory)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = factory();
                using var resp = Http.Send(req);
                var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    var msg = $"HTTP {(int)resp.StatusCode} em {req.RequestUri}: {Truncate(text, 300)}";
                    if ((int)resp.StatusCode >= 500 && attempt < 3)
                    {
                        last = new InvalidOperationException(msg);
                        continue;
                    }
                    throw new InvalidOperationException(msg);
                }
                return JsonDocument.Parse(text);
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                last = ex;
            }
        }
        throw new InvalidOperationException($"Management API falhou após 3 tentativas: {last?.Message}");
    }

    private static List<Dictionary<string, object?>> QueryRows(string sql)
    {
        using var doc = ManagementJson(() =>
        {
            var body = new StringContent(
                JsonSerializer.Serialize(new { query = sql }), Encoding.UTF8, "application/json");
            return ManagementRequest(HttpMethod.Post, "/database/query", body);
        });

        var root = doc.RootElement;
        var rows = root;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 &&
            root[0].ValueKind == JsonValueKind.Array)
            rows = root[0];

        var list = new List<Dictionary<string, object?>>();
        if (rows.ValueKind != JsonValueKind.Array) return list;

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var p in row.EnumerateObject())
                dict[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetInt64(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => p.Value.GetRawText(),
                };
            list.Add(dict);
        }
        return list;
    }

    public static List<BucketInfo> ListBuckets()
    {
        var rows = QueryRows(
            "select id, name, public, file_size_limit, allowed_mime_types, created_at " +
            "from storage.buckets order by id");

        var list = new List<BucketInfo>();
        foreach (var r in rows)
        {
            var id = r.GetValueOrDefault("id") as string ?? "";
            if (id.Length == 0) continue;

            string[]? allowed = null;
            if (r.GetValueOrDefault("allowed_mime_types") is string allowedRaw &&
                !string.IsNullOrWhiteSpace(allowedRaw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(allowedRaw);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        allowed = doc.RootElement.EnumerateArray()
                            .Select(x => x.GetString())
                            .Where(x => x != null)
                            .Select(x => x!)
                            .ToArray();
                }
                catch { /* MIME types ilegíveis: grava null */ }
            }

            list.Add(new BucketInfo
            {
                Id = id,
                Name = r.GetValueOrDefault("name") as string ?? id,
                Public = r.GetValueOrDefault("public") is true,
                FileSizeLimit = r.GetValueOrDefault("file_size_limit") is long l ? l : null,
                AllowedMimeTypes = allowed,
                CreatedAt = r.GetValueOrDefault("created_at") as string,
            });
        }
        return list;
    }

    public static List<StorageObjectRef> ListObjects()
    {
        var all = new List<StorageObjectRef>();
        for (var offset = 0;; offset += PageSize)
        {
            var rows = QueryRows(
                "select bucket_id, name from storage.objects order by bucket_id, name " +
                $"limit {PageSize} offset {offset}");
            foreach (var r in rows)
            {
                var bucket = r.GetValueOrDefault("bucket_id") as string ?? "";
                var name = r.GetValueOrDefault("name") as string ?? "";
                if (bucket.Length > 0 && name.Length > 0)
                    all.Add(new StorageObjectRef { Bucket = bucket, Path = name });
            }
            if (rows.Count < PageSize) break;
        }
        return all;
    }

    // ------------------------------------------------------------
    // Storage API (download/upload/bucket)
    // ------------------------------------------------------------

    public static (long Bytes, string Sha256, string? ContentType) DownloadToFile(
        string bucket, string path, string dest)
    {
        var url = BuildObjectUrl(bucket, path);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ServiceRole);
                using var resp = Http.Send(req);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var msg = $"HTTP {(int)resp.StatusCode} em object/{bucket}/{path}: {Truncate(body, 200)}";
                    if ((int)resp.StatusCode >= 500 && attempt < 3)
                    {
                        last = new InvalidOperationException(msg);
                        continue;
                    }
                    throw new InvalidOperationException(msg);
                }

                using var src = resp.Content.ReadAsStream();
                using var dst = File.Create(dest);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dst.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                    total += read;
                }

                var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                var contentType = resp.Content.Headers.ContentType?.MediaType;
                return (total, sha, contentType);
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                last = ex;
            }
        }
        throw new InvalidOperationException(
            $"download de {bucket}/{path} falhou após 3 tentativas: {last?.Message}");
    }

    public static void UploadObject(string bucket, string path, string sourceFile, string? contentType)
    {
        var url = BuildObjectUrl(bucket, path);
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var fs = File.OpenRead(sourceFile);
                using var content = new StreamContent(fs);
                if (!string.IsNullOrEmpty(contentType))
                    content.Headers.TryAddWithoutValidation("Content-Type", contentType);

                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ServiceRole);
                req.Headers.TryAddWithoutValidation("x-upsert", "true");

                using var resp = Http.Send(req);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var msg = $"HTTP {(int)resp.StatusCode} upload {bucket}/{path}: {Truncate(body, 200)}";
                    if ((int)resp.StatusCode >= 500 && attempt < 3)
                    {
                        last = new InvalidOperationException(msg);
                        continue;
                    }
                    throw new InvalidOperationException(msg);
                }
                return;
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                last = ex;
            }
        }
        throw new InvalidOperationException(
            $"upload de {bucket}/{path} falhou após 3 tentativas: {last?.Message}");
    }

    public static void EnsureBucket(BucketInfo bucket)
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = bucket.Id,
            name = string.IsNullOrEmpty(bucket.Name) ? bucket.Id : bucket.Name,
            @public = bucket.Public,
            file_size_limit = bucket.FileSizeLimit,
            allowed_mime_types = bucket.AllowedMimeTypes,
        });

        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.StorageApiBase}/bucket")
                {
                    Content = content,
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ServiceRole);

                using var resp = Http.Send(req);
                if (resp.IsSuccessStatusCode) return;

                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (resp.StatusCode == System.Net.HttpStatusCode.Conflict ||
                    body.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                    return;

                var msg = $"HTTP {(int)resp.StatusCode} criar bucket {bucket.Id}: {Truncate(body, 200)}";
                if ((int)resp.StatusCode >= 500 && attempt < 3)
                {
                    last = new InvalidOperationException(msg);
                    continue;
                }
                throw new InvalidOperationException(msg);
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                last = ex;
            }
        }
        throw new InvalidOperationException(
            $"criar bucket {bucket.Id} falhou após 3 tentativas: {last?.Message}");
    }

    public static string ComputeSha256(string file)
    {
        using var fs = File.OpenRead(file);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or IOException or TaskCanceledException;

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s[..max];
}
