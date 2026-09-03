using System.Collections.Immutable;
using RoslynMCP.Config;
using RoslynMCP.Languages.Resources.Core;

namespace RoslynMCP.Languages.Resources;

/// <summary>
/// What the resources pack actually runs with: a preset, whatever <c>roslynsense.json</c> layered
/// on top of it, and whether the pack is registered at all.
/// </summary>
internal sealed record ResourceSettings
{
    /// <summary><c>--no-resources</c>, or <c>tools.resources: false</c>.</summary>
    public static ResourceSettings Disabled { get; } = new() { Enabled = false };

    public required bool Enabled { get; init; }

    public ResourceDiscoveryOptions Discovery { get; init; } = ResourceDiscoveryOptions.Default;

    public ImmutableArray<ResourceRootConvention> Conventions { get; init; } = [];

    public ImmutableArray<ResourceLookup> Lookups { get; init; } = [];

    /// <summary>Keys a markup attribute names rather than any call site writing them out.</summary>
    public ImmutableArray<ResourceMarkupBinding> MarkupBindings { get; init; } = [];

    /// <summary>Whether a key no file of its family declares is reported. Opt-in: see
    /// <see cref="ResourcesConfig.MissingKeyDiagnostic"/>.</summary>
    public bool MissingKeyDiagnostic { get; init; }

    /// <summary>The convention a lookup's <see cref="ResourceLookup.Fallbacks"/> names, or null
    /// when the configuration dropped it.</summary>
    public ResourceRootConvention? Convention(string id)
    {
        foreach (var convention in Conventions)
        {
            if (convention.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return convention;
        }

        return null;
    }

    /// <summary>
    /// Folds the configured section onto the named preset. A malformed entry warns and is dropped
    /// rather than failing the load — the rest of the section is still worth having, and a config
    /// error must not leave the whole solution without navigation.
    /// </summary>
    public static ResourceSettings Resolve(bool enabled, ResourcesConfig? config, List<string> warnings)
    {
        if (!enabled)
            return Disabled;

        var preset = ResourcePresets.Named(config?.Preset, warnings);

        if (config is null)
        {
            return new ResourceSettings
            {
                Enabled = true,
                Conventions = preset.Conventions,
                Lookups = preset.Lookups,
                MarkupBindings = preset.MarkupBindings,
            };
        }

        var declared = new ResourcePreset(
            ReadConventions(config.Conventions, warnings),
            ReadLookups(config.Lookups, warnings))
        {
            MarkupBindings = ReadMarkupBindings(config.MarkupBindings, warnings),
        };

        var merged = ResourcePresets.Merge(preset, declared);

        return new ResourceSettings
        {
            Enabled = true,
            Discovery = ReadDiscovery(config, warnings),
            Conventions = merged.Conventions,
            Lookups = merged.Lookups,
            MarkupBindings = merged.MarkupBindings,
            MissingKeyDiagnostic = config.MissingKeyDiagnostic,
        };
    }

    private static ResourceDiscoveryOptions ReadDiscovery(ResourcesConfig config, List<string> warnings)
    {
        var options = ResourceDiscoveryOptions.Default with
        {
            Include = Strings(config.Include),
            Exclude = Strings(config.Exclude),
        };

        if (config.Overrides is not { Count: > 0 } overrides)
            return options;

        var rules = ImmutableArray.CreateBuilder<ResourceOverrideRule>(overrides.Count);

        foreach (var rule in overrides)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern))
            {
                warnings.Add("resources.overrides: an entry has no pattern; skipped.");
                continue;
            }

            rules.Add(new ResourceOverrideRule(rule.Pattern, rule.Rank));
        }

        return options with { Overrides = rules.ToImmutable() };
    }

    private static ImmutableArray<ResourceMarkupBinding> ReadMarkupBindings(
        IReadOnlyList<string>? configured, List<string> warnings)
    {
        if (configured is not { Count: > 0 })
            return [];

        var bindings = ImmutableArray.CreateBuilder<ResourceMarkupBinding>(configured.Count);

        foreach (string pattern in configured)
        {
            if (ResourceMarkupBinding.Parse(pattern ?? "", out string? problem) is { } binding)
                bindings.Add(binding);
            else
                warnings.Add($"resources.markupBindings: '{pattern}' is skipped, because {problem}.");
        }

        return bindings.ToImmutable();
    }

    private static ImmutableArray<ResourceRootConvention> ReadConventions(
        IReadOnlyList<ResourceConventionConfig>? configured, List<string> warnings)
    {
        if (configured is not { Count: > 0 })
            return [];

        var conventions = ImmutableArray.CreateBuilder<ResourceRootConvention>(configured.Count);

        foreach (var convention in configured)
        {
            if (convention.Id is not { Length: > 0 } id)
            {
                warnings.Add("resources.conventions: an entry has no id; skipped.");
                continue;
            }

            if (convention is { SiblingFolder.Length: > 0, RootFolder.Length: > 0 })
            {
                warnings.Add(
                    $"resources.conventions '{id}': siblingFolder and rootFolder are exclusive; skipped.");
                continue;
            }

            var suffix = convention.Suffix is { Count: > 0 } declared
                ? ImmutableArray.CreateRange(declared)
                : ImmutableArray.Create(".resx");

            conventions.Add(new ResourceRootConvention
            {
                Id = id,
                SiblingFolder = convention.SiblingFolder,
                RootFolder = convention.RootFolder,
                FixedName = convention.FixedName,
                Suffix = suffix,
            });
        }

        return conventions.ToImmutable();
    }

    private static ImmutableArray<ResourceLookup> ReadLookups(
        IReadOnlyList<ResourceLookupConfig>? configured, List<string> warnings)
    {
        if (configured is not { Count: > 0 })
            return [];

        var lookups = ImmutableArray.CreateBuilder<ResourceLookup>(configured.Count);

        foreach (var lookup in configured)
        {
            if (lookup.MethodName is not { Length: > 0 } methodName)
            {
                warnings.Add("resources.lookups: an entry has no methodName; skipped.");
                continue;
            }

            // Omitted containingType is the documented way to catch a helper each module
            // redeclares for itself; the entry stays, matched on name and signature alone.
            string? containingType = lookup.ContainingType is { Length: > 0 } declared ? declared : null;
            string member = containingType is null ? methodName : $"{containingType}.{methodName}";

            if (!Parse<RootSource>(lookup.RootSource, member, "rootSource", warnings, out var source)
                || !Parse<RootInterpretation>(
                    lookup.RootInterpretation, member, "rootInterpretation", warnings, out var interpretation))
            {
                continue;
            }

            lookups.Add(new ResourceLookup
            {
                ContainingType = containingType,
                MethodName = methodName,
                ParameterTypes = lookup.ParameterTypes is { } parameters
                    ? ImmutableArray.CreateRange(parameters)
                    : null,
                KeyIndex = lookup.KeyIndex,
                RootSource = source,
                RootInterpretation = interpretation,
                RootIndex = lookup.RootIndex,
                RootConstant = lookup.RootConstant,
                DefaultKeySuffix = lookup.DefaultKeySuffix,
                Fallbacks = Strings(lookup.Fallbacks),
            });
        }

        return lookups.ToImmutable();
    }

    private static ImmutableArray<string> Strings(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? ImmutableArray.CreateRange(values) : [];

    private static bool Parse<TEnum>(
        string? value, string member, string field, List<string> warnings, out TEnum parsed)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse(value, ignoreCase: true, out parsed))
            return true;

        warnings.Add(
            $"resources.lookups '{member}': {field} '{value}' is not one of "
            + $"{string.Join(", ", Enum.GetNames<TEnum>()).ToLowerInvariant()}; skipped.");
        return false;
    }
}
