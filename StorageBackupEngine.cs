using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BackupControlePro;

public sealed class StorageBackupResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string ManifestPath { get; set; } = "";
    public int Buckets { get; set; }
    public int Files { get; set; }
    public long Bytes { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>Backup dos ARQUIVOS do Supabase Storage (somente leitura).</summary>
public static class StorageBackupEngine
{
    internal static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static StorageBackupResult Run(string backupFolder, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        var result = new StorageBackupResult();
        var storageDir = Path.Combine(backupFolder, "storage");

        try
        {
            Directory.CreateDirectory(storageDir);
            log("-> storage: listando buckets e objetos");
            var buckets = SupabaseStorageClient.ListBuckets();
            var objects = SupabaseStorageClient.ListObjects();
            log($"   {buckets.Count} bucket(s), {objects.Count} arquivo(s)");

            var manifest = new StorageManifest
            {
                Project = AppConfig.ProjectRef,
                CreatedAt = DateTime.Now.ToString("o"),
                Tag = "app",
                Buckets = buckets,
                Totals = new StorageTotals { Buckets = buckets.Count },
            };

            var i = 0;
            foreach (var o in objects)
            {
                i++;
                var safeBucket = SupabaseStorageClient.SanitizeSegment(o.Bucket);
                var safePath = SupabaseStorageClient.SanitizeRelativePath(o.Path);
                var dest = Path.Combine(
                    storageDir, safeBucket, safePath.Replace('/', Path.DirectorySeparatorChar));

                var (bytes, sha, contentType) =
                    SupabaseStorageClient.DownloadToFile(o.Bucket, o.Path, dest);

                manifest.Files.Add(new StorageFileEntry
                {
                    Bucket = safeBucket,
                    Path = safePath,
                    Size = bytes,
                    Sha256 = sha,
                    ContentType = contentType,
                });
                manifest.Totals.Files++;
                manifest.Totals.Bytes += bytes;

                if (i % 25 == 0 || i == objects.Count)
                    log($"   {i}/{objects.Count} arquivo(s), {manifest.Totals.Bytes / 1024} KB");
            }

            var manifestPath = Path.Combine(storageDir, "storage-manifest.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(manifest, ManifestJsonOptions),
                new UTF8Encoding(false));

            result.Success = true;
            result.ManifestPath = manifestPath;
            result.Buckets = manifest.Totals.Buckets;
            result.Files = manifest.Totals.Files;
            result.Bytes = manifest.Totals.Bytes;
            sw.Stop();
            result.Elapsed = sw.Elapsed;
            log($"   OK: {result.Files} arquivo(s), {result.Bytes} bytes ({sw.Elapsed.TotalSeconds:F1}s)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Success = false;
            result.Error = ex.Message;
            result.Elapsed = sw.Elapsed;
            log($"   FALHA storage: {ex.Message}");
        }

        return result;
    }
}
