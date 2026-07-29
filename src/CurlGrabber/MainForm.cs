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
        options.Controls.Add(_chkResume);
        options.Controls.Add(_chkRetry);
        options.Controls.Add(_chkFail);
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
            : $"{parsed.HeaderCount} Header · {Host(parsed.Url)}";
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

        var arguments = new List<string>(parsed.Arguments) { "-L" };
        if (_chkFail.Checked)
        {
            arguments.Add("--fail");
        }

        if (resume)
        {
            arguments.Add("-C");
            arguments.Add("-");
        }

        if (_chkRetry.Checked)
        {
            arguments.AddRange(["--retry", "5", "--retry-delay", "2", "--retry-all-errors"]);
        }

        arguments.Add("-o");
        arguments.Add(targetPath);
        arguments.Add(parsed.Url);

        _lastTargetPath = targetPath;
        _txtLog.Clear();
        foreach (string warning in parsed.Warnings)
        {
            AppendLog("Hinweis: " + warning);
        }

        AppendLog("curl " + RenderForDisplay(arguments));
        AppendLog(new string('-', 72));

        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = 0;
        _startedAt = DateTime.Now;
        SetBusy(true);
        SetStatus("Verbindung wird aufgebaut…");

        _cancellation = new CancellationTokenSource();
        var lineSink = new Progress<string>(HandleCurlLine);

        CurlResult result;
        try
        {
            result = await _runner.RunAsync(
                arguments,
                line => ((IProgress<string>)lineSink).Report(line),
                _cancellation.Token);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            AppendLog("FEHLER: " + ex.Message);
            SetStatus("curl konnte nicht gestartet werden.");
            ShowError("curl konnte nicht gestartet werden:\n" + ex.Message);
            return;
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
        }

        SetBusy(false);
        ReportResult(result, targetPath);
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

            SetStatus(FormatProgress(progress));
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
        _settings.Maximized = WindowState == FormWindowState.Maximized;
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }

        _settings.Save();
    }
}
