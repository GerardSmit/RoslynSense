using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages.Values.Core;

namespace RoslynMCP.Languages.Values;

/// <summary>
/// The value sets this process knows about and where they are written, after the configuration has
/// been read and checked.
/// </summary>
/// <remarks>
/// A malformed entry warns and is dropped rather than failing the load, the same as every other
/// pack's settings: the rest of the section is still worth having, and a typo in one binding must
/// not cost the solution its diagnostics. The one thing worth being strict about is a binding
/// naming a set that does not exist, because that binding would otherwise be silently inert — which
/// is the exact failure this whole pack exists to remove.
/// </remarks>
internal sealed record ValueSettings
{
    /// <summary><c>--no-valuesets</c>, or <c>tools.valueSets: false</c>.</summary>
    public static ValueSettings Disabled { get; } = new() { Enabled = false };

    public required bool Enabled { get; init; }

    public ImmutableArray<ValueSetDefinition> Sets { get; init; } = [];

    public ImmutableArray<ValueBinding> Bindings { get; init; } = [];

    /// <summary>Whether a literal outside its set is reported at all.</summary>
    public bool UnknownValueDiagnostic { get; init; } = true;

    /// <summary>How loudly. See <see cref="ValueSetsConfig.Severity"/>.</summary>
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

    /// <summary>The set a binding names, or null when the configuration dropped it.</summary>
    public ValueSetDefinition? Set(string id)
    {
        foreach (var set in Sets)
        {
            if (set.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return set;
        }

        return null;
    }

    public static ValueSettings Resolve(bool enabled, ValueSetsConfig? config, List<string> warnings)
    {
        // No section means no pack, not an empty one. Unlike every other language this one has
        // nothing it could do by default — there is no file type it owns and no API it recognises,
        // only what the file declares — so a solution that has not configured it should not pay a
        // detector call on every string literal in it.
        if (!enabled || config is null)
            return Disabled;

        var sets = ReadSets(config.Sets, warnings);

        return new ValueSettings
        {
            Enabled = true,
            Sets = sets,
            Bindings = ReadBindings(config.Bindings, sets, warnings),
            UnknownValueDiagnostic = config.UnknownValueDiagnostic ?? true,
            Severity = ReadSeverity(config.Severity, warnings),
        };
    }

    private static ImmutableArray<ValueSetDefinition> ReadSets(
        IReadOnlyList<ValueSetEntry>? configured, List<string> warnings)
    {
        if (configured is not { Count: > 0 })
            return [];

        var sets = ImmutableArray.CreateBuilder<ValueSetDefinition>(configured.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in configured)
        {
            if (entry.Id is not { Length: > 0 } id || string.IsNullOrWhiteSpace(id))
            {
                warnings.Add("valueSets.sets: an entry has no id; skipped.");
                continue;
            }

            if (!seen.Add(id))
            {
                warnings.Add($"valueSets.sets '{id}': declared twice; the second is skipped.");
                continue;
            }

            bool hasQuery = !string.IsNullOrWhiteSpace(entry.Query);
            bool hasValues = entry.Values is { Count: > 0 };

            if (!hasQuery && !hasValues)
            {
                warnings.Add($"valueSets.sets '{id}': neither a query nor a list of values; skipped.");
                continue;
            }

            if (hasQuery && hasValues)
            {
                warnings.Add(
                    $"valueSets.sets '{id}': has both a query and a list; the query is used and the "
                    + "list ignored.");
            }

            sets.Add(new ValueSetDefinition
            {
                Id = id,
                Connection = entry.Connection,
                Query = hasQuery ? entry.Query : null,
                Inline = hasQuery ? [] : Inline(entry.Values!),
                CaseSensitive = entry.CaseSensitive ?? false,
            });
        }

        return sets.ToImmutable();
    }

    private static ImmutableArray<ValueEntry> Inline(IReadOnlyList<string> values)
    {
        var entries = ImmutableArray.CreateBuilder<ValueEntry>(values.Count);

        foreach (string? value in values)
        {
            if (value is { Length: > 0 })
                entries.Add(new ValueEntry(value, null));
        }

        return entries.ToImmutable();
    }

    private static ImmutableArray<ValueBinding> ReadBindings(
        IReadOnlyList<ValueBindingEntry>? configured, ImmutableArray<ValueSetDefinition> sets,
        List<string> warnings)
    {
        if (configured is not { Count: > 0 })
            return [];

        var bindings = ImmutableArray.CreateBuilder<ValueBinding>(configured.Count);

        foreach (var entry in configured)
        {
            if (entry.Set is not { Length: > 0 } id)
            {
                warnings.Add("valueSets.bindings: an entry names no set; skipped.");
                continue;
            }

            if (!sets.Any(set => set.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"valueSets.bindings: no set named '{id}'; the binding is skipped.");
                continue;
            }

            if (entry.MemberName is not { Length: > 0 } member || string.IsNullOrWhiteSpace(member))
            {
                warnings.Add($"valueSets.bindings for '{id}': no memberName; skipped.");
                continue;
            }

            int? index = entry.ValueIndex;

            if (index is < 0)
            {
                warnings.Add(
                    $"valueSets.bindings for '{id}': valueIndex {index} is not a parameter position; "
                    + "the binding is read as a member holding the value.");
                index = null;
            }

            bindings.Add(new ValueBinding
            {
                SetId = id,
                MemberName = member,
                ContainingType = string.IsNullOrWhiteSpace(entry.ContainingType)
                    ? null
                    : entry.ContainingType,
                ParameterTypes = entry.ParameterTypes is { } types ? [.. types] : null,
                ValueIndex = index,
            });
        }

        return bindings.ToImmutable();
    }

    private static DiagnosticSeverity ReadSeverity(string? configured, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return DiagnosticSeverity.Error;

        switch (configured.Trim().ToLowerInvariant())
        {
            case "error":
                return DiagnosticSeverity.Error;
            case "warning":
                return DiagnosticSeverity.Warning;
            case "information":
            case "info":
                return DiagnosticSeverity.Info;
            case "hint":
                return DiagnosticSeverity.Hidden;
            default:
                warnings.Add(
                    $"valueSets.severity '{configured}': not one of error, warning or information; "
                    + "using error.");
                return DiagnosticSeverity.Error;
        }
    }
}
