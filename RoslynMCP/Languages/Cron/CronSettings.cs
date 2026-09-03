using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages.Cron.Core;

namespace RoslynMCP.Languages.Cron;

/// <summary>
/// Which scheduling APIs this process recognises and how loudly it complains, after the
/// configuration has been read and checked.
/// </summary>
/// <remarks>
/// Unlike the value sets, an absent section is not the same as a disabled pack: the shipped
/// bindings cover Hangfire and Quartz, and the parameter-name rule covers a solution's own wrapper,
/// so the pack is useful with nothing configured at all. What configuration adds is the in-house
/// scheduler whose method is called something nobody could have guessed.
/// <para>
/// A malformed entry warns and is dropped rather than failing the load, the same as every other
/// pack's settings — a typo in one binding must not cost the solution its diagnostics.
/// </para>
/// </remarks>
internal sealed record CronSettings
{
    /// <summary><c>--no-cron</c>, or <c>tools.cron: false</c>.</summary>
    public static CronSettings Disabled { get; } = new() { Enabled = false };

    /// <summary>The shipped table alone, which is what an unconfigured solution gets.</summary>
    public static CronSettings Default { get; } = new()
    {
        Enabled = true,
        Bindings = CronPresets.Bindings,
        ParameterNames = CronPresets.ParameterNames,
    };

    public required bool Enabled { get; init; }

    /// <summary>The shipped bindings, then the user's. See <see cref="CronPresets.Bindings"/>.</summary>
    public ImmutableArray<CronBinding> Bindings { get; init; } = [];

    /// <summary>Parameter names that mean a string is a schedule, shipped and configured together.</summary>
    public ImmutableArray<string> ParameterNames { get; init; } = [];

    /// <summary>Whether an expression the library would reject is reported at all.</summary>
    public bool ExpressionDiagnostic { get; init; } = true;

    /// <summary>
    /// How loudly. Warning by default, and deliberately not error.
    /// </summary>
    /// <remarks>
    /// The string is read by a library this pack did not write, at a version it cannot see. Being
    /// wrong about a dialect is a real possibility, and an error would put a red squiggle under
    /// working code — which teaches people to stop reading the squiggles.
    /// </remarks>
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Warning;

    public static CronSettings Resolve(bool enabled, CronConfig? config, List<string> warnings)
    {
        if (!enabled)
            return Disabled;

        if (config is null)
            return Default;

        return new CronSettings
        {
            Enabled = true,
            Bindings = [.. CronPresets.Bindings, .. ReadBindings(config.Bindings, warnings)],
            ParameterNames = [.. CronPresets.ParameterNames, .. ReadNames(config.ParameterNames)],
            ExpressionDiagnostic = config.ExpressionDiagnostic ?? true,
            Severity = ReadSeverity(config.Severity, warnings),
        };
    }

    private static ImmutableArray<CronBinding> ReadBindings(
        IReadOnlyList<CronBindingEntry>? configured, List<string> warnings)
    {
        if (configured is not { Count: > 0 })
            return [];

        var bindings = ImmutableArray.CreateBuilder<CronBinding>(configured.Count);

        foreach (var entry in configured)
        {
            if (entry.MemberName is not { Length: > 0 } member || string.IsNullOrWhiteSpace(member))
            {
                warnings.Add("cron.bindings: an entry has no memberName; skipped.");
                continue;
            }

            if (entry.CronIndex is < 0)
            {
                warnings.Add(
                    $"cron.bindings for '{member}': cronIndex {entry.CronIndex} is not a parameter "
                    + "position; the schedule is looked for by parameter name instead.");
            }

            bindings.Add(new CronBinding
            {
                MemberName = member,
                ContainingType = string.IsNullOrWhiteSpace(entry.ContainingType)
                    ? null
                    : entry.ContainingType,
                ParameterTypes = entry.ParameterTypes is { } types ? [.. types] : null,
                CronIndex = entry.CronIndex is >= 0 ? entry.CronIndex : null,
                IdIndex = entry.IdIndex is >= 0 ? entry.IdIndex : null,
                MethodIndex = entry.MethodIndex is >= 0 ? entry.MethodIndex : null,

                // A configured entry names a wrapper of the solution's own, so nothing about it says
                // which library is underneath. The dialect it is read with is then the compilation's
                // to decide, unless the entry says outright.
                Library = CronLibrary.Unknown,
                Dialect = ReadDialect(entry.Dialect, member, warnings),
            });
        }

        return bindings.ToImmutable();
    }

    private static ImmutableArray<string> ReadNames(IReadOnlyList<string>? configured)
    {
        if (configured is not { Count: > 0 })
            return [];

        var names = ImmutableArray.CreateBuilder<string>(configured.Count);

        foreach (string? name in configured)
        {
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name.Trim());
        }

        return names.ToImmutable();
    }

    private static CronDialect ReadDialect(string? configured, string member, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return CronDialect.Standard;

        switch (configured.Trim().ToLowerInvariant())
        {
            case "hangfire":
                return CronDialect.Hangfire;
            case "quartz":
                return CronDialect.Quartz;
            case "standard":
            case "crontab":
                return CronDialect.Standard;
            default:
                warnings.Add(
                    $"cron.bindings for '{member}': dialect '{configured}' is not one of hangfire, "
                    + "quartz or standard; using standard.");
                return CronDialect.Standard;
        }
    }

    private static DiagnosticSeverity ReadSeverity(string? configured, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return DiagnosticSeverity.Warning;

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
                    $"cron.severity '{configured}': not one of error, warning or information; "
                    + "using warning.");
                return DiagnosticSeverity.Warning;
        }
    }
}
