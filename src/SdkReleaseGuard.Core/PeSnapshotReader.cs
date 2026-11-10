using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace SdkReleaseGuard.Core;

public static class PeSnapshotReader
{
    public const int MaximumFileSize = 128 * 1024 * 1024;

    public static BinarySnapshot ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileSize)
            throw new AnalysisException("The image exceeds the 128 MiB analysis limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return Read(bytes, Path.GetFileName(path));
    }

    public static BinarySnapshot Read(byte[] bytes, string fileName)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (bytes.Length > MaximumFileSize)
            throw new AnalysisException("The image exceeds the 128 MiB analysis limit.");
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new PEReader(stream);
            var image = new Image(bytes, reader.PEHeaders);
            return image.Snapshot(Path.GetFileName(fileName));
        }
        catch (BadImageFormatException ex)
        {
            throw new AnalysisException($"Invalid PE image: {ex.Message}", ex);
        }
        catch (OverflowException ex)
        {
            throw new AnalysisException("Invalid PE image: an address or table size overflowed.", ex);
        }
    }

    private sealed class Image
    {
        private readonly byte[] bytes;
        private readonly PEHeaders headers;
        private readonly PEHeader header;
        private int remainingEntries = 100_000;
        private int remainingStringBytes = 16 * 1024 * 1024;
        private readonly string architecture;

        public Image(byte[] bytes, PEHeaders headers)
        {
            this.bytes = bytes;
            this.headers = headers;
            header = headers.PEHeader ?? throw Error("COFF objects are not supported; provide a native DLL.");
            if (header.Magic is not (PEMagic.PE32 or PEMagic.PE32Plus))
                throw Error("Only PE32 and PE32+ images are supported.");
            var managedDirectory = GetDirectory(14);
            if (managedDirectory.RelativeVirtualAddress != 0 || managedDirectory.Size != 0)
                throw Error("Managed and mixed-mode assemblies are not supported; this tool checks native DLLs.");
            if (!headers.CoffHeader.Characteristics.HasFlag(Characteristics.Dll) ||
                !headers.CoffHeader.Characteristics.HasFlag(Characteristics.ExecutableImage))
                throw Error("The input must be an executable-image DLL, not an EXE or object file.");
            architecture = headers.CoffHeader.Machine switch
            {
                Machine.I386 when header.Magic == PEMagic.PE32 => "x86",
                Machine.Amd64 when header.Magic == PEMagic.PE32Plus => "x64",
                Machine.Arm64 when header.Magic == PEMagic.PE32Plus => "arm64",
                _ => throw Error("Unsupported machine/format combination. v1 supports x86, x64, and ARM64 native DLLs, not hybrid ARM64EC/ARM64X images.")
            };
            ValidateLayout();
        }

        public BinarySnapshot Snapshot(string fileName)
        {
            var exports = ReadExports();
            var imports = new List<ImportModule>();
            ReadImports(GetDirectory(1), delay: false, imports);
            ReadImports(GetDirectory(13), delay: true, imports);
            var merged = imports.GroupBy(module => (module.Name, module.DelayLoaded))
                .OrderBy(group => group.Key.Name, StringComparer.Ordinal).ThenBy(group => group.Key.DelayLoaded)
                .Select(group => new ImportModule(group.Key.Name, group.Key.DelayLoaded,
                    group.SelectMany(module => module.Symbols).Distinct()
                        .OrderBy(symbol => symbol.Identity, StringComparer.Ordinal).ToImmutableArray()))
                .ToImmutableArray();
            return new(fileName, Convert.ToHexStringLower(SHA256.HashData(bytes)), architecture,
                header.Magic == PEMagic.PE32 ? "PE32" : "PE32+", exports, merged);
        }

        private void ValidateLayout()
        {
            int fixedHeaderSize = header.Magic == PEMagic.PE32 ? 96 : 112;
            if (header.NumberOfRvaAndSizes < 0 ||
                fixedHeaderSize + (long)header.NumberOfRvaAndSizes * 8 > headers.CoffHeader.SizeOfOptionalHeader)
                throw Error("The declared data directories do not fit in the optional header.");
            if (headers.SectionHeaders.Length is < 1 or > 96)
                throw Error("The section count must be between 1 and 96.");
            int minimumHeaders = checked(headers.PEHeaderStartOffset + headers.CoffHeader.SizeOfOptionalHeader +
                headers.SectionHeaders.Length * 40);
            if (header.SizeOfHeaders < minimumHeaders || header.SizeOfHeaders > bytes.Length || header.SizeOfImage <= 0)
                throw Error("Invalid image/header size.");

            var virtualRanges = new List<(long Start, long End)>();
            var rawRanges = new List<(long Start, long End)>();
            foreach (var section in headers.SectionHeaders)
            {
                if (section.VirtualAddress < header.SizeOfHeaders || section.VirtualSize < 0 ||
                    section.SizeOfRawData < 0 || section.PointerToRawData < 0)
                    throw Error("Invalid section address or size.");
                long end = (long)section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData);
                if (end > header.SizeOfImage)
                    throw Error("A section extends beyond SizeOfImage.");
                if (end > section.VirtualAddress)
                    virtualRanges.Add((section.VirtualAddress, end));
                if (section.SizeOfRawData == 0)
                    continue;
                long rawEnd = (long)section.PointerToRawData + section.SizeOfRawData;
                if (section.PointerToRawData < header.SizeOfHeaders || rawEnd > bytes.Length)
                    throw Error("A section's raw data is truncated or overlaps the headers.");
                rawRanges.Add((section.PointerToRawData, rawEnd));
            }
            foreach (var ranges in new[] { virtualRanges, rawRanges })
            {
                var sorted = ranges.OrderBy(range => range.Start).ToArray();
                for (int i = 1; i < sorted.Length; i++)
                    if (sorted[i].Start < sorted[i - 1].End)
                        throw Error("Overlapping sections are ambiguous and are not supported.");
            }
        }

        private ImmutableArray<ExportSymbol> ReadExports()
        {
            var exportDirectory = GetDirectory(0);
            var directory = Directory(exportDirectory, 40);
            if (directory.IsEmpty)
                return [];
            uint baseOrdinal = U32(directory, 16);
            int addressCount = Count(U32(directory, 20));
            int nameCount = Count(U32(directory, 24));
            var addresses = Table(U32(directory, 28), addressCount, 4);
            var namePointers = Table(U32(directory, 32), nameCount, 4);
            var ordinals = Table(U32(directory, 36), nameCount, 2);
            var names = new Dictionary<int, List<string>>();
            string? previousName = null;
            for (int i = 0; i < nameCount; i++)
            {
                int index = U16(ordinals, i * 2);
                if (index >= addressCount || U32(addresses, index * 4) == 0)
                    throw Error("An export name refers to an absent export address.");
                string name = String(U32(namePointers, i * 4));
                if (previousName is not null && StringComparer.Ordinal.Compare(previousName, name) >= 0)
                    throw Error("Export names must be unique and sorted in lexical order.");
                previousName = name;
                if (!names.TryGetValue(index, out var aliases))
                    names[index] = aliases = [];
                aliases.Add(name);
            }
            var result = ImmutableArray.CreateBuilder<ExportSymbol>();
            uint start = checked((uint)exportDirectory.RelativeVirtualAddress);
            uint end = checked(start + (uint)exportDirectory.Size);
            for (int i = 0; i < addressCount; i++)
            {
                uint rva = U32(addresses, i * 4);
                if (rva == 0)
                    continue;
                string? forwarder = null;
                if (rva >= start && rva < end)
                {
                    forwarder = String(rva, checked((int)(end - rva)));
                    int dot = forwarder.LastIndexOf('.');
                    if (dot <= 0 || dot == forwarder.Length - 1)
                        throw Error("An export forwarder must identify a module and symbol.");
                    if (forwarder[dot + 1] == '#' &&
                        (!ushort.TryParse(forwarder.AsSpan(dot + 2), NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                        throw Error("An ordinal forwarder has an invalid ordinal.");
                }
                else if (!IsMapped(rva))
                    throw Error("An export points outside the mapped image.");
                result.Add(new(checked(baseOrdinal + (uint)i),
                    names.TryGetValue(i, out var aliases) ? aliases.ToImmutableArray() : [], forwarder));
            }
            return result.ToImmutable();
        }

        private void ReadImports(DirectoryEntry entry, bool delay, List<ImportModule> result)
        {
            int descriptorSize = delay ? 32 : 20;
            var directory = Directory(entry, descriptorSize);
            if (directory.IsEmpty)
                return;
            for (int offset = 0; offset <= directory.Length - descriptorSize; offset += descriptorSize)
            {
                var descriptor = directory.Slice(offset, descriptorSize);
                if (descriptor.IndexOfAnyExcept((byte)0) == -1)
                    return;
                Count(1);
                bool va = delay && (U32(descriptor, 0) & 1) == 0;
                if (delay && U32(descriptor, 0) > 1)
                    throw Error("Unsupported delay-import descriptor attributes.");
                uint nameRva = Address(U32(descriptor, delay ? 4 : 12), va);
                uint lookup = Address(U32(descriptor, delay ? 16 : 0), va);
                uint iat = Address(U32(descriptor, delay ? 12 : 16), va);
                if (nameRva == 0 || iat == 0)
                    throw Error("An import descriptor has no module name or address table.");
                if (lookup == 0)
                {
                    if (delay || U32(descriptor, 4) != 0 ||
                        GetDirectory(11).RelativeVirtualAddress != 0)
                        throw Error("Import names cannot be recovered from a bound or delay import without a lookup table.");
                    lookup = iat;
                }
                string name = String(nameRva).ToLowerInvariant();
                var symbols = ReadThunks(lookup, va);
                // The IAT must have space for the same entries and the terminating slot.
                Table(iat, checked(symbols.Length + 1), header.Magic == PEMagic.PE32 ? 4 : 8);
                result.Add(new(name, delay, symbols));
            }
            throw Error("The import directory has no null descriptor within its declared size.");
        }

        private ImmutableArray<ImportSymbol> ReadThunks(uint rva, bool va)
        {
            int width = header.Magic == PEMagic.PE32 ? 4 : 8;
            ulong ordinalFlag = width == 4 ? 0x80000000UL : 0x8000000000000000UL;
            var table = Rva(rva);
            var symbols = ImmutableArray.CreateBuilder<ImportSymbol>();
            for (int offset = 0; offset <= table.Length - width; offset += width)
            {
                ulong value = width == 4 ? U32(table, offset) : BinaryPrimitives.ReadUInt64LittleEndian(table[offset..]);
                if (value == 0)
                    return symbols.ToImmutable();
                Count(1);
                if ((value & ordinalFlag) != 0)
                {
                    if ((value & ~(ordinalFlag | 0xffffUL)) != 0)
                        throw Error("An ordinal import has nonzero reserved bits.");
                    symbols.Add(new(null, (ushort)(value & 0xffff)));
                }
                else
                {
                    if (value > (va ? uint.MaxValue : int.MaxValue))
                        throw Error("An import name address is invalid.");
                    uint nameRva = Address((uint)value, va);
                    Table(nameRva, 1, 2);
                    symbols.Add(new(String(checked(nameRva + 2)), null));
                }
            }
            throw Error("An import lookup table is unterminated or truncated.");
        }

        private uint Address(uint value, bool va)
        {
            if (value == 0 || !va)
                return value;
            if (value < header.ImageBase)
                throw Error("A legacy delay-import VA is below the image base.");
            return checked((uint)(value - header.ImageBase));
        }

        private ReadOnlySpan<byte> Directory(DirectoryEntry entry, int minimum)
        {
            if (entry.RelativeVirtualAddress == 0 && entry.Size == 0)
                return [];
            if (entry.RelativeVirtualAddress <= 0 || entry.Size < minimum)
                throw Error("An analysis directory has an invalid RVA or size.");
            return Table((uint)entry.RelativeVirtualAddress, entry.Size, 1);
        }

        private DirectoryEntry GetDirectory(int index) => index >= header.NumberOfRvaAndSizes ? default : index switch
        {
            0 => header.ExportTableDirectory,
            1 => header.ImportTableDirectory,
            11 => header.BoundImportTableDirectory,
            13 => header.DelayImportTableDirectory,
            14 => header.CorHeaderTableDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        private ReadOnlySpan<byte> Table(uint rva, int count, int width)
        {
            if (count == 0)
                return [];
            int size = checked(count * width);
            var data = Rva(rva);
            if (data.Length < size)
                throw Error("A table extends beyond its file-backed section.");
            return data[..size];
        }

        private ReadOnlySpan<byte> Rva(uint rva)
        {
            if (rva == 0)
                throw Error("A required RVA is zero.");
            if (rva < header.SizeOfHeaders)
                return bytes.AsSpan((int)rva, header.SizeOfHeaders - (int)rva);
            foreach (var section in headers.SectionHeaders)
            {
                long delta = (long)rva - section.VirtualAddress;
                if (delta >= 0 && delta < section.SizeOfRawData)
                    return bytes.AsSpan(checked(section.PointerToRawData + (int)delta), section.SizeOfRawData - (int)delta);
            }
            throw Error($"RVA 0x{rva:x} is not backed by file bytes.");
        }

        private bool IsMapped(uint rva) => rva < header.SizeOfHeaders ||
            headers.SectionHeaders.Any(section => rva >= section.VirtualAddress &&
                (long)rva - section.VirtualAddress < Math.Max(section.VirtualSize, section.SizeOfRawData));

        private string String(uint rva, int maximum = int.MaxValue)
        {
            var data = Rva(rva);
            data = data[..Math.Min(data.Length, Math.Min(maximum, 4097))];
            int length = data.IndexOf((byte)0);
            if (length <= 0 || length > 4096)
                throw Error("A required name is empty, unterminated, or exceeds 4096 bytes.");
            remainingStringBytes -= length;
            if (remainingStringBytes < 0)
                throw Error("The image exceeds the 16 MiB decoded-name budget.");
            foreach (byte value in data[..length])
                if (value < 0x20 || value > 0x7e)
                    throw Error("Only printable ASCII names are supported.");
            return Encoding.ASCII.GetString(data[..length]);
        }

        private int Count(uint count)
        {
            if (count > remainingEntries)
                throw Error("The image exceeds the 100,000 table-entry analysis budget.");
            remainingEntries -= (int)count;
            return (int)count;
        }

        private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        private static AnalysisException Error(string message) => new(message);
    }
}
