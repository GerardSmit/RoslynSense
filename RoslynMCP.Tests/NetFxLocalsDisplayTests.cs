using System.Diagnostics;
using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers what the locals of a 32-bit .NET Framework target actually look like — the case the
/// cross-bitness worker exists for, and the one where a rendering bug leaves the user staring at
/// "Count = {Count: the evaluated member threw an exception}" and bare "System.Nullable`1" rows.
/// </summary>
/// <remarks>
/// Each expectation matches what Visual Studio shows for the same local: a List's display string
/// carries the real count, expanding it shows elements rather than internals, and a nullable is
/// its value or "null" — never its type name.
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class NetFxLocalsDisplayTests(NetFxLocalsDisplayTests.StoppedTargetFixture fixture)
    : IClassFixture<NetFxLocalsDisplayTests.StoppedTargetFixture>
{
    [RequiresX86WorkerFact]
    public async Task WhenAListLocalIsShownThenItsDisplayStringHasTheRealCount()
    {
        var locals = await fixture.LocalsAsync();

        var list = Assert.Single(locals, v => v.Name == "orderIds");
        Assert.Equal("Count = 3", list.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAListLocalIsExpandedThenItsElementsAreShown()
    {
        var locals = await fixture.LocalsAsync();
        var list = Assert.Single(locals, v => v.Name == "orderIds");
        Assert.False(string.IsNullOrEmpty(list.VariablesReference), "the list is not expandable at all");

        var children = await fixture.ExpandAsync(list.VariablesReference);

        // The DebuggerTypeProxy view: elements first, not _items/_size internals.
        Assert.Equal("7", Assert.Single(children, c => c.Name == "[0]").Value);
        Assert.Equal("8", Assert.Single(children, c => c.Name == "[1]").Value);
        Assert.Equal("9", Assert.Single(children, c => c.Name == "[2]").Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenANullableLocalHasAValueThenTheValueIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var nullable = Assert.Single(locals, v => v.Name == "someValue");
        Assert.Equal("42", nullable.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenANullableLocalIsEmptyThenNullIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var nullable = Assert.Single(locals, v => v.Name == "noValue");
        Assert.Equal("null", nullable.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenANullableLocalHasAValueThenEvaluatingItGivesTheValue()
    {
        var (ok, value, error) = await fixture.EvaluateAsync("someValue");

        Assert.True(ok, error);
        Assert.Equal("42", value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAListCountIsEvaluatedThenItGivesTheCount()
    {
        var (ok, value, error) = await fixture.EvaluateAsync("orderIds.Count");

        Assert.True(ok, error);
        Assert.Equal("3", value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAnArrayLocalIsShownThenItsElementTypeAndLengthAreShown()
    {
        var locals = await fixture.LocalsAsync();

        var array = Assert.Single(locals, v => v.Name == "scores");
        Assert.Equal("int[2]", array.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAnArrayLocalIsExpandedThenItsElementsAreShown()
    {
        var locals = await fixture.LocalsAsync();
        var array = Assert.Single(locals, v => v.Name == "scores");
        Assert.False(string.IsNullOrEmpty(array.VariablesReference), "the array is not expandable at all");

        var children = await fixture.ExpandAsync(array.VariablesReference);

        Assert.Equal("5", Assert.Single(children, c => c.Name == "[0]").Value);
        Assert.Equal("6", Assert.Single(children, c => c.Name == "[1]").Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenADictionaryLocalIsShownThenItsDisplayStringHasTheRealCount()
    {
        var locals = await fixture.LocalsAsync();

        var dictionary = Assert.Single(locals, v => v.Name == "pairs");
        Assert.Equal("Count = 1", dictionary.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenADictionaryEntryIsExpandedThenItsKeyAndValueAreShown()
    {
        var locals = await fixture.LocalsAsync();
        var dictionary = Assert.Single(locals, v => v.Name == "pairs");
        Assert.False(string.IsNullOrEmpty(dictionary.VariablesReference), "the dictionary is not expandable at all");

        // The proxy view: one KeyValuePair entry, not buckets/entries internals.
        var entries = await fixture.ExpandAsync(dictionary.VariablesReference);
        var entry = Assert.Single(entries, c => c.Name == "[0]");
        Assert.False(string.IsNullOrEmpty(entry.VariablesReference), "the entry is not expandable at all");

        var members = await fixture.ExpandAsync(entry.VariablesReference);

        Assert.Equal("\"a\"", Assert.Single(members, m => m.Name == "key").Value);
        Assert.Equal("1", Assert.Single(members, m => m.Name == "value").Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAnEnumLocalIsShownThenItsMemberNameIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var day = Assert.Single(locals, v => v.Name == "day");
        Assert.Equal("Wednesday", day.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAFlagsEnumLocalIsShownThenTheCombinationIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var flavors = Assert.Single(locals, v => v.Name == "flavors");
        Assert.Equal("Sweet | Salty", flavors.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenADecimalLocalIsShownThenItsValueIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var price = Assert.Single(locals, v => v.Name == "price");
        Assert.Equal("19.95", price.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenADateTimeLocalIsShownThenItsDateTextIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var when = Assert.Single(locals, v => v.Name == "when");
        Assert.Equal("01/02/2020 03:04:05", when.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenATimeSpanLocalIsShownThenItsDurationIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var wait = Assert.Single(locals, v => v.Name == "wait");
        Assert.Equal("00:05:00", wait.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAGuidLocalIsShownThenItsTextIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var id = Assert.Single(locals, v => v.Name == "id");
        Assert.Equal("11111111-2222-3333-4444-555555555555", id.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenACharLocalIsShownThenItsLiteralIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var letter = Assert.Single(locals, v => v.Name == "letter");
        Assert.Equal("'x'", letter.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenABoxedPrimitiveLocalIsShownThenItsValueIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var boxed = Assert.Single(locals, v => v.Name == "boxed");
        Assert.Equal("42", boxed.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAStructOverridesToStringThenItsTextIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var point = Assert.Single(locals, v => v.Name == "point");
        Assert.Equal("(3, 4)", point.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenStoppedInAnInstanceMethodThenThisIsListed()
    {
        var locals = await fixture.LocalsAsync();

        var self = Assert.Single(locals, v => v.Name == "this");
        Assert.Contains("Inspector", self.Type, StringComparison.Ordinal);
    }

    [RequiresX86WorkerFact]
    public async Task WhenStoppedInAnInstanceMethodThenArgumentsKeepTheirOwnNames()
    {
        var locals = await fixture.LocalsAsync();

        // Before the off-by-one fix, "label" showed the this-object and "factor" showed "active".
        Assert.Equal("\"active\"", Assert.Single(locals, v => v.Name == "label").Value);
        Assert.Equal("3", Assert.Single(locals, v => v.Name == "factor").Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenThisIsExpandedThenItsFieldsAreShown()
    {
        var members = await fixture.ExpandAsync("this");

        Assert.Equal("7", Assert.Single(members, m => m.Name == "_seed").Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAComputedPropertyExistsThenItIsShownInTheExpansion()
    {
        var members = await fixture.ExpandAsync("this");

        Assert.Equal("14", Assert.Single(members, m => m.Name == "Doubled").Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAPropertyIsMarkedNeverBrowsableThenItIsHidden()
    {
        var members = await fixture.ExpandAsync("this");

        Assert.DoesNotContain(members, m => m.Name == "Hidden");
    }

    [RequiresX86WorkerFact]
    public async Task WhenAPropertyGetterThrowsThenTheExceptionIsShownInline()
    {
        var members = await fixture.ExpandAsync("this");

        var boom = Assert.Single(members, m => m.Name == "Boom");
        Assert.Contains("kaboom", boom.Value, StringComparison.Ordinal);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAnExceptionLocalIsExpandedThenItsInheritedMessageIsShown()
    {
        var locals = await fixture.LocalsAsync();
        var error = Assert.Single(locals, v => v.Name == "error");
        Assert.False(string.IsNullOrEmpty(error.VariablesReference), "the exception is not expandable at all");

        var members = await fixture.ExpandAsync(error.VariablesReference);

        // _message is declared on System.Exception, one assembly away from the leaf type.
        Assert.Equal("\"bad\"", Assert.Single(members, m => m.Name == "_message").Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAStringHasControlCharactersThenTheyAreEscaped()
    {
        var locals = await fixture.LocalsAsync();

        var multiline = Assert.Single(locals, v => v.Name == "multiline");
        Assert.Equal("\"line1\\r\\nline2\"", multiline.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAStringIsHugeThenTheDisplayIsTruncated()
    {
        var locals = await fixture.LocalsAsync();

        var huge = Assert.Single(locals, v => v.Name == "huge");
        Assert.EndsWith("...\"", huge.Value, StringComparison.Ordinal);
        Assert.True(huge.Value.Length <= 1100, $"the display is {huge.Value.Length} characters long");
    }

    [RequiresX86WorkerFact]
    public async Task WhenADoubleIsShownThenItUsesInvariantFormatting()
    {
        var locals = await fixture.LocalsAsync();

        var half = Assert.Single(locals, v => v.Name == "half");
        Assert.Equal("0.5", half.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAnIntPtrIsShownThenItsAddressIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var handle = Assert.Single(locals, v => v.Name == "handle");
        Assert.Equal("0x00001234", handle.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAGenericLocalIsShownThenItsTypeIsInstantiated()
    {
        var locals = await fixture.LocalsAsync();

        var list = Assert.Single(locals, v => v.Name == "orderIds");
        Assert.Equal("System.Collections.Generic.List<int>", list.Type);
    }

    [RequiresX86WorkerFact]
    public async Task WhenALocalIsListedThenItIsSettable()
    {
        var locals = await fixture.LocalsAsync();

        Assert.True(Assert.Single(locals, v => v.Name == "letter").Settable);
    }

    [RequiresX86WorkerFact]
    public async Task WhenALazyEnumerableIsExpandedThenAResultsViewIsOffered()
    {
        var locals = await fixture.LocalsAsync();
        var filtered = Assert.Single(locals, v => v.Name == "filtered");
        Assert.False(string.IsNullOrEmpty(filtered.VariablesReference), "the enumerable is not expandable at all");

        var children = await fixture.ExpandAsync(filtered.VariablesReference);

        var results = Assert.Single(children, c => c.Name == "Results View");
        Assert.False(string.IsNullOrEmpty(results.VariablesReference), "the Results View is not expandable");
    }

    [RequiresX86WorkerFact]
    public async Task WhenTheResultsViewIsExpandedThenTheElementsAreEnumerated()
    {
        var locals = await fixture.LocalsAsync();
        var filtered = Assert.Single(locals, v => v.Name == "filtered");
        var children = await fixture.ExpandAsync(filtered.VariablesReference);
        var results = Assert.Single(children, c => c.Name == "Results View");

        var elements = await fixture.ExpandAsync(results.VariablesReference);

        // Where(x => x < 9) over { 7, 8, 9 } produces 7 and 8, materialized on demand.
        Assert.Equal("7", Assert.Single(elements, e => e.Name == "[0]").Value);
        Assert.Equal("8", Assert.Single(elements, e => e.Name == "[1]").Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenStoppedAtABreakpointThenThereIsNoExceptionRow()
    {
        var locals = await fixture.LocalsAsync();

        Assert.DoesNotContain(locals, v => v.Name == "$exception");
    }

    [RequiresX86WorkerFact]
    public async Task WhenAMultiDimensionalArrayIsShownThenItsLengthsAreShown()
    {
        var locals = await fixture.LocalsAsync();

        var grid = Assert.Single(locals, v => v.Name == "grid");
        Assert.Equal("int[2,3]", grid.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAMultiDimensionalArrayIsShownThenItsTypeUsesCommaSyntax()
    {
        var locals = await fixture.LocalsAsync();

        var grid = Assert.Single(locals, v => v.Name == "grid");
        Assert.Equal("int[,]", grid.Type);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAMultiDimensionalArrayIsExpandedThenElementsAreNamedByIndices()
    {
        var locals = await fixture.LocalsAsync();
        var grid = Assert.Single(locals, v => v.Name == "grid");
        Assert.False(string.IsNullOrEmpty(grid.VariablesReference), "the array is not expandable at all");

        var children = await fixture.ExpandAsync(grid.VariablesReference);

        Assert.Equal("9", Assert.Single(children, c => c.Name == "[1,2]").Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAMultiDimensionalElementIsEvaluatedThenItGivesTheValue()
    {
        var (ok, value, error) = await fixture.EvaluateAsync("grid[1,2]");

        Assert.True(ok, error);
        Assert.Equal("9", value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAJaggedArrayIsShownThenTheOuterLengthIsInTheFirstBracket()
    {
        var locals = await fixture.LocalsAsync();

        var jagged = Assert.Single(locals, v => v.Name == "jagged");
        Assert.Equal("int[2][]", jagged.Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAnArrayIsLongerThanThePageThenRangeRowsReachEveryElement()
    {
        var locals = await fixture.LocalsAsync();
        var many = Assert.Single(locals, v => v.Name == "many");
        var ranges = await fixture.ExpandAsync(many.VariablesReference);

        Assert.Equal(["[0..99]", "[100..199]", "[200..249]"], ranges.Select(v => v.Name));
        var middle = Assert.Single(ranges, c => c.Name == "[100..199]");
        Assert.False(string.IsNullOrEmpty(middle.VariablesReference), "the range row is not expandable");

        var elements = await fixture.ExpandAsync(middle.VariablesReference);

        // Expanding a range starts at that range, not back at the beginning of the array.
        Assert.DoesNotContain(elements, c => c.Name == "[0]");
        Assert.Equal("55", Assert.Single(elements, c => c.Name == "[100]").Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenADictionaryIsIndexedByStringInAWatchThenTheValueIsShown()
    {
        var (ok, value, error) = await fixture.EvaluateAsync("pairs[\"a\"]");

        Assert.True(ok, error);
        Assert.Equal("1", value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenAListIsIndexedInAWatchThenItsIndexerIsUsed()
    {
        var (ok, value, error) = await fixture.EvaluateAsync("orderIds[1]");

        Assert.True(ok, error);
        Assert.Equal("8", value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenALocalIsCapturedByALambdaThenItStillShowsUnderItsOwnName()
    {
        var locals = await fixture.LocalsAsync();

        // The compiler moved it into a display class; the debugger moves it back.
        Assert.Equal("123", Assert.Single(locals, v => v.Name == "captured").Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenALocalIsCapturedThenTheDisplayClassItselfIsHidden()
    {
        var locals = await fixture.LocalsAsync();

        Assert.DoesNotContain(locals, v => v.Name.StartsWith("CS$", StringComparison.Ordinal));
    }

    [RequiresX86WorkerFact]
    public async Task WhenACapturedLocalIsEvaluatedThenItResolves()
    {
        var (ok, value, error) = await fixture.EvaluateAsync("captured");

        Assert.True(ok, error);
        Assert.Equal("123", value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenADelegateIsShownThenItsTargetMethodIsShown()
    {
        var locals = await fixture.LocalsAsync();

        var predicate = Assert.Single(locals, v => v.Name == "predicate");
        Assert.StartsWith("{Method = ", predicate.Value, StringComparison.Ordinal);
        Assert.Contains("Boolean", predicate.Value, StringComparison.Ordinal);
    }

    [RequiresX86WorkerFact]
    public async Task WhenATypeHasStaticsThenExpandingAnInstanceOffersAStaticMembersNode()
    {
        var members = await fixture.ExpandAsync("this");

        var statics = Assert.Single(members, m => m.Name == "Static members");
        Assert.False(string.IsNullOrEmpty(statics.VariablesReference), "the statics node is not expandable");

        var rows = await fixture.ExpandAsync(statics.VariablesReference);

        Assert.Equal("5", Assert.Single(rows, r => r.Name == "Counter").Value);
        Assert.Equal("9", Assert.Single(rows, r => r.Name == "Limit").Value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenABareStaticNameIsEvaluatedThenItResolvesAgainstTheFramesType()
    {
        var (ok, value, error) = await fixture.EvaluateAsync("Counter");

        Assert.True(ok, error);
        Assert.Equal("5", value);
    }

    [RequiresX86WorkerFact]
    public async Task WhenDebuggerDisplayNamesAnEntryThenTheElementRowUsesIt()
    {
        var locals = await fixture.LocalsAsync();
        var tags = Assert.Single(locals, v => v.Name == "tags");
        Assert.False(string.IsNullOrEmpty(tags.VariablesReference), "the array is not expandable at all");

        var children = await fixture.ExpandAsync(tags.VariablesReference);

        var entry = Assert.Single(children, c => c.Name == "tag 5");
        Assert.Equal("special", entry.Type);
        Assert.Equal("5", entry.Value);
    }

    /// <summary>
    /// One 32-bit target stopped at a breakpoint with the interesting locals in scope, shared by
    /// every test in the class — attaching the worker per test would cost half a minute each.
    /// </summary>
    public sealed class StoppedTargetFixture : IDisposable
    {
        private const string BreakpointStatement =
            "Use(orderIds, someValue, noValue, scores, pairs, day, flavors, price, when, wait, id, letter, boxed, point, error, multiline, huge, half, handle, filtered, grid, jagged, many, captured, predicate, tags);";

        private const string Source =
            """
            using System;
            using System.Collections.Generic;
            using System.Diagnostics;
            using System.Linq;

            namespace X86LocalsTarget
            {
                public enum Day
                {
                    Monday,
                    Tuesday,
                    Wednesday
                }

                [Flags]
                public enum Flavors
                {
                    None = 0,
                    Sweet = 1,
                    Salty = 2,
                    Sour = 4
                }

                public struct Point
                {
                    private readonly int _x;
                    private readonly int _y;

                    public Point(int x, int y)
                    {
                        _x = x;
                        _y = y;
                    }

                    public override string ToString()
                    {
                        return "(" + _x + ", " + _y + ")";
                    }
                }

                [DebuggerDisplay("{_x}", Name = "tag {_x}", Type = "special")]
                public class Tagged
                {
                    private readonly int _x;

                    public Tagged(int x)
                    {
                        _x = x;
                    }
                }

                public class Inspector
                {
                    public static int Counter = 5;
                    public const int Limit = 9;

                    private readonly int _seed;

                    public Inspector(int seed)
                    {
                        _seed = seed;
                    }

                    public int Doubled
                    {
                        get { return _seed * 2; }
                    }

                    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
                    public int Hidden { get; set; }

                    public int Boom
                    {
                        get { throw new InvalidOperationException("kaboom"); }
                    }

                    public void Inspect(string label, int factor)
                    {
                        List<int> orderIds = new List<int> { 7, 8, 9 };
                        int? someValue = 42;
                        int? noValue = null;
                        int[] scores = new int[] { 5, 6 };
                        Dictionary<string, int> pairs = new Dictionary<string, int> { { "a", 1 } };
                        Day day = Day.Wednesday;
                        Flavors flavors = Flavors.Sweet | Flavors.Salty;
                        decimal price = 19.95m;
                        DateTime when = new DateTime(2020, 1, 2, 3, 4, 5);
                        TimeSpan wait = TimeSpan.FromMinutes(5);
                        Guid id = new Guid("11111111-2222-3333-4444-555555555555");
                        char letter = 'x';
                        object boxed = 42;
                        Point point = new Point(3, 4);
                        Exception error = new InvalidOperationException("bad");
                        string multiline = "line1\r\nline2";
                        string huge = new string('a', 5000);
                        double half = 0.5;
                        IntPtr handle = new IntPtr(0x1234);
                        IEnumerable<int> filtered = orderIds.Where(IsSmall);
                        int[,] grid = new int[2, 3];
                        grid[1, 2] = 9;
                        int[][] jagged = new int[][] { new int[] { 1 }, new int[] { 2, 3 } };
                        int[] many = new int[250];
                        many[100] = 55;
                        int captured = 123;
                        Func<int, bool> predicate = delegate(int v) { return v > captured; };
                        Tagged[] tags = new Tagged[] { new Tagged(5) };
                        Use(orderIds, someValue, noValue, scores, pairs, day, flavors, price, when, wait, id, letter, boxed, point, error, multiline, huge, half, handle, filtered, grid, jagged, many, captured, predicate, tags);
                    }

                    private static bool IsSmall(int value)
                    {
                        return value < 9;
                    }

                    private static void Use(params object[] values)
                    {
                    }
                }

                public static class Program
                {
                    public static void Main()
                    {
                        Console.WriteLine("ready");
                        Console.Out.Flush();
                        for (int i = 0; i < 100000; i++)
                        {
                            new Inspector(7).Inspect("active", 3);
                            System.Threading.Thread.Sleep(20);
                        }
                    }
                }
            }
            """;

        private readonly object _gate = new();
        private Session? _session;

        public Task<List<DebugVariable>> LocalsAsync() => Stopped().Engine.VariablesAsync(0);

        public Task<List<DebugVariable>> ExpandAsync(string path) => Stopped().Engine.ExpandAsync(0, path);

        public Task<(bool Ok, string Value, string Error)> EvaluateAsync(string expression) =>
            Stopped().Engine.EvaluateAsync(0, expression);

        private Session Stopped()
        {
            lock (_gate)
                return _session ??= Session.Start();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _session?.Dispose();
                _session = null;
            }
        }

        private sealed class Session : IDisposable
        {
            public required WorkerDebugEngine Engine { get; init; }
            public required Process Target { get; init; }

            public static Session Start()
            {
                var directory = Path.Combine(Path.GetTempPath(), "roslynsense-x86-locals-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);

                var sourcePath = Path.Combine(directory, "Program.cs");
                var exe = Path.Combine(directory, "X86LocalsTarget.exe");
                File.WriteAllText(sourcePath, Source);

                var csc = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");

                var compile = Process.Start(new ProcessStartInfo
                {
                    FileName = csc,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = directory,
                    ArgumentList = { "-nologo", "-debug:full", "-platform:x86", "-r:System.Core.dll", "-out:" + exe, sourcePath },
                })!;
                compile.WaitForExit(120_000);
                if (compile.ExitCode != 0)
                    throw new InvalidOperationException("the 32-bit target did not compile: " + compile.StandardError.ReadToEnd());

                var breakpointLine = Array.FindIndex(
                    Source.ReplaceLineEndings("\n").Split('\n'),
                    line => line.Contains(BreakpointStatement, StringComparison.Ordinal)) + 1;

                var target = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                })!;
                target.StandardOutput.ReadLine();

                var engine = new WorkerDebugEngine(X86Target.WorkerPath!, sessionId: 2);
                try
                {
                    var hit = new TaskCompletionSource<DebugEvent>();
                    _ = Task.Run(async () =>
                    {
                        await foreach (var e in engine.Events.ReadAllAsync())
                        {
                            if (e.Kind == DebugEventKind.Breakpoint)
                                hit.TrySetResult(e);
                        }
                    });

                    engine.Attach(
                        target.Id,
                        [new BreakpointSpec { FilePath = sourcePath, Line = (uint)breakpointLine }],
                        DebugRuntime.NetFramework);

                    hit.Task.Wait(TimeSpan.FromSeconds(45));
                    if (!hit.Task.IsCompleted)
                        throw new InvalidOperationException("the breakpoint was never hit");

                    return new Session { Engine = engine, Target = target };
                }
                catch
                {
                    engine.Dispose();
                    try { if (!target.HasExited) target.Kill(entireProcessTree: true); } catch { }
                    target.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                try { Engine.Terminate(); } catch { }
                try { Engine.Dispose(); } catch { }
                try { if (!Target.HasExited) Target.Kill(entireProcessTree: true); } catch { }
                try { Target.Dispose(); } catch { }
            }
        }
    }
}
