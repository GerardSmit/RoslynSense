using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;

namespace RoslynMCP.Languages.WebForms;

/// <summary>How a configured attribute's value reads.</summary>
internal enum MarkupBindingKind
{
    /// <summary>A path from the bound item — <c>Customer.Account.DisplayName</c>.</summary>
    Member,

    /// <summary>A composite format string — <c>{0:dd-MM-yyyy}</c>.</summary>
    Format,
}

/// <summary>One attribute that carries a data expression, after the configuration has been read.</summary>
/// <param name="Tag">The tag as written, or null for any.</param>
/// <param name="Source">
/// For <see cref="MarkupBindingKind.Format"/>, the sibling attribute naming the value being
/// formatted — the <c>DataField</c> of <c>[ItemType].[Control.DataField]</c> — or null when the
/// entry did not say.
/// </param>
internal readonly record struct MarkupBinding(
    string? Tag, string Attribute, MarkupBindingKind Kind, string? Source)
{
    public bool Matches(string? prefix, string tagName, string attribute)
    {
        if (!Attribute.Equals(attribute, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Tag is not { Length: > 0 } tag || tag == "*")
            return true;

        string written = prefix is { Length: > 0 } p ? $"{p}:{tagName}" : tagName;
        return tag.Equals(written, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The markup attributes this solution reads as data expressions.
/// </summary>
/// <remarks>
/// Empty by default, and that is the whole design. The attributes that name a member of the bound
/// item come from the control library rather than from the framework, so there is no set that is
/// right for every solution — and the cost of guessing is not a missing feature but a warning on
/// every use of an attribute that turned out to hold something else.
/// <para>
/// A malformed entry warns and is dropped rather than failing the load, as every other pack's
/// settings do: the rest of the section is still worth having, and a typo in one entry must not
/// cost the solution its markup diagnostics.
/// </para>
/// </remarks>
internal sealed record MarkupBindingSettings
{
    public static MarkupBindingSettings None { get; } = new();

    /// <summary>
    /// What the running process reads.
    /// </summary>
    /// <remarks>
    /// Static because the markup handlers are static and reached from the LSP dispatch without a
    /// session to carry settings on — the same shape, and for the same reason, as
    /// <see cref="LspFeatureOptions"/>.
    /// </remarks>
    public static MarkupBindingSettings Current { get; set; } = None;

    public ImmutableArray<MarkupBinding> Attributes { get; init; } = [];

    /// <summary>Whether a name that binds to nothing is reported at all.</summary>
    public bool UnknownMemberDiagnostic { get; init; } = true;

    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Warning;

    /// <summary>The entry claiming an attribute on a tag, or null when none does.</summary>
    public MarkupBinding? For(string? prefix, string tagName, string attribute)
    {
        foreach (var binding in Attributes)
        {
            if (binding.Matches(prefix, tagName, attribute))
                return binding;
        }

        return null;
    }

    public static MarkupBindingSettings Resolve(WebFormsConfig? config, List<string> warnings)
    {
        if (config is null)
            return None;

        return new MarkupBindingSettings
        {
            Attributes = ReadAttributes(config.DataExpressions, warnings),
            UnknownMemberDiagnostic = config.UnknownMemberDiagnostic ?? true,
            Severity = ReadSeverity(config.Severity, warnings),
        };
    }

    private static ImmutableArray<MarkupBinding> ReadAttributes(
        IReadOnlyList<MarkupBindingEntry>? configured, List<string> warnings)
    {
        if (configured is not { Count: > 0 })
            return [];

        var read = ImmutableArray.CreateBuilder<MarkupBinding>(configured.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in configured)
        {
            if (entry.Attribute is not { Length: > 0 } attribute || string.IsNullOrWhiteSpace(attribute))
            {
                warnings.Add("webForms.dataExpressions: an entry has no attribute name; it is ignored.");
                continue;
            }

            var kind = entry.Kind?.ToLowerInvariant() switch
            {
                null or "" or "member" => MarkupBindingKind.Member,
                "format" => MarkupBindingKind.Format,
                _ => (MarkupBindingKind?)null,
            };

            if (kind is null)
            {
                warnings.Add(
                    $"webForms.dataExpressions: '{attribute}' has kind '{entry.Kind}', which is "
                    + "neither 'member' nor 'format'; the entry is ignored.");
                continue;
            }

            // Two entries claiming one attribute on one tag is a contradiction rather than a
            // refinement, and the second would never be reached.
            string key = $"{entry.Tag ?? "*"}|{attribute}";
            if (!seen.Add(key))
            {
                warnings.Add(
                    $"webForms.dataExpressions: '{attribute}' is configured more than once for "
                    + $"'{entry.Tag ?? "*"}'; the first entry is used.");
                continue;
            }

            read.Add(new MarkupBinding(entry.Tag, attribute, kind.Value, entry.Source));
        }

        return read.ToImmutable();
    }

    private static DiagnosticSeverity ReadSeverity(string? configured, List<string> warnings)
    {
        if (configured is not { Length: > 0 })
            return DiagnosticSeverity.Warning;

        switch (configured.ToLowerInvariant())
        {
            case "error": return DiagnosticSeverity.Error;
            case "warning": return DiagnosticSeverity.Warning;
            case "info" or "information": return DiagnosticSeverity.Info;
            case "hidden" or "none": return DiagnosticSeverity.Hidden;
            default:
                warnings.Add(
                    $"webForms.severity: '{configured}' is not a severity; 'warning' is used.");
                return DiagnosticSeverity.Warning;
        }
    }
}
