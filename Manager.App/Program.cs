using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Sm86.Manager.App
{
    internal static class Program
    {
        /// <summary>Scratch folder for this session's downloads; deleted on exit. Nothing is written next to the executable.</summary>
        public static readonly string TempDirectory = Path.Combine(Path.GetTempPath(), "DLSSFG-Manager");
        private static readonly string CrashLog = Path.Combine(Path.GetTempPath(), "DLSSFG-Manager-crash.log");
        private static Mutex _single;

        [STAThread]
        private static int Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"), "未处理的异常");
            try
            {
                ApplicationConfiguration.Initialize();
                try { Application.SetColorMode(SystemColorMode.System); } catch (Exception) { /* very old Windows 10 builds: stay in classic mode */ }
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => ReportCrash(e.Exception, "界面线程异常", fatal: false);

                bool first;
                _single = new Mutex(true, "DLSSFG-Manager-single-instance", out first);
                if (!first) { MessageBox.Show("DLSSFG Manager 已经在运行。", "DLSSFG Manager", MessageBoxButtons.OK, MessageBoxIcon.Information); return 0; }

                try { Directory.CreateDirectory(TempDirectory); } catch (Exception) { }
                MainForm form;
                try { form = new MainForm(); }
                catch (Exception ex) { ReportCrash(ex, "创建主窗口失败"); return 2; }
                Application.Run(form);
                return 0;
            }
            catch (Exception ex) { ReportCrash(ex, "启动失败"); return 3; }
            finally
            {
                try { if (Directory.Exists(TempDirectory)) Directory.Delete(TempDirectory, true); } catch (Exception) { }
                try { _single?.ReleaseMutex(); _single?.Dispose(); } catch (Exception) { }
            }
        }

        private static void ReportCrash(Exception ex, string stage, bool fatal = true)
        {
            var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + stage + Environment.NewLine + ex + Environment.NewLine +
                       "OS: " + Environment.OSVersion + "  .NET: " + Environment.Version + "  64-bit: " + Environment.Is64BitProcess + Environment.NewLine + new string('-', 60) + Environment.NewLine;
            try { File.AppendAllText(CrashLog, text); } catch (Exception) { }
            try
            {
                MessageBox.Show(stage + "：" + ex.GetType().Name + "\n" + ex.Message + "\n\n详细信息已写入：\n" + CrashLog,
                    "DLSSFG Manager" + (fatal ? " - 无法继续" : ""), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception) { }
        }
    }
}
