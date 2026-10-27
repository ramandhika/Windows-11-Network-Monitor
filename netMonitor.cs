// netMonitor - Lightweight Windows tray network monitor
// Green/red connection indicator pinned to the taskbar + down/up speed overlay.
// Build: csc /target:winexe /out:netMonitor.exe netMonitor.cs /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace netMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // only one instance may run - prevents stacked/duplicate overlays
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\netMonitor.SingleInstance", out createdNew))
            {
                if (!createdNew) return; // already running, just exit
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                }
                catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new NetMonitorContext());
            }
        }
    }

    internal sealed class NetMonitorContext : ApplicationContext
    {
        private const int PingIntervalMs = 5000;
        private const int SpeedIntervalMs = 1000;
        private const string NotifyIconRoot = @"Control Panel\NotifyIconSettings";

        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _speedTimer;
        private readonly System.Windows.Forms.Timer _pingTimer;
        private readonly OverlayForm _overlay;

        private bool _isOnline = true;
        private long _lastRx = -1;
        private long _lastTx = -1;
        private DateTime _lastSample = DateTime.Now;

        // quota tracking (per network + per day)
        private readonly QuotaTracker _quota = new QuotaTracker();
        private string _activeKey;
        private string _ifaceId;
        private long _lastIfRx = -1;
        private string _cachedSsid;
        private DateTime _ssidChecked = DateTime.MinValue;
        private long _lastIfTx = -1;
        private DateTime _lastQuotaSave = DateTime.Now;

        public NetMonitorContext()
        {
            _quota.Load();
            _overlay = new OverlayForm(GetQuotaView);

            _tray = new NotifyIcon
            {
                Icon = MakeIcon(Color.LimeGreen),
                Text = "netMonitor",
                Visible = true
            };
            _tray.ContextMenu = BuildMenu();

            PromoteToTaskbar(); // ask Windows 11 to show the icon on the taskbar itself

            // force the shell to re-read IsPromoted by re-registering the icon
            _tray.Visible = false;
            _tray.Visible = true;

            _speedTimer = new System.Windows.Forms.Timer { Interval = SpeedIntervalMs };
            _speedTimer.Tick += delegate { UpdateSpeeds(); };
            _speedTimer.Start();

            _pingTimer = new System.Windows.Forms.Timer { Interval = PingIntervalMs };
            _pingTimer.Tick += delegate { CheckConnection(); };
            _pingTimer.Start();

            UpdateSpeeds();
            CheckConnection();
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            menu.MenuItems.Add("Tampilkan / Sembunyikan overlay", delegate { _overlay.Toggle(); });
            menu.MenuItems.Add("Sematkan ikon ke taskbar", delegate
            {
                bool ok = PromoteToTaskbar();
                _tray.ShowBalloonTip(4000, "netMonitor",
                    ok ? "Ikon diminta tampil di taskbar. Jika belum muncul, buka Settings > Personalization > Taskbar > Other system tray icons > netMonitor > On."
                       : "Pengaturan tidak ditemukan. Buka Settings > Personalization > Taskbar > Other system tray icons > netMonitor > On.",
                    ToolTipIcon.Info);
            });
            menu.MenuItems.Add("Keluar", delegate { ExitApp(); });
            return menu;
        }

        /// <summary>Windows 11 hides third-party tray icons in the overflow menu by default.
        /// Setting IsPromoted=1 under HKCU\Control Panel\NotifyIconSettings pins it to the taskbar.</summary>
        private static bool PromoteToTaskbar()
        {
            try
            {
                string exe;
                try { exe = Process.GetCurrentProcess().MainModule.FileName; }
                catch
                {
                    exe = Assembly.GetExecutingAssembly().Location;
                }

                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(NotifyIconRoot, true))
                {
                    if (root == null) return false;
                    foreach (string name in root.GetSubKeyNames())
                    {
                        using (RegistryKey key = root.OpenSubKey(name, true))
                        {
                            if (key == null) continue;
                            object p = key.GetValue("ExecutablePath");
                            if (p == null) continue;
                            if (string.Equals(p.ToString(), exe, StringComparison.OrdinalIgnoreCase))
                            {
                                object promoted = key.GetValue("IsPromoted");
                                if (promoted is int && (int)promoted == 1) return true;
                                key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                return true;
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private void CheckConnection()
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                SetOnline(false);
                return;
            }
            Task.Factory.StartNew(delegate
            {
                bool ok;
                try
                {
                    using (Ping ping = new Ping())
                    {
                        PingReply reply = ping.Send("1.1.1.1", 1500);
                        ok = reply != null && reply.Status == IPStatus.Success;
                    }
                }
                catch { ok = false; }
                try { _overlay.BeginInvoke((Action)delegate { SetOnline(ok); }); }
                catch { }
            });
        }

        private void SetOnline(bool online)
        {
            if (online == _isOnline) return;
            _isOnline = online;
            _tray.Icon = MakeIcon(online ? Color.LimeGreen : Color.Red);
            _tray.Text = Clamp("netMonitor - " +
                (online ? "Terhubung internet" : "Tidak ada koneksi"), 63);
            _overlay.UpdateStatus(_isOnline, 0, 0);
        }

        private static string Clamp(string s, int n)
        {
            return s.Length > n ? s.Substring(0, n) : s;
        }

        private void UpdateSpeeds()
        {
            long rx = 0, tx = 0;        // all up interfaces (for the speed display)
            long ifRx = -1, ifTx = -1;  // active network's interface (for quota)
            string key = null;
            string ifName = null;
            try
            {
                // selection order: default route (best) -> sticky interface -> first with gateway
                int bestIndex = Routing.BestInterfaceIndex();
                NetworkInterface chosen = null;
                string chosenGw = null;
                NetworkInterface sticky = null;
                string stickyGw = null;
                NetworkInterface fallback = null;
                string fallbackGw = null;

                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props;
                    try { props = ni.GetIPProperties(); }
                    catch { props = null; }
                    if (props == null || props.GatewayAddresses.Count == 0) continue;
                    string gw = props.GatewayAddresses[0].Address.ToString();

                    if (bestIndex > 0)
                    {
                        try
                        {
                            if (props.GetIPv4Properties().Index == bestIndex)
                            {
                                chosen = ni;
                                chosenGw = gw;
                                break;
                            }
                        }
                        catch { }
                    }
                    if (sticky == null && _ifaceId != null && ni.Id == _ifaceId)
                    {
                        sticky = ni;
                        stickyGw = gw;
                    }
                    if (fallback == null)
                    {
                        fallback = ni;
                        fallbackGw = gw;
                    }
                }
                if (chosen == null)
                {
                    chosen = sticky != null ? sticky : fallback;
                    chosenGw = sticky != null ? stickyGw : fallbackGw;
                }

                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                    if (chosen != null && ni.Id == chosen.Id)
                    {
                        ifRx = st.BytesReceived;
                        ifTx = st.BytesSent;
                    }
                }

                if (chosen != null)
                {
                    _ifaceId = chosen.Id;
                    key = chosenGw;
                    ifName = chosen.Name;

                    // WiFi: use the actual SSID as the network identity so each
                    // hotspot/router shows up separately in today's history.
                    if (chosen.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        string ssid = GetSsidCached();
                        if (ssid != null && ssid.Length > 0)
                        {
                            string wifiKey = "wifi:" + ssid;
                            _quota.AdoptKey(chosenGw, wifiKey, ssid);
                            key = wifiKey;
                            ifName = ssid;
                        }
                    }
                }
                else
                {
                    _ifaceId = null;
                }
            }
            catch { }

            DateTime now = DateTime.Now;
            double sec = (now - _lastSample).TotalSeconds;
            double down = 0, up = 0;

            if (_lastRx >= 0 && sec > 0.25)
            {
                down = Math.Max(0, (rx - _lastRx) / sec);
                up = Math.Max(0, (tx - _lastTx) / sec);
            }

            if (sec > 0.25)
            {
                _lastRx = rx;
                _lastTx = tx;
                _lastSample = now;
            }

            // quota: only counts the active network; switching networks resets it
            if (key != _activeKey)
            {
                _activeKey = key;
                _lastIfRx = -1;
                _lastIfTx = -1;
            }
            if (key != null)
            {
                long dRx = 0, dTx = 0;
                if (_lastIfRx >= 0 && sec > 0.25)
                {
                    dRx = Math.Max(0, ifRx - _lastIfRx);
                    dTx = Math.Max(0, ifTx - _lastIfTx);
                }
                _quota.Touch(key, ifName, dRx, dTx, sec);
                _quota.SetActive(key);
            }
            else
            {
                _quota.SetActive(null);
            }
            _lastIfRx = ifRx;
            _lastIfTx = ifTx;

            // persist once every 30s when something changed
            if (_quota.Dirty && (now - _lastQuotaSave).TotalSeconds >= 30)
            {
                _quota.Save();
                _lastQuotaSave = now;
            }

            _tray.Text = Clamp("netMonitor - " + FormatSpeed(down) + " | " + FormatSpeed(up), 63);
            _overlay.UpdateStatus(_isOnline, down, up);
        }

        private string GetSsidCached()
        {
            // refresh every 2s; cheap native call, no process spawn
            if ((DateTime.Now - _ssidChecked).TotalSeconds < 2) return _cachedSsid;
            _ssidChecked = DateTime.Now;
            _cachedSsid = WifiSsid.GetCurrent();
            return _cachedSsid;
        }

        private QuotaView GetQuotaView()
        {
            return _quota.TodayHistory();
        }

        private static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec >= 1048576.0)
                return (bytesPerSec / 1048576.0).ToString("F1") + " MB/s";
            if (bytesPerSec >= 1024.0)
                return (bytesPerSec / 1024.0).ToString("F1") + " KB/s";
            return bytesPerSec.ToString("F0") + " B/s";
        }

        private void ExitApp()
        {
            _speedTimer.Stop();
            _pingTimer.Stop();
            _quota.Save();
            _tray.Visible = false;
            _tray.Dispose();
            _overlay.AllowClose = true;
            _overlay.Close();
            Application.Exit();
        }

        /// <summary>Bright filled circle with dark outline - visible on light and dark taskbars.</summary>
        private static Icon MakeIcon(Color color)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush fill = new SolidBrush(color))
                using (Pen outline = new Pen(Color.FromArgb(230, 15, 15, 15), 2f))
                {
                    g.FillEllipse(fill, 3, 3, 26, 26);
                    g.DrawEllipse(outline, 3, 3, 26, 26);
                    // small top-left highlight so it doesn't look flat
                    using (SolidBrush hi = new SolidBrush(Color.FromArgb(90, 255, 255, 255)))
                    {
                        g.FillEllipse(hi, 7, 7, 10, 10);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    /// <summary>Overlay that sits right above the system tray (near the network icon). Draggable, owner-drawn.</summary>
    internal sealed class OverlayForm : Form
    {
        private static readonly string ConfigPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "overlay.cfg");

        private double _down, _up;
        private bool _online = true;
        private bool _dragging;
        private Point _dragOffset;
        private bool _allowClose;
        private readonly Font _font = new Font("Segoe UI Semibold", 8.25f);
        private string _text = "...";

        // expanded state (click): shows LAN/public IP + today's network history
        private bool _expanded;
        private string _lanIp;
        private string _pubIp;
        private DateTime _pubFetched = DateTime.MinValue;
        private bool _pubFetching;
        private readonly Func<QuotaView> _quotaView;
        private QuotaView _view;
        private readonly Font _fontSmall = new Font("Segoe UI", 7.25f);
        private DateTime _expandedAt = DateTime.MinValue;
        private readonly bool _debug = Array.IndexOf(Environment.GetCommandLineArgs(), "--debug") >= 0;

        private void Log(string message)
        {
            if (!_debug) return;
            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "events.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        public const int CompactWidth = 170;
        public const int CompactHeight = 24;
        public const int DetailWidth = 300;
        public const int DetailRowHeight = 32;

        public bool AllowClose
        {
            set { _allowClose = value; }
        }

        public OverlayForm(Func<QuotaView> quotaView)
        {
            _quotaView = quotaView;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(32, 32, 32);
            Opacity = 0.9;
            Size = new Size(CompactWidth, CompactHeight);
            DoubleBuffered = true;

            // default: right above the tray icons (network icon area), bottom of the screen
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 150, wa.Bottom - Height - 6);

            LoadPosition(); // user-dragged position wins over default
            ApplyRoundedRegion();

            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += OnMouseUp;
            Paint += OnPaint;
            MouseClick += delegate { Log("MouseClick"); SetExpanded(!_expanded); }; // click to open/close
            MouseLeave += delegate
            {
                Log("MouseLeave drag=" + _dragging + " sinceExpand=" + (DateTime.Now - _expandedAt).TotalMilliseconds);
                // ignore a leave fired right after opening (resize can cause a spurious leave)
                if (!_dragging && (DateTime.Now - _expandedAt).TotalMilliseconds > 400) SetExpanded(false);
            };

            UpdateStatus(true, 0, 0);
            Show();
            FetchPublicIp(); // warm the cache so hover shows it instantly
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--expand") >= 0) SetExpanded(true);
        }

        /// <summary>Open on click (show IP + today's network history), close when the mouse leaves.
        /// Anchored bottom-right so growing upward never covers the taskbar.</summary>
        private void SetExpanded(bool expand)
        {
            if (_expanded == expand) return;
            _expandedAt = DateTime.Now;
            Log("SetExpanded(" + expand + ") at " + Location.X + "," + Location.Y + " size " + Width + "x" + Height);
            int bottom = Location.Y + Height;
            int right = Location.X + Width;
            _expanded = expand;
            if (expand)
            {
                _lanIp = GetLanIp();
                _view = _quotaView != null ? _quotaView() : null;
                if (_pubIp == null || (DateTime.Now - _pubFetched).TotalMinutes >= 10)
                {
                    FetchPublicIp();
                }
            }
            RecomputeSize();
            Rectangle wa = Screen.FromPoint(Location).WorkingArea;
            int x = Math.Max(wa.Left + 4, Math.Min(right - Width, wa.Right - Width - 4));
            int y = Math.Max(wa.Top + 4, bottom - Height);
            Location = new Point(x, y);
        }

        public void Toggle()
        {
            if (Visible) Hide(); else Show();
        }

        public void UpdateStatus(bool online, double down, double up)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)delegate { UpdateStatus(online, down, up); });
                return;
            }
            _online = online;
            _down = down;
            _up = up;
            Render();
        }

        private void Render()
        {
            _text = _online
                ? string.Format("\u2193 {0}   \u2191 {1}", F(_down), F(_up))
                : "Tidak ada internet";
            if (_expanded) _view = _quotaView != null ? _quotaView() : null;
            RecomputeSize();
        }

        private int DetailHeight()
        {
            int count = _view != null ? Math.Min(_view.Rows.Count, 5) : 0;
            return 68 + Math.Max(1, count) * DetailRowHeight + 10;
        }

        private void RecomputeSize()
        {
            // compact: fixed 170x24 | detail (click): fixed 300 wide, height follows entries
            int w = _expanded ? DetailWidth : CompactWidth;
            int h = _expanded ? DetailHeight() : CompactHeight;
            bool changed = Width != w || Height != h;
            if (changed)
            {
                Width = w;
                Height = h;
                ApplyRoundedRegion();
            }
            Invalidate();
        }

        private const int CornerRadius = 6;

        private void ApplyRoundedRegion()
        {
            Region old = Region;
            try
            {
                using (GraphicsPath path = RoundedPath(Width, Height, CornerRadius))
                {
                    Region = new Region(path);
                }
            }
            finally
            {
                if (old != null) old.Dispose();
            }
        }

        private static GraphicsPath RoundedPath(int width, int height, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(0, 0, d, d, 180, 90);                      // top-left
            path.AddArc(width - d - 1, 0, d, d, 270, 90);          // top-right
            path.AddArc(width - d - 1, height - d - 1, d, d, 0, 90);
            path.AddArc(0, height - d - 1, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private string PubText()
        {
            if (_pubIp == null) return _pubFetching ? "cek..." : "(gagal cek)";
            return _pubIp;
        }

        /// <summary>Best-effort LAN IPv4: prefer an up interface that has a default gateway.</summary>
        private static string GetLanIp()
        {
            try
            {
                string fallback = null;
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    IPInterfaceProperties props = ni.GetIPProperties();
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        if (props.GatewayAddresses.Count > 0) return ua.Address.ToString();
                        if (fallback == null) fallback = ua.Address.ToString();
                    }
                }
                return fallback;
            }
            catch { return null; }
        }

        /// <summary>Fetch the public internet IP via ipify (cached, retried every few minutes).</summary>
        private void FetchPublicIp()
        {
            if (_pubFetching) return;
            _pubFetching = true;
            Task.Factory.StartNew(delegate
            {
                string result = null;
                string[] urls = new string[]
                {
                    "https://api.ipify.org",
                    "http://api.ipify.org"
                };
                foreach (string url in urls)
                {
                    try
                    {
                        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                        req.Timeout = 6000;
                        using (WebResponse resp = req.GetResponse())
                        using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                        {
                            result = sr.ReadToEnd().Trim();
                        }
                        if (result != null && result.Length > 0) break;
                    }
                    catch { result = null; }
                }

                bool ok = result != null && result.Length <= 45;
                _pubFetched = ok ? DateTime.Now : DateTime.Now.AddMinutes(-8); // retry sooner on failure
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        _pubFetching = false;
                        _pubIp = ok ? result : null;
                        if (_expanded) RecomputeSize();
                    });
                }
                catch
                {
                    _pubFetching = false;
                    _pubIp = ok ? result : null;
                }
            });
        }

        private static string F(double bytesPerSec)
        {
            if (bytesPerSec >= 1048576.0) return (bytesPerSec / 1048576.0).ToString("F1") + " MB/s";
            if (bytesPerSec >= 1024.0) return (bytesPerSec / 1024.0).ToString("F1") + " KB/s";
            return bytesPerSec.ToString("F0") + " B/s";
        }

        private static string Dur(double seconds)
        {
            int total = (int)Math.Round(seconds);
            int h = total / 3600;
            int m = (total % 3600) / 60;
            if (h > 0) return h + "j " + m + "m";
            if (m > 0) return m + "m";
            return "<1m";
        }

        private void OnPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // row 1: status dot + speed (fixed positions in the top 24px)
            using (SolidBrush dot = new SolidBrush(_online ? Color.LimeGreen : Color.Red))
            using (Pen ring = new Pen(Color.FromArgb(220, 15, 15, 15), 1.4f))
            {
                Rectangle r = new Rectangle(7, 7, 10, 10);
                g.FillEllipse(dot, r);
                g.DrawEllipse(ring, r);
            }
            using (SolidBrush br = new SolidBrush(_online ? Color.Gainsboro : Color.Tomato))
            {
                g.DrawString(_text, _font, br, 22, 5);
            }

            // expanded panel: separator, IP info, then today's network history
            if (_expanded)
            {
                using (Pen sep = new Pen(Color.FromArgb(60, 255, 255, 255)))
                {
                    g.DrawLine(sep, 10, 25, Width - 10, 25);
                    g.DrawLine(sep, 10, 46, Width - 10, 46);
                }

                // one compact line for both IPs
                using (SolidBrush info = new SolidBrush(Color.FromArgb(180, 175, 175, 175)))
                {
                    string ipLine = "Lokal " + (_lanIp ?? "-") + "   \u00B7   Publik " + PubText();
                    g.DrawString(ipLine, _fontSmall, info, 12, 29);
                }

                // section header + total today
                using (SolidBrush title = new SolidBrush(Color.Gainsboro))
                using (SolidBrush totalBr = new SolidBrush(Color.FromArgb(255, 190, 235, 190)))
                {
                    g.DrawString("Riwayat hari ini", _font, title, 12, 50);
                    string total = _view != null ? _view.TotalText : "0 B";
                    SizeF ts = g.MeasureString(total, _font);
                    g.DrawString(total, _font, totalBr, Width - 12 - ts.Width, 50);
                }

                int y = 68;
                int count = _view != null ? Math.Min(_view.Rows.Count, 5) : 0;
                if (count == 0)
                {
                    using (SolidBrush empty = new SolidBrush(Color.FromArgb(150, 160, 160, 160)))
                    {
                        g.DrawString("Belum ada data jaringan hari ini", _fontSmall, empty, 12, y + 2);
                    }
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        QuotaRow row = _view.Rows[i];
                        int ry = y + i * DetailRowHeight;
                        bool active = row.Active;

                        // bullet: green for the network in use right now, dim otherwise
                        using (SolidBrush bullet = new SolidBrush(active ? Color.LimeGreen : Color.FromArgb(80, 80, 80)))
                        {
                            g.FillEllipse(bullet, 11, ry + 5, 7, 7);
                        }

                        using (SolidBrush nameBr = new SolidBrush(active ? Color.Gainsboro : Color.FromArgb(165, 172, 172, 172)))
                        using (SolidBrush sizeBr = new SolidBrush(Color.FromArgb(210, 220, 220, 220)))
                        {
                            string name = row.Name;
                            if (name.Length > 22) name = name.Substring(0, 21) + "\u2026";
                            string label = row.Key != null && row.Key.StartsWith("wifi:", StringComparison.Ordinal)
                                ? name                                        // WiFi: SSID only
                                : name + " \u00B7 " + row.Key;                // wired: adapter + gateway
                            g.DrawString(label, _font, nameBr, 23, ry);

                            string size = QuotaTracker.FormatBytes(row.Rx + row.Tx);
                            SizeF ss = g.MeasureString(size, _font);
                            g.DrawString(size, _font, sizeBr, Width - 12 - ss.Width, ry);
                        }

                        using (SolidBrush subBr = new SolidBrush(Color.FromArgb(145, 165, 165, 165)))
                        {
                            string range = row.FirstSeen.ToString("HH:mm") + "\u2013" +
                                (active ? "sekarang" : row.LastSeen.ToString("HH:mm"));
                            string sub = string.Format("{0}  \u00B7  {1}  \u00B7  \u2193 {2}  \u2191 {3}",
                                range, Dur(row.ActiveSeconds),
                                QuotaTracker.FormatBytes(row.Rx),
                                QuotaTracker.FormatBytes(row.Tx));
                            g.DrawString(sub, _fontSmall, subBr, 23, ry + 16);
                        }
                    }
                }
            }

            // thin subtle border matching the rounded shape
            using (GraphicsPath border = RoundedPath(Width - 1, Height - 1, CornerRadius - 1))
            using (Pen pb = new Pen(Color.FromArgb(70, 255, 255, 255)))
            {
                g.DrawPath(pb, border);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason != CloseReason.ApplicationExitCall)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragOffset = new Point(e.X, e.Y);
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point screen = PointToScreen(new Point(e.X, e.Y));
            Location = new Point(screen.X - _dragOffset.X, screen.Y - _dragOffset.Y);
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                SavePosition();
                // if the drag ended with the cursor outside the overlay, close the panel
                Rectangle bounds = new Rectangle(Location, Size);
                if (!bounds.Contains(Cursor.Position)) SetExpanded(false);
            }
        }

        private void LoadPosition()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                string[] parts = File.ReadAllText(ConfigPath).Trim().Split(',');
                int x = int.Parse(parts[0]);
                int y = int.Parse(parts[1]);
                Location = new Point(x, y);
            }
            catch { }
        }

        private void SavePosition()
        {
            try
            {
                File.WriteAllText(ConfigPath, Location.X + "," + Location.Y);
            }
            catch { }
        }
    }

    /// <summary>Asks Windows which interface actually carries outgoing traffic (default route).</summary>
    internal static class Routing
    {
        [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
        private static extern int GetBestInterface(uint destAddr, out uint bestIndex);

        public static int BestInterfaceIndex()
        {
            try
            {
                uint index;
                // 1.1.1.1 (0x01010101) as the probe destination
                if (GetBestInterface(0x01010101, out index) == 0) return (int)index;
            }
            catch { }
            return -1;
        }
    }

    /// <summary>Reads the current WiFi SSID via the native WLAN API (wlanapi.dll).</summary>
    internal static class WifiSsid
    {
        [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
        private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved,
            out uint negotiatedVersion, out IntPtr clientHandle);

        [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
        private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

        [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
        private static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved,
            out IntPtr interfaceList);

        [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
        private static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid,
            int opCode, IntPtr reserved, out int dataSize, out IntPtr data,
            IntPtr opcodeValueType);

        [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr memory);

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct WlanInterfaceInfo
        {
            public Guid InterfaceGuid;
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Description;
            public int State; // 1 = connected
        }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct WlanConnectionAttributes
        {
            public int State;
            public int ConnectionMode;
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 256)]
            public string ProfileName;
            public WlanAssociationAttributes Association;
            public WlanSecurityAttributes Security;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WlanAssociationAttributes
        {
            public Dot11Ssid Ssid;
            public int BssType;
            public int PhyType;
            public uint PhyIndex;
            public uint SignalQuality;
            public uint RxRate;
            public uint TxRate;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Dot11Ssid
        {
            public uint Length;
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] Bytes;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WlanSecurityAttributes
        {
            public int SecurityEnabled;
            public int OneXEnabled;
            public int AuthAlgorithm;
            public int CipherAlgorithm;
        }

        private const int OpcodeCurrentConnection = 7;

        /// <summary>SSID currently connected to, or null when not on WiFi / API unavailable.</summary>
        public static string GetCurrent()
        {
            IntPtr handle = IntPtr.Zero;
            IntPtr list = IntPtr.Zero;
            try
            {
                uint negotiated;
                if (WlanOpenHandle(2, IntPtr.Zero, out negotiated, out handle) != 0) return null;
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0 || list == IntPtr.Zero) return null;

                int count = System.Runtime.InteropServices.Marshal.ReadInt32(list);
                int size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(WlanInterfaceInfo));
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = (IntPtr)(list.ToInt64() + 8 + i * size);
                    WlanInterfaceInfo info = (WlanInterfaceInfo)System.Runtime.InteropServices.Marshal
                        .PtrToStructure(item, typeof(WlanInterfaceInfo));
                    if (info.State != 1) continue; // not connected

                    IntPtr data = IntPtr.Zero;
                    int dataSize;
                    int rc = WlanQueryInterface(handle, ref info.InterfaceGuid, OpcodeCurrentConnection,
                        IntPtr.Zero, out dataSize, out data, IntPtr.Zero);
                    if (rc != 0 || data == IntPtr.Zero) continue;
                    try
                    {
                        WlanConnectionAttributes attrs = (WlanConnectionAttributes)
                            System.Runtime.InteropServices.Marshal.PtrToStructure(
                                data, typeof(WlanConnectionAttributes));
                        int len = (int)attrs.Association.Ssid.Length;
                        byte[] bytes = attrs.Association.Ssid.Bytes;
                        if (bytes == null || len <= 0 || len > 32) continue;
                        return Encoding.UTF8.GetString(bytes, 0, len);
                    }
                    finally
                    {
                        WlanFreeMemory(data);
                    }
                }
            }
            catch { }
            finally
            {
                if (list != IntPtr.Zero) WlanFreeMemory(list);
                if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
            }
            return null;
        }
    }

    /// <summary>One network used today, shown in the detail panel.</summary>
    internal sealed class QuotaRow
    {
        public string Key;
        public string Name;
        public long Rx;
        public long Tx;
        public DateTime FirstSeen;
        public DateTime LastSeen;
        public double ActiveSeconds;
        public bool Active;
    }

    /// <summary>Snapshot of today's network history for the overlay.</summary>
    internal sealed class QuotaView
    {
        public List<QuotaRow> Rows = new List<QuotaRow>();
        public string TotalText = "0 B";
    }

    /// <summary>Cumulative per-day byte usage per network (keyed by gateway IP).
    /// New day = clean slate. Persisted to usage.cfg so restarts don't lose today's history.</summary>
    internal sealed class QuotaTracker
    {
        private static readonly string FilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "usage.cfg");

        private sealed class NetworkUsage
        {
            public string Name;
            public long Rx;
            public long Tx;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public double ActiveSeconds;
            public bool Active;
        }

        private readonly Dictionary<string, NetworkUsage> _today =
            new Dictionary<string, NetworkUsage>();
        private DateTime _day = DateTime.Today;
        private bool _dirty;

        public bool Dirty
        {
            get { return _dirty; }
        }

        /// <summary>Records usage/duration for the active network; also creates the entry
        /// as soon as a network is used, even with zero traffic.</summary>
        public void Touch(string key, string name, long rx, long tx, double activeSeconds)
        {
            CheckDay();
            bool isNew = !_today.ContainsKey(key);
            NetworkUsage u;
            if (isNew)
            {
                u = new NetworkUsage();
                u.FirstSeen = DateTime.Now;
                _today[key] = u;
            }
            else
            {
                u = _today[key];
            }
            if (u.Name == null || u.Name.Length == 0) u.Name = name;
            if (rx > 0 || tx > 0)
            {
                u.Rx += rx;
                u.Tx += tx;
                _dirty = true;
            }
            DateTime now = DateTime.Now;
            u.LastSeen = now;
            if (activeSeconds > 0 && activeSeconds < 5)
            {
                u.ActiveSeconds += activeSeconds;
            }
            _dirty = true;
        }

        /// <summary>Migrates a legacy gateway-keyed WiFi entry to its SSID key, so today's
        /// usage survives the upgrade. Ethernet entries (named after the adapter) are left alone.</summary>
        public void AdoptKey(string oldKey, string newKey, string newName)
        {
            if (oldKey == null || newKey == null || oldKey == newKey) return;
            CheckDay();
            NetworkUsage src;
            if (!_today.TryGetValue(oldKey, out src)) return;
            bool legacyWifi = src.Name == null || src.Name.Length == 0 ||
                              src.Name.Equals("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                              src.Name.StartsWith("Jaringan ", StringComparison.OrdinalIgnoreCase);
            if (!legacyWifi) return;

            NetworkUsage dst;
            if (_today.TryGetValue(newKey, out dst))
            {
                dst.Rx += src.Rx;
                dst.Tx += src.Tx;
                dst.ActiveSeconds += src.ActiveSeconds;
                if (src.FirstSeen < dst.FirstSeen) dst.FirstSeen = src.FirstSeen;
                if (src.LastSeen > dst.LastSeen) dst.LastSeen = src.LastSeen;
            }
            else
            {
                src.Name = newName;
                _today[newKey] = src;
            }
            _today.Remove(oldKey);
            _dirty = true;
        }

        /// <summary>Marks exactly one network as currently active (or none).</summary>
        public void SetActive(string key)
        {
            bool changed = false;
            foreach (KeyValuePair<string, NetworkUsage> kv in _today)
            {
                bool active = key != null && kv.Key == key;
                if (kv.Value.Active != active)
                {
                    kv.Value.Active = active;
                    changed = true;
                }
            }
            if (changed) _dirty = true;
        }

        public QuotaView TodayHistory()
        {
            CheckDay();
            QuotaView view = new QuotaView();
            long total = 0;
            foreach (KeyValuePair<string, NetworkUsage> kv in _today)
            {
                NetworkUsage u = kv.Value;
                QuotaRow row = new QuotaRow();
                row.Key = kv.Key;
                row.Name = u.Name ?? ("Jaringan " + kv.Key);
                row.Rx = u.Rx;
                row.Tx = u.Tx;
                row.FirstSeen = u.FirstSeen;
                row.LastSeen = u.LastSeen;
                row.ActiveSeconds = u.ActiveSeconds;
                row.Active = u.Active;
                view.Rows.Add(row);
                total += u.Rx + u.Tx;
            }
            view.Rows.Sort(delegate(QuotaRow a, QuotaRow b) { return a.FirstSeen.CompareTo(b.FirstSeen); });
            view.TotalText = FormatBytes(total);
            return view;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("F2") + " GB";
            if (bytes >= 1048576L) return (bytes / 1048576.0).ToString("F1") + " MB";
            if (bytes >= 1024L) return (bytes / 1024.0).ToString("F1") + " KB";
            return bytes + " B";
        }

        public void Save()
        {
            try
            {
                CheckDay();
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, NetworkUsage> kv in _today)
                {
                    NetworkUsage u = kv.Value;
                    string name = (u.Name ?? "").Replace('|', ' ');
                    sb.Append(_day.ToString("yyyyMMdd"))
                      .Append('|').Append(kv.Key)
                      .Append('|').Append(u.Rx)
                      .Append('|').Append(u.Tx)
                      .Append('|').Append(name)
                      .Append('|').Append(u.FirstSeen.Ticks)
                      .Append('|').Append(u.LastSeen.Ticks)
                      .Append('|').Append((long)u.ActiveSeconds)
                      .Append(Environment.NewLine);
                }
                File.WriteAllText(FilePath, sb.ToString());
                _dirty = false;
            }
            catch { }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                string today = DateTime.Today.ToString("yyyyMMdd");
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    string[] p = line.Split('|');
                    if (p.Length < 8 || p[0] != today) continue; // skip legacy 4-field lines
                    long rx, tx;
                    if (!long.TryParse(p[2], out rx) || !long.TryParse(p[3], out tx)) continue;
                    NetworkUsage u = new NetworkUsage();
                    u.Rx = rx;
                    u.Tx = tx;
                    u.Name = p[4];
                    long t;
                    if (long.TryParse(p[5], out t)) u.FirstSeen = new DateTime(t);
                    if (long.TryParse(p[6], out t)) u.LastSeen = new DateTime(t);
                    long a;
                    if (long.TryParse(p[7], out a)) u.ActiveSeconds = a;
                    if (u.FirstSeen == DateTime.MinValue) u.FirstSeen = DateTime.Now;
                    if (u.LastSeen == DateTime.MinValue) u.LastSeen = u.FirstSeen;
                    _today[p[1]] = u;
                }
            }
            catch { }
        }

        private void CheckDay()
        {
            if (DateTime.Today == _day) return;
            _day = DateTime.Today;
            _today.Clear();  // daily reset: everything starts from 0 again
            _dirty = true;
        }
    }
}
