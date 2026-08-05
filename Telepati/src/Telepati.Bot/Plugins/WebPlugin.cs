using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HtmlAgilityPack;
using Microsoft.SemanticKernel;
using Telepati.Shared.Configuration;

namespace Telepati.Bot.Plugins;

/// <summary>
/// Internet access for the bot: Tavily search plus page scraping. Both return text trimmed to a
/// size the context window can absorb — an untruncated page would eat the whole budget.
/// </summary>
public class WebPlugin(IHttpClientFactory httpClientFactory, BotOptions options)
{
    private const int MaxScrapedChars = 12000;

    [KernelFunction, Description("Cari informasi terbaru di internet lewat Tavily. Gunakan untuk pertanyaan tentang berita, harga, atau fakta terkini.")]
    public async Task<string> SearchAsync(
        [Description("Kata kunci pencarian")] string query,
        [Description("Jumlah hasil, 1-10")] int maxResults = 5,
        CancellationToken ct = default)
    {
        if (!options.EnableWebSearch) return "Fitur pencarian internet dimatikan oleh admin.";
        if (string.IsNullOrWhiteSpace(options.TavilyApiKey)) return "Tavily API key belum diisi di pengaturan.";

        try
        {
            var client = httpClientFactory.CreateClient(nameof(WebPlugin));
            var response = await client.PostAsJsonAsync("https://api.tavily.com/search", new
            {
                api_key = options.TavilyApiKey,
                query,
                max_results = Math.Clamp(maxResults, 1, 10),
                search_depth = "basic",
                include_answer = true
            }, ct);

            if (!response.IsSuccessStatusCode)
                return $"Pencarian gagal ({(int)response.StatusCode}).";

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;

            var builder = new StringBuilder();
            if (root.TryGetProperty("answer", out var answer) && answer.ValueKind == JsonValueKind.String)
            {
                builder.AppendLine($"Ringkasan: {answer.GetString()}").AppendLine();
            }

            if (root.TryGetProperty("results", out var results))
            {
                var index = 1;
                foreach (var item in results.EnumerateArray())
                {
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() : "(tanpa judul)";
                    var url = item.TryGetProperty("url", out var u) ? u.GetString() : "";
                    var content = item.TryGetProperty("content", out var c) ? c.GetString() : "";
                    builder.AppendLine($"{index++}. {title}\n   {url}\n   {Trim(content, 400)}");
                }
            }

            return builder.Length == 0 ? "Tidak ada hasil." : builder.ToString();
        }
        catch (Exception e)
        {
            return $"Pencarian gagal: {e.Message}";
        }
    }

    [KernelFunction, Description("Baca isi sebuah halaman web dan kembalikan teksnya (tanpa script/style).")]
    public async Task<string> ScrapePageAsync(
        [Description("URL halaman yang dibaca")] string url,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "URL tidak valid.";

        try
        {
            var client = httpClientFactory.CreateClient(nameof(WebPlugin));
            var html = await client.GetStringAsync(uri, ct);

            var document = new HtmlDocument();
            document.LoadHtml(html);

            foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//noscript|//iframe")?.ToList() ?? [])
            {
                node.Remove();
            }

            var title = document.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim();
            var text = HtmlEntity.DeEntitize(document.DocumentNode.SelectSingleNode("//body")?.InnerText ?? string.Empty);

            // Scraped markup leaves long runs of blank lines; collapse them before returning.
            var cleaned = string.Join('\n', text
                .Split('\n', StringSplitOptions.TrimEntries)
                .Where(line => line.Length > 0));

            return $"# {title}\nSumber: {uri}\n\n{Trim(cleaned, MaxScrapedChars)}";
        }
        catch (Exception e)
        {
            return $"Gagal membaca halaman: {e.Message}";
        }
    }

    [KernelFunction, Description("Ambil isi file teks dari URL (txt, csv, json, md, xml).")]
    public async Task<string> ReadTextFromUrlAsync(
        [Description("URL file")] string url,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "URL tidak valid.";

        try
        {
            var client = httpClientFactory.CreateClient(nameof(WebPlugin));
            var content = await client.GetStringAsync(uri, ct);
            return Trim(content, MaxScrapedChars);
        }
        catch (Exception e)
        {
            return $"Gagal mengambil file: {e.Message}";
        }
    }

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max] + $"\n… (dipotong, total {value.Length} karakter)";
    }
}
