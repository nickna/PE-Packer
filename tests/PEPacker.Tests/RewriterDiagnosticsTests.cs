using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Covers the failures a caller is most likely to hit, and that previously surfaced as raw
/// framework exceptions with no indication of cause.
/// </summary>
public class RewriterDiagnosticsTests
{
    /// <summary>
    /// The one plausible-looking value that is wrong under Native AOT, where
    /// <c>RuntimeEnvironment.GetRuntimeDirectory()</c> returns the app's own directory.
    /// Previously a <see cref="FileNotFoundException"/> naming only 'System.Runtime'.
    /// </summary>
    [Fact]
    public void Ctor_EmptyReferenceDirectory_ThrowsActionablePEPackerException()
    {
        var empty = Directory.CreateTempSubdirectory("pepacker_empty_");
        try
        {
            var ex = Assert.Throws<PEPackerException>(() =>
                new AssemblyReferenceRewriter(new MemoryStream(MinimalAssembly()), empty.FullName));

            Assert.Contains(empty.FullName, ex.Message);
            Assert.Contains("no .dll files", ex.Message);
            Assert.Contains("RuntimeEnvironment.GetRuntimeDirectory()", ex.Message);
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void Ctor_MissingReferenceDirectory_ThrowsPEPackerExceptionNamingThePath()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pepacker_missing_" + Guid.NewGuid().ToString("N")[..8]);

        var ex = Assert.Throws<PEPackerException>(() =>
            new AssemblyReferenceRewriter(new MemoryStream(MinimalAssembly()), missing));

        Assert.Contains(missing, ex.Message);
        Assert.Contains("does not exist", ex.Message);
    }

    /// <summary>
    /// A directory of managed DLLs that simply are not the framework: the load context
    /// cannot resolve its core assembly, and the caller needs to be told that is what
    /// went wrong.
    /// </summary>
    [Fact]
    public void Ctor_DirectoryWithoutSystemRuntime_ThrowsPEPackerExceptionNotFileNotFound()
    {
        var dir = Directory.CreateTempSubdirectory("pepacker_norheference_");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "NotTheFramework.dll"), MinimalAssembly());

            var ex = Assert.Throws<PEPackerException>(() =>
                new AssemblyReferenceRewriter(new MemoryStream(MinimalAssembly()), dir.FullName));

            Assert.Contains("System.Runtime", ex.Message);
            Assert.Contains(dir.FullName, ex.Message);
            Assert.NotNull(ex.InnerException);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A type reference scoped to an assembly the rewriter drops by name.
    /// </summary>
    /// <remarks>
    /// This was previously a <see cref="KeyNotFoundException"/> from a bare dictionary
    /// indexer, naming neither the type nor the reference. No fixture produced the shape,
    /// so the severity was inferred from reading the indexer rather than observed; this
    /// test settles it.
    /// </remarks>
    [Fact]
    public void Rewrite_TypeRefScopedToExcludedAssembly_ThrowsPEPackerExceptionNamingTheType()
    {
        using var rewriter = new AssemblyReferenceRewriter(
            new MemoryStream(AssemblyReferencingExcludedAssembly()),
            RuntimeEnvironment.GetRuntimeDirectory());

        var ex = Assert.Throws<PEPackerException>(rewriter.Rewrite);

        Assert.Contains("SharpTS.Runtime.TSObject", ex.Message);
        Assert.Contains("SharpTS", ex.Message);
        Assert.Contains("was not copied", ex.Message);
    }

    /// <summary>
    /// A minimal but valid managed assembly: manifest, module and the <c>&lt;Module&gt;</c>
    /// type, with no references at all.
    /// </summary>
    private static byte[] MinimalAssembly() => BuildAssembly(addExcludedReference: false);

    /// <summary>
    /// The same, plus an <c>AssemblyRef</c> to "SharpTS" and a <c>TypeRef</c> scoped to it —
    /// the shape <c>CreateAssemblyReferences</c> drops and <c>CopyTypeReferences</c> then
    /// cannot remap.
    /// </summary>
    private static byte[] AssemblyReferencingExcludedAssembly() => BuildAssembly(addExcludedReference: true);

    /// <summary>
    /// Hand-builds metadata rather than using <c>PersistedAssemblyBuilder</c>, which cannot
    /// emit a reference to an assembly that is not loadable.
    /// </summary>
    private static byte[] BuildAssembly(bool addExcludedReference)
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

        if (addExcludedReference)
        {
            var sharpTs = metadata.AddAssemblyReference(
                metadata.GetOrAddString("SharpTS"),
                new Version(1, 0, 0, 0),
                default,
                default,
                default,
                default);

            metadata.AddTypeReference(
                sharpTs,
                metadata.GetOrAddString("SharpTS.Runtime"),
                metadata.GetOrAddString("TSObject"));
        }

        // The <Module> pseudo-type must be row 1 of TypeDef.
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
