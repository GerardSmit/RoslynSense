using System.Reflection.PortableExecutable;
using RoslynMCP.Config;
using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Choosing the right PDB for an assembly, and the choice of which file a partial type is
/// declared in.
/// </summary>
[Collection(SharedState.Name)]
public class PdbLocatorTests
{
    /// <summary>
    /// A ReadyToRun assembly carries two CodeView entries and the native one comes first. Taking
    /// it fetches megabytes of a PDB with no Source Link and no managed sequence points.
    /// </summary>
    [RequiresSharedFrameworkFact]
    public void WhenAnAssemblyHasNativeAndManagedSymbolsThenThePortableOneIsChosen()
    {
        string assembly = SharedFrameworkAssembly()!;

        using var stream = File.OpenRead(assembly);
        using var peReader = new PEReader(stream);
        var entries = peReader.ReadDebugDirectory();

        var codeView = PdbLocator.ReadPortableCodeView(peReader, entries);

        Assert.NotNull(codeView);
        Assert.EndsWith(".pdb", codeView!.Value.Path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".ni.", codeView.Value.Path, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresSharedFrameworkFact]
    public void WhenTheSymbolKeyIsBuiltThenItRepeatsTheFileNameAroundTheIdentity()
    {
        string assembly = SharedFrameworkAssembly()!;

        using var stream = File.OpenRead(assembly);
        using var peReader = new PEReader(stream);

        var codeView = PdbLocator.ReadPortableCodeView(peReader, peReader.ReadDebugDirectory());
        Assert.NotNull(codeView);

        string key = PdbLocator.SsqpKey(codeView!.Value);
        string[] parts = key.Split('/');

        Assert.Equal(3, parts.Length);
        Assert.Equal(parts[0], parts[2]);

        // A 32-character GUID and the portable sentinel, not the real age: the age indexes the
        // converted Windows PDB, which cannot be read as metadata at all.
        Assert.Matches("^[0-9a-f]{32}ffffffff$", parts[1]);
    }

    [Fact]
    public async Task WhenTheFeatureIsOffThenNoSymbolsAreFetched()
    {
        bool originalExternal = LspFeatureOptions.ExternalSource;
        bool originalServer = LspFeatureOptions.SymbolServer;
        try
        {
            LspFeatureOptions.ExternalSource = false;
            LspFeatureOptions.SymbolServer = false;

            string assembly = typeof(object).Assembly.Location;
            using var stream = File.OpenRead(assembly);
            using var peReader = new PEReader(stream);

            // No PDB ships beside the framework assemblies, and downloading is refused, so there
            // is nothing left to find.
            Assert.Null(await PdbLocator.OpenAsync(peReader, assembly, default));
        }
        finally
        {
            LspFeatureOptions.ExternalSource = originalExternal;
            LspFeatureOptions.SymbolServer = originalServer;
        }
    }

    /// <summary>
    /// A partial type's methods are spread over several files. The earliest line across all of
    /// them is whichever file happens to declare something near its top, which is arbitrary.
    /// </summary>
    [Fact]
    public void WhenNoFileIsNamedAfterTheTypeThenTheOneMostMethodsCameFromWins()
    {
        // Document 7 holds one method that starts at line 3; document 4 holds three.
        (int Document, int Line, string Name)[] points =
            [(7, 3, "/a/Helpers.cs"), (4, 40, "/a/Internals.cs"), (4, 55, "/a/Internals.cs"), (4, 91, "/a/Internals.cs")];

        var chosen = SourceLinkService.ChooseDeclarationPoint(points, singleMethod: false, "Widget");

        Assert.Equal((4, 40), chosen);
    }

    /// <summary>
    /// Most of <c>String</c>'s methods are compiled from <c>String.Manipulation.cs</c>, but a
    /// reader who pressed F12 on <c>String</c> means <c>String.cs</c>.
    /// </summary>
    [Fact]
    public void WhenAFileIsNamedAfterTheTypeThenItWinsRegardlessOfMethodCount()
    {
        (int Document, int Line, string Name)[] points =
        [
            (7, 120, "/_/src/System/String.cs"),
            (4, 40, "/_/src/System/String.Manipulation.cs"),
            (4, 55, "/_/src/System/String.Manipulation.cs"),
            (4, 91, "/_/src/System/String.Manipulation.cs"),
        ];

        var chosen = SourceLinkService.ChooseDeclarationPoint(points, singleMethod: false, "String");

        Assert.Equal((7, 120), chosen);
    }

    [Fact]
    public void WhenTheCallerAskedAboutOneMethodThenItsOwnFileIsUsed()
    {
        (int Document, int Line, string Name)[] points = [(7, 3, "/a/Other.cs")];

        Assert.Equal((7, 3), SourceLinkService.ChooseDeclarationPoint(points, singleMethod: true, "Widget"));
    }

    [Fact]
    public void WhenTheMethodCountsTieThenTheEarlierDeclarationWins()
    {
        (int Document, int Line, string Name)[] points = [(7, 30, "/a/A.cs"), (4, 12, "/a/B.cs")];

        Assert.Equal((4, 12), SourceLinkService.ChooseDeclarationPoint(points, singleMethod: false, "Widget"));
    }

    [Fact]
    public void WhenThereAreNoSequencePointsThenThereIsNoDeclarationPoint()
    {
        Assert.Null(SourceLinkService.ChooseDeclarationPoint([], singleMethod: false, "Widget"));
    }

    /// <summary>A ReadyToRun assembly from the installed shared framework, if there is one.</summary>
    internal static string? SharedFrameworkAssembly()
    {
        string candidate = Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Private.CoreLib.dll");

        return File.Exists(candidate) ? candidate : null;
    }
}

/// <summary>Skips when the test is not running on a shared-framework install.</summary>
public sealed class RequiresSharedFrameworkFactAttribute : FactAttribute
{
    public RequiresSharedFrameworkFactAttribute()
    {
        if (PdbLocatorTests.SharedFrameworkAssembly() is null)
            Skip = "No shared-framework System.Private.CoreLib.dll to read symbols from.";
    }
}
