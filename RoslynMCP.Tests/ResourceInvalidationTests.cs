using Microsoft.CodeAnalysis;
using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Resources;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// A <c>.resx</c> written outside the editor, and the three separate paths by which that reaches
/// the catalog.
/// </summary>
/// <remarks>
/// They are tested apart because they serve different front ends and either one alone leaves the
/// other stale. The editor's <c>didChangeWatchedFiles</c> is the only notification an LSP session
/// gets, and an MCP session never sees one — its whole freshness mechanism is the
/// <see cref="FileSystemWatcher"/> <see cref="ProjectIndexCacheService"/> sets up when it first
/// answers. Wiring one and calling it done is how a feature ends up live in the editor and wrong
/// in the tool surface.
/// </remarks>
[Collection(SharedState.Name)]
public class ResourceInvalidationTests
{
    private const string AddedFamily = "Widget.ascx";

    [Fact]
    public void ThePacksOwnInvalidateIsWhatRefreshesADirectoryAlreadyWalked()
    {
        var pack = new ResourcesLanguage(EffectiveSettings.Resolve([], null, out _));
        using var site = new TempSite();

        Assert.Empty(ResourceCatalogService.Get(site.Root).Families);

        string added = site.Add("App_LocalResources", "Widget.ascx.resx");

        // Nothing has said the directory moved, so the catalog is still answering from the walk it
        // did above — which is the point of caching it.
        Assert.Empty(ResourceCatalogService.Get(site.Root).Families);

        Assert.True(pack.Invalidate(added, WatchedFileChange.Created));
        Assert.Equal(AddedFamily, Assert.Single(ResourceCatalogService.Get(site.Root).Families).BaseName);

        // Every path is offered to every pack, so declining the ones that are not its own is part
        // of the contract rather than an optimization.
        Assert.False(pack.Invalidate(Path.ChangeExtension(added, ".cs"), WatchedFileChange.Created));
    }

    [Fact]
    public async Task TheEditorsWatchedFileNotificationReachesThePack()
    {
        // The dispatch runs over the registered packs, and calling the handler directly rather
        // than through a server means no host has built a registry, so this stands in for one.
        Publish();
        using var site = new TempSite();

        Assert.Empty(ResourceCatalogService.Get(site.Root).Families);
        string added = site.Add("App_LocalResources", "Widget.ascx.resx");
        Assert.Empty(ResourceCatalogService.Get(site.Root).Families);

        var outcome = await WatchedFilesHandler.ProcessAsync(
            [new FileEvent(LspConverters.PathToUri(added), FileChangeType.Created)], default);

        // A resource file shapes no project and compiles into no snapshot: the batch claims it and
        // nothing is reloaded or evicted for it.
        Assert.False(outcome.ReloadedWorkspace);
        Assert.Empty(outcome.EvictedProjects);
        Assert.Contains(added, outcome.InvalidatedMarkup!, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(AddedFamily, Assert.Single(ResourceCatalogService.Get(site.Root).Families).BaseName);
    }

    [Fact]
    public async Task TheProjectIndexCachesOwnWatcherIsAllAnMcpSessionHas()
    {
        using var site = new TempSite();
        var project = site.Project();

        // Answering once is also what installs the watcher, which is why the stale read has to
        // come first here rather than being asserted the way the other two do it.
        var before = await ProjectIndexCacheService.GetResourceCatalogAsync(
            project, ResourceDiscoveryOptions.Default, default);
        Assert.Empty(before.Families);

        site.Add("App_LocalResources", "Widget.ascx.resx");

        var catalog = await SettledAsync(project);
        Assert.Equal(AddedFamily, Assert.Single(catalog.Families).BaseName);
    }

    /// <summary>The catalog once the file-system notification has landed. Polled because the
    /// notification is asynchronous and there is nothing to await on it; locally it arrives within
    /// a couple of hundred milliseconds.</summary>
    private static async Task<ResourceCatalog> SettledAsync(Project project)
    {
        var catalog = ResourceCatalog.Empty;

        for (int attempt = 0; attempt < 100 && catalog.Families.IsEmpty; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(100);

            catalog = await ProjectIndexCacheService.GetResourceCatalogAsync(
                project, ResourceDiscoveryOptions.Default, default);
        }

        Assert.False(
            catalog.Families.IsEmpty,
            "The file-system watcher never reported the new .resx to the project index cache.");

        return catalog;
    }

    private static void Publish() =>
        new LanguageRegistry(
            LanguagePackRegistration.Create(
                EffectiveSettings.Resolve([], null, out _), new MarkdownFormatter()))
            .Publish();

    /// <summary>
    /// A directory of its own for each test, so a catalog can be shown to be stale.
    /// </summary>
    /// <remarks>
    /// Not the shared fixture: it has a watcher of its own by the time any of this runs, and a
    /// notification arriving from it would make each of these tests pass without its own wiring
    /// doing anything.
    /// </remarks>
    private sealed class TempSite : IDisposable
    {
        public TempSite()
        {
            Root = Path.Combine(Path.GetTempPath(), "roslynsense-resources-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        /// <summary>Copied from the fixture rather than written here: what the reader returns for a
        /// file that is not really ResX is an empty key table, which reads the same as a stale
        /// catalog.</summary>
        public string Add(params string[] parts)
        {
            string path = Path.Combine([Root, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(FixturePaths.LocalizedResxFile, path);
            return path;
        }

        /// <summary>A project whose file lives here and whose documents are beside the point: the
        /// catalog is a function of the directory, and a <c>.resx</c> is not a Roslyn document.</summary>
        public Project Project()
        {
            var workspace = new AdhocWorkspace();
            var id = ProjectId.CreateNewId();

            return workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    id, VersionStamp.Create(), "Widgets", "Widgets", LanguageNames.CSharp,
                    filePath: Path.Combine(Root, "Widgets.csproj")))
                .GetProject(id)!;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
