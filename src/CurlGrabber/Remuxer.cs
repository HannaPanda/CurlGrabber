using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CurlGrabber;

public sealed class RemuxResult
{
    public bool Succeeded { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>Gesetzt, wenn ffmpeg gar nicht erst gefunden wurde.</summary>
    public bool ToolMissing { get; init; }
}

/// <summary>
/// Packt einen MPEG-Transportstrom ohne Neukodierung in einen MP4-Container um und fuegt dabei
/// auf Wunsch eine getrennt geladene Tonspur wieder mit dem Bild zusammen.
///
/// Umgepackt wird mit <c>-c copy</c>: Bild und Ton bleiben Byte fuer Byte dieselben, es wechselt
/// nur die Verpackung. Das kostet Sekunden statt Stunden und verliert nichts.
/// </summary>
public sealed class Remuxer
{
    private Process? _process;

    /// <summary>Pfad zu ffmpeg.exe: neben der eigenen EXE, sonst im PATH.</summary>
    public static string? ResolveFfmpegPath()
    {
        string? beside = Path.GetDirectoryName(Environment.ProcessPath);
        if (beside is not null)
        {
            string candidate = Path.Combine(beside, "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        foreach (string dir in (pathVariable ?? string.Empty).Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
            {
                continue;
            }

            try
            {
                string candidate = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ungueltiger PATH-Eintrag - ueberspringen.
            }
        }

        return null;
    }

    /// <summary>
    /// Baut die Argumentliste. Ohne <paramref name="audioPath"/> wird der Ton aus derselben
    /// Datei uebernommen - dann darf er aber auch fehlen, deshalb das Fragezeichen.
    /// </summary>
    public static List<string> BuildArguments(string videoPath, string? audioPath, string targetPath)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", videoPath };

        if (audioPath is not null)
        {
            arguments.Add("-i");
            arguments.Add(audioPath);
        }

        arguments.AddRange(["-map", "0:v:0"]);
        arguments.AddRange(audioPath is not null ? ["-map", "1:a:0"] : ["-map", "0:a:0?"]);

        // -c copy kopiert die Spuren unveraendert, +faststart zieht die Sprungtabelle nach vorne,
        // damit die Datei sofort abspielbar und durchsuchbar ist.
        arguments.AddRange(["-c", "copy", "-movflags", "+faststart", targetPath]);
        return arguments;
    }

    /// <summary>Liest aus ffmpegs Statuszeile den bereits verarbeiteten Zeitpunkt in Sekunden.</summary>
    public static double? ParseProgressSeconds(string line)
    {
        int at = line.IndexOf("time=", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        string value = line[(at + 5)..].TrimStart();
        int end = value.IndexOf(' ');
        if (end >= 0)
        {
            value = value[..end];
        }

        string[] parts = value.Split(':');
        if (parts.Length != 3)
        {
            return null;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return null;
        }

        return (hours * 3600) + (minutes * 60) + seconds;
    }

    public async Task<RemuxResult> RemuxAsync(
        string videoPath,
        string? audioPath,
        string targetPath,
        Action<string> onLine,
        Action<double> onSeconds,
        CancellationToken cancellationToken)
    {
        string? ffmpeg = ResolveFfmpegPath();
        if (ffmpeg is null)
        {
            return new RemuxResult
            {
                ToolMissing = true,
                Message = "ffmpeg wurde nicht gefunden - weder neben CurlGrabber.exe noch im PATH.",
            };
        }

        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in BuildArguments(videoPath, audioPath, targetPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        _process = process;

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Prozess war bereits beendet.
            }
        });

        var tail = new List<string>();

        void Handle(string line)
        {
            if (ParseProgressSeconds(line) is { } seconds)
            {
                onSeconds(seconds);
                return;
            }

            // Nur die letzten Zeilen aufheben - im Fehlerfall steht der Grund ganz hinten.
            tail.Add(line);
            if (tail.Count > 20)
            {
                tail.RemoveAt(0);
            }

            onLine(line);
        }

        try
        {
            await Task.WhenAll(
                ProcessOutput.PumpAsync(process.StandardError, Handle),
                ProcessOutput.PumpAsync(process.StandardOutput, Handle)).ConfigureAwait(false);

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _process = null;
        }

        if (process.ExitCode == 0)
        {
            return new RemuxResult { Succeeded = true, Message = "Umgepackt nach MP4." };
        }

        string reason = tail.LastOrDefault(l => l.Length > 0) ?? $"Abbruch mit Code {process.ExitCode}";
        return new RemuxResult { Message = $"ffmpeg scheiterte (Code {process.ExitCode}): {reason}" };
    }
}
