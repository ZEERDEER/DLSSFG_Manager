using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Sm86.Manager.App
{
    /// <summary>Colors derived from the active WinForms color mode plus the Windows accent color.</summary>
    public static class Palette
    {
        public static bool Dark => Application.IsDarkModeEnabled;
        public static Color Window => Dark ? Color.FromArgb(0x1F, 0x1F, 0x1F) : Color.FromArgb(0xF3, 0xF3, 0xF3);
        public static Color Card => Dark ? Color.FromArgb(0x2B, 0x2B, 0x2B) : Color.White;
        public static Color CardHover => Dark ? Color.FromArgb(0x32, 0x32, 0x32) : Color.FromArgb(0xFA, 0xFA, 0xFA);
        public static Color Line => Dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xE5, 0xE5, 0xE5);
        public static Color Text => Dark ? Color.FromArgb(0xF0, 0xF0, 0xF0) : Color.FromArgb(0x1B, 0x1B, 0x1B);
        public static Color Muted => Dark ? Color.FromArgb(0x9E, 0x9E, 0x9E) : Color.FromArgb(0x6B, 0x6B, 0x6B);
        public static Color Accent { get; } = ReadAccent();
        public static Color AccentText => Luma(Accent) > 0.6 ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.White;
        public static Color Success => Dark ? Color.FromArgb(0x6C, 0xCB, 0x7F) : Color.FromArgb(0x0F, 0x7B, 0x2E);
        public static Color Warning => Dark ? Color.FromArgb(0xF5, 0xC5, 0x5B) : Color.FromArgb(0x9A, 0x62, 0x00);
        public static Color Danger => Dark ? Color.FromArgb(0xFF, 0x8A, 0x80) : Color.FromArgb(0xC4, 0x2B, 0x1C);
        public static Color ButtonFace => Dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xFB, 0xFB, 0xFB);
        public static Color ButtonHover => Dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.FromArgb(0xF0, 0xF0, 0xF0);
        public static Color CloseHover => Color.FromArgb(0xC4, 0x2B, 0x1C);

        private static double Luma(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

        private static Color ReadAccent()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    if (key?.GetValue("AccentColor") is int abgr)
                    {
                        var c = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                        // Light or grey accents (typical for "automatic" accents from muted wallpapers) make poor button faces.
                        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
                        double saturation = max == 0 ? 0 : (max - min) / (double)max;
                        return Luma(c) > 0.7 || saturation < 0.2 ? Color.FromArgb(0x00, 0x67, 0xC0) : c;
                    }
                }
            }
            catch (Exception) { }
            return Color.FromArgb(0x00, 0x67, 0xC0);
        }
    }

    public static class Dpi
    {
        /// <summary>Scale a 96-dpi design value to the control's current DPI.</summary>
        public static int S(Control c, int value) => (int)Math.Round(value * (c?.DeviceDpi ?? 96) / 96.0);
        public static Padding S(Control c, Padding p) => new Padding(S(c, p.Left), S(c, p.Top), S(c, p.Right), S(c, p.Bottom));
        public static Size S(Control c, Size s) => new Size(S(c, s.Width), S(c, s.Height));

        /// <summary>First opaque ancestor color, so rounded corners blend with whatever is really behind a control.</summary>
        public static Color Behind(Control c)
        {
            for (var p = c.Parent; p != null; p = p.Parent) if (p.BackColor.A == 255) return p.BackColor;
            return Palette.Window;
        }
    }

    /// <summary>Rounded flat button. Fully self-painted (ButtonBase is marked Opaque, so the background must be drawn in OnPaint).</summary>
    public sealed class FlatButton : Button
    {
        public enum Role { Normal, Primary, Danger, Subtle, Close }
        private Role _role;
        private bool _hover;

        [DefaultValue(Role.Normal)]
        public Role Kind { get => _role; set { _role = value; Invalidate(); } }

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; TabStop = true; AutoSize = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaintBackground(PaintEventArgs e) { /* everything is painted in OnPaint */ }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(Dpi.Behind(this))) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Color face, text, border;
            switch (_role)
            {
                case Role.Primary: face = _hover ? ControlPaint.Light(Palette.Accent, 0.12f) : Palette.Accent; text = Palette.AccentText; border = face; break;
                case Role.Danger: face = _hover ? Palette.ButtonHover : Palette.ButtonFace; text = Palette.Danger; border = Palette.Line; break;
                case Role.Subtle: face = _hover ? Palette.ButtonHover : Color.Transparent; text = Palette.Text; border = Color.Transparent; break;
                case Role.Close: face = _hover ? Palette.CloseHover : Color.Transparent; text = _hover ? Color.White : Palette.Text; border = Color.Transparent; break;
                default: face = _hover ? Palette.ButtonHover : Palette.ButtonFace; text = Palette.Text; border = Palette.Line; break;
            }
            if (!Enabled) { text = Palette.Muted; if (_role == Role.Primary) face = Color.FromArgb(90, Palette.Accent); }
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = IconProvider.RoundedRect(rect, Dpi.S(this, 6)))
            {
                if (face.A > 0) using (var b = new SolidBrush(face)) g.FillPath(b, path);
                if (border.A > 0) using (var p = new Pen(border)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text, Font, rect, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(rect, -3, -3));
        }
    }

    /// <summary>Colored dot + text, drawn on the parent's background.</summary>
    public sealed class StatusPill : Control
    {
        private Color _dot = Color.Gray;
        public Color Dot { get => _dot; set { _dot = value; Invalidate(); } }
        public StatusPill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var bg = new SolidBrush(Dpi.Behind(this))) e.Graphics.FillRectangle(bg, ClientRectangle);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Dpi.S(this, 8), x = Dpi.S(this, 2);
            using (var b = new SolidBrush(_dot)) g.FillEllipse(b, x, Height / 2 - d / 2, d, d);
            int textX = x + d + Dpi.S(this, 6);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(textX, 0, Width - textX, Height), ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    }

    /// <summary>Rounded card surface with hover highlight; hosts the row layout.</summary>
    public class Card : Panel
    {
        private bool _hover;
        public bool Highlight { get; set; }
        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Card;
        }
        public void SetHover(bool value) { if (_hover == value) return; _hover = value; BackColor = value ? Palette.CardHover : Palette.Card; Invalidate(true); }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var bg = new SolidBrush(Dpi.Behind(this))) e.Graphics.FillRectangle(bg, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = IconProvider.RoundedRect(rect, Dpi.S(this, 8)))
            {
                using (var b = new SolidBrush(BackColor)) g.FillPath(b, path);
                using (var p = new Pen(Highlight ? Palette.Accent : Palette.Line)) g.DrawPath(p, path);
            }
        }
    }
}
