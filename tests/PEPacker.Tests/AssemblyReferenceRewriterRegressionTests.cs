using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using PEPacker;
using PEPacker.Tests.Infrastructure;
using Xunit;
using static PEPacker.Tests.Infrastructure.RewriterTestHelpers;

namespace PEPacker.Tests;

/// <summary>
/// Regression tests for a batch of fail-closed fixes in the rewriter core:
/// <list type="bullet">
/// <item>the small exception-section format overflowing its one-byte DataSize at 21+
/// regions (4 + 21*12 = 256, cast to a zero byte) and silently corrupting the body;</item>
/// <item>GenericParamConstraint rows being copied without recording a handle mapping, so
/// a custom attribute parented on a constraint failed with a misleading error;</item>
/// <item>the entry point being read without masking the token's table byte and silently
/// dropped on a map miss in <c>Save()</c>;</item>
/// <item>lifecycle misuse (Save before Rewrite, Rewrite twice, use after Dispose) being
/// accepted and failing confusingly deep inside System.Reflection.Metadata.</item>
/// </list>
/// </summary>
public class AssemblyReferenceRewriterRegressionTests
{
    // ---- exception-section format selection -----------------------------------

    /// <summary>
    /// 21 regions is the first count whose small-format DataSize (4 + n*12) no longer
    /// fits in one byte even though every individual offset does; 20 is the last that
    /// still fits, pinning the boundary from both sides.
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(30)]
    public void Rewrite_ManyExceptionRegions_RoundTripsAcrossTheSmallFormatBoundary(int regionCount)
    {
        var source = Build($"HandlerFixture{regionCount}", module =>
            BuildManyHandlersMethod(module, regionCount));
        var rewritten = Rewrite(source);

        // The rewritten body must still carry every region with the same shape. With the
        // overflowed DataSize the section claimed zero bytes and the regions vanished
        // (or the reader refused the body outright).
        var sourceRegions = ReadExceptionRegions(source, "Guarded");
        var rewrittenRegions = ReadExceptionRegions(rewritten, "Guarded");

        Assert.Equal(regionCount, sourceRegions.Count);
        Assert.Equal(sourceRegions, rewrittenRegions);

        // Full differential verification: nothing else changed, and ILVerify still
        // accepts the output.
        var differences = MetadataDiffer.Compare(source, rewritten);
        Assert.True(differences.Count == 0,
            $"{differences.Count} difference(s) after rewrite:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", differences.Take(40)));

        AssertNoVerificationErrorsIntroduced(source, rewritten);

        // ...and the handlers actually run.
        Execute(rewritten, asm =>
        {
            var method = asm.GetType("Fixture.Handlers")!.GetMethod("Guarded")!;
            Assert.Equal(regionCount, method.Invoke(null, null));
        });
    }

    // ---- generic-parameter-constraint custom attribute ------------------------

    /// <summary>
    /// A custom attribute parented on a GenericParamConstraint row — the shape Roslyn
    /// emits for NullableAttribute on a constrained type parameter. The constraint rows
    /// were copied but never mapped, so remapping the attribute's parent threw a
    /// misleading "was never copied" error.
    /// </summary>
    [Fact]
    public void Rewrite_PreservesCustomAttributeOnGenericParameterConstraint()
    {
        var source = ConstraintAttributeAssembly();
        var rewritten = Rewrite(source);

        using var pe = new PEReader(new MemoryStream(rewritten));
        var reader = pe.GetMetadataReader();

        Assert.Equal(1, reader.GetTableRowCount(TableIndex.GenericParamConstraint));

        var onConstraint = reader.CustomAttributes
            .Select(reader.GetCustomAttribute)
            .Where(a => a.Parent.Kind == HandleKind.GenericParameterConstraint)
            .ToList();

        var attribute = Assert.Single(onConstraint);

        // The constructor must still be the ObsoleteAttribute member reference.
        var ctor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        var attributeType = reader.GetTypeReference((TypeReferenceHandle)ctor.Parent);
        Assert.Equal("ObsoleteAttribute", reader.GetString(attributeType.Name));

        // And the constraint itself must point at a retargeted, non-nil scope.
        var constraint = reader.GetGenericParameterConstraint(
            (GenericParameterConstraintHandle)attribute.Parent);
        var constraintType = reader.GetTypeReference((TypeReferenceHandle)constraint.Type);
        Assert.Equal("IDisposable", reader.GetString(constraintType.Name));
        Assert.Equal(HandleKind.AssemblyReference, constraintType.ResolutionScope.Kind);
    }

    // ---- entry-point preservation ---------------------------------------------

    [Fact]
    public void Rewrite_PreservesEntryPoint()
    {
        var source = BuildExecutableWithMain();

        // Sanity: the fixture really carries a MethodDef entry-point token.
        int sourceToken = ReadEntryPointToken(source);
        Assert.Equal(0x06, sourceToken >> 24);

        var rewritten = Rewrite(source);

        int rewrittenToken = ReadEntryPointToken(rewritten);
        Assert.Equal(0x06, rewrittenToken >> 24);

        using var pe = new PEReader(new MemoryStream(rewritten));
        var reader = pe.GetMetadataReader();
        var entryPoint = reader.GetMethodDefinition(
            MetadataTokens.MethodDefinitionHandle(rewrittenToken & 0x00FFFFFF));
        Assert.Equal("Main", reader.GetString(entryPoint.Name));

        // The loaded assembly agrees, and the method still runs.
        Execute(rewritten, asm =>
        {
            Assert.NotNull(asm.EntryPoint);
            Assert.Equal("Main", asm.EntryPoint!.Name);
            Assert.Equal(42, asm.EntryPoint.Invoke(null, null));
        });
    }

    // ---- lifecycle guards -----------------------------------------------------

    [Fact]
    public void Save_BeforeRewrite_ThrowsInvalidOperation()
    {
        using var rewriter = CreateRewriter(BuildMinimalFixture());

        var ex = Assert.Throws<InvalidOperationException>(() => rewriter.Save(new MemoryStream()));
        Assert.Contains("Rewrite()", ex.Message);
    }

    [Fact]
    public void Rewrite_CalledTwice_ThrowsInvalidOperation()
    {
        using var rewriter = CreateRewriter(BuildMinimalFixture());
        rewriter.Rewrite();

        var ex = Assert.Throws<InvalidOperationException>(rewriter.Rewrite);
        Assert.Contains("already been called", ex.Message);
    }

    [Fact]
    public void Rewrite_AfterFailedRewrite_ThrowsInvalidOperationRatherThanEmittingGarbage()
    {
        // A fixture the rewriter rejects (TypeRef scoped to a dropped assembly): the
        // first Rewrite() fails partway, so a second call must not run over a
        // half-populated metadata builder.
        using var rewriter = new AssemblyReferenceRewriter(
            new MemoryStream(RewriterFixtures.Build(
                "SharpTS", new Version(1, 0, 0, 0), "SharpTS.Runtime", "TSObject")),
            RuntimeEnvironment.GetRuntimeDirectory());

        Assert.Throws<PEPackerException>(rewriter.Rewrite);
        Assert.Throws<InvalidOperationException>(rewriter.Rewrite);
    }

    [Fact]
    public void Rewrite_AfterDispose_ThrowsObjectDisposed()
    {
        var rewriter = CreateRewriter(BuildMinimalFixture());
        rewriter.Dispose();

        Assert.Throws<ObjectDisposedException>(rewriter.Rewrite);
    }

    [Fact]
    public void Save_AfterDispose_ThrowsObjectDisposed()
    {
        var rewriter = CreateRewriter(BuildMinimalFixture());
        rewriter.Rewrite();
        rewriter.Dispose();

        Assert.Throws<ObjectDisposedException>(() => rewriter.Save(new MemoryStream()));
    }

    // ---- fixtures -------------------------------------------------------------

    /// <summary>
    /// One method with <paramref name="regionCount"/> sequential try/catch regions, each
    /// incrementing a counter, so the region count is exactly controlled and the method's
    /// result proves every protected block executed.
    /// </summary>
    private static void BuildManyHandlersMethod(ModuleBuilder module, int regionCount)
    {
        var type = module.DefineType("Fixture.Handlers", TypeAttributes.Public);
        var method = type.DefineMethod("Guarded",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        var il = method.GetILGenerator();

        il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc_0);

        for (int i = 0; i < regionCount; i++)
        {
            il.BeginExceptionBlock();
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc_0);
            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Pop);
            il.EndExceptionBlock();
        }

        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ret);

        type.CreateType();
    }

    /// <summary>
    /// A console-style image whose CorHeader names <c>Main</c> as the entry point.
    /// <c>PersistedAssemblyBuilder.Save</c> always writes a DLL, so the PE is assembled
    /// explicitly from <c>GenerateMetadata</c> — the documented pattern for producing an
    /// executable.
    /// </summary>
    private static byte[] BuildExecutableWithMain()
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName("EntryPointFixture"), typeof(object).Assembly);
        var moduleBuilder = ab.DefineDynamicModule("EntryPointFixture");
        var type = moduleBuilder.DefineType("Fixture.Program", TypeAttributes.Public);

        var main = type.DefineMethod("Main",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        var il = main.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_S, (byte)42);
        il.Emit(OpCodes.Ret);
        type.CreateType();

        var metadataBuilder = ab.GenerateMetadata(out var ilStream, out var fieldData);
        var peBuilder = new ManagedPEBuilder(
            PEHeaderBuilder.CreateExecutableHeader(),
            new MetadataRootBuilder(metadataBuilder),
            ilStream,
            mappedFieldData: fieldData,
            entryPoint: MetadataTokens.MethodDefinitionHandle(main.MetadataToken));

        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        return blob.ToArray();
    }

    /// <summary>
    /// Hand-built metadata (Reflection.Emit cannot parent an attribute on a constraint):
    /// a generic type <c>Box`1</c> whose parameter <c>T</c> is constrained to
    /// <c>IDisposable</c>, with <c>[Obsolete]</c> applied to the constraint row itself.
    /// </summary>
    private static byte[] ConstraintAttributeAssembly()
    {
        var metadata = new MetadataBuilder();

        metadata.AddAssembly(
            metadata.GetOrAddString("ConstraintFixture"),
            new Version(1, 0, 0, 0),
            default,
            default,
            AssemblyFlags.PublicKey,
            AssemblyHashAlgorithm.Sha1);

        metadata.AddModule(
            0,
            metadata.GetOrAddString("ConstraintFixture.dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()),
            default,
            default);

        var corelib = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Private.CoreLib"),
            new Version(10, 0, 0, 0),
            default,
            default,
            default,
            default);

        var objectRef = metadata.AddTypeReference(
            corelib, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        var disposableRef = metadata.AddTypeReference(
            corelib, metadata.GetOrAddString("System"), metadata.GetOrAddString("IDisposable"));
        var obsoleteRef = metadata.AddTypeReference(
            corelib, metadata.GetOrAddString("System"), metadata.GetOrAddString("ObsoleteAttribute"));

        // The <Module> pseudo-type must be row 1 of TypeDef.
        metadata.AddTypeDefinition(
            default,
            default,
            metadata.GetOrAddString("<Module>"),
            baseType: default,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: MetadataTokens.MethodDefinitionHandle(1));

        var box = metadata.AddTypeDefinition(
            TypeAttributes.Public,
            metadata.GetOrAddString("Fixture"),
            metadata.GetOrAddString("Box`1"),
            baseType: objectRef,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: MetadataTokens.MethodDefinitionHandle(1));

        var genericParam = metadata.AddGenericParameter(
            box, GenericParameterAttributes.None, metadata.GetOrAddString("T"), 0);
        var constraint = metadata.AddGenericParameterConstraint(genericParam, disposableRef);

        // instance void .ctor() — HASTHIS (0x20), 0 params, ELEMENT_TYPE_VOID (0x01).
        var obsoleteCtor = metadata.AddMemberReference(
            obsoleteRef,
            metadata.GetOrAddString(".ctor"),
            metadata.GetOrAddBlob(new byte[] { 0x20, 0x00, 0x01 }));

        // Prolog 0x0001, zero named arguments.
        metadata.AddCustomAttribute(
            constraint,
            obsoleteCtor,
            metadata.GetOrAddBlob(new byte[] { 0x01, 0x00, 0x00, 0x00 }));

        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            new BlobBuilder());

        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        return blob.ToArray();
    }

    private static byte[] BuildMinimalFixture() => Build("LifecycleFixture", module =>
    {
        var type = module.DefineType("Fixture.Simple", TypeAttributes.Public);
        var method = type.DefineMethod("Answer",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_S, (byte)42);
        il.Emit(OpCodes.Ret);
        type.CreateType();
    });

    // ---- helpers --------------------------------------------------------------

    /// <summary>
    /// A rewriter whose lifecycle the test controls, which the shared one-shot
    /// <see cref="RewriterTestHelpers.Rewrite(byte[])"/> cannot express.
    /// </summary>
    private static AssemblyReferenceRewriter CreateRewriter(byte[] source) =>
        new(new MemoryStream(source), RuntimeEnvironment.GetRuntimeDirectory());

    /// <summary>
    /// The exception regions of a named method as comparable
    /// (kind, tryOffset, tryLength, handlerOffset, handlerLength) tuples.
    /// </summary>
    private static List<(ExceptionRegionKind Kind, int TryOffset, int TryLength, int HandlerOffset, int HandlerLength)>
        ReadExceptionRegions(byte[] image, string methodName)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var reader = pe.GetMetadataReader();

        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            if (reader.GetString(method.Name) != methodName) continue;

            return pe.GetMethodBody(method.RelativeVirtualAddress).ExceptionRegions
                .Select(r => (r.Kind, r.TryOffset, r.TryLength, r.HandlerOffset, r.HandlerLength))
                .ToList();
        }

        throw new InvalidOperationException($"Method '{methodName}' not found.");
    }

    private static int ReadEntryPointToken(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var corHeader = pe.PEHeaders.CorHeader;
        Assert.NotNull(corHeader);
        return corHeader!.EntryPointTokenOrRelativeVirtualAddress;
    }

    /// <summary>
    /// Runs ILVerify over both images and fails on findings the rewrite introduced,
    /// baselined against the source exactly as <c>MetadataRoundTripTests</c> does.
    /// </summary>
    private static void AssertNoVerificationErrorsIntroduced(byte[] source, byte[] rewritten)
    {
        using var verifier = new ILVerifyHarness();

        var before = verifier.Verify(source)
            .GroupBy(finding => finding)
            .ToDictionary(group => group.Key, group => group.Count());

        var introduced = new List<string>();
        foreach (var group in verifier.Verify(rewritten).GroupBy(finding => finding))
        {
            before.TryGetValue(group.Key, out int alreadyPresent);
            for (int i = alreadyPresent; i < group.Count(); i++)
            {
                introduced.Add(group.Key);
            }
        }

        Assert.True(introduced.Count == 0,
            $"the rewrite introduced {introduced.Count} IL verification error(s):{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", introduced.Take(30)));
    }
}
