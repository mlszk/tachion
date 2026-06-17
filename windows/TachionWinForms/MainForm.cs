using System.Diagnostics;
using System.Reflection;
using Tachion.Core;
using Tachion.Windows;

namespace Tachion;

public sealed class MainForm : Form
{
    private readonly NotifyIcon _tray;
    private readonly TextBox _folderBox = new();
    private readonly TextBox _urlBox = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _tokenBox = new();
    private readonly TextBox _logBox = new();
    private readonly Label _statusLabel = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly CheckBox _startupBox = new();
    private readonly CheckBox _autoSyncBox = new();

    private TachionSettings _settings;
    private SyncClient? _client;
    private bool _allowClose;
    private bool _loadingStartup;
    private bool _autoStartAttempted;

    public MainForm()
    {
        Text = "tachion " + AppVersion();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 460);
        Size = new Size(940, 560);
        Icon = LoadEmbeddedIcon("tachion.ico") ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        _settings = TachionSettings.Load();
        BuildUi();
        LoadSettingsToUi();

        _tray = new NotifyIcon
        {
            Text = "tachion " + AppVersion(),
            Icon = LoadStatusIcon(false),
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        Log("Ready. Config: " + TachionSettings.ConfigPath);
    }

    private static string AppVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
            version = Application.ProductVersion;

        version = version.Split('+')[0];
        return "v" + version;
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = "tachion " + AppVersion(),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10)
        };
        root.Controls.Add(title);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 4
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.Controls.Add(grid);

        AddRow(grid, 0, "Local folder", _folderBox, MakeButton("Choose...", ChooseFolder_Click));
        AddRow(grid, 1, "Server URL", _urlBox, null);
        AddRow(grid, 2, "Device name", _nameBox, null);
        _tokenBox.UseSystemPasswordChar = true;
        AddRow(grid, 3, "Token", _tokenBox, null);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 12, 0, 8),
            Padding = new Padding(0)
        };
        root.Controls.Add(buttons);

        ConfigureActionButton(_startButton, "Start sync", Start_Click, 96);
        buttons.Controls.Add(_startButton);

        ConfigureActionButton(_stopButton, "Stop sync", Stop_Click, 92);
        _stopButton.Enabled = false;
        buttons.Controls.Add(_stopButton);

        var saveButton = MakeButton("Save settings", Save_Click, 110);
        buttons.Controls.Add(saveButton);

        var openFolderButton = MakeButton("Open folder", OpenFolder_Click, 96);
        buttons.Controls.Add(openFolderButton);

        _startupBox.Text = "Run at Windows startup";
        _startupBox.AutoSize = false;
        _startupBox.Height = 28;
        _startupBox.Width = 180;
        _startupBox.TextAlign = ContentAlignment.MiddleLeft;
        _startupBox.CheckedChanged += StartupBox_Changed;
        _startupBox.Margin = new Padding(8, 0, 0, 0);
        buttons.Controls.Add(_startupBox);

        _autoSyncBox.Text = "Start sync when opened";
        _autoSyncBox.AutoSize = false;
        _autoSyncBox.Height = 28;
        _autoSyncBox.Width = 230;
        _autoSyncBox.TextAlign = ContentAlignment.MiddleLeft;
        _autoSyncBox.Margin = new Padding(8, 0, 0, 0);
        buttons.Controls.Add(_autoSyncBox);

        var statusPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        statusPanel.Controls.Add(new Label { Text = "Status:", AutoSize = true, Padding = new Padding(0, 3, 4, 0) });
        _statusLabel.Text = "Stopped";
        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font(Font, FontStyle.Bold);
        _statusLabel.Padding = new Padding(0, 3, 0, 0);
        statusPanel.Controls.Add(_statusLabel);
        root.Controls.Add(statusPanel);

        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Font = new Font("Consolas", 9f);
        root.Controls.Add(_logBox);
    }

    private static Button MakeButton(string text, EventHandler handler, int width = 0)
    {
        var button = new Button();
        ConfigureActionButton(button, text, handler, width);
        return button;
    }

    private static void ConfigureActionButton(Button button, string text, EventHandler handler, int width = 0)
    {
        button.Text = text;
        button.AutoSize = false;
        button.Height = 28;
        button.Width = width > 0 ? width : 96;
        button.Margin = new Padding(0, 0, 8, 0);
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.Click += handler;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, TextBox box, Control? extra)
    {
        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0, 3, 8, 3);

        var labelControl = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 7, 8, 0)
        };
        grid.Controls.Add(labelControl, 0, row);
        grid.Controls.Add(box, 1, row);
        if (extra != null)
            grid.Controls.Add(extra, 2, row);
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Start sync", null, (_, _) => Start_Click(this, EventArgs.Empty));
        menu.Items.Add("Stop sync", null, (_, _) => Stop_Click(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        return menu;
    }

    private void LoadSettingsToUi()
    {
        _folderBox.Text = _settings.SyncDir;
        _urlBox.Text = _settings.SyncUrl;
        _nameBox.Text = _settings.SyncName;
        _tokenBox.Text = _settings.SyncToken;
        _autoSyncBox.Checked = _settings.StartSyncOnLaunch;
        _loadingStartup = true;
        _startupBox.Checked = StartupService.IsEnabled();
        _loadingStartup = false;
    }

    private void SaveUiToSettings()
    {
        _settings.SyncDir = _folderBox.Text.Trim();
        _settings.SyncUrl = _urlBox.Text.Trim();
        _settings.SyncName = _nameBox.Text.Trim();
        _settings.SyncToken = _tokenBox.Text;
        _settings.StartSyncOnLaunch = _autoSyncBox.Checked;
        _settings.Save();
    }

    private Icon? LoadEmbeddedIcon(string fileName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return null;
            return new Icon(stream);
        }
        catch
        {
            return null;
        }
    }

    private Icon LoadStatusIcon(bool working)
    {
        var icon = LoadEmbeddedIcon(working ? "tray-green-16.ico" : "tray-red-16.ico");
        if (icon != null) return icon;

        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var fill = working ? Color.FromArgb(96, 176, 116) : Color.FromArgb(176, 104, 112);
            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, 2, 2, 12, 12);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void SetTrayWorking(bool working)
    {
        var oldIcon = _tray.Icon;
        _tray.Icon = LoadStatusIcon(working);
        oldIcon?.Dispose();
    }

    private void ChooseFolder_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose tachion sync folder",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folderBox.Text) ? _folderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _folderBox.Text = dialog.SelectedPath;
    }

    private void Save_Click(object? sender, EventArgs e)
    {
        SaveUiToSettings();
        Log("Settings saved.");
    }

    private void OpenFolder_Click(object? sender, EventArgs e)
    {
        var folder = _folderBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, "Folder does not exist yet.", "tachion", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void Start_Click(object? sender, EventArgs e)
    {
        try
        {
            SaveUiToSettings();
            _client?.Dispose();
            _client = new SyncClient(_settings, Log);
            _client.Start();
            _startButton.Enabled = false;
            _stopButton.Enabled = true;
            _statusLabel.Text = "Running";
            SetTrayWorking(true);
            Log("Sync started.");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Error";
            SetTrayWorking(false);
            Log("Start failed: " + ex.Message);
        }
    }

    private void Stop_Click(object? sender, EventArgs e)
    {
        _client?.Stop();
        _startButton.Enabled = true;
        _stopButton.Enabled = false;
        _statusLabel.Text = "Stopped";
        SetTrayWorking(false);
    }

    private void StartupBox_Changed(object? sender, EventArgs e)
    {
        if (_loadingStartup) return;
        try
        {
            StartupService.SetEnabled(_startupBox.Checked);
            Log(_startupBox.Checked ? "Startup enabled." : "Startup disabled.");
        }
        catch (Exception ex)
        {
            Log("Startup change failed: " + ex.Message);
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void Quit()
    {
        _allowClose = true;
        _client?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_autoStartAttempted || !_settings.StartSyncOnLaunch)
            return;

        _autoStartAttempted = true;
        Log("Auto-start sync enabled.");
        Start_Click(this, EventArgs.Empty);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(1000, "tachion", "Still running in the tray.", ToolTipIcon.Info);
            return;
        }
        base.OnFormClosing(e);
    }

    private void Log(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Log(text)));
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        _logBox.AppendText(line + Environment.NewLine);

        if (text.StartsWith("Connected", StringComparison.OrdinalIgnoreCase))
        {
            _statusLabel.Text = "Connected";
            SetTrayWorking(true);
        }
        else if (text.StartsWith("Connection error", StringComparison.OrdinalIgnoreCase))
        {
            _statusLabel.Text = "Reconnecting";
            SetTrayWorking(false);
        }
        else if (text.StartsWith("Stopped", StringComparison.OrdinalIgnoreCase))
        {
            _statusLabel.Text = "Stopped";
            SetTrayWorking(false);
        }
    }
}
