using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Covers reference handling now that keep/drop/retarget is the caller's decision rather
/// than three hardcoded name comparisons.
/// </summary>
public class ReferencePolicyTests
{
    [Fact]
    public void Default_RetargetsCoreLib_DropsSharpTS_KeepsEverythingElse()
    {
        Assert.Equal(ReferenceAction.RetargetToFacades, ReferencePolicy.Default("System.Private.CoreLib"));
        Assert.Equal(ReferenceAction.Drop, ReferencePolicy.Default("SharpTS"));
        Assert.Equal(ReferenceAction.Keep, ReferencePolicy.Default("Newtonsoft.Json"));
        Assert.Equal(ReferenceAction.Keep, ReferencePolicy.Default("System.Console"));
    }

    [Fact]
    public void RetargetCoreLibOnly_KeepsSharpTS()
    {
        Assert.Equal(ReferenceAction.RetargetToFacades, ReferencePolicy.RetargetCoreLibOnly("System.Private.CoreLib"));
        Assert.Equal(ReferenceAction.Keep, ReferencePolicy.RetargetCoreLibOnly("SharpTS"));
    }

    [Fact]
    public void DroppingReferences_DropsOnlyTheNamedOnes()
    {
        var policy = ReferencePolicy.DroppingReferences("Contoso.Runtime", "Contoso.Support");

        Assert.Equal(ReferenceAction.RetargetToFacades, policy("System.Private.CoreLib"));
        Assert.Equal(ReferenceAction.Drop, policy("Contoso.Runtime"));
        Assert.Equal(ReferenceAction.Drop, policy("Contoso.Support"));
        Assert.Equal(ReferenceAction.Keep, policy("SharpTS"));
        Assert.Equal(ReferenceAction.Keep, policy("System.Console"));
    }

    /// <summary>
    /// The regression that matters: the default must keep stripping a leaked SharpTS
    /// reference — one present in metadata but not used by any type reference, which is the
    /// shape SharpTS's post-pass exists to remove. SharpTS decides whether to run the
    /// rewriter at all partly by testing for that reference, so a default of Keep would
    /// leave the dependency in its output and make the pass pointless work.
    /// </summary>
    [Fact]
    public void DefaultPolicy_StillDropsALeakedSharpTSReference()
    {
        var source = AssemblyReferencing("SharpTS", referenceIsUsed: false);
        Assert.Contains("SharpTS", AssemblyReferenceNamesOf(source));

        var rewritten = Rewrite(source, policy: null);

        Assert.DoesNotContain("SharpTS", AssemblyReferenceNamesOf(rewritten));
    }

    /// <summary>
    /// A reference the source actually uses cannot be dropped, so the default policy
    /// refuses rather than emitting an assembly with an unresolvable type reference.
    /// </summary>
    [Fact]
    public void DefaultPolicy_RefusesWhenTheDroppedReferenceIsActuallyUsed()
    {
        var ex = Assert.Throws<PEPackerException>(() =>
            Rewrite(AssemblyReferencing("SharpTS", referenceIsUsed: true), policy: null));

        Assert.Contains("SharpTS", ex.Message);
    }

    /// <summary>
    /// The capability this unlocks: a consumer whose emitted programs genuinely depend on
    /// an assembly can now keep the reference instead of hitting that error.
    /// </summary>
    [Fact]
    public void RetargetCoreLibOnlyPolicy_PreservesTheReference_AndItsTypeReference()
    {
        var rewritten = Rewrite(
            AssemblyReferencing("SharpTS", referenceIsUsed: true),
            ReferencePolicy.RetargetCoreLibOnly);

        Assert.Contains("SharpTS", AssemblyReferenceNamesOf(rewritten));

        // The type reference must point at the copied row, not a nil scope.
        using var pe = new PEReader(new MemoryStream(rewritten));
        var reader = pe.GetMetadataReader();
        var typeRef = reader.TypeReferences
            .Select(reader.GetTypeReference)
            .Single(t => reader.GetString(t.Name) == "TSObject");

        Assert.Equal(HandleKind.AssemblyReference, typeRef.ResolutionScope.Kind);
        var scope = reader.GetAssemblyReference((AssemblyReferenceHandle)typeRef.ResolutionScope);
        Assert.Equal("SharpTS", reader.GetString(scope.Name));
    }

    [Fact]
    public void CustomPolicy_DroppingAnAssemblyItsTypesUse_ReportsTheAssemblyAndTheType()
    {
        var ex = Assert.Throws<PEPackerException>(() =>
            Rewrite(AssemblyReferencing("Contoso.Runtime", referenceIsUsed: true),
                ReferencePolicy.DroppingReferences("Contoso.Runtime")));

        Assert.Contains("Contoso.Runtime", ex.Message);
        Assert.Contains("TSObject", ex.Message);
        Assert.Contains("reference policy dropped", ex.Message);
    }

    [Fact]
    public void Ctor_NullPolicy_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AssemblyReferenceRewriter(
            new MemoryStream(AssemblyReferencing("SharpTS", referenceIsUsed: true)),
            RuntimeEnvironment.GetRuntimeDirectory(),
            referencePolicy: null!));
    }

    private static byte[] Rewrite(byte[] source, Func<string, ReferenceAction>? policy)
    {
        using var rewriter = policy is null
            ? new AssemblyReferenceRewriter(new MemoryStream(source), RuntimeEnvironment.GetRuntimeDirectory())
            : new AssemblyReferenceRewriter(new MemoryStream(source), RuntimeEnvironment.GetRuntimeDirectory(), policy);

        rewriter.Rewrite();
        using var output = new MemoryStream();
        rewriter.Save(output);
        return output.ToArray();
    }

    private static List<string> AssemblyReferenceNamesOf(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var reader = pe.GetMetadataReader();
        return reader.AssemblyReferences
            .Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
            .ToList();
    }

    /// <summary>
    /// A minimal assembly with an <c>AssemblyRef</c> to <paramref name="assemblyName"/>,
    /// optionally with a <c>TypeRef</c> scoped to it. Hand-built because
    /// <c>PersistedAssemblyBuilder</c> cannot emit a reference to an assembly that is not
    /// loadable.
    /// </summary>
    /// <param name="assemblyName">Simple name of the referenced assembly.</param>
    /// <param name="referenceIsUsed">
    /// When <see langword="false"/>, the reference row exists but nothing resolves through
    /// it — a "leaked" reference, which is the shape that can be dropped safely.
    /// </param>
    private static byte[] AssemblyReferencing(string assemblyName, bool referenceIsUsed)
    {
        var metadata = new MetadataBuilder();

        metadata.AddAssembly(
            metadata.GetOrAddString("Fixture"),
            new Version(1, 0, 0, 0),
            default,
            default,
            AssemblyFlags.PublicKey,
            AssemblyHashAlgorithm.Sha1);

        metadata.AddModule(
            0,
            metadata.GetOrAddString("Fixture.dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()),
            default,
            default);

        var reference = metadata.AddAssemblyReference(
            metadata.GetOrAddString(assemblyName),
            new Version(1, 0, 0, 0),
            default,
            default,
            default,
            default);

        if (referenceIsUsed)
        {
            metadata.AddTypeReference(
                reference,
                metadata.GetOrAddString("SharpTS.Runtime"),
                metadata.GetOrAddString("TSObject"));
        }

        metadata.AddTypeDefinition(
            default,
            default,
            metadata.GetOrAddString("<Module>"),
            baseType: default,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: MetadataTokens.MethodDefinitionHandle(1));

        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            new BlobBuilder());

        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        return blob.ToArray();
    }
}
