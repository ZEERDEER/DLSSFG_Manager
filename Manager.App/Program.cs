using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Sm86.Manager.App
{
    internal static class Program
    {
        private static string _crashLog;
        private static Mutex _single;

        [STAThread]
        private static int Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"), "未处理的异常");
            var baseDir = AppContext.BaseDirectory;
            _crashLog = Path.Combine(baseDir, "crash.log");
            try
            {
                var dataDir = Path.Combine(baseDir, "data");
                try { Directory.CreateDirectory(dataDir); _crashLog = Path.Combine(dataDir, "crash.log"); }
                catch (Exception ex)
                {
                    MessageBox.Show("无法创建数据目录：" + dataDir + "\n" + ex.Message + "\n\n请把管理器放在可写目录中运行。", "DLSSFG Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                AppSettings settings;
                try { settings = new SettingsStore(dataDir).Load(); } catch (Exception) { settings = new AppSettings(); }
                ApplicationConfiguration.Initialize();
                try { Application.SetColorMode(settings.Theme == "dark" ? SystemColorMode.Dark : settings.Theme == "light" ? SystemColorMode.Classic : SystemColorMode.System); }
                catch (Exception) { /* very old Windows 10 builds: stay in classic mode */ }
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => ReportCrash(e.Exception, "界面线程异常", fatal: false);

                bool first;
                _single = new Mutex(true, "DLSSFG-Manager-" + baseDir.ToLowerInvariant().GetHashCode().ToString("x"), out first);
                if (!first) { MessageBox.Show("DLSSFG Manager 已经在运行。", "DLSSFG Manager", MessageBoxButtons.OK, MessageBoxIcon.Information); return 0; }

                MainForm form;
                try { form = new MainForm(dataDir, settings); }
                catch (Exception ex) { ReportCrash(ex, "创建主窗口失败"); return 2; }
                Application.Run(form);
                return 0;
            }
            catch (Exception ex) { ReportCrash(ex, "启动失败"); return 3; }
            finally { try { _single?.ReleaseMutex(); _single?.Dispose(); } catch (Exception) { } }
        }

        /// <summary>Release the single-instance lock so Application.Restart can start the new process before we exit.</summary>
        public static void ReleaseSingleInstance() { try { _single?.ReleaseMutex(); _single?.Dispose(); _single = null; } catch (Exception) { } }

        private static void ReportCrash(Exception ex, string stage, bool fatal = true)
        {
            var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + stage + Environment.NewLine + ex + Environment.NewLine +
                       "OS: " + Environment.OSVersion + "  .NET: " + Environment.Version + "  64-bit: " + Environment.Is64BitProcess + Environment.NewLine + new string('-', 60) + Environment.NewLine;
            try { File.AppendAllText(_crashLog, text); } catch (Exception) { }
            try
            {
                MessageBox.Show(stage + "：" + ex.GetType().Name + "\n" + ex.Message + "\n\n详细信息已写入：\n" + _crashLog,
                    "DLSSFG Manager" + (fatal ? " - 无法继续" : ""), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception) { }
        }
    }
}
