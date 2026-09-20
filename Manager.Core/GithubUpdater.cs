using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Sm86.Manager
{
    /// <summary>
    /// Resolves the latest stable GitHub release to a fixed commit and downloads individual files from it with
    /// size + git blob verification. Files live in a temporary folder for the lifetime of the session only.
    /// </summary>
    public sealed class GithubUpdater : IDisposable
    {
        public const string Owner = "sdli1995";
        public const string Repo = "dlssg_for_sm86";
        public const string ThirdPartyNoticesName = "THIRD_PARTY_NOTICES.txt";

        public string ApiBase { get; set; } = "https://api.github.com";
        public string RawBase { get; set; } = "https://raw.githubusercontent.com";

        private readonly string _tempRoot;
        private readonly HttpClient _client;
        private readonly bool _ownsClient;
        private readonly Dictionary<string, string> _knownHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public GithubUpdater(string tempDirectory = null, HttpClient client = null)
        {
            _tempRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(tempDirectory) ? Path.Combine(Path.GetTempPath(), "DLSSFG-Manager") : tempDirectory);
            Directory.CreateDirectory(_tempRoot);
            _ownsClient = client == null;
            _client = client ?? CreateClient();
        }

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DLSSFG-Manager", "1.0"));
            c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrEmpty(token)) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return c;
        }

        public string TempDirectory => _tempRoot;

        /// <summary>sha256 → "tag" for every DLL verified this session.</summary>
        public IReadOnlyDictionary<string, string> KnownHashes => _knownHashes;

        /// <summary>Delete everything downloaded this session.</summary>
        public void Cleanup()
        {
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ check

        public async Task<ReleaseInfo> CheckLatestAsync(CancellationToken cancellationToken)
        {
            var release = JObject.Parse(await GetStringAsync(ApiBase + "/repos/" + Owner + "/" + Repo + "/releases/latest", cancellationToken).ConfigureAwait(false));
            var tag = (string)release["tag_name"];
            if (string.IsNullOrEmpty(tag)) throw new IOException("上游没有稳定 Release。");
            var info = new ReleaseInfo
            {
                Tag = tag,
                HtmlUrl = (string)release["html_url"] ?? "",
                Notes = (string)release["body"] ?? "",
                CheckedAtUtc = DateTime.UtcNow,
                Commit = await ResolveTagAsync(tag, cancellationToken).ConfigureAwait(false),
            };
            var tree = JObject.Parse(await GetStringAsync(ApiBase + "/repos/" + Owner + "/" + Repo + "/git/trees/" + info.Commit + "?recursive=1", cancellationToken).ConfigureAwait(false));
            foreach (var node in tree["tree"] as JArray ?? new JArray())
            {
                if ((string)node["type"] != "blob") continue;
                var path = (string)node["path"];
                info.Files[path] = new RemoteFile { RelativePath = path, BlobSha = (string)node["sha"] ?? "", Size = (long?)node["size"] ?? -1 };
            }
            if (tree["truncated"]?.Value<bool>() == true && FindRemote(info, "version.dll") == null)
                throw new IOException("上游文件清单过大被截断，无法定位补丁文件。");
            if (FindRemote(info, "version.dll") == null || FindRemote(info, InstallerService.IniName) == null)
                throw new IOException("Release " + tag + " 对应的提交中找不到 version.dll 或 " + InstallerService.IniName + "。");
            return info;
        }

        private async Task<string> ResolveTagAsync(string tag, CancellationToken ct)
        {
            var reference = JObject.Parse(await GetStringAsync(ApiBase + "/repos/" + Owner + "/" + Repo + "/git/ref/tags/" + Uri.EscapeDataString(tag), ct).ConfigureAwait(false));
            var obj = reference["object"];
            var sha = (string)obj?["sha"]; var type = (string)obj?["type"];
            if (string.IsNullOrEmpty(sha)) throw new IOException("无法解析 tag " + tag + "。");
            if (type == "tag")
            {
                var annotated = JObject.Parse(await GetStringAsync(ApiBase + "/repos/" + Owner + "/" + Repo + "/git/tags/" + sha, ct).ConfigureAwait(false));
                sha = (string)annotated["object"]?["sha"] ?? throw new IOException("无法解引用附注 tag " + tag + "。");
            }
            return sha;
        }

        /// <summary>Root file first, then alternatives/; returns null when the commit does not ship that proxy.</summary>
        public static RemoteFile FindRemote(ReleaseInfo release, string name)
        {
            if (release.Files.TryGetValue(name, out var f)) return f;
            if (release.Files.TryGetValue("alternatives/" + name, out f)) return f;
            return null;
        }

        /// <summary>git blob sha1 → tag for every proxy DLL shipped by the release (lets the installer recognise deployed files).</summary>
        public static Dictionary<string, string> ProxyBlobs(ReleaseInfo release)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var proxy in ProxyNames.All)
            {
                var remote = FindRemote(release, proxy);
                if (remote != null && !string.IsNullOrEmpty(remote.BlobSha)) map[remote.BlobSha] = release.Tag;
            }
            return map;
        }

        // ------------------------------------------------------------------ download

        public async Task<PatchPayload> DownloadAsync(ReleaseInfo release, string proxyName, CancellationToken cancellationToken, IProgress<TransferProgress> progress = null)
        {
            if (release == null) throw new ArgumentNullException(nameof(release));
            if (!ProxyNames.IsSupported(proxyName)) throw new ArgumentException("不支持的代理 DLL：" + proxyName, nameof(proxyName));
            proxyName = proxyName.ToLowerInvariant();
            var folder = Path.Combine(_tempRoot, string.IsNullOrEmpty(release.Commit) ? "unknown" : release.Commit.Substring(0, Math.Min(12, release.Commit.Length)));
            Directory.CreateDirectory(folder);
            var proxyRemote = FindRemote(release, proxyName) ?? throw new IOException("Release " + release.Tag + " 中没有 " + proxyName + "。");
            var iniRemote = FindRemote(release, InstallerService.IniName) ?? throw new IOException("Release " + release.Tag + " 中没有默认配置。");

            var proxyPath = await FetchVerifiedAsync(release, proxyRemote, Path.Combine(folder, proxyName), cancellationToken, progress).ConfigureAwait(false);
            var iniPath = await FetchVerifiedAsync(release, iniRemote, Path.Combine(folder, InstallerService.IniName), cancellationToken, progress).ConfigureAwait(false);
            var pe = PeInspector.Read(proxyPath);
            if (pe == null || !pe.IsDll || !pe.Is64Bit) { File.Delete(proxyPath); throw new IOException("下载的 " + proxyName + " 不是 64 位 DLL，已丢弃。"); }
            var payload = new PatchPayload { ProxyName = proxyName, ProxyPath = proxyPath, IniPath = iniPath, Tag = release.Tag, Commit = release.Commit, Sha256 = FileIntegrity.Sha256(proxyPath) };
            _knownHashes[payload.Sha256] = release.Tag;
            return payload;
        }

        private static bool Verify(RemoteFile remote, string path)
        {
            if (!File.Exists(path)) return false;
            if (remote.Size >= 0 && new FileInfo(path).Length != remote.Size) return false;
            return FileIntegrity.SameHash(FileIntegrity.GitBlobSha1(path), remote.BlobSha);
        }

        private async Task<string> FetchVerifiedAsync(ReleaseInfo release, RemoteFile remote, string destination, CancellationToken ct, IProgress<TransferProgress> progress)
        {
            if (Verify(remote, destination)) { progress?.Report(new TransferProgress { Message = "已下载：" + remote.RelativePath, Received = remote.Size, Total = remote.Size }); return destination; }
            var url = RawBase + "/" + Owner + "/" + Repo + "/" + release.Commit + "/" + remote.RelativePath;
            var tmp = destination + ".download";
            try
            {
                using (var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    await ThrowIfFailed(response, url).ConfigureAwait(false);
                    long total = response.Content.Headers.ContentLength ?? remote.Size;
                    using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920]; long received = 0; int read;
                        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            await file.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                            received += read;
                            progress?.Report(new TransferProgress { Message = "下载 " + remote.RelativePath, Received = received, Total = total });
                        }
                    }
                }
                if (!Verify(remote, tmp)) throw new IOException("下载的 " + remote.RelativePath + " 校验失败（大小或 Git blob hash 不匹配），已丢弃。");
                AtomicFile.Replace(tmp, destination);
                return destination;
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
        }

        private async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            HttpResponseMessage response;
            try { response = await _client.GetAsync(url, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw new IOException("无法连接 GitHub：" + Root(ex).Message, ex); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("连接 GitHub 超时。"); }
            using (response)
            {
                await ThrowIfFailed(response, url).ConfigureAwait(false);
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }

        private static async Task ThrowIfFailed(HttpResponseMessage response, string url)
        {
            if (response.IsSuccessStatusCode) return;
            var status = (int)response.StatusCode;
            if ((status == 403 || status == 429) && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0")
            {
                string when = "";
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var epoch))
                    when = "，将于 " + DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("HH:mm") + " 重置";
                throw new IOException("GitHub API 请求次数已用尽" + when + "。可稍后重试，或设置 GITHUB_TOKEN 环境变量提高额度。");
            }
            if (status == 404) throw new IOException("上游资源不存在（404）：" + url);
            var body = "";
            try { body = await response.Content.ReadAsStringAsync().ConfigureAwait(false); } catch (Exception) { }
            if (body.Length > 200) body = body.Substring(0, 200);
            throw new IOException("GitHub 返回 " + status + " " + response.ReasonPhrase + "：" + url + (body.Length > 0 ? "\n" + body : ""));
        }

        private static Exception Root(Exception ex) { while (ex.InnerException != null) ex = ex.InnerException; return ex; }

        public void Dispose() { if (_ownsClient) _client.Dispose(); }
    }
}
