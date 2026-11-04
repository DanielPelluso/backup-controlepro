using System.Diagnostics;
using System.Text.Json;

namespace BackupControlePro;

public sealed class StorageRestoreResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int Files { get; set; }
    public long Bytes { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Restauração dos ARQUIVOS do Storage a partir de storage-manifest.json.
/// Cria buckets ausentes, faz upload com x-upsert e verifica sha256.
/// Nunca apaga arquivos extras (operação destrutiva exige autorização explícita).
/// </summary>
public static class StorageRestoreEngine
{
    public static StorageRestoreResult Run(string backupFolder, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        var result = new StorageRestoreResult();
        var storageDir = Path.Combine(backupFolder, "storage");
        var manifestPath = Path.Combine(storageDir, "storage-manifest.json");

        try
        {
            if (!File.Exists(manifestPath))
                throw new InvalidOperationException($"manifesto não encontrado: {manifestPath}");

            var manifest = JsonSerializer.Deserialize<StorageManifest>(
                File.ReadAllText(manifestPath), StorageBackupEngine.ManifestJsonOptions)
                ?? throw new InvalidOperationException("manifesto inválido");

            log($"-> restaurando storage: {manifest.Buckets.Count} bucket(s), {manifest.Files.Count} arquivo(s)");

            foreach (var b in manifest.Buckets)
            {
                SupabaseStorageClient.EnsureBucket(b);
                log($"   bucket ok: {b.Id}");
            }

            var i = 0;
            foreach (var f in manifest.Files)
            {
                i++;
                var safeBucket = SupabaseStorageClient.SanitizeSegment(f.Bucket);
                var safePath = SupabaseStorageClient.SanitizeRelativePath(f.Path);
                var src = Path.Combine(
                    storageDir, safeBucket, safePath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(src))
                    throw new InvalidOperationException($"arquivo do backup ausente: {safeBucket}/{safePath}");

                var localSha = SupabaseStorageClient.ComputeSha256(src);
                if (!string.IsNullOrEmpty(f.Sha256) && localSha != f.Sha256)
                    throw new InvalidOperationException($"sha256 local difere do manifesto: {safeBucket}/{safePath}");

                SupabaseStorageClient.UploadObject(f.Bucket, f.Path, src, f.ContentType);

                var tmp = Path.Combine(Path.GetTempPath(), $"controlepro-verify-{Guid.NewGuid():N}");
                try
                {
                    var (_, remoteSha, _) =
                        SupabaseStorageClient.DownloadToFile(f.Bucket, f.Path, tmp);
                    if (!string.IsNullOrEmpty(f.Sha256) && remoteSha != f.Sha256)
                        throw new InvalidOperationException(
                            $"sha256 remoto difere do backup: {safeBucket}/{safePath}");
                }
                finally
                {
                    try { File.Delete(tmp); } catch { /* ignora */ }
                }

                result.Files++;
                result.Bytes += f.Size;
                if (i % 25 == 0 || i == manifest.Files.Count)
                    log($"   {i}/{manifest.Files.Count} arquivo(s), {result.Bytes / 1024} KB");
            }

            sw.Stop();
            result.Success = true;
            result.Elapsed = sw.Elapsed;
            log($"   OK: {result.Files} arquivo(s) verificados por sha256");
            log("   nota: arquivos extras no remoto (fora do backup) foram mantidos (sem deleção)");
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
