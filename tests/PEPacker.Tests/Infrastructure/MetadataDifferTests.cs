using System.Reflection;
using System.Reflection.Emit;
using Xunit;
using static PEPacker.Tests.Infrastructure.RewriterTestHelpers;

namespace PEPacker.Tests.Infrastructure;

/// <summary>
/// Self-tests for <see cref="MetadataDiffer"/>.
/// <para>
/// The round-trip tests assert "zero differences after rewrite". That assertion is only
/// worth anything if the differ reports differences when they exist — a comparison that
/// quietly skips a table or a byte range would green every round-trip test for the wrong
/// reason. This is the same reasoning that produced <see cref="ILVerifyHarnessTests"/> for
/// the other verification leg.
/// </para>
/// <para>
/// Each test corrupts a known site in an otherwise-valid image — an IL inline constant, a
/// switch jump table, a custom-attribute blob, mapped FieldRVA bytes, an added row — and
/// asserts the differ names it. The corrupted bytes are located by unique markers baked
/// into the fixture, and uniqueness is asserted so a collision fails loudly rather than
/// corrupting the wrong site.
/// </para>
/// </summary>
public class MetadataDifferTests
{
    [Fact]
    public void IdenticalImages_ReportClean()
    {
        var image = RewrittenProbe();
        Assert.Empty(MetadataDiffer.Compare(image, image));
    }

    /// <summary>
    /// Two separate builds of the same fixture differ only in MVID, which the differ
    /// deliberately ignores — so it must report clean rather than tripping on noise.
    /// </summary>
    [Fact]
    public void RebuiltIdenticalFixture_ReportsClean()
    {
        var first = Rewrite(Build("DifferProbeFixture", EmitProbeType));
        var second = Rewrite(Build("DifferProbeFixture", EmitProbeType));

        Assert.Empty(MetadataDiffer.Compare(first, second));
    }

    /// <summary>
    /// An inline <c>ldc.i4</c> constant is not a metadata token, so only the masked
    /// byte-for-byte IL comparison can see it change.
    /// </summary>
    [Fact]
    public void CorruptedMethodBodyConstant_IsReported()
    {
        var pristine = RewrittenProbe();
        var corrupted = CorruptByteAfterMarker(pristine, ConstantMarker, offsetFromMarker: 0);

        Assert.Contains(MetadataDiffer.Compare(pristine, corrupted),
            d => d.Contains("IL byte"));
    }

    /// <summary>
    /// A switch jump-table entry is a branch target, not a token; a one-byte change
    /// redirects a case with no length change anywhere.
    /// </summary>
    [Fact]
    public void CorruptedSwitchJumpTable_IsReported()
    {
        var pristine = RewrittenProbe();
        // The marker is the switch opcode (0x45) plus its little-endian case count;
        // offset 5 is the first byte of the first jump-table entry.
        var corrupted = CorruptByteAfterMarker(pristine, [0x45, 0x03, 0x00, 0x00, 0x00], offsetFromMarker: 5);

        Assert.Contains(MetadataDiffer.Compare(pristine, corrupted),
            d => d.Contains("IL byte"));
    }

    [Fact]
    public void CorruptedCustomAttributeBlob_IsReported()
    {
        var pristine = RewrittenProbe();
        // Flip a byte inside the attribute's serialized string argument (same length,
        // different text), which only a blob-content comparison can see.
        var corrupted = CorruptByteAfterMarker(pristine, CaSentinelBytes, offsetFromMarker: 10);

        Assert.Contains(MetadataDiffer.Compare(pristine, corrupted),
            d => d.Contains("value blob"));
    }

    [Fact]
    public void CorruptedFieldRvaData_IsReported()
    {
        var pristine = RewrittenProbe();
        var corrupted = CorruptByteAfterMarker(pristine, RvaDataBytes, offsetFromMarker: 3);

        Assert.Contains(MetadataDiffer.Compare(pristine, corrupted),
            d => d.Contains("field RVA data"));
    }

    [Fact]
    public void AddedMethodRow_IsReported()
    {
        var one = Rewrite(Build("DifferRowFixture", m => EmitTypeWithMethods(m, methodCount: 1)));
        var two = Rewrite(Build("DifferRowFixture", m => EmitTypeWithMethods(m, methodCount: 2)));

        Assert.Contains(MetadataDiffer.Compare(one, two),
            d => d.Contains("table MethodDef"));
    }

    // ---- fixture --------------------------------------------------------------

    /// <summary>The little-endian bytes of the unique inline constant in Constant().</summary>
    private static readonly byte[] ConstantMarker = BitConverter.GetBytes(0x1234ABCD);

    private static readonly byte[] CaSentinelBytes = "DIFFER-CA-SENTINEL"u8.ToArray();

    private static readonly byte[] RvaDataBytes = [0xDE, 0xAD, 0xBE, 0xEF];

    /// <summary>
    /// One rewritten image carrying each corruptible site. Rewritten rather than raw so
    /// <c>MetadataDiffer.CompareRetargeting</c>'s CoreLib check cannot muddy the report.
    /// </summary>
    private static byte[] RewrittenProbe() =>
        Rewrite(Build("DifferProbeFixture", EmitProbeType));

    private static void EmitProbeType(ModuleBuilder module)
    {
        var type = module.DefineType("Fx.DifferProbe", TypeAttributes.Public);

        // A custom attribute whose serialized string argument appears nowhere else.
        type.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(ObsoleteAttribute).GetConstructor([typeof(string)])!,
            ["DIFFER-CA-SENTINEL"]));

        // Mapped static data with a unique byte pattern.
        _ = type.DefineInitializedData("DifferData", RvaDataBytes,
            FieldAttributes.Private | FieldAttributes.Static);

        // A method body whose only distinctive bytes are a non-token inline constant.
        var constant = type.DefineMethod("Constant",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        var constantIl = constant.GetILGenerator();
        constantIl.Emit(OpCodes.Ldc_I4, 0x1234ABCD);
        constantIl.Emit(OpCodes.Ret);

        // A three-case switch, giving the jump table a known shape to corrupt.
        var sw = type.DefineMethod("Switch3",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
        var swIl = sw.GetILGenerator();
        var labels = new[] { swIl.DefineLabel(), swIl.DefineLabel(), swIl.DefineLabel() };
        swIl.Emit(OpCodes.Ldarg_0);
        swIl.Emit(OpCodes.Switch, labels);
        swIl.Emit(OpCodes.Ldc_I4_M1);
        swIl.Emit(OpCodes.Ret);
        for (int i = 0; i < labels.Length; i++)
        {
            swIl.MarkLabel(labels[i]);
            swIl.Emit(OpCodes.Ldc_I4, 100 + i);
            swIl.Emit(OpCodes.Ret);
        }

        type.CreateType();
    }

    private static void EmitTypeWithMethods(ModuleBuilder module, int methodCount)
    {
        var type = module.DefineType("Fx.Rows", TypeAttributes.Public);
        for (int i = 0; i < methodCount; i++)
        {
            var method = type.DefineMethod($"M{i}",
                MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ret);
        }
        type.CreateType();
    }

    // ---- corruption helpers ---------------------------------------------------

    /// <summary>
    /// Returns a copy of <paramref name="image"/> with one bit flipped at
    /// <paramref name="offsetFromMarker"/> bytes past the unique occurrence of
    /// <paramref name="marker"/>.
    /// </summary>
    private static byte[] CorruptByteAfterMarker(byte[] image, byte[] marker, int offsetFromMarker)
    {
        int index = FindUnique(image, marker);
        var copy = (byte[])image.Clone();
        copy[index + offsetFromMarker] ^= 0x01;
        return copy;
    }

    private static int FindUnique(byte[] haystack, byte[] needle)
    {
        int first = Find(haystack, needle, 0);
        Assert.True(first >= 0, "marker not found in the image");
        Assert.True(Find(haystack, needle, first + 1) < 0,
            "marker occurs more than once in the image, so the corruption site is ambiguous");
        return first;
    }

    private static int Find(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }
}
