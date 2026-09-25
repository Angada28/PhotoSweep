using System.Globalization;

namespace PhotoSweep.Presentation;

/// <summary>Formatting shared by the pages. Invariant culture, like the rest of the app's generated text.</summary>
public static class DisplayText
{
    /// <summary>"0 B", "512 B", "1.5 KB", "3.2 GB": 1024-based, one decimal at most.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : value.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>"0:07", "12:30", "1:02:03".</summary>
    public static string Duration(TimeSpan time) =>
        time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>"1 photo", "3 photos", with thousands separators.</summary>
    public static string Count(int count, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{count:N0} {(count == 1 ? singular : plural)}");
}
