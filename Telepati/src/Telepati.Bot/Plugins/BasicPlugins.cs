using System.ComponentModel;
using System.Data;
using System.Globalization;
using Microsoft.SemanticKernel;

namespace Telepati.Bot.Plugins;

/// <summary>Clock and calendar. Models have no reliable sense of "now" without this.</summary>
public class TimePlugin
{
    private static readonly TimeZoneInfo Jakarta = ResolveJakarta();

    [KernelFunction, Description("Ambil tanggal dan waktu saat ini di zona waktu tertentu (default WIB/Asia-Jakarta).")]
    public string GetCurrentDateTime(
        [Description("Nama zona waktu IANA atau Windows, contoh: Asia/Jakarta, UTC")] string? timeZone = null)
    {
        var zone = ResolveZone(timeZone);
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        return $"{now:dddd, dd MMMM yyyy HH:mm:ss} ({zone.Id})";
    }

    [KernelFunction, Description("Hitung selisih hari antara dua tanggal (format yyyy-MM-dd).")]
    public string GetDaysBetween(
        [Description("Tanggal awal, format yyyy-MM-dd")] string startDate,
        [Description("Tanggal akhir, format yyyy-MM-dd")] string endDate)
    {
        if (!DateTime.TryParse(startDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !DateTime.TryParse(endDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            return "Format tanggal tidak valid. Gunakan yyyy-MM-dd.";
        }

        return $"{(end - start).TotalDays:0} hari";
    }

    [KernelFunction, Description("Tambahkan durasi ke sebuah tanggal dan kembalikan hasilnya.")]
    public string AddToDate(
        [Description("Tanggal awal, format yyyy-MM-dd")] string date,
        [Description("Jumlah hari yang ditambahkan (boleh negatif)")] int days)
    {
        if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return "Format tanggal tidak valid. Gunakan yyyy-MM-dd.";

        return parsed.AddDays(days).ToString("dddd, dd MMMM yyyy", CultureInfo.InvariantCulture);
    }

    private static TimeZoneInfo ResolveZone(string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone)) return Jakarta;

        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
        catch { return Jakarta; }
    }

    /// <summary>Windows and Linux disagree on time-zone ids, so both spellings are tried.</summary>
    private static TimeZoneInfo ResolveJakarta()
    {
        foreach (var id in new[] { "Asia/Jakarta", "SE Asia Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* try the next spelling */ }
        }
        return TimeZoneInfo.Utc;
    }
}

/// <summary>Arithmetic the model should not be trusted to do in its head.</summary>
public class MathPlugin
{
    [KernelFunction, Description("Hitung ekspresi matematika, contoh: (120000 * 0.11) + 5000.")]
    public string Calculate([Description("Ekspresi matematika")] string expression)
    {
        try
        {
            // DataTable.Compute handles + - * / % and parentheses without an eval dependency.
            var result = new DataTable().Compute(expression, null);
            return System.Convert.ToString(result, CultureInfo.InvariantCulture) ?? "Tidak ada hasil.";
        }
        catch (Exception e)
        {
            return $"Ekspresi tidak bisa dihitung: {e.Message}";
        }
    }

    [KernelFunction, Description("Hitung statistik dasar (jumlah, rata-rata, min, maks, median) dari deretan angka.")]
    public string Statistics([Description("Angka dipisah koma, contoh: 10,20,30")] string numbers)
    {
        var values = numbers
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => double.TryParse(v, CultureInfo.InvariantCulture, out var parsed) ? parsed : (double?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .OrderBy(v => v)
            .ToList();

        if (values.Count == 0) return "Tidak ada angka valid.";

        var median = values.Count % 2 == 1
            ? values[values.Count / 2]
            : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;

        return $"n={values.Count}, jumlah={values.Sum():0.##}, rata-rata={values.Average():0.##}, " +
               $"min={values.First():0.##}, maks={values.Last():0.##}, median={median:0.##}";
    }

    [KernelFunction, Description("Konversi persentase, misalnya menghitung diskon atau pajak.")]
    public string Percentage(
        [Description("Nilai dasar")] double value,
        [Description("Persentase, contoh 11 untuk 11%")] double percent)
        => $"{percent}% dari {value:0.##} = {value * percent / 100:0.##} (total: {value + value * percent / 100:0.##})";
}
