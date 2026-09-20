using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Sm86.Manager
{
    /// <summary>Blocks file operations while the game's process is running.</summary>
    public sealed class ProcessGuard : IGameProcessGuard
    {
        public void EnsureStopped(GameEntry game)
        {
            var exe = Path.GetFullPath(game.ExePath);
            var name = Path.GetFileNameWithoutExtension(exe);
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string path = null;
                    try { path = p.MainModule?.FileName; } catch (Exception) { }
                    if (path == null || PathUtil.SamePath(path, exe))
                        throw new IOException("游戏正在运行（" + name + "，PID " + p.Id + "），请先退出游戏。");
                }
                finally { p.Dispose(); }
            }
        }
    }

    /// <summary>
    /// Stateless deployment: nothing is recorded anywhere. What is installed is read from the game folder itself,
    /// and a DLL is recognised as "ours" when its hash matches an upstream file (release tree blob hash or a
    /// download made in this session).
    /// </summary>
    public sealed class InstallerService
    {
        public const string IniName = "dlssg_sm86.ini";
        private const string StageSuffix = ".dlssfg-staging";
        private readonly IGameProcessGuard _guard;
        private readonly object _lock = new object();
        private readonly Dictionary<string, (long size, long ticks, string sha256, string blob)> _hashCache = new Dictionary<string, (long, long, string, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>sha256 → version label (files downloaded this session).</summary>
        public IDictionary<string, string> KnownHashes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>git blob sha1 → version label (every proxy in the latest upstream release tree).</summary>
        public IDictionary<string, string> KnownBlobs { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public InstallerService(IGameProcessGuard guard = null) { _guard = guard ?? new ProcessGuard(); }

        /// <summary>Version label for a DLL that matches an upstream file, otherwise null.</summary>
        public string Identify(string path)
        {
            var (sha, blob) = Hashes(path);
            if (sha != null && KnownHashes.TryGetValue(sha, out var label)) return label;
            if (blob != null && KnownBlobs.TryGetValue(blob, out label)) return label;
            return null;
        }

        private (string sha256, string blob) Hashes(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return (null, null);
                lock (_lock)
                {
                    if (_hashCache.TryGetValue(path, out var c) && c.size == info.Length && c.ticks == info.LastWriteTimeUtc.Ticks) return (c.sha256, c.blob);
                }
                var sha = FileIntegrity.Sha256(path); var blob = FileIntegrity.GitBlobSha1(path);
                lock (_lock) { _hashCache[path] = (info.Length, info.LastWriteTimeUtc.Ticks, sha, blob); }
                return (sha, blob);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return (null, null); }
        }

        // ------------------------------------------------------------------ inspect

        public InstalledState Inspect(GameEntry game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            var dir = game.DirectoryPath;
            var state = new InstalledState();
            if (!Directory.Exists(dir)) { state.Status = "目录不存在"; return state; }
            var present = ProxyNames.All.Where(p => File.Exists(Path.Combine(dir, p))).ToList();
            bool ini = File.Exists(Path.Combine(dir, IniName));
            var known = present.Select(p => (name: p, label: Identify(Path.Combine(dir, p)))).Where(t => t.label != null).ToList();
            if (known.Count == 1)
            {
                state.IsInstalled = true; state.IsManaged = true; state.ProxyName = known[0].name; state.Version = known[0].label; state.Status = "已安装";
                return state;
            }
            if (known.Count > 1) { state.IsAmbiguous = true; state.Version = "未知版本"; state.Status = "存在多个补丁 DLL"; return state; }
            var selected = present.FirstOrDefault(p => p.Equals(game.SelectedProxy, StringComparison.OrdinalIgnoreCase));
            if (ini && selected != null) { state.IsInstalled = true; state.ProxyName = selected; state.Version = "未知版本"; state.Status = "已安装（未知版本）"; return state; }
            if (ini && present.Count == 1) { state.IsInstalled = true; state.ProxyName = present[0]; state.Version = "未知版本"; state.Status = "已安装（未知版本）"; return state; }
            if (ini && present.Count > 1) { state.IsAmbiguous = true; state.Version = "未知版本"; state.Status = "存在多个代理 DLL"; return state; }
            state.Status = "未安装";
            return state;
        }

        // ------------------------------------------------------------------ install / update / switch

        public OperationResult Install(GameEntry game, PatchPayload payload)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (!ProxyNames.IsSupported(payload.ProxyName) || payload.ProxyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || payload.ProxyName.Contains(".."))
                throw new ArgumentException("无效的代理 DLL 名称：" + payload.ProxyName, nameof(payload));
            if (!File.Exists(payload.ProxyPath)) throw new FileNotFoundException("补丁 DLL 不存在：" + payload.ProxyPath);
            if (!File.Exists(payload.IniPath)) throw new FileNotFoundException("默认配置不存在：" + payload.IniPath);
            var proxy = payload.ProxyName.ToLowerInvariant();
            var dir = game.DirectoryPath;
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("游戏目录不存在：" + dir);

            _guard.EnsureStopped(game);
            EnsureWritable(dir);
            var payloadSha = FileIntegrity.Sha256(payload.ProxyPath);
            if (!string.IsNullOrEmpty(payload.Sha256) && !FileIntegrity.SameHash(payload.Sha256, payloadSha)) throw new IOException("补丁文件校验失败，已放弃安装。");
            if (!string.IsNullOrEmpty(payload.Tag)) KnownHashes[payloadSha] = payload.Tag;

            var target = Path.Combine(dir, proxy);
            var iniTarget = Path.Combine(dir, IniName);
            bool hadProxy = File.Exists(target), hadIni = File.Exists(iniTarget);

            // Switching: remove other proxies that are recognisably ours. Unknown files (e.g. a game's own dbghelp.dll) stay.
            var removed = new List<string>();
            foreach (var other in ProxyNames.All.Where(p => !p.Equals(proxy, StringComparison.OrdinalIgnoreCase)))
            {
                var path = Path.Combine(dir, other);
                if (File.Exists(path) && Identify(path) != null) { File.Delete(path); removed.Add(other); }
            }

            var staged = target + StageSuffix;
            try
            {
                File.Copy(payload.ProxyPath, staged, true);
                if (!FileIntegrity.SameHash(FileIntegrity.Sha256(staged), payloadSha)) throw new IOException("暂存文件校验失败。");
                AtomicFile.Replace(staged, target);
                if (!hadIni) File.Copy(payload.IniPath, iniTarget, true);
                if (!FileIntegrity.SameHash(FileIntegrity.Sha256(target), payloadSha)) throw new IOException("安装后校验失败。");
            }
            catch (Exception ex)
            {
                try { if (File.Exists(staged)) File.Delete(staged); } catch (Exception) { }
                throw new IOException("安装失败：" + ex.Message, ex);
            }
            var verb = removed.Count > 0 ? "已切换到 " + proxy + "（移除 " + string.Join("、", removed) + "）" : hadProxy ? "已更新 " + proxy : "已安装 " + proxy;
            return new OperationResult { Success = true, Message = verb + "（" + (string.IsNullOrEmpty(payload.Tag) ? "未知版本" : payload.Tag) + "）" + (hadIni ? "，已保留原有 INI" : "") };
        }

        // ------------------------------------------------------------------ uninstall

        public OperationResult Uninstall(GameEntry game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            var dir = game.DirectoryPath;
            _guard.EnsureStopped(game);
            var state = Inspect(game);
            var targets = new List<string>();
            if (state.IsAmbiguous || state.IsInstalled)
            {
                foreach (var p in ProxyNames.All)
                {
                    var path = Path.Combine(dir, p);
                    if (File.Exists(path) && (Identify(path) != null || p.Equals(state.ProxyName, StringComparison.OrdinalIgnoreCase))) targets.Add(p);
                }
            }
            if (targets.Count == 0 && !File.Exists(Path.Combine(dir, IniName))) return new OperationResult { Success = false, Message = "未安装，无需卸载。" };
            EnsureWritable(dir);
            foreach (var p in targets) File.Delete(Path.Combine(dir, p));
            var ini = Path.Combine(dir, IniName);
            if (File.Exists(ini)) File.Delete(ini);
            return new OperationResult { Success = true, Message = "已删除 " + string.Join("、", targets.Concat(new[] { IniName })) };
        }

        // ------------------------------------------------------------------ helpers

        private static void EnsureWritable(string dir)
        {
            var probe = Path.Combine(dir, ".dlssfg-write-test-" + Guid.NewGuid().ToString("N"));
            try { File.WriteAllBytes(probe, new byte[0]); File.Delete(probe); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { throw new IOException("目录不可写：" + dir + "（" + ex.Message + "）。请以管理员身份运行或检查权限。", ex); }
        }
    }

    internal static class AtomicFile
    {
        /// <summary>Move tmp onto destination; File.Replace makes the swap a single rename when the destination exists.</summary>
        public static void Replace(string tmp, string destination)
        {
            if (File.Exists(destination))
            {
                try { File.Replace(tmp, destination, null, true); return; }
                catch (PlatformNotSupportedException) { }
                catch (IOException) when (Environment.OSVersion.Platform != PlatformID.Win32NT) { }
                File.Delete(destination);
            }
            File.Move(tmp, destination);
        }
    }
}
