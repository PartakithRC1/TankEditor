using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DS2_Tank_Viewer
{
    /// <summary>
    /// Writer counterpart to TankReader.cs. Builds a RAW-format Dungeon Siege
    /// TankFile (.dsres / .dsmap) from a source directory of loose files.
    ///
    /// FIRST PASS: writes files uncompressed (DataFormat.Raw == 0). This is
    /// spec-complete and fully round-trippable against TankReader.Load().
    /// Zlib chunked-compression writing (DataFormat.Zlib == 1) is the next
    /// increment on top of this - the header/index layout below already has
    /// the hooks for it (see WriteCompressedHeaderPlaceholder notes).
    ///
    /// Field order in every struct below is a MIRROR of TankReader.ReadHeader /
    /// TankReader.Load, confirmed against your working reader - not re-derived
    /// from scratch - so a tank built here should read back byte-identically
    /// through TankReader.cs.
    /// </summary>
    public class TankWriter
    {
        // ====================== CONSTANTS (TankStructure.h) ======================

        public const uint DATA_SECTION_ALIGNMENT = 4 << 10; // 4096, alignment for start of data section
        public const uint DATA_ALIGNMENT = 8;                // per-file alignment inside data section
        public const uint INVALID_OFFSET = 0xFFFFFFFF;
        // Confirmed (not guessed): DS2_Tank_Viewer's own PatchToDs2() forces bytes
        // [8,9,10,11] of an RTC-built header to 00 01 01 00, which is exactly the
        // HeaderVersion DWORD's little-endian bytes -> 0x00010100. That's a real
        // observed value from a working DS2 pipeline, so this replaces the earlier
        // guessed MAKEVERSION(1,0,2) packing.
        public const uint HEADER_VERSION = 0x00010100;

        public const int COPYRIGHT_TEXT_LENGTH = 100;
        public const int BUILD_TEXT_LENGTH = 100;
        public const int TITLE_TEXT_LENGTH = 100;
        public const int AUTHOR_TEXT_LENGTH = 40;

        // eDataFormat
        public const ushort DATAFORMAT_RAW = 0;
        public const ushort DATAFORMAT_ZLIB = 1;
        public const ushort DATAFORMAT_LZO = 2;

        // eFileFlags
        public const ushort FILEFLAG_NONE = 0;
        public const ushort FILEFLAG_INVALID = 1 << 15;

        // ====================== INPUT MODEL ======================

        public class BuildFileEntry
        {
            public string SourcePath;   // full path on disk to read bytes from
            public string Name;         // just the filename, as stored in the tank
            public DateTime LastWriteTimeUtc;
        }

        public class BuildDirEntry
        {
            public string Name;                 // "" for root
            public BuildDirEntry Parent;        // null for root
            public DateTime LastWriteTimeUtc;
            public List<BuildDirEntry> Dirs = new List<BuildDirEntry>();
            public List<BuildFileEntry> Files = new List<BuildFileEntry>();

            // filled in during the write pass
            public uint SelfRelativeOffset;     // offset from top of DirSet, once written
        }

        // Header fields the caller can customize (mirrors TankHeader in TankReader.cs)
        public FourCC ProductId = new FourCC { c0 = (byte)'D', c1 = (byte)'S', c2 = (byte)'g', c3 = (byte)'2' }; // "DSg2" DS2 by default
        public FourCC CreatorId = new FourCC { c0 = (byte)'U', c1 = (byte)'S', c2 = (byte)'E', c3 = (byte)'R' }; // "USER"
        public ProductVersion ProductVersion = new ProductVersion { v1 = 1, v2 = 0, v3 = 0 };
        public ProductVersion MinimumVersion = new ProductVersion { v1 = 1, v2 = 0, v3 = 0 };
        public uint Priority = 0x4000; // PRIORITY_USER
        public uint Flags = 0;         // eTankFlags - NON_RETAIL / ALLOW_MULTIPLAYER_XFER / PROTECTED_CONTENT
        public Guid TankGuid = Guid.NewGuid();
        public string CopyrightText = "";
        public string BuildText = "";
        public string TitleText = "";
        public string AuthorText = "";
        public string DescriptionText = "";

        /// <summary>
        /// DEBUG/DIAGNOSTIC FLAG. When true, writes INVALID_CHECKSUM (0x00000000)
        /// for both IndexCrc32 and DataCrc32 instead of our computed values.
        /// TankStructure.h documents 0 as meaning "not important or wasn't
        /// computed" - i.e. the engine should skip CRC validation entirely.
        ///
        /// Use this to bisect a "resource file is corrupt" engine error:
        ///   - error goes away  -> our CRC32 computation is the bug (wrong
        ///                          algorithm variant or wrong byte range)
        ///   - error persists   -> CRC isn't the cause, look elsewhere
        ///                          (offsets, IndexSize, dir tree, etc.)
        ///
        /// Leave false for anything you actually intend to keep/ship - an
        /// unvalidated tank is exactly what INVALID_CHECKSUM is documented to
        /// produce, so this is a diagnostic tool, not a fix.
        /// </summary>
        public bool SkipChecksums = false;

        // ====================== NSTRING / ALIGNMENT (mirror of reader) ======================

        private ushort AlignToDword(ushort size) => (ushort)(size + (4 - (size % 4)));

        /// <summary>
        /// Writes an NSTRING exactly as TankReader.ReadNString expects to read it:
        ///   ushort length, then alignToDword(length+2)-2 bytes of [text + zero padding].
        /// Empty string is length=0 followed by a padding ushort(0) - 4 bytes total,
        /// matching the reader's early-return branch.
        /// </summary>
        private void WriteNString(BinaryWriter w, string s)
        {
            s ??= "";
            if (s.Length > ushort.MaxValue - 4)
                throw new ArgumentException("NSTRING too long: " + s);

            ushort lenInChars = (ushort)s.Length;
            w.Write(lenInChars);

            if (lenInChars == 0)
            {
                w.Write((ushort)0); // padding word - matches reader's read of a dummy word for the empty case
                return;
            }

            byte[] textBytes = Encoding.ASCII.GetBytes(s);
            ushort totalToWrite = (ushort)(AlignToDword((ushort)(lenInChars + 2)) - 2);
            w.Write(textBytes, 0, lenInChars);
            int padding = totalToWrite - lenInChars;
            for (int i = 0; i < padding; i++) w.Write((byte)0);
        }

        /// <summary>
        /// Fixed-size wide (UTF-16) zero-terminated field, e.g. header CopyrightText/BuildText/
        /// TitleText/AuthorText. Always writes exactly maxChars*2 bytes, zero-padded/truncated.
        /// </summary>
        private void WriteWideFixed(BinaryWriter w, string s, int maxChars)
        {
            s ??= "";
            byte[] bytes = new byte[maxChars * 2];
            byte[] src = Encoding.Unicode.GetBytes(s);
            int copyLen = Math.Min(src.Length, (maxChars - 1) * 2); // leave room for the null terminator
            Array.Copy(src, bytes, copyLen);
            w.Write(bytes);
        }

        /// <summary>
        /// WNSTRING per TankStructure.h spec: WNSTRING::GetSize(len) =
        /// GetDwordAlignUp( sizeof(WORD) + (len+1)*2 ), i.e. length word + (len+1)
        /// wide chars (text + null terminator), dword-aligned.
        ///
        /// NOTE: this intentionally does NOT match TankReader.ReadWideNString,
        /// which reuses the narrow-string alignment formula. That reader path
        /// backs DescriptionText, a field your extraction flow likely never
        /// exercised with non-empty data, so the mismatch never surfaced. This
        /// writer follows the header spec. If you build a tank with a non-empty
        /// DescriptionText and it round-trips wrong through TankReader.cs, this
        /// is where to look - fix ReadWideNString to match, not this method.
        /// </summary>
        private void WriteWideNString(BinaryWriter w, string s)
        {
            s ??= "";
            if (s.Length > ushort.MaxValue - 4)
                throw new ArgumentException("WNSTRING too long: " + s);

            ushort lenInChars = (ushort)s.Length;
            w.Write(lenInChars);

            int rawByteSize = 2 + (lenInChars + 1) * 2; // WORD + (len+1) wide chars
            int alignedSize = (rawByteSize + 3) & ~3;
            int payloadBytes = alignedSize - 2; // bytes after the length word

            byte[] text = Encoding.Unicode.GetBytes(s); // lenInChars*2 bytes, no terminator
            byte[] payload = new byte[payloadBytes];
            Array.Copy(text, payload, text.Length); // remaining bytes stay zero (null terminator + dword pad)
            w.Write(payload);
        }

        private void WriteAlignPad(Stream s, uint alignment)
        {
            long pos = s.Position;
            long rem = pos % alignment;
            if (rem == 0) return;
            long pad = alignment - rem;
            for (long i = 0; i < pad; i++) s.WriteByte(0);
        }

        // ====================== CRC32 (standard poly 0xEDB88320) ======================
        // NOTE: verify this against a known-good RTC-built tank before relying on it -
        // the exact CRC32 variant (poly / init / xorout) isn't independently confirmed
        // from the binaries you've provided, this is the standard zlib/PKZIP CRC32.

        private static readonly uint[] Crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
                crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        // ====================== BUILD TREE FROM DISK ======================

        public BuildDirEntry BuildTreeFromDirectory(string sourceDir)
        {
            var root = new BuildDirEntry
            {
                Name = "",
                Parent = null,
                LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(sourceDir)
            };
            PopulateDir(root, sourceDir);
            return root;
        }

        private void PopulateDir(BuildDirEntry node, string path)
        {
            foreach (var filePath in Directory.GetFiles(path).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                node.Files.Add(new BuildFileEntry
                {
                    SourcePath = filePath,
                    Name = Path.GetFileName(filePath),
                    LastWriteTimeUtc = File.GetLastWriteTimeUtc(filePath)
                });
            }

            foreach (var dirPath in Directory.GetDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var child = new BuildDirEntry
                {
                    Name = Path.GetFileName(dirPath),
                    Parent = node,
                    LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(dirPath)
                };
                node.Dirs.Add(child);
                PopulateDir(child, dirPath);
            }
        }

        // ====================== MAIN BUILD ENTRY POINT ======================

        public void Build(string sourceDir, string outputTankPath)
        {
            var root = BuildTreeFromDirectory(sourceDir);

            // Flatten dirs (breadth-first is fine, order doesn't matter as long as
            // parent offsets are resolved before children reference them) and files
            // (order also doesn't matter - FileEntry stores its own ParentOffset).
            var allDirs = new List<BuildDirEntry>();
            FlattenDirs(root, allDirs);

            var allFiles = new List<(BuildDirEntry Dir, BuildFileEntry File)>();
            foreach (var d in allDirs)
                foreach (var f in d.Files)
                    allFiles.Add((d, f));

            using (var ms = new MemoryStream())
            {
                // ---------- Pass 1: write DATA section, remember offsets ----------
                var dataOffsets = new Dictionary<BuildFileEntry, (uint Offset, uint Size, uint Crc)>();

                using (var dataStream = new MemoryStream())
                using (var dataWriter = new BinaryWriter(dataStream))
                {
                    foreach (var (dir, file) in allFiles)
                    {
                        WriteAlignPad(dataStream, DATA_ALIGNMENT);
                        byte[] bytes = File.ReadAllBytes(file.SourcePath);
                        uint offset = (uint)dataStream.Position;
                        uint crc = Crc32(bytes);
                        dataWriter.Write(bytes);
                        dataOffsets[file] = (offset, (uint)bytes.Length, crc);
                    }

                    uint dataSectionSize = (uint)dataStream.Length;
                    byte[] fullDataBytes = dataStream.ToArray();
                    uint dataCrc32 = Crc32(fullDataBytes);

                    // ---------- Pass 2: assign DirSet offsets ----------
                    // DirSet layout: [count][offsets[count]] then DirEntry blobs back to back.
                    // We need each dir's relative offset (from top of DirSet) before we can
                    // write ParentOffset fields, so do a dry-run size pass first.
                    var dirRelOffset = new Dictionary<BuildDirEntry, uint>();
                    uint dirCursor = (uint)(4 + 4 * allDirs.Count); // count + offsets table
                    foreach (var d in allDirs)
                    {
                        dirRelOffset[d] = dirCursor;
                        dirCursor += MeasureDirEntry(d);
                    }
                    uint dirSetSize = dirCursor;

                    // ---------- Pass 3: assign FileSet offsets ----------
                    var fileRelOffset = new Dictionary<BuildFileEntry, uint>();
                    uint fileCursor = (uint)(4 + 4 * allFiles.Count);
                    foreach (var (dir, file) in allFiles)
                    {
                        fileRelOffset[file] = fileCursor;
                        fileCursor += MeasureFileEntry(file, dataOffsets[file].Size);
                    }
                    uint fileSetSize = fileCursor;

                    // ---------- Compute section layout ----------
                    // RAW layout: Header -> Data (aligned to DATA_SECTION_ALIGNMENT) -> DirSet -> FileSet
                    uint headerSize = MeasureHeader();
                    uint dataOffset = AlignUp(headerSize, DATA_SECTION_ALIGNMENT);
                    uint dirSetOffset = dataOffset + dataSectionSize;
                    uint fileSetOffset = dirSetOffset + dirSetSize;
                    uint indexSize = dirSetSize + fileSetSize; // header + all index data, per spec comment // headerSize +

                    // ---------- Serialize DirSet + FileSet into a buffer so we can CRC it ----------
                    byte[] indexBytes;
                    using (var idxStream = new MemoryStream())
                    using (var idxWriter = new BinaryWriter(idxStream))
                    {
                        // DirSet
                        idxWriter.Write((uint)allDirs.Count);
                        foreach (var d in allDirs) idxWriter.Write(dirRelOffset[d]);
                        foreach (var d in allDirs)
                        {
                            uint parentOff = d.Parent == null ? 0 : dirRelOffset[d.Parent];
                            idxWriter.Write(parentOff);
                            idxWriter.Write((uint)(d.Dirs.Count + d.Files.Count));
                            WriteFileTime(idxWriter, d.LastWriteTimeUtc);
                            WriteNString(idxWriter, d.Name);
                            // ChildOffsets: per TankStructure.h these are offsets to each child
                            // (dirs AND files) so the reader can walk mixed children and range-test
                            // against m_DirFirst/m_DirLast vs m_FileFirst/m_FileLast to tell them apart.
                            foreach (var childDir in d.Dirs) idxWriter.Write(dirRelOffset[childDir]);
                            foreach (var childFile in d.Files) idxWriter.Write(fileSetOffset - dirSetOffset + fileRelOffset[childFile]);
                            // ^ NOTE: child offsets need to be comparable/usable the same way for dirs
                            // and files. This needs verification against a real RTC-built tank - see
                            // the flagged note at the bottom of my reply about ChildCount/ChildOffsets.
                        }

                        // FileSet
                        idxWriter.Write((uint)allFiles.Count);
                        foreach (var (dir, file) in allFiles) idxWriter.Write(fileRelOffset[file]);
                        foreach (var (dir, file) in allFiles)
                        {
                            var (offset, size, crc) = dataOffsets[file];
                            idxWriter.Write(dirRelOffset[dir]);          // ParentOffset
                            idxWriter.Write(size);                       // Size
                            idxWriter.Write(offset);                     // Offset (relative to data section top)
                            idxWriter.Write(crc);                        // CRC32
                            WriteFileTime(idxWriter, file.LastWriteTimeUtc);
                            idxWriter.Write(DATAFORMAT_RAW);             // Format - RAW for this first pass
                            idxWriter.Write(FILEFLAG_NONE);              // Flags
                            WriteNString(idxWriter, file.Name);
                            // (no CompressedHeader - only present when Format != RAW)
                        }

                        indexBytes = idxStream.ToArray();
                    }

                    uint indexCrc32 = Crc32(indexBytes);

                    if (SkipChecksums)
                    {
                        // INVALID_CHECKSUM per TankStructure.h - tells the engine not to validate.
                        indexCrc32 = 0;
                        dataCrc32 = 0;
                    }

                    // ---------- Write Header ----------
                    using (var outFs = new FileStream(outputTankPath, FileMode.Create, FileAccess.Write))
                    using (var w = new BinaryWriter(outFs))
                    {
                        WriteHeader(w, dirSetOffset, fileSetOffset, indexSize, dataOffset, indexCrc32, dataCrc32);
                        WriteAlignPad(outFs, DATA_SECTION_ALIGNMENT);
                        w.Write(fullDataBytes);
                        w.Write(indexBytes);
                    }
                }
            }
        }

        private static uint AlignUp(uint value, uint alignment) =>
            (value + alignment - 1) / alignment * alignment;

        private void FlattenDirs(BuildDirEntry node, List<BuildDirEntry> outList)
        {
            outList.Add(node);
            foreach (var c in node.Dirs) FlattenDirs(c, outList);
        }

        private void WriteFileTime(BinaryWriter w, DateTime utc)
        {
            long fileTime = utc.ToFileTimeUtc();
            w.Write((uint)(fileTime & 0xFFFFFFFF));       // LowDateTime
            w.Write((uint)((fileTime >> 32) & 0xFFFFFFFF)); // HighDateTime
        }

        private uint MeasureNStringSize(string s)
        {
            s ??= "";
            ushort lenInChars = (ushort)s.Length;
            if (lenInChars == 0) return 4;
            return (uint)(2 + (AlignToDword((ushort)(lenInChars + 2)) - 2));
        }

        private uint MeasureDirEntry(BuildDirEntry d)
        {
            uint size = 4 + 4 + 8; // ParentOffset + ChildCount + FileTime(8)
            size += MeasureNStringSize(d.Name);
            size += (uint)(4 * (d.Dirs.Count + d.Files.Count)); // child offsets
            return size;
        }

        private uint MeasureFileEntry(BuildFileEntry f, uint dataSize)
        {
            uint size = 4 + 4 + 4 + 4 + 8 + 2 + 2; // Parent+Size+Offset+Crc+FileTime+Format+Flags
            size += MeasureNStringSize(f.Name);
            // + CompressedHeader/ChunkHeader block once compression is added
            return size;
        }

        private uint MeasureHeader()
        {
            // Base fixed fields up through m_DataOffset, m_ProductVersion..m_UtcBuildTime,
            // plus fixed wide strings, plus the WNSTRING description (measured for current value).
            uint size = 0;
            size += 4 + 4;              // ProductId, TankId
            size += 4;                  // HeaderVersion
            size += 4 + 4;              // DirSetOffset, FileSetOffset
            size += 4;                  // IndexSize
            size += 4;                  // DataOffset
            size += 12 + 12;            // ProductVersion, MinimumVersion
            size += 4;                  // Priority
            size += 4;                  // Flags
            size += 4;                  // CreatorId
            size += 16;                 // GUID
            size += 4 + 4;              // IndexCrc32, DataCrc32
            size += 16;                 // SystemTime (8 x ushort)
            size += (uint)(COPYRIGHT_TEXT_LENGTH * 2);
            size += (uint)(BUILD_TEXT_LENGTH * 2);
            size += (uint)(TITLE_TEXT_LENGTH * 2);
            size += (uint)(AUTHOR_TEXT_LENGTH * 2);

            // WNSTRING DescriptionText, spec formula
            ushort lenInChars = (ushort)(DescriptionText ?? "").Length;
            int rawByteSize = 2 + (lenInChars + 1) * 2;
            int alignedSize = (rawByteSize + 3) & ~3;
            size += (uint)alignedSize;

            return size;
        }

        private void WriteHeader(BinaryWriter w, uint dirSetOffset, uint fileSetOffset,
            uint indexSize, uint dataOffset, uint indexCrc32, uint dataCrc32)
        {
            w.Write(ProductId.c0); w.Write(ProductId.c1); w.Write(ProductId.c2); w.Write(ProductId.c3);
            w.Write((byte)'T'); w.Write((byte)'a'); w.Write((byte)'n'); w.Write((byte)'k'); // TankId "Tank"

            w.Write(HEADER_VERSION);
            w.Write(dirSetOffset);
            w.Write(fileSetOffset);
            w.Write(indexSize);
            w.Write(dataOffset);

            w.Write(ProductVersion.v1); w.Write(ProductVersion.v2); w.Write(ProductVersion.v3);
            w.Write(MinimumVersion.v1); w.Write(MinimumVersion.v2); w.Write(MinimumVersion.v3);

            w.Write(Priority);
            w.Write(Flags);

            w.Write(CreatorId.c0); w.Write(CreatorId.c1); w.Write(CreatorId.c2); w.Write(CreatorId.c3);

            w.Write(TankGuid.ToByteArray()); // NOTE: verify byte order matches how the reader/game expects GUID bytes laid out

            w.Write(indexCrc32);
            w.Write(dataCrc32);

            var now = DateTime.UtcNow;
            w.Write((ushort)now.Year);
            w.Write((ushort)now.Month);
            w.Write((ushort)(int)now.DayOfWeek);
            w.Write((ushort)now.Day);
            w.Write((ushort)now.Hour);
            w.Write((ushort)now.Minute);
            w.Write((ushort)now.Second);
            w.Write((ushort)now.Millisecond);

            WriteWideFixed(w, CopyrightText, COPYRIGHT_TEXT_LENGTH);
            WriteWideFixed(w, BuildText, BUILD_TEXT_LENGTH);
            WriteWideFixed(w, TitleText, TITLE_TEXT_LENGTH);
            WriteWideFixed(w, AuthorText, AUTHOR_TEXT_LENGTH);
            WriteWideNString(w, DescriptionText);
        }
    }
}