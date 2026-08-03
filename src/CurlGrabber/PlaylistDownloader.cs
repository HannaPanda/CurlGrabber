namespace CurlGrabber;

public sealed class PlaylistDownloadResult
{
    public int RunsDone { get; init; }

    public int RunsTotal { get; init; }

    public long TotalBytes { get; init; }

    public CurlResult Last { get; init; } = new();

    public bool Failed { get; init; }

    public string? FailReason { get; init; }
}

/// <summary>
/// Laedt alle Stuecke einer Playlist und setzt sie in Playlist-Reihenfolge zusammen.
/// Ein vorgetaeuschter Datei-Anfang wird dabei je Datei einmal entfernt - er steht nur ganz
/// vorne, also nur in Stuecken, die bei Byte 0 beginnen.
/// </summary>
public sealed class PlaylistDownloader
{
    /// <summary>So viele Stuecke gehen in einen curl-Aufruf.</summary>
    public const int BatchSize = 24;

    /// <summary>Gleichzeitige Verbindungen innerhalb eines Aufrufs.</summary>
    public const int ParallelMax = 6;

    /// <summary>Ab so vielen Stuecken lohnt das Buendeln.</summary>
    public const int BatchThreshold = 8;

    private readonly CurlRunner _runner;

    public PlaylistDownloader(CurlRunner runner) => _runner = runner;

    /// <summary>
    /// Gebuendelt wird nur, wenn jedes Stueck eine ganze Datei ist. Sobald Byte-Bereiche im Spiel
    /// sind, braucht jedes Stueck seinen eigenen Range-Header, und der gilt in curl fuer alle
    /// URLs eines Aufrufs.
    /// </summary>
    public static bool CanBatch(IReadOnlyList<DownloadRun> runs)
        => runs.Count >= BatchThreshold && runs.All(r => r is { Offset: 0, Length: < 0 });

    public async Task<PlaylistDownloadResult> DownloadAsync(
        IReadOnlyList<string> baseArguments,
        IReadOnlyList<DownloadRun> runs,
        string targetPath,
        bool trimJunkPrefix,
        Action<string> onLine,
        Action<int, int> onRunStarting,
        Action<int, long, long> onRun,
        CancellationToken cancellationToken)
    {
        using (var fresh = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // Legt die Zieldatei leer an, bevor angehaengt wird.
        }

        return CanBatch(runs)
            ? await DownloadBatchedAsync(
                baseArguments, runs, targetPath, trimJunkPrefix,
                onLine, onRunStarting, onRun, cancellationToken).ConfigureAwait(false)
            : await DownloadOneByOneAsync(
                baseArguments, runs, targetPath, trimJunkPrefix,
                onLine, onRunStarting, onRun, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ein Abruf nach dem anderen, jeder mit eigenem Range-Header. Der Weg fuer Playlists mit
    /// EXT-X-BYTERANGE, bei denen aus vielen Segmenten ohnehin nur wenige grosse Abrufe werden.
    /// </summary>
    private async Task<PlaylistDownloadResult> DownloadOneByOneAsync(
        IReadOnlyList<string> baseArguments,
        IReadOnlyList<DownloadRun> runs,
        string targetPath,
        bool trimJunkPrefix,
        Action<string> onLine,
        Action<int, int> onRunStarting,
        Action<int, long, long> onRun,
        CancellationToken cancellationToken)
    {
        string chunkPath = targetPath + ".teil";
        long total = 0;
        var last = new CurlResult();

        try
        {
            for (int i = 0; i < runs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var run = runs[i];
                onRunStarting(i + 1, runs.Count);
                var arguments = new List<string>(baseArguments);

                if (run.Offset > 0 || run.Length >= 0)
                {
                    long end = run.Length >= 0 ? run.Offset + run.Length - 1 : -1;
                    arguments.Add("-H");
                    arguments.Add(end >= 0
                        ? $"Range: bytes={run.Offset}-{end}"
                        : $"Range: bytes={run.Offset}-");
                }

                arguments.Add("-o");
                arguments.Add(chunkPath);
                arguments.Add(run.Url);

                last = await _runner.RunAsync(arguments, onLine, cancellationToken).ConfigureAwait(false);

                if (last.Canceled || last.ExitCode != 0)
                {
                    return new PlaylistDownloadResult
                    {
                        RunsDone = i,
                        RunsTotal = runs.Count,
                        TotalBytes = total,
                        Last = last,
                    };
                }

                long got = File.Exists(chunkPath) ? new FileInfo(chunkPath).Length : 0;
                if (got == 0)
                {
                    return Fail(i, runs.Count, total, last,
                        $"Stueck {i + 1} kam leer zurueck ({Short(run.Url)}).");
                }

                // Wird der Range nicht beachtet, kommt mehr als bestellt - dann auf die in der
                // Playlist angegebene Laenge kuerzen.
                if (run.Length >= 0 && got > run.Length)
                {
                    Truncate(chunkPath, run.Length);
                    got = run.Length;
                }

                if (run.Length >= 0 && got < run.Length)
                {
                    return Fail(i, runs.Count, total, last,
                        $"Stueck {i + 1} ist unvollstaendig: {got} statt {run.Length} Bytes "
                        + $"({Short(run.Url)}).");
                }

                if (trimJunkPrefix && run.FromStart)
                {
                    var scan = PayloadTrimmer.Scan(chunkPath);
                    if (scan.HasPrefix)
                    {
                        PayloadTrimmer.RemovePrefix(chunkPath, scan.PrefixLength);
                        got -= scan.PrefixLength;
                    }
                }

                AppendTo(targetPath, chunkPath);
                total += got;
                onRun(i + 1, got, total);
            }
        }
        finally
        {
            TryDelete(chunkPath);
        }

        return new PlaylistDownloadResult
        {
            RunsDone = runs.Count,
            RunsTotal = runs.Count,
            TotalBytes = total,
            Last = last,
        };
    }

    /// <summary>
    /// Mehrere Stuecke je curl-Aufruf, innerhalb des Aufrufs parallel. Bei Playlists mit
    /// tausenden Zwei-Sekunden-Segmenten ist der Prozessstart samt TLS-Handschlag sonst teurer
    /// als die Uebertragung selbst - gemessen 3,9 s gegen 0,5 s fuer zwanzig Segmente.
    /// Angehaengt wird trotzdem streng in Playlist-Reihenfolge.
    /// </summary>
    private async Task<PlaylistDownloadResult> DownloadBatchedAsync(
        IReadOnlyList<string> baseArguments,
        IReadOnlyList<DownloadRun> runs,
        string targetPath,
        bool trimJunkPrefix,
        Action<string> onLine,
        Action<int, int> onRunStarting,
        Action<int, long, long> onRun,
        CancellationToken cancellationToken)
    {
        long total = 0;
        var last = new CurlResult();
        var chunkPaths = new List<string>();

        // Die Namen wiederholen sich je Charge; die letzte ist kleiner, deshalb wird zum
        // Aufraeumen alles gesammelt, was jemals angelegt wurde.
        var everUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (int start = 0; start < runs.Count; start += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int count = Math.Min(BatchSize, runs.Count - start);
                onRunStarting(start + 1, runs.Count);

                // Ohne eigene Fortschrittsanzeige: bei parallelen Uebertragungen schreibt curl
                // eine andere Tabelle, und die Zahlen kommen hier ohnehin aus den Stueckzahlen.
                var arguments = new List<string>(baseArguments) { "--no-progress-meter" };
                arguments.AddRange(["--parallel", "--parallel-max", ParallelMax.ToString()]);

                chunkPaths.Clear();
                for (int j = 0; j < count; j++)
                {
                    string chunkPath = $"{targetPath}.teil{j}";
                    chunkPaths.Add(chunkPath);
                    everUsed.Add(chunkPath);
                    arguments.AddRange(["-o", chunkPath, runs[start + j].Url]);
                }

                last = await _runner.RunAsync(arguments, onLine, cancellationToken).ConfigureAwait(false);

                if (last.Canceled || last.ExitCode != 0)
                {
                    return new PlaylistDownloadResult
                    {
                        RunsDone = start,
                        RunsTotal = runs.Count,
                        TotalBytes = total,
                        Last = last,
                    };
                }

                for (int j = 0; j < count; j++)
                {
                    string chunkPath = chunkPaths[j];
                    long got = File.Exists(chunkPath) ? new FileInfo(chunkPath).Length : 0;
                    if (got == 0)
                    {
                        return Fail(start + j, runs.Count, total, last,
                            $"Stueck {start + j + 1} kam leer zurueck ({Short(runs[start + j].Url)}).");
                    }

                    if (trimJunkPrefix)
                    {
                        var scan = PayloadTrimmer.Scan(chunkPath);
                        if (scan.HasPrefix)
                        {
                            PayloadTrimmer.RemovePrefix(chunkPath, scan.PrefixLength);
                            got -= scan.PrefixLength;
                        }
                    }

                    AppendTo(targetPath, chunkPath);
                    total += got;
                    onRun(start + j + 1, got, total);
                }
            }
        }
        finally
        {
            foreach (string chunkPath in everUsed)
            {
                TryDelete(chunkPath);
            }
        }

        return new PlaylistDownloadResult
        {
            RunsDone = runs.Count,
            RunsTotal = runs.Count,
            TotalBytes = total,
            Last = last,
        };
    }

    private static PlaylistDownloadResult Fail(
        int done, int totalRuns, long bytes, CurlResult last, string reason)
        => new()
        {
            RunsDone = done,
            RunsTotal = totalRuns,
            TotalBytes = bytes,
            Last = last,
            Failed = true,
            FailReason = reason,
        };

    private static void AppendTo(string targetPath, string chunkPath)
    {
        using var source = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var target = new FileStream(targetPath, FileMode.Append, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }

    private static void Truncate(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    private static string Short(string url) => url.Length <= 60 ? url : url[..57] + "...";

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
            // Teildatei bleibt liegen - kein Grund, deswegen zu scheitern.
        }
    }
}
