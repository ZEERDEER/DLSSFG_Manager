using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Sm86.Manager.App
{
    /// <summary>Thin accent progress line: determinate (0-100) or an indeterminate sweep.</summary>
    public sealed class ProgressLine : Control
    {
        private readonly Timer _timer = new Timer { Interval = 30 };
        private int _value; private bool _indeterminate; private float _phase;

        public ProgressLine()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 3; Visible = false;
            _timer.Tick += (s, e) => { _phase = (_phase + 0.012f) % 1f; Invalidate(); };
        }

        public int Value { get => _value; set { _value = Math.Max(0, Math.Min(100, value)); _indeterminate = false; _timer.Stop(); Invalidate(); } }
        public bool Indeterminate { get => _indeterminate; set { _indeterminate = value; if (value) _timer.Start(); else _timer.Stop(); Invalidate(); } }

        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) _timer.Stop(); else if (_indeterminate) _timer.Start(); }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Dpi.Behind(this))) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = new SolidBrush(Palette.Line)) g.FillRectangle(track, ClientRectangle);
            using (var fill = new SolidBrush(Palette.Accent))
            {
                if (_indeterminate)
                {
                    int w = Math.Max(Dpi.S(this, 120), Width / 4);
                    int x = (int)((Width + w) * _phase) - w;
                    g.FillRectangle(fill, x, 0, w, Height);
                }
                else g.FillRectangle(fill, 0, 0, (int)(Width * _value / 100.0), Height);
            }
        }

        protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Floating activity log: header with actions and a read-only monospace text area, styled like a card.</summary>
    public sealed class LogPanel : Card
    {
        private readonly TextBox _text = new TextBox();
        private readonly Label _title = new Label();
        public event Action CloseRequested;

        public LogPanel(Font uiFont)
        {
            Font = uiFont;
            int S(int v) => Dpi.S(this, v);
            Padding = new Padding(S(2));
            var header = new Panel { Dock = DockStyle.Top, Height = S(40), BackColor = Color.Transparent, Padding = new Padding(S(14), 0, S(8), 0) };
            _title.Text = "活动日志"; _title.AutoSize = false; _title.Dock = DockStyle.Fill; _title.TextAlign = ContentAlignment.MiddleLeft; _title.Font = new Font(uiFont, FontStyle.Bold); _title.ForeColor = Palette.Text; _title.BackColor = Color.Transparent;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, S(4), 0, 0) };
            foreach (var (text, kind, action) in new (string, FlatButton.Role, Action)[]
            {
                ("关闭", FlatButton.Role.Subtle, () => CloseRequested?.Invoke()),
                ("清空", FlatButton.Role.Subtle, () => _text.Clear()),
                ("复制", FlatButton.Role.Subtle, () => { try { if (_text.TextLength > 0) Clipboard.SetText(_text.Text); } catch (Exception) { } }),
            })
            {
                var b = new FlatButton { Text = text, Kind = kind, Size = new Size(S(56), S(30)), Margin = new Padding(S(4), 0, 0, 0), Font = uiFont };
                b.Click += (s, e) => action();
                buttons.Controls.Add(b);
            }
            header.Controls.Add(_title); header.Controls.Add(buttons);
            var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Palette.Line };
            _text.Multiline = true; _text.ReadOnly = true; _text.ScrollBars = ScrollBars.Vertical; _text.Dock = DockStyle.Fill; _text.BorderStyle = BorderStyle.None;
            _text.Font = new Font("Consolas", 9.5f); _text.BackColor = Palette.Card; _text.ForeColor = Palette.Text; _text.WordWrap = true;
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(S(12), S(8), S(6), S(8)) };
            body.Controls.Add(_text);
            Controls.Add(body); Controls.Add(line); Controls.Add(header);
        }

        public void Append(string line)
        {
            _text.AppendText(line + Environment.NewLine);
        }

        public int LineCount => _text.Lines.Length;
    }
}
