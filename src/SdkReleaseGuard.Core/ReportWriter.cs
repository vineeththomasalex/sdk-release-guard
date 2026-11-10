using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SdkReleaseGuard.Core;

public static class ReportWriter
{
    public const string ProjectUrl = "https://github.com/vineeththomasalex/sdk-release-guard";
    public static string Version => typeof(ReportWriter).Assembly.GetName().Version!.ToString(3);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Json(ComparisonReport report) => JsonSerializer.Serialize(report, JsonOptions) + "\n";

    public static string Text(ComparisonReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"SDK Release Guard {Version}");
        text.AppendLine($"Baseline:  {Quote(report.Baseline.FileName)} (SHA-256 {report.Baseline.Sha256})");
        text.AppendLine($"Candidate: {Quote(report.Candidate.FileName)} (SHA-256 {report.Candidate.Sha256})");
        text.AppendLine($"{report.Baseline.Architecture}/{report.Baseline.Format} -> {report.Candidate.Architecture}/{report.Candidate.Format}");
        text.AppendLine();
        foreach (var finding in report.Findings)
        {
            text.AppendLine($"[{finding.Severity.ToString().ToUpperInvariant()}] {finding.RuleId}: {finding.Message}");
            text.AppendLine($"  Action: {Rules.Get(finding.RuleId).Remediation}");
        }
        if (report.Findings.IsEmpty)
            text.AppendLine("No changes detected in the checked release surface.");
        text.AppendLine();
        text.AppendLine($"{report.Summary.Breaking} breaking, {report.Summary.Warnings} warning, {report.Summary.Informational} informational");
        text.AppendLine($"Gate: {(report.Passed ? "PASS" : "FAIL")} (fail-on: {report.FailOn.ToString().ToLowerInvariant()})");
        text.AppendLine("A passing gate is not proof of ABI or behavioral compatibility.");
        return text.ToString();
    }

    public static string Sarif(ComparisonReport report)
    {
        var rules = Rules.All.ToArray();
        string candidateUri = Uri.EscapeDataString(report.Candidate.FileName);
        var document = new
        {
            schema = "https://json.schemastore.org/sarif-2.1.0.json",
            version = "2.1.0",
            runs = new[]
            {
                new
                {
                    tool = new
                    {
                        driver = new
                        {
                            name = "SDK Release Guard",
                            semanticVersion = Version,
                            informationUri = ProjectUrl,
                            rules = rules.Select(rule => new
                            {
                                id = rule.Id,
                                name = rule.Name,
                                shortDescription = new { text = rule.Description },
                                fullDescription = new { text = rule.Description },
                                helpUri = $"{ProjectUrl}/blob/main/docs/rules.md#{rule.Id.ToLowerInvariant()}",
                                help = new { text = rule.Remediation },
                                defaultConfiguration = new { level = Level(rule.Severity) },
                                properties = new { tags = new[] { "compatibility", "release" } }
                            }).ToArray()
                        }
                    },
                    artifacts = new[]
                    {
                        new
                        {
                            location = new { uri = candidateUri },
                            hashes = new Dictionary<string, string> { ["sha-256"] = report.Candidate.Sha256 }
                        }
                    },
                    invocations = new[] { new { executionSuccessful = true } },
                    results = report.Findings.Select(finding => new
                    {
                        ruleId = finding.RuleId,
                        ruleIndex = Array.FindIndex(rules, rule => rule.Id == finding.RuleId),
                        level = Level(finding.Severity),
                        message = new { text = finding.Message },
                        locations = new[]
                        {
                            new
                            {
                                physicalLocation = new { artifactLocation = new { uri = candidateUri, index = 0 } },
                                logicalLocations = new[] { new { fullyQualifiedName = finding.Subject } }
                            }
                        },
                        partialFingerprints = new Dictionary<string, string>
                        {
                            ["releaseSurface/v1"] = Convert.ToHexStringLower(SHA256.HashData(
                                Encoding.UTF8.GetBytes($"{finding.RuleId}\n{finding.Subject}")))
                        }
                    }).ToArray(),
                    properties = new
                    {
                        report.SchemaVersion,
                        report.Passed,
                        report.FailOn,
                        report.Summary,
                        baselineSha256 = report.Baseline.Sha256,
                        candidateSha256 = report.Candidate.Sha256,
                        limitation = "Static native PE surface checks only; not proof of ABI or behavioral compatibility."
                    }
                }
            }
        };
        // The anonymous property above is renamed to the SARIF schema keyword.
        var node = JsonSerializer.SerializeToNode(document, JsonOptions)!.AsObject();
        node["$schema"] = node["schema"]!.DeepClone();
        node.Remove("schema");
        return node.ToJsonString(JsonOptions) + "\n";
    }

    private static string Level(Severity severity) => severity switch
    {
        Severity.Breaking => "error",
        Severity.Warning => "warning",
        _ => "note"
    };

    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
