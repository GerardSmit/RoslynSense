using System.Collections.Immutable;
using RoslynMCP.Config;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Templates;

/// <summary>
/// Where this process looks for template files, and which language it reads their names in.
/// </summary>
/// <remarks>
/// Folders and a language, and no table of names — which is what makes this pack different from
/// the routes and schedules ones. Those have to be told what a declaration looks like because it
/// is written in C# and could be anything; a template folder has a shape of its own, and the only
/// things that vary between one solution and the next are where it is and who is reading it.
/// </remarks>
internal sealed record TemplatesSettings
{
    /// <summary>
    /// Where a folder of templates conventionally sits: beside the application, under the
    /// directory a web application keeps its own data in.
    /// </summary>
    /// <remarks>
    /// Two of them because a folder of shipped templates and a folder of a customer's overrides is
    /// the arrangement that makes the pattern work at all — the second wins over the first, and
    /// both are read.
    /// </remarks>
    public static ImmutableArray<string> ConventionalFolders { get; } =
        ["App_Data/Templates", "App_Data/TemplatesCustom"];

    /// <summary>
    /// Where an application keeps the modules it installed, as opposed to the ones its templates
    /// declare.
    /// </summary>
    /// <remarks>
    /// Looked in only for a module the templates name and do not describe, which is a quarter of
    /// them — see <see cref="Core.TemplateControls"/> for why there are so many and what is done
    /// about it.
    /// </remarks>
    public static ImmutableArray<string> ConventionalControlFolders { get; } = ["DesktopModules"];

    /// <summary><c>--no-templates</c>, or <c>tools.templates: false</c>.</summary>
    /// <remarks>
    /// With no folders rather than with the conventional ones, so that nothing downstream has to
    /// check the switch as well as the list before it goes looking at a disk.
    /// </remarks>
    public static TemplatesSettings Disabled { get; } = new()
    {
        Enabled = false,
        Folders = [],
        ControlFolders = [],
    };

    /// <summary>The conventional folders alone, which is what an unconfigured solution gets.</summary>
    public static TemplatesSettings Default { get; } = new()
    {
        Enabled = true,
        Folders = ConventionalFolders,
        ControlFolders = ConventionalControlFolders,
    };

    public required bool Enabled { get; init; }

    /// <summary>
    /// The folders looked at, relative to each project.
    /// </summary>
    /// <remarks>
    /// The conventional ones stay in front of whatever is configured, for the reason every other
    /// pack keeps its shipped table in front of the user's: a solution that keeps its templates
    /// somewhere else is naming an addition, and taking the conventional folders away from it as
    /// well would be a second decision nobody asked for.
    /// </remarks>
    public ImmutableArray<string> Folders { get; init; } = ConventionalFolders;

    /// <summary>The folders a module's own control is looked for in, relative to each project.</summary>
    public ImmutableArray<string> ControlFolders { get; init; } = ConventionalControlFolders;

    /// <summary>
    /// Which language tag a row's name is read from, or null for whichever the file wrote first.
    /// </summary>
    /// <remarks>
    /// Named rather than taken from the machine's locale. The tree is a view of a file, and the
    /// languages in that file are the customer's rather than the reader's — a developer working
    /// on a Dutch application on an English machine wants the Dutch names, because those are the
    /// words in the screenshots they are being sent.
    /// </remarks>
    public string? Locale { get; init; }

    public static TemplatesSettings Resolve(
        bool enabled, TemplatesConfig? config, List<string> warnings)
    {
        if (!enabled)
            return Disabled;

        if (config is null)
            return Default;

        return new TemplatesSettings
        {
            Enabled = true,
            Folders = [.. ConventionalFolders, .. ReadFolders(config.Folders, "folders", warnings)],
            ControlFolders =
            [
                .. ConventionalControlFolders,
                .. ReadFolders(config.ControlFolders, "controlFolders", warnings),
            ],
            Locale = string.IsNullOrWhiteSpace(config.Locale) ? null : config.Locale.Trim(),
        };
    }

    /// <summary>
    /// The configured folders, as relative paths.
    /// </summary>
    /// <remarks>
    /// A rooted path is refused rather than followed. Every folder here is joined to a project
    /// directory, and one naming a drive would take the pack out of the solution entirely — which
    /// is not a thing a setting in a checked-in file should be able to do.
    /// </remarks>
    private static ImmutableArray<string> ReadFolders(
        IReadOnlyList<string>? configured, string setting, List<string> warnings)
    {
        if (configured is not { Count: > 0 })
            return [];

        var folders = ImmutableArray.CreateBuilder<string>(configured.Count);

        foreach (string? folder in configured)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                warnings.Add($"templates.{setting}: an entry is empty; skipped.");
                continue;
            }

            string trimmed = folder.Trim();

            if (PathHelper.IsRooted(trimmed) || trimmed.Contains("..", StringComparison.Ordinal))
            {
                warnings.Add(
                    $"templates.{setting}: '{trimmed}' is not a path relative to a project; "
                    + "skipped.");
                continue;
            }

            // The conventional ones are already in front of these, and a folder listed twice would
            // be read twice — which for the template folders means every declaration in it merged
            // over itself.
            if (!ConventionalFolders.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
                && !ConventionalControlFolders.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(trimmed);
            }
        }

        return folders.ToImmutable();
    }
}
