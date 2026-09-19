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
                Installer = new InstallerService(Path.Combine(Root, "data"), new NoProcess());
            }
            public string Target(string name) => Path.Combine(Game.DirectoryPath, name);
            public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }
        private sealed class NoProcess : IGameProcessGuard { public void EnsureStopped(GameEntry game) { } }
        private sealed class Running : IGameProcessGuard { public void EnsureStopped(GameEntry game) { throw new IOException("游戏运行中"); } }
        public static void Run()
        {
            Test.Run("install writes selected DLL and INI and records version", () => { using (var f = new Fixture()) { Test.True(f.Installer.Install(f.Game, f.Payload).Success); Test.True(File.Exists(f.Target("version.dll"))); Test.True(File.Exists(f.Target("dlssg_sm86.ini"))); Test.Equal("0.3.0", f.Installer.Inspect(f.Game).Version); } });
            Test.Run("update preserves custom INI bytes and unrelated DLL", () => { using (var f = new Fixture()) { var ini = new byte[] { 0xff, 0xfe, 65, 0, 13, 0 }; File.WriteAllBytes(f.Target("dlssg_sm86.ini"), ini); File.WriteAllText(f.Target("dbghelp.dll"), "Microsoft original"); File.WriteAllText(f.Target("version.dll"), "unknown old patch"); f.Installer.Install(f.Game, f.Payload); Test.True(File.ReadAllBytes(f.Target("dlssg_sm86.ini")).SequenceEqual(ini)); Test.Equal("Microsoft original", File.ReadAllText(f.Target("dbghelp.dll"))); } });
            Test.Run("uninstall removes managed patch but never restores old patch", () => { using (var f = new Fixture()) { File.WriteAllText(f.Target("version.dll"), "unknown old patch"); f.Installer.Install(f.Game, f.Payload); Test.True(f.Installer.Uninstall(f.Game).Success); Test.True(!File.Exists(f.Target("version.dll"))); Test.True(!File.Exists(f.Target("dlssg_sm86.ini"))); } });
            Test.Run("uninstall conflict keeps DLL and INI together", () => { using (var f = new Fixture()) { f.Installer.Install(f.Game, f.Payload); File.WriteAllText(f.Target("version.dll"), "externally replaced"); Test.Throws<IOException>(() => f.Installer.Uninstall(f.Game)); Test.True(File.Exists(f.Target("dlssg_sm86.ini"))); } });
            Test.Run("explicit update overwrites externally modified selected DLL after backup", () => { using (var f = new Fixture()) { f.Installer.Install(f.Game, f.Payload); File.WriteAllText(f.Target("version.dll"), "external old patch"); Test.True(f.Installer.Install(f.Game, f.Payload).Success); Test.True(File.ReadAllBytes(f.Target("version.dll")).SequenceEqual(File.ReadAllBytes(f.Payload.ProxyPath))); } });
            Test.Run("switch removes only previous tracked proxy", () => { using (var f = new Fixture()) { f.Installer.Install(f.Game, f.Payload); File.WriteAllText(f.Target("dbghelp.dll"), "Microsoft"); f.Payload.ProxyName = "dxgi.dll"; f.Game.SelectedProxy = "dxgi.dll"; f.Installer.Install(f.Game, f.Payload); Test.True(!File.Exists(f.Target("version.dll"))); Test.True(File.Exists(f.Target("dxgi.dll"))); Test.Equal("Microsoft", File.ReadAllText(f.Target("dbghelp.dll"))); } });
            Test.Run("switch refuses modified previous proxy", () => { using (var f = new Fixture()) { f.Installer.Install(f.Game, f.Payload); File.WriteAllText(f.Target("version.dll"), "changed"); f.Payload.ProxyName = "dxgi.dll"; Test.Throws<IOException>(() => f.Installer.Install(f.Game, f.Payload)); Test.True(!File.Exists(f.Target("dxgi.dll"))); } });
            Test.Run("game running blocks every write", () => { using (var f = new Fixture()) { var svc = new InstallerService(Path.Combine(f.Root,"other-data"),new Running()); Test.Throws<IOException>(() => svc.Install(f.Game, f.Payload)); Test.True(!File.Exists(f.Target("version.dll"))); } });
            Test.Run("locked INI blocks uninstall without losing DLL", () => { using (var f = new Fixture()) { f.Installer.Install(f.Game, f.Payload); using (var locked = new FileStream(f.Target("dlssg_sm86.ini"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Test.Throws<IOException>(() => f.Installer.Uninstall(f.Game)); Test.True(File.Exists(f.Target("version.dll"))); } });
            Test.Run("restore backup reverses uninstall", () => { using (var f = new Fixture()) { f.Installer.Install(f.Game,f.Payload); f.Installer.Uninstall(f.Game); f.Installer.RestoreLastBackup(f.Game); Test.True(File.Exists(f.Target("version.dll"))); Test.True(File.Exists(f.Target("dlssg_sm86.ini"))); Test.True(f.Installer.Inspect(f.Game).IsManaged); } });
            Test.Run("selected pre-existing patch can be removed directly", () => { using (var f = new Fixture()) { File.WriteAllText(f.Target("version.dll"), "old"); File.WriteAllText(f.Target("dlssg_sm86.ini"), "custom"); Test.True(f.Installer.Uninstall(f.Game).Success); Test.True(!File.Exists(f.Target("version.dll"))); } });
            Test.Run("proxy path traversal rejected", () => { using (var f = new Fixture()) { f.Payload.ProxyName = "../escape.dll"; Test.Throws<ArgumentException>(() => f.Installer.Install(f.Game, f.Payload)); } });
            Test.Run("empty recovery has no work", () => { using (var f = new Fixture()) Test.Equal(0, f.Installer.RecoverPending().Count); });
        }
    }
}
