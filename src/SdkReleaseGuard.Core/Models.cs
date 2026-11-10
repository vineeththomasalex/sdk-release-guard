using System.Collections.Immutable;

namespace SdkReleaseGuard.Core;

public enum Severity { Breaking, Warning, Info }
public enum FailOn { Breaking, Warning, None }

public sealed record ExportSymbol(uint Ordinal, ImmutableArray<string> Names, string? Forwarder = null);

public sealed record ImportSymbol(string? Name, ushort? Ordinal)
{
    public string Identity => Name is not null ? $"name:{Name}" : $"ordinal:{Ordinal}";
}

public sealed record ImportModule(string Name, bool DelayLoaded, ImmutableArray<ImportSymbol> Symbols);

public sealed record BinarySnapshot(
    string FileName,
    string Sha256,
    string Architecture,
    string Format,
    ImmutableArray<ExportSymbol> Exports,
    ImmutableArray<ImportModule> Imports);

public sealed record Finding(string RuleId, Severity Severity, string Subject, string Message);
public sealed record FindingSummary(int Breaking, int Warnings, int Informational);

public sealed record ComparisonReport(
    string SchemaVersion,
    BinarySnapshot Baseline,
    BinarySnapshot Candidate,
    FailOn FailOn,
    bool Passed,
    FindingSummary Summary,
    ImmutableArray<Finding> Findings);

public sealed class AnalysisException(string message, Exception? inner = null) : Exception(message, inner);
