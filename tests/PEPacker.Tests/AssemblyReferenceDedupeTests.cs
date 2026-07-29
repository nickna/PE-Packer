using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using PEPacker;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Regression test for duplicate AssemblyRef rows.
/// <para>
/// An assembly can reference a facade directly and also contain a CoreLib-scoped type
/// that retargets onto the same facade. The rewriter created a row for the retarget
/// destination and then copied the source's own reference verbatim, leaving two
/// AssemblyRef rows naming one assembly. Observed in real SharpTS output, where
/// <c>System.Collections.Concurrent</c> appeared twice.
/// </para>
/// <para>
/// Nothing failed as a result — the runtime binds either row — but it is metadata the
/// rewriter invented, and a reference the caller cannot account for.
/// </para>
/// </summary>
public class AssemblyReferenceDedupeTests
{
    [Fact]
    public void Rewrite_DoesNotDuplicate_AnAssemblyThatIsBothReferencedAndRetargetedTo()
    {
        var source = BuildAssemblyReferencingCoreLibAndFacade();
        var rewritten = Rewrite(source);

        var reader = new PEReader(new MemoryStream(rewritten)).GetMetadataReader();
        var names = reader.AssemblyReferences
            .Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
            .ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        // The facade must still be present — deduplication must not drop it.
        Assert.Contains("System.Collections.Concurrent", names);
        Assert.DoesNotContain("System.Private.CoreLib", names);
    }

    /// <summary>
    /// Hand-built so both sides of the collision are present: an explicit reference to
    /// System.Collections.Concurrent, plus a CoreLib-scoped TypeRef for a type the
    /// rewriter will retarget onto that same facade.
    /// </summary>
    private static byte[] BuildAssemblyReferencingCoreLibAndFacade()
    {
        var metadata = new MetadataBuilder();

        metadata.AddAssembly(
            metadata.GetOrAddString("DedupeFixture"), new Version(1, 0, 0, 0),
            default, default, 0, AssemblyHashAlgorithm.Sha1);

        metadata.AddModule(0, metadata.GetOrAddString("DedupeFixture.dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()), default, default);

        var coreLib = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Private.CoreLib"), new Version(10, 0, 0, 0),
            default, default, 0, default);

        // The direct reference.
        metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Collections.Concurrent"), new Version(10, 0, 0, 0),
            default, default, 0, default);

        // A CoreLib-scoped type that lives in that same facade, so the retarget pass
        // selects it as a destination too.
        metadata.AddTypeReference(coreLib,
            metadata.GetOrAddString("System.Collections.Concurrent"),
            metadata.GetOrAddString("ConcurrentDictionary`2"));

        metadata.AddTypeReference(coreLib,
            metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));

        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"),
            default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var peBuilder = new ManagedPEBuilder(
            PEHeaderBuilder.CreateLibraryHeader(),
            new MetadataRootBuilder(metadata),
            new BlobBuilder());

        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        return blob.ToArray();
    }

    private static byte[] Rewrite(byte[] source)
    {
        using var rewriter = new AssemblyReferenceRewriter(
            new MemoryStream(source), RuntimeEnvironment.GetRuntimeDirectory());

        rewriter.Rewrite();

        using var output = new MemoryStream();
        rewriter.Save(output);
        return output.ToArray();
    }
}
