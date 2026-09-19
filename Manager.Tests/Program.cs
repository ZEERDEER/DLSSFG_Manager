using System;

namespace Sm86.Manager.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            InstallerTests.Run();
            foreach (var name in new[] { "ScannerTests", "UpdaterTests" })
            {
                var type = typeof(Program).Assembly.GetType("Sm86.Manager.Tests." + name);
                if (type != null) type.GetMethod("Run").Invoke(null, null);
            }
            Console.WriteLine("Tests: " + Test.Passed + " passed; " + Test.Failed + " failed.");
            return Test.Failed == 0 ? 0 : 1;
        }
    }
    internal static class Test
    {
        public static int Passed, Failed;
        public static void Run(string name, Action action) { try { action(); Passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { Failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
        public static void True(bool value, string message = "Expected true") { if (!value) throw new Exception(message); }
        public static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + "; actual " + actual); }
        public static T Throws<T>(Action action) where T : Exception { try { action(); } catch (T ex) { return ex; } throw new Exception("Expected exception " + typeof(T).Name); }
    }
}
