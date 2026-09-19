using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sm86.Manager
{
    /// <summary>Minimal read-only PE parser: machine type, DLL flag, normal and delay-load import names. Never maps or executes code.</summary>
    public static class PeInspector
    {
        private const int MaxFileBytes = 512 * 1024 * 1024;

        public static PeInfo Read(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var info = new FileInfo(path);
                if (info.Length < 0x100 || info.Length > MaxFileBytes) return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                    return Parse(reader, info.Length);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private struct Section { public uint VirtualAddress, VirtualSize, RawPointer, RawSize; }

        private static PeInfo Parse(BinaryReader r, long length)
        {
            if (r.ReadUInt16() != 0x5A4D) return null; // MZ
            r.BaseStream.Position = 0x3C;
            uint peOffset = r.ReadUInt32();
            if (peOffset == 0 || peOffset + 24 > length) return null;
            r.BaseStream.Position = peOffset;
            if (r.ReadUInt32() != 0x00004550) return null; // PE\0\0
            ushort machine = r.ReadUInt16();
            ushort sectionCount = r.ReadUInt16();
            r.BaseStream.Position += 12; // timestamp, symtab ptr, symbol count
            ushort optionalSize = r.ReadUInt16();
            ushort characteristics = r.ReadUInt16();
            var result = new PeInfo { Is64Bit = machine == 0x8664, IsDll = (characteristics & 0x2000) != 0 };
            long optionalStart = r.BaseStream.Position;
            if (optionalSize < 96) return result;
            ushort magic = r.ReadUInt16();
            bool plus = magic == 0x20B;
            if (magic != 0x10B && !plus) return result;
            long dirStart = optionalStart + (plus ? 112 : 96);
            r.BaseStream.Position = optionalStart + (plus ? 108 : 92);
            uint dirCount = r.ReadUInt32();
            long sectionStart = optionalStart + optionalSize;
            var sections = new List<Section>();
            r.BaseStream.Position = sectionStart;
            for (int i = 0; i < sectionCount; i++)
            {
                if (r.BaseStream.Position + 40 > length) break;
                r.BaseStream.Position += 8;
                var s = new Section { VirtualSize = r.ReadUInt32(), VirtualAddress = r.ReadUInt32(), RawSize = r.ReadUInt32(), RawPointer = r.ReadUInt32() };
                r.BaseStream.Position += 16;
                sections.Add(s);
            }
            ReadDirectory(r, length, dirStart, dirCount, 1, sections, result.Imports, delay: false);
            ReadDirectory(r, length, dirStart, dirCount, 13, sections, result.DelayImports, delay: true);
            return result;
        }

        private static void ReadDirectory(BinaryReader r, long length, long dirStart, uint dirCount, int index, List<Section> sections, List<string> target, bool delay)
        {
            if (index >= dirCount) return;
            r.BaseStream.Position = dirStart + index * 8;
            uint rva = r.ReadUInt32();
            uint size = r.ReadUInt32();
            if (rva == 0 || size == 0) return;
            long offset = RvaToOffset(rva, sections);
            if (offset < 0 || offset >= length) return;
            int entrySize = delay ? 32 : 20;
            int nameField = delay ? 4 : 12;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 1024; i++)
            {
                long entry = offset + (long)i * entrySize;
                if (entry + entrySize > length) return;
                r.BaseStream.Position = entry;
                var raw = r.ReadBytes(entrySize);
                bool allZero = true;
                foreach (var b in raw) if (b != 0) { allZero = false; break; }
                if (allZero) return;
                uint nameRva = BitConverter.ToUInt32(raw, nameField);
                if (delay)
                {
                    uint attributes = BitConverter.ToUInt32(raw, 0);
                    if ((attributes & 1) == 0)
                    {
                        // Legacy delay-load descriptors store VAs instead of RVAs; convert using image base.
                        uint imageBase = ReadImageBase(r);
                        if (nameRva >= imageBase) nameRva -= imageBase;
                    }
                }
                long nameOffset = RvaToOffset(nameRva, sections);
                if (nameOffset < 0 || nameOffset >= length) continue;
                r.BaseStream.Position = nameOffset;
                var name = ReadAscii(r, 260);
                if (name.Length > 0 && seen.Add(name)) target.Add(name);
            }
        }

        private static uint ReadImageBase(BinaryReader r)
        {
            long saved = r.BaseStream.Position;
            try
            {
                r.BaseStream.Position = 0x3C;
                uint peOffset = r.ReadUInt32();
                r.BaseStream.Position = peOffset + 24;
                ushort magic = r.ReadUInt16();
                r.BaseStream.Position = peOffset + 24 + (magic == 0x20B ? 24 : 28);
                return magic == 0x20B ? (uint)r.ReadUInt64() : r.ReadUInt32();
            }
            finally { r.BaseStream.Position = saved; }
        }

        private static long RvaToOffset(uint rva, List<Section> sections)
        {
            foreach (var s in sections)
            {
                uint size = Math.Max(s.VirtualSize, s.RawSize);
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + size)
                {
                    long delta = rva - s.VirtualAddress;
                    if (delta >= s.RawSize) return -1;
                    return s.RawPointer + delta;
                }
            }
            return sections.Count == 0 ? rva : -1;
        }

        private static string ReadAscii(BinaryReader r, int max)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < max; i++)
            {
                if (r.BaseStream.Position >= r.BaseStream.Length) break;
                byte b = r.ReadByte();
                if (b == 0) break;
                if (b < 0x20 || b > 0x7E) return "";
                sb.Append((char)b);
            }
            return sb.ToString();
        }
    }
}
