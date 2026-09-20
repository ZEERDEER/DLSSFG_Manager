using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Sm86.Manager.App
{
    /// <summary>
    /// The whole game list drawn by one control with its own scrolling: no child windows per row, so the window paints
    /// atomically, and all coordinates are computed in client space (GDI text and GDI+ shapes stay aligned when scrolled).
    /// </summary>
    public sealed class GameListView : Control
    {
        public sealed class Row
        {
            public GameEntry Game;
            public InstalledState State;
            public Bitmap Icon;
            public string Version = "—", StatusText = "";
            public Color StatusDot = Color.Gray;
            public string PrimaryText = "安装", PrimaryAction = "install";
            public bool PrimaryIsAccent = true, ShowUninstall, Ambiguous, Busy;
            public string Tooltip = "";
        }

        private enum Part { None, Card, Check, Proxy, Primary, Uninstall, Thumb }

        private readonly List<Row> _rows = new List<Row>();
        private int _hoverRow = -1, _pressRow = -1;
        private Part _hoverPart = Part.None, _pressPart = Part.None;
        private readonly ToolTip _tip = new ToolTip { InitialDelay = 400, ReshowDelay = 200 };
        private string _tipShown = "";
        private Font _nameFont, _pathFont, _glyphFont;

        // scrolling
        private int _scrollY;
        private bool _thumbHover, _thumbDrag;
        private int _dragStartY, _dragStartScroll;

        public event Action<Row, bool> CheckedChanged;
        public event Action<Row, Rectangle> ProxyClicked;      // rectangle in screen coordinates (menu anchor)
        public event Action<Row, string> ActionRequested;      // "install" | "update" | "uninstall"
        public event Action<Row, Point> ContextMenuRequested;  // screen point

        public GameListView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Palette.Window; TabStop = true;
        }

        public IReadOnlyList<Row> Rows => _rows;

        public void SetRows(IEnumerable<Row> rows)
        {
            _rows.Clear(); _rows.AddRange(rows);
            _hoverRow = -1; _hoverPart = Part.None;
            ClampScroll(); Invalidate();
        }

        public void RefreshRow(Row row) { int i = _rows.IndexOf(row); if (i >= 0) Invalidate(CardRect(i)); }
        public void RefreshAll() => Invalidate();
        public Row RowFor(GameEntry game) => _rows.FirstOrDefault(r => r.Game == game);

        // ------------------------------------------------------------------ metrics

        private int S(int v) => Dpi.S(this, v);
        private int RowHeight => S(66);
        private int Gap => S(8);
        private int Pitch => RowHeight + Gap;
        private int TopPad => S(2);
        private int ContentHeight => _rows.Count == 0 ? 0 : TopPad + _rows.Count * Pitch - Gap + S(4);
        private int MaxScroll => Math.Max(0, ContentHeight - ClientSize.Height);
        private bool ScrollbarVisible => MaxScroll > 0;
        private int ScrollbarLane => S(14);

        protected override void OnResize(EventArgs e) { base.OnResize(e); ClampScroll(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); _nameFont?.Dispose(); _pathFont?.Dispose(); _glyphFont?.Dispose(); _nameFont = _pathFont = _glyphFont = null; }

        private Font NameFont => _nameFont ?? (_nameFont = new Font(Font.FontFamily, 11f, FontStyle.Bold));
        private Font PathFont => _pathFont ?? (_pathFont = new Font(Font.FontFamily, 8.5f));
        private Font GlyphFont => _glyphFont ?? (_glyphFont = new Font("Segoe MDL2 Assets", 8f));

        /// <summary>Card rectangle in client coordinates (scroll already applied).</summary>
        private Rectangle CardRect(int index)
        {
            int width = Math.Max(S(500), ClientSize.Width - (ScrollbarVisible ? ScrollbarLane : 0));
            return new Rectangle(0, TopPad + index * Pitch - _scrollY, width, RowHeight);
        }

        private struct Cells { public Rectangle Card, Check, Icon, Text, Version, Status, Proxy, Primary, Uninstall; }

        private Cells RowCells(int index)
        {
            var card = CardRect(index);
            var c = new Cells { Card = card };
            int pad = S(12), btnH = S(32), midY = card.Y + (card.Height - btnH) / 2;
            c.Check = new Rectangle(card.X + pad + S(4), card.Y + (card.Height - S(20)) / 2, S(20), S(20));
            c.Icon = new Rectangle(c.Check.Right + S(14), card.Y + (card.Height - S(40)) / 2, S(40), S(40));
            int right = card.Right - pad;
            c.Uninstall = new Rectangle(right - S(60), midY, S(60), btnH); right = c.Uninstall.X - S(8);
            c.Primary = new Rectangle(right - S(70), midY, S(70), btnH); right = c.Primary.X - S(10);
            c.Proxy = new Rectangle(right - S(124), midY, S(124), btnH); right = c.Proxy.X - S(14);
            c.Status = new Rectangle(right - S(124), card.Y, S(124), card.Height); right = c.Status.X - S(6);
            c.Version = new Rectangle(right - S(76), card.Y, S(76), card.Height); right = c.Version.X - S(8);
            int textX = c.Icon.Right + S(14);
            c.Text = new Rectangle(textX, card.Y, Math.Max(S(60), right - textX), card.Height);
            return c;
        }

        // ------------------------------------------------------------------ scrolling

        private void ClampScroll()
        {
            int clamped = Math.Max(0, Math.Min(_scrollY, MaxScroll));
            if (clamped != _scrollY) { _scrollY = clamped; Invalidate(); }
        }

        private void ScrollTo(int y)
        {
            int clamped = Math.Max(0, Math.Min(y, MaxScroll));
            if (clamped == _scrollY) return;
            _scrollY = clamped;
            Invalidate();
            RefreshHover(PointToClient(MousePosition));
        }

        private Rectangle TrackRect => new Rectangle(ClientSize.Width - ScrollbarLane, S(2), ScrollbarLane, Math.Max(0, ClientSize.Height - S(4)));

        private Rectangle ThumbRect()
        {
            if (!ScrollbarVisible) return Rectangle.Empty;
            var track = TrackRect;
            int thumbH = Math.Max(S(28), (int)((long)track.Height * ClientSize.Height / Math.Max(1, ContentHeight)));
            int travel = Math.Max(0, track.Height - thumbH);
            int y = track.Y + (MaxScroll == 0 ? 0 : (int)((long)travel * _scrollY / MaxScroll));
            int w = _thumbHover || _thumbDrag ? S(8) : S(5);
            return new Rectangle(track.Right - S(4) - w, y, w, thumbH);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int lines = SystemInformation.MouseWheelScrollLines;
            int step = lines <= 0 ? ClientSize.Height : Math.Max(Pitch, lines * S(24));
            ScrollTo(_scrollY - Math.Sign(e.Delta) * step * Math.Max(1, Math.Abs(e.Delta) / 120));
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode) { case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End: return true; }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Down: ScrollTo(_scrollY + Pitch); e.Handled = true; break;
                case Keys.Up: ScrollTo(_scrollY - Pitch); e.Handled = true; break;
                case Keys.PageDown: ScrollTo(_scrollY + ClientSize.Height - Pitch); e.Handled = true; break;
                case Keys.PageUp: ScrollTo(_scrollY - ClientSize.Height + Pitch); e.Handled = true; break;
                case Keys.Home: ScrollTo(0); e.Handled = true; break;
                case Keys.End: ScrollTo(MaxScroll); e.Handled = true; break;
            }
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Palette.Window)) e.Graphics.FillRectangle(b, e.ClipRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int first = Math.Max(0, (e.ClipRectangle.Top + _scrollY - TopPad) / Pitch), last = Math.Min(_rows.Count - 1, (e.ClipRectangle.Bottom + _scrollY - TopPad) / Pitch);
            for (int i = first; i <= last; i++) DrawRow(g, i);
            DrawScrollbar(g);
        }

        private void DrawScrollbar(Graphics g)
        {
            if (!ScrollbarVisible) return;
            var thumb = ThumbRect();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = IconProvider.RoundedRect(thumb, thumb.Width / 2))
            using (var b = new SolidBrush(Color.FromArgb(_thumbDrag ? 200 : _thumbHover ? 160 : 90, Palette.Muted)))
                g.FillPath(b, path);
        }

        private void DrawRow(Graphics g, int i)
        {
            var row = _rows[i];
            var c = RowCells(i);
            bool hover = i == _hoverRow;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // card
            var cardRect = new Rectangle(c.Card.X, c.Card.Y, c.Card.Width - 1, c.Card.Height - 1);
            using (var path = IconProvider.RoundedRect(cardRect, S(8)))
            {
                using (var b = new SolidBrush(hover ? Palette.CardHover : Palette.Card)) g.FillPath(b, path);
                using (var p = new Pen(row.Ambiguous ? Palette.Accent : Palette.Line)) g.DrawPath(p, path);
            }

            // checkbox
            bool checkHot = hover && _hoverPart == Part.Check;
            using (var path = IconProvider.RoundedRect(c.Check, S(4)))
            {
                if (row.Game.Selected)
                {
                    using (var b = new SolidBrush(checkHot ? ControlPaint.Light(Palette.Accent, 0.15f) : Palette.Accent)) g.FillPath(b, path);
                    using (var p = new Pen(Palette.AccentText, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        var r = c.Check;
                        g.DrawLines(p, new[] { new Point(r.X + r.Width * 27 / 100, r.Y + r.Height * 52 / 100), new Point(r.X + r.Width * 44 / 100, r.Y + r.Height * 70 / 100), new Point(r.X + r.Width * 75 / 100, r.Y + r.Height * 32 / 100) });
                    }
                }
                else
                {
                    using (var b = new SolidBrush(checkHot ? Palette.ButtonHover : Palette.ButtonFace)) g.FillPath(b, path);
                    using (var p = new Pen(checkHot ? Palette.Muted : Palette.Line)) g.DrawPath(p, path);
                }
            }

            // icon
            if (row.Icon != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(row.Icon, c.Icon); }
            else
            {
                using (var path = IconProvider.RoundedRect(c.Icon, S(8))) using (var b = new SolidBrush(Palette.ButtonFace)) g.FillPath(b, path);
                var initial = (row.Game.Title.Length > 0 ? row.Game.Title.Substring(0, 1) : "?").ToUpperInvariant();
                TextRenderer.DrawText(g, initial, NameFont, c.Icon, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            // name + path
            var nameRect = new Rectangle(c.Text.X, c.Text.Y + S(12), c.Text.Width, S(24));
            var pathRect = new Rectangle(c.Text.X, c.Text.Y + S(36), c.Text.Width, S(18));
            const TextFormatFlags line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, row.Game.Title, NameFont, nameRect, Palette.Text, line);
            var pathText = (row.Ambiguous ? "⚠ 检测到多个 EXE，右键选择实际渲染 EXE  ·  " : "") + row.Game.ExePath;
            TextRenderer.DrawText(g, pathText, PathFont, pathRect, row.Ambiguous ? Palette.Warning : Palette.Muted, line | TextFormatFlags.PathEllipsis);

            // version + status
            TextRenderer.DrawText(g, row.Version, Font, c.Version, Palette.Text, line);
            int dot = S(8);
            using (var b = new SolidBrush(row.StatusDot)) g.FillEllipse(b, c.Status.X, c.Status.Y + (c.Status.Height - dot) / 2, dot, dot);
            TextRenderer.DrawText(g, row.StatusText, Font, new Rectangle(c.Status.X + dot + S(8), c.Status.Y, c.Status.Width - dot - S(8), c.Status.Height), Palette.Text, line);

            // proxy picker + actions
            DrawButton(g, c.Proxy, row.Game.SelectedProxy, false, Palette.Text, hover && _hoverPart == Part.Proxy, row.Busy, true);
            DrawButton(g, c.Primary, row.PrimaryText, row.PrimaryIsAccent, row.PrimaryIsAccent ? Palette.AccentText : Palette.Text, hover && _hoverPart == Part.Primary, row.Busy, false);
            if (row.ShowUninstall) DrawButton(g, c.Uninstall, "卸载", false, Palette.Danger, hover && _hoverPart == Part.Uninstall, row.Busy, false);
        }

        private void DrawButton(Graphics g, Rectangle r, string text, bool accent, Color textColor, bool hot, bool disabled, bool dropdown)
        {
            Color face = accent ? (hot ? ControlPaint.Light(Palette.Accent, 0.12f) : Palette.Accent) : (hot ? Palette.ButtonHover : Palette.ButtonFace);
            Color border = accent ? face : Palette.Line;
            if (disabled) { textColor = Palette.Muted; if (accent) face = Color.FromArgb(90, Palette.Accent); }
            var rect = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            using (var path = IconProvider.RoundedRect(rect, S(6)))
            {
                using (var b = new SolidBrush(face)) g.FillPath(b, path);
                using (var p = new Pen(border)) g.DrawPath(p, path);
            }
            if (dropdown)
            {
                var textRect = new Rectangle(rect.X + S(10), rect.Y, rect.Width - S(30), rect.Height);
                TextRenderer.DrawText(g, text, Font, textRect, textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, "\uE70D", GlyphFont, new Rectangle(rect.Right - S(24), rect.Y, S(20), rect.Height), Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
                TextRenderer.DrawText(g, text, Font, rect, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        // ------------------------------------------------------------------ hit testing / mouse

        private (int row, Part part) HitTest(Point p)
        {
            if (ScrollbarVisible && Rectangle.Inflate(ThumbRect(), S(6), 0).Contains(p)) return (-1, Part.Thumb);
            int index = (p.Y + _scrollY - TopPad) / Pitch;
            if (p.Y + _scrollY < TopPad || index < 0 || index >= _rows.Count) return (-1, Part.None);
            var c = RowCells(index);
            if (!c.Card.Contains(p)) return (-1, Part.None);
            var row = _rows[index];
            if (Rectangle.Inflate(c.Check, S(6), S(10)).Contains(p)) return (index, Part.Check);
            if (c.Proxy.Contains(p)) return (index, Part.Proxy);
            if (c.Primary.Contains(p)) return (index, Part.Primary);
            if (row.ShowUninstall && c.Uninstall.Contains(p)) return (index, Part.Uninstall);
            return (index, Part.Card);
        }

        private void RefreshHover(Point client)
        {
            if (_thumbDrag) return;
            var (row, part) = HitTest(client);
            bool thumb = part == Part.Thumb;
            if (thumb != _thumbHover) { _thumbHover = thumb; Invalidate(TrackRect); }
            if (row != _hoverRow || part != _hoverPart)
            {
                int old = _hoverRow; _hoverRow = row; _hoverPart = part;
                if (old >= 0 && old < _rows.Count) Invalidate(CardRect(old));
                if (row >= 0) Invalidate(CardRect(row));
                bool busy = row >= 0 && _rows[row].Busy;
                Cursor = !busy && (part == Part.Check || part == Part.Proxy || part == Part.Primary || part == Part.Uninstall) ? Cursors.Hand : Cursors.Default;
                UpdateTooltip(row, part);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_thumbDrag)
            {
                var track = TrackRect; int thumbH = ThumbRect().Height; int travel = Math.Max(1, track.Height - thumbH);
                ScrollTo(_dragStartScroll + (int)((long)(e.Y - _dragStartY) * MaxScroll / travel));
                return;
            }
            RefreshHover(e.Location);
        }

        private void UpdateTooltip(int row, Part part)
        {
            string text = "";
            if (row >= 0)
            {
                var r = _rows[row];
                if (part == Part.Proxy) text = r.Tooltip;
                else if (part == Part.Card && !string.IsNullOrEmpty(r.Game.ExePath)) text = r.Game.ExePath;
            }
            if (text == _tipShown) return;
            _tipShown = text;
            _tip.SetToolTip(this, text);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverRow >= 0 && _hoverRow < _rows.Count) Invalidate(CardRect(_hoverRow));
            _hoverRow = -1; _hoverPart = Part.None; Cursor = Cursors.Default; UpdateTooltip(-1, Part.None);
            if (_thumbHover && !_thumbDrag) { _thumbHover = false; Invalidate(TrackRect); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            (_pressRow, _pressPart) = HitTest(e.Location);
            if (_pressPart == Part.Thumb && e.Button == MouseButtons.Left)
            {
                _thumbDrag = true; _dragStartY = e.Y; _dragStartScroll = _scrollY; Capture = true; Invalidate(TrackRect);
            }
            else if (ScrollbarVisible && e.Button == MouseButtons.Left && TrackRect.Contains(e.Location))
            {
                // click in the track: page towards the click
                var thumb = ThumbRect();
                ScrollTo(_scrollY + (e.Y < thumb.Y ? -1 : 1) * Math.Max(Pitch, ClientSize.Height - Pitch));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_thumbDrag) { _thumbDrag = false; Capture = false; Invalidate(TrackRect); RefreshHover(e.Location); return; }
            var (row, part) = HitTest(e.Location);
            bool samePlace = row >= 0 && row == _pressRow && part == _pressPart;
            _pressRow = -1; _pressPart = Part.None;
            if (row < 0) return;
            var r = _rows[row];
            if (e.Button == MouseButtons.Right) { ContextMenuRequested?.Invoke(r, PointToScreen(e.Location)); return; }
            if (e.Button != MouseButtons.Left || !samePlace) return;
            switch (part)
            {
                case Part.Check:
                    r.Game.Selected = !r.Game.Selected; Invalidate(CardRect(row));
                    CheckedChanged?.Invoke(r, r.Game.Selected); break;
                case Part.Proxy:
                    if (!r.Busy) ProxyClicked?.Invoke(r, RectangleToScreen(RowCells(row).Proxy)); break;
                case Part.Primary:
                    if (!r.Busy) ActionRequested?.Invoke(r, r.PrimaryAction); break;
                case Part.Uninstall:
                    if (!r.Busy && r.ShowUninstall) ActionRequested?.Invoke(r, "uninstall"); break;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _tip.Dispose(); _nameFont?.Dispose(); _pathFont?.Dispose(); _glyphFont?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
