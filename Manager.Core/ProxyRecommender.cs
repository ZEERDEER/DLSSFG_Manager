using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sm86.Manager
{
    public sealed class KnownGame
    {
        public string Name;
        public string SteamAppId;
        /// <summary>Rendering EXE relative to the install root, using '\' separators.</summary>
        public string ExeRelativePath;
        public string DefaultProxy;
        public string ExeFileName { get { var i = ExeRelativePath.LastIndexOf('\\'); return i < 0 ? ExeRelativePath : ExeRelativePath.Substring(i + 1); } }
    }

    public static class KnownGames
    {
        public static readonly KnownGame[] All =
        {
            new KnownGame { Name = "剑星", SteamAppId = "3489700", ExeRelativePath = @"SB\Binaries\Win64\SB-Win64-Shipping.exe", DefaultProxy = "version.dll" },
            new KnownGame { Name = "赛博朋克 2077", SteamAppId = "1091500", ExeRelativePath = @"bin\x64\Cyberpunk2077.exe", DefaultProxy = "dxgi.dll" },
            new KnownGame { Name = "仁王 3", SteamAppId = "3681010", ExeRelativePath = @"Nioh3.exe", DefaultProxy = "winmm.dll" },
        };

        public static KnownGame ByAppId(string appId) =>
            string.IsNullOrEmpty(appId) ? null : All.FirstOrDefault(g => g.SteamAppId == appId);

        public static KnownGame ByExeName(string exePath)
        {
            var name = Path.GetFileName(exePath ?? "");
            return All.FirstOrDefault(g => g.ExeFileName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Match either by AppID or by rendering EXE file name plus the tail of its relative path.</summary>
        public static KnownGame Match(GameEntry game)
        {
            if (game == null) return null;
            var byId = ByAppId(game.SteamAppId);
            if (byId != null) return byId;
            var byExe = ByExeName(game.ExePath);
            if (byExe == null) return null;
            return PathUtil.Key(game.ExePath).EndsWith(PathUtil.Key(byExe.ExeRelativePath), StringComparison.Ordinal) ? byExe : null;
        }
    }

    public static class ProxyRecommender
    {
        /// <summary>Upstream preference order used to break ties when static evidence is equal.</summary>
        public static readonly string[] UpstreamOrder = { "version.dll", "winmm.dll", "dbghelp.dll", "dinput8.dll", "dxgi.dll", "d3d12.dll" };

        public static void Apply(GameEntry game) => Apply(game, null);

        public static void Apply(GameEntry game, string savedSelection)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            string recommended; string reason;
            var known = KnownGames.Match(game);
            if (known != null)
            {
                recommended = known.DefaultProxy;
                reason = "已知游戏规则：" + known.Name + " 默认 " + known.DefaultProxy;
                if (string.IsNullOrEmpty(game.Name)) game.Name = known.Name;
                if (string.IsNullOrEmpty(game.SteamAppId)) game.SteamAppId = known.SteamAppId;
            }
            else
            {
                var pe = PeInspector.Read(game.ExePath);
                var candidates = new List<string>();
                if (pe != null)
                {
                    foreach (var import in pe.Imports.Concat(pe.DelayImports))
                        if (ProxyNames.IsSupported(import) && !candidates.Contains(import, StringComparer.OrdinalIgnoreCase))
                            candidates.Add(import.ToLowerInvariant());
                }
                if (candidates.Count > 0)
                {
                    recommended = UpstreamOrder.First(p => candidates.Contains(p));
                    reason = "EXE 导入表包含：" + string.Join("、", candidates.OrderBy(c => Array.IndexOf(UpstreamOrder, c)));
                }
                else
                {
                    recommended = "version.dll";
                    reason = pe == null ? "无法读取 EXE，默认 version.dll（待验证）" : "导入表无明确线索，默认 version.dll（待验证）";
                }
            }
            game.RecommendedProxy = recommended;
            game.RecommendationReason = reason;
            game.SelectedProxy = ProxyNames.IsSupported(savedSelection) ? savedSelection.ToLowerInvariant() : recommended;
        }
    }

    public static class PathUtil
    {
        /// <summary>Full path with trailing separators removed; safe to use for file access.</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : path;
            return full.TrimEnd('\\', '/');
        }

        /// <summary>Comparison key: normalized, Windows separators, lower-case. Not a usable path on non-Windows hosts.</summary>
        public static string Key(string path) => Normalize(path).Replace('/', '\\').ToLowerInvariant();

        public static string DirectoryKey(string exePath) => Key(Path.GetDirectoryName(Path.GetFullPath(exePath)));

        public static bool SamePath(string a, string b) => string.Equals(Key(a), Key(b), StringComparison.Ordinal);

        /// <summary>
        /// Upper-case drive letter plus the on-disk casing of every segment that exists (Steam manifests often store "d:\steam").
        /// Segments that cannot be resolved are kept as given; never throws.
        /// </summary>
        public static string ActualCase(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try
            {
                var full = Path.GetFullPath(path);
                var root = Path.GetPathRoot(full) ?? "";
                if (root.Length >= 2 && root[1] == ':') root = char.ToUpperInvariant(root[0]) + root.Substring(1);
                var current = root;
                var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0, IgnoreInaccessible = true };
                foreach (var part in full.Substring(Path.GetPathRoot(full)?.Length ?? 0).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var actual = part;
                    try
                    {
                        if (Directory.Exists(current))
                        {
                            var match = Directory.EnumerateFileSystemEntries(current, part, options).Select(Path.GetFileName).FirstOrDefault();
                            if (!string.IsNullOrEmpty(match)) actual = match;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    current = Path.Combine(current, actual);
                }
                return current;
            }
            catch (Exception) { return path; }
        }
    }
}
