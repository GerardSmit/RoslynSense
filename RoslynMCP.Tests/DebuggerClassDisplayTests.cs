using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The variables view for a realistic object graph, on both runtimes, held to the conventions
/// VS and Rider follow: what a row says, which rows exist, and in what order.
/// </summary>
/// <remarks>
/// One stop, one frame, every assertion against the same listing — the shapes interact (an
/// evaluated property retires the object being listed, a captured parameter has two homes), so
/// they are checked together rather than one shape per target.
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class DebuggerClassDisplayTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Linq;
        using System.Text;
        using System.Threading.Tasks;
        namespace Shop
        {
            public interface IEntity { int Id { get; } string Describe(); }
            public abstract class EntityBase : IEntity
            {
                private readonly Guid _key = new Guid("11111111-2222-3333-4444-555555555555");
                protected int version = 2;
                public int Id { get; set; }
                public string Name { get; set; }
                public abstract string Describe();
                public virtual string Kind => "entity";
            }
            public class Customer : EntityBase
            {
                public string Email { get; set; }
                public List<Order> Orders { get; } = new List<Order>();
                public Address Home;
                public Customer Referrer;
                public override string Describe() { return Name; }
                public static int Created = 3;
                public const string Table = "Customers";
                private int _secret = 42;
                public int Age { get; private set; }
                public override string Kind => "customer";
                public Customer() { Age = 30; }
                public string this[int index] { get { return "item" + index; } }
                public event EventHandler Changed;
            }
            public struct Address { public string City; public int Zip; public override string ToString() { return City + " " + Zip; } }
            public struct Money { public decimal Amount; public string Currency; }
            public enum Status { New, Paid }
            public class Item { public string Sku; public int Qty; }
            public class Order
            {
                public int Number { get; set; }
                public Money Total;
                public DateTime Placed;
                public Status State;
                public Dictionary<string, Item> Items = new Dictionary<string, Item>();
                public Order Parent;
                public string Note = "hello";
            }
            public class Person
            {
                public Person(string first, string last) { First = first; Last = last; }
                public string First { get; }
                public string Last { get; }
                public int Age { get; set; }
                public override string ToString() { return First + "\n" + Last; }
            }
            public class Box<T> { public T Value; public T[] Many; }
            public class Counter<T> { protected int _n = 5; public int Doubled { get { int d = _n; return d * 2; } } }
            public class Tally : Counter<string> { }
            public class Bag : IEnumerable<int>
            {
                private readonly int[] _items = { 4, 5 };
                public IEnumerator<int> GetEnumerator() { return ((IEnumerable<int>)_items).GetEnumerator(); }
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
            }
            public class Ledger : Bag { public int Count = 2; }
            public class Inspector
            {
                private readonly int _seed = 7;
                public int Seed { get { return _seed; } }
                public void Inspect(Customer customer, int factor)
                {
                    IEntity entity = customer;
                    Order order = customer.Orders[0];
                    Person person = new Person("Ada", "Lovelace") { Age = 36 };
                    Box<Order> box = new Box<Order> { Value = order, Many = new[] { order, null } };
                    var tuple = (1, "one");
                    KeyValuePair<string, int> kv = new KeyValuePair<string, int>("k", 5);
                    Exception ex = new InvalidOperationException("bad thing");
                    StringBuilder sb = new StringBuilder("abc");
                    Address? nullableAddress = customer.Home;
                    Status? nullableStatus = null;
                    var anon = new { A = 1, B = "x" };
                    DateTimeOffset dto = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
                    Func<int, int> fn = x => x * factor;
                    Money money = order.Total;
                    object boxedStruct = money;
                    IEnumerable<string> lazy = customer.Orders.Where(o => o.Number > 0).Select(o => "#" + o.Number);
                    Tally tally = new Tally();
                    Bag bag = new Bag();
                    Ledger ledger = new Ledger();
                    Use(entity, order, person, box, tuple, kv, ex, sb, nullableAddress, nullableStatus, anon, dto, fn, money, boxedStruct, lazy, tally, bag, ledger); // BREAK
                }
                private static void Use(params object[] values) { }
            }
            public static class Program
            {
                public static void Main()
                {
                    var customer = new Customer { Id = 1, Name = "Alice", Email = "a@example.com", Home = new Address { City = "Utrecht", Zip = 3500 } };
                    var order = new Order { Number = 10, Total = new Money { Amount = 19.95m, Currency = "EUR" }, Placed = new DateTime(2020, 1, 2, 3, 4, 5), State = Status.Paid };
                    order.Items["sku1"] = new Item { Sku = "sku1", Qty = 2 };
                    customer.Orders.Add(order);
                    Console.WriteLine("ready");
                    Console.Out.Flush();
                    while (true) { new Inspector().Inspect(customer, 3); System.Threading.Thread.Sleep(20); }
                }
            }
        }
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClassesAreShownTheWayVisualStudioShowsThem(bool coreClr)
    {
        if (!OperatingSystem.IsWindows()) return;
        var (directory, exe, sourcePath, line) = Compile(coreClr);
        using var target = Launch(coreClr, exe);
        var engine = new DebugSession(93);
        try
        {
            await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var stopped = new TaskCompletionSource();
            _ = Task.Run(async () =>
            {
                await foreach (var e in engine.Events.ReadAllAsync())
                {
                    if (e.Kind == DebugEventKind.Breakpoint) stopped.TrySetResult();
                }
            });
            engine.Attach(target.Id, [new BreakpointSpec { FilePath = sourcePath, Line = (uint)line }],
                coreClr ? DebugRuntime.CoreClr : DebugRuntime.NetFramework);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(40));

            var locals = await engine.VariablesAsync(0);
            Task<List<DebugVariable>> Expand(DebugVariable v) => engine.ExpandAsync(0, v.VariablesReference);
            DebugVariable Local(string name) => Assert.Single(locals, v => v.Name == name);

            // The frame lists what the user wrote, once each, and nothing of the compiler's.
            Assert.Single(locals, v => v.Name == "factor");
            Assert.DoesNotContain(locals, v => System.Text.RegularExpressions.Regex.IsMatch(v.Name, @"^(local|arg)\d+$"));
            Assert.DoesNotContain(locals, v => v.Name.StartsWith("CS$", StringComparison.Ordinal));

            // An object without a display string reads as its type in braces; every member —
            // base types' and private ones included — is listed once, alphabetically, and none is
            // lost to the evaluations that ran for the members before it.
            var customer = Local("customer");
            Assert.Equal("{Shop.Customer}", customer.Value);
            var members = await Expand(customer);
            Assert.Equal(
                ["Age", "Email", "Home", "Id", "Kind", "Name", "Orders", "Referrer", "version", "_key", "_secret", "Static members", "Raw View"],
                members.Select(m => m.Name));
            Assert.Equal("{Utrecht 3500}", Assert.Single(members, m => m.Name == "Home").Value);
            Assert.Equal("\"customer\"", Assert.Single(members, m => m.Name == "Kind").Value);
            Assert.Equal("{11111111-2222-3333-4444-555555555555}", Assert.Single(members, m => m.Name == "_key").Value);
            Assert.Equal(string.Empty, Assert.Single(members, m => m.Name == "Static members").Value);
            Assert.Equal(string.Empty, Assert.Single(members, m => m.Name == "Raw View").Value);
            var statics = await Expand(Assert.Single(members, m => m.Name == "Static members"));
            Assert.Equal("3", Assert.Single(statics, m => m.Name == "Created").Value);
            Assert.Equal("\"Customers\"", Assert.Single(statics, m => m.Name == "Table").Value);

            // A ToString override is the value, in braces and on one line.
            Assert.Equal("{Ada Lovelace}", Local("person").Value);
            Assert.Equal("{abc}", Local("sb").Value);
            Assert.Equal("{Shop.Box<Shop.Order>}", Local("box").Value);

            // Framework structs read as their text, but a decimal is just a number.
            var order = await Expand(Local("order"));
            Assert.Equal("{01/02/2020 03:04:05}", Assert.Single(order, m => m.Name == "Placed").Value);
            Assert.Equal("Paid", Assert.Single(order, m => m.Name == "State").Value);
            Assert.Empty(Assert.Single(order, m => m.Name == "State").VariablesReference);
            var total = await Expand(Assert.Single(order, m => m.Name == "Total"));
            var amount = Assert.Single(total, m => m.Name == "Amount");
            Assert.Equal("19.95", amount.Value);
            Assert.Empty(amount.VariablesReference);
            // The offset reads as its own text, in whatever the debuggee's culture spells a
            // date in — it is the target's ToString() that produces it, so the day and month
            // may lead in either order and the assertion cannot pin the pattern.
            var dto = Local("dto").Value;
            Assert.EndsWith("+02:00}", dto);
            Assert.Contains("2020", dto);
            Assert.DoesNotContain("DateTimeOffset", dto);

            // Tuples, pairs, anonymous types and exceptions have their own VS spellings.
            Assert.Equal("(1, \"one\")", Local("tuple").Value);
            var pair = await Expand(Local("kv"));
            // The runtimes differ on whether the pair's fields count as non-public state to fold
            // away; the properties lead and the fields are never inline either way.
            Assert.Equal(["Key", "Value"], pair.Take(2).Select(m => m.Name));
            Assert.DoesNotContain(pair, m => m.Name is "key" or "value");
            Assert.Contains(pair, m => m.Name == "Raw View");
            Assert.Equal("{ A = 1, B = \"x\" }", Local("anon").Value);
            var ex = Local("ex");
            Assert.Equal("{\"bad thing\"}", ex.Value);
            var exMembers = await Expand(ex);
            Assert.Equal("\"bad thing\"", Assert.Single(exMembers, m => m.Name == "Message").Value);
            Assert.DoesNotContain(exMembers, m => m.Name == "_message");
            var nonPublic = await Expand(Assert.Single(exMembers, m => m.Name == "Non-Public members"));
            Assert.Equal("\"bad thing\"", Assert.Single(nonPublic, m => m.Name == "_message").Value);

            // A Nullable<T> is its value, or null with nothing to expand.
            var nullableAddress = Local("nullableAddress");
            Assert.Equal("{Utrecht 3500}", nullableAddress.Value);
            Assert.Equal(["City", "Zip"], (await Expand(nullableAddress)).Select(m => m.Name));
            var nullableStatus = Local("nullableStatus");
            Assert.Equal("null", nullableStatus.Value);
            Assert.Empty(nullableStatus.VariablesReference);

            // A struct keeps its shape through a box, and a delegate reads as its method.
            Assert.Equal("{Shop.Money}", Local("boxedStruct").Value);
            Assert.StartsWith("{Method = {", Local("fn").Value);

            // A LINQ query is its elements, with the iterator's state under Raw View — none of
            // it inline, least of all a Current that has nothing in it before the sequence runs.
            var lazy = await Expand(Local("lazy"));
            Assert.Equal(["[0]", "Raw View"], lazy.Select(m => m.Name));
            Assert.Equal("\"#10\"", lazy[0].Value);
            // The runtimes spell the iterator's source field differently; either way it is there.
            Assert.Contains(await Expand(lazy[1]), m => m.Name is "_source" or "source");

            // A user's enumerable with nothing public but its sequence is its elements too; one
            // with public state of its own keeps that inline and the elements behind a Results View.
            var bag = await Expand(Local("bag"));
            Assert.Equal(["[0]", "[1]", "Raw View"], bag.Select(m => m.Name));
            Assert.Equal("4", bag[0].Value);
            var ledger = await Expand(Local("ledger"));
            Assert.Equal(["Count", "_items", "Results View"], ledger.Select(m => m.Name));

            // With the switch off, the VS shape: the iterator's members and a Results View.
            var asVs = engine.DisplayOptions.Clone();
            asVs.EnumerateResults = false;
            engine.DisplayOptions = asVs;
            var lazyAsVs = await Expand(Local("lazy"));
            Assert.Contains(lazyAsVs, m => m.Name == "Results View");
            Assert.DoesNotContain(lazyAsVs, m => m.Name == "[0]");
            engine.DisplayOptions = new DebugDisplayOptions();

            // A property inherited from a generic base is called with the base's type arguments,
            // not the derived type's — a mismatch faults the call with TargetParameterCountException.
            var tally = await Expand(Local("tally"));
            Assert.Equal("10", Assert.Single(tally, m => m.Name == "Doubled").Value);
        }
        finally
        {
            try { engine.Terminate(); } catch { }
            try { if (!target.HasExited) target.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static (string Dir, string Exe, string SourcePath, int Line) Compile(bool coreClr)
    {
        var directory = Path.Combine(Path.GetTempPath(), "debug-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "Program.cs");
        var exe = Path.Combine(directory, coreClr ? "Target.dll" : "Target.exe");
        File.WriteAllText(sourcePath, Source);
        var framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319");
        var references = coreClr
            ? ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            : new[] { Path.Combine(framework, "mscorlib.dll"), Path.Combine(framework, "System.dll"), Path.Combine(framework, "System.Core.dll"), Path.Combine(framework, "Microsoft.CSharp.dll") };
        var compilation = CSharpCompilation.Create("Target",
            [CSharpSyntaxTree.ParseText(SourceText.From(Source, Encoding.UTF8), path: sourcePath)],
            references.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Debug));
        using (var pe = File.Create(exe))
        using (var pdb = File.Create(Path.ChangeExtension(exe, ".pdb")))
        {
            var emit = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        }
        if (coreClr)
            File.WriteAllText(Path.Combine(directory, "Target.runtimeconfig.json"),
                System.Text.Json.JsonSerializer.Serialize(new { runtimeOptions = new {
                    tfm = $"net{Environment.Version.Major}.0",
                    framework = new { name = "Microsoft.NETCore.App", version = $"{Environment.Version.Major}.0.0" }
                } }));
        var line = Source.Split('\n').ToList().FindIndex(l => l.Contains("// BREAK")) + 1;
        return (directory, exe, sourcePath, line);
    }

    private static Process Launch(bool coreClr, string exe)
    {
        var start = new ProcessStartInfo(coreClr ? "dotnet" : exe)
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        if (coreClr) start.ArgumentList.Add(exe);
        return Process.Start(start)!;
    }
}
