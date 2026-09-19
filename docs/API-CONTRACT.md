# Shared implementation contract

Namespace: `Sm86.Manager` (Core) and `Sm86.Manager.App` (UI). Core and tests target `net10.0`, the app targets `net10.0-windows` (WinForms). Nullable annotations are off; C# latest.

Models are in `Manager.Core/Models.cs`. Services throw clear Chinese exceptions for actionable failures; the UI catches and reports per game.

- `PeInspector.Read(string path) : PeInfo` — invalid PE returns null; read only, never loads code.
- `ProxyRecommender.Apply(GameEntry game, string savedSelection = null)` populates RecommendedProxy, SelectedProxy, RecommendationReason.
- `GameScanner.Scan(IEnumerable<string> customRoots, bool includeSteam, CancellationToken, IProgress<string> progress = null) : ScanResult`; `SteamRootProvider` can be overridden for tests; `SteamInstallRoots()` exposes the client roots.
- `GameScanner.FromExecutable(string exePath) : GameEntry` validates an x64 rendering executable and applies the recommendation.
- `PathUtil.Key/DirectoryKey/SamePath` are comparison keys (lower-case, backslashes); `PathUtil.ActualCase` returns the on-disk casing with an upper-case drive letter and is applied to every scanned path.
- `GithubUpdater(string dataDirectory, HttpClient client = null)`; `CheckLatestAsync(CancellationToken) : Task<ReleaseInfo>`; `DownloadAsync(ReleaseInfo, string proxyName, CancellationToken, IProgress<TransferProgress> = null) : Task<PatchPayload>`; `LoadCachedRelease()`, `LoadCachedPayload(release, proxy)`, `KnownHashes()`; `IDisposable`.
- `LocalPackage.Open(string directory, string proxyName, IDictionary<string,string> knownHashes = null) : PatchPayload` loads a proxy from the root or `alternatives\` plus the root INI; no file moves.
- `SteamStoreClient(string dataDirectory, HttpClient client = null)`; `Peek(appId)` (cache only), `GetAsync(appId, CancellationToken)`; localized names via the official store API, cached on disk.
- `InstallerService(string dataDirectory, IGameProcessGuard guard = null)`; `Inspect(GameEntry) : InstalledState`; `Install(GameEntry, PatchPayload) : OperationResult`; `Uninstall(GameEntry)`; `RestoreLastBackup(GameEntry)`; `RecoverPending() : List<string>`; `ListBackups(GameEntry)`; `KnownHashes` labels hand-installed copies.
- `SettingsStore(string dataDirectory)`; `Load() : AppSettings`; `Save(AppSettings)` (atomic write).
- `FileIntegrity.Sha256(path)` and `GitBlobSha1(path)` return lower-case hex.

UI runs scan/disk work in `Task.Run`; network methods are asynchronous. Never start or patch real games in tests; installation tests only use unique temporary directories. Check/update errors never destroy a valid cache. The per-game selected DLL is retained on update, while an explicit install with another selection means switch.

The UI is a real WinForms application: the game list is a single custom-drawn `ScrollableControl`, the caption is integrated into the client area (WM_NCCALCSIZE) while the native frame is kept, and colors follow `Application.SetColorMode` (system / light / dark).
