namespace SdkReleaseGuard.Core;

public sealed record Rule(string Id, Severity Severity, string Name, string Description, string Remediation);

public static class Rules
{
    public static IReadOnlyList<Rule> All { get; } = Array.AsReadOnly<Rule>(
    [
        new("SRG001", Severity.Breaking, "ArchitectureChanged",
            "The target CPU architecture changed.",
            "Compare builds for the same target architecture; ship separate architecture-specific assets."),
        new("SRG002", Severity.Breaking, "ImageFormatChanged",
            "The image changed between PE32 and PE32+.",
            "Keep the process bitness contract or publish a separate target package."),
        new("SRG101", Severity.Breaking, "NamedExportRemoved",
            "A previously exported name no longer resolves.",
            "Restore the exact exported name or provide a compatible forwarding shim."),
        new("SRG102", Severity.Breaking, "NamedExportOrdinalChanged",
            "A named export has moved to another ordinal.",
            "Preserve explicit export ordinals for consumers that import by ordinal."),
        new("SRG103", Severity.Breaking, "ExportOrdinalRemoved",
            "A previously populated export ordinal is absent.",
            "Restore the ordinal, including NONAME exports, or deliberately version the binary contract."),
        new("SRG104", Severity.Warning, "ForwarderChanged",
            "An export's forwarding target or forwarding status changed.",
            "Verify that the destination exists on supported systems and preserves the old behavior."),
        new("SRG105", Severity.Info, "NamedExportAdded",
            "A new exported name is available.",
            "Document the addition; it does not prove that the rest of the binary remains compatible."),
        new("SRG106", Severity.Info, "ExportOrdinalAdded",
            "A new export ordinal is populated.",
            "Reserve the ordinal in future releases if consumers may use it."),
        new("SRG201", Severity.Warning, "DependencyAdded",
            "A new ordinary or delay-loaded import dependency was introduced.",
            "Check deployment and supported OS requirements; delay-loaded dependencies are needed when invoked."),
        new("SRG202", Severity.Warning, "ImportedSymbolAdded",
            "An existing dependency now requires an additional symbol.",
            "Verify that the new symbol exists in the oldest supported dependency version."),
        new("SRG203", Severity.Warning, "DelayImportMadeEager",
            "A dependency that was only delay-loaded is now loaded eagerly.",
            "Verify that the dependency is available at process load time on every supported deployment."),
        new("SRG204", Severity.Info, "DependencyRemoved",
            "An ordinary or delay-loaded import dependency was removed.",
            "Review packaging; this does not account for runtime LoadLibrary calls or forwarded exports.")
    ]);

    public static Rule Get(string id) => All.First(rule => rule.Id == id);
}
