using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sm86.Manager
{
    public static class ProxyNames
    {
        public static readonly string[] All = { "version.dll", "winmm.dll", "dbghelp.dll", "dinput8.dll", "dxgi.dll", "d3d12.dll" };
        public static bool IsSupported(string value) => All.Contains(value ?? "", StringComparer.OrdinalIgnoreCase);
        public static string RepositoryPath(string proxy)
        {
            if (!IsSupported(proxy)) throw new ArgumentException("不支持的代理 DLL。", nameof(proxy));
            return proxy.Equals("version.dll", StringComparison.OrdinalIgnoreCase) ? "version.dll" : "alternatives/" + proxy.ToLowerInvariant();
        }
    }

    public sealed class GameEntry
    {
        public string Name { get; set; } = "";
        /// <summary>Localized store name (from Steam) when known; UI shows it in preference to Name.</summary>
        public string DisplayName { get; set; } = "";
        public string Title => string.IsNullOrEmpty(DisplayName) ? Name : DisplayName;
        public string ExePath { get; set; } = "";
        public string InstallRoot { get; set; } = "";
        public string SteamAppId { get; set; } = "";
        public string SelectedProxy { get; set; } = "version.dll";
        public string RecommendedProxy { get; set; } = "version.dll";
        public string RecommendationReason { get; set; } = "";
        public bool Selected { get; set; }
        public string Status { get; set; } = "未安装";
        public string InstalledVersion { get; set; } = "—";
        /// <summary>How the rendering EXE was located (known rule, UE layout, single candidate, manual...).</summary>
        public string LocatedBy { get; set; } = "";
        /// <summary>Other plausible x64 executables found in the install; non-empty means the user should confirm ExePath.</summary>
        public List<string> CandidateExes { get; set; } = new List<string>();
        public string DirectoryPath => Path.GetDirectoryName(Path.GetFullPath(ExePath));
    }

    public sealed class ScanResult
    {
        public List<GameEntry> Games { get; set; } = new List<GameEntry>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class PeInfo
    {
        public bool Is64Bit { get; set; }
        public bool IsDll { get; set; }
        public List<string> Imports { get; set; } = new List<string>();
        public List<string> DelayImports { get; set; } = new List<string>();
    }

    public sealed class RemoteFile
    {
        public string RelativePath { get; set; } = "";
        public string BlobSha { get; set; } = "";
        public long Size { get; set; }
    }

    public sealed class ReleaseInfo
    {
        public string Tag { get; set; } = "";
        public string Commit { get; set; } = "";
        public string HtmlUrl { get; set; } = "";
        public string Notes { get; set; } = "";
        public DateTime CheckedAtUtc { get; set; }
        public Dictionary<string, RemoteFile> Files { get; set; } = new Dictionary<string, RemoteFile>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class TransferProgress
    {
        public string Message { get; set; } = "";
        public long Received { get; set; }
        public long Total { get; set; }
    }

    public sealed class PatchPayload
    {
        public string ProxyName { get; set; } = "";
        public string ProxyPath { get; set; } = "";
        public string IniPath { get; set; } = "";
        public string Tag { get; set; } = "本地补丁";
        public string Commit { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    public sealed class InstalledState
    {
        public bool IsInstalled { get; set; }
        public bool IsManaged { get; set; }
        public bool IsModified { get; set; }
        public bool IsAmbiguous { get; set; }
        public string ProxyName { get; set; } = "";
        public string Version { get; set; } = "—";
        public string Status { get; set; } = "未安装";
    }

    public sealed class OperationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string BackupDirectory { get; set; } = "";
    }

    public sealed class AppSettings
    {
        public string Theme { get; set; } = "system";
        public string LocalPackageDirectory { get; set; } = "";
        public List<string> ScanFolders { get; set; } = new List<string>();
        public List<GameEntry> Games { get; set; } = new List<GameEntry>();
    }

    public interface IGameProcessGuard { void EnsureStopped(GameEntry game); }
}
