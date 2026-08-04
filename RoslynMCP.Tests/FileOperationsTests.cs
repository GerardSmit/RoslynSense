using System.Xml.Linq;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>workspace/willRenameFiles: renaming a file renames the type it declares, and the
/// references that go with it.</summary>
[Collection(SharedState.Name)]
public class FileOperationsTests : IAsyncLifetime
{
    private string _typePath = "";
    private string _userPath = "";

    public async Task InitializeAsync()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        _typePath = Path.Combine(FixturePaths.SampleProjectDir, $"RenameSubject{suffix}.cs");
        _userPath = Path.Combine(FixturePaths.SampleProjectDir, $"RenameUser{suffix}.cs");

        await File.WriteAllTextAsync(_typePath, $$"""
            namespace SampleProject;

            public class RenameSubject{{suffix}}
            {
                public int Value => 1;
            }
            """);
        await File.WriteAllTextAsync(_userPath, $$"""
            namespace SampleProject;

            public class RenameUser{{suffix}}
            {
                public int Use() => new RenameSubject{{suffix}}().Value;
            }
            """);

        await WorkspaceService.EvictAllAsync();
    }

    public async Task DisposeAsync()
    {
        File.Delete(_typePath);
        File.Delete(_userPath);
        await WorkspaceService.EvictAllAsync();
    }

    [Fact]
    public async Task RenamingAFileRenamesItsTypeAndEveryReference()
    {
        string newPath = Path.Combine(
            FixturePaths.SampleProjectDir,
            "Renamed" + Path.GetFileName(_typePath));

        var edit = await FileOperationsHandler.WillRenameAsync(
            new RenameFilesParams([new FileRename(
                LspConverters.PathToUri(_typePath), LspConverters.PathToUri(newPath))]),
            default);

        Assert.NotNull(edit);
        // Nothing moves on the C# path, so the answer stays in the simple form every client
        // understands.
        Assert.Null(edit!.DocumentChanges);
        // The declaration and the use site in the other file both move.
        Assert.Contains(edit.Changes, c => c.Key.EndsWith(Path.GetFileName(_typePath), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(edit.Changes, c => c.Key.EndsWith(Path.GetFileName(_userPath), StringComparison.OrdinalIgnoreCase));
        // Roslyn emits minimal diffs, so an individual edit's text can be a fragment like "d" —
        // asserting on it would test the differ, not us. Our contract is that both the
        // declaration and the reference get edits.
        Assert.All(edit.Changes.Values, edits => Assert.NotEmpty(edits));
    }

    [Fact]
    public async Task RenamingToAnInvalidIdentifierProducesNoEdit()
    {
        string newPath = Path.Combine(FixturePaths.SampleProjectDir, "not-an-identifier.cs");

        var edit = await FileOperationsHandler.WillRenameAsync(
            new RenameFilesParams([new FileRename(
                LspConverters.PathToUri(_typePath), LspConverters.PathToUri(newPath))]),
            default);

        Assert.Null(edit);
    }

    [Fact]
    public async Task RenamingAFileWhoseNameNeverMatchedATypeIsLeftAlone()
    {
        // Nothing in the project declares a type called Calculator2, so there is nothing to
        // rename — guessing would rewrite code the user did not ask about.
        var edit = await FileOperationsHandler.WillRenameAsync(
            new RenameFilesParams([new FileRename(
                LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, "Services.cs")),
                LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, "Calculator2.cs")))]),
            default);

        Assert.Null(edit);
    }

    [Fact]
    public async Task NonSourceFilesAreIgnored()
    {
        var edit = await FileOperationsHandler.WillRenameAsync(
            new RenameFilesParams([new FileRename(
                LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, "readme.md")),
                LspConverters.PathToUri(Path.Combine(FixturePaths.SampleProjectDir, "guide.md")))]),
            default);

        Assert.Null(edit);
    }

    [Fact]
    public async Task RenamingMarkupCarriesItsCodeBehindAndDesignerWithIt()
    {
        // A page is three files pretending to be one. Calling the handler directly means no host
        // has built a registry, so this stands in for one — without the pack an .aspx is a file
        // the C# path declines and nothing happens at all.
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        string oldPath = FixturePaths.DesignerAspxFile;
        string newPath = Path.Combine(FixturePaths.AspxProjectDir, "Renamed.aspx");

        var edit = await FileOperationsHandler.WillRenameAsync(
            new RenameFilesParams([new FileRename(
                LspConverters.PathToUri(oldPath), LspConverters.PathToUri(newPath))]),
            default);

        Assert.NotNull(edit);

        // Only documentChanges can carry a file move, so the answer has to be in that form.
        var moves = Assert.IsType<object[]>(edit!.DocumentChanges).OfType<RenameFile>().ToList();
        Assert.Equal(2, moves.Count);
        Assert.Contains(moves, m =>
            m.OldUri.EndsWith("/Designer.aspx.cs", StringComparison.Ordinal) &&
            m.NewUri.EndsWith("/Renamed.aspx.cs", StringComparison.Ordinal));
        Assert.Contains(moves, m =>
            m.OldUri.EndsWith("/Designer.aspx.designer.cs", StringComparison.Ordinal) &&
            m.NewUri.EndsWith("/Renamed.aspx.designer.cs", StringComparison.Ordinal));

        // The text edits come first: they are keyed on the paths the files still have.
        var ordered = edit.DocumentChanges!;
        Assert.True(
            Array.FindLastIndex(ordered, o => o is TextDocumentEdit) <
            Array.FindIndex(ordered, o => o is RenameFile));

        var markup = edit.Changes[LspConverters.PathToUri(oldPath)];
        // The directive stops naming a file that has moved, and stops naming a class that has
        // been renamed — with the namespace in front of it left where it was.
        Assert.Contains(markup, e => e.NewText == "Renamed.aspx.cs");
        Assert.Contains(markup, e => e.NewText == "AspxProject.RenamedPage");

        // DesignerPage was named after the file, so it follows it, and Roslyn rewrites the
        // declaration in both halves of the partial class.
        Assert.Contains(edit.Changes, c =>
            c.Key.EndsWith("/Designer.aspx.cs", StringComparison.Ordinal));
        Assert.Contains(edit.Changes, c =>
            c.Key.EndsWith("/Designer.aspx.designer.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreatingMarkupDropsAnyParseStillMemoizedForThatPath()
    {
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        string created = Path.Combine(
            FixturePaths.AspxProjectDir, $"Created{Guid.NewGuid():N}.aspx");
        await File.WriteAllTextAsync(created, """
            <%@ Page Language="C#" %>
            <html>
            <body>
                <asp:Label ID="lblCreated" runat="server" Text="created" />
            </body>
            </html>
            """);
        try
        {
            var before = await AspxDocumentService.GetAsync(created, default);
            Assert.NotNull(before);
            // Parsing is memoized, so this instance is what a later request is handed back — which
            // is what makes the assertion after didCreate say anything at all.
            Assert.Same(before, await AspxDocumentService.GetAsync(created, default));

            var untouched = await AspxDocumentService.GetAsync(FixturePaths.DefaultAspxFile, default);
            Assert.NotNull(untouched);

            await FileOperationsHandler.DidCreateAsync(
                new CreateFilesParams([new FileCreate(LspConverters.PathToUri(created))]),
                default);

            // A file created at a path one was deleted from is a different file wearing the same
            // name, and the editor would otherwise be answered from the parse of the file that is
            // gone.
            Assert.NotSame(before, await AspxDocumentService.GetAsync(created, default));

            // Only that path. Dropping every entry would cost the whole solution's markup a
            // reparse because one file appeared, and would hide a per-path drop that never ran.
            Assert.Same(
                untouched,
                await AspxDocumentService.GetAsync(FixturePaths.DefaultAspxFile, default));
        }
        finally
        {
            File.Delete(created);
        }
    }

    [Fact]
    public async Task DeletingMarkupDropsItsProjectItemAndNotItsSiblings()
    {
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        // A project of its own, listing its files the way a legacy WebForms project does: an item
        // has to be there for its removal to be visible, and the AspxProject fixture is SDK-style,
        // where nothing is listed at all.
        string directory = Path.Combine(Path.GetTempPath(), $"markup-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string project = Path.Combine(directory, "Legacy.csproj");
            string markup = Path.Combine(directory, "Page.aspx");
            string codeBehind = markup + ".cs";
            string designer = markup + ".designer.cs";

            await File.WriteAllTextAsync(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Content Include="Page.aspx" />
                    <Compile Include="Page.aspx.cs" />
                    <Compile Include="Page.aspx.designer.cs" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(markup, """
                <%@ Page Language="C#" CodeBehind="Page.aspx.cs" Inherits="Legacy.PagePage" %>
                """);
            await File.WriteAllTextAsync(codeBehind,
                "namespace Legacy; public partial class PagePage { }");
            await File.WriteAllTextAsync(designer,
                "namespace Legacy; public partial class PagePage { }");

            // didDelete arrives after the fact: the editor has already taken the file away.
            File.Delete(markup);

            await FileOperationsHandler.DidDeleteAsync(
                new DeleteFilesParams([new FileDelete(LspConverters.PathToUri(markup))]),
                default);

            var items = XDocument.Load(project).Descendants()
                .Where(e => e.Name.LocalName is "Content" or "Compile")
                .Select(e => e.Attribute("Include")?.Value)
                .ToList();

            // A project still listing a page that is gone does not build.
            Assert.DoesNotContain("Page.aspx", items);

            // The code-behind and designer were not what the user deleted, so neither their items
            // nor the files themselves may follow the markup out — undoing the delete would not
            // put them back.
            Assert.Contains("Page.aspx.cs", items);
            Assert.Contains("Page.aspx.designer.cs", items);
            Assert.True(File.Exists(codeBehind));
            Assert.True(File.Exists(designer));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MovingMarkupToAnotherFolderCarriesItsProjectItemsWithIt()
    {
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        // Explicit items, the way a legacy WebForms project lists its pages. The AspxProject
        // fixture globs instead, so an item could not follow a move there whatever the code did.
        string directory = Path.Combine(Path.GetTempPath(), $"markup-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string project = Path.Combine(directory, "Legacy.csproj");
            string markup = Path.Combine(directory, "Page.aspx");

            await File.WriteAllTextAsync(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Content Include="Page.aspx" />
                    <Compile Include="Page.aspx.cs">
                      <DependentUpon>Page.aspx</DependentUpon>
                    </Compile>
                    <Compile Include="Page.aspx.designer.cs">
                      <DependentUpon>Page.aspx</DependentUpon>
                    </Compile>
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(markup, """
                <%@ Page Language="C#" CodeBehind="Page.aspx.cs" Inherits="Legacy.PagePage" %>
                """);
            await File.WriteAllTextAsync(markup + ".cs",
                "namespace Legacy; public partial class PagePage { }");
            await File.WriteAllTextAsync(markup + ".designer.cs",
                "namespace Legacy; public partial class PagePage { }");

            string folder = Path.Combine(directory, "Pages");
            Directory.CreateDirectory(folder);
            string moved = Path.Combine(folder, "Page.aspx");

            var edit = await FileOperationsHandler.WillRenameAsync(
                new RenameFilesParams([new FileRename(
                    LspConverters.PathToUri(markup), LspConverters.PathToUri(moved))]),
                default);

            // The siblings move with the page, and their items have to move with them.
            Assert.NotNull(edit);
            Assert.Equal(2, Assert.IsType<object[]>(edit!.DocumentChanges).OfType<RenameFile>().Count());

            var items = XDocument.Load(project).Descendants()
                .Where(e => e.Name.LocalName is "Content" or "Compile")
                .ToList();
            var includes = items.Select(e => e.Attribute("Include")?.Value).ToList();

            // A project pointing at the folder the page left does not build.
            Assert.Contains(Path.Combine("Pages", "Page.aspx"), includes);
            Assert.Contains(Path.Combine("Pages", "Page.aspx.cs"), includes);
            Assert.Contains(Path.Combine("Pages", "Page.aspx.designer.cs"), includes);
            Assert.DoesNotContain("Page.aspx", includes);

            // DependentUpon is relative to the item's own folder, and all three landed in the
            // same one, so the nesting is spelled exactly as it was before the move.
            Assert.All(
                items.Where(e => e.Name.LocalName == "Compile"),
                e => Assert.Equal("Page.aspx", e.Element(e.Name.Namespace + "DependentUpon")?.Value));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RenamingAPageRepointsEveryItemThatNamedIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"item-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string project = Path.Combine(directory, "Legacy.csproj");
            await File.WriteAllTextAsync(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Content Include="Default.aspx" />
                    <Compile Include="Default.aspx.cs">
                      <DependentUpon>Default.aspx</DependentUpon>
                    </Compile>
                    <Compile Include="Default.aspx.designer.cs">
                      <DependentUpon>Default.aspx</DependentUpon>
                    </Compile>
                    <Content Include="MyDefault.aspx" />
                  </ItemGroup>
                </Project>
                """);

            string markup = Path.Combine(directory, "Default.aspx");
            string renamed = Path.Combine(directory, "Home.aspx");

            // The order the markup pack uses: the siblings first, then the page whose name the
            // metadata on those siblings spells out.
            await ProjectMutationService.RenameFileItemAsync(markup + ".cs", renamed + ".cs");
            await ProjectMutationService.RenameFileItemAsync(
                markup + ".designer.cs", renamed + ".designer.cs");
            await ProjectMutationService.RenameFileItemAsync(markup, renamed);

            var items = XDocument.Load(project).Descendants()
                .Where(e => e.Name.LocalName is "Content" or "Compile")
                .ToList();
            var includes = items.Select(e => e.Attribute("Include")?.Value).ToList();

            Assert.Contains("Home.aspx", includes);
            Assert.Contains("Home.aspx.cs", includes);
            Assert.Contains("Home.aspx.designer.cs", includes);

            // A path is matched as a path, not as text: MyDefault.aspx ends with the old name
            // without being it, and rewriting it would break a page nobody touched.
            Assert.Contains("MyDefault.aspx", includes);

            Assert.All(
                items.Where(e => e.Name.LocalName == "Compile"),
                e => Assert.Equal("Home.aspx", e.Element(e.Name.Namespace + "DependentUpon")?.Value));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RenamingAFolderCarriesEveryItemUnderneathIt()
    {
        // A legacy project lists each page individually, so a folder rename that only fixed the
        // folder's own item — or no item at all — leaves every page under it naming a path that
        // is gone, and the next build fails on files nobody touched.
        string directory = Path.Combine(Path.GetTempPath(), $"item-folder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "Pages"));
        try
        {
            string project = Path.Combine(directory, "Legacy.csproj");
            await File.WriteAllTextAsync(project, """
                <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <ItemGroup>
                    <Content Include="Pages\Default.aspx" />
                    <Compile Include="Pages\Default.aspx.cs">
                      <DependentUpon>Default.aspx</DependentUpon>
                    </Compile>
                    <Content Include="PagesArchive\Old.aspx" />
                  </ItemGroup>
                </Project>
                """);

            string source = Path.Combine(directory, "Pages");
            string renamed = Path.Combine(directory, "Views");
            Directory.Move(source, renamed);

            await ProjectMutationService.RenameFileItemAsync(source, renamed);

            var items = XDocument.Load(project).Descendants()
                .Where(e => e.Name.LocalName is "Content" or "Compile")
                .ToList();
            var includes = items.Select(e => e.Attribute("Include")?.Value).ToList();

            Assert.Contains(@"Views\Default.aspx", includes);
            Assert.Contains(@"Views\Default.aspx.cs", includes);

            // A sibling folder whose name merely starts with the renamed one is not underneath it.
            Assert.Contains(@"PagesArchive\Old.aspx", includes);

            // DependentUpon points at a sibling, so the folder moving does not change it.
            Assert.Equal(
                "Default.aspx",
                items.Single(e => e.Name.LocalName == "Compile")
                    .Element(items[0].Name.Namespace + "DependentUpon")?.Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RenamingAFileAProjectNeverListedLeavesItAlone()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"item-glob-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string project = Path.Combine(directory, "Globbed.csproj");
            string text = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """;
            await File.WriteAllTextAsync(project, text);

            await ProjectMutationService.RenameFileItemAsync(
                Path.Combine(directory, "Thing.cs"), Path.Combine(directory, "Other.cs"));

            // An SDK-style project globs its files, so there is nothing to move and no reason to
            // touch the file — rewriting it would only churn the user's diff.
            Assert.Equal(text, await File.ReadAllTextAsync(project));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeletingMarkupLeavesItsSiblingsAlone()
    {
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        // The editor deleted exactly what the user selected. Undoing that delete would not put
        // the code-behind back, so nothing here may remove it.
        await FileOperationsHandler.DidDeleteAsync(
            new DeleteFilesParams([
                new FileDelete(LspConverters.PathToUri(FixturePaths.DesignerAspxFile)),
            ]),
            default);

        Assert.True(File.Exists(FixturePaths.DesignerAspxFile));
        Assert.True(File.Exists(FixturePaths.DesignerAspxFile + ".cs"));
        Assert.True(File.Exists(FixturePaths.DesignerAspxDesignerFile));
    }
}
