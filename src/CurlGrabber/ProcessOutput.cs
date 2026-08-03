using System.Text;

namespace CurlGrabber;

/// <summary>
/// Liest einen Ausgabestrom zeichenweise. curl und ffmpeg trennen ihre Fortschrittszeilen mit
/// Wagenruecklauf statt Zeilenumbruch, deshalb funktioniert ReadLine() bei beiden nicht.
/// </summary>
public static class ProcessOutput
{
    public static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0)
                    {
                        onLine(line.ToString());
                        line.Clear();
                    }
                }
                else
                {
                    line.Append(c);
                }
            }
        }

        if (line.Length > 0)
        {
            onLine(line.ToString());
        }
    }
}
