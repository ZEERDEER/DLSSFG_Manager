using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Sm86.Manager
{
    /// <summary>Resolves the latest stable GitHub release to a fixed commit and downloads individual files from it with size + git blob verification.</summary>
    public sealed class GithubUpdater : IDisposable
    {
        public const string Owner = "sdli1995";
        public const string Repo = "dlssg_for_sm86";
        public const string ThirdPartyNoticesName = "THIRD_PARTY_NOTICES.txt";

        public string ApiBase { get; set; } = "https://api.github.com";
        public string RawBase { get; set; } = "https://raw.githubusercontent.com";

        private readonly string _cacheRoot, _releasePath;
        private readonly HttpClient _client;
        private readonly bool _ownsClient;

        public GithubUpdater(string dataDirectory, HttpClient client = null)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("数据目录不能为空。", nameof(dataDirectory));
            _cacheRoot = Path.Combine(Path.GetFullPath(dataDirectory), "cache");
            _releasePath = Path.Combine(_cacheRoot, "release.json");
            Directory.CreateDirectory(_cacheRoot);
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

        public string CacheDirectory => _cacheRoot;

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
            AtomicFile.WriteAllText(_releasePath, JsonConvert.SerializeObject(info, Formatting.Indented));
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

        public ReleaseInfo LoadCachedRelease()
        {
            if (!File.Exists(_releasePath)) return null;
            try { return JsonConvert.DeserializeObject<ReleaseInfo>(File.ReadAllText(_releasePath, Encoding.UTF8)); }
            catch (JsonException) { return null; }
        }

        /// <summary>Root file first, then alternatives/; returns null when the commit does not ship that proxy.</summary>
        public static RemoteFile FindRemote(ReleaseInfo release, string name)
        {
            if (release.Files.TryGetValue(name, out var f)) return f;
            if (release.Files.TryGetValue("alternatives/" + name, out f)) return f;
            return null;
        }

        // ------------------------------------------------------------------ download

        public async Task<PatchPayload> DownloadAsync(ReleaseInfo release, string proxyName, CancellationToken cancellationToken, IProgress<TransferProgress> progress = null)
        {
            if (release == null) throw new ArgumentNullException(nameof(release));
            if (!ProxyNames.IsSupported(proxyName)) throw new ArgumentException("不支持的代理 DLL：" + proxyName, nameof(proxyName));
            proxyName = proxyName.ToLowerInvariant();
            var folder = ReleaseCacheFolder(release);
            Directory.CreateDirectory(folder);
            var proxyRemote = FindRemote(release, proxyName) ?? throw new IOException("Release " + release.Tag + " 中没有 " + proxyName + "。");
            var iniRemote = FindRemote(release, InstallerService.IniName) ?? throw new IOException("Release " + release.Tag + " 中没有默认配置。");
            var noticesRemote = FindRemote(release, ThirdPartyNoticesName);

            var proxyPath = await FetchVerifiedAsync(release, proxyRemote, Path.Combine(folder, proxyName), cancellationToken, progress).ConfigureAwait(false);
            var iniPath = await FetchVerifiedAsync(release, iniRemote, Path.Combine(folder, InstallerService.IniName), cancellationToken, progress).ConfigureAwait(false);
            if (noticesRemote != null)
            {
                try { await FetchVerifiedAsync(release, noticesRemote, Path.Combine(folder, ThirdPartyNoticesName), cancellationToken, progress).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException || ex is HttpRequestException) { /* notices are informational */ }
            }
            var pe = PeInspector.Read(proxyPath);
            if (pe == null || !pe.IsDll || !pe.Is64Bit) { File.Delete(proxyPath); throw new IOException("下载的 " + proxyName + " 不是 64 位 DLL，已丢弃。"); }
            var payload = new PatchPayload { ProxyName = proxyName, ProxyPath = proxyPath, IniPath = iniPath, Tag = release.Tag, Commit = release.Commit, Sha256 = FileIntegrity.Sha256(proxyPath) };
            RecordHash(payload);
            return payload;
        }

        /// <summary>Return a payload from the cache without any network use, or null if the cache is incomplete or fails verification.</summary>
        public PatchPayload LoadCachedPayload(ReleaseInfo release, string proxyName)
        {
            if (release == null || !ProxyNames.IsSupported(proxyName)) return null;
            proxyName = proxyName.ToLowerInvariant();
            var folder = ReleaseCacheFolder(release);
            var proxyPath = Path.Combine(folder, proxyName);
            var iniPath = Path.Combine(folder, InstallerService.IniName);
            var proxyRemote = FindRemote(release, proxyName); var iniRemote = FindRemote(release, InstallerService.IniName);
            if (proxyRemote == null || iniRemote == null || !Verify(proxyRemote, proxyPath) || !Verify(iniRemote, iniPath)) return null;
            return new PatchPayload { ProxyName = proxyName, ProxyPath = proxyPath, IniPath = iniPath, Tag = release.Tag, Commit = release.Commit, Sha256 = FileIntegrity.Sha256(proxyPath) };
        }

        /// <summary>sha256 → "tag (commit)" for every verified DLL ever downloaded; lets the installer label hand-installed copies.</summary>
        public Dictionary<string, string> KnownHashes()
        {
            var path = Path.Combine(_cacheRoot, "known-hashes.json");
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { return new Dictionary<string, string>(JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8)) ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase); }
            catch (JsonException) { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        }

        private void RecordHash(PatchPayload payload)
        {
            var known = KnownHashes();
            known[payload.Sha256] = InstallerService.VersionLabel(payload.Tag, payload.Commit);
            AtomicFile.WriteAllText(Path.Combine(_cacheRoot, "known-hashes.json"), JsonConvert.SerializeObject(known, Formatting.Indented));
        }

        private string ReleaseCacheFolder(ReleaseInfo release)
        {
            var safeTag = new string(release.Tag.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            var commit = string.IsNullOrEmpty(release.Commit) ? "unknown" : release.Commit.Substring(0, Math.Min(12, release.Commit.Length));
            return Path.Combine(_cacheRoot, safeTag + "-" + commit);
        }

        private static bool Verify(RemoteFile remote, string path)
        {
            if (!File.Exists(path)) return false;
            if (remote.Size >= 0 && new FileInfo(path).Length != remote.Size) return false;
            return FileIntegrity.SameHash(FileIntegrity.GitBlobSha1(path), remote.BlobSha);
        }

        private async Task<string> FetchVerifiedAsync(ReleaseInfo release, RemoteFile remote, string destination, CancellationToken ct, IProgress<TransferProgress> progress)
        {
            if (Verify(remote, destination)) { progress?.Report(new TransferProgress { Message = "使用已校验缓存：" + remote.RelativePath, Received = remote.Size, Total = remote.Size }); return destination; }
            var url = RawBase + "/" + Owner + "/" + Repo + "/" + release.Commit + "/" + remote.RelativePath;
            var tmp = destination + ".download";
            try
            {
                using (var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    await ThrowIfFailed(response, url).ConfigureAwait(false);
                    long total = response.Content.Headers.ContentLength ?? remote.Size;
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
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
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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

    /// <summary>Loads a patch from a local upstream checkout / release folder without moving anything.</summary>
    public static class LocalPackage
    {
        public static PatchPayload Open(string directory, string proxyName, IDictionary<string, string> knownHashes = null)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) throw new DirectoryNotFoundException("本地补丁目录不存在：" + directory);
            if (!ProxyNames.IsSupported(proxyName)) throw new ArgumentException("不支持的代理 DLL：" + proxyName, nameof(proxyName));
            proxyName = proxyName.ToLowerInvariant();
            var proxyPath = new[] { Path.Combine(directory, proxyName), Path.Combine(directory, "alternatives", proxyName), Path.Combine(directory, "altnative", proxyName) }.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("本地补丁目录中没有 " + proxyName + "（根目录或 alternatives\\ 下）。");
            var iniPath = Path.Combine(directory, InstallerService.IniName);
            if (!File.Exists(iniPath)) throw new FileNotFoundException("本地补丁目录缺少 " + InstallerService.IniName + "。");
            var pe = PeInspector.Read(proxyPath);
            if (pe == null || !pe.IsDll || !pe.Is64Bit) throw new InvalidDataException(proxyPath + " 不是 64 位 DLL。");
            var sha = FileIntegrity.Sha256(proxyPath);
            var tag = "本地补丁";
            if (knownHashes != null && knownHashes.TryGetValue(sha, out var label)) tag = label;
            return new PatchPayload { ProxyName = proxyName, ProxyPath = proxyPath, IniPath = iniPath, Tag = tag, Commit = "", Sha256 = sha };
        }
    }
}
