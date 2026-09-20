using System;
using System.IO;
using System.Linq;
using Sm86.Manager;

namespace Sm86.Manager.Tests
{
    internal static class InstallerTests
    {
        internal sealed class Fixture : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "sm86-tests-" + Guid.NewGuid().ToString("N"));
            public readonly GameEntry Game;
            public readonly PatchPayload Payload;
            public readonly InstallerService Installer;
            public Fixture()
            {
                Directory.CreateDirectory(Root);
                var gameDir = Path.Combine(Root, "游戏 with spaces"); Directory.CreateDirectory(gameDir);
                Game = new GameEntry { Name = "Test", ExePath = Path.Combine(gameDir, "game.exe"), SelectedProxy = "version.dll" };
                File.WriteAllBytes(Game.ExePath, new byte[] { 1, 2, 3 });
                var source = Path.Combine(Root, "source"); Directory.CreateDirectory(source);
                Payload = new PatchPayload { ProxyName = "version.dll", ProxyPath = Path.Combine(source, "version.dll"), IniPath = Path.Combine(source, "dlssg_sm86.ini"), Tag = "0.3.0", Commit = "commit-for-test" };
                File.WriteAllBytes(Payload.ProxyPath, new byte[] { 7, 8, 9, 10 }); File.WriteAllText(Payload.IniPath, "[General]\r\nEnabled=1\r\n");
                Installer = new InstallerService(new NoProcess());
            }
            public string Target(string name) => Path.Combine(Game.DirectoryPath, name);
            public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }
        private sealed class NoProcess : IGameProcessGuard { public void EnsureStopped(GameEntry game) { } }
        private sealed class Running : IGameProcessGuard { public void EnsureStopped(GameEntry game) { throw new IOException("游戏运行中"); } }

        public static void Run()
        {
            Test.Run("install writes selected DLL and INI; state comes from the folder and the payload hash", () =>
            {
                using (var f = new Fixture())
                {
                    Test.True(f.Installer.Install(f.Game, f.Payload).Success);
                    Test.True(File.Exists(f.Target("version.dll"))); Test.True(File.Exists(f.Target("dlssg_sm86.ini")));
                    var st = f.Installer.Inspect(f.Game);
                    Test.True(st.IsInstalled && st.IsManaged); Test.Equal("0.3.0", st.Version); Test.Equal("version.dll", st.ProxyName);
                    // a fresh service that has never seen the hash still sees an installed (unknown-version) patch
                    var fresh = new InstallerService(new NoProcess());
                    var st2 = fresh.Inspect(f.Game);
                    Test.True(st2.IsInstalled && !st2.IsManaged); Test.Equal("未知版本", st2.Version);
                    // and recognises it once the release tree's blob hash is known
                    fresh.KnownBlobs[FileIntegrity.GitBlobSha1(f.Payload.ProxyPath)] = "0.3.0";
                    Test.Equal("0.3.0", fresh.Inspect(f.Game).Version);
                }
            });
            Test.Run("update preserves custom INI bytes and unrelated DLL", () =>
            {
                using (var f = new Fixture())
                {
                    var ini = new byte[] { 0xff, 0xfe, 65, 0, 13, 0 }; File.WriteAllBytes(f.Target("dlssg_sm86.ini"), ini);
                    File.WriteAllText(f.Target("dbghelp.dll"), "Microsoft original"); File.WriteAllText(f.Target("version.dll"), "unknown old patch");
                    var r = f.Installer.Install(f.Game, f.Payload);
                    Test.True(r.Success && r.Message.Contains("已保留原有 INI"), r.Message);
                    Test.True(File.ReadAllBytes(f.Target("dlssg_sm86.ini")).SequenceEqual(ini));
                    Test.Equal("Microsoft original", File.ReadAllText(f.Target("dbghelp.dll")));
                    Test.True(File.ReadAllBytes(f.Target("version.dll")).SequenceEqual(File.ReadAllBytes(f.Payload.ProxyPath)));
                }
            });
            Test.Run("uninstall deletes the deployed DLL and INI, nothing else", () =>
            {
                using (var f = new Fixture())
                {
                    File.WriteAllText(f.Target("dbghelp.dll"), "Microsoft");
                    f.Installer.Install(f.Game, f.Payload);
                    Test.True(f.Installer.Uninstall(f.Game).Success);
                    Test.True(!File.Exists(f.Target("version.dll"))); Test.True(!File.Exists(f.Target("dlssg_sm86.ini")));
                    Test.Equal("Microsoft", File.ReadAllText(f.Target("dbghelp.dll")));
                    Test.True(!f.Installer.Uninstall(f.Game).Success); // nothing left
                    Test.Equal("未安装", f.Installer.Inspect(f.Game).Status);
                }
            });
            Test.Run("switch removes only the previously deployed proxy", () =>
            {
                using (var f = new Fixture())
                {
                    f.Installer.Install(f.Game, f.Payload);
                    File.WriteAllText(f.Target("dbghelp.dll"), "Microsoft");
                    f.Payload.ProxyName = "dxgi.dll"; f.Game.SelectedProxy = "dxgi.dll";
                    var r = f.Installer.Install(f.Game, f.Payload);
                    Test.True(r.Message.Contains("已切换到 dxgi.dll"), r.Message);
                    Test.True(!File.Exists(f.Target("version.dll"))); Test.True(File.Exists(f.Target("dxgi.dll")));
                    Test.Equal("Microsoft", File.ReadAllText(f.Target("dbghelp.dll")));
                    Test.Equal("dxgi.dll", f.Installer.Inspect(f.Game).ProxyName);
                }
            });
            Test.Run("unknown foreign proxy is left alone when switching", () =>
            {
                using (var f = new Fixture())
                {
                    File.WriteAllText(f.Target("winmm.dll"), "somebody else's winmm");
                    f.Installer.Install(f.Game, f.Payload);
                    Test.True(File.Exists(f.Target("winmm.dll")));
                }
            });
            Test.Run("two recognised patch DLLs are reported as ambiguous and both removed on uninstall", () =>
            {
                using (var f = new Fixture())
                {
                    f.Installer.Install(f.Game, f.Payload);
                    File.Copy(f.Target("version.dll"), f.Target("winmm.dll"));
                    var st = f.Installer.Inspect(f.Game);
                    Test.True(st.IsAmbiguous, st.Status);
                    Test.True(f.Installer.Uninstall(f.Game).Success);
                    Test.True(!File.Exists(f.Target("version.dll")) && !File.Exists(f.Target("winmm.dll")));
                }
            });
            Test.Run("game running blocks every write", () =>
            {
                using (var f = new Fixture())
                {
                    var svc = new InstallerService(new Running());
                    Test.Throws<IOException>(() => svc.Install(f.Game, f.Payload));
                    Test.True(!File.Exists(f.Target("version.dll")));
                    Test.Throws<IOException>(() => svc.Uninstall(f.Game));
                }
            });
            Test.Run("pre-existing hand-installed patch matching the selected proxy can be removed", () =>
            {
                using (var f = new Fixture())
                {
                    File.WriteAllText(f.Target("version.dll"), "old"); File.WriteAllText(f.Target("dlssg_sm86.ini"), "custom");
                    var st = f.Installer.Inspect(f.Game);
                    Test.True(st.IsInstalled && !st.IsManaged);
                    Test.True(f.Installer.Uninstall(f.Game).Success);
                    Test.True(!File.Exists(f.Target("version.dll")) && !File.Exists(f.Target("dlssg_sm86.ini")));
                }
            });
            Test.Run("lone INI without any proxy is not an installation", () =>
            {
                using (var f = new Fixture())
                {
                    File.WriteAllText(f.Target("dlssg_sm86.ini"), "custom");
                    Test.True(!f.Installer.Inspect(f.Game).IsInstalled);
                    Test.True(f.Installer.Uninstall(f.Game).Success); // cleans the stray INI
                    Test.True(!File.Exists(f.Target("dlssg_sm86.ini")));
                }
            });
            Test.Run("corrupt payload is refused before touching the game folder", () =>
            {
                using (var f = new Fixture())
                {
                    f.Payload.Sha256 = "deadbeef";
                    Test.Throws<IOException>(() => f.Installer.Install(f.Game, f.Payload));
                    Test.True(!File.Exists(f.Target("version.dll")));
                }
            });
            Test.Run("proxy path traversal rejected", () => { using (var f = new Fixture()) { f.Payload.ProxyName = "../escape.dll"; Test.Throws<ArgumentException>(() => f.Installer.Install(f.Game, f.Payload)); } });
        }
    }
}
