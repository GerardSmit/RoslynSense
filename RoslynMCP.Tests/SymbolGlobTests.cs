using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The symbol load policy: which modules get their PDBs opened under the
/// <c>debugger.symbolInclude</c> / <c>debugger.symbolExclude</c> globs.
/// </summary>
public class SymbolGlobTests
{
    private const string SitePage =
        @"C:\Users\u\AppData\Local\Temp\Temporary ASP.NET Files\root\32274e7c\App_Web_kdcxh2h1.dll";
    private const string SiteBin = @"C:\src\WebFormsApp\bin\WebFormsApp.dll";

    [Fact]
    public void WithNoGlobsEveryModuleLoadsSymbols()
    {
        var options = new DebugDisplayOptions();

        Assert.True(SymbolGlobs.WantsSymbols(options, SitePage));
        Assert.True(SymbolGlobs.WantsSymbols(options, SiteBin));
    }

    [Fact]
    public void AGlobWithoutASeparatorMatchesTheFileName()
    {
        var options = new DebugDisplayOptions { SymbolExclude = ["App_Web_*.dll"] };

        Assert.False(SymbolGlobs.WantsSymbols(options, SitePage));
        Assert.True(SymbolGlobs.WantsSymbols(options, SiteBin));
    }

    [Fact]
    public void AGlobWithASeparatorMatchesTheFullPath()
    {
        var options = new DebugDisplayOptions { SymbolExclude = [@"**\Temporary ASP.NET Files\**"] };

        Assert.False(SymbolGlobs.WantsSymbols(options, SitePage));
        Assert.True(SymbolGlobs.WantsSymbols(options, SiteBin));
    }

    [Fact]
    public void ANonEmptyIncludeListLoadsOnlyWhatItNames()
    {
        var options = new DebugDisplayOptions { SymbolInclude = ["WebFormsApp.dll"] };

        Assert.True(SymbolGlobs.WantsSymbols(options, SiteBin));
        Assert.False(SymbolGlobs.WantsSymbols(options, SitePage));
    }

    [Fact]
    public void ExcludeWinsOverInclude()
    {
        var options = new DebugDisplayOptions
        {
            SymbolInclude = ["*.dll"],
            SymbolExclude = ["WebFormsApp.dll"],
        };

        Assert.False(SymbolGlobs.WantsSymbols(options, SiteBin));
        Assert.True(SymbolGlobs.WantsSymbols(options, SitePage));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var options = new DebugDisplayOptions { SymbolExclude = ["APP_WEB_*.DLL"] };

        Assert.False(SymbolGlobs.WantsSymbols(options, SitePage));
    }

    [Fact]
    public void ASingleStarDoesNotCrossDirectories()
    {
        var options = new DebugDisplayOptions { SymbolExclude = [@"C:\src\*.dll"] };

        Assert.True(SymbolGlobs.WantsSymbols(options, SiteBin));
        Assert.False(SymbolGlobs.WantsSymbols(options, @"C:\src\Direct.dll"));
    }

    [Fact]
    public void ForwardAndBackwardSlashesAreInterchangeable()
    {
        var options = new DebugDisplayOptions { SymbolExclude = ["**/bin/**"] };

        Assert.False(SymbolGlobs.WantsSymbols(options, SiteBin));
    }
}
