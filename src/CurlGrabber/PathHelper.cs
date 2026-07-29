using System.Globalization;
using System.Text;

namespace CurlGrabber;

public static class PathHelper
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>Ersetzt alles, was Windows in einem Dateinamen nicht erlaubt.</summary>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            sb.Append(Array.IndexOf(Invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
        }

        // Nachlaufende Punkte und Leerzeichen kann der Explorer nicht oeffnen.
        return sb.ToString().TrimEnd('.', ' ');
    }

    /// <summary>Formatiert eine Byte-Anzahl fuer die Anzeige.</summary>
    public static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        string format = unit == 0 ? "{0:0} {1}" : "{0:0.0} {1}";
        return string.Format(CultureInfo.CurrentCulture, format, bytes, units[unit]);
    }
}
