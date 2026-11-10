using System.Collections.Immutable;

namespace SdkReleaseGuard.Core;

public static class ReleaseComparer
{
    public static ComparisonReport Compare(BinarySnapshot baseline, BinarySnapshot candidate, FailOn failOn = FailOn.Breaking)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Enum.IsDefined(failOn))
            throw new ArgumentOutOfRangeException(nameof(failOn));

        var findings = new List<Finding>();
        void Add(string ruleId, string subject, string message) =>
            findings.Add(new(ruleId, Rules.Get(ruleId).Severity, subject, message));

        if (baseline.Architecture != candidate.Architecture)
            Add("SRG001", "architecture", $"Target changed from {baseline.Architecture} to {candidate.Architecture}.");
        if (baseline.Format != candidate.Format)
            Add("SRG002", "image-format", $"Image format changed from {baseline.Format} to {candidate.Format}.");

        var oldNames = NamedExports(baseline);
        var newNames = NamedExports(candidate);
        var oldOrdinals = baseline.Exports.ToDictionary(export => export.Ordinal);
        var newOrdinals = candidate.Exports.ToDictionary(export => export.Ordinal);

        foreach (var (name, oldExport) in oldNames)
        {
            if (!newNames.TryGetValue(name, out var newExport))
                Add("SRG101", $"export:{name}", $"Export {Quote(name)} was removed (previous ordinal {oldExport.Ordinal}).");
            else
            {
                if (oldExport.Ordinal != newExport.Ordinal)
                    Add("SRG102", $"export:{name}", $"Export {Quote(name)} moved from ordinal {oldExport.Ordinal} to {newExport.Ordinal}; ordinal consumers may bind incorrectly.");
                if (!SameForwarder(oldExport.Forwarder, newExport.Forwarder))
                    Add("SRG104", $"export:{name}", $"Export {Quote(name)} changed from {Target(oldExport.Forwarder)} to {Target(newExport.Forwarder)}.");
            }
        }

        foreach (var (ordinal, oldExport) in oldOrdinals)
        {
            if (!newOrdinals.TryGetValue(ordinal, out var newExport))
                Add("SRG103", $"ordinal:{ordinal}", $"Export ordinal {ordinal} was removed.");
            // Named consumers are covered above; unnamed ordinal consumers have no name to match.
            else if (oldExport.Names.IsEmpty && !SameForwarder(oldExport.Forwarder, newExport.Forwarder))
                Add("SRG104", $"ordinal:{ordinal}", $"Export ordinal {ordinal} changed from {Target(oldExport.Forwarder)} to {Target(newExport.Forwarder)}.");
        }

        foreach (var (name, export) in newNames)
            if (!oldNames.ContainsKey(name))
                Add("SRG105", $"export:{name}", $"Export {Quote(name)} was added at ordinal {export.Ordinal}.");
        foreach (var ordinal in newOrdinals.Keys)
            if (!oldOrdinals.ContainsKey(ordinal))
                Add("SRG106", $"ordinal:{ordinal}", $"Export ordinal {ordinal} was added.");

        var oldImports = Modules(baseline);
        var newImports = Modules(candidate);
        foreach (var (key, module) in newImports)
        {
            string subject = $"import:{key.Name}:{(key.Delay ? "delay" : "eager")}";
            if (!oldImports.TryGetValue(key, out var oldModule))
            {
                bool madeEager = !key.Delay && oldImports.ContainsKey((key.Name, true));
                Add(madeEager ? "SRG203" : "SRG201", subject,
                    madeEager
                        ? $"Dependency {Quote(module.Name)} now loads eagerly instead of only on demand."
                        : $"New {(module.DelayLoaded ? "delay-loaded" : "eager")} dependency {Quote(module.Name)}.");
                // A whole new module already implies all its symbols are new requirements.
                continue;
            }
            var oldSymbols = oldModule.Symbols.ToHashSet();
            foreach (var symbol in module.Symbols)
                if (!oldSymbols.Contains(symbol))
                    Add("SRG202", $"{subject}:{symbol.Identity}",
                        $"Dependency {Quote(module.Name)} now requires {Quote(symbol.Identity)} ({(module.DelayLoaded ? "delay-loaded" : "eager")}).");
        }
        foreach (var (key, module) in oldImports)
            if (!newImports.ContainsKey(key))
                Add("SRG204", $"import:{key.Name}:{(key.Delay ? "delay" : "eager")}",
                    $"Removed {(module.DelayLoaded ? "delay-loaded" : "eager")} dependency {Quote(module.Name)}.");

        var sorted = findings.OrderBy(finding => finding.Severity)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.Subject, StringComparer.Ordinal).ToImmutableArray();
        var summary = new FindingSummary(
            sorted.Count(f => f.Severity == Severity.Breaking),
            sorted.Count(f => f.Severity == Severity.Warning),
            sorted.Count(f => f.Severity == Severity.Info));
        bool passed = failOn == FailOn.None ||
            (summary.Breaking == 0 && (failOn == FailOn.Breaking || summary.Warnings == 0));
        return new("1.0", baseline, candidate, failOn, passed, summary, sorted);
    }

    private static Dictionary<string, ExportSymbol> NamedExports(BinarySnapshot snapshot) =>
        snapshot.Exports.SelectMany(export => export.Names.Select(name => (name, export)))
            .ToDictionary(pair => pair.name, pair => pair.export, StringComparer.Ordinal);

    private static Dictionary<(string Name, bool Delay), ImportModule> Modules(BinarySnapshot snapshot) =>
        snapshot.Imports.ToDictionary(module => (module.Name.ToLowerInvariant(), module.DelayLoaded));

    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);
    private static string Target(string? forwarder) => forwarder is null ? "a local implementation" : $"forwarder {Quote(forwarder)}";

    private static bool SameForwarder(string? left, string? right)
    {
        if (left is null || right is null)
            return left == right;
        static (string Module, string Symbol) Split(string value)
        {
            int dot = value.LastIndexOf('.');
            return (value[..dot].ToLowerInvariant(), value[(dot + 1)..]);
        }
        return Split(left) == Split(right);
    }
}
