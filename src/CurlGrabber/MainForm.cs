using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CurlGrabber;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly CurlRunner _runner = new();

    private readonly TextBox _txtCurl = new();
    private readonly TextBox _txtFile = new();
    private readonly TextBox _txtFolder = new();
    private readonly TextBox _txtLog = new();
    private readonly Label _lblParseInfo = new();
    private readonly Label _lblStatus = new();
    private readonly ProgressBar _progress = new();
    private readonly CheckBox _chkResume = new();
    private readonly CheckBox _chkRetry = new();
    private readonly CheckBox _chkFail = new();
    private readonly CheckBox _chkTrim = new();
    private readonly CheckBox _chkSegments = new();
    private readonly CheckBox _chkRemux = new();
    private readonly Button _btnPaste = new();
    private readonly Button _btnClear = new();
    private readonly Button _btnSuggest = new();
    private readonly Button _btnBrowse = new();
    private readonly Button _btnStart = new();
    private readonly Button _btnCancel = new();
    private readonly Button _btnOpenFolder = new();

    private CancellationTokenSource? _cancellation;
    private string _lastSuggestion = string.Empty;
    private string _lastParsedUrl = string.Empty;
    private string _lastTargetPath = string.Empty;
    private DateTime _startedAt;
    private string _overallPrefix = string.Empty;

    /// <summary>Gesetzt, wenn die Playlist Bild und Ton getrennt fuehrt.</summary>
    private string? _separateAudioPath;

    private double _playlistSeconds;

    public MainForm()
    {
        _settings = AppSettings.Load();

        Text = "CurlGrabber";
        MinimumSize = new Size(760, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Size = _settings is { WindowWidth: > 400, WindowHeight: > 300 }
            ? new Size(_settings.WindowWidth, _settings.WindowHeight)
            : new Size(920, 760);
        if (_settings.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }

        BuildLayout();
        WireEvents();

        _txtFolder.Text = _settings.ResolveStartFolder();
        _chkResume.Checked = _settings.Resume;
        _chkRetry.Checked = _settings.Retry;
        _chkFail.Checked = _settings.FailOnHttpError;
        _chkTrim.Checked = _settings.TrimJunkPrefix;
        _chkSegments.Checked = _settings.Segmented;
        _chkRemux.Checked = _settings.RemuxToMp4;

        SetStatus("Bereit. cURL-Befehl einfuegen.");
        UpdateParseInfo();
    }

    // ------------------------------------------------------------------ Layout

    private void BuildLayout()
    {
        var monospace = new Font("Consolas", 9f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // 0 Ueberschrift
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));         // 1 cURL-Feld
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // 2 Buttons + Parse-Info
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // 3 Ziel
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // 4 Optionen
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // 5 Aktionen
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // 6 Fortschritt
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));            // 7 Status
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));         // 8 Log

        root.Controls.Add(new Label
        {
            Text = "cURL-Befehl  (Firefox: Netzwerkanalyse → Rechtsklick → Kopieren → Als cURL kopieren)",
            AutoSize = true,
            Margin = new Padding(3, 0, 3, 4),
        }, 0, 0);

        _txtCurl.Multiline = true;
        _txtCurl.ScrollBars = ScrollBars.Both;
        _txtCurl.WordWrap = false;
        _txtCurl.Font = monospace;
        _txtCurl.Dock = DockStyle.Fill;
        _txtCurl.AcceptsTab = false;
        root.Controls.Add(_txtCurl, 0, 1);

        var curlButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 8),
        };
        ConfigureButton(_btnPaste, "Aus Zwischenablage einfuegen", 190);
        ConfigureButton(_btnClear, "Leeren", 80);
        _lblParseInfo.AutoSize = true;
        _lblParseInfo.Margin = new Padding(12, 8, 3, 3);
        _lblParseInfo.ForeColor = SystemColors.GrayText;
        curlButtons.Controls.Add(_btnPaste);
        curlButtons.Controls.Add(_btnClear);
        curlButtons.Controls.Add(_lblParseInfo);
        root.Controls.Add(curlButtons, 0, 2);

        var target = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6),
        };
        target.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        target.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        target.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        target.Controls.Add(new Label
        {
            Text = "Dateiname:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 8, 3),
        }, 0, 0);
        _txtFile.Dock = DockStyle.Fill;
        _txtFile.Font = monospace;
        target.Controls.Add(_txtFile, 1, 0);
        ConfigureButton(_btnSuggest, "Aus URL", 110);
        _btnSuggest.Margin = new Padding(6, 3, 3, 3);
        target.Controls.Add(_btnSuggest, 2, 0);

        target.Controls.Add(new Label
        {
            Text = "Zielordner:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 8, 3),
        }, 0, 1);
        _txtFolder.Dock = DockStyle.Fill;
        _txtFolder.Font = monospace;
        target.Controls.Add(_txtFolder, 1, 1);
        ConfigureButton(_btnBrowse, "Durchsuchen…", 110);
        _btnBrowse.Margin = new Padding(6, 3, 3, 3);
        target.Controls.Add(_btnBrowse, 2, 1);

        root.Controls.Add(target, 0, 3);

        var options = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6),
        };
        ConfigureCheckBox(_chkResume, "Abgebrochenen Download fortsetzen (-C -)");
        ConfigureCheckBox(_chkRetry, "Bei Netzwerkfehlern wiederholen (--retry)");
        ConfigureCheckBox(_chkFail, "Bei HTTP-Fehler abbrechen (--fail)");
        ConfigureCheckBox(_chkTrim, "Vorgetaeuschten Datei-Anfang entfernen");
        ConfigureCheckBox(_chkSegments, "In Abschnitten laden, bis nichts mehr kommt");
        ConfigureCheckBox(_chkRemux, "Danach nach MP4 umpacken (ffmpeg, verlustfrei)");
        options.Controls.Add(_chkResume);
        options.Controls.Add(_chkRetry);
        options.Controls.Add(_chkFail);
        options.Controls.Add(_chkTrim);
        options.Controls.Add(_chkSegments);
        options.Controls.Add(_chkRemux);
        root.Controls.Add(options, 0, 4);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
        };
        ConfigureButton(_btnStart, "Download starten", 170);
        _btnStart.Height = 34;
        _btnStart.Font = new Font(Font, FontStyle.Bold);
        ConfigureButton(_btnCancel, "Abbrechen", 110);
        _btnCancel.Height = 34;
        _btnCancel.Enabled = false;
        ConfigureButton(_btnOpenFolder, "Ordner oeffnen", 140);
        _btnOpenFolder.Height = 34;
        actions.Controls.Add(_btnStart);
        actions.Controls.Add(_btnCancel);
        actions.Controls.Add(_btnOpenFolder);
        root.Controls.Add(actions, 0, 5);

        _progress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _progress.Height = 22;
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Margin = new Padding(3, 0, 3, 4);
        root.Controls.Add(_progress, 0, 6);

        _lblStatus.AutoSize = true;
        _lblStatus.Margin = new Padding(3, 0, 3, 8);
        root.Controls.Add(_lblStatus, 0, 7);

        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.WordWrap = false;
        _txtLog.Font = monospace;
        _txtLog.BackColor = SystemColors.Window;
        _txtLog.Dock = DockStyle.Fill;
        root.Controls.Add(_txtLog, 0, 8);

        Controls.Add(root);
        AcceptButton = _btnStart;
    }

    private static void ConfigureButton(Button button, string text, int width)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 28;
        button.AutoEllipsis = true;
    }

    private static void ConfigureCheckBox(CheckBox box, string text)
    {
        box.Text = text;
        box.AutoSize = true;
        box.Margin = new Padding(3, 4, 16, 4);
    }

    private void WireEvents()
    {
        _txtCurl.TextChanged += (_, _) => UpdateParseInfo();
        _btnPaste.Click += (_, _) => PasteFromClipboard();
        _btnClear.Click += (_, _) =>
        {
            _txtCurl.Clear();
            _txtCurl.Focus();
        };
        _btnSuggest.Click += (_, _) =>
        {
            string suggestion = CurlCommandParser.SuggestFileName(CurlCommandParser.Parse(_txtCurl.Text).Url);
            _txtFile.Text = suggestion;
            _lastSuggestion = suggestion;
        };
        _btnBrowse.Click += (_, _) => BrowseForFolder();
        _btnStart.Click += async (_, _) => await StartDownloadAsync();
        _btnCancel.Click += (_, _) =>
        {
            _cancellation?.Cancel();
            SetStatus("Wird abgebrochen…");
        };
        _btnOpenFolder.Click += (_, _) => OpenTargetFolder();
        FormClosing += MainForm_FormClosing;
    }

    // ------------------------------------------------------------------ Aktionen

    private void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                _txtCurl.Text = Clipboard.GetText();
                _txtCurl.SelectionStart = _txtCurl.TextLength;
            }
            else
            {
                SetStatus("Die Zwischenablage enthaelt keinen Text.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Zwischenablage nicht lesbar: " + ex.Message);
        }
    }

    private void UpdateParseInfo()
    {
        if (_txtCurl.TextLength == 0)
        {
            _lblParseInfo.Text = string.Empty;
            _lastParsedUrl = string.Empty;
            return;
        }

        var parsed = CurlCommandParser.Parse(_txtCurl.Text);

        _lblParseInfo.Text = parsed.Url.Length == 0
            ? "Keine URL erkannt."
            : $"{parsed.HeaderCount} Header · {Host(parsed.Url)}"
              + (parsed.RemovedRange is null ? string.Empty : "  ·  Range-Angabe entfernt")
              + (parsed.RemovedEncoding is null ? string.Empty : "  ·  Accept-Encoding entfernt");
        _lblParseInfo.ForeColor = parsed.Url.Length == 0 ? Color.Firebrick : SystemColors.GrayText;

        if (parsed.Url == _lastParsedUrl)
        {
            return;
        }

        _lastParsedUrl = parsed.Url;

        // Vorschlag nur setzen, solange das Feld leer ist oder noch den alten Vorschlag enthaelt.
        if (_txtFile.TextLength == 0 || _txtFile.Text == _lastSuggestion)
        {
            string suggestion = CurlCommandParser.SuggestFileName(parsed.Url);
            _txtFile.Text = suggestion;
            _lastSuggestion = suggestion;
        }
    }

    private static string Host(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Zielordner fuer den Download",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        string current = _txtFolder.Text.Trim();
        if (Directory.Exists(current))
        {
            dialog.SelectedPath = current;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _txtFolder.Text = dialog.SelectedPath;
        }
    }

    private void OpenTargetFolder()
    {
        string folder = _txtFolder.Text.Trim();
        try
        {
            if (_lastTargetPath.Length > 0 && File.Exists(_lastTargetPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastTargetPath}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            if (Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            else
            {
                SetStatus("Der Ordner existiert nicht.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Ordner konnte nicht geoeffnet werden: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ Download

    private async Task StartDownloadAsync()
    {
        if (_runner.IsRunning)
        {
            return;
        }

        var parsed = CurlCommandParser.Parse(_txtCurl.Text);
        if (parsed.Url.Length == 0)
        {
            ShowError("In dem eingefuegten Befehl wurde keine URL gefunden.\n\n"
                      + "Erwartet wird die Ausgabe von: Firefox → Netzwerkanalyse → Rechtsklick "
                      + "→ Kopieren → Als cURL kopieren.");
            _txtCurl.Focus();
            return;
        }

        string folder = _txtFolder.Text.Trim();
        if (folder.Length == 0)
        {
            ShowError("Bitte einen Zielordner waehlen.");
            return;
        }

        if (!Directory.Exists(folder))
        {
            var answer = MessageBox.Show(
                this,
                $"Der Ordner existiert nicht:\n{folder}\n\nSoll er angelegt werden?",
                "Ordner anlegen",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                ShowError("Ordner konnte nicht angelegt werden:\n" + ex.Message);
                return;
            }
        }

        string fileName = PathHelper.SanitizeFileName(_txtFile.Text);
        if (fileName.Length == 0)
        {
            ShowError("Bitte einen Dateinamen angeben.");
            _txtFile.Focus();
            return;
        }

        _txtFile.Text = fileName;

        string targetPath;
        try
        {
            targetPath = Path.GetFullPath(Path.Combine(folder, fileName));
        }
        catch (Exception ex)
        {
            ShowError("Ungueltiger Zielpfad:\n" + ex.Message);
            return;
        }

        bool resume = _chkResume.Checked;

        if (File.Exists(targetPath))
        {
            var choice = AskAboutExistingFile(targetPath);
            if (choice == ExistingFileChoice.Cancel)
            {
                return;
            }

            if (choice == ExistingFileChoice.Overwrite)
            {
                resume = false;
                try
                {
                    File.Delete(targetPath);
                }
                catch (Exception ex)
                {
                    ShowError("Vorhandene Datei konnte nicht geloescht werden:\n" + ex.Message);
                    return;
                }
            }
            else
            {
                resume = true;
            }
        }

        bool segmented = _chkSegments.Checked;

        var arguments = new List<string>(parsed.Arguments) { "-L" };
        if (_chkFail.Checked)
        {
            arguments.Add("--fail");
        }

        // Im Abschnittsbetrieb uebernimmt die Ueberlappung das Fortsetzen - curls eigenes
        // -C - wuerde dort mit dem selbst gesetzten Range-Header kollidieren.
        if (resume && !segmented)
        {
            arguments.Add("-C");
            arguments.Add("-");
        }

        if (_chkRetry.Checked)
        {
            arguments.AddRange(["--retry", "5", "--retry-delay", "2", "--retry-all-errors"]);
        }

        _lastTargetPath = targetPath;
        _separateAudioPath = null;
        _playlistSeconds = 0;
        _txtLog.Clear();
        foreach (string warning in parsed.Warnings)
        {
            AppendLog("Hinweis: " + warning);
        }

        AppendLog("curl " + RenderForDisplay(arguments) + " -o \"" + targetPath + "\" " + parsed.Url);
        if (segmented)
        {
            AppendLog("Abschnittsbetrieb: weitere Anfragen mit Range-Header, bis nichts Neues mehr kommt.");
        }

        AppendLog(new string('-', 72));

        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = 0;
        _startedAt = DateTime.Now;
        SetBusy(true);
        SetStatus("Verbindung wird aufgebaut…");

        _cancellation = new CancellationTokenSource();
        var lineSink = new Progress<string>(HandleCurlLine);

        CurlResult result;
        string finalPath = targetPath;
        try
        {
            if (segmented)
            {
                result = await RunSegmentedAsync(arguments, parsed.Url, targetPath, resume, lineSink);
            }
            else
            {
                var single = new List<string>(arguments) { "-o", targetPath, parsed.Url };
                result = await _runner.RunAsync(
                    single,
                    line => ((IProgress<string>)lineSink).Report(line),
                    _cancellation.Token);
            }

            if (result is { ExitCode: 0, Canceled: false })
            {
                // Hinter der URL kann statt der Datei eine Playlist stecken - dann ist das eben
                // Geladene nur die Wegbeschreibung und das Video liegt anderswo.
                var viaPlaylist = await TryPlaylistAsync(arguments, parsed.Url, targetPath, lineSink);
                if (viaPlaylist is not null)
                {
                    result = viaPlaylist;
                }
                else if (_chkTrim.Checked)
                {
                    await TrimJunkPrefixAsync(targetPath);
                }
            }

            if (result is { ExitCode: 0, Canceled: false })
            {
                finalPath = await MaybeRemuxAsync(targetPath);
                _lastTargetPath = finalPath;
            }
        }
        catch (OperationCanceledException)
        {
            result = new CurlResult { Canceled = true, Description = "Abgebrochen." };
        }
        catch (Exception ex)
        {
            AppendLog("FEHLER: " + ex.Message);
            SetStatus("Der Download ist gescheitert.");
            ShowError("Der Download ist gescheitert:\n" + ex.Message);
            return;
        }
        finally
        {
            _overallPrefix = string.Empty;
            _cancellation?.Dispose();
            _cancellation = null;
            SetBusy(false);
        }

        ReportResult(result, finalPath);
    }

    /// <summary>
    /// Prueft, ob die geladene Datei eine HLS-Playlist ist, und holt in dem Fall alles, was
    /// darin steht. Liefert null, wenn es keine Playlist war - dann bleibt es beim normalen
    /// Download.
    /// </summary>
    private async Task<CurlResult?> TryPlaylistAsync(
        IReadOnlyList<string> baseArguments,
        string url,
        string targetPath,
        Progress<string> lineSink)
    {
        // Eine Playlist ist ein Textschnipsel. Alles Groessere ist das Video selbst und wird
        // gar nicht erst darauf untersucht.
        const long maxPlaylistBytes = 8 * 1024 * 1024;

        var info = new FileInfo(targetPath);
        if (!info.Exists || info.Length == 0 || info.Length > maxPlaylistBytes)
        {
            return null;
        }

        if (!M3u8Playlist.LooksLikePlaylist(ReadHead(targetPath, 16)))
        {
            return null;
        }

        string playlistUrl = url;
        var playlist = M3u8Playlist.Parse(M3u8Playlist.ReadText(targetPath), playlistUrl);

        // Die getrennte Tonspur steht in der Master-Playlist und muss ueber den Sprung zur
        // Bild-Variante hinweg gemerkt werden.
        string? audioUrl = playlist.AudioUrl;
        string? audioName = playlist.AudioName;

        // Master-Playlist: erst die beste Variante nachladen, die enthaelt die Segmente.
        for (int hop = 0; playlist.VariantUrl is not null && hop < 3; hop++)
        {
            AppendLog("Master-Playlist erkannt, beste Variante: " + playlist.VariantUrl);
            playlistUrl = playlist.VariantUrl;

            var fetched = await FetchPlaylistAsync(baseArguments, playlistUrl, targetPath, lineSink);
            if (fetched.Canceled || fetched.ExitCode != 0)
            {
                return fetched;
            }

            playlist = M3u8Playlist.Parse(M3u8Playlist.ReadText(targetPath), playlistUrl);
            audioUrl ??= playlist.AudioUrl;
            audioName ??= playlist.AudioName;
        }

        if (Reject(playlist) is { } rejected)
        {
            return rejected;
        }

        var runs = M3u8Playlist.BuildRuns(playlist.Segments);
        _playlistSeconds = playlist.TotalDuration;

        AppendLog(new string('-', 72));
        AppendLog($"Playlist erkannt: {playlist.Segments.Count} Segmente, {runs.Count} Abruf(e), "
                  + $"{TimeSpan.FromSeconds(playlist.TotalDuration):h\\:mm\\:ss} Spielzeit.");
        if (PlaylistDownloader.CanBatch(runs))
        {
            AppendLog($"Gebuendelt: je {PlaylistDownloader.BatchSize} Stuecke pro Aufruf, "
                      + $"{PlaylistDownloader.ParallelMax} gleichzeitig.");
        }

        if (audioUrl is not null)
        {
            AppendLog($"Ton liegt getrennt vor ({audioName ?? "ohne Namen"}) und wird hinterher "
                      + "wieder mit dem Bild zusammengefuegt.");
        }

        var outcome = await RunPlaylistAsync(
            baseArguments, runs, targetPath, audioUrl is null ? string.Empty : "Bild ", lineSink);

        if (outcome is not PlaylistDownloadResult { Failed: false, Last: { ExitCode: 0, Canceled: false } })
        {
            return Unwrap(outcome);
        }

        if (audioUrl is null)
        {
            return outcome.Last;
        }

        // ------------------------------------------------------------------ Tonspur
        string audioPlaylistPath = targetPath + ".ton.m3u8";
        try
        {
            var fetched = await FetchPlaylistAsync(baseArguments, audioUrl, audioPlaylistPath, lineSink);
            if (fetched.Canceled || fetched.ExitCode != 0)
            {
                return fetched;
            }

            var audio = M3u8Playlist.Parse(M3u8Playlist.ReadText(audioPlaylistPath), audioUrl);
            if (Reject(audio) is { } audioRejected)
            {
                return audioRejected;
            }

            var audioRuns = M3u8Playlist.BuildRuns(audio.Segments);
            string audioPath = Path.ChangeExtension(targetPath, null) + ".ton.ts";

            AppendLog(new string('-', 72));
            AppendLog($"Tonspur: {audio.Segments.Count} Segmente, {audioRuns.Count} Abruf(e), "
                      + $"{TimeSpan.FromSeconds(audio.TotalDuration):h\\:mm\\:ss} Spielzeit.");

            var audioOutcome = await RunPlaylistAsync(
                baseArguments, audioRuns, audioPath, "Ton ", lineSink);

            if (audioOutcome is not PlaylistDownloadResult
                { Failed: false, Last: { ExitCode: 0, Canceled: false } })
            {
                return Unwrap(audioOutcome);
            }

            _separateAudioPath = audioPath;
            return audioOutcome.Last;
        }
        finally
        {
            TryDelete(audioPlaylistPath);
        }
    }

    /// <summary>Laedt eine Playlist-Datei - ohne Fortschrittsanzeige, das sind ein paar Kilobyte.</summary>
    private async Task<CurlResult> FetchPlaylistAsync(
        IReadOnlyList<string> baseArguments,
        string url,
        string path,
        Progress<string> lineSink)
    {
        var arguments = new List<string>(baseArguments) { "--no-progress-meter", "-o", path, url };
        return await _runner.RunAsync(
            arguments,
            line => ((IProgress<string>)lineSink).Report(line),
            _cancellation!.Token);
    }

    /// <summary>Prueft, was CurlGrabber an einer Playlist nicht verarbeiten kann.</summary>
    private CurlResult? Reject(M3u8Playlist playlist)
    {
        foreach (string warning in playlist.Warnings)
        {
            AppendLog("Hinweis: " + warning);
        }

        if (playlist.Encryption is not null)
        {
            AppendLog($"ABBRUCH: Die Segmente sind verschluesselt ({playlist.Encryption}). "
                      + "CurlGrabber kann sie nicht entschluesseln.");
            return new CurlResult
            {
                ExitCode = -1,
                Description = $"Die Playlist ist verschluesselt ({playlist.Encryption}).",
            };
        }

        if (playlist.Segments.Count == 0)
        {
            AppendLog("ABBRUCH: In der Playlist standen keine Segmente.");
            return new CurlResult { ExitCode = -1, Description = "Leere Playlist." };
        }

        return null;
    }

    private async Task<PlaylistDownloadResult> RunPlaylistAsync(
        IReadOnlyList<string> baseArguments,
        IReadOnlyList<DownloadRun> runs,
        string targetPath,
        string label,
        Progress<string> lineSink)
    {
        var downloader = new PlaylistDownloader(_runner);
        var progressSink = new Progress<string>(AppendLog);
        var lastLogged = DateTime.MinValue;

        var outcome = await downloader.DownloadAsync(
            baseArguments,
            runs,
            targetPath,
            _chkTrim.Checked,
            line => ((IProgress<string>)lineSink).Report(line),
            (number, count) => _overallPrefix = $"{label}Stueck {number}/{count}  ·  ",
            (number, added, total) =>
            {
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = (int)Math.Min(100, number * 100L / Math.Max(1, runs.Count));
                SetStatus($"{label}Stueck {number}/{runs.Count}  ·  "
                          + $"{PathHelper.FormatBytes(total)} zusammengesetzt");

                // Bei tausenden Segmenten wuerde jede Zeile das Log zumuellen.
                if (number == runs.Count || DateTime.Now - lastLogged > TimeSpan.FromSeconds(5))
                {
                    lastLogged = DateTime.Now;
                    ((IProgress<string>)progressSink).Report(
                        $"{label}Stueck {number}/{runs.Count}: {PathHelper.FormatBytes(added)}  ·  "
                        + $"insgesamt {PathHelper.FormatBytes(total)}");
                }
            },
            _cancellation!.Token);

        _overallPrefix = string.Empty;

        if (!outcome.Failed && outcome.Last is { ExitCode: 0, Canceled: false })
        {
            AppendLog($"{outcome.RunsDone} von {outcome.RunsTotal} Stueck(en) zusammengesetzt.");
        }

        return outcome;
    }

    private CurlResult Unwrap(PlaylistDownloadResult outcome)
    {
        if (!outcome.Failed)
        {
            return outcome.Last;
        }

        AppendLog("ABBRUCH: " + outcome.FailReason);
        return new CurlResult
        {
            ExitCode = outcome.Last.ExitCode == 0 ? -1 : outcome.Last.ExitCode,
            Description = outcome.FailReason ?? "Die Playlist konnte nicht geladen werden.",
        };
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
            // Aufraeumen ist kein Grund zu scheitern.
        }
    }

    private static byte[] ReadHead(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[(int)Math.Min(count, stream.Length)];
        stream.ReadExactly(buffer, 0, buffer.Length);
        return buffer;
    }

    /// <summary>Laedt die Datei in mehreren Anfragen - siehe <see cref="SegmentedDownloader"/>.</summary>
    private async Task<CurlResult> RunSegmentedAsync(
        IReadOnlyList<string> baseArguments,
        string url,
        string targetPath,
        bool resume,
        Progress<string> lineSink)
    {
        var downloader = new SegmentedDownloader(_runner);
        var chunkSink = new Progress<string>(AppendLog);

        var outcome = await downloader.DownloadAsync(
            baseArguments,
            url,
            targetPath,
            resume,
            line => ((IProgress<string>)lineSink).Report(line),
            (number, added, total) => ((IProgress<string>)chunkSink).Report(
                $"Abschnitt {number}: +{PathHelper.FormatBytes(added)}  ·  "
                + $"insgesamt {PathHelper.FormatBytes(total)}"),
            _cancellation!.Token);

        if (outcome.Stalled)
        {
            AppendLog("ABBRUCH: " + outcome.StallReason);
            return new CurlResult
            {
                ExitCode = outcome.Last.ExitCode == 0 ? -1 : outcome.Last.ExitCode,
                Description = outcome.StallReason ?? "Die Abschnitte passten nicht zusammen.",
            };
        }

        if (outcome.Last.ExitCode == 0 && !outcome.Last.Canceled)
        {
            AppendLog($"{outcome.Requests} Anfrage(n), {PathHelper.FormatBytes(outcome.TotalBytes)} zusammengesetzt.");
        }

        return outcome.Last;
    }

    /// <summary>
    /// Packt den fertigen Transportstrom in einen MP4-Container um und fuegt dabei eine getrennt
    /// geladene Tonspur wieder ein. Liefert den Pfad, unter dem das Ergebnis am Ende liegt.
    ///
    /// Bei getrenntem Ton ist das keine Kuer: ohne diesen Schritt bliebe das Bild stumm. Fehlt
    /// dann ffmpeg, bleiben beide Teile liegen, statt ein halbes Ergebnis zu melden.
    /// </summary>
    private async Task<string> MaybeRemuxAsync(string targetPath)
    {
        string? audioPath = _separateAudioPath;
        bool needed = audioPath is not null;

        if (!needed && (!_chkRemux.Checked || !LooksLikeTransportStream(targetPath)))
        {
            return targetPath;
        }

        string finalPath = Path.ChangeExtension(targetPath, ".mp4");
        if (string.Equals(finalPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            // Die Zieldatei heisst schon .mp4 - dann daneben umpacken und danach ersetzen.
            finalPath = targetPath + ".neu.mp4";
        }

        AppendLog(new string('-', 72));
        SetStatus("Wird nach MP4 umgepackt…");
        _progress.Value = 0;

        var remuxer = new Remuxer();
        var result = await remuxer.RemuxAsync(
            targetPath,
            audioPath,
            finalPath,
            AppendLog,
            seconds =>
            {
                if (_playlistSeconds > 0)
                {
                    _progress.Value = (int)Math.Clamp(seconds * 100 / _playlistSeconds, 0, 100);
                }

                SetStatus($"Wird nach MP4 umgepackt…  ·  {TimeSpan.FromSeconds(seconds):h\\:mm\\:ss}"
                          + (_playlistSeconds > 0
                              ? $" von {TimeSpan.FromSeconds(_playlistSeconds):h\\:mm\\:ss}"
                              : string.Empty));
            },
            _cancellation!.Token);

        if (!result.Succeeded)
        {
            AppendLog(result.ToolMissing
                ? "Nicht umgepackt: " + result.Message
                : "Umpacken fehlgeschlagen: " + result.Message);

            if (needed)
            {
                AppendLog($"Bild und Ton liegen getrennt: {targetPath} und {audioPath}. "
                          + "Mit ffmpeg lassen sie sich jederzeit zusammenfuegen.");
            }

            TryDelete(finalPath);
            return targetPath;
        }

        AppendLog($"Verlustfrei nach MP4 umgepackt: {finalPath}");
        TryDelete(targetPath);
        if (audioPath is not null)
        {
            TryDelete(audioPath);
        }

        // War das Ziel selbst schon eine .mp4, tritt das Ergebnis an ihre Stelle.
        if (finalPath.EndsWith(".neu.mp4", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Move(finalPath, targetPath, overwrite: true);
                return targetPath;
            }
            catch (Exception ex)
            {
                AppendLog("Umbenennen fehlgeschlagen, das Ergebnis bleibt unter dem Zwischennamen: "
                          + ex.Message);
            }
        }

        return finalPath;
    }

    /// <summary>Ein MPEG-Transportstrom hat alle 188 Bytes ein 0x47.</summary>
    private static bool LooksLikeTransportStream(string path)
    {
        try
        {
            byte[] head = ReadHead(path, 188 * 5);
            return head.Length == 188 * 5
                   && head[0] == 0x47 && head[188] == 0x47 && head[376] == 0x47 && head[752] == 0x47;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Schneidet einen vorgetaeuschten Datei-Anfang weg. Gefunden wird er ueber den Beginn der
    /// echten Nutzlast, nicht ueber die Tarnung - siehe <see cref="PayloadTrimmer"/>.
    /// </summary>
    private async Task TrimJunkPrefixAsync(string path)
    {
        PayloadScan scan;
        try
        {
            scan = await Task.Run(() => PayloadTrimmer.Scan(path));
        }
        catch (Exception ex)
        {
            AppendLog("Der Dateianfang konnte nicht geprueft werden: " + ex.Message);
            return;
        }

        if (!scan.HasPrefix)
        {
            return;
        }

        SetStatus("Vorgetaeuschter Datei-Anfang wird entfernt…");
        try
        {
            await Task.Run(() => PayloadTrimmer.RemovePrefix(path, scan.PrefixLength));
            AppendLog($"Vorgetaeuschter Datei-Anfang entfernt: {scan.PrefixLength} Bytes "
                      + $"vor dem {scan.Format}-Datenstrom.");
        }
        catch (Exception ex)
        {
            AppendLog("Der vorgetaeuschte Datei-Anfang konnte nicht entfernt werden: " + ex.Message);
        }
    }

    private void HandleCurlLine(string line)
    {
        if (CurlProgressParser.TryParse(line, out var progress))
        {
            if (progress.Total is null && progress.Received > 0)
            {
                // Ohne Content-Length kennt curl den Prozentwert nicht.
                _progress.Style = ProgressBarStyle.Marquee;
            }
            else
            {
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = progress.Percent;
            }

            SetStatus(_overallPrefix + FormatProgress(progress));
            return;
        }

        if (CurlProgressParser.IsHeaderLine(line))
        {
            return;
        }

        AppendLog(line);
    }

    private static string FormatProgress(CurlProgress progress)
    {
        var sb = new StringBuilder();

        sb.Append(progress.Total is null
            ? "Laeuft"
            : string.Format(CultureInfo.CurrentCulture, "{0} %", progress.Percent));

        sb.Append("  ·  ").Append(PathHelper.FormatBytes(progress.Received));
        if (progress.Total is { } total)
        {
            sb.Append(" / ").Append(PathHelper.FormatBytes(total));
        }
        else
        {
            sb.Append(" (Gesamtgroesse unbekannt)");
        }

        if (progress.Speed > 0)
        {
            sb.Append("  ·  ").Append(PathHelper.FormatBytes(progress.Speed)).Append("/s");
        }

        if (progress.TimeLeft is { } left)
        {
            sb.Append("  ·  Restzeit ").Append(FormatDuration(left));
        }

        return sb.ToString();
    }

    private static string FormatDuration(TimeSpan span)
        => span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private void ReportResult(CurlResult result, string targetPath)
    {
        var elapsed = DateTime.Now - _startedAt;
        long size = 0;
        try
        {
            if (File.Exists(targetPath))
            {
                size = new FileInfo(targetPath).Length;
            }
        }
        catch
        {
            // Groesse ist nur Zusatzinfo.
        }

        AppendLog(new string('-', 72));

        if (result.Canceled)
        {
            AppendLog($"Abgebrochen. Teildatei behalten ({PathHelper.FormatBytes(size)}) - "
                      + "mit aktivierter Option \"fortsetzen\" kann spaeter weitergeladen werden.");
            SetStatus("Abgebrochen.");
            return;
        }

        AppendLog($"curl beendet mit Code {result.ExitCode}: {result.Description}");

        if (result.ExitCode == 0)
        {
            _progress.Value = 100;
            AppendLog($"Datei: {targetPath}");
            AppendLog($"Groesse: {PathHelper.FormatBytes(size)}  ·  Dauer: {elapsed:hh\\:mm\\:ss}");
            SetStatus($"Fertig – {PathHelper.FormatBytes(size)} in {elapsed:hh\\:mm\\:ss}");
        }
        else
        {
            SetStatus("Fehlgeschlagen: " + result.Description);
        }
    }

    private enum ExistingFileChoice
    {
        Overwrite,
        Resume,
        Cancel,
    }

    private ExistingFileChoice AskAboutExistingFile(string targetPath)
    {
        long size = 0;
        try
        {
            size = new FileInfo(targetPath).Length;
        }
        catch
        {
            // Groesse ist nur Zusatzinfo.
        }

        var overwrite = new TaskDialogCommandLinkButton(
            "Neu herunterladen", "Die vorhandene Datei wird geloescht und der Download beginnt von vorne.");
        var resume = new TaskDialogCommandLinkButton(
            "Fortsetzen", $"Ab {PathHelper.FormatBytes(size)} weiterladen (-C -). "
                          + "Funktioniert nur, wenn der Server das unterstuetzt.");
        var cancel = TaskDialogButton.Cancel;

        var page = new TaskDialogPage
        {
            Caption = "CurlGrabber",
            Heading = "Die Zieldatei existiert bereits",
            Text = targetPath,
            Icon = TaskDialogIcon.Warning,
            AllowCancel = true,
            Buttons = { overwrite, resume, cancel },
        };

        var clicked = TaskDialog.ShowDialog(this, page);
        if (clicked == overwrite)
        {
            return ExistingFileChoice.Overwrite;
        }

        return clicked == resume ? ExistingFileChoice.Resume : ExistingFileChoice.Cancel;
    }

    /// <summary>Nur fuer die Anzeige im Log - ausgefuehrt wird stets die Argumentliste.</summary>
    private static string RenderForDisplay(IEnumerable<string> arguments)
        => string.Join(' ', arguments.Select(a =>
            a.Length == 0 || a.Any(char.IsWhiteSpace) ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));

    // ------------------------------------------------------------------ UI-Zustand

    private void SetBusy(bool busy)
    {
        _btnStart.Enabled = !busy;
        _btnCancel.Enabled = busy;
        _btnPaste.Enabled = !busy;
        _btnClear.Enabled = !busy;
        _btnSuggest.Enabled = !busy;
        _btnBrowse.Enabled = !busy;
        _txtCurl.ReadOnly = busy;
        _txtFile.ReadOnly = busy;
        _txtFolder.ReadOnly = busy;
        _chkResume.Enabled = !busy;
        _chkRetry.Enabled = !busy;
        _chkFail.Enabled = !busy;
        _chkTrim.Enabled = !busy;
        _chkSegments.Enabled = !busy;
        _chkRemux.Enabled = !busy;
        UseWaitCursor = false;
    }

    private void SetStatus(string text) => _lblStatus.Text = text;

    private void AppendLog(string line)
    {
        if (_txtLog.TextLength > 200_000)
        {
            _txtLog.Text = _txtLog.Text[^100_000..];
        }

        _txtLog.AppendText(line + Environment.NewLine);
    }

    private void ShowError(string message)
        => MessageBox.Show(this, message, "CurlGrabber", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_runner.IsRunning)
        {
            var answer = MessageBox.Show(
                this,
                "Es laeuft noch ein Download. Wirklich beenden?",
                "CurlGrabber",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _cancellation?.Cancel();
        }

        _settings.LastFolder = _txtFolder.Text.Trim();
        _settings.Resume = _chkResume.Checked;
        _settings.Retry = _chkRetry.Checked;
        _settings.FailOnHttpError = _chkFail.Checked;
        _settings.TrimJunkPrefix = _chkTrim.Checked;
        _settings.Segmented = _chkSegments.Checked;
        _settings.RemuxToMp4 = _chkRemux.Checked;
        _settings.Maximized = WindowState == FormWindowState.Maximized;
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }

        _settings.Save();
    }
}
