using System.Globalization;
using System.Xml.Linq;

namespace RoslynMCP.Services.Testing;

/// <summary>One test's outcome from a VSTest run.</summary>
public sealed record TestResult(
    string FullyQualifiedName,
    string Outcome,
    double DurationMs,
    string? ErrorMessage,
    string? StackTrace,
    string? StandardOutput)
{
    public bool Passed => Outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase);
    public bool Failed => Outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads a VSTest .trx file into structured results. Both surfaces need this: the MCP tool
/// renders markdown from it, and the Test Explorer maps it onto test items.
/// </summary>
public static class TrxParser
{
    private static readonly XNamespace s_ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static IReadOnlyList<TestResult> Parse(string trxPath)
    {
        if (!File.Exists(trxPath))
            return [];

        try
        {
            var document = XDocument.Load(trxPath);
            return document.Descendants(s_ns + "UnitTestResult")
                .Select(ParseResult)
                .ToList();
        }
        catch (Exception)
        {
            // A truncated TRX (killed run, disk full) yields no results rather than an error:
            // the caller already knows the run did not finish cleanly from the exit code.
            return [];
        }
    }

    /// <summary>
    /// TRX carries the display name, which for a data-driven test includes its arguments
    /// (<c>Test(x: 1)</c>). Results are matched against discovery by fully-qualified name, so
    /// the argument suffix has to come off — otherwise every theory case looks unmatched.
    /// </summary>
    public static string NormalizeTestName(string testName)
    {
        int parenthesis = testName.IndexOf('(');
        return parenthesis < 0 ? testName : testName[..parenthesis];
    }

    private static TestResult ParseResult(XElement result)
    {
        var errorInfo = result.Element(s_ns + "Output")?.Element(s_ns + "ErrorInfo");

        return new TestResult(
            FullyQualifiedName: NormalizeTestName(result.Attribute("testName")?.Value ?? ""),
            Outcome: result.Attribute("outcome")?.Value ?? "None",
            DurationMs: ParseDuration(result.Attribute("duration")?.Value),
            ErrorMessage: errorInfo?.Element(s_ns + "Message")?.Value?.Trim(),
            StackTrace: errorInfo?.Element(s_ns + "StackTrace")?.Value?.Trim(),
            StandardOutput: result.Element(s_ns + "Output")?.Element(s_ns + "StdOut")?.Value?.Trim());
    }

    private static double ParseDuration(string? duration) =>
        TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.TotalMilliseconds
            : 0;
}
