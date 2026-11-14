using System.Globalization;
namespace Monitor.Core;

public static class SeekTime
{
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var parts = text.Trim().Split(':');
        if (parts.Length == 1)
        {
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && double.IsFinite(seconds) && seconds >= 0;
        }
        if (parts.Length is not (2 or 3))
        {
            return false;
        }
        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double sec) || !double.IsFinite(sec) || sec < 0 || sec >= 60)
        {
            return false;
        }
        if (!long.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out long minutes) || minutes < 0 || (parts.Length == 3 && minutes >= 60))
        {
            return false;
        }
        long hours = 0;
        if (parts.Length == 3 && (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours < 0))
        {
            return false;
        }
        seconds = hours * 3600d + minutes * 60d + sec;
        return double.IsFinite(seconds);
    }
}
