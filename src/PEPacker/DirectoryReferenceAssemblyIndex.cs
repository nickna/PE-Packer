using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace PEPacker;

/// <summary>
/// Builds a <see cref="IReferenceAssemblyIndex"/> by reading a directory of framework
/// assemblies — a shared framework directory or a reference pack.
/// </summary>
/// <remarks>
/// <para>
/// This is the behaviour the rewriter had built in, extracted so that an index can come
/// from somewhere other than the filesystem. It requires the framework to be present on
/// disk, so a Native AOT tool on a machine with no .NET installed needs a different
/// implementation.
/// </para>
/// <para>
/// Reads metadata directly with <see cref="MetadataReader"/> rather than through
/// <c>MetadataLoadContext</c>. That dependency existed for three lookups — assembly
/// identity, public types, forwarded types — all of which are available here, and it was
/// the last source of trim and AOT analysis warnings in consumers' native images.
/// </para>
/// </remarks>
public sealed class DirectoryReferenceAssemblyIndex : IReferenceAssemblyIndex
{
    /// <summary>
    /// The assembly the rewriter retargets away from. It is an implementation assembly, so
    /// it is never a valid retarget destination and is skipped when indexing.
    /// </summary>
    private const string CoreLib = WellKnownAssemblies.CoreLib;

    /// <summary>
    /// The core facade, which must resolve for the index to be usable at all.
    /// </summary>
    private const string SystemRuntime = WellKnownAssemblies.SystemRuntime;

    private readonly Dictionary<string, string> _typeToAssembly = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssemblyIdentity> _identities = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads <paramref name="referenceAssemblyPath"/> and indexes every public and forwarded
    /// type it finds.
    /// </summary>
    /// <param name="referenceAssemblyPath">
    /// A directory of framework assemblies: a shared framework directory
    /// (<c>dotnet/shared/Microsoft.NETCore.App/&lt;version&gt;</c>) or a reference pack
    /// (<c>dotnet/packs/Microsoft.NETCore.App.Ref/&lt;version&gt;/ref/&lt;tfm&gt;</c>).
    /// </param>
    /// <exception cref="PEPackerException">
    /// The directory is missing, holds no assemblies, has no usable <c>System.Runtime</c>, or
    /// yielded no types.
    /// </exception>
    public DirectoryReferenceAssemblyIndex(string referenceAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(referenceAssemblyPath);

        if (!Directory.Exists(referenceAssemblyPath))
        {
            throw new PEPackerException(
                $"Reference assembly directory '{referenceAssemblyPath}' does not exist. " +
                RequiredReferenceDirectoryHint);
        }

        var assemblies = Directory.GetFiles(referenceAssemblyPath, "*.dll");
        if (assemblies.Length == 0)
        {
            throw new PEPackerException(
                $"Reference assembly directory '{referenceAssemblyPath}' contains no .dll files. " +
                RequiredReferenceDirectoryHint);
        }

        foreach (var assemblyPath in InIndexingOrder(assemblies))
        {
            Index(assemblyPath);
        }

        // Previously the load context resolved its core assembly eagerly, so a directory of
        // unrelated DLLs failed at construction. Reading metadata resolves nothing, so the
        // same condition has to be checked deliberately.
        if (!_identities.ContainsKey(SystemRuntime))
        {
            throw new PEPackerException(
                $"Reference assembly directory '{referenceAssemblyPath}' holds {assemblies.Length} " +
                $".dll file(s) but no '{SystemRuntime}', so the framework type map cannot be built. " +
                RequiredReferenceDirectoryHint);
        }

        // An empty index is not a usable one: every CoreLib-scoped type reference would fall
        // back to System.Runtime and no AssemblyRef row would carry a real identity, so the
        // output would be quietly wrong rather than absent.
        if (_typeToAssembly.Count == 0)
        {
            throw new PEPackerException(
                $"Reference assembly directory '{referenceAssemblyPath}' yielded no framework types " +
                $"({assemblies.Length} .dll file(s) scanned). Rewriting would produce an assembly " +
                $"with unresolved references. {RequiredReferenceDirectoryHint}");
        }
    }

    /// <summary>
    /// What a caller has to pass, and the one plausible value that silently is not it.
    /// </summary>
    /// <remarks>
    /// Under Native AOT <c>RuntimeEnvironment.GetRuntimeDirectory()</c> returns the
    /// application's own directory rather than the empty string, so the obvious way to
    /// obtain this path degrades into "scan a folder with no framework assemblies in it".
    /// </remarks>
    internal const string RequiredReferenceDirectoryHint =
        "Expected a directory containing the framework assemblies — either a shared framework " +
        "directory (dotnet/shared/Microsoft.NETCore.App/<version>) or a reference pack " +
        "(dotnet/packs/Microsoft.NETCore.App.Ref/<version>/ref/<tfm>). Note that under Native AOT, " +
        "RuntimeEnvironment.GetRuntimeDirectory() returns the running application's own directory, " +
        "which holds no framework assemblies and is not a valid value here.";

    /// <summary>
    /// Umbrella facades that forward most of the framework. They are indexed first so a
    /// specific facade overwrites them for any type both describe.
    /// </summary>
    /// <remarks>
    /// A shared framework directory contains <c>mscorlib</c>, <c>netstandard</c> and
    /// <c>System</c> alongside the granular facades, and all of them forward
    /// <c>Dictionary&lt;,&gt;</c>. Since indexing is last-wins, whichever came last in
    /// <see cref="Directory.GetFiles(string, string)"/> order decided the answer — and that
    /// order is alphabetical on Windows but filesystem order on Linux. The same assembly
    /// therefore retargeted to <c>System.Collections</c> on one platform and <c>mscorlib</c> on
    /// the other. Both resolve at run time, so nothing failed; the output simply was not
    /// reproducible across machines.
    /// </remarks>
    private static readonly string[] UmbrellaFacades = ["mscorlib", "netstandard", "System"];

    /// <summary>
    /// Orders the files so indexing is deterministic and prefers specific facades.
    /// </summary>
    private static IEnumerable<string> InIndexingOrder(string[] assemblies)
    {
        // Ordinal by file name first, so the result never depends on directory enumeration
        // order; then umbrellas ahead of everything else so specific facades win.
        return assemblies
            .OrderBy(p => IsUmbrella(p) ? 0 : 1)
            .ThenBy(Path.GetFileName, StringComparer.Ordinal);

        static bool IsUmbrella(string path) =>
            Array.Exists(
                UmbrellaFacades,
                u => string.Equals(Path.GetFileNameWithoutExtension(path), u, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Number of indexed types. Exposed so a caller can report what a directory yielded.
    /// </summary>
    public int TypeCount => _typeToAssembly.Count;

    /// <summary>
    /// Number of indexed assemblies.
    /// </summary>
    public int AssemblyCount => _identities.Count;

    /// <summary>
    /// Every type name this index can resolve, so the contents can be captured by
    /// <see cref="EmbeddedReferenceAssemblyIndex.Write"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="IReferenceAssemblyIndex"/> is deliberately a lookup rather than an enumeration —
    /// an implementation reading from a precomputed blob has no reason to be able to list itself —
    /// so this lives here rather than on the interface.
    /// </remarks>
    public IEnumerable<string> TypeNames => _typeToAssembly.Keys;

    /// <summary>
    /// Every assembly name this index has an identity for.
    /// </summary>
    /// <remarks>
    /// Captured alongside <see cref="TypeNames"/> so a serialised index answers
    /// <see cref="TryGetIdentity"/> the same way this one does. Deriving the identity set from the
    /// types alone would drop the facades that own no publicly resolvable type — a substantial
    /// minority of a reference pack — and quietly make the two implementations disagree.
    /// </remarks>
    public IEnumerable<string> AssemblyNames => _identities.Keys;

    /// <inheritdoc/>
    public bool TryResolveType(string fullTypeName, [NotNullWhen(true)] out AssemblyIdentity? owner)
    {
        owner = null;
        return _typeToAssembly.TryGetValue(fullTypeName, out var assemblyName)
            && _identities.TryGetValue(assemblyName, out owner);
    }

    /// <inheritdoc/>
    public bool TryGetIdentity(string simpleName, [NotNullWhen(true)] out AssemblyIdentity? identity) =>
        _identities.TryGetValue(simpleName, out identity);

    /// <summary>
    /// Records one assembly's identity, public types and forwarded types.
    /// </summary>
    private void Index(string assemblyPath)
    {
        MetadataReader reader;
        PEReader peReader;

        try
        {
            peReader = new PEReader(File.OpenRead(assemblyPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        using (peReader)
        {
            // The directory holds native libraries alongside managed ones (clrjit,
            // hostpolicy, ...), and a managed file without an assembly manifest is a module
            // rather than something with an identity. Both are expected and skipped.
            try
            {
                if (!peReader.HasMetadata)
                {
                    return;
                }

                reader = peReader.GetMetadataReader();
                if (!reader.IsAssembly)
                {
                    return;
                }
            }
            catch (BadImageFormatException)
            {
                return;
            }

            try
            {
                var assemblyDefinition = reader.GetAssemblyDefinition();
                var name = reader.GetString(assemblyDefinition.Name);

                if (name == CoreLib)
                {
                    return;
                }

                _identities[name] = ToIdentity(reader, assemblyDefinition, name);

                // Forwarded types first, so that a type an assembly both forwards and defines
                // resolves to the definition — the order MetadataLoadContext produced.
                IndexForwardedTypes(reader, name);
                IndexPublicTypes(reader, name);
            }
            catch (BadImageFormatException)
            {
                // Malformed metadata in one file should not lose the rest of the directory.
            }
        }
    }

    /// <summary>
    /// Indexes types this assembly forwards elsewhere, so a reference to the facade name
    /// still resolves.
    /// </summary>
    /// <remarks>
    /// <c>MetadataLoadContext.GetForwardedTypes()</c> resolved each target, so a facade
    /// forwarding outside the probe directory threw and only the entries that happened to
    /// resolve were recoverable. Reading the table resolves nothing, so coverage here is
    /// strictly better.
    /// </remarks>
    private void IndexForwardedTypes(MetadataReader reader, string assemblyName)
    {
        foreach (var handle in reader.ExportedTypes)
        {
            var exportedType = reader.GetExportedType(handle);
            if (!exportedType.IsForwarder)
            {
                continue;
            }

            var fullName = FullNameOf(reader, exportedType);
            if (fullName is not null)
            {
                _typeToAssembly[fullName] = assemblyName;
            }
        }
    }

    /// <summary>
    /// Indexes the assembly's public types, nested ones included.
    /// </summary>
    private void IndexPublicTypes(MetadataReader reader, string assemblyName)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var typeDefinition = reader.GetTypeDefinition(handle);
            var visibility = typeDefinition.Attributes & TypeAttributes.VisibilityMask;

            // Public top-level types and nested-public types, matching the
            // `IsPublic || IsNestedPublic` filter this replaced. Note a nested-public type
            // inside a non-public type qualifies, exactly as it did before.
            if (visibility is not (TypeAttributes.Public or TypeAttributes.NestedPublic))
            {
                continue;
            }

            var fullName = FullNameOf(reader, typeDefinition);
            if (fullName is not null)
            {
                _typeToAssembly[fullName] = assemblyName;
            }
        }
    }

    /// <summary>
    /// Builds the reflection-style full name of a type definition, nesting included:
    /// <c>Namespace.Outer+Inner</c>.
    /// </summary>
    private static string? FullNameOf(MetadataReader reader, TypeDefinition typeDefinition)
    {
        var name = reader.GetString(typeDefinition.Name);
        if (name.Length == 0)
        {
            return null;
        }

        if (!typeDefinition.IsNested)
        {
            var ns = reader.GetString(typeDefinition.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        // Walk outwards, guarding against a malformed cycle rather than looping forever.
        var segments = new List<string> { name };
        var declaringHandle = typeDefinition.GetDeclaringType();

        for (int depth = 0; !declaringHandle.IsNil && depth < 64; depth++)
        {
            var declaring = reader.GetTypeDefinition(declaringHandle);
            segments.Add(reader.GetString(declaring.Name));

            if (!declaring.IsNested)
            {
                var ns = reader.GetString(declaring.Namespace);
                return Compose(ns, segments);
            }

            declaringHandle = declaring.GetDeclaringType();
        }

        return null;
    }

    /// <summary>
    /// Builds the full name of a forwarded type, nesting included.
    /// </summary>
    private static string? FullNameOf(MetadataReader reader, ExportedType exportedType)
    {
        var name = reader.GetString(exportedType.Name);
        if (name.Length == 0)
        {
            return null;
        }

        // A nested forwarded type points at its parent exported-type row instead of carrying
        // a namespace of its own.
        if (exportedType.Implementation.Kind != HandleKind.ExportedType)
        {
            var ns = reader.GetString(exportedType.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        var segments = new List<string> { name };
        var parentHandle = exportedType.Implementation;

        for (int depth = 0; depth < 64; depth++)
        {
            var parent = reader.GetExportedType((ExportedTypeHandle)parentHandle);
            segments.Add(reader.GetString(parent.Name));

            if (parent.Implementation.Kind != HandleKind.ExportedType)
            {
                return Compose(reader.GetString(parent.Namespace), segments);
            }

            parentHandle = parent.Implementation;
        }

        return null;
    }

    /// <summary>
    /// Joins outermost-first segments into <c>Namespace.Outer+Inner</c>.
    /// </summary>
    /// <param name="ns">Namespace of the outermost type.</param>
    /// <param name="innermostFirst">Type name segments, innermost first.</param>
    private static string Compose(string ns, List<string> innermostFirst)
    {
        var builder = new StringBuilder();
        if (ns.Length > 0)
        {
            builder.Append(ns).Append('.');
        }

        for (int i = innermostFirst.Count - 1; i >= 0; i--)
        {
            builder.Append(innermostFirst[i]);
            if (i > 0)
            {
                builder.Append('+');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Converts an assembly manifest into the identity the rewriter emits.
    /// </summary>
    /// <remarks>
    /// Reference assemblies carry full public keys with the <c>PublicKey</c> flag set, but
    /// the token is what an <c>AssemblyRef</c> row should hold, so the key is reduced to a
    /// token and the flag dropped to keep the identity self-consistent.
    /// </remarks>
    private static AssemblyIdentity ToIdentity(
        MetadataReader reader,
        AssemblyDefinition assemblyDefinition,
        string name) => new(
            Name: name,
            Version: assemblyDefinition.Version,
            CultureName: reader.GetString(assemblyDefinition.Culture),
            PublicKeyToken: PublicKeyTokenOf(reader.GetBlobBytes(assemblyDefinition.PublicKey)),
            Flags: assemblyDefinition.Flags & MeaningfulInAssemblyReference);

    /// <summary>
    /// The only flags an <c>AssemblyRef</c> row carries meaningfully (ECMA-335 II.23.1.5),
    /// besides <see cref="AssemblyFlags.PublicKey"/>, which is deliberately excluded because
    /// the identity holds a token rather than a full key.
    /// </summary>
    /// <remarks>
    /// Masking rather than only clearing <c>PublicKey</c> matters. An <c>AssemblyDef</c> also
    /// carries JIT hints and reserved bits that mean nothing in a reference: the net10.0
    /// reference pack sets 0x70 on <c>System.Runtime</c>, none of which is a defined
    /// <see cref="AssemblyFlags"/> value. Reading the manifest directly exposes those,
    /// whereas <c>AssemblyName.Flags</c> did not, so propagating them would have silently
    /// changed the emitted rows relative to every previous release — and differently
    /// depending on whether the caller pointed at a reference pack or a shared framework.
    /// </remarks>
    private const AssemblyFlags MeaningfulInAssemblyReference =
        AssemblyFlags.Retargetable | AssemblyFlags.ContentTypeMask;

    /// <summary>
    /// Reduces a full public key to its eight-byte token per ECMA-335: the low eight bytes
    /// of its SHA-1 hash, in reverse order.
    /// </summary>
    /// <remarks>
    /// SHA-1 is not a security choice here — it is what the token format specifies, and it is
    /// what <c>AssemblyName.GetPublicKeyToken()</c> computed before this replaced it.
    /// </remarks>
    private static ImmutableArray<byte> PublicKeyTokenOf(byte[] publicKey)
    {
        // An unsigned assembly has no key, and a key already eight bytes long is a token.
        if (publicKey.Length == 0)
        {
            return [];
        }

        if (publicKey.Length == 8)
        {
            return [.. publicKey];
        }

        var hash = SHA1.HashData(publicKey);
        var token = new byte[8];
        for (int i = 0; i < token.Length; i++)
        {
            token[i] = hash[hash.Length - 1 - i];
        }

        return [.. token];
    }
}
