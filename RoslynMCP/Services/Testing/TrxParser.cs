using System.Globalization;
using Microsoft.Language.Xml;

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
    public static IReadOnlyList<TestResult> Parse(string trxPath)
    {
        if (!File.Exists(trxPath))
            return [];

        try
        {
            var document = Parser.ParseText(File.ReadAllText(trxPath));
            var results = new List<TestResult>();

            foreach (var result in document.DescendantsByLocalName("UnitTestResult"))
            {
                // A result with no name is the tail of a truncated file: the parse is error
                // tolerant, so half of an element still comes back as an element, and what marks
                // it as the fragment it is is having nothing in it.
                if (ParseResult(result) is { FullyQualifiedName.Length: > 0 } parsed)
                    results.Add(parsed);
            }

            return results;
        }
        catch (IOException)
        {
            // A TRX being written, or one on a disk that filled up, yields no results rather than
            // an error: the caller already knows the run did not finish cleanly from the exit code.
            // Malformed content needs no catch — the parse is error-tolerant, and a truncated file
            // simply has fewer results in it.
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

    /// <remarks>
    /// By local name throughout. TRX binds the VSTest schema to the default namespace, but a file
    /// written through a tool that binds it to a prefix instead says the same thing, and matching
    /// on the local name reads both.
    /// </remarks>
    private static TestResult ParseResult(XmlElementBaseSyntax result)
    {
        var output = result.GetElementByLocalName("Output");
        var errorInfo = output?.GetElementByLocalName("ErrorInfo");

        return new TestResult(
            FullyQualifiedName: NormalizeTestName(result.GetAttributeValue("testName") ?? ""),
            Outcome: result.GetAttributeValue("outcome") ?? "None",
            DurationMs: ParseDuration(result.GetAttributeValue("duration")),
            ErrorMessage: errorInfo?.GetElementByLocalName("Message")?.Value.Trim(),
            StackTrace: errorInfo?.GetElementByLocalName("StackTrace")?.Value.Trim(),
            StandardOutput: output?.GetElementByLocalName("StdOut")?.Value.Trim());
    }

    private static double ParseDuration(string? duration) =>
        TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.TotalMilliseconds
            : 0;
}
