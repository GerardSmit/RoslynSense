using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages.Templates.Core;

namespace RoslynMCP.Languages.Templates;

/// <summary>
/// The screens an application declares in template files, as the tree they describe.
/// </summary>
/// <remarks>
/// <para>
/// A pack that owns no files and answers no request about one, the same as the routes and
/// schedules packs. What it contributes is a section of the Discovery view, and for the same
/// reason: the thing it lists exists nowhere in the editor. An application whose screens are
/// declared in data rather than in code has a structure — pages under pages, each hosting
/// something that renders it — and none of it is visible from a file tree, because the files are
/// two hundred fragments named after the change that introduced them rather than after the screen
/// they describe.
/// </para>
/// <para>
/// The two questions it answers are the two halves of any declared screen: where is this declared,
/// and what actually renders it. They are in different files, in different languages, in different
/// projects, and nothing links them but a name — so the buttons on a row are the whole point of
/// the section rather than a convenience on top of it.
/// </para>
/// </remarks>
internal sealed partial class TemplatesLanguage : ILanguagePack
{
    /// <summary>
    /// The pack id, the <c>roslynSense.languages.*</c> key and the <c>tools.templates</c> gate,
    /// one string so a new surface cannot spell it differently from the last one.
    /// </summary>
    public const string PackId = "templates";

    public TemplatesLanguage(EffectiveSettings settings)
        : this(settings.Templates)
    {
    }

    /// <summary>The settings directly, for the hosts and the tests that have already resolved them.</summary>
    internal TemplatesLanguage(TemplatesSettings settings)
    {
        Settings = settings;
        Templates = new TemplateIndex(settings.ControlFolders);
    }

    internal TemplatesSettings Settings { get; }

    /// <summary>The merged templates of each root, parsed once and kept until a file changes.</summary>
    internal TemplateIndex Templates { get; }

    public string Id => PackId;

    public string DisplayName => "Templates";

    /// <summary>
    /// None.
    /// </summary>
    /// <remarks>
    /// The files are YAML, and claiming <c>.yml</c> would claim every pipeline definition and
    /// every compose file in the solution with it. This pack knows which YAML files are its own by
    /// where they are rather than by what they are called, which is not a thing a file extension
    /// can express — so it claims none and reads the folder itself.
    /// </remarks>
    public ImmutableArray<string> FileExtensions { get; } = [];

    /// <summary>Nothing to declare: the pack contributes a section and no editor feature.</summary>
    public LanguageCapabilities Capabilities => LanguageCapabilities.None;

    /// <summary>None. Nothing here is resolved through a compilation.</summary>
    public ImmutableArray<string> WellKnownTypeNames { get; } = [];

    /// <summary>No contributor pass over C# symbols has anything to add to a declared screen.</summary>
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } = [];

    /// <summary>Nothing is projected: a template is read where it is written.</summary>
    public bool IsProjectionPath(string? filePath) => false;
}
