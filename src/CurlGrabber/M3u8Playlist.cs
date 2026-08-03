using System.Globalization;
using System.Text;

namespace CurlGrabber;

/// <summary>Ein Eintrag der Playlist: eine URL, optional mit Byte-Bereich darin.</summary>
public sealed class PlaylistSegment
{
    public string Url { get; init; } = string.Empty;

    /// <summary>Startbyte innerhalb der Datei, -1 wenn die ganze Datei gemeint ist.</summary>
    public long Offset { get; init; } = -1;

    /// <summary>Laenge des Bereichs, -1 wenn die ganze Datei gemeint ist.</summary>
    public long Length { get; init; } = -1;

    public double Duration { get; init; }

    public bool HasByteRange => Length >= 0;
}

/// <summary>
/// Ein zusammenhaengend ladbares Stueck: entweder eine ganze Datei oder ein Bereich daraus,
/// entstanden aus mehreren aufeinanderfolgenden Segmenten derselben Datei.
/// </summary>
public sealed class DownloadRun
{
    public string Url { get; init; } = string.Empty;

    public long Offset { get; init; }

    /// <summary>Laenge, -1 fuer "bis zum Ende".</summary>
    public long Length { get; init; } = -1;

    public int SegmentCount { get; init; }

    public double Duration { get; init; }

    /// <summary>Nur am Dateianfang kann ein vorgetaeuschter Datei-Anfang stehen.</summary>
    public bool FromStart => Offset == 0;
}

/// <summary>
/// Liest eine HLS-Playlist. Unterstuetzt neben gewoehnlichen Segmentlisten auch
/// EXT-X-BYTERANGE, bei dem viele Segmente in wenigen grossen Dateien liegen und nur ueber
/// Byte-Bereiche angesprochen werden.
/// </summary>
public sealed class M3u8Playlist
{
    public List<PlaylistSegment> Segments { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>Bei einer Master-Playlist: die URL der besten Variante.</summary>
    public string? VariantUrl { get; private set; }

    /// <summary>
    /// Bei einer Master-Playlist mit getrennter Tonspur: die URL der Ton-Playlist. Dann enthaelt
    /// die Bild-Variante wirklich nur Bild und muss hinterher wieder mit dem Ton zusammengefuegt
    /// werden.
    /// </summary>
    public string? AudioUrl { get; private set; }

    /// <summary>Sprache oder Name der gewaehlten Tonspur, nur fuers Log.</summary>
    public string? AudioName { get; private set; }

    /// <summary>Gesetzt, wenn die Segmente verschluesselt sind - dann ist hier Schluss.</summary>
    public string? Encryption { get; private set; }

    public double TotalDuration => Segments.Sum(s => s.Duration);

    /// <summary>Erkennt am Dateianfang, ob es sich um eine Playlist handelt.</summary>
    public static bool LooksLikePlaylist(ReadOnlySpan<byte> head)
    {
        // Ein BOM davor ist erlaubt, sonst muss #EXTM3U als Erstes stehen.
        ReadOnlySpan<byte> bom = [0xef, 0xbb, 0xbf];
        if (head.StartsWith(bom))
        {
            head = head[3..];
        }

        return head.StartsWith("#EXTM3U"u8);
    }

    public static M3u8Playlist Parse(string text, string playlistUrl)
    {
        var playlist = new M3u8Playlist();

        double duration = 0;
        long rangeLength = -1;
        long rangeOffset = -1;
        long bestBandwidth = -1;
        string? pendingVariant = null;
        bool audioIsDefault = false;

        // Ohne @-Angabe schliesst ein Bereich an den vorigen derselben Datei an.
        var nextOffset = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    string value = line["#EXTINF:".Length..].Split(',')[0];
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                }
                else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
                {
                    ParseByteRange(line["#EXT-X-BYTERANGE:".Length..], out rangeLength, out rangeOffset);
                }
                else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                {
                    string method = AttributeValue(line, "METHOD") ?? "unbekannt";
                    if (!method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    {
                        playlist.Encryption = method;
                    }
                }
                else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    string? bandwidth = AttributeValue(line, "BANDWIDTH");
                    pendingVariant = bandwidth ?? "0";
                }
                else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
                {
                    // Getrennte Spuren. Nur Ton ist hier interessant; Untertitel laesst
                    // CurlGrabber liegen. Ohne URI steckt die Spur schon im Bild-Strom.
                    string? type = AttributeValue(line, "TYPE");
                    string? uri = AttributeValue(line, "URI");

                    if (uri is { Length: > 0 }
                        && string.Equals(type, "AUDIO", StringComparison.OrdinalIgnoreCase))
                    {
                        bool isDefault = string.Equals(
                            AttributeValue(line, "DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase);

                        // Die als DEFAULT gekennzeichnete Spur gewinnt, sonst die erste.
                        if (playlist.AudioUrl is null || (isDefault && !audioIsDefault))
                        {
                            playlist.AudioUrl = Resolve(playlistUrl, uri);
                            playlist.AudioName = AttributeValue(line, "NAME")
                                                 ?? AttributeValue(line, "LANGUAGE");
                            audioIsDefault = isDefault;
                        }
                    }
                }

                continue;
            }

            string resolved = Resolve(playlistUrl, line);

            // Zeile nach EXT-X-STREAM-INF: eine Variante, keine Segmentliste.
            if (pendingVariant is not null)
            {
                if (long.TryParse(pendingVariant, out long bandwidth) && bandwidth > bestBandwidth)
                {
                    bestBandwidth = bandwidth;
                    playlist.VariantUrl = resolved;
                }
                else if (playlist.VariantUrl is null)
                {
                    playlist.VariantUrl = resolved;
                }

                pendingVariant = null;
                continue;
            }

            long offset = rangeOffset;
            if (rangeLength >= 0 && offset < 0)
            {
                offset = nextOffset.GetValueOrDefault(resolved);
            }

            if (rangeLength >= 0)
            {
                nextOffset[resolved] = offset + rangeLength;
            }

            playlist.Segments.Add(new PlaylistSegment
            {
                Url = resolved,
                Offset = rangeLength >= 0 ? offset : -1,
                Length = rangeLength,
                Duration = duration,
            });

            duration = 0;
            rangeLength = -1;
            rangeOffset = -1;
        }

        if (playlist.Segments.Count == 0 && playlist.VariantUrl is null)
        {
            playlist.Warnings.Add("In der Playlist standen keine Segmente.");
        }

        return playlist;
    }

    /// <summary>
    /// Fasst aufeinanderfolgende Segmente derselben Datei zu einem Stueck zusammen. Bei
    /// Byte-Bereichs-Playlists werden aus vielen hundert Segmenten so wenige grosse Abrufe.
    /// </summary>
    public static List<DownloadRun> BuildRuns(IReadOnlyList<PlaylistSegment> segments)
    {
        var runs = new List<DownloadRun>();

        foreach (var segment in segments)
        {
            if (!segment.HasByteRange)
            {
                runs.Add(new DownloadRun
                {
                    Url = segment.Url,
                    Offset = 0,
                    Length = -1,
                    SegmentCount = 1,
                    Duration = segment.Duration,
                });
                continue;
            }

            var last = runs.Count > 0 ? runs[^1] : null;
            if (last is { Length: >= 0 }
                && string.Equals(last.Url, segment.Url, StringComparison.Ordinal)
                && last.Offset + last.Length == segment.Offset)
            {
                runs[^1] = new DownloadRun
                {
                    Url = last.Url,
                    Offset = last.Offset,
                    Length = last.Length + segment.Length,
                    SegmentCount = last.SegmentCount + 1,
                    Duration = last.Duration + segment.Duration,
                };
                continue;
            }

            runs.Add(new DownloadRun
            {
                Url = segment.Url,
                Offset = segment.Offset,
                Length = segment.Length,
                SegmentCount = 1,
                Duration = segment.Duration,
            });
        }

        return runs;
    }

    // ------------------------------------------------------------------ Hilfen

    private static void ParseByteRange(string spec, out long length, out long offset)
    {
        length = -1;
        offset = -1;

        string[] parts = spec.Trim().Split('@');
        if (parts.Length >= 1 && long.TryParse(parts[0], out long parsedLength))
        {
            length = parsedLength;
        }

        if (parts.Length == 2 && long.TryParse(parts[1], out long parsedOffset))
        {
            offset = parsedOffset;
        }
    }

    private static string? AttributeValue(string line, string name)
    {
        int start = line.IndexOf(name + "=", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += name.Length + 1;
        if (start >= line.Length)
        {
            return null;
        }

        if (line[start] == '"')
        {
            int end = line.IndexOf('"', start + 1);
            return end < 0 ? null : line[(start + 1)..end];
        }

        int comma = line.IndexOf(',', start);
        return comma < 0 ? line[start..] : line[start..comma];
    }

    /// <summary>Macht aus einer Playlist-Zeile eine absolute URL.</summary>
    public static string Resolve(string playlistUrl, string reference)
    {
        if (reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return reference;
        }

        if (!Uri.TryCreate(playlistUrl, UriKind.Absolute, out var baseUri))
        {
            return reference;
        }

        // Protokollrelativ: //host/pfad uebernimmt das Schema der Playlist.
        if (reference.StartsWith("//", StringComparison.Ordinal))
        {
            return baseUri.Scheme + ":" + reference;
        }

        return Uri.TryCreate(baseUri, reference, out var absolute) ? absolute.ToString() : reference;
    }

    /// <summary>Liest die Playlist als Text - sie kommt gern mit falschem Content-Type.</summary>
    public static string ReadText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
    }
}
