using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Sm86.Manager
{
    public sealed class InstallRecord
    {
        public string ExeDirectory { get; set; } = "";
        public string ProxyName { get; set; } = "";
        public string ProxySha256 { get; set; } = "";
        public bool IniOwned { get; set; }
        public string Tag { get; set; } = "";
        public string Commit { get; set; } = "";
        public DateTime InstalledAtUtc { get; set; }
        public string LastBackupDirectory { get; set; } = "";
    }

    public sealed class BackupFile
    {
        public string Name { get; set; } = "";
        public bool Existed { get; set; }
        public string Sha256 { get; set; } = "";
    }

    public sealed class BackupManifest
    {
        public string ExeDirectory { get; set; } = "";
        public string Operation { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public List<BackupFile> Files { get; set; } = new List<BackupFile>();
        public InstallRecord Record { get; set; }
    }

    internal sealed class PendingOperation
    {
        public string Id { get; set; } = "";
        public string ExeDirectory { get; set; } = "";
        public string Operation { get; set; } = "";
        public string BackupDirectory { get; set; } = "";
        public List<string> StagedFiles { get; set; } = new List<string>();
        public DateTime StartedAtUtc { get; set; }
    }

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

    public sealed class InstallerService
    {
        public const string IniName = "dlssg_sm86.ini";
        private const string StageSuffix = ".sm86-staging";
        private readonly string _data, _recordsPath, _backupsRoot, _pendingRoot;
        private readonly IGameProcessGuard _guard;
        private readonly object _lock = new object();

        /// <summary>Optional table of known patch hashes (sha256 → version label) used to label unmanaged installs.</summary>
        public IDictionary<string, string> KnownHashes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public InstallerService(string dataDirectory, IGameProcessGuard guard = null)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("数据目录不能为空。", nameof(dataDirectory));
            _data = Path.GetFullPath(dataDirectory);
            _recordsPath = Path.Combine(_data, "installs.json");
            _backupsRoot = Path.Combine(_data, "backups");
            _pendingRoot = Path.Combine(_data, "pending");
            Directory.CreateDirectory(_backupsRoot);
            Directory.CreateDirectory(_pendingRoot);
            _guard = guard ?? new ProcessGuard();
        }

        public string BackupsDirectory => _backupsRoot;

        // ------------------------------------------------------------------ inspect

        public InstalledState Inspect(GameEntry game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            var dir = game.DirectoryPath;
            var state = new InstalledState();
            var record = FindRecord(dir);
            if (record != null)
            {
                var target = Path.Combine(dir, record.ProxyName);
                state.ProxyName = record.ProxyName;
                state.Version = string.IsNullOrEmpty(record.Tag) ? "未知版本" : record.Tag;
                state.IsManaged = true;
                if (!File.Exists(target)) { state.Status = "文件缺失（记录仍在）"; state.IsInstalled = false; return state; }
                state.IsInstalled = true;
                if (FileIntegrity.SameHash(FileIntegrity.Sha256(target), record.ProxySha256)) { state.Status = "已安装"; return state; }
                state.IsModified = true; state.Status = "已被外部修改"; return state;
            }
            if (!Directory.Exists(dir)) { state.Status = "目录不存在"; return state; }
            var present = ProxyNames.All.Where(p => File.Exists(Path.Combine(dir, p))).ToList();
            bool ini = File.Exists(Path.Combine(dir, IniName));
            // Known-hash match beats everything else.
            foreach (var p in present)
            {
                var sha = SafeSha(Path.Combine(dir, p));
                if (sha != null && KnownHashes.TryGetValue(sha, out var label))
                { state.IsInstalled = true; state.ProxyName = p; state.Version = label; state.Status = "已安装（手工，未纳管）"; return state; }
            }
            var selected = present.FirstOrDefault(p => p.Equals(game.SelectedProxy, StringComparison.OrdinalIgnoreCase));
            if (selected != null && (ini || present.Count == 1 || !selected.Equals("dbghelp.dll", StringComparison.OrdinalIgnoreCase)))
            { state.IsInstalled = true; state.ProxyName = selected; state.Version = "未知版本"; state.Status = "已存在旧插件（未知版本）"; return state; }
            if (ini && present.Count == 1)
            { state.IsInstalled = true; state.ProxyName = present[0]; state.Version = "未知版本"; state.Status = "已存在旧插件（未知版本）"; return state; }
            if (ini && present.Count > 1)
            { state.IsAmbiguous = true; state.Status = "存在多个代理 DLL，请选择当前代理"; state.Version = "未知版本"; return state; }
            state.Status = ini ? "仅有 INI，未安装" : "未安装";
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

            lock (_lock)
            {
                _guard.EnsureStopped(game);
                EnsureWritable(dir);
                var record = FindRecord(dir);
                string oldProxy = null;
                if (record != null && !record.ProxyName.Equals(proxy, StringComparison.OrdinalIgnoreCase))
                {
                    var oldPath = Path.Combine(dir, record.ProxyName);
                    if (File.Exists(oldPath))
                    {
                        if (!FileIntegrity.SameHash(FileIntegrity.Sha256(oldPath), record.ProxySha256))
                            throw new IOException("原代理 " + record.ProxyName + " 已被外部修改，为避免误删未执行切换。请先手动确认或恢复备份。");
                        oldProxy = record.ProxyName;
                    }
                }
                var target = Path.Combine(dir, proxy);
                var iniTarget = Path.Combine(dir, IniName);
                bool iniExisted = File.Exists(iniTarget);
                var payloadSha = FileIntegrity.Sha256(payload.ProxyPath);
                if (!string.IsNullOrEmpty(payload.Sha256) && !FileIntegrity.SameHash(payload.Sha256, payloadSha))
                    throw new IOException("补丁文件校验失败，已放弃安装。");

                var names = new List<string> { proxy, IniName };
                if (oldProxy != null) names.Add(oldProxy);
                var backup = CreateBackup(dir, oldProxy != null ? "switch" : (record != null || File.Exists(target) ? "update" : "install"), names, record);
                var pending = BeginPending(dir, "install", backup);
                var staged = new List<string>();
                try
                {
                    var stagedProxy = target + StageSuffix;
                    staged.Add(stagedProxy); pending.StagedFiles.Add(stagedProxy); SavePending(pending);
                    File.Copy(payload.ProxyPath, stagedProxy, true);
                    if (!FileIntegrity.SameHash(FileIntegrity.Sha256(stagedProxy), payloadSha)) throw new IOException("暂存文件校验失败。");
                    string stagedIni = null;
                    if (!iniExisted)
                    {
                        stagedIni = iniTarget + StageSuffix;
                        staged.Add(stagedIni); pending.StagedFiles.Add(stagedIni); SavePending(pending);
                        File.Copy(payload.IniPath, stagedIni, true);
                    }
                    AtomicFile.Replace(stagedProxy, target);
                    if (stagedIni != null) AtomicFile.Replace(stagedIni, iniTarget);
                    if (oldProxy != null) File.Delete(Path.Combine(dir, oldProxy));
                    if (!FileIntegrity.SameHash(FileIntegrity.Sha256(target), payloadSha)) throw new IOException("安装后校验失败。");
                    var newRecord = new InstallRecord
                    {
                        ExeDirectory = dir, ProxyName = proxy, ProxySha256 = payloadSha,
                        IniOwned = !iniExisted || (record?.IniOwned ?? false),
                        Tag = payload.Tag ?? "", Commit = payload.Commit ?? "", InstalledAtUtc = DateTime.UtcNow, LastBackupDirectory = backup,
                    };
                    SaveRecord(newRecord);
                    EndPending(pending);
                    var verb = oldProxy != null ? "已切换到 " + proxy : (record != null ? "已更新 " + proxy : "已安装 " + proxy);
                    return new OperationResult { Success = true, Message = verb + "（" + VersionLabel(newRecord.Tag, newRecord.Commit) + "）", BackupDirectory = backup };
                }
                catch (Exception ex)
                {
                    RollBack(backup, staged);
                    EndPending(pending);
                    throw new IOException("安装失败，已恢复原状：" + ex.Message, ex);
                }
            }
        }

        // ------------------------------------------------------------------ uninstall

        public OperationResult Uninstall(GameEntry game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            var dir = game.DirectoryPath;
            lock (_lock)
            {
                _guard.EnsureStopped(game);
                var state = Inspect(game);
                if (state.IsAmbiguous) throw new IOException("目录中存在多个代理 DLL，请先在列表中选择当前代理。");
                var record = FindRecord(dir);
                if (record != null && state.IsModified)
                    throw new IOException("已纳管的 " + record.ProxyName + " 已被外部修改，为避免误删未执行卸载，配置文件已保留。");
                if (!state.IsInstalled)
                {
                    if (record != null && !File.Exists(Path.Combine(dir, record.ProxyName))) { RemoveRecord(dir); return new OperationResult { Success = true, Message = "文件已不存在，已清除安装记录。" }; }
                    return new OperationResult { Success = false, Message = "未安装，无需卸载。" };
                }
                EnsureWritable(dir);
                var proxy = state.ProxyName;
                var names = new List<string> { proxy, IniName };
                var backup = CreateBackup(dir, "uninstall", names, record);
                var pending = BeginPending(dir, "uninstall", backup);
                try
                {
                    // Open both for delete before touching either so a lock on the INI leaves the DLL in place.
                    foreach (var n in names) { var p = Path.Combine(dir, n); if (File.Exists(p)) ProbeDeletable(p); }
                    var ini = Path.Combine(dir, IniName);
                    if (File.Exists(ini)) File.Delete(ini);
                    File.Delete(Path.Combine(dir, proxy));
                    RemoveRecord(dir);
                    EndPending(pending);
                    return new OperationResult { Success = true, Message = "已卸载 " + proxy + "，备份保存在 " + backup, BackupDirectory = backup };
                }
                catch (Exception ex)
                {
                    RollBack(backup, new List<string>());
                    EndPending(pending);
                    throw new IOException("卸载失败，已恢复原状：" + ex.Message, ex);
                }
            }
        }

        // ------------------------------------------------------------------ restore / recover

        public OperationResult RestoreLastBackup(GameEntry game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            var dir = game.DirectoryPath;
            lock (_lock)
            {
                _guard.EnsureStopped(game);
                var backup = LatestBackup(dir);
                if (backup == null) return new OperationResult { Success = false, Message = "没有该游戏的备份。" };
                var manifest = ReadManifest(backup);
                if (manifest == null) return new OperationResult { Success = false, Message = "备份清单损坏：" + backup };
                var pre = CreateBackup(dir, "restore", manifest.Files.Select(f => f.Name).ToList(), FindRecord(dir));
                var pending = BeginPending(dir, "restore", pre);
                try
                {
                    ApplyBackup(backup, manifest);
                    if (manifest.Record != null) SaveRecord(manifest.Record); else RemoveRecord(dir);
                    EndPending(pending);
                    return new OperationResult { Success = true, Message = "已恢复 " + manifest.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " 的备份（" + manifest.Operation + " 前状态）", BackupDirectory = backup };
                }
                catch (Exception ex)
                {
                    RollBack(pre, new List<string>());
                    EndPending(pending);
                    throw new IOException("恢复失败，已回退：" + ex.Message, ex);
                }
            }
        }

        public List<string> RecoverPending()
        {
            var messages = new List<string>();
            lock (_lock)
            {
                foreach (var file in Directory.EnumerateFiles(_pendingRoot, "*.json").ToList())
                {
                    PendingOperation pending;
                    try { pending = JsonConvert.DeserializeObject<PendingOperation>(File.ReadAllText(file, Encoding.UTF8)); }
                    catch (Exception) { pending = null; }
                    if (pending == null) { File.Delete(file); continue; }
                    try
                    {
                        RollBack(pending.BackupDirectory, pending.StagedFiles);
                        var manifest = ReadManifest(pending.BackupDirectory);
                        if (manifest != null) { if (manifest.Record != null) SaveRecord(manifest.Record); else RemoveRecord(pending.ExeDirectory); }
                        messages.Add("已恢复未完成的操作（" + pending.Operation + "）：" + pending.ExeDirectory);
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        messages.Add("恢复未完成的操作失败：" + pending.ExeDirectory + "：" + ex.Message);
                    }
                }
            }
            return messages;
        }

        public List<string> ListBackups(GameEntry game)
        {
            var folder = BackupFolderFor(game.DirectoryPath);
            return Directory.Exists(folder) ? Directory.GetDirectories(folder).OrderByDescending(d => d).ToList() : new List<string>();
        }

        // ------------------------------------------------------------------ helpers

        public static string VersionLabel(string tag, string commit) =>
            string.IsNullOrEmpty(tag) ? "未知版本" : (string.IsNullOrEmpty(commit) ? tag : tag + " (" + commit.Substring(0, Math.Min(7, commit.Length)) + ")");

        private static string SafeSha(string path) { try { return FileIntegrity.Sha256(path); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } }

        private static void EnsureWritable(string dir)
        {
            var probe = Path.Combine(dir, ".sm86-write-test-" + Guid.NewGuid().ToString("N"));
            try { File.WriteAllBytes(probe, new byte[0]); File.Delete(probe); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { throw new IOException("目录不可写：" + dir + "（" + ex.Message + "）。请以管理员身份运行或检查权限。", ex); }
        }

        private static void ProbeDeletable(string path)
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }

        // records

        private Dictionary<string, InstallRecord> LoadRecords()
        {
            if (!File.Exists(_recordsPath)) return new Dictionary<string, InstallRecord>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, InstallRecord>>(File.ReadAllText(_recordsPath, Encoding.UTF8));
                return new Dictionary<string, InstallRecord>(dict ?? new Dictionary<string, InstallRecord>(), StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException ex) { throw new IOException("安装记录已损坏：" + _recordsPath, ex); }
        }
        private void SaveRecords(Dictionary<string, InstallRecord> records) => AtomicFile.WriteAllText(_recordsPath, JsonConvert.SerializeObject(records, Formatting.Indented));
        public InstallRecord FindRecord(string dir) { LoadRecords().TryGetValue(PathUtil.Key(dir), out var r); return r; }
        private void SaveRecord(InstallRecord record) { var all = LoadRecords(); all[PathUtil.Key(record.ExeDirectory)] = record; SaveRecords(all); }
        private void RemoveRecord(string dir) { var all = LoadRecords(); if (all.Remove(PathUtil.Key(dir))) SaveRecords(all); }

        // backups

        private string BackupFolderFor(string dir)
        {
            var key = PathUtil.Key(dir);
            string hash;
            using (var sha = System.Security.Cryptography.SHA1.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 8).ToLowerInvariant();
            var leaf = new string(Path.GetFileName(PathUtil.Normalize(dir)).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            if (leaf.Length > 40) leaf = leaf.Substring(0, 40);
            return Path.Combine(_backupsRoot, (leaf.Length == 0 ? "game" : leaf) + "-" + hash);
        }

        private string CreateBackup(string dir, string operation, List<string> names, InstallRecord record)
        {
            var folder = BackupFolderFor(dir);
            Directory.CreateDirectory(folder);
            var backup = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            int n = 0; while (Directory.Exists(backup)) backup = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + (++n));
            Directory.CreateDirectory(backup);
            var manifest = new BackupManifest { ExeDirectory = dir, Operation = operation, CreatedAtUtc = DateTime.UtcNow, Record = record };
            try
            {
                foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var source = Path.Combine(dir, name);
                    var entry = new BackupFile { Name = name, Existed = File.Exists(source) };
                    if (entry.Existed)
                    {
                        var dest = Path.Combine(backup, name);
                        File.Copy(source, dest, true);
                        entry.Sha256 = FileIntegrity.Sha256(source);
                        if (!FileIntegrity.SameHash(FileIntegrity.Sha256(dest), entry.Sha256)) throw new IOException("备份校验失败：" + name);
                    }
                    manifest.Files.Add(entry);
                }
                File.WriteAllText(Path.Combine(backup, "manifest.json"), JsonConvert.SerializeObject(manifest, Formatting.Indented), new UTF8Encoding(false));
                return backup;
            }
            catch (Exception ex)
            {
                try { Directory.Delete(backup, true); } catch (Exception) { }
                throw new IOException("备份失败，未修改任何文件：" + ex.Message, ex);
            }
        }

        private static BackupManifest ReadManifest(string backup)
        {
            var path = Path.Combine(backup ?? "", "manifest.json");
            if (!File.Exists(path)) return null;
            try { return JsonConvert.DeserializeObject<BackupManifest>(File.ReadAllText(path, Encoding.UTF8)); } catch (JsonException) { return null; }
        }

        private string LatestBackup(string dir)
        {
            var folder = BackupFolderFor(dir);
            if (!Directory.Exists(folder)) return null;
            return Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, "manifest.json"))).OrderByDescending(d => d).FirstOrDefault();
        }

        /// <summary>Put the target directory back exactly as the manifest describes: existing files restored, absent files removed.</summary>
        private static void ApplyBackup(string backup, BackupManifest manifest)
        {
            foreach (var f in manifest.Files)
            {
                var target = Path.Combine(manifest.ExeDirectory, f.Name);
                if (f.Existed)
                {
                    var tmp = target + StageSuffix;
                    File.Copy(Path.Combine(backup, f.Name), tmp, true);
                    AtomicFile.Replace(tmp, target);
                }
                else if (File.Exists(target)) File.Delete(target);
            }
        }

        private void RollBack(string backup, List<string> staged)
        {
            foreach (var s in staged ?? new List<string>()) { try { if (File.Exists(s)) File.Delete(s); } catch (Exception) { } }
            var manifest = ReadManifest(backup);
            if (manifest == null) return;
            ApplyBackup(backup, manifest);
        }

        // pending journal

        private PendingOperation BeginPending(string dir, string op, string backup)
        {
            var p = new PendingOperation { Id = Guid.NewGuid().ToString("N"), ExeDirectory = dir, Operation = op, BackupDirectory = backup, StartedAtUtc = DateTime.UtcNow };
            SavePending(p);
            return p;
        }
        private void SavePending(PendingOperation p) => AtomicFile.WriteAllText(Path.Combine(_pendingRoot, p.Id + ".json"), JsonConvert.SerializeObject(p));
        private void EndPending(PendingOperation p) { var f = Path.Combine(_pendingRoot, p.Id + ".json"); if (File.Exists(f)) File.Delete(f); }
    }
}
