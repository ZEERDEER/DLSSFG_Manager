using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Sm86.Manager
{
    /// <summary>Portable settings stored in the data directory as settings.json; writes are atomic.</summary>
    public sealed class SettingsStore
    {
        private readonly string _path;
        public SettingsStore(string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("数据目录不能为空。", nameof(dataDirectory));
            Directory.CreateDirectory(dataDirectory);
            _path = Path.Combine(dataDirectory, "settings.json");
        }

        public string FilePath => _path;

        public AppSettings Load()
        {
            if (!File.Exists(_path)) return new AppSettings();
            try
            {
                var settings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(_path, Encoding.UTF8));
                if (settings == null) return new AppSettings();
                settings.ScanFolders = settings.ScanFolders ?? new System.Collections.Generic.List<string>();
                settings.Games = settings.Games ?? new System.Collections.Generic.List<GameEntry>();
                foreach (var game in settings.Games)
                    if (!ProxyNames.IsSupported(game.SelectedProxy)) game.SelectedProxy = "version.dll";
                return settings;
            }
            catch (JsonException ex)
            {
                throw new IOException("设置文件已损坏：" + _path + "（" + ex.Message + "）", ex);
            }
        }

        public void Save(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            AtomicFile.WriteAllText(_path, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }
    }

    internal static class AtomicFile
    {
        public static void WriteAllText(string path, string content)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            Replace(tmp, path);
        }

        /// <summary>Move tmp onto destination. Uses File.Replace when destination exists so the swap is a single rename.</summary>
        public static void Replace(string tmp, string destination)
        {
            if (File.Exists(destination))
            {
                try { File.Replace(tmp, destination, null, true); return; }
                catch (PlatformNotSupportedException) { }
                catch (IOException) when (!IsWindows) { }
                File.Delete(destination);
            }
            File.Move(tmp, destination);
        }

        private static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;
    }
}
