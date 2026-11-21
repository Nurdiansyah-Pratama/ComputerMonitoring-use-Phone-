using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PhoneBox.Agent;

/// <summary>Desktop GUI window + system tray icon: status, editor window, Wi-Fi/USB connect, logs, autostart, quit.</summary>
public sealed class Tray : IHostedService
{
    private readonly string SettingsUrl;
    private readonly int _port;
    private readonly bool _startMinimized;

    private readonly IHostApplicationLifetime _life;
    private readonly AdbReverse _adb;
    private Thread? _thread;
    private ApplicationContext? _ctx;
    private Control? _invoker;
    private NotifyIcon? _icon;
    private MainWindow? _window;
    private EventWaitHandle? _showEvent;
    private volatile bool _stopping;

    public Tray(IHostApplicationLifetime life, AdbReverse adb, AgentEndpoint endpoint)
    {
        SettingsUrl = endpoint.SettingsUrl;
        _port = endpoint.Port;
        _startMinimized = Environment.GetCommandLineArgs().Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        _life = life;
        _adb = adb;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _stopping = true;
        try { _showEvent?.Set(); } catch { }
        try
        {
            _invoker?.BeginInvoke(() =>
            {
                if (_window is not null) { _window.AllowClose = true; _window.Close(); }
                if (_icon is not null) _icon.Visible = false;
                _ctx?.ExitThread();
            });
        }
        catch { }
        return Task.CompletedTask;
    }

    private void ShowMainWindow()
    {
        if (_window is null || _window.IsDisposed) return;
        _window.BringToFrontAndShow();
    }

    private void Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Invisible control used to marshal calls onto this UI thread (its handle is created here).
        var host = new Control();
        _ = host.Handle;
        _invoker = host;
        _ctx = new ApplicationContext();

        using var iconStream = AppPaths.Resource("phonebox.ico");
        var appIcon = iconStream is null ? SystemIcons.Application : new Icon(iconStream, 32, 32);
        _window = new MainWindow(SettingsUrl, _adb, appIcon);

        // Named event so launching PhoneBox a second time pops this GUI window straight to the front.
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"PhoneBox_ShowWindow_{_port}");
            var watcher = new Thread(() =>
            {
                while (!_stopping && _showEvent is not null)
                {
                    if (_showEvent.WaitOne() && !_stopping)
                    {
                        try { host.BeginInvoke(ShowMainWindow); } catch { }
                    }
                }
            }) { IsBackground = true, Name = "show-window-watcher" };
            watcher.Start();
        }
        catch { }

        var status = new ToolStripMenuItem { Enabled = false };
        var autostart = new ToolStripMenuItem("Start with Windows");
        autostart.Click += (_, _) => { ToggleAutostart(); autostart.Checked = File.Exists(AppPaths.StartupShortcut); };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"PhoneBox {AppPaths.Version}") { Enabled = false });
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open PhoneBox", null, (_, _) => ShowMainWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Open in Browser…", null, (_, _) => OpenUrl(SettingsUrl)));
        // cancelled while Windows is ending the session (no adb is started then, for at most a minute): nothing to show
        menu.Items.Add("Open panel on phone", null, async (_, _) => { try { await _adb.OpenPanelAsync(); } catch (OperationCanceledException) { } });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Edit config.json", null, (_, _) => Open("notepad.exe", AppPaths.ConfigPath));
        menu.Items.Add("Open log", null, (_, _) => Open("notepad.exe", AppPaths.LogPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _life.StopApplication());
        menu.Opening += (_, _) =>
        {
            status.Text = _adb.Connected ? $"Phone connected ({_adb.Devices})" : "No phone connected";
            autostart.Checked = File.Exists(AppPaths.StartupShortcut);
        };

        _icon = new NotifyIcon
        {
            Icon = appIcon,
            Text = "PhoneBox",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowMainWindow();
        _adb.StateChanged += () =>
        {
            try { host.BeginInvoke(() => _icon.Text = _adb.Connected ? "PhoneBox — phone connected" : "PhoneBox — waiting for phone"); } catch { }
        };

        if (!_startMinimized) _window.Show();

        Application.Run(_ctx);
        _showEvent?.Dispose();
        _window?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static void Open(string app, string path)
    {
        try { Process.Start(new ProcessStartInfo(app, $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Adds/removes a Startup-folder shortcut (only when the user clicks the menu item).</summary>
    private static void ToggleAutostart()
    {
        try
        {
            if (File.Exists(AppPaths.StartupShortcut)) { File.Delete(AppPaths.StartupShortcut); return; }
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic lnk = shell.CreateShortcut(AppPaths.StartupShortcut);
            lnk.TargetPath = Environment.ProcessPath;
            lnk.Arguments = "--minimized";
            lnk.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            lnk.Description = "PhoneBox agent";
            lnk.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not change the startup shortcut: " + ex.Message, "PhoneBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
