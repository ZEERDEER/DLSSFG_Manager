using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Sm86.Manager
{
    public sealed class GameScanner
    {
        /// <summary>Override for tests: returns Steam library roots (directories containing "steamapps") instead of reading the registry.</summary>
        public Func<IEnumerable<string>> SteamRootProvider { get; set; }

        private static readonly Regex ExcludedExeName = new Regex(
            @"(launcher|crash|report|unins|setup|install|redist|vc_redist|dxsetup|ue4prereq|uecc|eac|easyanticheat|battleye|unitycrashhandler|cefsharp|helper|updater|benchmark|config|tool|editor|server|bootstrap|watchdog|sentry|webhelper)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] ExcludedDirNames = { "_commonredist", "redist", "redistributables", "engine", "support", "prerequisites", "thirdparty", "third_party", "tools", "installer", "dotnet", "directx", "vcredist", "easyanticheat", "battleye" };
        private const int MaxDepth = 4;

        public ScanResult Scan(IEnumerable<string> customRoots, bool includeSteam, CancellationToken cancellationToken, IProgress<string> progress = null)
        {
            var result = new ScanResult();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(GameEntry entry)
            {
                if (entry == null) return;
                var key = PathUtil.DirectoryKey(entry.ExePath);
                if (!seen.Add(key)) return;
                result.Games.Add(entry);
            }

            if (includeSteam)
            {
                foreach (var library in SafeSteamLibraries(result.Warnings))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report("扫描 Steam 库：" + library);
                    foreach (var manifest in SafeEnumerateFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var kv = ReadVdf(File.ReadAllText(manifest, Encoding.UTF8));
                            string appId = Get(kv, "appid"), name = Get(kv, "name"), installDir = Get(kv, "installdir");
                            if (string.IsNullOrEmpty(installDir)) continue;
                            var root = Path.Combine(library, "steamapps", "common", installDir);
                            if (!Directory.Exists(root)) continue;
                            var entry = Locate(root, name, appId, cancellationToken);
                            if (entry != null) Add(entry);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            result.Warnings.Add("无法读取清单 " + manifest + "：" + ex.Message);
                        }
                    }
                }
            }

            foreach (var root in (customRoots ?? Enumerable.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) { result.Warnings.Add("目录不存在：" + root); continue; }
                progress?.Report("扫描目录：" + root);
                foreach (var entry in LocateCustomRoot(root, cancellationToken)) Add(entry);
            }
            return result;
        }

        /// <summary>A custom root may be one game or a folder of games; decide which and return entries accordingly.</summary>
        public List<GameEntry> LocateCustomRoot(string root, CancellationToken cancellationToken)
        {
            root = PathUtil.Normalize(root);
            var rootName = Path.GetFileName(root);
            var self = Locate(root, rootName, "", cancellationToken);
            var subs = new List<GameEntry>();
            foreach (var sub in SafeEnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsExcludedDir(sub)) continue;
                var entry = Locate(sub, Path.GetFileName(sub), "", cancellationToken);
                if (entry != null) subs.Add(entry);
            }
            if (self == null) return subs;
            bool exeDirectlyInRoot = PathUtil.SamePath(Path.GetDirectoryName(self.ExePath), root);
            bool ueLayout = Directory.Exists(Path.Combine(root, "Engine")) && self.ExePath.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase);
            bool knownGame = KnownGames.Match(self) != null;
            if (exeDirectlyInRoot || ueLayout || knownGame || subs.Count <= 1 && self.CandidateExes.Count == 0)
            {
                // Single game: keep root naming, but if the only sub found the same EXE use whichever name is more specific.
                if (subs.Count == 1 && PathUtil.SamePath(subs[0].ExePath, self.ExePath) && !ueLayout && !knownGame && !exeDirectlyInRoot)
                    return subs;
                return new List<GameEntry> { self };
            }
            return subs;
        }

        /// <summary>Wrap one executable picked by the user (or dropped on the window).</summary>
        public GameEntry FromExecutable(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) throw new FileNotFoundException("找不到 EXE：" + exePath);
            var pe = PeInspector.Read(exePath);
            if (pe == null) throw new InvalidDataException("不是有效的 Windows 可执行文件：" + exePath);
            if (pe.IsDll) throw new InvalidDataException("请选择 EXE 而不是 DLL：" + exePath);
            if (!pe.Is64Bit) throw new InvalidDataException("这是 32 位程序，本补丁只支持 64 位游戏：" + exePath);
            var known = KnownGames.ByExeName(exePath);
            var entry = new GameEntry
            {
                Name = known?.Name ?? Path.GetFileNameWithoutExtension(exePath),
                ExePath = Path.GetFullPath(exePath),
                InstallRoot = Path.GetDirectoryName(Path.GetFullPath(exePath)),
                SteamAppId = known?.SteamAppId ?? "",
                LocatedBy = "手动选择",
            };
            return Finish(entry);
        }

        /// <summary>Find the rendering executable inside an install root. Returns null when nothing usable exists.</summary>
        public GameEntry Locate(string installRoot, string name, string appId, CancellationToken cancellationToken)
        {
            installRoot = PathUtil.Normalize(installRoot);
            var known = KnownGames.ByAppId(appId);
            if (known != null)
            {
                var exe = Path.Combine(installRoot, known.ExeRelativePath.Replace('\\', Path.DirectorySeparatorChar));
                if (File.Exists(exe))
                    return Finish(new GameEntry { Name = known.Name, ExePath = exe, InstallRoot = installRoot, SteamAppId = appId, LocatedBy = "已知游戏规则" });
            }
            var candidates = new List<Candidate>();
            Collect(installRoot, installRoot, 0, candidates, cancellationToken);
            if (candidates.Count == 0) return null;
            var ranked = candidates.OrderByDescending(c => c.Score).ThenByDescending(c => c.Size).ToList();
            var best = ranked[0];
            var entry = new GameEntry
            {
                Name = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(best.Path) : name,
                ExePath = best.Path,
                InstallRoot = installRoot,
                SteamAppId = appId ?? "",
                LocatedBy = best.Reason,
            };
            var strong = ranked.Where(c => c.Score >= best.Score - 10).ToList();
            if (strong.Count > 1) entry.CandidateExes = strong.Select(c => c.Path).ToList();
            var byName = KnownGames.ByExeName(best.Path);
            if (byName != null && string.IsNullOrEmpty(entry.SteamAppId)) { entry.SteamAppId = byName.SteamAppId; entry.Name = byName.Name; }
            return Finish(entry);
        }

        private static GameEntry Finish(GameEntry entry)
        {
            entry.ExePath = PathUtil.ActualCase(entry.ExePath);
            entry.InstallRoot = PathUtil.ActualCase(entry.InstallRoot);
            ProxyRecommender.Apply(entry);
            return entry;
        }

        private sealed class Candidate { public string Path; public int Score; public long Size; public string Reason; }

        private void Collect(string root, string dir, int depth, List<Candidate> list, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in SafeEnumerateFiles(dir, "*.exe"))
            {
                var fileName = Path.GetFileName(file);
                if (ExcludedExeName.IsMatch(Path.GetFileNameWithoutExtension(fileName))) continue;
                var pe = PeInspector.Read(file);
                if (pe == null || pe.IsDll || !pe.Is64Bit) continue;
                long size = new FileInfo(file).Length;
                int score = 0; string reason = "唯一候选";
                var rel = file.Substring(root.Length).Replace('/', '\\');
                if (fileName.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)) { score += 100; reason = "UE Shipping 可执行文件"; }
                else if (rel.IndexOf(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase) >= 0) { score += 60; reason = "UE Binaries\\Win64 目录"; }
                else if (rel.IndexOf(@"\bin\x64\", StringComparison.OrdinalIgnoreCase) >= 0 || rel.IndexOf(@"\x64\", StringComparison.OrdinalIgnoreCase) >= 0) { score += 40; reason = "x64 子目录"; }
                if (KnownGames.ByExeName(file) != null) { score += 200; reason = "已知游戏 EXE 名称"; }
                if (depth == 0) score += 5;
                if (size > 20L * 1024 * 1024) score += 10;
                var rootName = Path.GetFileName(root);
                if (!string.IsNullOrEmpty(rootName) && Path.GetFileNameWithoutExtension(fileName).IndexOf(rootName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) >= 0) score += 15;
                list.Add(new Candidate { Path = file, Score = score, Size = size, Reason = reason });
            }
            if (depth >= MaxDepth) return;
            foreach (var sub in SafeEnumerateDirectories(dir))
            {
                if (IsExcludedDir(sub)) continue;
                Collect(root, sub, depth + 1, list, ct);
            }
        }

        private static bool IsExcludedDir(string dir)
        {
            var name = Path.GetFileName(dir).ToLowerInvariant();
            return ExcludedDirNames.Contains(name) || name.StartsWith(".");
        }

        // ---- Steam ----

        private IEnumerable<string> SafeSteamLibraries(List<string> warnings)
        {
            var libraries = new List<string>();
            try
            {
                var roots = (SteamRootProvider ?? SteamInstallRoots)().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var root in roots)
                {
                    libraries.Add(root);
                    var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                    if (!File.Exists(vdf)) vdf = Path.Combine(root, "config", "libraryfolders.vdf");
                    if (!File.Exists(vdf)) continue;
                    foreach (var path in ParseLibraryFolders(File.ReadAllText(vdf, Encoding.UTF8)))
                        if (Directory.Exists(path)) libraries.Add(PathUtil.Normalize(path));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                warnings.Add("读取 Steam 库列表失败：" + ex.Message);
            }
            if (libraries.Count == 0) warnings.Add("未找到 Steam 安装；可手动添加目录。");
            return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Steam client install roots (registry, then the default Program Files location). Empty on non-Windows.</summary>
        public static IEnumerable<string> SteamInstallRoots()
        {
            var list = new List<string>();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return list;
#pragma warning disable CA1416 // guarded by the platform check above
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var p = key?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(p)) list.Add(p.Replace('/', '\\'));
                }
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    var p = key?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(p)) list.Add(p);
                }
            }
            catch (Exception) { }
#pragma warning restore CA1416
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf86)) list.Add(Path.Combine(pf86, "Steam"));
            return list;
        }

        /// <summary>Extract every library path from libraryfolders.vdf (supports both the old "N" "path" and the new nested "path" layout).</summary>
        public static List<string> ParseLibraryFolders(string vdf)
        {
            var paths = new List<string>();
            foreach (var pair in VdfPairs(vdf))
            {
                bool keyOk = pair.Key.Equals("path", StringComparison.OrdinalIgnoreCase) || pair.Key.All(char.IsDigit);
                if (keyOk && pair.Value.Length > 1 && (pair.Value.Contains("\\") || pair.Value.Contains("/"))) paths.Add(pair.Value);
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Flat key/value read of a VDF/ACF file: first occurrence of each key wins.</summary>
        public static Dictionary<string, string> ReadVdf(string text)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in VdfPairs(text)) if (!dict.ContainsKey(pair.Key)) dict[pair.Key] = pair.Value;
            return dict;
        }

        /// <summary>Tokenize VDF into quoted strings and braces; a string followed by a string is a key/value pair.</summary>
        private static IEnumerable<KeyValuePair<string, string>> VdfPairs(string text)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '"')
                {
                    var sb = new StringBuilder(); i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) { i++; sb.Append(text[i] == 'n' ? '\n' : text[i] == 't' ? '\t' : text[i]); }
                        else sb.Append(text[i]);
                        i++;
                    }
                    i++;
                    tokens.Add("\"" + sb);
                }
                else if (c == '{' || c == '}') { tokens.Add(c.ToString()); i++; }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; }
                else i++;
            }
            for (int t = 0; t + 1 < tokens.Count; t++)
            {
                if (tokens[t][0] == '"' && tokens[t + 1][0] == '"')
                {
                    yield return new KeyValuePair<string, string>(tokens[t].Substring(1), tokens[t + 1].Substring(1));
                    t++;
                }
            }
        }

        private static string Get(Dictionary<string, string> kv, string key) => kv.TryGetValue(key, out var v) ? v : "";

        private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
        {
            try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).ToList() : Enumerable.Empty<string>(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
        }
        private static IEnumerable<string> SafeEnumerateDirectories(string dir)
        {
            try { return Directory.EnumerateDirectories(dir).ToList(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
        }
    }
}
