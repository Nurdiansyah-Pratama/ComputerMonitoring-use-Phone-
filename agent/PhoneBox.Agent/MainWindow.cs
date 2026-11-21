using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using QRCoder;

namespace PhoneBox.Agent;

/// <summary>
/// Native Windows desktop GUI for PhoneBox: embeds the full visual layout & appearance editor (WebView2)
/// together with a live phone connection bar and one-click Wi-Fi QR / USB connect controls.
/// Closing the window minimizes quietly to the system tray so the phone stays connected.
/// </summary>
public sealed class MainWindow : Form
{
    private readonly string _settingsUrl;
    private readonly AdbReverse _adb;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Panel _topBar;
    private readonly Label _statusDot;
    private readonly Label _statusLabel;
    private readonly WebView2 _web;
    private readonly Label _loadingLabel;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private bool _webReady;
    private string _currentPath = "/";

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    public MainWindow(string settingsUrl, AdbReverse adb, Icon? appIcon)
    {
        _settingsUrl = settingsUrl.TrimEnd('/');
        _adb = adb;

        Text = $"PhoneBox {AppPaths.Version} — Desk Panel Studio";
        if (appIcon is not null) Icon = appIcon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1240, 800);
        MinimumSize = new Size(920, 600);
        BackColor = Color.FromArgb(11, 15, 23);
        ForeColor = Color.FromArgb(238, 242, 246);

        // Top toolbar
        _topBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 52,
            BackColor = Color.FromArgb(15, 20, 30),
            Padding = new Padding(14, 8, 14, 8),
        };
        _topBar.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(35, 45, 62), 1);
            e.Graphics.DrawLine(pen, 0, _topBar.Height - 1, _topBar.Width, _topBar.Height - 1);
        };

        var leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 6, 0, 0),
        };
        var brand = new Label
        {
            Text = "PhoneBox",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 11.5f, FontStyle.Bold),
            ForeColor = Color.White,
            Margin = new Padding(0, 0, 14, 0),
        };
        _statusDot = new Label
        {
            Text = "●",
            AutoSize = true,
            Font = new Font("Segoe UI", 10f),
            ForeColor = Color.FromArgb(245, 158, 11),
            Margin = new Padding(0, 2, 4, 0),
        };
        _statusLabel = new Label
        {
            Text = "Memeriksa koneksi HP...",
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(180, 192, 208),
            Margin = new Padding(0, 2, 0, 0),
        };
        leftFlow.Controls.Add(brand);
        leftFlow.Controls.Add(_statusDot);
        leftFlow.Controls.Add(_statusLabel);

        var rightFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 2, 0, 0),
        };

        var btnEditor = MakeNavButton("Editor Tampilan", Color.FromArgb(32, 44, 64), (_, _) => NavigateTo("/"));
        var btnPreview = MakeNavButton("Live Preview", Color.FromArgb(32, 44, 64), (_, _) => NavigateTo("/panel/"));
        var btnWifi = MakeNavButton("Hubungkan Wi-Fi (QR)", Color.FromArgb(16, 124, 110), (_, _) => ShowWifiPairingDialog());
        var btnUsb = MakeNavButton("Buka di HP (USB)", Color.FromArgb(37, 99, 235), async (_, _) => await TriggerUsbConnectAsync());
        var btnReload = MakeNavButton("Refresh", Color.FromArgb(30, 38, 52), (_, _) =>
        {
            if (_webReady && _web is not null) _web.CoreWebView2?.Reload();
        });

        rightFlow.Controls.Add(btnEditor);
        rightFlow.Controls.Add(btnPreview);
        rightFlow.Controls.Add(btnWifi);
        rightFlow.Controls.Add(btnUsb);
        rightFlow.Controls.Add(btnReload);

        _topBar.Controls.Add(rightFlow);
        _topBar.Controls.Add(leftFlow);

        // Main content container (WebView2 + fallback/loading label)
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(11, 15, 23) };
        _loadingLabel = new Label
        {
            Text = "Memuat PhoneBox Editor...",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11f),
            ForeColor = Color.FromArgb(148, 163, 184),
        };
        _web = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(11, 15, 23),
            Visible = false,
        };
        content.Controls.Add(_web);
        content.Controls.Add(_loadingLabel);

        Controls.Add(content);
        Controls.Add(_topBar);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _pollTimer.Tick += async (_, _) => await RefreshConnectionStatusAsync();

        HandleCreated += (_, _) => ApplyDarkTitleBar(Handle);
        Shown += async (_, _) =>
        {
            await InitializeWebViewAsync();
            await RefreshConnectionStatusAsync();
            _pollTimer.Start();
        };
        FormClosing += (_, e) =>
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private static Button MakeNavButton(string text, Color bg, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9f),
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(6, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.BorderColor = Color.FromArgb(65, 80, 105);
        btn.Click += onClick;
        return btn;
    }

    public void BringToFrontAndShow()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        TopMost = true;
        TopMost = false;
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webReady) return;
        // Wait briefly until the local HTTP server answers on 127.0.0.1:8765
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var r = await _http.GetAsync(_settingsUrl + "/api/info");
                if (r.IsSuccessStatusCode) break;
            }
            catch { }
            await Task.Delay(250);
        }

        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhoneBox", "webview2");
            Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                // Open external links in the default system browser, keep local PhoneBox pages in-app
                if (!e.Uri.StartsWith(_settingsUrl, StringComparison.OrdinalIgnoreCase))
                {
                    e.Handled = true;
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                }
            };
            _webReady = true;
            _web.Visible = true;
            _loadingLabel.Visible = false;
            _web.CoreWebView2.Navigate(_settingsUrl + _currentPath);
        }
        catch (Exception ex)
        {
            _loadingLabel.Text = $"WebView2 tidak tersedia ({ex.Message}).\nBuka pengaturan di browser: {_settingsUrl}";
            _loadingLabel.Cursor = Cursors.Hand;
            _loadingLabel.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(_settingsUrl) { UseShellExecute = true }); } catch { }
            };
        }
    }

    private void NavigateTo(string path)
    {
        _currentPath = path;
        if (_webReady && _web.CoreWebView2 is not null)
        {
            _web.CoreWebView2.Navigate(_settingsUrl + path);
        }
    }

    private async Task TriggerUsbConnectAsync()
    {
        _statusLabel.Text = "Menghubungkan & membuka panel di HP lewat USB...";
        try
        {
            await _adb.OpenPanelAsync();
            await Task.Delay(800);
        }
        catch { }
        await RefreshConnectionStatusAsync();
    }

    private async Task RefreshConnectionStatusAsync()
    {
        if (IsDisposed || !Visible) return;
        try
        {
            var infoJson = await _http.GetStringAsync(_settingsUrl + "/api/info");
            using var infoDoc = JsonDocument.Parse(infoJson);
            var root = infoDoc.RootElement;
            var connected = root.TryGetProperty("connected", out var c) && c.GetBoolean();
            var via = root.TryGetProperty("via", out var v) ? v.GetString() : null;

            string? phoneModel = null;
            try
            {
                var phonesJson = await _http.GetStringAsync(_settingsUrl + "/api/phones");
                using var pDoc = JsonDocument.Parse(phonesJson);
                if (pDoc.RootElement.ValueKind == JsonValueKind.Array && pDoc.RootElement.GetArrayLength() > 0)
                {
                    var first = pDoc.RootElement[0];
                    phoneModel = first.TryGetProperty("model", out var m) ? m.GetString() : null;
                    if (string.IsNullOrEmpty(via) && first.TryGetProperty("via", out var pv))
                        via = pv.GetString();
                }
            }
            catch { }

            var lanIps = Lan.Addresses();
            var ipText = lanIps.Count > 0 ? lanIps[0] : "127.0.0.1";

            if (connected)
            {
                _statusDot.ForeColor = Color.FromArgb(34, 197, 94);
                var label = string.IsNullOrEmpty(phoneModel) ? "HP Terhubung" : phoneModel;
                _statusLabel.Text = $"Terhubung via {via ?? "USB/Wi-Fi"} · {label}  (LAN {ipText}:8766)";
                _statusLabel.ForeColor = Color.FromArgb(187, 247, 208);
            }
            else
            {
                _statusDot.ForeColor = Color.FromArgb(245, 158, 11);
                _statusLabel.Text = $"Menunggu HP · Wi-Fi siap di {ipText}:8766 (UDP 8767)";
                _statusLabel.ForeColor = Color.FromArgb(253, 230, 138);
            }
        }
        catch
        {
            _statusDot.ForeColor = Color.FromArgb(239, 68, 68);
            _statusLabel.Text = "Menghubungkan ke PhoneBox Agent...";
            _statusLabel.ForeColor = Color.FromArgb(254, 202, 202);
        }
    }

    private async void ShowWifiPairingDialog()
    {
        try
        {
            using var resp = await _http.PostAsync(_settingsUrl + "/api/pair/start", null);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("enabled", out var en) || !en.GetBoolean())
            {
                MessageBox.Show(this,
                    "Mode Wi-Fi sedang nonaktif di Settings > General > Wi-Fi & Tailscale.\nAktifkan dulu lalu klik Save & Restart.",
                    "Hubungkan Wi-Fi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                NavigateTo("/");
                return;
            }

            var link = root.GetProperty("link").GetString() ?? "";
            var check = root.TryGetProperty("check", out var ck) ? ck.GetString() ?? "" : "";
            var port = root.TryGetProperty("port", out var pt) ? pt.GetInt32() : 8766;
            var hosts = root.TryGetProperty("hosts", out var hs)
                ? string.Join(", ", hs.EnumerateArray().Select(x => x.GetString()))
                : "";
            var startClaims = root.TryGetProperty("claims", out var cl) ? cl.GetInt64() : 0;
            var attempt = root.TryGetProperty("attempt", out var at) ? at.GetInt64() : 0;

            using var qrGen = new QRCodeGenerator();
            using var qrData = qrGen.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
            var pngBytes = new PngByteQRCode(qrData).GetGraphic(8);
            using var ms = new MemoryStream(pngBytes);
            using var qrBmp = new Bitmap(ms);

            using var dlg = new Form
            {
                Text = "Hubungkan HP lewat Wi-Fi",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ClientSize = new Size(420, 500),
                BackColor = Color.FromArgb(15, 20, 30),
                ForeColor = Color.White,
            };
            dlg.HandleCreated += (_, _) => ApplyDarkTitleBar(dlg.Handle);

            var title = new Label
            {
                Text = "Scan QR Code ini dengan kamera HP Android",
                Dock = DockStyle.Top,
                Height = 36,
                TextAlign = ContentAlignment.BottomCenter,
                Font = new Font("Segoe UI Semibold", 11f),
            };
            var qrBox = new PictureBox
            {
                Image = (Image)qrBmp.Clone(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(270, 270),
                Location = new Point(75, 48),
                BackColor = Color.White,
                Padding = new Padding(8),
            };
            var infoLbl = new Label
            {
                Text = $"Kode Verifikasi:  {check}\nAlamat PC:  {hosts}:{port}\n\n" +
                       "1. Pastikan HP dan laptop di Wi-Fi yang sama.\n" +
                       "2. Scan QR di atas lalu pilih 'Open in PhoneBox'.\n" +
                       "3. Konfirmasi kode verifikasi di HP.",
                Location = new Point(24, 330),
                Size = new Size(372, 112),
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(203, 213, 225),
            };
            var statusLbl = new Label
            {
                Text = "Menunggu HP melakukan scan...",
                Location = new Point(24, 448),
                Size = new Size(260, 28),
                Font = new Font("Segoe UI Semibold", 9.5f),
                ForeColor = Color.FromArgb(45, 212, 191),
            };
            var closeBtn = new Button
            {
                Text = "Tutup",
                DialogResult = DialogResult.OK,
                Location = new Point(316, 444),
                Size = new Size(80, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(36, 46, 66),
                ForeColor = Color.White,
            };
            closeBtn.FlatAppearance.BorderSize = 0;

            dlg.Controls.AddRange([title, qrBox, infoLbl, statusLbl, closeBtn]);

            using var watch = new System.Windows.Forms.Timer { Interval = 1500 };
            watch.Tick += async (_, _) =>
            {
                try
                {
                    var stJson = await _http.GetStringAsync(_settingsUrl + "/api/pair/status");
                    using var stDoc = JsonDocument.Parse(stJson);
                    if (stDoc.RootElement.TryGetProperty("claims", out var nowClaims) && nowClaims.GetInt64() > startClaims)
                    {
                        watch.Stop();
                        statusLbl.Text = "✓ HP berhasil tersambung lewat Wi-Fi!";
                        statusLbl.ForeColor = Color.FromArgb(74, 222, 128);
                        await RefreshConnectionStatusAsync();
                    }
                }
                catch { }
            };
            watch.Start();
            dlg.FormClosed += async (_, _) =>
            {
                watch.Stop();
                qrBox.Image?.Dispose();
                try { await _http.PostAsync($"{_settingsUrl}/api/pair/cancel?attempt={attempt}", null); } catch { }
            };
            dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Gagal memulai pairing Wi-Fi: {ex.Message}", "PhoneBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _http.Dispose();
        }
        base.Dispose(disposing);
    }

    private static void ApplyDarkTitleBar(nint hwnd)
    {
        try
        {
            var dark = 1;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);
}
