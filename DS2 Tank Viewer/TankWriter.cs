using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DS2_Tank_Viewer
{
    /// <summary>
    /// Writer counterpart to TankReader.cs. Builds a Dungeon Siege TankFile
    /// (.dsres / .dsmap) from a source directory of loose files.
    ///
    ///   - ProductId ("DSig") and HeaderVersion (1.0.2 / 0x00010002) are DS1-native
    ///     values - what RTC.exe itself writes, confirmed directly from
    ///     Header. This writer stays DS1-native on purpose:
    ///     Form1.cs's PatchToDs2() is applied afterward for the DS2 editor path,
    ///     same as it already is for the legacy RTC.exe path, so this class alone
    ///     can still build real, untouched DS1 .res/.map files for callers who
    ///     don't call PatchToDs2.
    ///   - Section layout (Header -> pad(16) -> Data -> pad(16) -> DirSet -> FileSet)
    ///     and IndexSize (DirSet+FileSet only, header excluded)
    ///   - Each directory's ChildOffsets array is one case-insensitive alphabetical
    ///     merge of its subdirs and files. sorted-map merge
    ///     and binary_search over that same array.
    ///   - Compress = true (default) writes DATAFORMAT_ZLIB with chunked
    ///     compression, scheme (16KB chunks,
    ///     5% minimum-ratio fallback to raw, 16-byte in-place-decode overhead
    ///     margin on full-size chunks) - see the COMPRESSION ENGINE section below
    ///     for the exact reasoning, cross-checked against a real RTC-built tank.
    ///     Set Compress = false to force DATAFORMAT_RAW for every file instead.
    /// </summary>
    public class TankWriter
    {
        // ====================== CONSTANTS (TankStructure.h) ======================

        // RAW_HEADER_PAD from TankStructure.h - confirmed value
        // writer now updated to: once between the header/description text and the start of the data
        // section, and again between the end of the data section and the start of the
        // DirSet. There is NO 4096-byte ("page") alignment anywhere I was wrong before
        // data offset is only ever dword-aligned. There is also no
        // per-file alignment gap inside the data section...
        // m_FileEntry.m_Offset = currentPos - m_DataOffset with files packed back-to-back,
        // zero gap. (Both of those - 4096 section alignment and an 8-byte per-file gap -
        // were guesses in the prior version of this file and did not match what the engine seems to expect.)
        public const uint RAW_HEADER_PAD = 16;
        public const uint INVALID_OFFSET = 0xFFFFFFFF;
        // DS1 value. The original TankStructure.h (Scott Bilas / Gas Powered Games)
        // defines HEADER_VERSION = MAKEVERSION(1,0,2) for Dungeon Siege 1 / LoA, vs.
        // MAKEVERSION(1,1,0) for DS2. The packing is (major<<16)|(minor<<8)|build,
        // which is confirmed independently by TankEditors own PatchToDs2(): it forces bytes
        // [8..11] to 00 01 01 00 -> 0x00010100 little-endian -> exactly
        // (1<<16)|(1<<8)|0, i.e. version 1.1.0 (DS2). Applying the same packing to
        // DS1's 1.0.2 gives (1<<16)|(0<<8)|2 = 0x00010002 -> bytes 02 00 01 00.
        // This is the value RTC.exe writes natively, before PatchToDs2 touches it.
        public const uint HEADER_VERSION = 0x00010002;

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

        // ====================== COMPRESSION (constants) ======================
        public const int CHUNK_SIZE = 16 * 1024;
        public const float MIN_COMPRESSION_RATIO = 0.05f;
        public const uint STARTING_OVERHEAD = 16;

        /// <summary>
        /// If true (default), files are written DATAFORMAT_ZLIB with chunked
        /// compression, matching RTC.exe's real output byte-for-byte in layout
        /// (though not necessarily identical compressed bytes, since deflate
        /// implementations vary slightly - .NET's ZLibStream vs zlib 1.1.4).
        /// Set false to force DATAFORMAT_RAW for every file (the old behavior).
        /// </summary>
        public bool Compress = true;

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
        // DS1 default. PatchToDs2() converts an RTC-built tank from "DSig" (DS1) to
        // "DSg2" (DS2) by overwriting only bytes[2,3] ('i','g' -> 'g','2'). "DSig" is
        // also the native ProductId documented in TankStructure.h / confirmed by every
        // known DS1/.dsres archive (e.g. dump output: "Product id.........: DSig").
        public FourCC ProductId = new FourCC { c0 = (byte)'D', c1 = (byte)'S', c2 = (byte)'i', c3 = (byte)'g' }; // "DSig" DS1 by default
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
        /// backs DescriptionText, a field extraction flow likely never
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
        // this is the standard zlib/PKZIP CRC32.

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

        // ====================== COMPRESSION ENGINE ======================
        //    Implements chunked-zlib path
        //
        //    the wire format is standard zlib
        //    (2-byte header + deflate stream + 4-byte Adler32 trailer) at
        //    Z_DEFAULT_COMPRESSION - exactly what System.IO.Compression.ZLibStream
        //    produces. Seek(chunk->m_Offset) + independent inflate per chunk - so each
        //    chunk is verified to be its own complete, independent zlib stream, not
        //    a single stream spanning chunk boundaries. Where I messed up in the past.
        //  - A file is split into CHUNK_SIZE (16384-byte) pieces. Any chunk that is
        //    NOT a full CHUNK_SIZE piece (i.e. the trailing partial chunk, or the
        //    whole file if it's under 16384 bytes) is compressed with zero
        //    "overhead" - compress the whole chunk, done. (Also did and undid and did ...ugh lol)
        //  - Any FULL 16384-byte chunk reserves STARTING_OVERHEAD (16) raw bytes at
        //    its tail: only the first (write-overhead) bytes are deflated, and the
        //    last `overhead` bytes are appended uncompressed after the deflate
        //    stream. (think I've been doing this right for awhile) for any
        //    correctly-implemented deflate, the first attempt always fits (this
        //    matches every chunk observed in a real RTC-built tank, which all show
        //    ExtraBytes == 16, never doubled) - so we do it in one shot rather than
        //    replicating the retry loop.
        //  - Per-chunk fallback: if a chunk's compressed size plus overhead isn't
        //    smaller than the chunk itself, that chunk is stored raw instead
        //    (CompressedSize == UncompressedSize signals this to the reader).
        //  - Per-file fallback: after all chunks are done, if the file's overall
        //    compression ratio is below MIN_COMPRESSION_RATIO (5%), the entire file
        //    reverts to DATAFORMAT_RAW - no CompressedHeader/ChunkHeader at all.

        private struct ChunkRec
        {
            public uint UncompressedSize, CompressedSize, ExtraBytes, Offset;
        }

        private struct CompressedFileResult
        {
            public ushort Format;              // DATAFORMAT_RAW or DATAFORMAT_ZLIB
            public byte[] PhysicalBytes;        // what actually gets written to the data section
            public List<ChunkRec> Chunks;       // empty when Format == RAW
        }

        private static byte[] ZlibDeflateOnce(byte[] src, int offset, int count)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(src, offset, count);
            return ms.ToArray();
        }

        private CompressedFileResult CompressFile(byte[] raw)
        {
            if (!Compress || raw.Length == 0)
                return new CompressedFileResult { Format = DATAFORMAT_RAW, PhysicalBytes = raw, Chunks = new List<ChunkRec>() };

            var chunks = new List<ChunkRec>();
            using var physical = new MemoryStream();
            uint runningOffset = 0;
            int pos = 0;

            while (pos < raw.Length)
            {
                int write = Math.Min(CHUNK_SIZE, raw.Length - pos);
                uint overhead = (write < CHUNK_SIZE) ? 0u : STARTING_OVERHEAD;
                int localWrite = write - (int)overhead;

                byte[] compressed = ZlibDeflateOnce(raw, pos, localWrite);
                uint outSize = (uint)compressed.Length;

                bool fits = (overhead == 0) || (outSize + overhead < (uint)write);

                byte[] tail;
                if (!fits)
                {
                    // per-chunk fallback: store this chunk raw
                    compressed = new byte[write];
                    Array.Copy(raw, pos, compressed, 0, write);
                    outSize = (uint)write;
                    overhead = 0;
                    tail = Array.Empty<byte>();
                }
                else
                {
                    tail = new byte[overhead];
                    Array.Copy(raw, pos + localWrite, tail, 0, (int)overhead);
                }

                physical.Write(compressed, 0, compressed.Length);
                physical.Write(tail, 0, tail.Length);

                chunks.Add(new ChunkRec
                {
                    UncompressedSize = (uint)write,
                    CompressedSize = outSize,
                    ExtraBytes = overhead,
                    Offset = runningOffset
                });

                runningOffset += outSize + overhead;
                pos += write;
            }

            byte[] physicalBytes = physical.ToArray();

            float ratio = 1f - ((float)physicalBytes.Length / raw.Length);
            if (ratio < MIN_COMPRESSION_RATIO)
                return new CompressedFileResult { Format = DATAFORMAT_RAW, PhysicalBytes = raw, Chunks = new List<ChunkRec>() };

            return new CompressedFileResult { Format = DATAFORMAT_ZLIB, PhysicalBytes = physicalBytes, Chunks = chunks };
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
            // (order also doesn't matter).
            var allDirs = new List<BuildDirEntry>();
            FlattenDirs(root, allDirs);

            var allFiles = new List<(BuildDirEntry Dir, BuildFileEntry File)>();
            foreach (var d in allDirs)
                foreach (var f in d.Files)
                    allFiles.Add((d, f));

            using (var ms = new MemoryStream())
            {
                // ---------- Pass 1: write DATA section, remember offsets ----------
                var fileMeta = new Dictionary<BuildFileEntry, (uint Offset, uint Size, uint Crc, ushort Format, List<ChunkRec> Chunks)>();

                using (var dataStream = new MemoryStream())
                using (var dataWriter = new BinaryWriter(dataStream))
                {
                    // Running CRC32 over each file's UNCOMPRESSED bytes, concatenated in
                    // processing order (AddCRC32 is fed rawData, i.e. pre-compression bytes,
                    // regardless of what's physically written to disk). Deliberately NOT the
                    // same as Crc32(fullDataBytes) once compression makes those two diverge.
                    uint dataCrcRunning = 0xFFFFFFFF;

                    foreach (var (dir, file) in allFiles)
                    {
                        byte[] bytes = File.ReadAllBytes(file.SourcePath);
                        uint offset = (uint)dataStream.Position;
                        uint crc = Crc32(bytes);

                        foreach (byte b in bytes)
                            dataCrcRunning = Crc32Table[(dataCrcRunning ^ b) & 0xFF] ^ (dataCrcRunning >> 8);

                        var result = CompressFile(bytes);
                        dataWriter.Write(result.PhysicalBytes);
                        fileMeta[file] = (offset, (uint)bytes.Length, crc, result.Format, result.Chunks);
                    }

                    uint dataSectionSize = (uint)dataStream.Length;
                    byte[] fullDataBytes = dataStream.ToArray();
                    uint dataCrc32 = dataCrcRunning ^ 0xFFFFFFFF;

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
                        fileCursor += MeasureFileEntry(file, fileMeta[file].Format, fileMeta[file].Chunks.Count);
                    }
                    uint fileSetSize = fileCursor;

                    // ---------- Compute section layout ----------
                    // RAW layout
                    //  Header sizes here are all already dword multiples
                    //  so the extra align-up is a no-op in practice, but it's kept for
                    //  correctness in case DescriptionText length ever isn't.)
                    uint headerSize = MeasureHeader();
                    uint dataOffset = AlignUp(headerSize + RAW_HEADER_PAD, 4);
                    uint dirSetOffset = dataOffset + dataSectionSize + RAW_HEADER_PAD;
                    uint fileSetOffset = dirSetOffset + dirSetSize;
                    uint indexSize = dirSetSize + fileSetSize; // header NOT included

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
                            // binary_search across
                            // this array comparing names regardless of dir/file, so it MUST
                            // be one merged case-insensitive alphabetical list - NOT all
                            // dirs followed by all files.
                            foreach (var child in MergeSortedChildren(d))
                            {
                                if (child.IsDir)
                                    idxWriter.Write(dirRelOffset[child.Dir]);
                                else
                                    idxWriter.Write(fileSetOffset - dirSetOffset + fileRelOffset[child.File]);
                            }
                        }

                        // FileSet
                        idxWriter.Write((uint)allFiles.Count);
                        foreach (var (dir, file) in allFiles) idxWriter.Write(fileRelOffset[file]);
                        foreach (var (dir, file) in allFiles)
                        {
                            var (offset, size, crc, format, chunks) = fileMeta[file];
                            idxWriter.Write(dirRelOffset[dir]);          // ParentOffset
                            idxWriter.Write(size);                       // Size (uncompressed)
                            idxWriter.Write(offset);                     // Offset (relative to data section top)
                            idxWriter.Write(crc);                        // CRC32 (of uncompressed bytes)
                            WriteFileTime(idxWriter, file.LastWriteTimeUtc);
                            idxWriter.Write(format);                     // Format
                            idxWriter.Write(FILEFLAG_NONE);              // Flags
                            WriteNString(idxWriter, file.Name);

                            if (format != DATAFORMAT_RAW)
                            {
                                // CompressedHeader: total physical bytes (all chunks' compressed
                                // data + their overhead tails combined), then the constant chunk
                                // size used to split this file (always CHUNK_SIZE here - real RTC
                                // output shows every compressed file carries this, even ones under
                                // one chunk in size, rather than collapsing to 0).
                                uint totalCompressedSize = (uint)chunks.Sum(c => (long)c.CompressedSize + c.ExtraBytes);
                                idxWriter.Write(totalCompressedSize);
                                idxWriter.Write((uint)CHUNK_SIZE);
                                foreach (var c in chunks)
                                {
                                    idxWriter.Write(c.UncompressedSize);
                                    idxWriter.Write(c.CompressedSize);
                                    idxWriter.Write(c.ExtraBytes);
                                    idxWriter.Write(c.Offset);
                                }
                            }
                        }

                        indexBytes = idxStream.ToArray();
                    }

                    uint indexCrc32 = Crc32(indexBytes);

                    if (SkipChecksums)
                    {
                        // INVALID_CHECKSUM per TankStructure.h - tells the engine not to validate?
                        indexCrc32 = 0;
                        dataCrc32 = 0;
                    }

                    // ---------- Write Header ----------
                    using (var outFs = new FileStream(outputTankPath, FileMode.Create, FileAccess.Write))
                    using (var w = new BinaryWriter(outFs))
                    {
                        WriteHeader(w, dirSetOffset, fileSetOffset, indexSize, dataOffset, indexCrc32, dataCrc32);
                        // pad up to dataOffset (RAW_HEADER_PAD(16) + dword-align)
                        while (outFs.Position < dataOffset) outFs.WriteByte(0);
                        w.Write(fullDataBytes);
                        // second RAW_HEADER_PAD(16) block before the index
                        w.Write(new byte[RAW_HEADER_PAD]);
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

        private struct ChildRef
        {
            public bool IsDir;
            public BuildDirEntry Dir;
            public BuildFileEntry File;
            public string Name => IsDir ? Dir.Name : File.Name;
        }

        /// <summary>
        /// Merges a directory's subdirs and files into a single case-insensitive
        /// alphabetical order. compare_no_case semantics.
        /// d.Dirs and d.Files are already individually sorted (see PopulateDir), so
        /// this is a standard two-pointer merge.
        /// </summary>
        private List<ChildRef> MergeSortedChildren(BuildDirEntry d)
        {
            var result = new List<ChildRef>(d.Dirs.Count + d.Files.Count);
            int di = 0, fi = 0;
            while (di < d.Dirs.Count || fi < d.Files.Count)
            {
                if (di >= d.Dirs.Count)
                {
                    result.Add(new ChildRef { IsDir = false, File = d.Files[fi++] });
                }
                else if (fi >= d.Files.Count)
                {
                    result.Add(new ChildRef { IsDir = true, Dir = d.Dirs[di++] });
                }
                else
                {
                    int cmp = string.Compare(d.Files[fi].Name, d.Dirs[di].Name, StringComparison.OrdinalIgnoreCase);
                    if (cmp < 0)
                        result.Add(new ChildRef { IsDir = false, File = d.Files[fi++] });
                    else
                        result.Add(new ChildRef { IsDir = true, Dir = d.Dirs[di++] });
                }
            }
            return result;
        }

        private uint MeasureFileEntry(BuildFileEntry f, ushort format, int chunkCount)
        {
            uint size = 4 + 4 + 4 + 4 + 8 + 2 + 2; // Parent+Size+Offset+Crc+FileTime+Format+Flags
            size += MeasureNStringSize(f.Name);
            if (format != DATAFORMAT_RAW)
                size += 8 + (uint)(16 * chunkCount); // CompressedHeader(8) + ChunkHeader(16) each
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

            // RTC.exe's real output packs Priority as (PRIORITY_USER<<16)|minorPriority -
            // a real user-built DS2 tank shows Priority = 0x40004000, not the plain
            // 0x00004000 PRIORITY_USER constant, so this packing must happen in
            // RTC.exe's own CLI/main() handling
            // rather than confirmed from source. If a small/plain value is given
            // (<= 0xFFFF, i.e. just a "minor priority", which is what Form1.cs's UI
            // default of 0x4000 looks like), auto-pack it to match. Pass an already-full
            // 32-bit value if you need exact control and don't want the auto-pack.
            uint packedPriority = (Priority <= 0xFFFF) ? (0x40000000u | Priority) : Priority;
            w.Write(packedPriority);
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