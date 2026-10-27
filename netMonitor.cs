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
        private long _lastIfRx = -1;
        private long _lastIfTx = -1;
        private DateTime _lastQuotaSave = DateTime.Now;

        public NetMonitorContext()
        {
            _quota.Load();
            _overlay = new OverlayForm(GetQuotaLine);

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
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                    if (key != null) continue;
                    // first up interface with a default gateway = the network we are on
                    IPInterfaceProperties props;
                    try { props = ni.GetIPProperties(); }
                    catch { props = null; }
                    if (props == null || props.GatewayAddresses.Count == 0) continue;
                    key = props.GatewayAddresses[0].Address.ToString();
                    ifRx = st.BytesReceived;
                    ifTx = st.BytesSent;
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
            if (key != null && _lastIfRx >= 0 && sec > 0.25)
            {
                _quota.Add(key, Math.Max(0, ifRx - _lastIfRx), Math.Max(0, ifTx - _lastIfTx));
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

        private string GetQuotaLine()
        {
            return _activeKey == null
                ? "Kuota: -"
                : "Kuota: " + _quota.Total(_activeKey);
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

        // expanded state (hover): shows LAN + public IP + quota under the speeds
        private bool _expanded;
        private string _lanIp;
        private string _pubIp;
        private DateTime _pubFetched = DateTime.MinValue;
        private bool _pubFetching;
        private readonly Func<string> _quotaGetter;

        public bool AllowClose
        {
            set { _allowClose = value; }
        }

        public OverlayForm(Func<string> quotaGetter)
        {
            _quotaGetter = quotaGetter;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(32, 32, 32);
            Opacity = 0.9;
            Size = new Size(FixedWidth, 24);
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
            MouseClick += delegate { SetExpanded(!_expanded); }; // click to open/close
            MouseLeave += delegate { if (!_dragging) SetExpanded(false); }; // mouse out = close

            UpdateStatus(true, 0, 0);
            Show();
            FetchPublicIp(); // warm the cache so hover shows it instantly
        }

        /// <summary>Open on click (show IP + quota info), close when the mouse leaves. Bottom edge stays put.</summary>
        private void SetExpanded(bool expand)
        {
            if (_expanded == expand) return;
            _expanded = expand;
            if (expand)
            {
                _lanIp = GetLanIp();
                if (_pubIp == null || (DateTime.Now - _pubFetched).TotalMinutes >= 10)
                {
                    FetchPublicIp();
                }
            }
            int x = Math.Max(6, Location.X);
            int y = expand
                ? Math.Max(6, Location.Y - 60)   // grow upward, keep bottom edge near the tray
                : Location.Y + 60;
            Location = new Point(x, y);
            RecomputeSize();
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
            RecomputeSize();
        }

        private void RecomputeSize()
        {
            // fixed size - only the height changes between compact and expanded
            int h = _expanded ? 84 : 24;
            if (Height != h)
            {
                Height = h;
                ApplyRoundedRegion();
            }
            Invalidate();
        }

        private const int CornerRadius = 6;
        private const int FixedWidth = 170;

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
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.ipify.org");
                    req.Timeout = 6000;
                    using (WebResponse resp = req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                    {
                        result = sr.ReadToEnd().Trim();
                    }
                }
                catch { }

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

            // expanded rows: separator line + LAN/public IP
            if (_expanded)
            {
                using (Pen sep = new Pen(Color.FromArgb(60, 255, 255, 255)))
                {
                    g.DrawLine(sep, 10, 25, Width - 10, 25);
                }
                using (SolidBrush info = new SolidBrush(Color.FromArgb(185, 200, 200, 200)))
                {
                    g.DrawString("Lokal: " + (_lanIp ?? "-"), _font, info, 12, 28);
                    g.DrawString("Publik: " + PubText(), _font, info, 12, 46);
                    g.DrawString(_quotaGetter != null ? _quotaGetter() : "Kuota: -", _font, info, 12, 64);
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

    /// <summary>Cumulative per-day byte usage per network (keyed by gateway IP).
    /// New day = clean slate. Persisted to usage.cfg so restarts don't lose today's count.</summary>
    internal sealed class QuotaTracker
    {
        private static readonly string FilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "usage.cfg");

        private sealed class NetworkUsage
        {
            public long Rx;
            public long Tx;
        }

        private readonly Dictionary<string, NetworkUsage> _today =
            new Dictionary<string, NetworkUsage>();
        private DateTime _day = DateTime.Today;
        private bool _dirty;

        public bool Dirty
        {
            get { return _dirty; }
        }

        public void Add(string key, long rx, long tx)
        {
            if (rx <= 0 && tx <= 0) return;
            CheckDay();
            NetworkUsage u;
            if (!_today.TryGetValue(key, out u))
            {
                u = new NetworkUsage();
                _today[key] = u;
            }
            u.Rx += rx;
            u.Tx += tx;
            _dirty = true;
        }

        public string Total(string key)
        {
            CheckDay();
            NetworkUsage u;
            long total = 0;
            if (_today.TryGetValue(key, out u)) total = u.Rx + u.Tx;
            if (total >= 1073741824.0) return (total / 1073741824.0).ToString("F2") + " GB";
            if (total >= 1048576.0) return (total / 1048576.0).ToString("F1") + " MB";
            if (total >= 1024.0) return (total / 1024.0).ToString("F1") + " KB";
            return total + " B";
        }

        public void Save()
        {
            try
            {
                CheckDay();
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, NetworkUsage> kv in _today)
                {
                    sb.Append(_day.ToString("yyyyMMdd"))
                      .Append('|').Append(kv.Key)
                      .Append('|').Append(kv.Value.Rx)
                      .Append('|').Append(kv.Value.Tx)
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
                    if (p.Length != 4 || p[0] != today) continue;
                    long rx, tx;
                    if (!long.TryParse(p[2], out rx) || !long.TryParse(p[3], out tx)) continue;
                    NetworkUsage u = new NetworkUsage();
                    u.Rx = rx;
                    u.Tx = tx;
                    _today[p[1]] = u;
                }
            }
            catch { }
        }

        private void CheckDay()
        {
            if (DateTime.Today == _day) return;
            _day = DateTime.Today;
            _today.Clear();  // daily reset: usage starts from 0 again
            _dirty = true;
        }
    }
}
