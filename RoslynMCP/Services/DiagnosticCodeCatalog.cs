using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynMCP.Services;

/// <summary>What is known about a diagnostic code, by whoever knows it.</summary>
/// <param name="Code">The code as it is written in a suppression list — <c>NU1605</c>.</param>
/// <param name="Title">One line naming the rule, where the source has one.</param>
/// <param name="Description">The paragraph explaining it, where the source has one.</param>
/// <param name="Message">The text the build actually prints, with the format holes left in.</param>
/// <param name="Category">The rule's category — <c>Usage</c>, <c>Compiler</c>.</param>
/// <param name="Severity">What it is reported as when it is not suppressed.</param>
/// <param name="HelpLink">The documentation page for it.</param>
internal readonly record struct DiagnosticCodeInfo(
    string Code,
    string? Title,
    string? Description,
    string? Message,
    string? Category,
    DiagnosticSeverity? Severity,
    string? HelpLink)
{
    /// <summary>Whether anything beyond the code itself is known.</summary>
    public bool IsEmpty => Title is null && Description is null && Message is null;
}

/// <summary>
/// What a diagnostic code means, from whichever source owns it.
/// </summary>
/// <remarks>
/// <para>
/// A suppression list is the one place every code family meets: a <c>NoWarn</c> holds
/// <c>CS0168</c> beside <c>NU1605</c> beside <c>CA1822</c>, and each of those three is documented
/// somewhere different. The catalog is the seam that answers about all of them, in the order of how
/// authoritative the source is: the analyzer that defines the rule, then the compiler's own
/// resources, then the vendored documentation, then — for a code nobody here owns — the family's
/// documentation URL, which is derivable from the prefix alone.
/// </para>
/// <para>
/// The analyzer step is the only one that needs a project, and it never loads one. A hover is not
/// a reason to start an MSBuild evaluation; if the solution is open the descriptors are already in
/// memory, and if it is not, the vendored table and the link still answer.
/// </para>
/// </remarks>
internal static class DiagnosticCodeCatalog
{
    /// <summary>A code as a suppression list writes it: letters then digits, nothing else.</summary>
    public static bool IsCode(string token) =>
        Split(token) is not (null, null);

    private static readonly Lazy<ImmutableDictionary<string, VendoredEntry>> s_vendored =
        new(LoadVendored, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<CompilerFacts?> s_compiler =
        new(CompilerFacts.TryBind, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Descriptors by rule id, per project.
    /// </summary>
    /// <remarks>
    /// Keyed on the analyzer set rather than the project version: the descriptors a project
    /// supports change when its analyzer references change, and nothing else about the project
    /// touches them. Rebuilding the map on every hover would walk <c>SupportedDiagnostics</c> for
    /// several hundred analyzers, each of which materialises its localisable strings.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, (ImmutableArray<DiagnosticAnalyzer> Analyzers,
        ImmutableDictionary<string, DiagnosticDescriptor> Descriptors)> s_descriptors =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record VendoredEntry(string? Severity, string? Message, string? Description);

    /// <summary>
    /// Everything known about <paramref name="code"/>, or null when it is not a diagnostic code.
    /// </summary>
    /// <param name="code">The code, in any casing.</param>
    /// <param name="project">The project the suppression is written in, when one is loaded. Only
    /// its analyzers are read, and only from what is already in memory.</param>
    public static DiagnosticCodeInfo? Lookup(string code, Project? project = null)
    {
        var (prefix, number) = Split(code);
        if (prefix is null || number is null)
            return null;

        string normalized = prefix + number;

        return FromAnalyzer(normalized, project)
            ?? FromCompiler(normalized, prefix, number)
            ?? FromVendored(normalized)
            ?? new DiagnosticCodeInfo(normalized, null, null, null, null, null, HelpLinkFor(prefix, number));
    }

    /// <summary>The rule as its analyzer declares it — the only source that knows a custom rule.</summary>
    private static DiagnosticCodeInfo? FromAnalyzer(string code, Project? project)
    {
        if (project is null || !DescriptorsFor(project).TryGetValue(code, out var descriptor))
            return null;

        return new DiagnosticCodeInfo(
            code,
            Text(descriptor.Title),
            Text(descriptor.Description),
            Text(descriptor.MessageFormat),
            descriptor.Category,
            descriptor.DefaultSeverity,
            descriptor.HelpLinkUri is { Length: > 0 } link ? link : HelpLinkFor(code));
    }

    /// <summary>
    /// A compiler diagnostic, from the compiler's own resources.
    /// </summary>
    /// <remarks>
    /// <c>ErrorFacts</c> is what Roslyn builds the descriptor from when it reports the diagnostic,
    /// so this is the same text the Problems panel shows rather than a copy of it that can drift.
    /// It is internal and reached by reflection — the publicizer this project uses elsewhere cannot
    /// take <c>Microsoft.CodeAnalysis.CSharp</c>: publicizing that one assembly breaks
    /// <c>MSBuildWorkspace</c>'s GAC probing at runtime, and every project in the solution stops
    /// loading. Reflection has the failure mode a typed call was chosen to avoid, so
    /// <see cref="CompilerFacts"/> reports what it could not bind and the hover falls back to the
    /// documentation link rather than going silently blank.
    /// </remarks>
    private static DiagnosticCodeInfo? FromCompiler(string code, string prefix, string number)
    {
        if (!prefix.Equals("CS", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            || s_compiler.Value is not { } facts
            || facts.ErrorCode(value) is not { } errorCode)
        {
            return null;
        }

        // Every code has a message; only the ones Roslyn documents have a title. A code with
        // neither is a value in the enum that is not a diagnostic anyone can write down.
        string? title = Text(facts.Title(errorCode));
        string? message = facts.Message(errorCode);
        if (title is null && message is null)
            return null;

        return new DiagnosticCodeInfo(
            code,
            title,
            Text(facts.Description(errorCode)),
            message,
            facts.Category(errorCode),
            facts.Severity(errorCode),
            facts.HelpLink(errorCode) is { Length: > 0 } link ? link : HelpLinkFor(code));
    }

    /// <summary>The vendored documentation — NuGet's codes and MSBuild's warnings.</summary>
    private static DiagnosticCodeInfo? FromVendored(string code)
    {
        if (!s_vendored.Value.TryGetValue(code, out var entry))
            return null;

        return new DiagnosticCodeInfo(
            code,
            Title: null,
            entry.Description,
            entry.Message,
            Category: null,
            entry.Severity switch
            {
                "error" => DiagnosticSeverity.Error,
                "warning" => DiagnosticSeverity.Warning,
                _ => null,
            },
            HelpLinkFor(code));
    }

    private static ImmutableDictionary<string, DiagnosticDescriptor> DescriptorsFor(Project project)
    {
        string key = project.FilePath ?? project.Name;
        var analyzers = AnalyzerService.GetAnalyzersFor(project);

        if (s_descriptors.TryGetValue(key, out var cached) && cached.Analyzers.Equals(analyzers))
            return cached.Descriptors;

        var builder = ImmutableDictionary.CreateBuilder<string, DiagnosticDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var analyzer in analyzers)
        {
            // A third-party analyzer that throws while describing itself must not take the hover
            // — or anything else asking — down with it.
            try
            {
                foreach (var descriptor in analyzer.SupportedDiagnostics)
                    builder.TryAdd(descriptor.Id, descriptor);
            }
            catch (Exception ex)
            {
                ServiceLog.Error(
                    $"Analyzer '{analyzer.GetType().Name}' failed to describe its diagnostics: {ex.Message}",
                    key: $"analyzer-descriptors:{analyzer.GetType().FullName}");
            }
        }

        var descriptors = builder.ToImmutable();
        s_descriptors[key] = (analyzers, descriptors);
        return descriptors;
    }

    /// <summary>The documentation page for a code, from its family alone.</summary>
    /// <remarks>
    /// Every family below publishes one page per code at a fixed URL, so the link is knowable
    /// without a table and stays right for a code minted after this table was vendored — which is
    /// the case that matters, because that is exactly the code nothing else here can describe.
    /// </remarks>
    private static string? HelpLinkFor(string code)
    {
        var (prefix, number) = Split(code);
        return prefix is null || number is null ? null : HelpLinkFor(prefix, number);
    }

    private static string? HelpLinkFor(string prefix, string number)
    {
        string lower = (prefix + number).ToLowerInvariant();

        return prefix.ToUpperInvariant() switch
        {
            "CS" => $"https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/{lower}",
            "NU" => $"https://learn.microsoft.com/nuget/reference/errors-and-warnings/{lower}",
            "MSB" => $"https://learn.microsoft.com/visualstudio/msbuild/errors/{lower}",
            "CA" => $"https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/{lower}",
            "IDE" => $"https://learn.microsoft.com/dotnet/fundamentals/code-analysis/style-rules/{lower}",
            "NETSDK" => $"https://learn.microsoft.com/dotnet/core/tools/sdk-errors/{lower}",
            "SYSLIB" => $"https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/{lower}",
            _ => null,
        };
    }

    /// <summary>A code split into its family and its number, or nulls when it is neither.</summary>
    private static (string? Prefix, string? Number) Split(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return (null, null);

        code = code.Trim();
        int digit = 0;
        while (digit < code.Length && char.IsAsciiLetter(code[digit]))
            digit++;

        if (digit == 0 || digit == code.Length)
            return (null, null);

        for (int i = digit; i < code.Length; i++)
        {
            if (!char.IsAsciiDigit(code[i]))
                return (null, null);
        }

        return (code[..digit].ToUpperInvariant(), code[digit..]);
    }

    /// <summary>
    /// Roslyn's <c>ErrorFacts</c>, bound once.
    /// </summary>
    /// <remarks>
    /// All-or-nothing: if any member fails to bind the whole thing is null and CS codes fall back
    /// to their documentation link, rather than a hover that shows a title and silently loses the
    /// description because one lookup moved.
    /// </remarks>
    private sealed class CompilerFacts
    {
        private readonly Type _errorCode;
        private readonly MethodInfo _title;
        private readonly MethodInfo _description;
        private readonly MethodInfo _message;
        private readonly MethodInfo _category;
        private readonly MethodInfo _severity;
        private readonly MethodInfo _helpLink;

        private CompilerFacts(
            Type errorCode, MethodInfo title, MethodInfo description, MethodInfo message,
            MethodInfo category, MethodInfo severity, MethodInfo helpLink)
        {
            (_errorCode, _title, _description, _message, _category, _severity, _helpLink) =
                (errorCode, title, description, message, category, severity, helpLink);
        }

        public static CompilerFacts? TryBind()
        {
            var assembly = typeof(CSharpSyntaxTree).Assembly;
            var errorCode = assembly.GetType("Microsoft.CodeAnalysis.CSharp.ErrorCode");
            var facts = assembly.GetType("Microsoft.CodeAnalysis.CSharp.ErrorFacts");

            if (errorCode is null || facts is null)
                return Missing("ErrorCode/ErrorFacts");

            const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            MethodInfo? Method(string name, params Type[] parameters) =>
                facts.GetMethod(name, Flags, binder: null, [errorCode, .. parameters], modifiers: null);

            return Method("GetTitle") is { } title
                && Method("GetDescription") is { } description
                && Method("GetMessage", typeof(CultureInfo)) is { } message
                && Method("GetCategory") is { } category
                && Method("GetSeverity") is { } severity
                && Method("GetHelpLink") is { } helpLink
                ? new CompilerFacts(errorCode, title, description, message, category, severity, helpLink)
                : Missing("one of ErrorFacts.GetTitle/GetDescription/GetMessage/GetCategory/GetSeverity/GetHelpLink");

            static CompilerFacts? Missing(string what)
            {
                // Visible, because the symptom otherwise is CS hovers quietly showing a bare link
                // after a Roslyn upgrade, which nothing else would ever explain.
                Console.Error.WriteLine(
                    $"[Diagnostics] Roslyn's {what} could not be bound; CS codes will only link to their documentation.");
                return null;
            }
        }

        /// <summary>The enum value for a number, or null when the compiler has no such code.</summary>
        public object? ErrorCode(int value) =>
            Enum.IsDefined(_errorCode, value) ? Enum.ToObject(_errorCode, value) : null;

        public LocalizableString? Title(object code) => _title.Invoke(null, [code]) as LocalizableString;

        public LocalizableString? Description(object code) => _description.Invoke(null, [code]) as LocalizableString;

        public string? Message(object code) =>
            _message.Invoke(null, [code, CultureInfo.CurrentUICulture]) as string;

        public string? Category(object code) => _category.Invoke(null, [code]) as string;

        public DiagnosticSeverity? Severity(object code) =>
            _severity.Invoke(null, [code]) is DiagnosticSeverity severity ? severity : null;

        public string? HelpLink(object code) => _helpLink.Invoke(null, [code]) as string;
    }

    private static string? Text(LocalizableString? value)
    {
        string? text = value?.ToString(CultureInfo.CurrentUICulture);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static ImmutableDictionary<string, VendoredEntry> LoadVendored()
    {
        var entries = ImmutableDictionary.CreateBuilder<string, VendoredEntry>(StringComparer.OrdinalIgnoreCase);
        const string resource = "RoslynMCP.Services.DiagnosticCodes.diagnostic-codes.json";

        try
        {
            using var stream = typeof(DiagnosticCodeCatalog).Assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                // A build that failed to embed the table. Hover still answers with the help link.
                Console.Error.WriteLine($"[Diagnostics] Embedded '{resource}' is missing.");
                return entries.ToImmutable();
            }

            using var document = JsonDocument.Parse(stream);
            foreach (var code in document.RootElement.EnumerateObject())
            {
                entries[code.Name] = new VendoredEntry(
                    String(code.Value, "severity"),
                    String(code.Value, "message"),
                    String(code.Value, "description"));
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[Diagnostics] Embedded '{resource}' did not parse: {ex.Message}");
        }

        return entries.ToImmutable();

        static string? String(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
