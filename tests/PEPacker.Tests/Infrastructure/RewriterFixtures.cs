using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace PEPacker.Tests;

/// <summary>
/// Hand-built metadata fixtures shared by the index and policy tests.
/// </summary>
/// <remarks>
/// Built directly with <see cref="MetadataBuilder"/> rather than <c>PersistedAssemblyBuilder</c>,
/// which cannot emit a reference to an assembly that is not loadable — and referencing
/// <c>System.Private.CoreLib</c> or a made-up assembly by name is exactly what these tests need.
/// </remarks>
internal static class RewriterFixtures
{
    /// <summary>
    /// A minimal assembly whose single type reference is scoped to
    /// <c>System.Private.CoreLib</c> — the shape the rewriter exists to retarget.
    /// </summary>
    internal static byte[] CoreLibReferencingAssembly(string ns = "System", string typeName = "Object") =>
        Build("System.Private.CoreLib", new Version(10, 0, 0, 0), ns, typeName);

    /// <summary>
    /// A minimal assembly with an <c>AssemblyRef</c> to <paramref name="referencedAssembly"/>
    /// and, when <paramref name="addTypeReference"/> is set, a <c>TypeRef</c> scoped to it.
    /// </summary>
    /// <param name="referencedAssembly">Simple name of the referenced assembly.</param>
    /// <param name="referencedVersion">Version of the referenced assembly.</param>
    /// <param name="ns">Namespace of the referenced type.</param>
    /// <param name="typeName">Name of the referenced type.</param>
    /// <param name="addTypeReference">
    /// When <see langword="false"/>, the reference row exists but nothing resolves through
    /// it — a "leaked" reference, which is the shape a policy can drop safely.
    /// </param>
    internal static byte[] Build(
        string referencedAssembly,
        Version referencedVersion,
        string ns,
        string typeName,
        bool addTypeReference = true)
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
            metadata.GetOrAddString(referencedAssembly),
            referencedVersion,
            default,
            default,
            default,
            default);

        if (addTypeReference)
        {
            metadata.AddTypeReference(
                reference,
                metadata.GetOrAddString(ns),
                metadata.GetOrAddString(typeName));
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

    /// <summary>
    /// The assembly reference names in a PE image.
    /// </summary>
    internal static List<string> AssemblyReferenceNames(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var reader = pe.GetMetadataReader();
        return reader.AssemblyReferences
            .Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
            .ToList();
    }
}
