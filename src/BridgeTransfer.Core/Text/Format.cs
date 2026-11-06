using System.Globalization;

namespace BridgeTransfer.Core.Text;

/// <summary>Human-readable formatting for sizes, speeds and durations.</summary>
public static class Format
{
    private static readonly string[] Units = { "bytes", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>Formats a byte count using 1024-based units, like Windows Explorer.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0)
        {
            return "unknown";
        }

        if (bytes == 1)
        {
            return "1 byte";
        }

        if (bytes < 1024)
        {
            return bytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "N0" : value >= 10 ? "N1" : "N2";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond <= 0 || double.IsNaN(bytesPerSecond) ? "" : Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            return "";
        }

        if (span.TotalSeconds < 60)
        {
            return $"{Math.Max(1, (int)Math.Ceiling(span.TotalSeconds))} s";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{(int)span.TotalMinutes} min {span.Seconds:00} s";
        }

        return $"{(int)span.TotalHours} h {span.Minutes:00} min";
    }

    /// <summary>Local display time for a UTC timestamp.</summary>
    public static string LocalTime(DateTime? utc) =>
        utc is null ? "" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public static string LocalTimeWithSeconds(DateTime? utc) =>
        utc is null ? "unknown" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    public static string Count(int n, string singular, string? plural = null) =>
        n.ToString("N0", CultureInfo.CurrentCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");
}
