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
    private readonly CurlRunner _runner;

    public PlaylistDownloader(CurlRunner runner) => _runner = runner;

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
        string chunkPath = targetPath + ".teil";
        long total = 0;
        var last = new CurlResult();

        try
        {
            using (var fresh = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // Legt die Zieldatei leer an, bevor angehaengt wird.
            }

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
