using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Deciding which loaded modules hold code the user wrote — the answer stepping, breakpoint
/// narration and hot reload targeting all rest on.
/// </summary>
public class UserCodeTests
{
    private static UserCodeMap Solution() => UserCodeMap.From(
    [
        @"D:\work\App\bin\Debug\net10.0\App.dll",
        @"D:\work\App.Core\bin\Debug\net10.0\App.Core.dll",
    ]);

    [Fact]
    public void AnAssemblyTheSolutionBuildsIsTheUsers()
    {
        Assert.Equal(
            UserCodeVerdict.User,
            Solution().Classify(@"D:\work\App\bin\Debug\net10.0\App.dll"));
    }

    [Fact]
    public void TheSameAssemblyRunningFromSomewhereElseIsStillTheUsers()
    {
        // The whole reason the map holds names rather than paths: a web application runs from a
        // shadow copy and a test runs from a per-run directory, and neither is where it was built.
        var map = Solution();

        Assert.Equal(
            UserCodeVerdict.User,
            map.Classify(@"C:\inetpub\site\bin\App.Core.dll"));
        Assert.Equal(
            UserCodeVerdict.User,
            map.Classify(@"C:\Users\x\AppData\Local\Temp\testrun-7f2\App.dll"));
    }

    [Fact]
    public void AModuleTheSolutionDoesNotBuildIsNotThereforeSomebodyElses()
    {
        // Absence is not evidence. The solution is not a census of the user's projects: one whose
        // design-time build failed contributes no output path, a workspace opened for a single
        // project holds only that project and its references while the process runs its siblings,
        // and a build step that renames or merges an assembly leaves a module no project claims.
        // Ruling those out would step back out of the user's own methods and mute the diagnostic
        // written to explain why.
        Assert.Equal(
            UserCodeVerdict.Unknown,
            Solution().Classify(@"D:\work\App\bin\Debug\net10.0\Newtonsoft.Json.dll"));
        Assert.True(Solution().CouldBeUserCode(@"D:\work\App\bin\Debug\net10.0\Sibling.dll"));
    }

    [Fact]
    public void AProjectWithNoBuiltOutputDoesNotTakeItsSiblingsWithIt()
    {
        // A project whose output path the workspace could not supply is dropped from the map, and
        // that has to cost only its own positive answer — not turn it, or anything else, External.
        var map = UserCodeMap.From([@"D:\work\App\bin\Debug\net10.0\App.dll", string.Empty]);

        Assert.True(map.KnowsTheSolution);
        Assert.Equal(UserCodeVerdict.User, map.Classify(@"D:\work\App\bin\Debug\net10.0\App.dll"));
        Assert.Equal(
            UserCodeVerdict.Unknown,
            map.Classify(@"D:\work\App.Core\bin\Debug\net10.0\App.Core.dll"));
    }

    [Theory]
    [InlineData(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\mscorlib.dll")]
    [InlineData(@"C:\Windows\assembly\GAC_MSIL\System.Web\4.0.0.0__b03f5f7f11d50a3a\System.Web.dll")]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.0\System.Private.CoreLib.dll")]
    [InlineData(@"C:\Users\x\.nuget\packages\serilog\4.0.0\lib\net8.0\Serilog.dll")]
    public void TheFrameworkAndTheRestoredPackagesAreSomebodyElses(string module)
    {
        // These still matter with a solution open: a self-contained application carries its own
        // runtime in the same folder as its own assemblies.
        Assert.Equal(UserCodeVerdict.External, Solution().Classify(module));
    }

    [Fact]
    public void WithNoSolutionOpenNothingIsRuledIn()
    {
        // Unknown rather than User, so nothing claims to know which assemblies are the user's —
        // that claim is what the runtime's own Just My Code would be trusted with.
        var map = UserCodeMap.None;

        Assert.False(map.KnowsTheSolution);
        Assert.Equal(UserCodeVerdict.Unknown, map.Classify(@"D:\work\App\bin\App.dll"));
        Assert.Equal(UserCodeVerdict.Unknown, map.Classify(@"C:\anywhere\Whatever.dll"));
    }

    [Fact]
    public void OnlyAKnownSolutionCanProduceAPositiveAnswer()
    {
        // What the whole feature turns on. The runtime's own Just My Code needs somewhere marked
        // as the user's or a filtered step never finds anywhere to stop, and only a solution can
        // say where that is — which is why KnowsTheSolution gates arming one at all.
        Assert.DoesNotContain(
            new[]
            {
                UserCodeMap.None.Classify(@"D:\work\App\bin\App.dll"),
                UserCodeMap.None.Classify(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\mscorlib.dll"),
            },
            v => v == UserCodeVerdict.User);
    }

    [Fact]
    public void WithNoSolutionOpenTheFrameworkIsStillRuledOut()
    {
        // Attaching to a process nobody opened a project for still gets the old behaviour rather
        // than no behaviour.
        Assert.Equal(
            UserCodeVerdict.External,
            UserCodeMap.None.Classify(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\mscorlib.dll"));
    }

    [Fact]
    public void AnUnknownModuleCountsAsTheUsers()
    {
        // The fail-open reading, which is what every caller that has to pick a side uses. Being
        // wrong this way costs a frame in a call stack; being wrong the other way is a breakpoint
        // that never binds with nothing said about why.
        Assert.True(UserCodeMap.None.CouldBeUserCode(@"D:\work\App\bin\App.dll"));
        Assert.False(UserCodeMap.None.CouldBeUserCode(
            @"C:\Windows\assembly\GAC_MSIL\System.Web\4.0.0.0__b03f5f7f11d50a3a\System.Web.dll"));
    }

    [Fact]
    public void ThePageCompilersOutputStaysSteppable()
    {
        // Named unpredictably per compilation and living below the Framework directory, so neither
        // the solution nor a directory can identify it — but it holds the user's markup and inline
        // code, and ruling it out would mean a breakpoint in a page could never bind.
        const string generated =
            @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Temporary ASP.NET Files\root\a1\b2\App_Web_x1y2z3.dll";

        Assert.Equal(UserCodeVerdict.Unknown, Solution().Classify(generated));
        Assert.True(Solution().CouldBeUserCode(generated));
    }

    [Fact]
    public void AnEmptyPathAnswersNothing()
    {
        Assert.Equal(UserCodeVerdict.Unknown, Solution().Classify(string.Empty));
    }
}
