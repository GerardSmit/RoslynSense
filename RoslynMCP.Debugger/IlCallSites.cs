namespace RoslynMCP.Debugger;

/// <summary>One call instruction in a method body.</summary>
/// <param name="Offset">Its IL offset — what the runtime wants when asked where a return value is
/// still live.</param>
/// <param name="Token">The metadata token it calls, or 0 for <c>calli</c>, which names no method.</param>
/// <param name="ConstructsAnObject">True for <c>newobj</c>, whose result is the new object rather
/// than a return value.</param>
public readonly record struct IlCallSite(int Offset, int Token, bool ConstructsAnObject);

/// <summary>
/// Finds the calls in a stretch of IL.
/// </summary>
/// <remarks>
/// <para>
/// Written because there is no way to ask a runtime "which calls does this statement make". The
/// question has to be answered from the method body, and answering it needs a real walk: IL
/// instructions are variable-length, so scanning for the call opcodes as bytes would find them
/// inside operands as often as it found real instructions.
/// </para>
/// <para>
/// Only the lengths matter here, not the meanings, so this is a table of operand sizes rather than
/// a disassembler. Everything it does not recognise ends the walk rather than guessing a length —
/// a wrong length desynchronises the stream and every offset after it is fiction.
/// </para>
/// </remarks>
public static class IlCallSites
{
    private const byte Call = 0x28;
    private const byte Calli = 0x29;
    private const byte CallVirt = 0x6F;
    private const byte NewObj = 0x73;
    private const byte Switch = 0x45;
    private const byte TwoByte = 0xFE;

    /// <summary>Not a real instruction: the marker for a byte the table has no length for.</summary>
    private const sbyte Unknown = -1;

    /// <summary>
    /// The calls made between two IL offsets.
    /// </summary>
    /// <param name="il">The whole method body's IL, from offset zero — the walk has to start where
    /// the instruction stream does, not where the range does, or the first instruction it decodes
    /// may be the middle of another one.</param>
    /// <param name="start">First offset of interest, inclusive.</param>
    /// <param name="end">Last offset of interest, exclusive.</param>
    public static List<IlCallSite> Between(byte[] il, int start, int end)
    {
        var found = new List<IlCallSite>();
        if (il.Length == 0 || end <= start)
            return found;

        int at = 0;
        while (at < il.Length)
        {
            int opcodeAt = at;
            byte code = il[at++];

            int operand;
            if (code == TwoByte)
            {
                if (at >= il.Length)
                    break;
                operand = TwoByteOperands[il[at++]];
            }
            else
            {
                operand = OneByteOperands[code];
            }

            if (operand == Unknown)
                break;

            if (code == Switch)
            {
                if (at + 4 > il.Length)
                    break;
                int cases = BitConverter.ToInt32(il, at);
                if (cases < 0 || cases > (il.Length - at - 4) / 4)
                    break;
                operand = 4 + (cases * 4);
            }

            if (at + operand > il.Length)
                break;

            if (opcodeAt >= start && opcodeAt < end && IsCall(code))
            {
                found.Add(new IlCallSite(
                    opcodeAt,
                    // calli's operand is a signature, not a method — there is no name to report.
                    code == Calli ? 0 : BitConverter.ToInt32(il, at),
                    code == NewObj));
            }

            at += operand;
        }

        return found;
    }

    private static bool IsCall(byte code) =>
        code is Call or Calli or CallVirt or NewObj;

    /// <summary>Operand bytes per one-byte opcode; <see cref="Unknown"/> where no instruction is
    /// defined, so an unrecognised byte stops the walk instead of desynchronising it.</summary>
    private static readonly sbyte[] OneByteOperands = BuildOneByte();

    /// <summary>Operand bytes per opcode behind the <c>0xFE</c> prefix.</summary>
    private static readonly sbyte[] TwoByteOperands = BuildTwoByte();

    private static sbyte[] BuildOneByte()
    {
        var table = new sbyte[256];
        Array.Fill(table, (sbyte)Unknown);

        // No operand: the arithmetic, the conversions, the stack shuffling, the short-form loads.
        Fill(table, 0, 0x00, 0x01);          // nop, break
        Fill(table, 0, 0x02, 0x0D);          // ldarg.0-3, ldloc.0-3, stloc.0-3
        Fill(table, 0, 0x14, 0x1E);          // ldnull, ldc.i4.m1 - ldc.i4.8
        Fill(table, 0, 0x25, 0x26);          // dup, pop
        Fill(table, 0, 0x2A, 0x2A);          // ret
        Fill(table, 0, 0x46, 0x57);          // ldind.*, stind.*
        Fill(table, 0, 0x58, 0x66);          // add - not
        Fill(table, 0, 0x67, 0x6E);          // conv.*
        Fill(table, 0, 0x76, 0x76);          // conv.r.un
        Fill(table, 0, 0x7A, 0x7A);          // throw
        Fill(table, 0, 0x82, 0x8B);          // conv.ovf.*.un
        Fill(table, 0, 0x8E, 0x8E);          // ldlen
        Fill(table, 0, 0x90, 0xA2);          // ldelem.*, stelem.*
        Fill(table, 0, 0xB3, 0xBA);          // conv.ovf.*
        Fill(table, 0, 0xC3, 0xC3);          // ckfinite
        Fill(table, 0, 0xD1, 0xD5);          // conv.u2 - conv.ovf.u
        Fill(table, 0, 0xD6, 0xDB);          // add.ovf - sub.ovf.un
        Fill(table, 0, 0xDC, 0xDC);          // endfinally
        Fill(table, 0, 0xDF, 0xE0);          // stind.i, conv.u

        // One byte: the short forms and the short branches.
        Fill(table, 1, 0x0E, 0x13);          // ldarg.s - stloc.s
        Fill(table, 1, 0x1F, 0x1F);          // ldc.i4.s
        Fill(table, 1, 0x2B, 0x37);          // br.s - blt.un.s
        Fill(table, 1, 0xDE, 0xDE);          // leave.s

        // Four bytes: tokens, long branches, 32-bit constants.
        Fill(table, 4, 0x20, 0x20);          // ldc.i4
        Fill(table, 4, 0x22, 0x22);          // ldc.r4
        Fill(table, 4, 0x27, 0x29);          // jmp, call, calli
        Fill(table, 4, 0x38, 0x44);          // br - blt.un
        Fill(table, 4, 0x45, 0x45);          // switch — the case table is measured while walking
        Fill(table, 4, 0x6F, 0x75);          // callvirt, cpobj, ldobj, ldstr, newobj, castclass, isinst
        Fill(table, 4, 0x79, 0x79);          // unbox
        Fill(table, 4, 0x7B, 0x81);          // ldfld - stobj
        Fill(table, 4, 0x8C, 0x8D);          // box, newarr
        Fill(table, 4, 0x8F, 0x8F);          // ldelema
        Fill(table, 4, 0xA3, 0xA5);          // ldelem, stelem, unbox.any
        Fill(table, 4, 0xC2, 0xC2);          // refanyval
        Fill(table, 4, 0xC6, 0xC6);          // mkrefany
        Fill(table, 4, 0xD0, 0xD0);          // ldtoken
        Fill(table, 4, 0xDD, 0xDD);          // leave

        // Eight bytes: the 64-bit constants.
        Fill(table, 8, 0x21, 0x21);          // ldc.i8
        Fill(table, 8, 0x23, 0x23);          // ldc.r8

        return table;
    }

    private static sbyte[] BuildTwoByte()
    {
        var table = new sbyte[256];
        Array.Fill(table, (sbyte)Unknown);

        Fill(table, 0, 0x00, 0x05);          // arglist, ceq, cgt, cgt.un, clt, clt.un
        Fill(table, 4, 0x06, 0x07);          // ldftn, ldvirtftn
        Fill(table, 2, 0x09, 0x0E);          // ldarg - stloc (long forms)
        Fill(table, 0, 0x0F, 0x0F);          // localloc
        Fill(table, 0, 0x11, 0x11);          // endfilter
        Fill(table, 1, 0x12, 0x12);          // unaligned.
        Fill(table, 0, 0x13, 0x14);          // volatile., tail.
        Fill(table, 4, 0x15, 0x16);          // initobj, constrained.
        Fill(table, 0, 0x17, 0x18);          // cpblk, initblk
        Fill(table, 1, 0x19, 0x19);          // no.
        Fill(table, 0, 0x1A, 0x1A);          // rethrow
        Fill(table, 4, 0x1C, 0x1C);          // sizeof
        Fill(table, 0, 0x1D, 0x1E);          // refanytype, readonly.

        return table;
    }

    private static void Fill(sbyte[] table, sbyte operand, int from, int to)
    {
        for (int i = from; i <= to; i++)
            table[i] = operand;
    }
}
