using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sm86.Manager.Tests
{
    /// <summary>Writes tiny but structurally valid PE files (PE32+ or PE32) with optional import / delay-import names. Never executable.</summary>
    internal static class FakePe
    {
        public static void Write(string path, bool is64 = true, bool isDll = false, string[] imports = null, string[] delayImports = null, int padding = 0)
        {
            imports = imports ?? new string[0]; delayImports = delayImports ?? new string[0];
            const uint sectionVa = 0x1000, sectionRaw = 0x400;
            var section = new MemoryStream();
            var w = new BinaryWriter(section);
            // reserve descriptor tables first, names after
            long importTable = 0, delayTable = 0;
            int importBytes = (imports.Length + 1) * 20, delayBytes = (delayImports.Length + 1) * 32;
            importTable = 0; delayTable = importBytes;
            w.Write(new byte[importBytes + delayBytes]);
            var importNameRvas = new List<uint>(); var delayNameRvas = new List<uint>();
            foreach (var n in imports) { importNameRvas.Add(sectionVa + (uint)section.Position); w.Write(Encoding.ASCII.GetBytes(n)); w.Write((byte)0); }
            foreach (var n in delayImports) { delayNameRvas.Add(sectionVa + (uint)section.Position); w.Write(Encoding.ASCII.GetBytes(n)); w.Write((byte)0); }
            w.Write(new byte[padding]);
            section.Position = importTable;
            for (int i = 0; i < imports.Length; i++) { w.Write(1u); w.Write(0u); w.Write(0u); w.Write(importNameRvas[i]); w.Write(sectionVa + 0x800u); }
            section.Position = delayTable;
            for (int i = 0; i < delayImports.Length; i++) { w.Write(1u); w.Write(delayNameRvas[i]); w.Write(new byte[24]); }
            var body = section.ToArray();
            uint rawSize = (uint)((body.Length + 0x1FF) / 0x200 * 0x200);

            var file = new MemoryStream();
            var f = new BinaryWriter(file);
            f.Write(new byte[0x80]); file.Position = 0; f.Write((ushort)0x5A4D); file.Position = 0x3C; f.Write(0x80u); file.Position = 0x80;
            f.Write(0x00004550u);
            f.Write((ushort)(is64 ? 0x8664 : 0x14C)); f.Write((ushort)1); f.Write(0u); f.Write(0u); f.Write(0u);
            ushort optSize = (ushort)(is64 ? 240 : 224); f.Write(optSize);
            f.Write((ushort)(0x0002 | 0x0020 | (isDll ? 0x2000 : 0)));
            long opt = file.Position;
            f.Write(new byte[optSize]);
            file.Position = opt; f.Write((ushort)(is64 ? 0x20B : 0x10B));
            file.Position = opt + (is64 ? 24 : 28); if (is64) f.Write(0x140000000UL); else f.Write(0x400000u);
            file.Position = opt + (is64 ? 108 : 92); f.Write(16u);
            long dirs = opt + (is64 ? 112 : 96);
            if (imports.Length > 0) { file.Position = dirs + 8; f.Write(sectionVa + (uint)importTable); f.Write((uint)importBytes); }
            if (delayImports.Length > 0) { file.Position = dirs + 13 * 8; f.Write(sectionVa + (uint)delayTable); f.Write((uint)delayBytes); }
            file.Position = opt + optSize;
            f.Write(Encoding.ASCII.GetBytes(".rdata\0\0")); f.Write((uint)body.Length); f.Write(sectionVa); f.Write(rawSize); f.Write(sectionRaw); f.Write(new byte[12]); f.Write(0x40000040u);
            file.Position = sectionRaw; f.Write(body); file.Position = sectionRaw + rawSize; f.Write((byte)0);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, file.ToArray());
        }
    }
}
