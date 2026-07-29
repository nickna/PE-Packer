using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using PEPacker;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Regression tests for the method-body IL decoder.
/// <para>
/// The decoder used to size operands from a switch expression that fell through to
/// "no operand" for anything it had not enumerated. <c>switch</c> was listed as
/// "handled specially" but never was, and the token-bearing <c>ldelem</c>/<c>stelem</c>
/// forms were absent entirely, so their operand bytes were read as opcodes and the walk
/// lost sync with the instruction stream.
/// </para>
/// <para>
/// The damage was silent: a desynced walk steps over real <c>ldstr</c> sites, those
/// strings never reach the rebuilt <c>#US</c> heap, and every later string offset
/// shifts. An 8-arm switch returned a neighbouring literal with no error at all;
/// 17- and 19-arm switches threw <c>BadImageFormatException: No string associated
/// with token</c> at invoke time.
/// </para>
/// <para>
/// Separately, StandAloneSig rows were copied on demand from <c>CopyMethodBody</c>,
/// which renumbered the table (local-variable signatures landed first) and left
/// <c>calli</c> operands pointing at a LocalVarSig row.
/// </para>
/// </summary>
public class AssemblyReferenceRewriterILTests
{
    /// <summary>
    /// Arities chosen so the jump table's leading bytes decode as opcodes with 4- and
    /// 8-byte operands — the shapes that used to knock the old walk out of sync.
    /// SharpTS emits <c>switch</c> for async state machines, iterators and union types,
    /// which is exactly the async/generator code this rewriter exists to post-process.
    /// </summary>
    private static readonly int[] SwitchArities = [1, 2, 3, 8, 16, 17, 19, 24, 31, 32, 46, 48];

    [Fact]
    public void Rewrite_PreservesStringLiterals_AcrossSwitchArities()
    {
        var source = Build("SwitchFixture", BuildSwitchMethods);
        var rewritten = Rewrite(source);

        // Metadata-level check: every ldstr in the rewritten body must still resolve to
        // the literal the source body referenced. Execution alone can mask a shifted
        // #US heap when the wrong string happens to be readable.
        foreach (var n in SwitchArities)
        {
            var (sourceIL, sourceReader) = ReadMethodBody(source, $"Sw{n}");
            var (rewrittenIL, rewrittenReader) = ReadMethodBody(rewritten, $"Sw{n}");

            foreach (var site in FindLdStrOffsets(sourceIL))
            {
                var expected = sourceReader.GetUserString(
                    MetadataTokens.UserStringHandle(BitConverter.ToInt32(sourceIL, site + 1) & 0xFFFFFF));
                var actual = rewrittenReader.GetUserString(
                    MetadataTokens.UserStringHandle(BitConverter.ToInt32(rewrittenIL, site + 1) & 0xFFFFFF));

                Assert.Equal(expected, actual);
            }
        }

        // ...and the rewritten assembly must actually run, hitting the first arm, the
        // last arm and the default path of every switch.
        Execute(rewritten, asm =>
        {
            var type = asm.GetType("Fixture.Switches")!;
            foreach (var n in SwitchArities)
            {
                var method = type.GetMethod($"Sw{n}")!;
                Assert.Equal($"L0-{n}", method.Invoke(null, [0]));
                Assert.Equal($"L{n - 1}-{n}", method.Invoke(null, [n - 1]));
                Assert.Equal($"D-{n}", method.Invoke(null, [n]));
            }
        });
    }

    [Fact]
    public void Rewrite_KeepsCalliPointingAtItsMethodSignature()
    {
        var source = Build("CalliFixture", BuildCalliMethods);
        var rewritten = Rewrite(source);

        // The calli signature is row 1 in the source and a LocalVarSig is row 2. When
        // rows were copied on demand the local sig took row 1 and calli followed the
        // stale token straight into it.
        Assert.Equal(DescribeStandaloneSignatures(source), DescribeStandaloneSignatures(rewritten));

        var calliToken = FindCalliToken(rewritten);
        Assert.Equal(0x11, calliToken >> 24);
        Assert.NotEqual(0x07, StandaloneSignatureHeader(rewritten, calliToken & 0xFFFFFF));

        Execute(rewritten, asm =>
        {
            var type = asm.GetType("Fixture.Calls")!;
            Assert.Equal(42, type.GetMethod("UsesCalli")!.Invoke(null, null));
        });
    }

    [Fact]
    public void Rewrite_PreservesTokenBearingArrayOpcodes()
    {
        var source = Build("ArrayFixture", BuildArrayMethods);
        var rewritten = Rewrite(source);

        Execute(rewritten, asm =>
        {
            var type = asm.GetType("Fixture.Arrays")!;

            // stelem <type> / ldelem <type> carry a 4-byte type token. Neither form was
            // in the old opcode tables, so the walk desynced by four bytes at each one.
            Assert.Equal(["first", "second"], (string[])type.GetMethod("BuildViaStelem")!.Invoke(null, null)!);
            Assert.Equal("second", type.GetMethod("ReadViaLdelem")!.Invoke(null, [1]));
        });
    }

    [Fact]
    public void Rewrite_PreservesLocalVariableSignatures()
    {
        // Guards the phase reorder: local signatures are now resolved from a map built
        // before any method body is copied, rather than appended while copying.
        var source = Build("LocalsFixture", BuildLocalsMethods);
        var rewritten = Rewrite(source);

        Execute(rewritten, asm =>
        {
            var method = asm.GetType("Fixture.Locals")!.GetMethod("Sum")!;
            var locals = method.GetMethodBody()!.LocalVariables;

            Assert.Equal([typeof(int), typeof(string), typeof(long)],
                locals.OrderBy(l => l.LocalIndex).Select(l => l.LocalType).ToArray());
            Assert.Equal(15, method.Invoke(null, [5]));
        });
    }

    // ---- fixtures -------------------------------------------------------------

    private static void BuildSwitchMethods(ModuleBuilder module)
    {
        var type = module.DefineType("Fixture.Switches", TypeAttributes.Public);

        foreach (var n in SwitchArities)
        {
            var method = type.DefineMethod($"Sw{n}", MethodAttributes.Public | MethodAttributes.Static,
                typeof(string), [typeof(int)]);
            var il = method.GetILGenerator();

            var labels = new Label[n];
            for (int i = 0; i < n; i++) labels[i] = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Switch, labels);

            // Several literals on the default path, so a one-byte desync lands inside a
            // token rather than realigning on the next instruction boundary.
            il.Emit(OpCodes.Ldstr, $"A-{n}");
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldstr, $"B-{n}");
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldstr, $"D-{n}");
            il.Emit(OpCodes.Ret);

            for (int i = 0; i < n; i++)
            {
                il.MarkLabel(labels[i]);
                il.Emit(OpCodes.Ldstr, $"L{i}-{n}");
                il.Emit(OpCodes.Ret);
            }
        }

        type.CreateType();
    }

    private static void BuildCalliMethods(ModuleBuilder module)
    {
        var type = module.DefineType("Fixture.Calls", TypeAttributes.Public);

        var target = type.DefineMethod("Target", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), Type.EmptyTypes);
        var targetIl = target.GetILGenerator();
        targetIl.Emit(OpCodes.Ldc_I4_S, (byte)42);
        targetIl.Emit(OpCodes.Ret);

        // Defined before the method with locals, so its signature takes StandAloneSig row 1.
        var usesCalli = type.DefineMethod("UsesCalli", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), Type.EmptyTypes);
        var il = usesCalli.GetILGenerator();
        il.Emit(OpCodes.Ldftn, target);
        il.EmitCalli(OpCodes.Calli, CallingConventions.Standard, typeof(int), Type.EmptyTypes, null);
        il.Emit(OpCodes.Ret);

        var withLocals = type.DefineMethod("WithLocals", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), Type.EmptyTypes);
        var localsIl = withLocals.GetILGenerator();
        localsIl.DeclareLocal(typeof(int));
        localsIl.DeclareLocal(typeof(string));
        localsIl.Emit(OpCodes.Ldc_I4_5);
        localsIl.Emit(OpCodes.Stloc_0);
        localsIl.Emit(OpCodes.Ldloc_0);
        localsIl.Emit(OpCodes.Ret);

        type.CreateType();
    }

    private static void BuildArrayMethods(ModuleBuilder module)
    {
        var type = module.DefineType("Fixture.Arrays", TypeAttributes.Public);

        var build = type.DefineMethod("BuildViaStelem", MethodAttributes.Public | MethodAttributes.Static,
            typeof(string[]), Type.EmptyTypes);
        var il = build.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, typeof(string));
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldstr, "first");
        il.Emit(OpCodes.Stelem, typeof(string));
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldstr, "second");
        il.Emit(OpCodes.Stelem, typeof(string));
        il.Emit(OpCodes.Ret);

        var read = type.DefineMethod("ReadViaLdelem", MethodAttributes.Public | MethodAttributes.Static,
            typeof(string), [typeof(int)]);
        var il2 = read.GetILGenerator();
        il2.Emit(OpCodes.Call, build);
        il2.Emit(OpCodes.Ldarg_0);
        il2.Emit(OpCodes.Ldelem, typeof(string));
        il2.Emit(OpCodes.Ret);

        type.CreateType();
    }

    private static void BuildLocalsMethods(ModuleBuilder module)
    {
        var type = module.DefineType("Fixture.Locals", TypeAttributes.Public);

        var sum = type.DefineMethod("Sum", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(int)]);
        var il = sum.GetILGenerator();
        il.DeclareLocal(typeof(int));
        il.DeclareLocal(typeof(string));
        il.DeclareLocal(typeof(long));

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_S, (byte)10);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ret);

        type.CreateType();
    }

    // ---- helpers --------------------------------------------------------------

    private static byte[] Build(string name, Action<ModuleBuilder> emit)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        emit(ab.DefineDynamicModule(name));

        using var stream = new MemoryStream();
        ab.Save(stream);
        return stream.ToArray();
    }

    private static byte[] Rewrite(byte[] source)
    {
        // Only a directory containing System.Runtime.dll and the BCL facades is needed;
        // the shared-framework runtime directory always qualifies.
        using var rewriter = new AssemblyReferenceRewriter(
            new MemoryStream(source), RuntimeEnvironment.GetRuntimeDirectory());

        rewriter.Rewrite();

        using var output = new MemoryStream();
        rewriter.Save(output);
        return output.ToArray();
    }

    private static void Execute(byte[] image, Action<Assembly> assertions)
    {
        var alc = new AssemblyLoadContext("rewritten-il-fixture", isCollectible: true);
        try
        {
            using var ms = new MemoryStream(image);
            assertions(alc.LoadFromStream(ms));
        }
        finally
        {
            alc.Unload();
        }
    }

    private static (byte[] IL, MetadataReader Reader) ReadMethodBody(byte[] image, string methodName)
    {
        var pe = new PEReader(new MemoryStream(image));
        var reader = pe.GetMetadataReader();

        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            if (reader.GetString(method.Name) != methodName) continue;

            return (pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!, reader);
        }

        throw new InvalidOperationException($"Method '{methodName}' not found.");
    }

    /// <summary>
    /// Locates <c>ldstr</c> instructions with a correct walk — <c>switch</c> included —
    /// independently of the decoder under test.
    /// </summary>
    private static List<int> FindLdStrOffsets(byte[] il)
    {
        var offsets = new List<int>();
        int i = 0;

        while (i < il.Length)
        {
            int start = i;
            switch (il[i++])
            {
                case 0x72: // ldstr
                    offsets.Add(start);
                    i += 4;
                    break;
                case 0x45: // switch
                    var cases = BitConverter.ToUInt32(il, i);
                    i += 4 + (int)(4 * cases);
                    break;
                default:
                    break; // the switch fixtures use only 0-operand opcodes otherwise
            }
        }

        return offsets;
    }

    private static string DescribeStandaloneSignatures(byte[] image)
    {
        var pe = new PEReader(new MemoryStream(image));
        var reader = pe.GetMetadataReader();

        return string.Join(",", Enumerable
            .Range(1, reader.GetTableRowCount(TableIndex.StandAloneSig))
            .Select(row => StandaloneSignatureHeader(reader, row) == 0x07 ? "LocalVarSig" : "MethodSig"));
    }

    private static byte StandaloneSignatureHeader(byte[] image, int row) =>
        StandaloneSignatureHeader(new PEReader(new MemoryStream(image)).GetMetadataReader(), row);

    private static byte StandaloneSignatureHeader(MetadataReader reader, int row)
    {
        var signature = reader.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(row));
        return reader.GetBlobReader(signature.Signature).ReadByte();
    }

    private static int FindCalliToken(byte[] image)
    {
        var (il, _) = ReadMethodBody(image, "UsesCalli");
        var index = Array.IndexOf(il, (byte)0x29); // calli
        Assert.True(index >= 0, "calli opcode not found in UsesCalli");
        return BitConverter.ToInt32(il, index + 1);
    }
}
