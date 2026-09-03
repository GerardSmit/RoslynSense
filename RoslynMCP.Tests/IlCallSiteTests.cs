using System.Reflection;
using System.Reflection.Emit;
using RoslynMCP.Debugger;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Walking a method body to find the calls a statement makes — the question that has to be answered
/// before the runtime can be asked where a call's return value is still live.
/// </summary>
public class IlCallSiteTests
{
    [Fact]
    public void ACallIsFoundWithTheMethodItCalls()
    {
        // nop; call 0x06000123; ret
        byte[] il = [0x00, 0x28, 0x23, 0x01, 0x00, 0x06, 0x2A];

        var site = Assert.Single(IlCallSites.Between(il, 0, il.Length));

        Assert.Equal(1, site.Offset);
        Assert.Equal(0x06000123, site.Token);
        Assert.False(site.ConstructsAnObject);
    }

    [Fact]
    public void ACallOpcodeInsideAnOperandIsNotACall()
    {
        // The reason this is a walk and not a search. `ldc.i4 0x0000286F` holds the bytes of both
        // `callvirt` and `call` in its operand; a scan for those bytes reports two calls where
        // there are none, and every offset it hands the runtime afterwards is fiction.
        byte[] il = [0x20, 0x6F, 0x28, 0x00, 0x00, 0x2A];

        Assert.Empty(IlCallSites.Between(il, 0, il.Length));
    }

    [Fact]
    public void ASwitchTableIsSteppedOverRatherThanDecoded()
    {
        // switch (3 cases); call 0x06000001; ret — a variable-length instruction, and the one
        // place a fixed table cannot give the answer.
        byte[] il =
        [
            0x45, 0x03, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00,
            0x03, 0x00, 0x00, 0x00,
            0x28, 0x01, 0x00, 0x00, 0x06,
            0x2A,
        ];

        var site = Assert.Single(IlCallSites.Between(il, 0, il.Length));

        Assert.Equal(17, site.Offset);
    }

    [Fact]
    public void ThePrefixedOpcodesAreMeasuredFromTheirOwnTable()
    {
        // ldarg 1 (fe 09, two-byte operand); callvirt; ret. Measuring the prefixed instruction with
        // the one-byte table would put the walk two bytes out.
        byte[] il = [0xFE, 0x09, 0x01, 0x00, 0x6F, 0x11, 0x00, 0x00, 0x0A, 0x2A];

        var site = Assert.Single(IlCallSites.Between(il, 0, il.Length));

        Assert.Equal(4, site.Offset);
        Assert.Equal(0x0A000011, site.Token);
    }

    [Fact]
    public void NewObjIsFoundAndSaysWhatItIs()
    {
        // Worth finding — it is where a `new` on the line comes from — and worth distinguishing:
        // its result is the object, not a return value the runtime keeps live separately.
        byte[] il = [0x73, 0x05, 0x00, 0x00, 0x0A, 0x2A];

        var site = Assert.Single(IlCallSites.Between(il, 0, il.Length));

        Assert.True(site.ConstructsAnObject);
    }

    [Fact]
    public void CalliNamesNoMethod()
    {
        // Its operand is a signature, so there is no token to resolve to a name.
        byte[] il = [0x29, 0x01, 0x00, 0x00, 0x11, 0x2A];

        var site = Assert.Single(IlCallSites.Between(il, 0, il.Length));

        Assert.Equal(0, site.Token);
    }

    [Fact]
    public void OnlyTheCallsInsideTheRangeAreReported()
    {
        // A step covers one statement, not the method. The walk still starts at offset zero —
        // starting at the range would decode from the middle of whatever instruction spans it.
        byte[] il =
        [
            0x28, 0x01, 0x00, 0x00, 0x06,   // 0: call
            0x28, 0x02, 0x00, 0x00, 0x06,   // 5: call
            0x2A,                            // 10: ret
        ];

        var site = Assert.Single(IlCallSites.Between(il, 5, 10));

        Assert.Equal(5, site.Offset);
    }

    [Fact]
    public void AnUndefinedByteEndsTheWalk()
    {
        // A length this does not know is worse than no answer: it desynchronises the stream, and
        // everything decoded afterwards is a guess presented as a fact.
        byte[] il = [0xA6, 0x28, 0x01, 0x00, 0x00, 0x06];

        Assert.Empty(IlCallSites.Between(il, 0, il.Length));
    }

    [Fact]
    public void ATruncatedBodyStopsRatherThanReadingPastItsEnd()
    {
        byte[] il = [0x28, 0x01, 0x00];

        Assert.Empty(IlCallSites.Between(il, 0, il.Length));
    }

    [Fact]
    public void ARealMethodBodyDecodesToTheCallsItActuallyMakes()
    {
        // The table checked against a compiler rather than against itself: every token found is
        // resolved back through the same module, which only succeeds if the walk stayed in step
        // with the real instruction boundaries for the whole body.
        var method = typeof(IlCallSiteTests).GetMethod(
            nameof(SampleWithSeveralCalls), BindingFlags.NonPublic | BindingFlags.Static)!;
        var il = method.GetMethodBody()!.GetILAsByteArray()!;

        var sites = IlCallSites.Between(il, 0, il.Length);
        var names = sites
            .Where(s => s.Token != 0)
            .Select(s => method.Module.ResolveMethod(s.Token)?.Name)
            .ToList();

        Assert.Contains("Concat", names);
        Assert.Contains("get_Length", names);
        Assert.Contains("Max", names);
        // Every one of them resolved: a token read from the wrong offset almost never does.
        Assert.DoesNotContain(null, names);
    }

    [Fact]
    public void EveryOpcodeIsMeasuredTheWayTheRuntimeMeasuresIt()
    {
        // The table pinned against the runtime's own opcode list rather than against a sample of
        // it. One wrong length is not one wrong instruction: the walk desynchronises there and
        // every offset after it is fiction, which is a fabricated call site handed to the runtime
        // as a real one. A hand-written body can only ever miss the entry that is wrong.
        var wrong = new List<string>();

        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opcode)
                continue;

            // The prefix placeholders are reserved bytes with no instruction behind them; the
            // table rightly refuses to measure one, and a body containing one is corrupt.
            if (opcode.Name is null || opcode.Name.StartsWith("prefix", StringComparison.Ordinal))
                continue;

            // Unsigned, because OpCode.Value is a short and every two-byte opcode is negative in it.
            int value = (ushort)opcode.Value;
            int expected = OperandSize(opcode.OperandType);
            int actual = Measure(value);
            if (expected != actual)
                wrong.Add($"{opcode.Name} (0x{value:X4}): expected {expected}, table says {actual}");
        }

        Assert.Empty(wrong);
    }

    /// <summary>What the table says an opcode's operand costs, by walking a body made of it.</summary>
    private static int Measure(int value)
    {
        // A call after the instruction under test: its reported offset is where the walk thought
        // the instruction ended, which is exactly the length the table gave it.
        byte[] prefix = value > 0xFF
            ? [(byte)(value >> 8), (byte)value]
            : [(byte)value];

        for (int operand = 0; operand <= 8; operand++)
        {
            byte[] il =
            [
                .. prefix,
                .. new byte[operand],
                0x28, 0x01, 0x00, 0x00, 0x06,
                0x2A,
            ];

            // The trailing call is the last site found; the instruction under test may itself be
            // a call, which is why this looks at the last one rather than the only one.
            var sites = IlCallSites.Between(il, 0, il.Length);
            if (sites.Count > 0 && sites[^1].Offset == prefix.Length + operand)
                return operand;
        }

        return -1;
    }

    private static int OperandSize(OperandType type) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
            OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
            OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        // switch is measured from its own case table while walking; the empty table this probes
        // with is four bytes, and the dedicated test covers a populated one.
        OperandType.InlineSwitch => 4,
        _ => -1,
    };

    private static int SampleWithSeveralCalls(string left, string right)
    {
        var joined = string.Concat(left, right);
        int length = joined.Length;
        var buffer = new string[Math.Max(length, 1)];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = i switch
            {
                0 => "zero",
                1 => "one",
                2 => "two",
                _ => joined,
            };
        }

        return buffer.Length;
    }
}
