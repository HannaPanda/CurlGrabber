using System.Diagnostics;
using System.Text;

namespace CurlGrabber;

public sealed class CurlResult
{
    public int ExitCode { get; init; }
    public bool Canceled { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>Startet curl.exe und liefert dessen Ausgabe zeilenweise zurueck.</summary>
public sealed class CurlRunner
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Pfad zu curl.exe: bevorzugt das mit Windows ausgelieferte in System32.</summary>
    public static string ResolveCurlPath()
    {
        string system32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
        if (File.Exists(system32))
        {
            return system32;
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
                string candidate = Path.Combine(dir, "curl.exe");
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

        return "curl.exe";
    }

    public async Task<CurlResult> RunAsync(
        IReadOnlyList<string> arguments,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ResolveCurlPath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Jedes Argument einzeln uebergeben - so muss nichts fuer eine Kommandozeile
        // escaped werden und Header duerfen beliebige Sonderzeichen enthalten.
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        _process = process;

        bool canceled = false;
        using var registration = cancellationToken.Register(() =>
        {
            canceled = true;
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

        try
        {
            await Task.WhenAll(
                ProcessOutput.PumpAsync(process.StandardError, onLine),
                ProcessOutput.PumpAsync(process.StandardOutput, onLine)).ConfigureAwait(false);

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _process = null;
        }

        int exitCode = process.ExitCode;
        return new CurlResult
        {
            ExitCode = exitCode,
            Canceled = canceled,
            Description = DescribeExitCode(exitCode),
        };
    }

    public static string DescribeExitCode(int code) => code switch
    {
        0 => "Erfolgreich abgeschlossen.",
        1 => "Nicht unterstuetztes Protokoll.",
        3 => "Fehlerhafte URL.",
        5 => "Proxy konnte nicht aufgeloest werden.",
        6 => "Host konnte nicht aufgeloest werden - stimmt die Adresse noch?",
        7 => "Verbindung zum Server fehlgeschlagen.",
        18 => "Uebertragung unvollstaendig - der Server hat vorzeitig geschlossen.",
        22 => "Der Server hat mit einem HTTP-Fehler geantwortet (z. B. 403/404). "
              + "Bei CDN-Links ist meist der Token abgelaufen: cURL-Befehl neu kopieren.",
        23 => "Schreibfehler - Ziellaufwerk voll oder Datei gesperrt?",
        26 => "Lesefehler bei den Sendedaten.",
        27 => "Zu wenig Speicher.",
        28 => "Zeitueberschreitung.",
        33 => "Der Server unterstuetzt kein Fortsetzen - ohne Fortsetzen erneut versuchen.",
        35 => "TLS-Handshake fehlgeschlagen.",
        36 => "Fortsetzen nicht moeglich - die vorhandene Datei passt nicht zum Server.",
        47 => "Zu viele Weiterleitungen.",
        52 => "Der Server hat nichts zurueckgeliefert.",
        56 => "Fehler beim Empfangen der Daten.",
        60 => "Zertifikat konnte nicht geprueft werden.",
        61 => "Der Server hat in einem Verfahren komprimiert, das curl nicht auspacken kann "
              + "(meist brotli oder zstd aus einem mitkopierten Accept-Encoding-Header).",
        _ => $"curl wurde mit Code {code} beendet.",
    };
}
