using System.Globalization;

namespace CurlGrabber;

/// <summary>Ein Zustand der curl-Fortschrittsanzeige.</summary>
public readonly struct CurlProgress
{
    public int Percent { get; init; }

    /// <summary>Bereits geladene Bytes.</summary>
    public double Received { get; init; }

    /// <summary>Gesamtgroesse in Bytes, oder null wenn der Server sie nicht meldet.</summary>
    public double? Total { get; init; }

    /// <summary>Durchschnittsgeschwindigkeit in Bytes/Sekunde.</summary>
    public double Speed { get; init; }

    /// <summary>Selbst berechnete Restzeit, oder null wenn sie sich nicht bestimmen laesst.</summary>
    public TimeSpan? TimeLeft { get; init; }
}

/// <summary>
/// Liest die klassische curl-Fortschrittstabelle von stderr.
///
///   % Total    % Received % Xferd  Average Speed   Time    Time     Time  Current
///                                  Dload  Upload   Total   Spent    Left  Speed
///  100 28.60M 100 28.60M   0      0 26.41M      0   00:01   00:01              0
///
/// Die drei Zeitspalten laesst curl leer, solange es die Werte nicht kennt - dadurch
/// schwankt die Feldanzahl je Zeile zwischen 9 und 12. Deshalb werden nur die acht
/// vorderen Felder ausgewertet, die immer belegt sind; die Restzeit wird selbst
/// berechnet.
/// </summary>
public static class CurlProgressParser
{
    public static bool IsHeaderLine(string line)
        => line.Contains("% Total", StringComparison.Ordinal)
           || line.Contains("Dload", StringComparison.Ordinal);

    public static bool TryParse(string line, out CurlProgress progress)
    {
        progress = default;

        string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8)
        {
            return false;
        }

        // Die drei Prozentspalten sind reine Ganzzahlen.
        if (!TryParsePercent(parts[0], out int percent)
            || !TryParsePercent(parts[2], out _)
            || !TryParsePercent(parts[4], out _))
        {
            return false;
        }

        // Groessen- und Geschwindigkeitsspalten in curls Kurzschreibweise.
        if (ParseSize(parts[1]) is not { } total
            || ParseSize(parts[3]) is not { } received
            || ParseSize(parts[5]) is null
            || ParseSize(parts[6]) is not { } downloadSpeed
            || ParseSize(parts[7]) is null)
        {
            return false;
        }

        TimeSpan? remaining = null;
        if (total > 0 && downloadSpeed > 0 && received <= total)
        {
            double seconds = (total - received) / downloadSpeed;
            if (seconds is >= 0 and < 60 * 60 * 24 * 30)
            {
                remaining = TimeSpan.FromSeconds(seconds);
            }
        }

        progress = new CurlProgress
        {
            Percent = Math.Clamp(percent, 0, 100),
            Total = total > 0 ? total : null,
            Received = received,
            Speed = downloadSpeed,
            TimeLeft = remaining,
        };

        return true;
    }

    private static bool TryParsePercent(string token, out int value)
    {
        value = 0;
        return token.Length <= 3
               && token.All(char.IsAsciiDigit)
               && int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Wandelt curls Kurzschreibweise ("812k", "1.2G", "0") in Bytes um.</summary>
    public static double? ParseSize(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        double multiplier = 1;
        string number = token;

        char last = token[^1];
        if (!char.IsAsciiDigit(last))
        {
            number = token[..^1];
            multiplier = last switch
            {
                'k' or 'K' => 1024d,
                'M' or 'm' => 1024d * 1024,
                'G' or 'g' => 1024d * 1024 * 1024,
                'T' or 't' => 1024d * 1024 * 1024 * 1024,
                'P' or 'p' => 1024d * 1024 * 1024 * 1024 * 1024,
                _ => double.NaN,
            };

            if (double.IsNaN(multiplier))
            {
                return null;
            }
        }

        if (number.Length == 0
            || !number.All(c => char.IsAsciiDigit(c) || c == '.')
            || !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return null;
        }

        return value * multiplier;
    }
}
