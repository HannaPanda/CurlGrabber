namespace CurlGrabber;

public sealed class SegmentedDownloadResult
{
    /// <summary>Wie oft curl gestartet wurde.</summary>
    public int Requests { get; init; }

    /// <summary>Groesse der zusammengesetzten Datei.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Ergebnis des letzten curl-Laufs.</summary>
    public CurlResult Last { get; init; } = new();

    /// <summary>Gesetzt, wenn ein Abschnitt nicht an das Vorhandene anschloss.</summary>
    public bool Stalled { get; init; }

    public string? StallReason { get; init; }
}

/// <summary>
/// Holt eine Datei in mehreren Anfragen, bis nichts Neues mehr ankommt. Noetig bei Servern, die
/// pro Antwort nur eine begrenzte Menge herausgeben - dann liefert eine einzelne Anfrage eine
/// stillschweigend abgeschnittene Datei.
///
/// Zusammengesetzt wird nicht nach Byte-Positionen, sondern ueber die Ueberlappung: jede Anfrage
/// beginnt ein Stueck vor dem bereits Vorhandenen, und der neue Abschnitt wird an der Stelle
/// angesetzt, an der sich beide decken. Das ist noetig, weil manche Server den angeforderten
/// Start nicht genau einhalten - beobachtet wurde ein fester Versatz von 39 Bytes. Nach
/// Byte-Positionen zusammengesetzt fehlten an jeder Abschnittsgrenze genau diese Bytes.
/// </summary>
public sealed class SegmentedDownloader
{
    /// <summary>So weit vor dem Vorhandenen setzt die naechste Anfrage an.</summary>
    public const int OverlapBytes = 64 * 1024;

    /// <summary>
    /// So viele Bytes vom Ende des Vorhandenen dienen als Suchmuster. Bewusst deutlich kuerzer
    /// als die Rueckgriffweite: haelt der Server den angeforderten Start nicht genau ein und
    /// beginnt spaeter, faengt der Abschnitt hinter dem Anfang der Ueberlappung an. Ein Muster
    /// ueber die volle Ueberlappung waere dann nicht mehr enthalten und der Download bliebe
    /// stehen, obwohl die Daten luecklos anschliessen.
    /// </summary>
    public const int AnchorBytes = 4 * 1024;

    /// <summary>So weit vorne im Abschnitt wird die Ueberlappung gesucht.</summary>
    private const int SearchLimit = 4 * 1024 * 1024;

    /// <summary>Notbremse, damit ein sich falsch verhaltender Server keine Endlosschleife baut.</summary>
    private const int MaxRequests = 512;

    private readonly CurlRunner _runner;

    public SegmentedDownloader(CurlRunner runner) => _runner = runner;

    /// <param name="baseArguments">Header und Flags - ohne Range, ohne -o und ohne URL.</param>
    /// <param name="onChunk">Meldet nach jedem Abschnitt: Nummer, neue Bytes, Gesamtgroesse.</param>
    public async Task<SegmentedDownloadResult> DownloadAsync(
        IReadOnlyList<string> baseArguments,
        string url,
        string targetPath,
        bool resume,
        Action<string> onLine,
        Action<int, long, long> onChunk,
        CancellationToken cancellationToken)
    {
        string chunkPath = targetPath + ".teil";
        long have = resume && File.Exists(targetPath) ? new FileInfo(targetPath).Length : 0;
        if (!resume)
        {
            File.Delete(targetPath);
        }

        int requests = 0;
        var last = new CurlResult();

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (requests >= MaxRequests)
                {
                    return Stall(requests, have, last, $"Abbruch nach {MaxRequests} Anfragen.");
                }

                var arguments = new List<string>(baseArguments);

                // Die erste Anfrage bleibt ohne Range - so verhaelt sich der Server wie beim
                // Abspielen im Browser und liefert den Dateianfang mitsamt seinem Vorspann.
                if (have > 0)
                {
                    arguments.Add("-H");
                    arguments.Add($"Range: bytes={Math.Max(0, have - OverlapBytes)}-");
                }

                arguments.Add("-o");
                arguments.Add(chunkPath);
                arguments.Add(url);

                requests++;
                last = await _runner.RunAsync(arguments, onLine, cancellationToken).ConfigureAwait(false);

                if (last.Canceled)
                {
                    return new SegmentedDownloadResult
                    {
                        Requests = requests,
                        TotalBytes = have,
                        Last = last,
                    };
                }

                if (last.ExitCode != 0)
                {
                    return new SegmentedDownloadResult
                    {
                        Requests = requests,
                        TotalBytes = have,
                        Last = last,
                    };
                }

                long chunkLength = File.Exists(chunkPath) ? new FileInfo(chunkPath).Length : 0;
                if (chunkLength == 0)
                {
                    break; // Nichts mehr da - fertig.
                }

                long added;
                try
                {
                    added = Append(targetPath, chunkPath, have);
                }
                catch (InvalidDataException ex)
                {
                    return Stall(requests, have, last, ex.Message);
                }

                if (added <= 0)
                {
                    break; // Der Abschnitt brachte nichts Neues - fertig.
                }

                have += added;
                onChunk(requests, added, have);
            }
        }
        finally
        {
            TryDelete(chunkPath);
        }

        return new SegmentedDownloadResult
        {
            Requests = requests,
            TotalBytes = have,
            Last = last,
        };
    }

    private static SegmentedDownloadResult Stall(int requests, long have, CurlResult last, string reason)
        => new()
        {
            Requests = requests,
            TotalBytes = have,
            Last = last,
            Stalled = true,
            StallReason = reason,
        };

    /// <summary>Haengt den neuen Abschnitt an und liefert die Anzahl der neuen Bytes.</summary>
    private static long Append(string targetPath, string chunkPath, long have)
    {
        using var chunk = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (have == 0)
        {
            using var fresh = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            chunk.CopyTo(fresh);
            return fresh.Length;
        }

        int anchorLength = (int)Math.Min(AnchorBytes, have);
        var anchor = new byte[anchorLength];
        using (var target = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            target.Position = have - anchorLength;
            target.ReadExactly(anchor, 0, anchorLength);
        }

        int lookahead = (int)Math.Min(chunk.Length, SearchLimit);
        var head = new byte[lookahead];
        chunk.ReadExactly(head, 0, lookahead);

        int overlapEnd = FindOverlapEnd(anchor, head);
        if (overlapEnd < 0)
        {
            throw new InvalidDataException(
                "Der nachgeladene Abschnitt schliesst nicht an die bereits geladenen Daten an.");
        }

        using var append = new FileStream(targetPath, FileMode.Open, FileAccess.Write, FileShare.None);
        append.Position = have;
        chunk.Position = overlapEnd;
        chunk.CopyTo(append);
        return chunk.Length - overlapEnd;
    }

    /// <summary>
    /// Sucht im neuen Abschnitt die Stelle, an der das bereits Vorhandene endet - also das
    /// Ankermuster. Liefert -1, wenn der Abschnitt nicht anschliesst.
    /// </summary>
    public static int FindOverlapEnd(ReadOnlySpan<byte> anchor, ReadOnlySpan<byte> chunk)
    {
        if (anchor.Length == 0)
        {
            return 0;
        }

        if (chunk.Length < anchor.Length)
        {
            return -1;
        }

        int found = chunk.IndexOf(anchor);
        return found < 0 ? -1 : found + anchor.Length;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Die Teildatei bleibt liegen - kein Grund, den Download scheitern zu lassen.
        }
    }
}
