using System.Buffers.Binary;
using System.Text;
using SdkReleaseGuard.Core;

namespace SdkReleaseGuard.Tests;

internal sealed record FixtureExport(uint Ordinal, string[] Names, string? Forwarder = null);
internal sealed record FixtureImport(string Module, bool Delay, ImportSymbol[] Symbols, bool Legacy = false);

internal sealed class PeFixture
{
    public byte[] Bytes { get; } = new byte[0x2200];
    public int OptionalHeader => 0x98;
    public int Directories => OptionalHeader + (is64 ? 112 : 96);
    public int Section => OptionalHeader + (is64 ? 240 : 224);
    public int ExportDirectory { get; private set; }
    public int ExportAddresses { get; private set; }
    public int ExportNames { get; private set; }
    public int ExportOrdinals { get; private set; }
    public int ImportDirectory { get; private set; }
    public int DelayDirectory { get; private set; }
    public int FirstLookup { get; private set; }
    private readonly bool is64;
    private int cursor = 0x200;
    private const uint ImageBase32 = 0x10000000;

    public PeFixture(bool is64 = true)
    {
        this.is64 = is64;
        Put16(0, 0x5a4d);
        Put32(0x3c, 0x80);
        Put32(0x80, 0x4550);
        Put16(0x84, is64 ? (ushort)0x8664 : (ushort)0x14c);
        Put16(0x86, 1);
        Put16(0x94, is64 ? (ushort)240 : (ushort)224);
        Put16(0x96, 0x2022);
        Put16(OptionalHeader, is64 ? (ushort)0x20b : (ushort)0x10b);
        if (is64)
            Put64(OptionalHeader + 24, 0x180000000);
        else
            Put32(OptionalHeader + 28, ImageBase32);
        Put32(OptionalHeader + 32, 0x1000);
        Put32(OptionalHeader + 36, 0x200);
        Put32(OptionalHeader + 56, 0x4000);
        Put32(OptionalHeader + 60, 0x200);
        Put16(OptionalHeader + 68, 3);
        Put32(Directories - 4, 16);
        Encoding.ASCII.GetBytes(".rdata").CopyTo(Bytes, Section);
        Put32(Section + 8, 0x2400);
        Put32(Section + 12, 0x1000);
        Put32(Section + 16, 0x2000);
        Put32(Section + 20, 0x200);
        Put32(Section + 36, 0x40000040);
    }

    public PeFixture WithExports(params FixtureExport[] exports)
    {
        ExportDirectory = Allocate(40);
        uint ordinalBase = exports.Length == 0 ? 1 : exports.Min(export => export.Ordinal);
        int addressCount = exports.Length == 0 ? 0 : checked((int)(exports.Max(export => export.Ordinal) - ordinalBase + 1));
        var names = exports.SelectMany(export => export.Names.Select(name => (name, export.Ordinal)))
            .OrderBy(pair => pair.name, StringComparer.Ordinal).ToArray();
        ExportAddresses = Allocate(addressCount * 4);
        ExportNames = Allocate(names.Length * 4);
        ExportOrdinals = Allocate(names.Length * 2);
        Put32(ExportDirectory + 12, Rva(Ascii("sdk.dll")));
        Put32(ExportDirectory + 16, ordinalBase);
        Put32(ExportDirectory + 20, (uint)addressCount);
        Put32(ExportDirectory + 24, (uint)names.Length);
        Put32(ExportDirectory + 28, Rva(ExportAddresses));
        Put32(ExportDirectory + 32, Rva(ExportNames));
        Put32(ExportDirectory + 36, Rva(ExportOrdinals));
        foreach (var export in exports)
            Put32(ExportAddresses + checked((int)(export.Ordinal - ordinalBase)) * 4,
                export.Forwarder is null ? 0x2f00U : Rva(Ascii(export.Forwarder)));
        for (int i = 0; i < names.Length; i++)
        {
            Put32(ExportNames + i * 4, Rva(Ascii(names[i].name)));
            Put16(ExportOrdinals + i * 2, checked((ushort)(names[i].Ordinal - ordinalBase)));
        }
        SetDirectory(0, ExportDirectory, cursor - ExportDirectory);
        return this;
    }

    public PeFixture WithImports(params FixtureImport[] imports)
    {
        foreach (bool delay in new[] { false, true })
        {
            var group = imports.Where(import => import.Delay == delay).ToArray();
            if (group.Length == 0)
                continue;
            int width = is64 ? 8 : 4;
            int size = delay ? 32 : 20;
            int start = Allocate((group.Length + 1) * size);
            if (delay) DelayDirectory = start; else ImportDirectory = start;
            SetDirectory(delay ? 13 : 1, start, (group.Length + 1) * size);
            for (int i = 0; i < group.Length; i++)
            {
                var import = group[i];
                int descriptor = start + i * size;
                uint Address(int offset) => Rva(offset) + (import.Legacy ? ImageBase32 : 0);
                int name = Ascii(import.Module);
                int lookup = Allocate((import.Symbols.Length + 1) * width);
                int iat = Allocate((import.Symbols.Length + 1) * width);
                if (FirstLookup == 0) FirstLookup = lookup;
                if (delay)
                {
                    Put32(descriptor, import.Legacy ? 0U : 1U);
                    Put32(descriptor + 4, Address(name));
                    Put32(descriptor + 12, Address(iat));
                    Put32(descriptor + 16, Address(lookup));
                }
                else
                {
                    Put32(descriptor, Address(lookup));
                    Put32(descriptor + 12, Address(name));
                    Put32(descriptor + 16, Address(iat));
                }
                for (int j = 0; j < import.Symbols.Length; j++)
                {
                    var symbol = import.Symbols[j];
                    ulong value;
                    if (symbol.Name is not null)
                    {
                        int hint = Allocate(2 + Encoding.ASCII.GetByteCount(symbol.Name) + 1);
                        Encoding.ASCII.GetBytes(symbol.Name).CopyTo(Bytes, hint + 2);
                        value = Address(hint);
                    }
                    else
                        value = (is64 ? 0x8000000000000000UL : 0x80000000UL) | symbol.Ordinal!.Value;
                    if (is64)
                    {
                        Put64(lookup + j * width, value);
                        Put64(iat + j * width, value);
                    }
                    else
                    {
                        Put32(lookup + j * width, (uint)value);
                        Put32(iat + j * width, (uint)value);
                    }
                }
            }
        }
        return this;
    }

    public void SetDirectory(int index, int offset, int size)
    {
        Put32(Directories + index * 8, Rva(offset));
        Put32(Directories + index * 8 + 4, (uint)size);
    }

    public static uint Rva(int offset) => checked((uint)(offset - 0x200 + 0x1000));
    public uint Get32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(offset));
    public void Put16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(offset), value);
    public void Put32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(offset), value);
    public void Put64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Bytes.AsSpan(offset), value);

    private int Allocate(int size)
    {
        cursor = (cursor + 7) & ~7;
        int result = cursor;
        cursor += size;
        if (cursor >= 0x1f00)
            throw new InvalidOperationException("Fixture metadata would overlap the test export implementation.");
        return result;
    }

    private int Ascii(string text)
    {
        int offset = Allocate(Encoding.ASCII.GetByteCount(text) + 1);
        Encoding.ASCII.GetBytes(text).CopyTo(Bytes, offset);
        return offset;
    }
}
