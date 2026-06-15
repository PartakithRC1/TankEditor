using ICSharpCode.SharpZipLib.Zip.Compression;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static DS2_Tank_Viewer.TankReader;

namespace DS2_Tank_Viewer
{
    public class TankWriter
    {
        private TankHeader _header;
        private readonly List<WriterDirEntry> _dirs = new List<WriterDirEntry>();
        private readonly List<WriterFileEntry> _files = new List<WriterFileEntry>();
        private readonly Dictionary<string, int> _pathToDirIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<byte> _dataBuffer = new List<byte>();

        private class WriterDirEntry
        {
            public string CanonicalPath = "";
            public string Name = "";
            public int ParentIndex = -1;
            public FileTime FileTime;
            public List<int> ChildDirIndices = new List<int>();
            public List<int> ChildFileIndices = new List<int>();
            public uint DSOOffset;
        }

        private class WriterFileEntry
        {
            public string Name = "";
            public int DirIndex;
            public uint Size;
            public uint DataOffset;
            public uint Crc32;
            public FileTime FileTime;
            public ushort Format;
            public ushort Flags;
            public CompressedHeader CompressedInfo;
            public bool IsCompressed => Format != 0;
            public uint FSOOffset;
        }

        public TankWriter()
        {
            InitHeader();
            _dirs.Add(new WriterDirEntry
            {
                CanonicalPath = "",
                Name = "",
                ParentIndex = -1,
                FileTime = NowFileTime()
            });
            _pathToDirIndex[""] = 0;
        }

        public void SetTitle(string title) => _header.TitleText = title;
        public void SetAuthor(string author) => _header.AuthorText = author;
        public void SetCopyright(string text) => _header.CopyrightText = text;
        public void SetDescription(string text) => _header.DescriptionText = text;

        public void AddFile(string virtualPath, byte[] data, bool compress = true)
        {
            virtualPath = virtualPath.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrEmpty(virtualPath))
                throw new ArgumentException("Empty virtual path");

            int lastSlash = virtualPath.LastIndexOf('/');
            string dirPath = lastSlash >= 0 ? virtualPath.Substring(0, lastSlash) : "";
            string fileName = lastSlash >= 0 ? virtualPath.Substring(lastSlash + 1) : virtualPath;
            fileName = fileName.ToLowerInvariant();
            dirPath = dirPath.ToLowerInvariant();

            int dirIndex = EnsureDirectory(dirPath);

            var entry = new WriterFileEntry
            {
                Name = fileName,
                DirIndex = dirIndex,
                Size = (uint)data.Length,
                Crc32 = Crc32(data),
                FileTime = NowFileTime(),
                DataOffset = (uint)_dataBuffer.Count
            };

            if (compress && data.Length > 0)
            {
                entry.Format = 1; // DATAFORMAT_ZLIB (raw deflate)
                entry.CompressedInfo = CompressRawDeflate(data);
            }
            else
            {
                entry.Format = 0; // RAW
                _dataBuffer.AddRange(data);
                AlignDataBuffer();
            }

            _files.Add(entry);
            _dirs[dirIndex].ChildFileIndices.Add(_files.Count - 1);
        }

        public void Save(string filePath)
        {
            SortFileSet();
            SerializeDirectoriesAndFiles(out byte[] dirSetData, out byte[] fileSetData);
            WriteTankFile(filePath, dirSetData, fileSetData);
        }

        // ---------------------------------------------------------------------
        // Compression: RAW DEFLATE (no zlib header, no adler32)
        // ---------------------------------------------------------------------
        private CompressedHeader CompressRawDeflate(byte[] data)
        {
            var deflater = new Deflater(Deflater.BEST_COMPRESSION, noZlibHeaderOrFooter: true);
            deflater.SetInput(data);
            deflater.Finish();

            byte[] buffer = new byte[data.Length + 12];
            int compSize = deflater.Deflate(buffer);
            byte[] compressed = new byte[compSize];
            Array.Copy(buffer, compressed, compSize);

            _dataBuffer.AddRange(compressed);
            AlignDataBuffer();

            return new CompressedHeader
            {
                ChunkSize = 0,
                CompressedSize = (uint)compSize,
                Chunks = new List<CompressedChunk>()
            };
        }

        private void AlignDataBuffer()
        {
            int align = 8;
            int pad = (align - (_dataBuffer.Count % align)) % align;
            for (int i = 0; i < pad; i++) _dataBuffer.Add(0);
        }

        // ---------------------------------------------------------------------
        // Sorting & Serialisation
        // ---------------------------------------------------------------------
        private void SortFileSet()
        {
            var sorted = _files.Select((f, i) => new { f, i })
                               .OrderBy(x => x.f.Name)
                               .ToList();
            var oldToNew = new int[_files.Count];
            for (int newIdx = 0; newIdx < sorted.Count; newIdx++)
                oldToNew[sorted[newIdx].i] = newIdx;

            foreach (var dir in _dirs)
            {
                for (int j = 0; j < dir.ChildFileIndices.Count; j++)
                    dir.ChildFileIndices[j] = oldToNew[dir.ChildFileIndices[j]];
            }

            _files.Clear();
            _files.AddRange(sorted.Select(x => x.f));
        }

        private void SerializeDirectoriesAndFiles(out byte[] dirSetData, out byte[] fileSetData)
        {
            // ---- Directory Set ----
            var dirBody = new byte[_dirs.Count][];
            for (int i = 0; i < _dirs.Count; i++)
                dirBody[i] = SerializeDirEntry(i, placeholder: true);

            uint bodiesStart = (uint)(4 + 4 * _dirs.Count);
            var dsoOffsets = new uint[_dirs.Count];
            uint running = bodiesStart;
            for (int i = 0; i < _dirs.Count; i++)
            {
                dsoOffsets[i] = running;
                _dirs[i].DSOOffset = running;
                running += (uint)dirBody[i].Length;
            }

            for (int i = 0; i < _dirs.Count; i++)
                dirBody[i] = SerializeDirEntry(i, placeholder: false);

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((uint)_dirs.Count);
                foreach (uint off in dsoOffsets) w.Write(off);
                foreach (var body in dirBody) w.Write(body);
                dirSetData = ms.ToArray();
            }

            // ---- File Set ----
            var fileBody = new byte[_files.Count][];
            for (int i = 0; i < _files.Count; i++)
                fileBody[i] = SerializeFileEntry(i, placeholder: true);

            uint fileBodyStart = (uint)(4 + 4 * _files.Count);
            var fsoOffsets = new uint[_files.Count];
            uint fileRunning = fileBodyStart;
            for (int i = 0; i < _files.Count; i++)
            {
                fsoOffsets[i] = fileRunning;
                _files[i].FSOOffset = fileRunning;
                fileRunning += (uint)fileBody[i].Length;
            }

            for (int i = 0; i < _files.Count; i++)
                fileBody[i] = SerializeFileEntry(i, placeholder: false);

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((uint)_files.Count);
                foreach (uint off in fsoOffsets) w.Write(off);
                foreach (var body in fileBody) w.Write(body);
                fileSetData = ms.ToArray();
            }
        }

        private byte[] SerializeDirEntry(int index, bool placeholder)
        {
            var dir = _dirs[index];
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                uint parentDso = (!placeholder && dir.ParentIndex >= 0) ? _dirs[dir.ParentIndex].DSOOffset : 0;
                w.Write(parentDso);
                w.Write((uint)(dir.ChildDirIndices.Count + dir.ChildFileIndices.Count));
                w.Write(dir.FileTime.LowDateTime);
                w.Write(dir.FileTime.HighDateTime);
                WriteNString(w, dir.Name);

                var children = new List<(string Name, bool IsFile, int Index)>();
                foreach (int ci in dir.ChildDirIndices) children.Add((_dirs[ci].Name, false, ci));
                foreach (int fi in dir.ChildFileIndices) children.Add((_files[fi].Name, true, fi));
                children = children.OrderBy(c => c.Name).ToList();

                if (!placeholder)
                {
                    foreach (var child in children)
                    {
                        w.Write(child.IsFile ? _files[child.Index].FSOOffset : _dirs[child.Index].DSOOffset);
                    }
                }
                else
                {
                    for (int i = 0; i < children.Count; i++) w.Write(0U);
                }
                return ms.ToArray();
            }
        }

        private byte[] SerializeFileEntry(int index, bool placeholder)
        {
            var file = _files[index];
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                uint parentDso = placeholder ? 0 : _dirs[file.DirIndex].DSOOffset;
                w.Write(parentDso);
                w.Write(file.Size);
                w.Write(file.DataOffset);
                w.Write(file.Crc32);
                w.Write(file.FileTime.LowDateTime);
                w.Write(file.FileTime.HighDateTime);
                w.Write(file.Format);
                w.Write(file.Flags);
                WriteNString(w, file.Name);

                if (file.IsCompressed && file.CompressedInfo != null)
                {
                    var ci = file.CompressedInfo;
                    w.Write(ci.CompressedSize);
                    w.Write(ci.ChunkSize);
                    foreach (var chunk in ci.Chunks)
                    {
                        w.Write(chunk.UncompressedSize);
                        w.Write(chunk.CompressedSize);
                        w.Write(chunk.ExtraBytes);
                        w.Write(chunk.Offset);
                    }
                }
                return ms.ToArray();
            }
        }

        private void WriteTankFile(string path, byte[] dirSetData, byte[] fileSetData)
        {
            byte[] headerRaw = SerializeHeader(_header);
            uint headerSize = (uint)headerRaw.Length;
            uint dataOffset = (uint)AlignUp((int)headerSize, 4096);
            int headerPad = (int)(dataOffset - headerSize);

            uint dataEnd = dataOffset + (uint)_dataBuffer.Count;
            uint dirSetOffset = (uint)AlignUp((int)dataEnd, 4096);
            int dataPad = (int)(dirSetOffset - dataEnd);
            for (int i = 0; i < dataPad; i++) _dataBuffer.Add(0);

            uint fileSetOffset = dirSetOffset + (uint)dirSetData.Length;
            uint indexSize = (uint)(dirSetData.Length + fileSetData.Length);

            byte[] indexForCrc = dirSetData.Concat(fileSetData).ToArray();
            _header.IndexCrc32 = Crc32(indexForCrc);
            _header.DataCrc32 = _dataBuffer.Count > 0 ? Crc32(_dataBuffer.ToArray()) : 0;
            _header.DirSetOffset = dirSetOffset;
            _header.FileSetOffset = fileSetOffset;
            _header.DataOffset = dataOffset;
            _header.IndexSize = indexSize;

            byte[] finalHeader = SerializeHeader(_header);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(finalHeader);
                for (int i = 0; i < headerPad; i++) w.Write((byte)0);
                w.Write(_dataBuffer.ToArray());
                w.Write(dirSetData);
                w.Write(fileSetData);
            }
        }

        // ---------------------------------------------------------------------
        // String serialisation (with null terminator and DWORD padding)
        // ---------------------------------------------------------------------
        private void WriteNString(BinaryWriter w, string str)
        {
            if (str == null) str = "";
            byte[] ascii = Encoding.ASCII.GetBytes(str);
            ushort len = (ushort)ascii.Length;
            w.Write(len);
            if (len > 0) w.Write(ascii);
            w.Write((byte)0); // null terminator
            int total = 2 + len + 1;
            int pad = AlignUp(total, 4) - total;
            for (int i = 0; i < pad; i++) w.Write((byte)0);
        }

        private void WriteWideFixed(BinaryWriter w, string str, int maxChars)
        {
            if (str == null) str = "";
            if (str.Length >= maxChars) str = str.Substring(0, maxChars - 1);
            byte[] bytes = Encoding.Unicode.GetBytes(str);
            byte[] buffer = new byte[maxChars * 2];
            Array.Copy(bytes, buffer, bytes.Length);
            w.Write(buffer);
        }

        private void WriteWideNString(BinaryWriter w, string str)
        {
            if (str == null) str = "";
            byte[] wide = Encoding.Unicode.GetBytes(str);
            ushort lenChars = (ushort)(wide.Length / 2);
            w.Write(lenChars);
            if (lenChars > 0) w.Write(wide);
            w.Write((ushort)0); // wide null terminator
            int total = 2 + (lenChars * 2) + 2;
            int pad = AlignUp(total, 4) - total;
            for (int i = 0; i < pad; i++) w.Write((byte)0);
        }

        private byte[] SerializeHeader(TankHeader h)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(h.ProductId.c0); w.Write(h.ProductId.c1); w.Write(h.ProductId.c2); w.Write(h.ProductId.c3);
                w.Write(h.TankId.c0); w.Write(h.TankId.c1); w.Write(h.TankId.c2); w.Write(h.TankId.c3);
                w.Write(h.HeaderVersion);
                w.Write(h.DirSetOffset);
                w.Write(h.FileSetOffset);
                w.Write(h.IndexSize);
                w.Write(h.DataOffset);
                w.Write(h.ProductVersion.v1); w.Write(h.ProductVersion.v2); w.Write(h.ProductVersion.v3);
                w.Write(h.MinimumVersion.v1); w.Write(h.MinimumVersion.v2); w.Write(h.MinimumVersion.v3);
                w.Write(h.Priority);
                w.Write(h.Flags);
                w.Write(h.CreatorId.c0); w.Write(h.CreatorId.c1); w.Write(h.CreatorId.c2); w.Write(h.CreatorId.c3);
                w.Write(h.Guid);
                w.Write(h.IndexCrc32);
                w.Write(h.DataCrc32);
                w.Write(h.UtcBuildTime.Year); w.Write(h.UtcBuildTime.Month); w.Write(h.UtcBuildTime.DayOfWeek); w.Write(h.UtcBuildTime.Day);
                w.Write(h.UtcBuildTime.Hour); w.Write(h.UtcBuildTime.Minute); w.Write(h.UtcBuildTime.Second); w.Write(h.UtcBuildTime.Milliseconds);
                WriteWideFixed(w, h.CopyrightText, 100);
                WriteWideFixed(w, h.BuildText, 100);
                WriteWideFixed(w, h.TitleText, 100);
                WriteWideFixed(w, h.AuthorText, 40);
                WriteWideNString(w, h.DescriptionText);
                for (int i = 0; i < 16; i++) w.Write((byte)0);
                return ms.ToArray();
            }
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------
        private int EnsureDirectory(string dirPath)
        {
            if (string.IsNullOrEmpty(dirPath)) return 0;
            if (_pathToDirIndex.TryGetValue(dirPath, out int existing)) return existing;

            string[] parts = dirPath.Split('/');
            int parent = 0;
            string current = "";
            for (int i = 0; i < parts.Length; i++)
            {
                current = i == 0 ? parts[i] : current + "/" + parts[i];
                if (!_pathToDirIndex.TryGetValue(current, out int idx))
                {
                    var newDir = new WriterDirEntry
                    {
                        CanonicalPath = current,
                        Name = parts[i],
                        ParentIndex = parent,
                        FileTime = NowFileTime()
                    };
                    idx = _dirs.Count;
                    _dirs.Add(newDir);
                    _pathToDirIndex[current] = idx;
                    _dirs[parent].ChildDirIndices.Add(idx);
                }
                parent = idx;
            }
            return parent;
        }

        private void InitHeader()
        {
            var now = DateTime.UtcNow;
            _header = new TankHeader
            {
                ProductId = new FourCC { c0 = (byte)'D', c1 = (byte)'S', c2 = (byte)'g', c3 = (byte)'2' }, // "DSg2"
                TankId = new FourCC { c0 = (byte)'T', c1 = (byte)'a', c2 = (byte)'n', c3 = (byte)'k' },
                HeaderVersion = MakeVersion(1, 0, 2),
                ProductVersion = new ProductVersion { v1 = 0x00050000, v2 = 0x000307d2, v3 = 0x00010008 },
                MinimumVersion = new ProductVersion { v1 = 0x00050000, v2 = 0x000307d2, v3 = 0x00010008 },
                Priority = 0x4000,
                Flags = 0,
                CreatorId = new FourCC { c0 = (byte)'U', c1 = (byte)'S', c2 = (byte)'E', c3 = (byte)'R' },
                Guid = Guid.NewGuid().ToByteArray(),
                CopyrightText = "",
                BuildText = "",
                TitleText = "",
                AuthorText = "",
                DescriptionText = "",
                UtcBuildTime = new SystemTime
                {
                    Year = (ushort)now.Year,
                    Month = (ushort)now.Month,
                    DayOfWeek = (ushort)now.DayOfWeek,
                    Day = (ushort)now.Day,
                    Hour = (ushort)now.Hour,
                    Minute = (ushort)now.Minute,
                    Second = (ushort)now.Second,
                    Milliseconds = (ushort)now.Millisecond
                }
            };
        }

        private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
        private static uint MakeVersion(int major, int minor, int rev) => (uint)((major << 16) | (minor << 8) | rev);
        private static FileTime NowFileTime()
        {
            long ft = DateTime.UtcNow.ToFileTimeUtc();
            return new FileTime { LowDateTime = (uint)(ft & 0xFFFFFFFF), HighDateTime = (uint)((ft >> 32) & 0xFFFFFFFF) };
        }
        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data) crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
            return ~crc;
        }

private static readonly uint[] Crc32Table =
        {
            0x00000000,0x77073096,0xEE0E612C,0x990951BA,0x076DC419,0x706AF48F,0xE963A535,0x9E6495A3,
            0x0EDB8832,0x79DCB8A4,0xE0D5E91E,0x97D2D988,0x09B64C2B,0x7EB17CBD,0xE7B82D07,0x90BF1D91,
            0x1DB71064,0x6AB020F2,0xF3B97148,0x84BE41DE,0x1ADAD47D,0x6DDDE4EB,0xF4D4B551,0x83D385C7,
            0x136C9856,0x646BA8C0,0xFD62F97A,0x8A65C9EC,0x14015C4F,0x63066CD9,0xFA0F3D63,0x8D080DF5,
            0x3B6E20C8,0x4C69105E,0xD56041E4,0xA2677172,0x3C03E4D1,0x4B04D447,0xD20D85FD,0xA50AB56B,
            0x35B5A8FA,0x42B2986C,0xDBBBC9D6,0xACBCF940,0x32D86CE3,0x45DF5C75,0xDCD60DCF,0xABD13D59,
            0x26D930AC,0x51DE003A,0xC8D75180,0xBFD06116,0x21B4F4B5,0x56B3C423,0xCFBA9599,0xB8BDA50F,
            0x2802B89E,0x5F058808,0xC60CD9B2,0xB10BE924,0x2F6F7C87,0x58684C11,0xC1611DAB,0xB6662D3D,
            0x76DC4190,0x01DB7106,0x98D220BC,0xEFD5102A,0x71B18589,0x06B6B51F,0x9FBFE4A5,0xE8B8D433,
            0x7807C9A2,0x0F00F934,0x9609A88E,0xE10E9818,0x7F6A0DBB,0x086D3D2D,0x91646C97,0xE6635C01,
            0x6B6B51F4,0x1C6C6162,0x856530D8,0xF262004E,0x6C0695ED,0x1B01A57B,0x8208F4C1,0xF50FC457,
            0x65B0D9C6,0x12B7E950,0x8BBEB8EA,0xFCB9887C,0x62DD1DDF,0x15DA2D49,0x8CD37CF3,0xFBD44C65,
            0x4DB26158,0x3AB551CE,0xA3BC0074,0xD4BB30E2,0x4ADFA541,0x3DD895D7,0xA4D1C46D,0xD3D6F4FB,
            0x4369E96A,0x346ED9FC,0xAD678846,0xDA60B8D0,0x44042D73,0x33031DE5,0xAA0A4C5F,0xDD0D7CC9,
            0x5005713C,0x270241AA,0xBE0B1010,0xC90C2086,0x5768B525,0x206F85B3,0xB966D409,0xCE61E49F,
            0x5EDEF90E,0x29D9C998,0xB0D09822,0xC7D7A8B4,0x59B33D17,0x2EB40D81,0xB7BD5C3B,0xC0BA6CAD,
            0xEDB88320,0x9ABFB3B6,0x03B6E20C,0x74B1D29A,0xEAD54739,0x9DD277AF,0x04DB2615,0x73DC1683,
            0xE3630B12,0x94643B84,0x0D6D6A3E,0x7A6A5AA8,0xE40ECF0B,0x9309FF9D,0x0A00AE27,0x7D079EB1,
            0xF00F9344,0x8708A3D2,0x1E01F268,0x6906C2FE,0xF762575D,0x806567CB,0x196C3671,0x6E6B06E7,
            0xFED41B76,0x89D32BE0,0x10DA7A5A,0x67DD4ACC,0xF9B9DF6F,0x8EBEEFF9,0x17B7BE43,0x60B08ED5,
            0xD6D6A3E8,0xA1D1937E,0x38D8C2C4,0x4FDFF252,0xD1BB67F1,0xA6BC5767,0x3FB506DD,0x48B2364B,
            0xD80D2BDA,0xAF0A1B4C,0x36034AF6,0x41047A60,0xDF60EFC3,0xA867DF55,0x316E8EEF,0x4669BE79,
            0xCB61B38C,0xBC66831A,0x256FD2A0,0x5268E236,0xCC0C7795,0xBB0B4703,0x220216B9,0x5505262F,
            0xC5BA3BBE,0xB2BD0B28,0x2BB45A92,0x5CB36A04,0xC2D7FFA7,0xB5D0CF31,0x2CD99E8B,0x5BDEAE1D,
            0x9B64C2B0,0xEC63F226,0x756AA39C,0x026D930A,0x9C0906A9,0xEB0E363F,0x72076785,0x05005713,
            0x95BF4A82,0xE2B87A14,0x7BB12BAE,0x0CB61B38,0x92D28E9B,0xE5D5BE0D,0x7CDCEFB7,0x0BDBDF21,
            0x86D3D2D4,0xF1D4E242,0x68DDB3F8,0x1FDA836E,0x81BE16CD,0xF6B9265B,0x6FB077E1,0x18B74777,
            0x88085AE6,0xFF0F6A70,0x66063BCA,0x11010B5C,0x8F659EFF,0xF862AE69,0x616BFFD3,0x166CCF45,
            0xA00AE278,0xD70DD2EE,0x4E048354,0x3903B3C2,0xA7672661,0xD06016F7,0x4969474D,0x3E6E77DB,
            0xAED16A4A,0xD9D65ADC,0x40DF0B66,0x37D83BF0,0xA9BCAE53,0xDEBB9EC5,0x47B2CF7F,0x30B5FFE9,
            0xBDBDF21C,0xCABAC28A,0x53B39330,0x24B4A3A6,0xBAD03605,0xCDD70693,0x54DE5729,0x23D967BF,
            0xB3667A2E,0xC4614AB8,0x5D681B02,0x2A6F2B94,0xB40BBE37,0xC30C8EA1,0x5A05DF1B,0x2D02EF8D
        };
    }
}