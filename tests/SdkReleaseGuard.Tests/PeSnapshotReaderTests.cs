using SdkReleaseGuard.Core;

namespace SdkReleaseGuard.Tests;

public sealed class PeSnapshotReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsNamedAliasedOrdinalOnlyAndForwardedExports(bool is64)
    {
        var fixture = new PeFixture(is64).WithExports(
            new(7, ["Add", "Sum"]), new(9, []), new(10, ["Forward"], "helper.#5"));
        var snapshot = PeSnapshotReader.Read(fixture.Bytes, "sdk.dll");
        Assert.Equal(is64 ? "x64" : "x86", snapshot.Architecture);
        Assert.Equal(new uint[] { 7, 9, 10 }, snapshot.Exports.Select(export => export.Ordinal));
        Assert.Equal(["Add", "Sum"], snapshot.Exports[0].Names.ToArray());
        Assert.Empty(snapshot.Exports[1].Names);
        Assert.Equal("helper.#5", snapshot.Exports[2].Forwarder);
    }

    [Fact]
    public void ReadsArm64WithoutTreatingItAsX64()
    {
        var fixture = new PeFixture();
        fixture.Put16(0x84, 0xaa64);
        Assert.Equal("arm64", PeSnapshotReader.Read(fixture.Bytes, "sdk.dll").Architecture);
    }

    [Fact]
    public void RespectsDeclaredDirectoryCountInsteadOfReadingUnusedHeaderBytes()
    {
        var fixture = new PeFixture().WithExports(new FixtureExport(1, ["Add"]))
            .WithImports(new FixtureImport("helper.dll", false, [new("Api", null)]));
        fixture.Put32(fixture.Directories - 4, 1);
        var snapshot = PeSnapshotReader.Read(fixture.Bytes, "sdk.dll");
        Assert.Single(snapshot.Exports);
        Assert.Empty(snapshot.Imports);
        fixture.Put32(fixture.Directories - 4, 17);
        Assert.Throws<AnalysisException>(() => PeSnapshotReader.Read(fixture.Bytes, "sdk.dll"));
    }

    [Theory]
    [InlineData("helper.#+7")]
    [InlineData("helper.# 7")]
    [InlineData("helper.#65536")]
    public void RejectsNonCanonicalOrdinalForwarderSyntax(string forwarder)
    {
        var fixture = new PeFixture().WithExports(new FixtureExport(1, ["Api"], forwarder));
        Assert.Throws<AnalysisException>(() => PeSnapshotReader.Read(fixture.Bytes, "sdk.dll"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsEagerAndDelayImportsByNameAndOrdinal(bool is64)
    {
        var fixture = new PeFixture(is64).WithImports(
            new("HELPER.dll", false, [new("Api", null), new(null, 42)]),
            new("OPTIONAL.dll", true, [new("Later", null)]));
        var snapshot = PeSnapshotReader.Read(fixture.Bytes, "sdk.dll");
        Assert.Equal("helper.dll", snapshot.Imports[0].Name);
        Assert.Contains(new ImportSymbol(null, 42), snapshot.Imports[0].Symbols);
        Assert.Contains(new ImportSymbol("Api", null), snapshot.Imports[0].Symbols);
        Assert.True(snapshot.Imports[1].DelayLoaded);
        Assert.Equal("Later", Assert.Single(snapshot.Imports[1].Symbols).Name);
    }

    [Fact]
    public void HandlesLegacyVaBasedDelayDescriptorsAndThunks()
    {
        var fixture = new PeFixture(false).WithImports(new FixtureImport("helper.dll", true,
            [new("Legacy", null), new(null, 5)], Legacy: true));
        var snapshot = PeSnapshotReader.Read(fixture.Bytes, "sdk.dll");
        Assert.Contains(new ImportSymbol("Legacy", null), snapshot.Imports[0].Symbols);
        Assert.Contains(new ImportSymbol(null, 5), snapshot.Imports[0].Symbols);
    }

    [Fact]
    public void FallsBackToUnboundIatButRejectsBoundIatWithoutLookup()
    {
        var fixture = new PeFixture().WithImports(new FixtureImport("helper.dll", false, [new("Api", null)]));
        fixture.Put32(fixture.ImportDirectory, 0);
        Assert.Equal("Api", Assert.Single(Assert.Single(PeSnapshotReader.Read(fixture.Bytes, "sdk.dll").Imports).Symbols).Name);
        fixture.Put32(fixture.ImportDirectory + 4, 123);
        Assert.Throws<AnalysisException>(() => PeSnapshotReader.Read(fixture.Bytes, "sdk.dll"));
    }

    [Fact]
    public void MergesRepeatedImportDescriptorsWithoutLosingSymbols()
    {
        var fixture = new PeFixture().WithImports(
            new("HELPER.dll", false, [new("One", null)]),
            new("helper.DLL", false, [new("Two", null)]));
        var module = Assert.Single(PeSnapshotReader.Read(fixture.Bytes, "sdk.dll").Imports);
        Assert.Equal(2, module.Symbols.Length);
    }

    [Fact]
    public void CodeRelocationAndTimestampsDoNotCreateCompatibilityFindings()
    {
        var before = new PeFixture().WithExports(new FixtureExport(1, ["Add"]));
        var after = new PeFixture().WithExports(new FixtureExport(1, ["Add"]));
        after.Put32(after.ExportAddresses, 0x2f80);
        after.Put32(0x88, 123456);
        var report = ReleaseComparer.Compare(PeSnapshotReader.Read(before.Bytes, "a.dll"), PeSnapshotReader.Read(after.Bytes, "b.dll"));
        Assert.Empty(report.Findings);
        Assert.NotEqual(report.Baseline.Sha256, report.Candidate.Sha256);
    }

    [Fact]
    public void ExportedZeroFilledDataIsAllowedButMetadataInZeroFillIsRejected()
    {
        var fixture = new PeFixture().WithExports(new FixtureExport(1, ["Data"]));
        fixture.Put32(fixture.ExportAddresses, 0x3100);
        Assert.Single(PeSnapshotReader.Read(fixture.Bytes, "sdk.dll").Exports);
        fixture.Put32(fixture.ExportNames, 0x3100);
        Assert.Throws<AnalysisException>(() => PeSnapshotReader.Read(fixture.Bytes, "sdk.dll"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(511)]
    [InlineData(2048)]
    [InlineData(8703)]
    public void RejectsTruncatedImages(int length)
    {
        var fixture = new PeFixture().WithExports(new FixtureExport(1, ["Add"]));
        Assert.Throws<AnalysisException>(() => PeSnapshotReader.Read(fixture.Bytes[..length], "bad.dll"));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("name-index")]
    [InlineData("name-rva")]
    [InlineData("ordinal-overflow")]
    [InlineData("zero-address")]
    [InlineData("bad-forwarder")]
    [InlineData("unterminated-forwarder")]
    [InlineData("invalid-ascii")]
    [InlineData("directory-size")]
    [InlineData("raw-offset")]
    [InlineData("unsupported-architecture")]
    [InlineData("not-dll")]
    [InlineData("managed")]
    public void RejectsMalformedOrUnsupportedExportImages(string mutation)
    {
        var fixture = new PeFixture().WithExports(
            new(1, ["Add"]), new(2, ["Forward"], "helper.Api"));
        switch (mutation)
        {
            case "count": fixture.Put32(fixture.ExportDirectory + 20, uint.MaxValue); break;
            case "name-index": fixture.Put16(fixture.ExportOrdinals, 100); break;
            case "name-rva": fixture.Put32(fixture.ExportNames, 0xfffffff0); break;
            case "ordinal-overflow": fixture.Put32(fixture.ExportDirectory + 16, uint.MaxValue); break;
            case "zero-address": fixture.Put32(fixture.ExportAddresses, 0); break;
            case "bad-forwarder":
                int forwarder = checked((int)fixture.Get32(fixture.ExportAddresses + 4) - 0x1000 + 0x200);
                fixture.Bytes[forwarder + 6] = (byte)'X';
                break;
            case "unterminated-forwarder":
                fixture.Put32(fixture.ExportAddresses, fixture.Get32(fixture.Directories) + fixture.Get32(fixture.Directories + 4) - 1);
                fixture.Bytes[fixture.ExportDirectory + (int)fixture.Get32(fixture.Directories + 4) - 1] = (byte)'x';
                break;
            case "invalid-ascii":
                int name = checked((int)fixture.Get32(fixture.ExportNames) - 0x1000 + 0x200);
                fixture.Bytes[name] = 0x1b;
                break;
            case "directory-size": fixture.Put32(fixture.Directories + 4, 39); break;
            case "raw-offset": fixture.Put32(fixture.Section + 20, 100); break;
            case "unsupported-architecture": fixture.Put16(0x84, 0xa641); break;
            case "not-dll": fixture.Put16(0x96, 2); break;
            case "managed": fixture.SetDirectory(14, 0x200, 72); break;
        }
        Assert.Throws<AnalysisException>(() => PeSnapshotReader.Read(fixture.Bytes, "bad.dll"));
    }

    [Theory]
    [InlineData("no-descriptor-terminator")]
    [InlineData("no-thunk-terminator")]
    [InlineData("reserved-ordinal-bits")]
    [InlineData("name-rva-overflow")]
    [InlineData("missing-iat")]
    [InlineData("truncated-iat")]
    public void RejectsMalformedImports(string mutation)
    {
        var fixture = new PeFixture().WithImports(new FixtureImport("helper.dll", false, [new(null, 7)]));
        switch (mutation)
        {
            case "no-descriptor-terminator": fixture.Put32(fixture.Directories + 12, 20); break;
            case "no-thunk-terminator":
                fixture.Put32(fixture.ImportDirectory, PeFixture.Rva(fixture.Bytes.Length - 8));
                fixture.Put64(fixture.Bytes.Length - 8, 0x8000000000000007);
                break;
            case "reserved-ordinal-bits": fixture.Put64(fixture.FirstLookup, 0x8000000000010007); break;
            case "name-rva-overflow": fixture.Put64(fixture.FirstLookup, 0x100000000); break;
            case "missing-iat": fixture.Put32(fixture.ImportDirectory + 16, 0); break;
            case "truncated-iat": fixture.Put32(fixture.ImportDirectory + 16, PeFixture.Rva(fixture.Bytes.Length - 8)); break;
        }
        Assert.Throws<AnalysisException>(() => PeSnapshotReader.Read(fixture.Bytes, "bad.dll"));
    }

    [Fact]
    public void DeterministicMutationCampaignNeverLeaksAnUncheckedParserException()
    {
        var seed = new PeFixture().WithExports(new FixtureExport(1, ["Add"]))
            .WithImports(new FixtureImport("helper.dll", false, [new("Api", null)])).Bytes;
        var random = new Random(7102026);
        for (int i = 0; i < 1000; i++)
        {
            var bytes = (byte[])seed.Clone();
            for (int j = 0; j < 3; j++)
                bytes[random.Next(0, 1024)] ^= (byte)random.Next(1, 256);
            try
            {
                var snapshot = PeSnapshotReader.Read(bytes, "mutated.dll");
                Assert.Empty(ReleaseComparer.Compare(snapshot, snapshot).Findings);
            }
            catch (AnalysisException)
            {
                // Invalid images must be rejected, not reported as a successful empty comparison.
            }
        }
    }
}
