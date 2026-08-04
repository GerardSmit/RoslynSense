using Microsoft.Extensions.DependencyInjection;
using RoslynMCP.Config;
using RoslynMCP.Languages.Mediator;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.Razor;
using RoslynMCP.Languages.Resources;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Services;

namespace RoslynMCP.Languages;

/// <summary>
/// The one place a language pack is switched on for a process. Three hosts build a container —
/// the MCP server, the shared-host daemon and the CLI — and all three go through here so a pack
/// cannot end up registered in one and missing from another.
/// </summary>
internal static class LanguagePackRegistration
{
    /// <summary>
    /// The packs <paramref name="settings"/> enables, for a host that has no container. Order is
    /// registration order and it is what <see cref="LanguageRegistry"/> preserves.
    /// </summary>
    public static IReadOnlyList<ILanguagePack> Create(EffectiveSettings settings, IOutputFormatter formatter)
    {
        var packs = new List<ILanguagePack>();

        if (settings.WebForms)
            packs.Add(new WebFormsLanguage(formatter));
        if (settings.Razor)
            packs.Add(new RazorLanguage(formatter));
        if (settings.Proto)
            packs.Add(new ProtoLanguage(formatter));
        if (settings.Mediator)
            packs.Add(new MediatorLanguage());
        if (settings.Resources.Enabled)
            packs.Add(new ResourcesLanguage(settings));

        return packs;
    }

    /// <summary>Registers the enabled packs and the registry over them.</summary>
    public static void AddLanguagePacks(this IServiceCollection services, EffectiveSettings settings)
    {
        if (settings.WebForms)
            AddPack<WebFormsLanguage>(services);
        if (settings.Razor)
            AddPack<RazorLanguage>(services);
        if (settings.Proto)
            AddPack<ProtoLanguage>(services);
        if (settings.Mediator)
            AddPack<MediatorLanguage>(services);
        if (settings.Resources.Enabled)
            AddPack<ResourcesLanguage>(services);

        services.AddSingleton(sp => new LanguageRegistry(sp.GetServices<ILanguagePack>()).Publish());
    }

    /// <summary>
    /// One instance of the pack, registered as a pack and under every MCP tool-handler interface
    /// it implements. The tools ask for <c>IEnumerable&lt;I*Handler&gt;</c> and know nothing about
    /// packs; registering both from one place is what puts a single gate in front of the editor
    /// features and the AI tools instead of each carrying its own.
    /// </summary>
    private static void AddPack<TPack>(IServiceCollection services)
        where TPack : class, ILanguagePack
    {
        services.AddSingleton<TPack>();
        services.AddSingleton<ILanguagePack>(sp => sp.GetRequiredService<TPack>());

        AddHandler<TPack, IGoToDefinitionHandler>(services);
        AddHandler<TPack, IFindUsagesHandler>(services);
        AddHandler<TPack, IOutlineHandler>(services);
        AddHandler<TPack, IRenameHandler>(services);
        AddHandler<TPack, IDiagnosticsHandler>(services);
    }

    private static void AddHandler<TPack, THandler>(IServiceCollection services)
        where TPack : class, ILanguagePack
        where THandler : class
    {
        if (typeof(THandler).IsAssignableFrom(typeof(TPack)))
            services.AddSingleton(sp => (THandler)(object)sp.GetRequiredService<TPack>());
    }
}
