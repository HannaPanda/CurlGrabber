using System.Text;

namespace CurlGrabber;

/// <summary>Ergebnis des Zerlegens eines eingefuegten cURL-Befehls.</summary>
public sealed class ParsedCurlCommand
{
    public string Url { get; set; } = string.Empty;

    /// <summary>Alle uebernommenen Optionen (Header, Cookies, Daten, Flags) ohne URL und ohne Ausgabe-Optionen.</summary>
    public List<string> Arguments { get; } = new();

    public List<string> Warnings { get; } = new();

    public int HeaderCount { get; set; }

    /// <summary>Die entfernte Range-Angabe, falls der Befehl eine enthielt.</summary>
    public string? RemovedRange { get; set; }
}

/// <summary>
/// Zerlegt einen aus dem Browser kopierten cURL-Befehl.
/// Unterstuetzt beide Firefox-Varianten: "Als cURL kopieren (Windows)" (cmd.exe-Escaping mit ^)
/// und "Als cURL kopieren (POSIX)" (Bash-Escaping mit \ und ').
/// </summary>
public static class CurlCommandParser
{
    /// <summary>Optionen, die einen eigenen Wert im naechsten Token erwarten.</summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "-H", "--header",
        "-b", "--cookie",
        "-c", "--cookie-jar",
        "-d", "--data", "--data-raw", "--data-ascii", "--data-binary", "--data-urlencode",
        "-X", "--request",
        "-e", "--referer",
        "-A", "--user-agent",
        "-u", "--user",
        "--url",
        "-F", "--form", "--form-string",
        "-x", "--proxy", "--proxy-user", "--proxy-header",
        "-r", "--range",
        "-m", "--max-time", "--connect-timeout", "--expect100-timeout",
        "--retry", "--retry-delay", "--retry-max-time",
        "--limit-rate", "--max-filesize", "--max-redirs",
        "--interface", "--resolve", "--connect-to",
        "--cert", "--key", "--cacert", "--capath", "--ciphers", "--pass",
        "--dns-servers", "--local-port", "--happy-eyeballs-timeout-ms",
    };

    /// <summary>Optionen, die verworfen werden und einen Wert mitbringen.</summary>
    private static readonly HashSet<string> DroppedValueOptions = new(StringComparer.Ordinal)
    {
        "-o", "--output", "--output-dir", "-w", "--write-out", "-K", "--config",
    };

    /// <summary>Flags, die verworfen werden (Ausgabeziel oder Fortschrittsanzeige betreffend).</summary>
    private static readonly HashSet<string> DroppedFlags = new(StringComparer.Ordinal)
    {
        "-O", "--remote-name", "--remote-header-name", "-J",
        "-s", "--silent", "-sS", "-Ss", "--no-progress-meter", "-#", "--progress-bar",
        "-v", "--verbose", "--trace", "--trace-ascii", "-i", "--include",
    };

    public static ParsedCurlCommand Parse(string input)
    {
        var result = new ParsedCurlCommand();
        var tokens = Tokenize(input ?? string.Empty);

        if (tokens.Count == 0)
        {
            result.Warnings.Add("Es wurde kein Befehl erkannt.");
            return result;
        }

        int start = 0;
        if (LooksLikeCurlExecutable(tokens[0]))
        {
            start = 1;
        }

        for (int i = start; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (token.Length == 0)
            {
                continue;
            }

            // Langoption in der Form --name=wert aufteilen.
            string name = token;
            string? inlineValue = null;
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                int eq = token.IndexOf('=');
                if (eq > 2)
                {
                    name = token[..eq];
                    inlineValue = token[(eq + 1)..];
                }
            }

            if (name.Length > 1 && name[0] == '-')
            {
                if (DroppedValueOptions.Contains(name))
                {
                    if (inlineValue is null)
                    {
                        i++; // zugehoerigen Wert mit verwerfen
                    }
                    continue;
                }

                if (DroppedFlags.Contains(name))
                {
                    continue;
                }

                if (ValueOptions.Contains(name))
                {
                    string? value = inlineValue;
                    if (value is null)
                    {
                        if (i + 1 >= tokens.Count)
                        {
                            result.Warnings.Add($"Option {name} hat keinen Wert und wurde ignoriert.");
                            continue;
                        }

                        value = tokens[++i];
                    }

                    if (name is "--url")
                    {
                        if (result.Url.Length == 0)
                        {
                            result.Url = value;
                        }

                        continue;
                    }

                    if (name is "-r" or "--range")
                    {
                        NoteRemovedRange(result, value);
                        continue;
                    }

                    if (name is "-H" or "--header")
                    {
                        if (IsRangeHeader(value, out string rangeValue))
                        {
                            NoteRemovedRange(result, rangeValue);
                            continue;
                        }

                        result.HeaderCount++;
                    }

                    result.Arguments.Add(name);
                    result.Arguments.Add(value);
                    continue;
                }

                // Unbekanntes Flag unveraendert uebernehmen.
                result.Arguments.Add(token);
                continue;
            }

            if (result.Url.Length == 0)
            {
                result.Url = token;
            }
            else
            {
                result.Warnings.Add($"Zusaetzliche URL ignoriert: {Shorten(token)}");
            }
        }

        if (result.Url.Length == 0)
        {
            result.Warnings.Add("In dem Befehl wurde keine URL gefunden.");
        }
        else if (!result.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 && !result.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add($"Die erkannte URL sieht ungewoehnlich aus: {Shorten(result.Url)}");
        }

        return result;
    }

    /// <summary>Prueft, ob ein -H-Wert der Range-Header ist, und liefert dessen Wert.</summary>
    private static bool IsRangeHeader(string header, out string value)
    {
        value = string.Empty;
        int colon = header.IndexOf(':');
        if (colon < 0
            || !header[..colon].Trim().Equals("Range", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = header[(colon + 1)..].Trim();
        return true;
    }

    /// <summary>
    /// Der Browser fordert bei Videos immer nur den Ausschnitt an, den der Player gerade braucht.
    /// Uebernaehme CurlGrabber diese Angabe, kaeme statt der Datei nur dieses Stueck an - manche
    /// CDNs antworten dabei sogar mit 200 statt 206, sodass curl die Kuerzung nicht bemerkt.
    /// </summary>
    private static void NoteRemovedRange(ParsedCurlCommand result, string value)
    {
        result.RemovedRange ??= value;
        result.Warnings.Add(
            $"Range-Angabe entfernt ({DescribeRange(value)}) - es wird die vollstaendige Datei geladen.");
    }

    /// <summary>Ergaenzt eine Range-Angabe um ihre Groesse, soweit sie sich ausrechnen laesst.</summary>
    private static string DescribeRange(string value)
    {
        string span = value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
            ? value["bytes=".Length..]
            : value;

        string[] parts = span.Split('-');
        if (parts.Length == 2
            && long.TryParse(parts[0].Trim(), out long from)
            && long.TryParse(parts[1].Trim(), out long to)
            && to >= from)
        {
            return $"{value} - nur {PathHelper.FormatBytes(to - from + 1)}";
        }

        return value;
    }

    /// <summary>Schlaegt anhand der URL einen Dateinamen vor.</summary>
    public static string SuggestFileName(string url)
    {
        const string fallback = "video.mp4";
        if (string.IsNullOrWhiteSpace(url))
        {
            return fallback;
        }

        string path;
        try
        {
            path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        }
        catch
        {
            path = url;
        }

        string segment = path.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
        try
        {
            segment = Uri.UnescapeDataString(segment);
        }
        catch
        {
            // Segment bleibt wie es ist.
        }

        segment = PathHelper.SanitizeFileName(segment);

        // Hinter einer Playlist steckt das Video, nicht die Liste - der Name soll passen.
        // .txt steht dabei fuer die Hoster, die ihre Master-Playlist als master.txt ausliefern;
        // eine echte Textdatei laedt mit diesem Werkzeug ohnehin niemand.
        if (segment.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || segment.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
            || segment.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return "video.ts";
        }

        string extension = Path.GetExtension(segment);
        bool usableExtension = extension.Length is >= 2 and <= 6
                               && extension[1..].All(char.IsLetterOrDigit);

        if (segment.Length == 0 || !usableExtension)
        {
            return fallback;
        }

        return segment;
    }

    private static bool LooksLikeCurlExecutable(string token)
    {
        if (token.Length == 0 || token[0] == '-')
        {
            return false;
        }

        try
        {
            string name = Path.GetFileNameWithoutExtension(token);
            return name.Equals("curl", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string Shorten(string value)
        => value.Length <= 70 ? value : value[..67] + "...";

    // ---------------------------------------------------------------- Tokenizer

    private static List<string> Tokenize(string input)
    {
        // "Als cURL kopieren (Windows)" escaped jedes Anfuehrungszeichen als ^" -- das ist das
        // eindeutigste Erkennungsmerkmal fuer die cmd.exe-Variante.
        if (input.Contains("^\"", StringComparison.Ordinal))
        {
            return TokenizeWindows(CaretUnescape(input));
        }

        return TokenizeBash(input);
    }

    /// <summary>Entfernt das cmd.exe-Escaping: ^X wird zu X, ^ am Zeilenende verbindet die Zeilen.</summary>
    private static string CaretUnescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '^')
            {
                sb.Append(c);
                continue;
            }

            if (i + 1 >= s.Length)
            {
                break; // einzelnes ^ am Ende
            }

            char next = s[i + 1];
            if (next is '\r' or '\n')
            {
                i++;
                if (next == '\r' && i + 1 < s.Length && s[i + 1] == '\n')
                {
                    i++;
                }

                sb.Append(' '); // Tokens muessen getrennt bleiben
                continue;
            }

            sb.Append(next);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Zerlegt eine Kommandozeile nach den Regeln der C-Runtime (wie sie curl.exe sieht).</summary>
    private static List<string> TokenizeWindows(string s)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        bool started = false;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            if (c == '"')
            {
                started = true;
                if (inQuotes && i + 1 < s.Length && s[i + 1] == '"')
                {
                    current.Append('"'); // "" innerhalb von Anfuehrungszeichen = literales "
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }

                continue;
            }

            current.Append(c);
            started = true;
        }

        if (started)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>Zerlegt eine Kommandozeile nach Bash-Regeln (POSIX-Variante, Chrome/Firefox).</summary>
    private static List<string> TokenizeBash(string s)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool started = false;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            if (c == '\\')
            {
                if (i + 1 >= s.Length)
                {
                    continue;
                }

                char next = s[i + 1];
                i++;
                if (next is '\r' or '\n')
                {
                    if (next == '\r' && i + 1 < s.Length && s[i + 1] == '\n')
                    {
                        i++;
                    }

                    continue; // Zeilenfortsetzung
                }

                current.Append(next);
                started = true;
                continue;
            }

            if (c == '\'')
            {
                started = true;
                i++;
                while (i < s.Length && s[i] != '\'')
                {
                    current.Append(s[i]);
                    i++;
                }

                continue;
            }

            if (c == '"')
            {
                started = true;
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        char next = s[i + 1];
                        if (next is '"' or '\\' or '$' or '`')
                        {
                            current.Append(next);
                            i += 2;
                            continue;
                        }

                        if (next == '\n')
                        {
                            i += 2;
                            continue;
                        }
                    }

                    current.Append(s[i]);
                    i++;
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }

                continue;
            }

            current.Append(c);
            started = true;
        }

        if (started)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
