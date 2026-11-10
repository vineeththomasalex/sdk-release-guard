using System.Collections.Immutable;
using SdkReleaseGuard.Core;

namespace SdkReleaseGuard.Tests;

public sealed class ReleaseComparerTests
{
    private static BinarySnapshot Image(ExportSymbol[]? exports = null, ImportModule[]? imports = null) =>
        new("sdk.dll", new string('a', 64), "x64", "PE32+", (exports ?? []).ToImmutableArray(), (imports ?? []).ToImmutableArray());

    [Fact]
    public void RemovalBreaksBothNamedAndOrdinalConsumers()
    {
        var report = ReleaseComparer.Compare(Image([new(1, ["Add"])]), Image());
        Assert.False(report.Passed);
        Assert.Equal(["SRG101", "SRG103"], report.Findings.Select(finding => finding.RuleId));
        Assert.Equal(2, report.Summary.Breaking);
    }

    [Fact]
    public void DetectsOrdinalSwapsEvenWhenNoNamesDisappear()
    {
        var report = ReleaseComparer.Compare(Image([new(1, ["Add"]), new(2, ["Subtract"])]),
            Image([new(2, ["Add"]), new(1, ["Subtract"])]));
        Assert.Equal(2, report.Findings.Length);
        Assert.All(report.Findings, finding => Assert.Equal("SRG102", finding.RuleId));
        Assert.False(report.Passed);
    }

    [Fact]
    public void AliasesAreIndependentNamesAndDoNotRemoveTheirSharedOrdinal()
    {
        var report = ReleaseComparer.Compare(Image([new(1, ["Add", "Sum"])]), Image([new(1, ["Add"])]));
        Assert.Equal("SRG101", Assert.Single(report.Findings).RuleId);
        var addition = ReleaseComparer.Compare(Image([new(1, ["Add"])]), Image([new(1, ["Add", "Sum"])]));
        Assert.Equal("SRG105", Assert.Single(addition.Findings).RuleId);
        Assert.True(addition.Passed);
    }

    [Fact]
    public void DetectsOrdinalOnlyRemovalAndDoesNotCompareCodeAddresses()
    {
        var report = ReleaseComparer.Compare(Image([new(20, [])]), Image());
        Assert.Equal("SRG103", Assert.Single(report.Findings).RuleId);
    }

    [Theory]
    [InlineData("helper.Api", "HELPER.Api", 0)]
    [InlineData("helper.Api", "helper.api", 1)]
    [InlineData("helper.#1", "helper.#2", 1)]
    [InlineData(null, "helper.Api", 1)]
    [InlineData("helper.Api", null, 1)]
    public void ComparesForwarderModulesCaseInsensitivelyButTargetsExactly(string? before, string? after, int count)
    {
        var report = ReleaseComparer.Compare(Image([new(1, ["Api"], before)]), Image([new(1, ["Api"], after)]));
        Assert.Equal(count, report.Findings.Length);
        Assert.All(report.Findings, finding => Assert.Equal("SRG104", finding.RuleId));
    }

    [Fact]
    public void DetectsUnnamedForwarderChanges()
    {
        var report = ReleaseComparer.Compare(Image([new(1, [], "helper.#1")]), Image([new(1, [], "helper.#2")]));
        Assert.Equal("SRG104", Assert.Single(report.Findings).RuleId);
    }

    [Fact]
    public void SymbolNamesAreCaseSensitive()
    {
        var report = ReleaseComparer.Compare(Image([new(1, ["Add"])]), Image([new(1, ["add"])]));
        Assert.Contains(report.Findings, finding => finding.RuleId == "SRG101");
    }

    [Fact]
    public void DetectsArchitectureAndBitnessChanges()
    {
        var report = ReleaseComparer.Compare(Image(), Image() with { Architecture = "x86", Format = "PE32" });
        Assert.Equal(["SRG001", "SRG002"], report.Findings.Select(finding => finding.RuleId));
    }

    [Fact]
    public void NewImportsFromExistingModulesAreNotHiddenByModuleMatching()
    {
        var before = Image(imports: [new("HELPER.dll", false, [new("Existing", null)])]);
        var after = Image(imports: [new("helper.DLL", false, [new("Existing", null), new("New", null), new(null, 7)])]);
        var report = ReleaseComparer.Compare(before, after);
        Assert.Equal(2, report.Findings.Length);
        Assert.All(report.Findings, finding => Assert.Equal("SRG202", finding.RuleId));
    }

    [Fact]
    public void NamedHashNumberAndOrdinalAreNotConflated()
    {
        var report = ReleaseComparer.Compare(Image(imports: [new("helper.dll", false, [new("#7", null)])]),
            Image(imports: [new("helper.dll", false, [new(null, 7)])]));
        Assert.Equal("SRG202", Assert.Single(report.Findings).RuleId);
    }

    [Fact]
    public void DelayToEagerIsDistinguishedFromANewModule()
    {
        var report = ReleaseComparer.Compare(Image(imports: [new("helper.dll", true, [new("Api", null)])]),
            Image(imports: [new("helper.dll", false, [new("Api", null)])]));
        Assert.Contains(report.Findings, finding => finding.RuleId == "SRG203");
        Assert.Contains(report.Findings, finding => finding.RuleId == "SRG204");
        Assert.DoesNotContain(report.Findings, finding => finding.RuleId == "SRG201");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportsNewDependenciesOnceRatherThanOncePerSymbol(bool delay)
    {
        var report = ReleaseComparer.Compare(Image(), Image(imports: [new("helper.dll", delay, [new("Api", null), new("Other", null)])]));
        Assert.Equal("SRG201", Assert.Single(report.Findings).RuleId);
    }

    [Theory]
    [InlineData(FailOn.Breaking, true)]
    [InlineData(FailOn.Warning, false)]
    [InlineData(FailOn.None, true)]
    public void WarningThresholdsHaveExplicitSemantics(FailOn threshold, bool passed)
    {
        var report = ReleaseComparer.Compare(Image(), Image(imports: [new("helper.dll", false, [new("Api", null)])]), threshold);
        Assert.Equal(passed, report.Passed);
        Assert.Equal(1, report.Summary.Warnings);
    }

    [Fact]
    public void AdvisoryModeStillReportsBreakingFindings()
    {
        var report = ReleaseComparer.Compare(Image([new(1, ["Add"])]), Image(), FailOn.None);
        Assert.True(report.Passed);
        Assert.Equal(2, report.Summary.Breaking);
    }

    [Fact]
    public void FindingOrderIsIndependentOfSnapshotOrdering()
    {
        var first = ReleaseComparer.Compare(Image([new(3, ["Z"]), new(1, ["A"])]), Image());
        var second = ReleaseComparer.Compare(Image([new(1, ["A"]), new(3, ["Z"])]), Image());
        Assert.Equal(first.Findings.ToArray(), second.Findings.ToArray());
    }
}
