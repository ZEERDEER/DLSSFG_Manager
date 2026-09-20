# Shared implementation contract

Namespace: `Sm86.Manager` (Core) and `Sm86.Manager.App` (UI). Core and tests target `net10.0`, the app targets `net10.0-windows` (WinForms). Nullable annotations are off; C# latest.

Design rule since 1.1.0: **nothing is persisted**. No settings file, no install records, no backups, no download cache on disk. Everything is derived from the game folders and the upstream release each time the app runs; downloads live in the session temp folder and are deleted on exit.

Models are in `Manager.Core/Models.cs`. Services throw clear Chinese exceptions for actionable failures; the UI catches and reports per game.

- `PeInspector.Read(string path) : PeInfo` — invalid PE returns null; read only, never loads code.
- `ProxyRecommender.Apply(GameEntry game, string savedSelection = null)` populates RecommendedProxy, SelectedProxy, RecommendationReason.
- `GameScanner.Scan(IEnumerable<string> customRoots, bool includeSteam, CancellationToken, IProgress<string> progress = null) : ScanResult`; `SteamRootProvider` can be overridden for tests; `SteamInstallRoots()` exposes the client roots.
- `GameScanner.FromExecutable(string exePath) : GameEntry` validates an x64 rendering executable and applies the recommendation.
- `PathUtil.Key/DirectoryKey/SamePath` are comparison keys (lower-case, backslashes); `PathUtil.ActualCase` returns the on-disk casing with an upper-case drive letter and is applied to every scanned path.
- `GithubUpdater(string tempDirectory = null, HttpClient client = null)`; `CheckLatestAsync(CancellationToken) : Task<ReleaseInfo>`; `DownloadAsync(ReleaseInfo, string proxyName, CancellationToken, IProgress<TransferProgress> = null) : Task<PatchPayload>` (verified, reused within the session); `ProxyBlobs(release)` maps git blob sha → tag for every proxy in the release; `KnownHashes` (sha256 → tag, this session); `Cleanup()`; `IDisposable`.
- `SteamStoreClient(HttpClient client = null)`; `Peek(appId)` (memory only), `GetAsync(appId, CancellationToken)`; localized names via the official store API.
- `InstallerService(IGameProcessGuard guard = null)`; `KnownHashes` / `KnownBlobs` (hash → version label) feed `Identify(path)`; `Inspect(GameEntry) : InstalledState` reads the folder (a proxy is "ours" when its hash is known; otherwise selected proxy + INI = installed with unknown version; two candidates = ambiguous); `Install(GameEntry, PatchPayload)` copies the DLL (staged, verified) and the INI when missing, removing other recognised proxies; `Uninstall(GameEntry)` deletes recognised/selected proxies and the INI.
- `FileIntegrity.Sha256(path)` and `GitBlobSha1(path)` return lower-case hex.

UI runs scan/disk work in `Task.Run`; network methods are asynchronous. Never start or patch real games in tests; installation tests only use unique temporary directories.

The UI is a real WinForms application: the game list is a single custom-drawn control with its own scrolling (all geometry in client coordinates so GDI text and GDI+ shapes stay aligned), the caption is integrated into the client area (WM_NCCALCSIZE) while the native frame is kept, and colors follow `Application.SetColorMode(SystemColorMode.System)`.
