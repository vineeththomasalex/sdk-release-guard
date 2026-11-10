using System.CommandLine;
using System.Text;
using System.Text.Json;
using SdkReleaseGuard.Core;

namespace SdkReleaseGuard.Cli;

public enum ReportFormat { Text, Json, Sarif }

public static class CommandLine
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var baseline = new Option<string>("--baseline", "-b")
        {
            Description = "Previous native DLL (the contract to preserve).",
            Required = true
        };
        var candidate = new Option<string>("--candidate", "-c")
        {
            Description = "New native DLL to evaluate.",
            Required = true
        };
        var format = new Option<ReportFormat>("--format")
        {
            Description = "Report format: text, json, or sarif.",
            DefaultValueFactory = _ => ReportFormat.Text
        };
        var failOn = new Option<FailOn>("--fail-on")
        {
            Description = "Gate threshold: breaking (default), warning, or none.",
            DefaultValueFactory = _ => FailOn.Breaking
        };
        var destination = new Option<string?>("--output", "-o")
        {
            Description = "Write a new UTF-8 report file instead of stdout. Existing files are never overwritten."
        };
        var compare = new Command("compare", "Compare two native Windows DLLs without loading or executing them.")
        {
            baseline, candidate, format, failOn, destination
        };
        compare.SetAction(parse =>
        {
            try
            {
                var reportFormat = parse.GetValue(format);
                var threshold = parse.GetValue(failOn);
                if (!Enum.IsDefined(reportFormat) || !Enum.IsDefined(threshold))
                    throw new ArgumentException("Unknown report format or gate threshold.");
                var oldImage = PeSnapshotReader.ReadFile(parse.GetRequiredValue(baseline));
                var newImage = PeSnapshotReader.ReadFile(parse.GetRequiredValue(candidate));
                var report = ReleaseComparer.Compare(oldImage, newImage, threshold);
                string text = reportFormat switch
                {
                    ReportFormat.Json => ReportWriter.Json(report),
                    ReportFormat.Sarif => ReportWriter.Sarif(report),
                    _ => ReportWriter.Text(report)
                };
                string? path = parse.GetValue(destination);
                if (path is null)
                    output.Write(text);
                else
                    WriteReport(path, text);
                return report.Passed ? 0 : 1;
            }
            catch (Exception ex) when (ex is AnalysisException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error.WriteLine($"sdk-release-guard: {JsonSerializer.Serialize(ex.Message)}");
                return 2;
            }
        });
        var root = new RootCommand("Detect native DLL release risks. A passing gate is not proof of full ABI compatibility.")
        {
            compare
        };
        var result = root.Parse(args);
        if (result.Errors.Count > 0)
        {
            foreach (var parseError in result.Errors)
                error.WriteLine($"sdk-release-guard: {JsonSerializer.Serialize(parseError.Message)}");
            error.WriteLine("Run sdk-release-guard compare --help for usage.");
            return 2;
        }
        return result.Invoke(new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false });
    }

    private static void WriteReport(string path, string content)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        string temporary = Path.Combine(directory, $".srg-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
            }
            File.Move(temporary, fullPath, overwrite: false);
        }
        finally
        {
            if (created && File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
