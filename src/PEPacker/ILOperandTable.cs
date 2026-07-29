namespace PEPacker;

/// <summary>
/// How many bytes follow an opcode, and whether those bytes are a metadata token
/// that has to be remapped when an assembly is rebuilt.
/// </summary>
internal enum ILOperandKind : byte
{
    /// <summary>Opcode is not defined by ECMA-335 — encountering one means the decoder lost sync.</summary>
    Undefined = 0,

    /// <summary>No operand.</summary>
    None,

    /// <summary>One byte (ShortInlineVar, ShortInlineI, ShortInlineBrTarget).</summary>
    Byte,

    /// <summary>Two bytes (InlineVar).</summary>
    Short,

    /// <summary>Four bytes that are not a token (InlineI, ShortInlineR, InlineBrTarget).</summary>
    Int,

    /// <summary>Eight bytes (InlineI8, InlineR).</summary>
    Long,

    /// <summary>A 4-byte case count followed by that many 4-byte targets (InlineSwitch).</summary>
    Switch,

    /// <summary>
    /// A 4-byte metadata token (InlineField, InlineMethod, InlineSig, InlineString,
    /// InlineTok, InlineType) that must be mapped to the target assembly.
    /// </summary>
    Token,
}

/// <summary>
/// Operand classification for every opcode in ECMA-335 Partition VI Appendix C.
/// </summary>
/// <remarks>
/// A full table is the only safe way to walk a method body: the previous
/// switch-expression approach fell through to "no operand" for anything it had not
/// enumerated, so <c>switch</c>, <c>ldelem</c>, <c>stelem</c> and <c>no.</c> silently
/// desynchronised the decoder and their operand bytes were then read as opcodes.
/// </remarks>
internal static class ILOperandTable
{
    /// <summary>First byte of a two-byte opcode.</summary>
    public const byte ExtendedPrefix = 0xFE;

    private static readonly ILOperandKind[] s_single = BuildSingleByte();
    private static readonly ILOperandKind[] s_extended = BuildExtended();

    /// <summary>Classifies a single-byte opcode.</summary>
    public static ILOperandKind Get(byte opCode) => s_single[opCode];

    /// <summary>Classifies the second byte of a <c>0xFE</c>-prefixed opcode.</summary>
    public static ILOperandKind GetExtended(byte opCode) => s_extended[opCode];

    private static ILOperandKind[] BuildSingleByte()
    {
        var t = new ILOperandKind[256];

        // nop..stloc.3
        Fill(t, ILOperandKind.None, 0x00, 0x0D);
        // ldarg.s..stloc.s (ShortInlineVar)
        Fill(t, ILOperandKind.Byte, 0x0E, 0x13);
        // ldnull..ldc.i4.8
        Fill(t, ILOperandKind.None, 0x14, 0x1E);
        t[0x1F] = ILOperandKind.Byte;    // ldc.i4.s
        t[0x20] = ILOperandKind.Int;     // ldc.i4
        t[0x21] = ILOperandKind.Long;    // ldc.i8
        t[0x22] = ILOperandKind.Int;     // ldc.r4
        t[0x23] = ILOperandKind.Long;    // ldc.r8
        // 0x24 unused
        t[0x25] = ILOperandKind.None;    // dup
        t[0x26] = ILOperandKind.None;    // pop
        t[0x27] = ILOperandKind.Token;   // jmp
        t[0x28] = ILOperandKind.Token;   // call
        t[0x29] = ILOperandKind.Token;   // calli  (StandAloneSig)
        t[0x2A] = ILOperandKind.None;    // ret
        // br.s..blt.un.s
        Fill(t, ILOperandKind.Byte, 0x2B, 0x37);
        // br..blt.un
        Fill(t, ILOperandKind.Int, 0x38, 0x44);
        t[0x45] = ILOperandKind.Switch;  // switch
        // ldind.i1..conv.u8
        Fill(t, ILOperandKind.None, 0x46, 0x6E);
        t[0x6F] = ILOperandKind.Token;   // callvirt
        // cpobj, ldobj, ldstr, newobj, castclass, isinst
        Fill(t, ILOperandKind.Token, 0x70, 0x75);
        t[0x76] = ILOperandKind.None;    // conv.r.un
        // 0x77, 0x78 unused
        t[0x79] = ILOperandKind.Token;   // unbox
        t[0x7A] = ILOperandKind.None;    // throw
        // ldfld, ldflda, stfld, ldsfld, ldsflda, stsfld, stobj
        Fill(t, ILOperandKind.Token, 0x7B, 0x81);
        // conv.ovf.*.un
        Fill(t, ILOperandKind.None, 0x82, 0x8B);
        t[0x8C] = ILOperandKind.Token;   // box
        t[0x8D] = ILOperandKind.Token;   // newarr
        t[0x8E] = ILOperandKind.None;    // ldlen
        t[0x8F] = ILOperandKind.Token;   // ldelema
        // ldelem.i1..stelem.ref
        Fill(t, ILOperandKind.None, 0x90, 0xA2);
        t[0xA3] = ILOperandKind.Token;   // ldelem <type>
        t[0xA4] = ILOperandKind.Token;   // stelem <type>
        t[0xA5] = ILOperandKind.Token;   // unbox.any
        // 0xA6..0xB2 unused
        // conv.ovf.i1..conv.ovf.u8
        Fill(t, ILOperandKind.None, 0xB3, 0xBA);
        // 0xBB..0xC1 unused
        t[0xC2] = ILOperandKind.Token;   // refanyval
        t[0xC3] = ILOperandKind.None;    // ckfinite
        // 0xC4, 0xC5 unused
        t[0xC6] = ILOperandKind.Token;   // mkrefany
        // 0xC7..0xCF unused
        t[0xD0] = ILOperandKind.Token;   // ldtoken
        // conv.u2..endfinally
        Fill(t, ILOperandKind.None, 0xD1, 0xDC);
        t[0xDD] = ILOperandKind.Int;     // leave
        t[0xDE] = ILOperandKind.Byte;    // leave.s
        t[0xDF] = ILOperandKind.None;    // stind.i
        t[0xE0] = ILOperandKind.None;    // conv.u
        // 0xE1..0xFF unused; 0xFE is handled by the caller as a prefix

        return t;
    }

    private static ILOperandKind[] BuildExtended()
    {
        var t = new ILOperandKind[256];

        // arglist, ceq, cgt, cgt.un, clt, clt.un
        Fill(t, ILOperandKind.None, 0x00, 0x05);
        t[0x06] = ILOperandKind.Token;   // ldftn
        t[0x07] = ILOperandKind.Token;   // ldvirtftn
        // 0x08 unused
        // ldarg, ldarga, starg, ldloc, ldloca, stloc (InlineVar)
        Fill(t, ILOperandKind.Short, 0x09, 0x0E);
        t[0x0F] = ILOperandKind.None;    // localloc
        // 0x10 unused
        t[0x11] = ILOperandKind.None;    // endfilter
        t[0x12] = ILOperandKind.Byte;    // unaligned.
        t[0x13] = ILOperandKind.None;    // volatile.
        t[0x14] = ILOperandKind.None;    // tail.
        t[0x15] = ILOperandKind.Token;   // initobj
        t[0x16] = ILOperandKind.Token;   // constrained.
        t[0x17] = ILOperandKind.None;    // cpblk
        t[0x18] = ILOperandKind.None;    // initblk
        t[0x19] = ILOperandKind.Byte;    // no.
        t[0x1A] = ILOperandKind.None;    // rethrow
        // 0x1B unused
        t[0x1C] = ILOperandKind.Token;   // sizeof
        t[0x1D] = ILOperandKind.None;    // refanytype
        t[0x1E] = ILOperandKind.None;    // readonly.
        // 0x1F.. unused

        return t;
    }

    private static void Fill(ILOperandKind[] table, ILOperandKind kind, int lo, int hi)
    {
        for (int i = lo; i <= hi; i++)
        {
            table[i] = kind;
        }
    }
}
