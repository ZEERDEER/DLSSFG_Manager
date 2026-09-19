using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sm86.Manager;

namespace Sm86.Manager.Tests
{
    internal static class UpdaterTests
    {
        /// <summary>In-memory GitHub: one release, an annotated tag, a tree and raw blobs.</summary>
        private sealed class FakeGithub : HttpMessageHandler
        {
            public const string Tag = "0.3.0", TagObject = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Commit = "0123456789abcdef0123456789abcdef01234567";
            public readonly Dictionary<string, byte[]> Blobs = new Dictionary<string, byte[]>();
            public readonly Dictionary<string, byte[]> Served = new Dictionary<string, byte[]>();
            public int Requests; public bool RateLimited; public bool AnnotatedTag = true;
            public FakeGithub()
            {
                var dll = new MemoryStream(); var tmp = Path.GetTempFileName(); FakePe.Write(tmp, isDll: true, padding: 3000); Blobs["version.dll"] = File.ReadAllBytes(tmp);
                FakePe.Write(tmp, isDll: true, padding: 100); Blobs["alternatives/dxgi.dll"] = File.ReadAllBytes(tmp); File.Delete(tmp);
                Blobs["dlssg_sm86.ini"] = Encoding.UTF8.GetBytes("[General]\nEnabled=1\n");
                Blobs["THIRD_PARTY_NOTICES.txt"] = Encoding.UTF8.GetBytes("notices");
                Blobs["archive/0.1.0/version.dll"] = new byte[] { 1, 2, 3 };
                foreach (var kv in Blobs) Served[kv.Key] = kv.Value;
            }
            public static string BlobSha(byte[] data)
            {
                using (var sha = SHA1.Create())
                {
                    var header = Encoding.ASCII.GetBytes("blob " + data.Length + "\0");
                    var all = new byte[header.Length + data.Length]; Buffer.BlockCopy(header, 0, all, 0, header.Length); Buffer.BlockCopy(data, 0, all, header.Length, data.Length);
                    return BitConverter.ToString(sha.ComputeHash(all)).Replace("-", "").ToLowerInvariant();
                }
            }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                var url = request.RequestUri.ToString();
                if (RateLimited)
                {
                    var r = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"API rate limit exceeded\"}") };
                    r.Headers.Add("X-RateLimit-Remaining", "0"); r.Headers.Add("X-RateLimit-Reset", "1900000000");
                    return Task.FromResult(r);
                }
                string json = null;
                if (url.EndsWith("/releases/latest")) json = "{\"tag_name\":\"" + Tag + "\",\"html_url\":\"https://example/rel\",\"body\":\"notes\"}";
                else if (url.EndsWith("/git/ref/tags/" + Tag)) json = AnnotatedTag ? "{\"object\":{\"sha\":\"" + TagObject + "\",\"type\":\"tag\"}}" : "{\"object\":{\"sha\":\"" + Commit + "\",\"type\":\"commit\"}}";
                else if (url.EndsWith("/git/tags/" + TagObject)) json = "{\"object\":{\"sha\":\"" + Commit + "\",\"type\":\"commit\"}}";
                else if (url.Contains("/git/trees/" + Commit))
                {
                    var sb = new StringBuilder("{\"truncated\":false,\"tree\":[");
                    bool first = true;
                    foreach (var kv in Blobs) { if (!first) sb.Append(','); first = false; sb.Append("{\"path\":\"" + kv.Key + "\",\"type\":\"blob\",\"sha\":\"" + BlobSha(kv.Value) + "\",\"size\":" + kv.Value.Length + "}"); }
                    sb.Append(",{\"path\":\"alternatives\",\"type\":\"tree\",\"sha\":\"x\"}]}");
                    json = sb.ToString();
                }
                else if (url.Contains("/" + Commit + "/"))
                {
                    var path = url.Substring(url.IndexOf("/" + Commit + "/") + Commit.Length + 2);
                    if (Served.TryGetValue(path, out var data)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404") });
                }
                if (json == null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no route: " + url) });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
        }

        private sealed class Fixture : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "sm86-upd-" + Guid.NewGuid().ToString("N"));
            public readonly FakeGithub Github = new FakeGithub();
            public readonly GithubUpdater Updater;
            public Fixture() { Directory.CreateDirectory(Root); Updater = new GithubUpdater(Path.Combine(Root, "data"), new HttpClient(Github)); }
            public void Dispose() { Updater.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }

        public static void Run()
        {
            Test.Run("check latest resolves annotated tag to commit and lists files", () =>
            {
                using (var f = new Fixture())
                {
                    var rel = f.Updater.CheckLatestAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Test.Equal(FakeGithub.Tag, rel.Tag); Test.Equal(FakeGithub.Commit, rel.Commit);
                    Test.True(rel.Files.ContainsKey("version.dll") && rel.Files.ContainsKey("alternatives/dxgi.dll"));
                    Test.True(GithubUpdater.FindRemote(rel, "dxgi.dll").RelativePath == "alternatives/dxgi.dll");
                    Test.True(GithubUpdater.FindRemote(rel, "winmm.dll") == null);
                    var cached = f.Updater.LoadCachedRelease();
                    Test.Equal(FakeGithub.Commit, cached.Commit); Test.True(cached.CheckedAtUtc > DateTime.UtcNow.AddMinutes(-1));
                }
            });
            Test.Run("lightweight tag resolves directly", () =>
            {
                using (var f = new Fixture())
                {
                    f.Github.AnnotatedTag = false;
                    Test.Equal(FakeGithub.Commit, f.Updater.CheckLatestAsync(CancellationToken.None).GetAwaiter().GetResult().Commit);
                }
            });
            Test.Run("download verifies blob hash, caches, records known hash, second download is offline", () =>
            {
                using (var f = new Fixture())
                {
                    var rel = f.Updater.CheckLatestAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var payload = f.Updater.DownloadAsync(rel, "dxgi.dll", CancellationToken.None).GetAwaiter().GetResult();
                    Test.Equal("dxgi.dll", payload.ProxyName); Test.Equal(FakeGithub.Tag, payload.Tag); Test.Equal(FakeGithub.Commit, payload.Commit);
                    Test.True(File.ReadAllBytes(payload.ProxyPath).Length == f.Github.Blobs["alternatives/dxgi.dll"].Length);
                    Test.Equal(FileIntegrity.Sha256(payload.ProxyPath), payload.Sha256);
                    Test.True(File.Exists(Path.Combine(Path.GetDirectoryName(payload.ProxyPath), GithubUpdater.ThirdPartyNoticesName)));
                    Test.True(f.Updater.KnownHashes().ContainsKey(payload.Sha256));
                    int before = f.Github.Requests;
                    var again = f.Updater.DownloadAsync(rel, "dxgi.dll", CancellationToken.None).GetAwaiter().GetResult();
                    Test.Equal(before, f.Github.Requests); Test.Equal(payload.ProxyPath, again.ProxyPath);
                    Test.True(f.Updater.LoadCachedPayload(rel, "dxgi.dll") != null); Test.True(f.Updater.LoadCachedPayload(rel, "version.dll") == null);
                }
            });
            Test.Run("corrupted download is rejected and leaves no file", () =>
            {
                using (var f = new Fixture())
                {
                    var rel = f.Updater.CheckLatestAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var bad = (byte[])f.Github.Blobs["version.dll"].Clone(); bad[bad.Length - 1] ^= 0xFF; f.Github.Served["version.dll"] = bad;
                    var ex = Test.Throws<IOException>(() => f.Updater.DownloadAsync(rel, "version.dll", CancellationToken.None).GetAwaiter().GetResult());
                    Test.True(ex.Message.Contains("校验失败"), ex.Message);
                    Test.True(!File.Exists(Path.Combine(f.Updater.CacheDirectory, FakeGithub.Tag + "-" + FakeGithub.Commit.Substring(0, 12), "version.dll")));
                    Test.True(f.Updater.LoadCachedPayload(rel, "version.dll") == null);
                }
            });
            Test.Run("rate limit yields a clear message and keeps cached release", () =>
            {
                using (var f = new Fixture())
                {
                    f.Updater.CheckLatestAsync(CancellationToken.None).GetAwaiter().GetResult();
                    f.Github.RateLimited = true;
                    var ex = Test.Throws<IOException>(() => f.Updater.CheckLatestAsync(CancellationToken.None).GetAwaiter().GetResult());
                    Test.True(ex.Message.Contains("请求次数"), ex.Message);
                    Test.True(f.Updater.LoadCachedRelease() != null);
                }
            });
            Test.Run("unsupported proxy and missing proxy in release are refused", () =>
            {
                using (var f = new Fixture())
                {
                    var rel = f.Updater.CheckLatestAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Test.Throws<ArgumentException>(() => f.Updater.DownloadAsync(rel, "evil.dll", CancellationToken.None).GetAwaiter().GetResult());
                    Test.Throws<IOException>(() => f.Updater.DownloadAsync(rel, "winmm.dll", CancellationToken.None).GetAwaiter().GetResult());
                }
            });
            Test.Run("git blob sha matches reference implementation", () =>
            {
                var tmp = Path.GetTempFileName();
                try { File.WriteAllText(tmp, "hello\n"); Test.Equal("ce013625030ba8dba906f756967f9e9ca394464a", FileIntegrity.GitBlobSha1(tmp)); }
                finally { File.Delete(tmp); }
            });
            Test.Run("local package opens root or alternatives proxy and validates", () =>
            {
                using (var f = new Fixture())
                {
                    var dir = Path.Combine(f.Root, "local 补丁"); Directory.CreateDirectory(Path.Combine(dir, "alternatives"));
                    File.WriteAllBytes(Path.Combine(dir, "version.dll"), f.Github.Blobs["version.dll"]);
                    File.WriteAllBytes(Path.Combine(dir, "alternatives", "dxgi.dll"), f.Github.Blobs["alternatives/dxgi.dll"]);
                    File.WriteAllText(Path.Combine(dir, "dlssg_sm86.ini"), "[General]\nEnabled=1\n");
                    var p = LocalPackage.Open(dir, "dxgi.dll");
                    Test.True(p.ProxyPath.EndsWith("dxgi.dll")); Test.Equal("本地补丁", p.Tag);
                    var known = new Dictionary<string, string> { { FileIntegrity.Sha256(Path.Combine(dir, "version.dll")), "0.3.0 (0123456)" } };
                    Test.Equal("0.3.0 (0123456)", LocalPackage.Open(dir, "version.dll", known).Tag);
                    Test.Throws<FileNotFoundException>(() => LocalPackage.Open(dir, "winmm.dll"));
                    File.WriteAllText(Path.Combine(dir, "winmm.dll"), "not a dll but long enough .................................................................................................................................................................................................................................");
                    Test.Throws<InvalidDataException>(() => LocalPackage.Open(dir, "winmm.dll"));
                }
            });
            Test.Run("steam store client caches hits and misses and backs off on 429", () =>
            {
                using (var f = new Fixture())
                {
                    var handler = new FakeStore();
                    using (var store = new SteamStoreClient(Path.Combine(f.Root, "data"), new HttpClient(handler)) { RequestGap = TimeSpan.Zero })
                    {
                        var cp = store.GetAsync("1091500", CancellationToken.None).GetAwaiter().GetResult();
                        Test.Equal("赛博朋克 2077", cp.Name); Test.True(cp.Found);
                        Test.Equal(1, handler.Requests);
                        Test.Equal("赛博朋克 2077", store.GetAsync("1091500", CancellationToken.None).GetAwaiter().GetResult().Name);
                        Test.Equal(1, handler.Requests); // served from cache
                        var miss = store.GetAsync("999", CancellationToken.None).GetAwaiter().GetResult();
                        Test.True(miss != null && !miss.Found);
                        Test.True(store.Peek("999") != null && !store.Peek("999").Found);
                        handler.RateLimited = true;
                        Test.True(store.GetAsync("3681010", CancellationToken.None).GetAwaiter().GetResult() == null);
                        handler.RateLimited = false; int before = handler.Requests;
                        Test.True(store.GetAsync("3681010", CancellationToken.None).GetAwaiter().GetResult() == null); // backing off, no request
                        Test.Equal(before, handler.Requests);
                    }
                    using (var store2 = new SteamStoreClient(Path.Combine(f.Root, "data"), new HttpClient(handler)))
                        Test.Equal("赛博朋克 2077", store2.Peek("1091500").Name); // persisted on disk
                }
            });
        }

        private sealed class FakeStore : HttpMessageHandler
        {
            public int Requests; public bool RateLimited;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                if (RateLimited) return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429));
                var url = request.RequestUri.ToString();
                string json = url.Contains("appids=1091500")
                    ? "{\"1091500\":{\"success\":true,\"data\":{\"type\":\"game\",\"name\":\"赛博朋克 2077\",\"header_image\":\"https://x/h.jpg\"}}}"
                    : url.Contains("appids=3681010") ? "{\"3681010\":{\"success\":true,\"data\":{\"type\":\"game\",\"name\":\"仁王3\"}}}" : "{\"999\":{\"success\":false}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
        }
    }
}
