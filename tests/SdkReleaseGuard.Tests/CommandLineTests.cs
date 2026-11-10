using System.Text.Json;
using SdkReleaseGuard.Cli;

namespace SdkReleaseGuard.Tests;

public sealed class CommandLineTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"srg-tests-{Guid.NewGuid():N}");
    private readonly string baseline;
    private readonly string candidate;

    public CommandLineTests()
    {
        Directory.CreateDirectory(directory);
        baseline = Path.Combine(directory, "old library.dll");
        candidate = Path.Combine(directory, "new library.dll");
        File.WriteAllBytes(baseline, new PeFixture().WithExports(new FixtureExport(1, ["Add"])).Bytes);
        File.WriteAllBytes(candidate, new PeFixture().Bytes);
    }

    private (int Code, string Out, string Error) Run(params string[] extras)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = CommandLine.Run(["compare", "-b", baseline, "-c", candidate, .. extras], output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void BreakingResultHasExitOneAndValidJsonWithoutDiagnostics()
    {
        var result = Run("--format", "json");
        Assert.Equal(1, result.Code);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Out);
        Assert.Equal("1.0", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.False(json.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("summary").GetProperty("breaking").GetInt32());
        Assert.DoesNotContain(directory, result.Out);
    }

    [Fact]
    public void SameInputPassesAndHelpIsUsable()
    {
        File.Copy(baseline, candidate, overwrite: true);
        var result = Run();
        Assert.Equal(0, result.Code);
        Assert.Contains("No changes detected", result.Out);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CommandLine.Run(["compare", "--help"], output, error));
        Assert.Contains("--baseline", output.ToString());
    }

    [Fact]
    public void InvalidInputFailsEvenInAdvisoryModeWithoutCreatingAReport()
    {
        File.WriteAllText(candidate, "not a DLL");
        string report = Path.Combine(directory, "report.json");
        var result = Run("--fail-on", "none", "--format", "json", "--output", report);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Out);
        Assert.NotEmpty(result.Error);
        Assert.False(File.Exists(report));
    }

    [Theory]
    [InlineData("--format", "nonsense")]
    [InlineData("--format", "99")]
    [InlineData("--fail-on", "99")]
    [InlineData("--unknown", "value")]
    public void InvalidOptionsReturnTwoWithoutReports(string option, string value)
    {
        var result = Run(option, value);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Out);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public void MissingRequiredArgumentsReturnTwo()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CommandLine.Run(["compare"], output, error));
        Assert.Empty(output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public void WritesAReportEvenWhenTheGateFailsButNeverOverwrites()
    {
        string report = Path.Combine(directory, "report.json");
        Assert.Equal(1, Run("--format", "json", "--output", report).Code);
        string first = File.ReadAllText(report);
        Assert.Equal(2, Run("--format", "json", "--output", report).Code);
        Assert.Equal(first, File.ReadAllText(report));
        Assert.Empty(Directory.GetFiles(directory, ".srg-*.tmp"));
        byte[] original = File.ReadAllBytes(baseline);
        Assert.Equal(2, Run("--output", baseline).Code);
        Assert.Equal(original, File.ReadAllBytes(baseline));
    }

    [Fact]
    public void MissingFilesAndOutputDirectoriesReturnTwo()
    {
        Assert.Equal(2, Run("--output", Path.Combine(directory, "missing", "report.json")).Code);
        File.Delete(candidate);
        Assert.Equal(2, Run().Code);
    }

    [Fact]
    public void SarifReferencesItsRulesAndCandidateArtifact()
    {
        var result = Run("--format", "sarif");
        Assert.Equal(1, result.Code);
        using var document = JsonDocument.Parse(result.Out);
        var root = document.RootElement;
        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        Assert.True(root.TryGetProperty("$schema", out _));
        var run = root.GetProperty("runs")[0];
        Assert.True(run.GetProperty("invocations")[0].GetProperty("executionSuccessful").GetBoolean());
        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules");
        foreach (var finding in run.GetProperty("results").EnumerateArray())
        {
            int index = finding.GetProperty("ruleIndex").GetInt32();
            Assert.Equal(rules[index].GetProperty("id").GetString(), finding.GetProperty("ruleId").GetString());
            Assert.Equal("error", finding.GetProperty("level").GetString());
            Assert.Equal("new%20library.dll", finding.GetProperty("locations")[0]
                .GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString());
        }
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
