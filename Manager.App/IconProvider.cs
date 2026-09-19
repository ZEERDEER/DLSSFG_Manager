using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Sm86.Manager.App
{
    /// <summary>Offline game icons: Steam's local library cache first, then the icon embedded in the game EXE.</summary>
    public sealed class IconProvider : IDisposable
    {
        private readonly Dictionary<string, Bitmap> _memory = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private readonly List<string> _steamRoots;
        private readonly string _diskCache;
        public int Size { get; }

        public IconProvider(string dataDirectory, int size)
        {
            Size = size;
            _diskCache = Path.Combine(dataDirectory, "icons");
            Directory.CreateDirectory(_diskCache);
            try { _steamRoots = GameScanner.SteamInstallRoots().Where(Directory.Exists).ToList(); }
            catch (Exception) { _steamRoots = new List<string>(); }
        }

        /// <summary>Returns a cached bitmap sized Size×Size, or null when nothing usable exists. Safe to call from a worker thread.</summary>
        public Bitmap Get(GameEntry game)
        {
            var key = PathUtil.DirectoryKey(game.ExePath) + "|" + Path.GetFileName(game.ExePath).ToLowerInvariant();
            lock (_lock) { if (_memory.TryGetValue(key, out var cached)) return cached; }
            Bitmap bmp = null;
            var diskFile = Path.Combine(_diskCache, Hash(key) + "-" + Size + ".png");
            try { if (File.Exists(diskFile)) using (var img = Image.FromFile(diskFile)) bmp = new Bitmap(img); } catch (Exception) { bmp = null; }
            if (bmp == null)
            {
                // The EXE usually carries a 256 px icon; Steam's cached icon is only 32 px, so it is the fallback.
                bmp = FromExecutable(game.ExePath) ?? FromSteamCache(game.SteamAppId);
                if (bmp != null) { try { bmp.Save(diskFile, ImageFormat.Png); } catch (Exception) { } }
            }
            lock (_lock) { _memory[key] = bmp; }
            return bmp;
        }

        private Bitmap FromSteamCache(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return null;
            foreach (var root in _steamRoots)
            {
                var cache = Path.Combine(root, "appcache", "librarycache");
                try
                {
                    var legacy = Path.Combine(cache, appId + "_icon.jpg");
                    if (File.Exists(legacy)) { var b = LoadScaled(legacy); if (b != null) return b; }
                    var folder = Path.Combine(cache, appId);
                    if (Directory.Exists(folder))
                    {
                        // New layout: assets named by hash; the icon is the small square one.
                        foreach (var file in Directory.EnumerateFiles(folder, "*.jpg").Concat(Directory.EnumerateFiles(folder, "*.png")))
                        {
                            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                            if (name.StartsWith("library_") || name.StartsWith("header") || name.StartsWith("logo") || name.StartsWith("hero")) continue;
                            using (var img = Image.FromFile(file))
                            {
                                if (img.Width != img.Height || img.Width > 128) continue;
                                return Scale(img);
                            }
                        }
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        private Bitmap LoadScaled(string file) { try { using (var img = Image.FromFile(file)) return Scale(img); } catch (Exception) { return null; } }

        private Bitmap FromExecutable(string exePath)
        {
            if (!File.Exists(exePath)) return null;
            // Ask for the largest embedded icon and downscale: much sharper than the 32 px "associated" icon.
            foreach (var request in new[] { 256, 128, 64, 48 })
            {
                try
                {
                    using (var icon = ExtractIcon(exePath, request))
                    {
                        if (icon == null) continue;
                        using (var bmp = icon.ToBitmap())
                            if (bmp.Width >= Size || request == 48) return Scale(bmp);
                    }
                }
                catch (Exception) { }
            }
            try { using (var icon = Icon.ExtractAssociatedIcon(exePath)) using (var bmp = icon?.ToBitmap()) return bmp == null ? null : Scale(bmp); }
            catch (Exception) { return null; }
        }

        private Bitmap Scale(Image source)
        {
            var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                using (var path = RoundedRect(new Rectangle(0, 0, Size, Size), Size / 5))
                {
                    g.SetClip(path);
                    g.DrawImage(source, new Rectangle(0, 0, Size, Size));
                }
            }
            return bmp;
        }

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || r.Width < d || r.Height < d) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static string Hash(string s)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        // SHDefExtractIcon gives us a properly sized icon instead of the 32 px associated icon.
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHDefExtractIconW(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

        private static Icon ExtractIcon(string file, int size)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            uint packed = (uint)(size & 0xFFFF) | ((uint)(size & 0xFFFF) << 16);
            if (SHDefExtractIconW(file, 0, 0, out var large, out var small, packed) != 0 || large == IntPtr.Zero) return null;
            try { return (Icon)Icon.FromHandle(large).Clone(); }
            finally { DestroyIcon(large); if (small != IntPtr.Zero) DestroyIcon(small); }
        }

        public void Dispose()
        {
            lock (_lock) { foreach (var b in _memory.Values) b?.Dispose(); _memory.Clear(); }
        }
    }
}
