using System.Reflection;
using System.Reflection.Emit;
using PEPacker;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Pins the operand classification the method-body decoder walks with.
/// <para>
/// These assertions discriminate where behavioural tests cannot: the rewriter's handle
/// maps are currently order-preserving identities for most tables, so a mis-sized
/// operand can leave output that still happens to load. The classification itself is
/// the contract — get it wrong and the walk desynchronises regardless.
/// </para>
/// </summary>
public class ILOperandTableTests
{
    /// <summary>
    /// Cross-checks every opcode against <see cref="OpCodes"/>, which carries the BCL's
    /// own <see cref="OperandType"/> for each instruction. Covers all ~220 opcodes rather
    /// than a hand-picked sample.
    /// </summary>
    [Fact]
    public void Table_MatchesBclOperandTypes_ForEveryOpCode()
    {
        var mismatches = new List<string>();

        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            // Prefix1..Prefix7/Prefixref are encoding artifacts, not instructions.
            if (field.Name.StartsWith("Prefix", StringComparison.Ordinal)) continue;
            if (field.GetValue(null) is not OpCode opCode) continue;

            var raw = (ushort)opCode.Value;
            var actual = raw > 0xFF
                ? ILOperandTable.GetExtended((byte)(raw & 0xFF))
                : ILOperandTable.Get((byte)raw);

            var expected = Expected(opCode.OperandType);
            if (actual != expected)
            {
                mismatches.Add($"{opCode.Name} (0x{raw:X2}, {opCode.OperandType}): expected {expected}, got {actual}");
            }
        }

        Assert.Empty(mismatches);
    }

    /// <summary>
    /// The classifications the previous switch-expression decoder got wrong. Each fell
    /// through to "no operand", so its operand bytes were decoded as opcodes.
    /// </summary>
    [Fact]
    public void Table_ClassifiesPreviouslyMissedOpCodes()
    {
        Assert.Equal(ILOperandKind.Switch, ILOperandTable.Get(0x45)); // was "handled specially", never was
        Assert.Equal(ILOperandKind.Token, ILOperandTable.Get(0xA3));  // ldelem <type> — absent from both tables
        Assert.Equal(ILOperandKind.Token, ILOperandTable.Get(0xA4));  // stelem <type> — absent from both tables
        Assert.Equal(ILOperandKind.Byte, ILOperandTable.GetExtended(0x19)); // no. — carried an unaccounted byte
    }

    /// <summary>
    /// Undefined encodings must stay <see cref="ILOperandKind.Undefined"/> so the decoder
    /// throws instead of guessing — silently continuing is what let the walk drift before.
    /// </summary>
    [Theory]
    [InlineData(0x24)]
    [InlineData(0x77)]
    [InlineData(0xA6)]
    [InlineData(0xC7)]
    public void Table_LeavesUnusedSingleByteEncodingsUndefined(int opCode)
    {
        Assert.Equal(ILOperandKind.Undefined, ILOperandTable.Get((byte)opCode));
    }

    [Theory]
    [InlineData(0x08)]
    [InlineData(0x10)]
    [InlineData(0x1B)]
    [InlineData(0x40)]
    public void Table_LeavesUnusedExtendedEncodingsUndefined(int opCode)
    {
        Assert.Equal(ILOperandKind.Undefined, ILOperandTable.GetExtended((byte)opCode));
    }

    private static ILOperandKind Expected(OperandType operandType) => operandType switch
    {
        OperandType.InlineNone => ILOperandKind.None,

        OperandType.ShortInlineVar or
        OperandType.ShortInlineI or
        OperandType.ShortInlineBrTarget => ILOperandKind.Byte,

        OperandType.InlineVar => ILOperandKind.Short,

        OperandType.InlineI or
        OperandType.ShortInlineR or
        OperandType.InlineBrTarget => ILOperandKind.Int,

        OperandType.InlineI8 or
        OperandType.InlineR => ILOperandKind.Long,

        OperandType.InlineSwitch => ILOperandKind.Switch,

        OperandType.InlineField or
        OperandType.InlineMethod or
        OperandType.InlineSig or
        OperandType.InlineString or
        OperandType.InlineTok or
        OperandType.InlineType => ILOperandKind.Token,

        _ => throw new InvalidOperationException($"Unhandled operand type {operandType}"),
    };
}
