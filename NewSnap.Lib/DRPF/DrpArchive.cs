﻿using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NewSnap.Lib
{
    public class DrpArchive
    {
        /// <summary> Crc32(DRPF) </summary>
        private const uint ArchiveHeaderMagic = 0x7F0E5359;
        /// <summary> Crc32(fhdr) </summary>
        private const uint CryptoBlockMagic = 0xC65753E8;
        /// <summary> Crc32(resd) </summary>
        private const uint FileBlockMagic = 0xE0A331B4;
        /// <summary> Crc32(Oodl) </summary>
        private const uint CompressedDataMagic = 0xE42D98BA;

        /* Structure:
         * u32 Magic (DRPF)
         * u32 Checksum (CRC32 over data[0x10..0x94])
         * u32 Content Handling (encrypted toggle)
         * u32 Header Size
         * u32 FileCount
         *
         * 0x14:
         * byte[0x80] Seed Table
         *
         * 0x94:
         * Variable Size Chunks for each file
         *
         * End:
         * u32[] Footer
         */

        private readonly DrpFileEntry[] _files;
        private readonly byte[] _seedTable;

        public int FileCount => _files.Length;

        public string GetFileName(int i) => _files[i].GetFullFileName();
        public byte[] GetFileData(int i) => _files[i].GetData();

        /// <summary>
        /// Initializes the archive from the input data. The input reference will be decrypted if it is encrypted.
        /// </summary>
        public DrpArchive(byte[] data)
        {
            if ((data.Length & 3) != 0)
                throw new ArgumentException("Invalid file size! Expected a multiple of 4.");

            var archive = new ReadOnlySpan<byte>(data);
            // Check the archive magic.
            if (BitConverter.ToUInt32(archive) != ArchiveHeaderMagic)
                throw new ArgumentException("Invalid archive magic");

            // Copy the encryption table.
            _seedTable = archive.Slice(0x14, 0x80).ToArray();

            // Decrypt the archive.
            if (BitConverter.ToUInt32(archive[0x08..]) != CryptoBlockMagic)
                Decrypt(data.AsSpan());

            // Verify the header's checksum (CRC32 over file count and seed table).
            if (BitConverter.ToUInt32(archive[0x04..]) != Crc32.ComputeChecksum(archive[0x10..0x94]))
                throw new ArgumentException("Invalid archive checksum");

            // Get the file count.
            var fileCount = BitConverter.ToInt32(archive[0x10..]);
            archive = archive[0x94..];
            _files = ReadFiles(fileCount, ref archive);

            // Whatever is left in the archive span is the footer.
            // Not that we have any use for it.
        }

        private static DrpFileEntry[] ReadFiles(int fileCount, ref ReadOnlySpan<byte> archive)
        {
            var result = new DrpFileEntry[fileCount];
            for (int i = 0; i < fileCount; i++)
            {
                var file = GetFile(archive);
                result[i] = file;
                archive = archive[file.Header.SizeTotal..];
            }
            return result;
        }

        private void Decrypt(Span<byte> archive)
        {
            // Decrypt the crypto chunk (skipping data, as the crypto table isn't encrypted).
            var seed = BitConverter.ToUInt32(archive[0x04..]);
            CryptHeader(archive, seed);

            if (BitConverter.ToUInt32(archive[0x08..]) != CryptoBlockMagic)
                throw new ArgumentException("Invalid Crypto Block Magic!");

            // TODO: There's logic for the case when this isn't 0x90 -- is this ever used?
            if (BitConverter.ToUInt32(archive[0x0C..]) != 0x90)
                throw new ArgumentException("Invalid Crypto Block Size!");

            if (BitConverter.ToUInt32(archive[0x04..]) != Crc32.ComputeChecksum(archive[0x10..0x94]))
                throw new ArgumentException("Invalid archive checksum");

            var fileCount = BitConverter.ToInt32(archive[0x10..]);
            var fileData = archive[0x94..];
            for (int i = 0; i < fileCount; ++i)
            {
                CryptChunk(fileData, i);
                if (BitConverter.ToUInt32(fileData[4..]) != FileBlockMagic)
                    throw new ArgumentException("Invalid File Block Magic?");

                // Advance
                var chunkSize = BitConverter.ToInt32(fileData[8..]);
                fileData = fileData[chunkSize..];
            }

            // Decrypt remainder of file as the footer.
            CryptFooter(fileData, seed);
        }

        private void CryptHeader(Span<byte> archive, uint seed)
        {
            // Get the RNG
            var rng = GetEncryptionRng(seed);

            // Decrypt the header
            rng.DecryptWord(archive[0x08..]);
            rng.DecryptWord(archive[0x0C..]);

            // Decrypt the file count.
            rng.DecryptWord(archive[0x10..]);
        }

        private void CryptFooter(Span<byte> footer, uint seed)
        {
            var rng = GetEncryptionRng(seed);

            // Decrypt to end.
            while (footer.Length != 0)
            {
                rng.DecryptWord(footer);
                footer = footer[sizeof(uint)..];
            }
        }

        private void CryptChunk(Span<byte> archive, int index, bool cryptData = true)
        {
            var seed = BitConverter.ToUInt32(archive);
            var rng = GetEncryptionRng(seed);

            // Decrypt the chunk header
            rng.DecryptWord(archive[0x04..]);
            rng.DecryptWord(archive[0x08..]);

            if (!cryptData)
                return;

            // Decrypt the data
            var chunkSize = BitConverter.ToUInt32(archive[0x08..]);
            if ((chunkSize & 3) != 0)
                throw new ArgumentException($"Invalid ChunkSize {chunkSize:X} at chunk index {index:X}");

            // Treat as u32 instead of u8, since we have the above assumption. Can go 4x as fast.
            var asUint = MemoryMarshal.Cast<byte, uint>(archive);
            var count = chunkSize >> 2;
            for (int i = 3; i < count; i++)
                rng.DecryptWord(asUint, i);
        }

        private XorShift GetEncryptionRng(uint seed)
        {
            var xs = GetXorshiftSeed(seed);
            return new XorShift(xs);
        }

        /// <summary>
        /// The <see cref="seed"/> is interpreted as u8 indexes in the <see cref="_seedTable"/> to build the actual <see cref="XorShift"/> seed.
        /// </summary>
        private uint GetXorshiftSeed(uint seed)
        {
            var key = 0u;
            for (int i = 0; i < 4; ++i)
            {
                var index = (seed >> (i * 8)) & 0x7F;
                key |= (uint)_seedTable[index] << (i * 8);
            }
            return key;
        }

        private static DrpFileEntry GetFile(ReadOnlySpan<byte> file)
        {
            var header = new DrpFileHeader(file);
            // Check the block magic.
            if (header.Magic != FileBlockMagic)
                throw new ArgumentException("Invalid File Block Magic?");

            // Get the file name length.
            var name = file[0x18..];
            var fileNameLength = name.IndexOf((byte)0);

            // Get the file name.
            var fileName = Encoding.ASCII.GetString(name[..fileNameLength]);

            // Extract the compressed data.
            var chunkSize = header.SizeTotal;
            var compressedSize = header.CompressedSize;
            var decompressedSize = header.DecompressedSize;

            var compressedFileOffset = (0x18 + fileNameLength + 4) & ~3;
            if (((compressedFileOffset + compressedSize + 3) & ~3) > chunkSize)
                throw new ArgumentException($"Invalid chunk extents {compressedFileOffset:X} + {compressedSize:X} > {chunkSize:X}");

            var compression = BitConverter.ToUInt32(file[compressedFileOffset..]);
            if (compression == CompressedDataMagic)
            {
                // File is compressed. Skip the u32 Oodl header.
                var compressedData = file.Slice(compressedFileOffset + 4, compressedSize - 4);
                var data = ReadCompressed(compressedData, decompressedSize);
                return new DrpFileEntry(fileName, data, header) {Compressed = true};
            }
            else
            {
                // File is uncompressed.
                if (compressedSize != decompressedSize)
                    throw new ArgumentException("Invalid uncompressed file extents");

                var data = file.Slice(compressedFileOffset, compressedSize).ToArray();
                return new DrpFileEntry(fileName, data, header) {Compressed = false};
            }
        }

        /// <summary>
        /// Reads compressed data and decompresses it.
        /// </summary>
        /// <param name="region">Compressed data region</param>
        /// <param name="decompressedSize">Length of data once decompressed.</param>
        /// <returns>Decompressed data</returns>
        private static byte[] ReadCompressed(ReadOnlySpan<byte> region, int decompressedSize)
        {
            // Decompress the file.
            var decompressed = Oodle.Decompress(region, decompressedSize);
            if (decompressed == null)
                throw new ArgumentException("Failed to decompress file contents.");
            return decompressed;
        }
    }
}
