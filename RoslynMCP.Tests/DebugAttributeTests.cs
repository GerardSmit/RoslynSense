using System.Collections.Concurrent;
using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers the <c>System.Diagnostics</c> debugger attributes on the ICorDebug engine:
/// <c>DebuggerDisplay</c>, <c>DebuggerTypeProxy</c>, <c>DebuggerBrowsable</c>, and the stepping
/// markers that make up Just My Code.
/// </summary>
/// <remarks>
/// Against a real .NET Framework target rather than a mock, because none of this can be faked
/// convincingly: the display strings are evaluated by running the target's own getters, and the
/// proxy is a real object constructed inside the debuggee.
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class DebugAttributeTests : IAsyncLifetime
{
    private FxTargetProcess? _target;
    private DebugSession? _session;

    /// <summary>
    /// Every event the session raised. The channel has one consumer by design, so a test that
    /// read it directly would steal events from the pump — and the stepping tests need to see
    /// events the pump has already taken.
    /// </summary>
    private readonly ConcurrentQueue<DebugEvent> _events = new();

    private DebugSession Session => _session ?? throw new InvalidOperationException("no session");

    public async Task InitializeAsync()
    {
        if (!FxTargetProcess.IsAvailable)
            return;

        _target = FxTargetProcess.Launch();
        _session = new DebugSession(1);

        _ = Task.Run(async () =>
        {
            await foreach (var e in _session.Events.ReadAllAsync())
                _events.Enqueue(e);
        });

        _session.Attach(
            _target.Process.Id,
            [new BreakpointSpec { FilePath = FxTargetProcess.SourcePath, Line = (uint)FxTargetProcess.BreakpointLine }],
            DebugRuntime.NetFramework);

        await WaitForAsync(DebugEventKind.Breakpoint);
    }

    /// <summary>Waits for the next event of a kind, from the events recorded since the last wait.</summary>
    private async Task<DebugEvent> WaitForAsync(DebugEventKind kind)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            while (_events.TryDequeue(out var recorded))
            {
                if (recorded.Kind == kind)
                    return recorded;
            }
            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException($"no {kind} event arrived within 30 seconds");
    }

    public Task DisposeAsync()
    {
        try { _session?.Terminate(); } catch { }
        _target?.Dispose();
        return Task.CompletedTask;
    }

    private static DebugVariable Find(IEnumerable<DebugVariable> variables, string name) =>
        variables.FirstOrDefault(v => v.Name == name)
        ?? throw new Xunit.Sdk.XunitException(
            $"'{name}' was not among [{string.Join(", ", variables.Select(v => v.Name))}]");

    // --- DebuggerDisplay ------------------------------------------------------------------------

    [RequiresNetFrameworkFact]
    public async Task WhenTypeDeclaresADisplayStringThenTheValueIsRenderedWithIt()
    {
        var order = Find(await Session.ExpandAsync(0, ""), "order");

        // "Order {Id}: {Name,nq}" — Id has no backing field, so this proves the getter ran, and
        // ',nq' proves the specifier was honoured rather than pasted into the output.
        Assert.Matches(@"^Order \d+: sample$", order.Value);
    }

    [RequiresNetFrameworkFact]
    public async Task WhenDisplayStringsAreDisabledThenTheTypeNameIsShownInstead()
    {
        Session.DisplayOptions = new DebugDisplayOptions { DebuggerDisplay = false };

        var order = Find(await Session.ExpandAsync(0, ""), "order");

        Assert.Equal("FxTarget.Order", order.Value);
    }

    [RequiresNetFrameworkFact]
    public async Task WhenAConditionComparesAValueThenTheDisplayStringDoesNotStandInForIt()
    {
        // The condition path stringifies without the display string; 'Name' must compare as its
        // own value, not as the sentence the type would like to be shown as.
        var (ok, value, error) = await Session.EvaluateAsync(0, "order.Name");

        Assert.True(ok, error);
        Assert.Equal("\"sample\"", value);
    }

    // --- DebuggerBrowsable ----------------------------------------------------------------------

    [RequiresNetFrameworkFact]
    public async Task WhenAFieldIsBrowsableNeverThenItIsHiddenButReachableThroughRawView()
    {
        var children = await Session.ExpandAsync(0, "order");

        Assert.DoesNotContain(children, v => v.Name == "_secret");
        Assert.Contains(children, v => v.Name == "Name");

        var rawView = Find(children, "Raw View");
        var raw = await Session.ExpandAsync(0, rawView.VariablesReference);

        Assert.Contains(raw, v => v.Name == "_secret");
    }

    [RequiresNetFrameworkFact]
    public async Task WhenBrowsableIsDisabledThenEveryFieldIsListed()
    {
        Session.DisplayOptions = new DebugDisplayOptions { Browsable = false };

        var children = await Session.ExpandAsync(0, "order");

        Assert.Contains(children, v => v.Name == "_secret");
    }

    // --- DebuggerTypeProxy ----------------------------------------------------------------------

    [RequiresNetFrameworkFact]
    public async Task WhenTypeDeclaresAProxyThenItsMembersAreShownInsteadOfTheFields()
    {
        var children = await Session.ExpandAsync(0, "bag");

        // BagView exposes Count, and Items is RootHidden — so the used slots appear directly and
        // the storage array does not appear at all.
        Assert.Contains(children, v => v.Name == "Count" && v.Value == "3");
        Assert.DoesNotContain(children, v => v.Name == "_slots");
        Assert.Equal(["7", "8", "9"], children.Where(v => v.Name.StartsWith('[')).Select(v => v.Value));
    }

    [RequiresNetFrameworkFact]
    public async Task WhenTheProxyIsDisabledThenTheObjectsOwnFieldsAreShown()
    {
        Session.DisplayOptions = new DebugDisplayOptions { TypeProxy = false };

        var children = await Session.ExpandAsync(0, "bag");

        Assert.Contains(children, v => v.Name == "_slots");
        Assert.Contains(children, v => v.Name == "_count" && v.Value == "3");
    }

    [RequiresNetFrameworkFact]
    public async Task WhenAProxyMemberIsExpandedThenItsPathResolvesBackThroughTheProxy()
    {
        var count = Find(await Session.ExpandAsync(0, "bag"), "Count");

        // Proxy members are addressed through a '$proxy' segment; the array does the same for its
        // Raw View, and both must survive a round trip.
        var raw = Find(await Session.ExpandAsync(0, "bag"), "Raw View");
        var fields = await Session.ExpandAsync(0, raw.VariablesReference);

        Assert.Equal("3", count.Value);
        Assert.Contains(fields, v => v.Name == "_slots");
    }

    // --- Just My Code ---------------------------------------------------------------------------

    [RequiresNetFrameworkFact]
    public async Task WhenSteppingIntoAStepThroughMethodThenTheDebuggerComesStraightBackOut()
    {
        var landed = await StepIntoTwiceAsync(justMyCode: true);

        Assert.DoesNotContain("Twice", landed.MethodName, StringComparison.Ordinal);
        Assert.Contains("Compute", landed.MethodName, StringComparison.Ordinal);
    }

    [RequiresNetFrameworkFact]
    public async Task WhenJustMyCodeIsDisabledThenSteppingEntersTheStepThroughMethod()
    {
        var landed = await StepIntoTwiceAsync(justMyCode: false);

        Assert.Contains("Twice", landed.MethodName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Steps from the breakpoint onto the guarded call, then into it — the second step is the one
    /// the attribute governs.
    /// </summary>
    private async Task<DebugEvent> StepIntoTwiceAsync(bool justMyCode)
    {
        Session.DisplayOptions = new DebugDisplayOptions { JustMyCode = justMyCode };

        var onTheCall = await StepAsync();
        Assert.Equal((uint)FxTargetProcess.GuardedCallLine, onTheCall.Line);

        return await StepAsync();
    }

    private Task<DebugEvent> StepAsync()
    {
        Session.Step(StepKind.Into);
        return WaitForAsync(DebugEventKind.Step);
    }
}

/// <summary>
/// The parts of the attribute support that are pure parsing, and therefore testable without a
/// debuggee: the display format grammar and the type names a proxy attribute stores.
/// </summary>
public class DebuggerDisplayFormatTests
{
    [Fact]
    public void WhenFormatMixesLiteralsAndExpressionsThenBothAreReturnedInOrder()
    {
        var parts = DebuggerDisplayFormat.Parse("Order {Id}: {Name}");

        Assert.Collection(parts,
            p => Assert.Equal(new DisplayPart("Order ", false, false), p),
            p => Assert.Equal(new DisplayPart("Id", true, false), p),
            p => Assert.Equal(new DisplayPart(": ", false, false), p),
            p => Assert.Equal(new DisplayPart("Name", true, false), p));
    }

    [Fact]
    public void WhenExpressionCarriesNoQuotesThenTheSpecifierIsSeparatedFromIt()
    {
        var parts = DebuggerDisplayFormat.Parse("{Name,nq}");

        Assert.Equal(new DisplayPart("Name", true, true), Assert.Single(parts));
    }

    [Fact]
    public void WhenExpressionCarriesAnUnsupportedSpecifierThenItIsDroppedButTheExpressionSurvives()
    {
        var parts = DebuggerDisplayFormat.Parse("{Count,d}");

        Assert.Equal(new DisplayPart("Count", true, false), Assert.Single(parts));
    }

    [Fact]
    public void WhenExpressionIndexesThenTheCommaInsideBracketsIsNotASpecifier()
    {
        var parts = DebuggerDisplayFormat.Parse("{Items[0].Name}");

        Assert.Equal(new DisplayPart("Items[0].Name", true, false), Assert.Single(parts));
    }

    [Fact]
    public void WhenBracesAreDoubledThenTheyAreLiteral()
    {
        var parts = DebuggerDisplayFormat.Parse("{{literal}}");

        Assert.Equal(new DisplayPart("{literal}", false, false), Assert.Single(parts));
    }

    [Fact]
    public void WhenABraceIsNeverClosedThenTheRestIsTreatedAsText()
    {
        var parts = DebuggerDisplayFormat.Parse("count = {Count");

        // Better a visibly wrong display string than a value that silently renders as nothing.
        Assert.Equal(new DisplayPart("count = {Count", false, false), Assert.Single(parts));
    }

    [Theory]
    [InlineData("Ns.View, MyAsm, Version=1.0.0.0, Culture=neutral", "Ns.View")]
    [InlineData("Ns.Mine`1+View", "Ns.Mine`1+View")]
    [InlineData("Ns.Dict`2[[System.Int32, mscorlib],[System.String, mscorlib]]", "Ns.Dict`2")]
    [InlineData("  Ns.View  ", "Ns.View")]
    public void WhenTypeNameIsStoredThenAssemblyAndGenericArgumentsAreStripped(string stored, string expected) =>
        Assert.Equal(expected, DebuggerAttributes.NormalizeTypeName(stored));
}
