using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed class SteamAppInfo
    {
        public string AppId { get; set; } = "";
        /// <summary>Localized (zh-CN) store name; empty when the store had no entry.</summary>
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string HeaderImage { get; set; } = "";
        public DateTime FetchedAtUtc { get; set; }
        public bool Found => !string.IsNullOrEmpty(Name);
    }

    /// <summary>
    /// Chinese display names from the official Steam store API (appdetails, l=schinese). SteamDB has no public API and forbids
    /// scraping, so it is deliberately not used. Results are cached on disk; misses are cached for a day, hits for 30 days.
    /// Requests are serialized with a small gap and back off for an hour after a 429.
    /// </summary>
    public sealed class SteamStoreClient : IDisposable
    {
        public string StoreBase { get; set; } = "https://store.steampowered.com";
        public TimeSpan HitTtl { get; set; } = TimeSpan.FromDays(30);
        public TimeSpan MissTtl { get; set; } = TimeSpan.FromDays(1);
        public TimeSpan RequestGap { get; set; } = TimeSpan.FromMilliseconds(300);

        private readonly string _cachePath;
        private readonly HttpClient _client;
        private readonly bool _ownsClient;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _lock = new object();
        private Dictionary<string, SteamAppInfo> _cache;
        private DateTime _backoffUntilUtc = DateTime.MinValue;
        private DateTime _lastRequestUtc = DateTime.MinValue;

        public SteamStoreClient(string dataDirectory, HttpClient client = null)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("数据目录不能为空。", nameof(dataDirectory));
            var dir = Path.Combine(Path.GetFullPath(dataDirectory), "steam");
            Directory.CreateDirectory(dir);
            _cachePath = Path.Combine(dir, "apps.json");
            _ownsClient = client == null;
            _client = client ?? CreateClient();
        }

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DLSSFG-Manager", "1.0"));
            c.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("zh-CN"));
            return c;
        }

        /// <summary>Cached entry without any network access; null when unknown or expired.</summary>
        public SteamAppInfo Peek(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return null;
            lock (_lock)
            {
                Load();
                return _cache.TryGetValue(appId, out var info) && !Expired(info) ? info : null;
            }
        }

        public async Task<SteamAppInfo> GetAsync(string appId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(appId)) return null;
            var cached = Peek(appId);
            if (cached != null) return cached;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cached = Peek(appId);
                if (cached != null) return cached;
                if (DateTime.UtcNow < _backoffUntilUtc) return null;
                var wait = RequestGap - (DateTime.UtcNow - _lastRequestUtc);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                _lastRequestUtc = DateTime.UtcNow;
                var url = StoreBase + "/api/appdetails?appids=" + Uri.EscapeDataString(appId) + "&l=schinese&cc=cn&filters=basic";
                string body;
                using (var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        _backoffUntilUtc = DateTime.UtcNow.AddHours(1);
                        return null;
                    }
                    if (!response.IsSuccessStatusCode) return null;
                    body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                var info = Parse(appId, body);
                lock (_lock) { Load(); _cache[appId] = info; Save(); }
                return info;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            finally { _gate.Release(); }
        }

        public static SteamAppInfo Parse(string appId, string json)
        {
            var info = new SteamAppInfo { AppId = appId, FetchedAtUtc = DateTime.UtcNow };
            try
            {
                var root = JObject.Parse(json);
                var entry = root[appId] as JObject;
                if (entry?["success"]?.Value<bool>() == true && entry["data"] is JObject data)
                {
                    info.Name = ((string)data["name"] ?? "").Trim();
                    info.Type = (string)data["type"] ?? "";
                    info.HeaderImage = (string)data["header_image"] ?? "";
                }
            }
            catch (JsonException) { }
            return info;
        }

        private bool Expired(SteamAppInfo info) => DateTime.UtcNow - info.FetchedAtUtc > (info.Found ? HitTtl : MissTtl);

        private void Load()
        {
            if (_cache != null) return;
            _cache = new Dictionary<string, SteamAppInfo>(StringComparer.Ordinal);
            if (!File.Exists(_cachePath)) return;
            try
            {
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, SteamAppInfo>>(File.ReadAllText(_cachePath, Encoding.UTF8));
                if (loaded != null) foreach (var kv in loaded) if (kv.Value != null) _cache[kv.Key] = kv.Value;
            }
            catch (JsonException) { }
        }

        private void Save()
        {
            try { AtomicFile.WriteAllText(_cachePath, JsonConvert.SerializeObject(_cache, Formatting.Indented)); }
            catch (IOException) { }
        }

        public void Dispose() { if (_ownsClient) _client.Dispose(); _gate.Dispose(); }
    }
}
