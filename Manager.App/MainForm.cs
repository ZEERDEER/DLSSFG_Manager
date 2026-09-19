using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Sm86.Manager.App
{
    public sealed class MainForm : Form
    {
        public const string UpstreamUrl = "https://github.com/" + GithubUpdater.Owner + "/" + GithubUpdater.Repo;
        public const string ClientUrl = "https://github.com/ZEERDEER/DLSSFG_Manager";

        private readonly string _dataDir;
        private readonly SettingsStore _store;
        private readonly InstallerService _installer;
        private readonly GithubUpdater _updater;
        private readonly SteamStoreClient _steam;
        private readonly IconProvider _icons;
        private readonly GameScanner _scanner = new GameScanner();
        private readonly AppSettings _settings;
        private ReleaseInfo _release;
        private CancellationTokenSource _scanCts, _namesCts;
        private bool _busy;

        // header
        private readonly Label _title = new Label(), _subtitle = new Label();
        private readonly FlatButton _btnScan = new FlatButton(), _btnAdd = new FlatButton(), _btnCheck = new FlatButton(), _btnLog = new FlatButton(), _btnMore = new FlatButton(), _btnCancel = new FlatButton();
        private readonly FlatButton _btnMin = new FlatButton(), _btnMax = new FlatButton(), _btnClose = new FlatButton();
        private readonly ProgressLine _progress = new ProgressLine();
        private readonly System.Windows.Forms.Timer _statusTimer = new System.Windows.Forms.Timer { Interval = 6000 };
        // center
        private readonly Panel _center = new Panel();
        private readonly GameListView _list = new GameListView();
        private readonly Label _empty = new Label();
        private LogPanel _logPanel;
        private readonly List<string> _pendingLog = new List<string>();
        // footer
        private readonly Label _selectionInfo = new Label();
        private readonly FlatButton _btnInstallSel = new FlatButton(), _btnUpdateSel = new FlatButton(), _btnUninstallSel = new FlatButton();

        private readonly Dictionary<GameEntry, InstalledState> _states = new Dictionary<GameEntry, InstalledState>();

        public MainForm(string dataDir, AppSettings settings)
        {
            _dataDir = dataDir; _settings = settings;
            _store = new SettingsStore(dataDir);
            _installer = new InstallerService(dataDir);
            _updater = new GithubUpdater(dataDir);
            _steam = new SteamStoreClient(dataDir);
            foreach (var kv in _updater.KnownHashes()) _installer.KnownHashes[kv.Key] = kv.Value;
            _release = _updater.LoadCachedRelease();

            Text = "DLSSFG Manager";
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            Size = S(new Size(1180, 760)); MinimumSize = S(new Size(900, 540));
            BackColor = Palette.Window; ForeColor = Palette.Text;
            DoubleBuffered = true; AllowDrop = true;
            DragEnter += (s, e) => { if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += OnDropped;
            Icon = SystemIcons.Application;
            _icons = new IconProvider(dataDir, S(40));
            _statusTimer.Tick += (s, e) => { _statusTimer.Stop(); if (!_busy) UpdateSubtitle(); };

            BuildLayout();
            Load += OnLoaded;
            FormClosing += (s, e) => { _scanCts?.Cancel(); _namesCts?.Cancel(); SaveSettings(); };
        }

        private int S(int v) => Dpi.S(this, v);
        private Size S(Size v) => Dpi.S(this, v);

        /// <summary>WS_EX_COMPOSITED: the form and all children are painted into one buffer per frame — no half-painted frames.</summary>
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x02000000; return cp; }
        }

        // ------------------------------------------------------------------ integrated caption
        // The client area is extended over the title bar (WM_NCCALCSIZE) while the window keeps its normal frame,
        // so shadow, rounded corners, snapping, resizing and maximize behaviour all stay native.

        private const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCLIENT = 1, HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
        private const int SM_CYSIZEFRAME = 33, SM_CXPADDEDBORDER = 92;
        private const uint SWP_FRAMECHANGED = 0x20, SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4;

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct NCCALCSIZE_PARAMS { public RECT Rect0, Rect1, Rect2; public IntPtr Pos; }

        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private int FrameThickness()
        {
            try { return GetSystemMetricsForDpi(SM_CYSIZEFRAME, (uint)DeviceDpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, (uint)DeviceDpi); }
            catch (Exception) { return S(8); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                var p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
                int top = p.Rect0.Top;
                base.WndProc(ref m);
                p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
                p.Rect0.Top = top + (IsZoomed(Handle) ? FrameThickness() : 0);
                Marshal.StructureToPtr(p, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT && !IsZoomed(Handle))
                {
                    long lp = m.LParam.ToInt64();
                    var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    if (pt.Y < FrameThickness()) m.Result = (IntPtr)(pt.X < S(20) ? HTTOPLEFT : pt.X > ClientSize.Width - S(20) ? HTTOPRIGHT : HTTOP);
                }
                return;
            }
            base.WndProc(ref m);
        }

        private void BeginDrag() { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero); }
        private void ToggleMaximize() { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_btnMax != null) _btnMax.Text = WindowState == FormWindowState.Maximized ? "\uE923" : "\uE922";
            PlaceLogPanel();
        }

        private void MakeDraggable(Control c)
        {
            c.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left && e.Clicks == 1) BeginDrag(); };
            c.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleMaximize(); };
        }

        // ------------------------------------------------------------------ layout

        private void BuildLayout()
        {
            Padding = new Padding(0, FrameThickness() - 1, 0, 0);

            // ---- header
            var header = new Panel { Dock = DockStyle.Top, Height = S(96), BackColor = Palette.Window };
            _title.Text = "DLSSFG Manager"; _title.Font = new Font(Font.FontFamily, 16f, FontStyle.Bold); _title.AutoSize = true; _title.Location = new Point(S(24), S(22)); _title.ForeColor = Palette.Text; _title.BackColor = Color.Transparent;
            _subtitle.AutoSize = false; _subtitle.Location = new Point(S(26), S(58)); _subtitle.Size = new Size(S(560), S(22)); _subtitle.ForeColor = Palette.Muted; _subtitle.BackColor = Color.Transparent; _subtitle.AutoEllipsis = true; _subtitle.TextAlign = ContentAlignment.MiddleLeft;

            var caption = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(32), FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0), Margin = new Padding(0) };
            var glyphFont = new Font("Segoe MDL2 Assets", 9f);
            foreach (var (b, glyph, kind, tip) in new[] { (_btnClose, "\uE8BB", FlatButton.Role.Close, "关闭"), (_btnMax, "\uE922", FlatButton.Role.Subtle, "最大化"), (_btnMin, "\uE921", FlatButton.Role.Subtle, "最小化") })
            {
                b.Text = glyph; b.Kind = kind; b.Font = glyphFont; b.Size = new Size(S(46), S(32)); b.Margin = new Padding(0); b.TabStop = false;
                new ToolTip().SetToolTip(b, tip);
                caption.Controls.Add(b);
            }
            _btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;
            _btnMax.Click += (s, e) => ToggleMaximize();
            _btnClose.Click += (s, e) => Close();

            var toolbar = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0), Margin = new Padding(0) };
            foreach (var (b, text, width, tip) in new[] { (_btnCancel, "取消扫描", 96, ""), (_btnScan, "扫描游戏", 96, "读取 Steam 库和自定义目录"), (_btnAdd, "添加 ▾", 84, "添加目录或 EXE"), (_btnCheck, "检查更新", 96, "查询上游最新 Release"), (_btnLog, "\uE7C3", 40, "活动日志"), (_btnMore, "\uE712", 40, "更多") })
            {
                b.Text = text; b.Kind = width == 40 ? FlatButton.Role.Subtle : FlatButton.Role.Normal; b.Size = new Size(S(width), S(34)); b.Margin = new Padding(S(6), 0, 0, 0);
                if (width == 40) b.Font = new Font("Segoe MDL2 Assets", 10f);
                if (tip.Length > 0) new ToolTip().SetToolTip(b, tip);
                toolbar.Controls.Add(b);
            }
            _btnCancel.Visible = false;
            _btnScan.Click += async (s, e) => await ScanAsync(null);
            _btnCancel.Click += (s, e) => _scanCts?.Cancel();
            _btnAdd.Click += (s, e) => ShowAddMenu();
            _btnCheck.Click += async (s, e) => await CheckUpdatesAsync(true);
            _btnLog.Click += (s, e) => ToggleLog();
            _btnMore.Click += (s, e) => ShowMoreMenu();
            _progress.Dock = DockStyle.Bottom; _progress.Height = S(3);
            header.Controls.Add(_title); header.Controls.Add(_subtitle); header.Controls.Add(toolbar); header.Controls.Add(caption); header.Controls.Add(_progress);
            void PlaceToolbar()
            {
                toolbar.Location = new Point(header.ClientSize.Width - toolbar.Width - S(16), S(46));
                _subtitle.Width = Math.Max(S(200), toolbar.Left - _subtitle.Left - S(16));
            }
            header.Resize += (s, e) => PlaceToolbar(); toolbar.SizeChanged += (s, e) => PlaceToolbar();
            foreach (var c in new Control[] { header, caption, _title, _subtitle, toolbar }) MakeDraggable(c);

            // ---- footer
            var footer = new Panel { Dock = DockStyle.Bottom, Height = S(64), Padding = new Padding(S(24), S(14), S(24), S(12)), BackColor = Palette.Window };
            var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
            _selectionInfo.AutoSize = true; _selectionInfo.Margin = new Padding(0, S(9), S(12), 0); _selectionInfo.ForeColor = Palette.Muted; _selectionInfo.BackColor = Color.Transparent;
            left.Controls.Add(_selectionInfo);
            foreach (var (b, text, kind) in new[] { (_btnInstallSel, "安装所选", FlatButton.Role.Primary), (_btnUpdateSel, "更新所选", FlatButton.Role.Normal), (_btnUninstallSel, "卸载所选", FlatButton.Role.Danger) })
            { b.Text = text; b.Kind = kind; b.Size = new Size(S(96), S(34)); b.Margin = new Padding(0, 0, S(8), 0); left.Controls.Add(b); }
            _btnInstallSel.Click += async (s, e) => await RunBatchAsync("install", Checked());
            _btnUpdateSel.Click += async (s, e) => await RunBatchAsync("update", Checked());
            _btnUninstallSel.Click += async (s, e) => await RunBatchAsync("uninstall", Checked());

            // One-line credits: "Powered by sdli1995 · Designed by zeer", each name a link.
            var credits = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.Transparent, Padding = new Padding(0, S(9), 0, 0) };
            var creditFont = new Font("Segoe UI", 9f);
            foreach (var (text, url) in new[] { ("Designed by zeer", ClientUrl), ("·", null), ("Powered by sdli1995", UpstreamUrl) })
            {
                var l = new Label { Text = text, Font = creditFont, AutoSize = true, ForeColor = Palette.Muted, BackColor = Color.Transparent, Margin = new Padding(S(5), 0, S(5), 0) };
                if (url != null)
                {
                    l.Cursor = Cursors.Hand; new ToolTip().SetToolTip(l, url);
                    l.MouseEnter += (s, e) => l.ForeColor = Palette.Text; l.MouseLeave += (s, e) => l.ForeColor = Palette.Muted;
                    l.Click += (s, e) => OpenPath(url);
                }
                credits.Controls.Add(l);
            }
            footer.Controls.Add(left); footer.Controls.Add(credits);

            // ---- center: the list plus overlays
            _center.Dock = DockStyle.Fill; _center.BackColor = Palette.Window; _center.Padding = new Padding(S(24), S(4), S(24), S(4));
            _list.Dock = DockStyle.Fill;
            _list.CheckedChanged += (row, v) => { UpdateSelectionSummary(); SaveSettings(); };
            _list.ProxyClicked += (row, anchor) => ShowProxyMenu(row, anchor);
            _list.ActionRequested += async (row, op) => await RunBatchAsync(op, new[] { row.Game });
            _list.ContextMenuRequested += (row, at) => ShowRowMenu(row, at);
            _empty.Text = "还没有游戏。\n点击右上角「扫描游戏」自动读取 Steam 库，或把游戏 EXE / 文件夹拖到这里。"; _empty.AutoSize = false; _empty.Dock = DockStyle.Fill; _empty.TextAlign = ContentAlignment.MiddleCenter; _empty.ForeColor = Palette.Muted; _empty.Font = new Font(Font.FontFamily, 11f); _empty.BackColor = Palette.Window; _empty.Visible = false;
            _center.Controls.Add(_empty); _center.Controls.Add(_list);
            _empty.BringToFront();

            Controls.Add(_center); Controls.Add(footer); Controls.Add(header);
            _center.BringToFront();
            UpdateSubtitle(); UpdateSelectionSummary();
        }

        // ------------------------------------------------------------------ log panel (floating over the list)

        private void EnsureLogPanel()
        {
            if (_logPanel != null) return;
            _logPanel = new LogPanel(Font) { Visible = false };
            _logPanel.CloseRequested += () => ToggleLog();
            _center.Controls.Add(_logPanel);
            _logPanel.BringToFront();
            foreach (var line in _pendingLog) _logPanel.Append(line);
            _pendingLog.Clear();
            PlaceLogPanel();
        }

        private void PlaceLogPanel()
        {
            if (_logPanel == null) return;
            var area = _center.DisplayRectangle;
            int h = Math.Max(S(160), (int)(area.Height * 0.42));
            _logPanel.Bounds = new Rectangle(area.X, area.Bottom - h, area.Width, h);
        }

        private void ToggleLog()
        {
            EnsureLogPanel();
            _logPanel.Visible = !_logPanel.Visible;
            if (_logPanel.Visible) { PlaceLogPanel(); _logPanel.BringToFront(); }
        }

        private void Log(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(text))); return; }
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
            if (_logPanel != null) _logPanel.Append(line); else _pendingLog.Add(line);
        }

        // ------------------------------------------------------------------ status (header subtitle + progress line)

        private void UpdateSubtitle()
        {
            var parts = new List<string>
            {
                _release != null ? "上游 " + _release.Tag + "，检查于 " + _release.CheckedAtUtc.ToLocalTime().ToString("MM-dd HH:mm") : "尚未检查上游版本",
                _settings.Games.Count + " 个游戏",
            };
            if (!string.IsNullOrEmpty(_settings.LocalPackageDirectory)) parts.Add("本地补丁：" + _settings.LocalPackageDirectory);
            _subtitle.Text = string.Join("  ·  ", parts); _subtitle.ForeColor = Palette.Muted;
        }

        private void SetStatus(string text, bool transient = false)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text, transient))); return; }
            _subtitle.Text = text; _subtitle.ForeColor = Palette.Text;
            _statusTimer.Stop();
            if (transient) _statusTimer.Start();
        }

        private void ShowProgress(bool visible, bool indeterminate = true, int value = 0)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => ShowProgress(visible, indeterminate, value))); return; }
            if (indeterminate) _progress.Indeterminate = true; else _progress.Value = value;
            _progress.Visible = visible;
        }

        // ------------------------------------------------------------------ startup

        private async void OnLoaded(object sender, EventArgs e)
        {
            Log("数据目录：" + _dataDir + (Palette.Dark ? "  · 深色模式" : "  · 浅色模式"));
            bool changed = false;
            foreach (var g in _settings.Games)
            {
                var fixedExe = PathUtil.ActualCase(g.ExePath); var fixedRoot = PathUtil.ActualCase(g.InstallRoot);
                if (fixedExe != g.ExePath || fixedRoot != g.InstallRoot) { g.ExePath = fixedExe; g.InstallRoot = fixedRoot; changed = true; }
            }
            if (changed) SaveSettings();
            try { foreach (var msg in await Task.Run(() => _installer.RecoverPending())) Log(msg); }
            catch (Exception ex) { Log("恢复未完成操作失败：" + ex.Message); }
            RebuildRows();
            _ = FetchNamesAsync();
        }

        // ------------------------------------------------------------------ rows

        private IEnumerable<GameEntry> Checked() => _settings.Games.Where(g => g.Selected).ToList();

        private void RebuildRows()
        {
            var rows = new List<GameListView.Row>();
            foreach (var g in _settings.Games)
            {
                var row = new GameListView.Row { Game = g };
                Bind(row);
                rows.Add(row);
            }
            _list.SetRows(rows);
            foreach (var row in rows) LoadIconAsync(row);
            _empty.Visible = _settings.Games.Count == 0;
            UpdateSelectionSummary();
        }

        private void Bind(GameListView.Row row)
        {
            var g = row.Game;
            InstalledState st = null;
            try { st = _installer.Inspect(g); _states[g] = st; g.Status = st.Status; g.InstalledVersion = st.Version; }
            catch (Exception ex) { g.Status = "无法检查：" + ex.Message; _states.Remove(g); }
            row.State = st;
            row.Ambiguous = g.CandidateExes.Count > 1 && g.LocatedBy != "用户选择";
            row.Tooltip = "推荐 " + g.RecommendedProxy + "：" + g.RecommendationReason + "\n点击更换代理 DLL；每个游戏只启用一个。";
            bool installed = st != null && st.IsInstalled;
            row.Version = installed ? st.Version : "—";
            if (st == null) { row.StatusText = g.Status; row.StatusDot = Palette.Danger; }
            else if (st.IsModified) { row.StatusText = "已被外部修改"; row.StatusDot = Palette.Danger; }
            else if (st.IsAmbiguous) { row.StatusText = "多个代理 DLL"; row.StatusDot = Palette.Warning; }
            else if (!installed) { row.StatusText = "未安装"; row.StatusDot = Palette.Muted; }
            else if (_release != null && st.Version != "未知版本" && st.Version != _release.Tag) { row.StatusText = "可更新 → " + _release.Tag; row.StatusDot = Palette.Warning; }
            else if (st.Version == "未知版本") { row.StatusText = st.IsManaged ? "已安装" : "旧插件 · 未知版本"; row.StatusDot = Palette.Warning; }
            else { row.StatusText = st.IsManaged ? "已安装" : "已安装 · 未纳管"; row.StatusDot = Palette.Success; }

            if (!installed) { row.PrimaryAction = "install"; row.PrimaryText = "安装"; row.PrimaryIsAccent = true; }
            else if (!st.ProxyName.Equals(g.SelectedProxy, StringComparison.OrdinalIgnoreCase)) { row.PrimaryAction = "install"; row.PrimaryText = "切换"; row.PrimaryIsAccent = true; }
            else if (_release != null && !st.IsModified && st.Version != _release.Tag) { row.PrimaryAction = "update"; row.PrimaryText = "更新"; row.PrimaryIsAccent = true; }
            else { row.PrimaryAction = "install"; row.PrimaryText = "重装"; row.PrimaryIsAccent = false; }
            row.ShowUninstall = installed;
            _list.RefreshRow(row);
        }

        private void BindAll() { foreach (var r in _list.Rows) Bind(r); _list.RefreshAll(); UpdateSelectionSummary(); }

        private void LoadIconAsync(GameListView.Row row)
        {
            var g = row.Game;
            Task.Run(() => { try { return _icons.Get(g); } catch (Exception) { return null; } }).ContinueWith(t =>
            {
                if (t.Result == null || IsDisposed) return;
                try { BeginInvoke(new Action(() => { row.Icon = t.Result; _list.RefreshRow(row); })); } catch (Exception) { }
            });
        }

        private void UpdateSelectionSummary()
        {
            int n = _settings.Games.Count(g => g.Selected);
            _selectionInfo.Text = n > 0 ? "已勾选 " + n + " 个" : "勾选游戏后可批量操作";
            _btnInstallSel.Enabled = n > 0 && !_busy; _btnUninstallSel.Enabled = n > 0 && !_busy;
            _btnUpdateSel.Enabled = n > 0 && !_busy && _settings.Games.Any(g => g.Selected && _states.TryGetValue(g, out var st) && st.IsInstalled);
        }

        private void MergeGames(IEnumerable<GameEntry> found, string source)
        {
            int added = 0, updated = 0;
            foreach (var g in found)
            {
                var key = PathUtil.DirectoryKey(g.ExePath);
                var existing = _settings.Games.FirstOrDefault(x => PathUtil.DirectoryKey(x.ExePath) == key);
                if (existing == null) { g.Selected = false; _settings.Games.Add(g); added++; }
                else
                {
                    ProxyRecommender.Apply(existing, existing.SelectedProxy);
                    if (string.IsNullOrEmpty(existing.SteamAppId)) existing.SteamAppId = g.SteamAppId;
                    if (KnownGames.Match(g) != null && !string.IsNullOrEmpty(g.Name)) existing.Name = g.Name;
                    if (existing.LocatedBy != "用户选择") { existing.ExePath = g.ExePath; existing.CandidateExes = g.CandidateExes; existing.LocatedBy = g.LocatedBy; }
                    existing.InstallRoot = g.InstallRoot;
                    updated++;
                }
            }
            _settings.Games.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            Log(source + "：新增 " + added + " 个，已存在 " + updated + " 个。");
            SaveSettings();
            RebuildRows(); UpdateSubtitle();
            _ = FetchNamesAsync();
        }

        private void SaveSettings() { try { _store.Save(_settings); } catch (Exception ex) { Log("保存设置失败：" + ex.Message); } }

        // ------------------------------------------------------------------ names (Steam store, cached)

        private async Task FetchNamesAsync()
        {
            _namesCts?.Cancel();
            var cts = _namesCts = new CancellationTokenSource();
            bool changed = false;
            try
            {
                foreach (var g in _settings.Games.Where(g => !string.IsNullOrEmpty(g.SteamAppId) && string.IsNullOrEmpty(g.DisplayName)).ToList())
                {
                    if (cts.IsCancellationRequested) return;
                    var info = _steam.Peek(g.SteamAppId) ?? await _steam.GetAsync(g.SteamAppId, cts.Token);
                    if (info != null && info.Found && info.Name != g.Title)
                    {
                        g.DisplayName = info.Name; changed = true;
                        var row = _list.RowFor(g); if (row != null) _list.RefreshRow(row);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("获取 Steam 名称失败：" + ex.Message); }
            if (changed && !cts.IsCancellationRequested) SaveSettings();
        }

        // ------------------------------------------------------------------ menus

        private ContextMenuStrip NewMenu()
        {
            var m = new ContextMenuStrip { BackColor = Palette.Card, ForeColor = Palette.Text, ShowImageMargin = false, ShowCheckMargin = false, Font = Font };
            m.Renderer = new ToolStripProfessionalRenderer(new MenuColors());
            return m;
        }

        private void ShowProxyMenu(GameListView.Row row, Rectangle anchor)
        {
            var g = row.Game;
            var menu = NewMenu(); menu.ShowCheckMargin = true;
            foreach (var proxy in ProxyNames.All)
            {
                var isRecommended = proxy.Equals(g.RecommendedProxy, StringComparison.OrdinalIgnoreCase);
                var item = new ToolStripMenuItem(proxy + (isRecommended ? "      推荐" : "")) { Checked = proxy.Equals(g.SelectedProxy, StringComparison.OrdinalIgnoreCase) };
                item.Click += (s, e) =>
                {
                    if (g.SelectedProxy.Equals(proxy, StringComparison.OrdinalIgnoreCase)) return;
                    g.SelectedProxy = proxy; SaveSettings(); Bind(row);
                    if (_states.TryGetValue(g, out var st) && st.IsInstalled && !st.ProxyName.Equals(proxy, StringComparison.OrdinalIgnoreCase))
                        SetStatus("「" + g.Title + "」当前部署的是 " + st.ProxyName + "，点击「切换」后生效。", transient: true);
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("推荐依据：" + g.RecommendationReason) { Enabled = false });
            menu.Show(new Point(anchor.Left, anchor.Bottom + S(4)));
        }

        private void ShowAddMenu()
        {
            var menu = NewMenu();
            menu.Items.Add("添加扫描目录…", null, async (s, e) => await AddFolderAsync());
            menu.Items.Add("选择游戏 EXE…", null, (s, e) => AddExeDialog());
            if (_settings.ScanFolders.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
                foreach (var f in _settings.ScanFolders.ToList())
                {
                    var item = new ToolStripMenuItem("移除目录  " + f);
                    item.Click += (s, e) => { _settings.ScanFolders.Remove(f); SaveSettings(); Log("已移除扫描目录：" + f); };
                    menu.Items.Add(item);
                }
            }
            menu.Show(_btnAdd, new Point(0, _btnAdd.Height + S(4)));
        }

        private void ShowMoreMenu()
        {
            var menu = NewMenu();
            menu.Items.Add("导入本地补丁目录…", null, (s, e) => ImportLocal());
            if (!string.IsNullOrEmpty(_settings.LocalPackageDirectory))
                menu.Items.Add("停用本地补丁（改用上游）", null, (s, e) => { _settings.LocalPackageDirectory = ""; SaveSettings(); UpdateSubtitle(); Log("已停用本地补丁。"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("全部勾选", null, (s, e) => { foreach (var g in _settings.Games) g.Selected = true; _list.RefreshAll(); UpdateSelectionSummary(); SaveSettings(); });
            menu.Items.Add("全部取消勾选", null, (s, e) => { foreach (var g in _settings.Games) g.Selected = false; _list.RefreshAll(); UpdateSelectionSummary(); SaveSettings(); });
            menu.Items.Add("刷新状态", null, (s, e) => BindAll());
            menu.Items.Add(new ToolStripSeparator());
            var theme = new ToolStripMenuItem("主题");
            foreach (var (label, value) in new[] { ("跟随系统", "system"), ("浅色", "light"), ("深色", "dark") })
            {
                var item = new ToolStripMenuItem(label) { Checked = _settings.Theme == value };
                item.Click += (s, e) => ChangeTheme(value);
                theme.DropDownItems.Add(item);
            }
            theme.DropDown.Renderer = menu.Renderer; theme.DropDown.BackColor = Palette.Card; theme.DropDown.ForeColor = Palette.Text;
            menu.Items.Add(theme);
            menu.Items.Add("打开数据目录", null, (s, e) => OpenPath(_dataDir));
            menu.Items.Add("打开备份目录", null, (s, e) => OpenPath(_installer.BackupsDirectory));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关于", null, (s, e) => MessageBox.Show(this,
                "DLSSFG Manager " + typeof(MainForm).Assembly.GetName().Version?.ToString(3) +
                "\n\nPowered by sdli1995 — DLSS Frame Generation for SM86 (RTX 20/30)\n" + UpstreamUrl +
                "\n\nDesigned by zeer\n" + ClientUrl +
                "\n\n游戏名称来自 Steam 商店（本地缓存），图标来自游戏 EXE 或 Steam 缓存。\n安装状态仅表示文件已部署；帧生成是否生效需启动游戏验证。", "关于", MessageBoxButtons.OK, MessageBoxIcon.Information));
            menu.Show(_btnMore, new Point(_btnMore.Width - menu.Width, _btnMore.Height + S(4)));
        }

        private void ShowRowMenu(GameListView.Row row, Point at)
        {
            var g = row.Game;
            var menu = NewMenu();
            if (g.CandidateExes.Count > 1) menu.Items.Add("选择实际渲染 EXE…", null, (s, e) => ChooseExe(row));
            menu.Items.Add("打开游戏目录", null, (s, e) => OpenPath(g.DirectoryPath));
            var ini = Path.Combine(g.DirectoryPath, InstallerService.IniName);
            menu.Items.Add("打开 dlssg_sm86.ini", null, (s, e) => OpenPath(ini)).Enabled = File.Exists(ini);
            menu.Items.Add("打开日志目录", null, (s, e) => OpenLogs(g)).Enabled = File.Exists(ini);
            menu.Items.Add("恢复最近备份…", null, async (s, e) => await RestoreAsync(row)).Enabled = _installer.ListBackups(g).Count > 0;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("从列表移除", null, (s, e) => RemoveGame(g));
            menu.Show(at);
        }

        // ------------------------------------------------------------------ scan / add

        private async Task ScanAsync(IEnumerable<string> onlyRoots)
        {
            if (_busy) return;
            var roots = onlyRoots?.ToList() ?? _settings.ScanFolders.ToList();
            bool includeSteam = onlyRoots == null;
            _scanCts = new CancellationTokenSource();
            SetBusy(true, "扫描中…"); _btnCancel.Visible = true; ShowProgress(true);
            var progress = new Progress<string>(m => SetStatus(m));
            string outcome;
            try
            {
                var result = await Task.Run(() => _scanner.Scan(roots, includeSteam, _scanCts.Token, progress));
                foreach (var w in result.Warnings) Log("提示：" + w);
                MergeGames(result.Games, "扫描完成");
                int ambiguous = _settings.Games.Count(g => g.CandidateExes.Count > 1 && g.LocatedBy != "用户选择");
                outcome = "扫描完成，共 " + _settings.Games.Count + " 个游戏。" + (ambiguous > 0 ? " 有 " + ambiguous + " 个需要确认实际 EXE（右键该行）。" : "");
            }
            catch (OperationCanceledException) { outcome = "扫描已取消。"; }
            catch (Exception ex) { Log("扫描失败：" + ex.Message); outcome = "扫描失败：" + ex.Message; }
            _btnCancel.Visible = false; ShowProgress(false); _scanCts = null;
            SetBusy(false, outcome);
        }

        private async Task AddFolderAsync()
        {
            using (var dlg = new FolderBrowserDialog { Description = "选择游戏目录，或放了多个游戏的文件夹", ShowNewFolderButton = false, UseDescriptionForTitle = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!_settings.ScanFolders.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase)) { _settings.ScanFolders.Add(dlg.SelectedPath); SaveSettings(); }
                await ScanAsync(new[] { dlg.SelectedPath });
            }
        }

        private void AddExeDialog()
        {
            using (var dlg = new OpenFileDialog { Filter = "游戏可执行文件 (*.exe)|*.exe", Title = "选择游戏实际运行的 EXE（渲染进程）" })
                if (dlg.ShowDialog(this) == DialogResult.OK) AddExe(dlg.FileName);
        }

        private void AddExe(string path)
        {
            try { MergeGames(new[] { _scanner.FromExecutable(path) }, "添加 EXE"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法添加", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private async void OnDropped(object sender, DragEventArgs e)
        {
            if (!(e.Data?.GetData(DataFormats.FileDrop) is string[] paths)) return;
            var dirs = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) dirs.Add(p);
                else if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) AddExe(p);
            }
            if (dirs.Count > 0) await ScanAsync(dirs);
        }

        private void ChooseExe(GameListView.Row row)
        {
            var g = row.Game;
            using (var dlg = new Form { Text = "选择实际渲染 EXE — " + g.Title, Size = S(new Size(720, 360)), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, BackColor = Palette.Window, ForeColor = Palette.Text, Font = Font })
            {
                var list = new ListBox { Dock = DockStyle.Fill, BackColor = Palette.Card, ForeColor = Palette.Text, BorderStyle = BorderStyle.None, IntegralHeight = false, Font = Font };
                foreach (var c in g.CandidateExes) list.Items.Add(c);
                list.SelectedIndex = Math.Max(0, g.CandidateExes.FindIndex(c => PathUtil.SamePath(c, g.ExePath)));
                var hint = new Label { Dock = DockStyle.Top, Height = S(40), Text = "补丁会安装到所选 EXE 的同目录。UE 游戏通常是 *-Win64-Shipping.exe；启动器和崩溃报告程序已被排除。", ForeColor = Palette.Muted, Padding = new Padding(S(12), S(10), S(12), 0), BackColor = Palette.Window };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = S(52), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(S(12), S(10), S(12), 0), BackColor = Palette.Window };
                var ok = new FlatButton { Text = "确定", Kind = FlatButton.Role.Primary, Size = S(new Size(90, 32)), DialogResult = DialogResult.OK };
                var cancel = new FlatButton { Text = "取消", Size = S(new Size(90, 32)), DialogResult = DialogResult.Cancel, Margin = new Padding(S(8), 0, 0, 0) };
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
                list.DoubleClick += (s, e) => { if (list.SelectedIndex >= 0) dlg.DialogResult = DialogResult.OK; };
                dlg.Controls.Add(list); dlg.Controls.Add(hint); dlg.Controls.Add(buttons); dlg.AcceptButton = ok; dlg.CancelButton = cancel;
                if (dlg.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0) return;
                var chosen = g.CandidateExes[list.SelectedIndex];
                var key = PathUtil.DirectoryKey(chosen);
                if (_settings.Games.Any(x => x != g && PathUtil.DirectoryKey(x.ExePath) == key)) { MessageBox.Show(this, "列表中已有指向同一目录的游戏。", "重复", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                g.ExePath = PathUtil.ActualCase(chosen); g.LocatedBy = "用户选择";
                ProxyRecommender.Apply(g, g.SelectedProxy);
                SaveSettings(); Bind(row); LoadIconAsync(row);
            }
        }

        private void RemoveGame(GameEntry g)
        {
            if (MessageBox.Show(this, "从列表移除「" + g.Title + "」？不会改动游戏目录里的任何文件。", "移除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _settings.Games.Remove(g); SaveSettings(); RebuildRows(); UpdateSubtitle();
        }

        // ------------------------------------------------------------------ patch sources

        private void ImportLocal()
        {
            using (var dlg = new FolderBrowserDialog { Description = "选择本地补丁目录（上游仓库或发布包解压目录，须包含 dlssg_sm86.ini）", ShowNewFolderButton = false, UseDescriptionForTitle = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var probe = LocalPackage.Open(dlg.SelectedPath, "version.dll", _installer.KnownHashes);
                    _settings.LocalPackageDirectory = dlg.SelectedPath; SaveSettings(); UpdateSubtitle();
                    Log("已导入本地补丁目录：" + dlg.SelectedPath + "（version.dll 识别为：" + probe.Tag + "）。安装时将优先使用它。");
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "本地补丁无效", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        private async Task<bool> CheckUpdatesAsync(bool verbose)
        {
            if (_busy) return false;
            SetBusy(true, "正在查询 GitHub 最新 Release…"); ShowProgress(true);
            bool ok = await FetchReleaseAsync(verbose);
            ShowProgress(false);
            SetBusy(false, ok ? "上游最新版本 " + _release.Tag + "。" : null);
            return ok;
        }

        private async Task<bool> FetchReleaseAsync(bool verbose)
        {
            SetStatus("正在查询 GitHub 最新 Release…");
            try
            {
                _release = await _updater.CheckLatestAsync(CancellationToken.None);
                Log("最新稳定 Release：" + _release.Tag + "，提交 " + _release.Commit.Substring(0, 7) + "。");
                BindAll();
                return true;
            }
            catch (Exception ex)
            {
                Log("检查更新失败：" + ex.Message);
                if (_release != null) Log("将继续使用已缓存的 Release " + _release.Tag + "（检查于 " + _release.CheckedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "）。");
                if (verbose) MessageBox.Show(this, ex.Message, "检查更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private async Task<PatchPayload> GetPayloadAsync(string proxy, bool preferUpstream, Dictionary<string, PatchPayload> memo)
        {
            if (memo.TryGetValue(proxy, out var cached)) return cached;
            PatchPayload payload = null;
            if (!preferUpstream && !string.IsNullOrEmpty(_settings.LocalPackageDirectory))
            {
                try { payload = LocalPackage.Open(_settings.LocalPackageDirectory, proxy, _installer.KnownHashes); Log("使用本地补丁 " + proxy + "（" + payload.Tag + "）。"); }
                catch (Exception ex) { Log("本地补丁不可用（" + ex.Message + "），改用上游。"); }
            }
            if (payload == null)
            {
                if (_release == null || preferUpstream)
                    if (!await FetchReleaseAsync(false) && _release == null) throw new IOException("无法获取上游 Release，也没有本地补丁；请检查网络或导入本地补丁。");
                payload = _updater.LoadCachedPayload(_release, proxy);
                if (payload != null) Log("使用已校验缓存 " + proxy + "（" + _release.Tag + "）。");
                else
                {
                    var progress = new Progress<TransferProgress>(tp =>
                    {
                        int pct = tp.Total > 0 ? (int)Math.Min(100, tp.Received * 100 / Math.Max(1, tp.Total)) : 0;
                        SetStatus(tp.Message + (tp.Total > 0 ? "  " + pct + "%" : ""));
                        if (tp.Total > 0) ShowProgress(true, false, pct);
                    });
                    payload = await _updater.DownloadAsync(_release, proxy, CancellationToken.None, progress);
                    ShowProgress(true);
                    foreach (var kv in _updater.KnownHashes()) _installer.KnownHashes[kv.Key] = kv.Value;
                    Log("已下载并校验 " + proxy + "（" + _release.Tag + "，SHA-256 " + payload.Sha256.Substring(0, 12) + "…）。");
                }
            }
            memo[proxy] = payload;
            return payload;
        }

        // ------------------------------------------------------------------ operations

        private async Task RunBatchAsync(string op, IEnumerable<GameEntry> games)
        {
            if (_busy) return;
            var targets = games.ToList();
            if (targets.Count == 0) { SetStatus("请先勾选要操作的游戏。", transient: true); return; }
            var ambiguous = targets.Where(g => g.CandidateExes.Count > 1 && g.LocatedBy != "用户选择").ToList();
            if (ambiguous.Count > 0 && MessageBox.Show(this, "以下游戏检测到多个可执行文件，尚未确认实际渲染 EXE：\n\n" + string.Join("\n", ambiguous.Select(g => g.Title)) + "\n\n仍按当前选择继续？", "请确认 EXE", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            if (op == "uninstall" && MessageBox.Show(this, "将备份并移除 " + targets.Count + " 个游戏的补丁 DLL 与 dlssg_sm86.ini。继续？", "卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            string verb = op == "install" ? "安装" : op == "update" ? "更新" : "卸载";
            SetBusy(true, verb + "中…"); ShowProgress(true);
            foreach (var r in _list.Rows) if (targets.Contains(r.Game)) { r.Busy = true; _list.RefreshRow(r); }
            var memo = new Dictionary<string, PatchPayload>(StringComparer.OrdinalIgnoreCase);
            int ok = 0, failed = 0, skipped = 0;
            foreach (var g in targets)
            {
                SetStatus(verb + "：" + g.Title);
                var row = _list.RowFor(g);
                try
                {
                    _states.TryGetValue(g, out var st);
                    if (op == "uninstall")
                    {
                        var r = await Task.Run(() => _installer.Uninstall(g));
                        Log((r.Success ? "✓ " : "· ") + g.Title + "：" + r.Message); if (r.Success) ok++; else skipped++;
                    }
                    else
                    {
                        string proxy = g.SelectedProxy;
                        if (op == "update")
                        {
                            if (st == null || !st.IsInstalled) { Log("· " + g.Title + "：未安装，跳过更新。"); skipped++; continue; }
                            if (st.IsAmbiguous) { Log("· " + g.Title + "：存在多个代理 DLL，请先确认当前代理。"); skipped++; continue; }
                            proxy = st.ProxyName;
                        }
                        var payload = await GetPayloadAsync(proxy, op == "update", memo);
                        if (op == "update" && st.Version == payload.Tag && !st.IsModified) { Log("· " + g.Title + "：已是 " + payload.Tag + "，无需更新。"); skipped++; continue; }
                        var r = await Task.Run(() => _installer.Install(g, payload));
                        Log("✓ " + g.Title + "：" + r.Message); ok++;
                    }
                }
                catch (Exception ex) { Log("✗ " + g.Title + "：" + ex.Message); failed++; }
                finally { if (row != null) { row.Busy = false; Bind(row); } }
            }
            foreach (var r in _list.Rows) if (r.Busy) { r.Busy = false; _list.RefreshRow(r); }
            ShowProgress(false);
            SetBusy(false, verb + "完成：成功 " + ok + "，失败 " + failed + "，跳过 " + skipped + "。");
            if (failed > 0 && (_logPanel == null || !_logPanel.Visible)) ToggleLog();
        }

        private async Task RestoreAsync(GameListView.Row row)
        {
            var g = row.Game; if (_busy) return;
            var backups = _installer.ListBackups(g);
            if (backups.Count == 0) { SetStatus("没有该游戏的备份。", transient: true); return; }
            if (MessageBox.Show(this, "恢复「" + g.Title + "」最近一次备份（" + Path.GetFileName(backups[0]) + "）？\n当前文件会先再备份一次。", "恢复备份", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            SetBusy(true, "恢复中…");
            string outcome;
            try { var r = await Task.Run(() => _installer.RestoreLastBackup(g)); Log((r.Success ? "✓ " : "· ") + g.Title + "：" + r.Message); outcome = r.Message; }
            catch (Exception ex) { Log("✗ " + g.Title + "：" + ex.Message); outcome = "恢复失败：" + ex.Message; }
            Bind(row);
            SetBusy(false, outcome);
        }

        // ------------------------------------------------------------------ misc

        private void ChangeTheme(string value)
        {
            if (_settings.Theme == value) return;
            _settings.Theme = value; SaveSettings();
            if (MessageBox.Show(this, "主题在重新启动后生效。现在重启管理器？", "主题", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Program.ReleaseSingleInstance();
                Application.Restart();
            }
        }

        private void OpenLogs(GameEntry g)
        {
            var ini = Path.Combine(g.DirectoryPath, InstallerService.IniName);
            var dir = IniReader.LogDirectory(ini) ?? Path.Combine(g.DirectoryPath, "dlssg_sm86", "logs");
            if (!Directory.Exists(dir)) { SetStatus("日志目录尚不存在（游戏运行后才会创建）：" + dir, transient: true); return; }
            OpenPath(dir);
        }

        private void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("无法打开：" + ex.Message, transient: true); }
        }

        /// <summary>Enter/leave the busy state. A status given on leave stays for a few seconds, then the subtitle returns to normal.</summary>
        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            foreach (var b in new[] { _btnScan, _btnAdd, _btnCheck, _btnMore }) b.Enabled = !busy;
            UseWaitCursor = busy;
            UpdateSelectionSummary();
            if (status != null) SetStatus(status, transient: !busy);
            else if (!busy) UpdateSubtitle();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _statusTimer.Dispose(); _updater.Dispose(); _steam.Dispose(); _icons.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Menu colors matching the card palette.</summary>
    internal sealed class MenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Palette.ButtonHover;
        public override Color MenuItemBorder => Palette.Line;
        public override Color MenuBorder => Palette.Line;
        public override Color ToolStripDropDownBackground => Palette.Card;
        public override Color ImageMarginGradientBegin => Palette.Card;
        public override Color ImageMarginGradientMiddle => Palette.Card;
        public override Color ImageMarginGradientEnd => Palette.Card;
        public override Color SeparatorDark => Palette.Line;
        public override Color SeparatorLight => Palette.Line;
        public override Color MenuItemSelectedGradientBegin => Palette.ButtonHover;
        public override Color MenuItemSelectedGradientEnd => Palette.ButtonHover;
        public override Color MenuItemPressedGradientBegin => Palette.ButtonHover;
        public override Color MenuItemPressedGradientEnd => Palette.ButtonHover;
        public override Color CheckBackground => Palette.ButtonHover;
        public override Color CheckSelectedBackground => Palette.ButtonHover;
        public override Color CheckPressedBackground => Palette.ButtonHover;
    }

    internal static class IniReader
    {
        /// <summary>[Logging] Directory, resolved relative to the INI's folder; null when the INI is absent or the key is missing.</summary>
        public static string LogDirectory(string iniPath)
        {
            if (!File.Exists(iniPath)) return null;
            string section = "";
            foreach (var raw in File.ReadAllLines(iniPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[') { section = line.Trim('[', ']').Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq < 0 || !section.Equals("Logging", StringComparison.OrdinalIgnoreCase)) continue;
                if (!line.Substring(0, eq).Trim().Equals("Directory", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line.Substring(eq + 1).Trim().Trim('"');
                if (value.Length == 0) return null;
                return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(iniPath) ?? "", value));
            }
            return null;
        }
    }
}
