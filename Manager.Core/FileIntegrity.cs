using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Sm86.Manager
{
    public static class FileIntegrity
    {
        public static string Sha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(stream));
        }

        /// <summary>Git blob object id: sha1("blob " + size + "\0" + content).</summary>
        public static string GitBlobSha1(string path)
        {
            var info = new FileInfo(path);
            using (var sha = SHA1.Create())
            {
                var header = Encoding.ASCII.GetBytes("blob " + info.Length + "\0");
                sha.TransformBlock(header, 0, header.Length, null, 0);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(sha.Hash);
            }
        }

        public static bool SameHash(string a, string b) =>
            !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
