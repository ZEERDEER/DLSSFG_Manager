using System;
using System.IO;
using System.Linq;
using System.Threading;
using Sm86.Manager;

namespace Sm86.Manager.Tests
{
    internal static class ScannerTests
    {
        private sealed class Fixture : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "sm86-scan-" + Guid.NewGuid().ToString("N"));
            public Fixture() { Directory.CreateDirectory(Root); }
            public string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());
            public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }

            /// <summary>Build a Steam root with a second library and manifests for the three known games.</summary>
            public string MakeSteam()
            {
                var steam = P("Steam"); var lib2 = P("Games 库 二", "SteamLibrary");
                Directory.CreateDirectory(Path.Combine(steam, "steamapps")); Directory.CreateDirectory(Path.Combine(lib2, "steamapps"));
                File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                    "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace("\\", "\\\\") + "\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + lib2.Replace("\\", "\\\\") + "\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"1091500\"\t\t\"1\"\n\t\t}\n\t}\n}\n");
                Manifest(steam, "3489700", "Stellar Blade", "StellarBlade");
                FakePe.Write(Path.Combine(steam, "steamapps", "common", "StellarBlade", "SB", "Binaries", "Win64", "SB-Win64-Shipping.exe"));
                FakePe.Write(Path.Combine(steam, "steamapps", "common", "StellarBlade", "SB.exe")); // UE bootstrap launcher at root, should lose
                Manifest(lib2, "1091500", "Cyberpunk 2077", "Cyberpunk 2077");
                FakePe.Write(Path.Combine(lib2, "steamapps", "common", "Cyberpunk 2077", "bin", "x64", "Cyberpunk2077.exe"), imports: new[] { "version.dll", "KERNEL32.dll" });
                FakePe.Write(Path.Combine(lib2, "steamapps", "common", "Cyberpunk 2077", "REDprelauncher.exe"));
                Manifest(lib2, "3681010", "NIOH 3", "Nioh3");
                FakePe.Write(Path.Combine(lib2, "steamapps", "common", "Nioh3", "Nioh3.exe"));
                return steam;
            }
            private static void Manifest(string lib, string appId, string name, string dir)
            {
                Directory.CreateDirectory(Path.Combine(lib, "steamapps"));
                File.WriteAllText(Path.Combine(lib, "steamapps", "appmanifest_" + appId + ".acf"),
                    "\"AppState\"\n{\n\t\"appid\"\t\t\"" + appId + "\"\n\t\"name\"\t\t\"" + name + "\"\n\t\"installdir\"\t\t\"" + dir + "\"\n}\n");
            }
        }

        private static GameScanner Scanner(string steamRoot) => new GameScanner { SteamRootProvider = () => steamRoot == null ? new string[0] : new[] { steamRoot } };

        public static void Run()
        {
            Test.Run("steam libraries: three known games located with default proxies", () =>
            {
                using (var f = new Fixture())
                {
                    var r = Scanner(f.MakeSteam()).Scan(new string[0], true, CancellationToken.None);
                    Test.Equal(3, r.Games.Count);
                    var sb = r.Games.Single(g => g.SteamAppId == "3489700"); var cp = r.Games.Single(g => g.SteamAppId == "1091500"); var nioh = r.Games.Single(g => g.SteamAppId == "3681010");
                    Test.True(sb.ExePath.EndsWith("SB-Win64-Shipping.exe"), sb.ExePath); Test.Equal("version.dll", sb.SelectedProxy); Test.Equal("剑星", sb.Name);
                    Test.True(cp.ExePath.EndsWith("Cyberpunk2077.exe"), cp.ExePath); Test.Equal("dxgi.dll", cp.SelectedProxy); Test.Equal("赛博朋克 2077", cp.Name);
                    Test.True(nioh.ExePath.EndsWith("Nioh3.exe")); Test.Equal("winmm.dll", nioh.SelectedProxy);
                    Test.Equal("已知游戏规则", cp.LocatedBy);
                    Test.True(r.Games.All(g => g.CandidateExes.Count == 0), "known rules must not report ambiguity");
                }
            });
            Test.Run("known game without appid recognised by exe path, static import does not override rule", () =>
            {
                using (var f = new Fixture())
                {
                    var exe = f.P("custom", "Cyberpunk 2077", "bin", "x64", "Cyberpunk2077.exe");
                    FakePe.Write(exe, imports: new[] { "version.dll" });
                    FakePe.Write(f.P("custom", "Cyberpunk 2077", "REDprelauncher.exe"));
                    var r = Scanner(null).Scan(new[] { f.P("custom") }, false, CancellationToken.None);
                    Test.Equal(1, r.Games.Count);
                    Test.Equal("dxgi.dll", r.Games[0].RecommendedProxy); Test.Equal("1091500", r.Games[0].SteamAppId);
                }
            });
            Test.Run("custom root that is itself one UE game: shipping exe wins, launcher and 32-bit ignored", () =>
            {
                using (var f = new Fixture())
                {
                    var game = f.P("我的 游戏", "Some Game");
                    Directory.CreateDirectory(Path.Combine(game, "Engine"));
                    FakePe.Write(Path.Combine(game, "SomeGame.exe"));
                    FakePe.Write(Path.Combine(game, "SG", "Binaries", "Win64", "SG-Win64-Shipping.exe"), imports: new[] { "dxgi.dll", "winmm.dll" });
                    FakePe.Write(Path.Combine(game, "SG", "Binaries", "Win64", "CrashReportClient.exe"));
                    FakePe.Write(Path.Combine(game, "SG", "Binaries", "Win32", "SG-Win32-Shipping.exe"), is64: false);
                    var r = Scanner(null).Scan(new[] { game }, false, CancellationToken.None);
                    Test.Equal(1, r.Games.Count);
                    Test.True(r.Games[0].ExePath.EndsWith("SG-Win64-Shipping.exe"), r.Games[0].ExePath);
                    Test.Equal("Some Game", r.Games[0].Name);
                    Test.Equal("winmm.dll", r.Games[0].RecommendedProxy); // upstream order beats dxgi when both imported
                    Test.True(r.Games[0].RecommendationReason.Contains("导入表"));
                }
            });
            Test.Run("custom root as folder of games; default version.dll marked pending verification", () =>
            {
                using (var f = new Fixture())
                {
                    FakePe.Write(f.P("lib", "Alpha", "Alpha.exe"));
                    FakePe.Write(f.P("lib", "Beta", "bin", "Beta.exe"), delayImports: new[] { "dbghelp.dll" });
                    FakePe.Write(f.P("lib", "_CommonRedist", "vcredist_x64.exe"));
                    Directory.CreateDirectory(f.P("lib", "Empty"));
                    var r = Scanner(null).Scan(new[] { f.P("lib") }, false, CancellationToken.None);
                    Test.Equal(2, r.Games.Count);
                    var alpha = r.Games.Single(g => g.Name == "Alpha"); var beta = r.Games.Single(g => g.Name == "Beta");
                    Test.Equal("version.dll", alpha.RecommendedProxy); Test.True(alpha.RecommendationReason.Contains("待验证"));
                    Test.Equal("dbghelp.dll", beta.RecommendedProxy);
                }
            });
            Test.Run("duplicate paths from steam and custom roots merge", () =>
            {
                using (var f = new Fixture())
                {
                    var steam = f.MakeSteam();
                    var r = Scanner(steam).Scan(new[] { Path.Combine(steam, "steamapps", "common"), f.P("missing") }, true, CancellationToken.None);
                    Test.Equal(3, r.Games.Count);
                    Test.True(r.Warnings.Any(w => w.Contains("目录不存在")));
                }
            });
            Test.Run("ambiguous candidates are reported for user choice", () =>
            {
                using (var f = new Fixture())
                {
                    FakePe.Write(f.P("g", "GameA.exe")); FakePe.Write(f.P("g", "GameB.exe"));
                    var r = Scanner(null).Scan(new[] { f.P("g") }, false, CancellationToken.None);
                    Test.Equal(1, r.Games.Count); Test.Equal(2, r.Games[0].CandidateExes.Count);
                }
            });
            Test.Run("FromExecutable validates and applies saved-independent recommendation", () =>
            {
                using (var f = new Fixture())
                {
                    var exe = f.P("m", "Nioh3.exe"); FakePe.Write(exe);
                    var g = new GameScanner().FromExecutable(exe);
                    Test.Equal("仁王 3", g.Name); Test.Equal("winmm.dll", g.SelectedProxy); Test.Equal("手动选择", g.LocatedBy);
                    var x86 = f.P("m", "old.exe"); FakePe.Write(x86, is64: false);
                    Test.Throws<InvalidDataException>(() => new GameScanner().FromExecutable(x86));
                    var dll = f.P("m", "lib.dll"); FakePe.Write(dll, isDll: true);
                    Test.Throws<InvalidDataException>(() => new GameScanner().FromExecutable(dll));
                    File.WriteAllText(f.P("m", "text.exe"), "not a pe file at all, but long enough to pass the size check.............................................................................................................................................................................................");
                    Test.Throws<InvalidDataException>(() => new GameScanner().FromExecutable(f.P("m", "text.exe")));
                }
            });
            Test.Run("saved selection overrides recommendation", () =>
            {
                using (var f = new Fixture())
                {
                    var exe = f.P("s", "Nioh3.exe"); FakePe.Write(exe);
                    var g = new GameEntry { ExePath = exe };
                    ProxyRecommender.Apply(g, "d3d12.dll");
                    Test.Equal("winmm.dll", g.RecommendedProxy); Test.Equal("d3d12.dll", g.SelectedProxy);
                    ProxyRecommender.Apply(g, "bogus.dll"); Test.Equal("winmm.dll", g.SelectedProxy);
                }
            });
            Test.Run("PE inspector reads imports and delay imports, rejects garbage", () =>
            {
                using (var f = new Fixture())
                {
                    var exe = f.P("pe", "a.exe"); FakePe.Write(exe, imports: new[] { "KERNEL32.dll", "dxgi.dll" }, delayImports: new[] { "d3d12.dll" });
                    var pe = PeInspector.Read(exe);
                    Test.True(pe.Is64Bit && !pe.IsDll); Test.True(pe.Imports.Contains("dxgi.dll")); Test.True(pe.DelayImports.Contains("d3d12.dll"));
                    File.WriteAllBytes(f.P("pe", "junk.exe"), new byte[4096]);
                    Test.True(PeInspector.Read(f.P("pe", "junk.exe")) == null);
                    Test.True(PeInspector.Read(f.P("pe", "nope.exe")) == null);
                }
            });
            Test.Run("cancellation stops the scan", () =>
            {
                using (var f = new Fixture())
                {
                    FakePe.Write(f.P("c", "A", "a.exe"));
                    var cts = new CancellationTokenSource(); cts.Cancel();
                    Test.Throws<OperationCanceledException>(() => Scanner(null).Scan(new[] { f.P("c") }, false, cts.Token));
                }
            });
            Test.Run("libraryfolders.vdf parsing handles old and new formats", () =>
            {
                var paths = GameScanner.ParseLibraryFolders("\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\"\t\"123\"\n\t\"1\"\t\t\"D:\\\\SteamLibrary\"\n\t\"2\"\n\t{\n\t\t\"path\"\t\t\"E:\\\\Games\\\\Steam\"\n\t}\n}");
                Test.Equal(2, paths.Count); Test.Equal("D:\\SteamLibrary", paths[0]); Test.Equal("E:\\Games\\Steam", paths[1]);
            });
            Test.Run("ActualCase restores on-disk casing and upper-cases the drive letter", () =>
            {
                using (var f = new Fixture())
                {
                    var exe = f.P("Games Lib", "MyGame", "Bin", "Game.exe"); FakePe.Write(exe);
                    var lowered = Path.Combine(f.Root, "games lib", "mygame", "bin", "game.exe");
                    var fixedPath = PathUtil.ActualCase(lowered);
                    Test.True(fixedPath.EndsWith(Path.Combine("Games Lib", "MyGame", "Bin", "Game.exe")), fixedPath);
                    var root = Path.GetPathRoot(fixedPath);
                    if (root.Length >= 2 && root[1] == ':') Test.True(char.IsUpper(root[0]), root);
                    var missing = Path.Combine(f.Root, "Games Lib", "nope", "x.exe");
                    Test.Equal(missing, PathUtil.ActualCase(missing)); // unresolvable segments are kept as given
                    Test.Equal("", PathUtil.ActualCase(""));
                    var scanned = new GameScanner().FromExecutable(fixedPath); // (Linux hosts are case-sensitive, so use the resolved path)
                    Test.True(scanned.ExePath.EndsWith("Game.exe"), scanned.ExePath);
                }
            });
            Test.Run("settings round-trip keeps games and selections", () =>
            {
                using (var f = new Fixture())
                {
                    var store = new SettingsStore(f.P("data"));
                    var s = new AppSettings { Theme = "dark" };
                    s.ScanFolders.Add(f.P("x")); s.Games.Add(new GameEntry { Name = "n", ExePath = f.P("x", "n.exe"), SelectedProxy = "dxgi.dll", Selected = true });
                    store.Save(s);
                    var back = store.Load();
                    Test.Equal("dark", back.Theme); Test.Equal(1, back.Games.Count); Test.Equal("dxgi.dll", back.Games[0].SelectedProxy); Test.True(back.Games[0].Selected);
                    File.WriteAllText(store.FilePath, "{ not json");
                    Test.Throws<IOException>(() => store.Load());
                }
            });
        }
    }
}
