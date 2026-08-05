using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using Microsoft.SemanticKernel;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Configuration;

namespace Telepati.Bot.Plugins;

/// <summary>
/// Files the bot may touch, all of them inside the configured workspace. Every path argument is
/// resolved through <see cref="Workspace"/>, which refuses anything that escapes the sandbox —
/// the model is never trusted with a raw filesystem path.
/// </summary>
public class FilePlugin(
    Workspace workspace,
    IStorageService storage,
    IHttpClientFactory httpClientFactory)
{
    private const int MaxReadChars = 20000;

    [KernelFunction, Description("Daftar file dan folder di dalam workspace.")]
    public string ListFiles([Description("Sub-folder relatif, kosongkan untuk akar workspace")] string? folder = null)
    {
        try
        {
            var directory = workspace.Resolve(folder ?? ".");
            if (!Directory.Exists(directory)) return "Folder tidak ada.";

            var builder = new StringBuilder();
            foreach (var entry in Directory.EnumerateDirectories(directory))
                builder.AppendLine($"[DIR ] {Path.GetFileName(entry)}");

            foreach (var entry in Directory.EnumerateFiles(directory))
            {
                var info = new FileInfo(entry);
                builder.AppendLine($"[FILE] {info.Name} ({info.Length:N0} bytes)");
            }

            return builder.Length == 0 ? "Folder kosong." : builder.ToString();
        }
        catch (Exception e)
        {
            return $"Gagal membaca folder: {e.Message}";
        }
    }

    [KernelFunction, Description("Baca isi file teks dari workspace.")]
    public async Task<string> ReadFileAsync(
        [Description("Path file relatif terhadap workspace")] string path,
        CancellationToken ct = default)
    {
        try
        {
            var full = workspace.Resolve(path);
            if (!File.Exists(full)) return "File tidak ditemukan.";

            var content = await File.ReadAllTextAsync(full, ct);
            return content.Length <= MaxReadChars ? content : content[..MaxReadChars] + "\n… (dipotong)";
        }
        catch (Exception e)
        {
            return $"Gagal membaca file: {e.Message}";
        }
    }

    [KernelFunction, Description("Tulis atau timpa file teks di workspace. Gunakan untuk membuat kode, dokumen, atau data.")]
    public async Task<string> WriteFileAsync(
        [Description("Path file relatif terhadap workspace")] string path,
        [Description("Isi file")] string content,
        CancellationToken ct = default)
    {
        try
        {
            var full = workspace.Resolve(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content, ct);
            return $"Tersimpan: {path} ({content.Length:N0} karakter).";
        }
        catch (Exception e)
        {
            return $"Gagal menulis file: {e.Message}";
        }
    }

    [KernelFunction, Description("Unduh file dari URL ke dalam workspace, misalnya lampiran yang dikirim user.")]
    public async Task<string> DownloadFileAsync(
        [Description("URL file")] string url,
        [Description("Nama file tujuan di workspace")] string fileName,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "URL tidak valid.";

        try
        {
            var client = httpClientFactory.CreateClient(nameof(FilePlugin));
            await using var source = await client.GetStreamAsync(uri, ct);

            var full = workspace.Resolve(fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            await using var target = File.Create(full);
            await source.CopyToAsync(target, ct);

            return $"Terunduh ke workspace: {fileName} ({target.Length:N0} bytes).";
        }
        catch (Exception e)
        {
            return $"Gagal mengunduh: {e.Message}";
        }
    }

    [KernelFunction, Description("Kirim hasil kerja ke user. File di-upload ke storage dan URL unduhannya dikembalikan. Folder otomatis di-zip.")]
    public async Task<string> ShareResultAsync(
        [Description("Path file atau folder di workspace yang mau dikirim")] string path,
        CancellationToken ct = default)
    {
        try
        {
            var full = workspace.Resolve(path);
            string uploadPath;
            string uploadName;
            var temporaryZip = false;

            if (Directory.Exists(full))
            {
                // A folder is not shareable as-is, so it is zipped into a single artefact first.
                uploadName = $"{Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))}.zip";
                uploadPath = Path.Combine(Path.GetTempPath(), $"{Guid.CreateVersion7():N}.zip");
                ZipFile.CreateFromDirectory(full, uploadPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                temporaryZip = true;
            }
            else if (File.Exists(full))
            {
                uploadPath = full;
                uploadName = Path.GetFileName(full);
            }
            else
            {
                return "File atau folder tidak ditemukan di workspace.";
            }

            await using (var stream = File.OpenRead(uploadPath))
            {
                var key = await storage.UploadAsync(stream, uploadName, GuessContentType(uploadName), StorageFolders.BotOutputs, ct);
                var url = await storage.GetUrlAsync(key, ct);

                if (temporaryZip) File.Delete(uploadPath);
                return $"Berhasil di-upload. Kirim tautan ini ke user: [{uploadName}]({url})";
            }
        }
        catch (Exception e)
        {
            return $"Gagal upload hasil: {e.Message}";
        }
    }

    [KernelFunction, Description("Hapus file di workspace.")]
    public string DeleteFile([Description("Path file relatif terhadap workspace")] string path)
    {
        try
        {
            var full = workspace.Resolve(path);
            if (File.Exists(full)) { File.Delete(full); return $"Terhapus: {path}"; }
            if (Directory.Exists(full)) { Directory.Delete(full, recursive: true); return $"Folder terhapus: {path}"; }
            return "Tidak ditemukan.";
        }
        catch (Exception e)
        {
            return $"Gagal menghapus: {e.Message}";
        }
    }

    private static string GuessContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".zip" => "application/zip",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".txt" or ".md" => "text/plain",
        ".html" => "text/html",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream"
    };
}
