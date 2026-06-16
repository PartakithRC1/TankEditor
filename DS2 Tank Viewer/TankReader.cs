using ICSharpCode.SharpZipLib.Zip.Compression;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using static DS2_Tank_Viewer.TankReader;

namespace DS2_Tank_Viewer
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FourCC
    {
        public byte c0, c1, c2, c3;
        public override string ToString() => new string(new[] { (char)c0, (char)c1, (char)c2, (char)c3 });
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ProductVersion { public uint v1, v2, v3; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FileTime { public uint LowDateTime; public uint HighDateTime; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

    public class TankHeader
    {
        public FourCC ProductId { get; set; }
        public FourCC TankId { get; set; }
        public uint HeaderVersion { get; set; }
        public uint DirSetOffset { get; set; }
        public uint FileSetOffset { get; set; }
        public uint IndexSize { get; set; }
        public uint DataOffset { get; set; }
        public ProductVersion ProductVersion { get; set; }
        public ProductVersion MinimumVersion { get; set; }
        public uint Priority { get; set; }
        public uint Flags { get; set; }
        public FourCC CreatorId { get; set; }
        public byte[] Guid { get; set; } = new byte[16];
        public uint IndexCrc32 { get; set; }
        public uint DataCrc32 { get; set; }
        public SystemTime UtcBuildTime { get; set; }
        public string CopyrightText { get; set; } = "";
        public string BuildText { get; set; } = "";
        public string TitleText { get; set; } = "";
        public string AuthorText { get; set; } = "";
        public string DescriptionText { get; set; } = "";
    }

    public class DirEntry
    {
        public uint RelativeOffset { get; set; }
        public uint ParentOffset { get; set; }
        public uint ChildCount { get; set; }
        public FileTime FileTime { get; set; }
        public string Name { get; set; } = "";
        public List<uint> ChildOffsets { get; set; } = new List<uint>();
    }

    public class FileEntry
    {
        public uint ParentOffset { get; set; }
        public uint Size { get; set; }
        public uint Offset { get; set; }
        public uint Crc32 { get; set; }
        public FileTime FileTime { get; set; }
        public ushort Format { get; set; }
        public ushort Flags { get; set; }
        public string Name { get; set; } = "";
        public bool IsCompressed => Format != 0;
        public CompressedHeader CompressedInfo { get; set; }
    }

    public class TankReader
    {
        public TankHeader Header { get; private set; }
        private List<DirEntry> dirEntries = new List<DirEntry>();
        private List<FileEntry> fileEntries = new List<FileEntry>();
        private Dictionary<string, FileEntry> fileTable = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<uint, DirEntry> dirLookup = new Dictionary<uint, DirEntry>();
        private string currentTankPath;
        private List<string> extractionLog = new List<string>();

        // ====================== NSTRING ALIGNMENT ======================
        //
        // Mirrors the C++ alignToDword(size) = size + (4 - size%4)
        // Note: this ALWAYS adds padding bytes even when already aligned (adds 4 if size%4==0).
        // Then the call site does: alignToDword(lenInChars + 2) - 2
        // So total bytes consumed after the length word = alignToDword(len+2) - 2
        //
        private ushort AlignToDword(ushort size)
        {
            // C++ version: size + (4 - (size % 4))  -- always adds 1-4 bytes
            return (ushort)(size + (4 - (size % 4)));
        }

        public string ReadNString(BinaryReader reader)
        {
            ushort lenInChars = reader.ReadUInt16();
            if (lenInChars == 0)
            {
                reader.ReadUInt16(); // consume padding word to keep dword alignment
                return "";
            }

            // Total bytes to read after the length word:
            //   alignToDword(lenInChars + 2) - 2
            // This gives us lenInChars of actual text plus 0-3 null padding bytes.
            ushort totalToRead = (ushort)(AlignToDword((ushort)(lenInChars + 2)) - 2);
            byte[] buffer = reader.ReadBytes(totalToRead);
            return Encoding.ASCII.GetString(buffer, 0, lenInChars);
        }

        private string ReadWideFixed(BinaryReader reader, int maxChars)
        {
            byte[] bytes = reader.ReadBytes(maxChars * 2);
            int len = 0;
            while (len < bytes.Length - 1 && (bytes[len] != 0 || bytes[len + 1] != 0))
                len += 2;
            return Encoding.Unicode.GetString(bytes, 0, len);
        }

        private string ReadWideNString(BinaryReader reader)
        {
            // Wide NString: same alignment logic but each character is 2 bytes.
            // C++ readWNString: same alignToDword trick, reads lenInChars*2 bytes.
            ushort lenInChars = reader.ReadUInt16();
            if (lenInChars == 0) { reader.ReadUInt16(); return ""; }

            // Same formula as ReadNString but for wide chars:
            ushort totalToRead = (ushort)(AlignToDword((ushort)(lenInChars + 2)) - 2);
            byte[] bytes = reader.ReadBytes(totalToRead);
            return Encoding.Unicode.GetString(bytes, 0, lenInChars * 2);
        }

        // ====================== COMPRESSED HEADER ======================

        public class CompressedChunk
        {
            public uint UncompressedSize { get; set; }
            public uint CompressedSize { get; set; }
            public uint ExtraBytes { get; set; }
            public uint Offset { get; set; }

            // A chunk is compressed when its uncompressed and compressed sizes differ.
            // (From ChunkHeader::IsCompressed in TankStructure.h)
            public bool IsCompressed => UncompressedSize != CompressedSize;
        }

        public class CompressedHeader
        {
            public uint CompressedSize { get; set; }
            public uint ChunkSize { get; set; }
            public uint NumChunks { get; set; }
            public List<CompressedChunk> Chunks { get; set; } = new List<CompressedChunk>();
        }

        // ====================== LOAD ======================

        public void Load(string filePath)
        {
            currentTankPath = filePath;
            dirEntries.Clear();
            fileEntries.Clear();
            fileTable.Clear();
            dirLookup.Clear();

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                Header = ReadHeader(reader);

                // === DirSet ===
                reader.BaseStream.Position = Header.DirSetOffset;
                uint numDirs = reader.ReadUInt32();
                var dirOffsets = new List<uint>((int)numDirs);
                for (int i = 0; i < numDirs; i++)
                    dirOffsets.Add(reader.ReadUInt32());

                for (int i = 0; i < numDirs; i++)
                {
                    uint relativeOffset = dirOffsets[i];
                    reader.BaseStream.Position = Header.DirSetOffset + relativeOffset;

                    var dir = new DirEntry
                    {
                        RelativeOffset = relativeOffset,
                        ParentOffset = reader.ReadUInt32(),
                        ChildCount = reader.ReadUInt32(),
                        FileTime = new FileTime { LowDateTime = reader.ReadUInt32(), HighDateTime = reader.ReadUInt32() }
                    };
                    dir.Name = ReadNString(reader);

                    if (dir.ParentOffset == 0 && string.IsNullOrEmpty(dir.Name))
                        dir.Name = "\\";

                    for (int c = 0; c < dir.ChildCount; c++)
                        dir.ChildOffsets.Add(reader.ReadUInt32());

                    dirEntries.Add(dir);
                    dirLookup[relativeOffset] = dir;
                }

                // === FileSet ===
                reader.BaseStream.Position = Header.FileSetOffset;
                uint numFiles = reader.ReadUInt32();
                var fileOffsets = new List<uint>((int)numFiles);
                for (int i = 0; i < numFiles; i++)
                    fileOffsets.Add(reader.ReadUInt32());

                for (int i = 0; i < numFiles; i++)
                {
                    reader.BaseStream.Position = Header.FileSetOffset + fileOffsets[i];

                    var file = new FileEntry
                    {
                        ParentOffset = reader.ReadUInt32(),
                        Size = reader.ReadUInt32(),
                        Offset = reader.ReadUInt32(),
                        Crc32 = reader.ReadUInt32(),
                        FileTime = new FileTime { LowDateTime = reader.ReadUInt32(), HighDateTime = reader.ReadUInt32() },
                        Format = reader.ReadUInt16(),
                        Flags = reader.ReadUInt16(),
                        Name = ReadNString(reader)
                    };

                    if (file.IsCompressed && file.Size > 0)
                    {
                        var compHeader = new CompressedHeader
                        {
                            CompressedSize = reader.ReadUInt32(),
                            ChunkSize = reader.ReadUInt32()
                        };


                        // NumChunks is COMPUTED (not stored in file), same as C++:
                        //   numChunks = ceil(fileSize / chunkSize)  when chunkSize != 0
                        if (compHeader.ChunkSize > 0)
                        {
                            compHeader.NumChunks = (uint)Math.Ceiling((double)file.Size / compHeader.ChunkSize);

                            for (uint c = 0; c < compHeader.NumChunks; c++)
                            {
                                compHeader.Chunks.Add(new CompressedChunk
                                {
                                    UncompressedSize = reader.ReadUInt32(),
                                    CompressedSize = reader.ReadUInt32(),
                                    ExtraBytes = reader.ReadUInt32(),
                                    Offset = reader.ReadUInt32()
                                });
                            }
                        }

                        file.CompressedInfo = compHeader;


                    }

                    fileEntries.Add(file);

                }

                BuildFileTable();
            }
        }

        private TankHeader ReadHeader(BinaryReader reader)
        {
            var h = new TankHeader
            {
                ProductId = new FourCC { c0 = reader.ReadByte(), c1 = reader.ReadByte(), c2 = reader.ReadByte(), c3 = reader.ReadByte() },
                TankId = new FourCC { c0 = reader.ReadByte(), c1 = reader.ReadByte(), c2 = reader.ReadByte(), c3 = reader.ReadByte() }
            };
            // Accept both DS1 ("DSig") and DS2 ("DSg2") product IDs
            string pid = h.ProductId.ToString();
            if (pid != "DSg2" && pid != "DSig")
                throw new Exception($"Invalid signature! Got {h.ProductId}{h.TankId}");

            h.HeaderVersion = reader.ReadUInt32();
            h.DirSetOffset = reader.ReadUInt32();
            h.FileSetOffset = reader.ReadUInt32();
            h.IndexSize = reader.ReadUInt32();
            h.DataOffset = reader.ReadUInt32();

            h.ProductVersion = new ProductVersion { v1 = reader.ReadUInt32(), v2 = reader.ReadUInt32(), v3 = reader.ReadUInt32() };
            h.MinimumVersion = new ProductVersion { v1 = reader.ReadUInt32(), v2 = reader.ReadUInt32(), v3 = reader.ReadUInt32() };
            h.Priority = reader.ReadUInt32();
            h.Flags = reader.ReadUInt32();
            h.CreatorId = new FourCC { c0 = reader.ReadByte(), c1 = reader.ReadByte(), c2 = reader.ReadByte(), c3 = reader.ReadByte() };

            h.Guid = reader.ReadBytes(16);
            h.IndexCrc32 = reader.ReadUInt32();
            h.DataCrc32 = reader.ReadUInt32();

            h.UtcBuildTime = new SystemTime
            {
                Year = reader.ReadUInt16(),
                Month = reader.ReadUInt16(),
                DayOfWeek = reader.ReadUInt16(),
                Day = reader.ReadUInt16(),
                Hour = reader.ReadUInt16(),
                Minute = reader.ReadUInt16(),
                Second = reader.ReadUInt16(),
                Milliseconds = reader.ReadUInt16()
            };

            h.CopyrightText = ReadWideFixed(reader, 100);
            h.BuildText = ReadWideFixed(reader, 100);
            h.TitleText = ReadWideFixed(reader, 100);
            h.AuthorText = ReadWideFixed(reader, 40);
            h.DescriptionText = ReadWideNString(reader);

            return h;
        }

        private void BuildFileTable()
        {
            foreach (var file in fileEntries)
            {
                string fullPath = BuildFullPath(file.ParentOffset) + file.Name;
                fileTable[fullPath] = file;
            }
        }

        private string BuildFullPath(uint parentRelativeOffset)
        {
            if (parentRelativeOffset == 0) return "\\";
            if (dirLookup.TryGetValue(parentRelativeOffset, out DirEntry dir))
            {
                string parentPath = BuildFullPath(dir.ParentOffset);
                return parentPath.TrimEnd('\\') + "\\" + dir.Name + "\\";
            }
            return "\\";
        }

        public List<string> GetFileList() => new List<string>(fileTable.Keys);
        public int FileCount => fileEntries.Count;
        public int DirCount => dirEntries.Count;

        // ====================== DECOMPRESSION ======================

        /// <summary>
        /// Decompress a single zlib chunk. Tries with-header first, then raw deflate.
        /// </summary>
        private byte[] DecompressZlibChunk(byte[] compressedData, int expectedSize)
        {
            if (compressedData == null || compressedData.Length == 0)
                return Array.Empty<byte>();

            // Try standard zlib (with 0x78 header)
            try
            {
                var inflater = new Inflater(noHeader: false);
                inflater.SetInput(compressedData);
                byte[] output = new byte[expectedSize];
                int got = inflater.Inflate(output);
                if (got > 0) return output.Take(got).ToArray();
            }
            catch { }

            // Fallback: raw deflate (no zlib header)
            try
            {
                var inflater = new Inflater(noHeader: true);
                inflater.SetInput(compressedData);
                byte[] output = new byte[expectedSize];
                int got = inflater.Inflate(output);
                if (got > 0) return output.Take(got).ToArray();
            }
            catch { }

            extractionLog.Add("Zlib failed completely. Returning raw data buffer.");
            return compressedData;
        }

        private byte[] DecompressFile(FileEntry entry)
        {
            // FileFlagInvalid = 1 << 15 = 0x8000
            if ((entry.Flags & 0x8000) != 0)
            {
                extractionLog.Add($"SKIPPED: {entry.Name} (Flagged as Invalid by engine)");
                return Array.Empty<byte>();
            }

            if (entry.Size == 0) return Array.Empty<byte>();

            if (!entry.IsCompressed)
                return ReadRawData(entry.Offset, (int)entry.Size);

            var comp = entry.CompressedInfo;

            // No chunk info (chunkSize == 0 means not chunked): read the whole blob and decompress
            if (comp == null || comp.ChunkSize == 0 || comp.Chunks.Count == 0)
            {
                // Read compressedSize bytes and decompress directly
                uint readSize = (comp != null && comp.CompressedSize > 0) ? comp.CompressedSize : entry.Size;
                byte[] blob = ReadRawData(entry.Offset, (int)readSize);
                if (entry.Format == 1) // Zlib
                    return DecompressZlibChunk(blob, (int)entry.Size);
                // LZO unchunked: caller would need lzo.net – left as passthrough for now
                return blob;
            }

            // === CHUNKED DECOMPRESSION ===
            //
            // The critical detail from TankStructure.h / tank_file_reader.cpp:
            //
            //   compressedData.resize(chunk.compressedSize + chunk.extraBytes)
            //   tank.readBytes(compressedData.data(), compressedData.size())          // read compressed+extra together
            //
            //   uncompressedData.resize(chunk.uncompressedSize + chunk.extraBytes)
            //   decompress(uncompressedData, &uncompressedLen,
            //              compressedData.data(),
            //              compressedData.size() - chunk.extraBytes)                  // decompress only compressedSize bytes
            //
            //   fileContents.append(uncompressedData[0 .. uncompressedLen])          // keep decompressed bytes
            //
            //   if (chunk.extraBytes != 0 && !compressedData.empty())
            //       fileContents.append(compressedData[compressedSize .. compressedSize + extraBytes])
            //       // extraBytes are ALREADY in compressedData buffer, NOT a separate read
            //
            var result = new List<byte>((int)entry.Size);

            using (var fs = new FileStream(currentTankPath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                long dataBase = Header.DataOffset + entry.Offset;

                foreach (var chunk in comp.Chunks)
                {
                    // Sanity-check chunk sizes to catch corruption before allocating memory
                    if (chunk.CompressedSize > 64 * 1024 * 1024 ||
                        chunk.UncompressedSize > 64 * 1024 * 1024 ||
                        chunk.ExtraBytes > 1024 * 1024)
                    {
                        extractionLog.Add($"CORRUPTION: {entry.Name} contains impossible chunk sizes. Skipping chunk.");
                        continue;
                    }

                    reader.BaseStream.Position = dataBase + chunk.Offset;

                    if (!chunk.IsCompressed)
                    {
                        // Stored without compression — sizes are equal, just copy bytes
                        byte[] raw = reader.ReadBytes((int)chunk.UncompressedSize);
                        result.AddRange(raw);
                    }
                    else
                    {
                        // Read compressed data + extra bytes together (they are contiguous in file)
                        int totalRead = (int)(chunk.CompressedSize + chunk.ExtraBytes);
                        byte[] compBuf = reader.ReadBytes(totalRead);

                        // Decompress only the first CompressedSize bytes
                        byte[] compOnly = compBuf.Take((int)chunk.CompressedSize).ToArray();

                        byte[] decompressed;
                        if (entry.Format == 1) // Zlib
                        {
                            decompressed = DecompressZlibChunk(compOnly, (int)chunk.UncompressedSize);
                        }
                        else if (entry.Format == 2) // LZO
                        {
                            decompressed = new byte[chunk.UncompressedSize];
                            using (var ms = new MemoryStream(compOnly))
                            using (var lzo = new lzo.net.LzoStream(ms, CompressionMode.Decompress))
                                lzo.Read(decompressed, 0, decompressed.Length);
                        }
                        else
                        {
                            decompressed = compOnly;
                        }

                        result.AddRange(decompressed);

                        // ExtraBytes trail the compressed data in the SAME buffer.
                        // They are copied verbatim to the end of the decompressed chunk output.
                        if (chunk.ExtraBytes > 0)
                        {
                            byte[] extra = compBuf.Skip((int)chunk.CompressedSize).Take((int)chunk.ExtraBytes).ToArray();
                            result.AddRange(extra);
                        }
                    }
                }
            }

            return result.ToArray();
        }

        private byte[] ReadRawData(long offset, int size)
        {
            using (var fs = new FileStream(currentTankPath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                reader.BaseStream.Position = Header.DataOffset + offset;
                return reader.ReadBytes(size);
            }
        }

        // ====================== EXTRACTION ======================

        public bool ExtractSingleFile(FileEntry entry, string destFilePath)
        {
            try
            {
                byte[] data = DecompressFile(entry);
                string dir = Path.GetDirectoryName(destFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(destFilePath, data);
                extractionLog.Add($"SUCCESS: {entry.Name} ({data.Length} bytes)");
                return true;
            }
            catch (Exception ex)
            {
                extractionLog.Add($"FAILED: {entry.Name} | Error: {ex.Message}");
                return false;
            }
        }

        public void ExtractAll(string outputRootPath)
        {
            extractionLog.Clear();
            if (string.IsNullOrEmpty(outputRootPath))
                throw new ArgumentException("Output path required");

            string tankName = Path.GetFileNameWithoutExtension(currentTankPath);
            string basePath = Path.Combine(outputRootPath, tankName);
            Directory.CreateDirectory(basePath);

            int successCount = 0;
            foreach (var kvp in fileTable)
            {
                string relativePath = kvp.Key.TrimStart('\\');
                string destFile = Path.Combine(basePath, relativePath);
                if (ExtractSingleFile(kvp.Value, destFile))
                    successCount++;
            }

            //ShowExtractionLog(successCount);
            MessageBox.Show("Extraction Complete!");
        }

        private void ShowExtractionLog(int successCount)
        {
            string log = string.Join("\n", extractionLog);
            string summary = $"Extracted {successCount} / {fileTable.Count} files successfully.\n\n" + log;
            try { MessageBox.Show(summary, "Extraction Complete", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { MessageBox.Show("Too many files to show summary check log."); }
            try { File.WriteAllText("ds2_extraction_log.txt", summary); } catch { }
        }

        public int ExtractSelected(string selectedPath, string outputRootPath)
        {
            if (string.IsNullOrEmpty(selectedPath))
                throw new ArgumentNullException(nameof(selectedPath));

            // Normalise: use backslashes, no leading slash
            string prefix = selectedPath.Replace('/', '\\').TrimStart('\\');

            // Collect every fileTable key that matches the prefix.
            // fileTable keys have a leading backslash (e.g. "\ui\config\options.gas"),
            // so we strip it before comparing.
            var matches = new List<string>();
            foreach (string key in fileTable.Keys)
            {
                string normalised = key.TrimStart('\\');
                // Exact file match  OR  folder prefix match (prefix + backslash)
                if (normalised.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                    normalised.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(key);
                }
            }

            if (matches.Count == 0)
                return 0;

            string basePath = Path.Combine(outputRootPath, "Extracted");
            Directory.CreateDirectory(basePath);

            int success = 0;
            foreach (string key in matches)
            {
                if (!fileTable.TryGetValue(key, out var entry))
                    continue;

                string relativePath = key.TrimStart('\\');
                string dest = Path.Combine(basePath, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                if (ExtractSingleFile(entry, dest))
                    success++;
            }

            return success;
        }

    }
}