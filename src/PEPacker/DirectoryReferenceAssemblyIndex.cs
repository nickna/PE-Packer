using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace PEPacker;

/// <summary>
/// Builds a <see cref="IReferenceAssemblyIndex"/> by scanning a directory of framework
/// assemblies — a shared framework directory or a reference pack.
/// </summary>
/// <remarks>
/// This is the behaviour the rewriter had built in, extracted so that an index can come
/// from somewhere other than the filesystem. It requires the framework to be present on
/// disk, so a Native AOT tool on a machine with no .NET installed needs a different
/// implementation.
/// </remarks>
public sealed class DirectoryReferenceAssemblyIndex : IReferenceAssemblyIndex
{
    /// <summary>
    /// The assembly the rewriter retargets away from. It is an implementation assembly, so
    /// it is never a valid retarget destination and is skipped when indexing.
    /// </summary>
    private const string CoreLib = "System.Private.CoreLib";

    private readonly Dictionary<string, string> _typeToAssembly = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssemblyIdentity> _identities = new(StringComparer.Ordinal);

    /// <summary>
    /// Scans <paramref name="referenceAssemblyPath"/> and indexes every public and forwarded
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

        var resolver = new PathAssemblyResolver(assemblies);

        // Constructing the load context resolves the core assembly immediately, so a
        // directory of unrelated DLLs fails here with a bare FileNotFoundException naming
        // 'System.Runtime' and nothing about what was actually wanted.
        MetadataLoadContext mlc;
        try
        {
            mlc = new MetadataLoadContext(resolver, "System.Runtime");
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException
                                      or BadImageFormatException)
        {
            throw new PEPackerException(
                $"Reference assembly directory '{referenceAssemblyPath}' holds {assemblies.Length} " +
                ".dll file(s) but no usable 'System.Runtime', so the framework type map cannot be " +
                $"built. {RequiredReferenceDirectoryHint}", ex);
        }

        using (mlc)
        {
            Index(mlc, assemblies);
        }

        // An empty index is not a usable one: every CoreLib-scoped type reference would fall
        // back to System.Runtime and no AssemblyRef row would carry a real identity, so the
        // output would be quietly wrong rather than absent.
        if (_typeToAssembly.Count == 0 || _identities.Count == 0)
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
    /// Number of indexed types. Exposed so a caller can report what a directory yielded.
    /// </summary>
    public int TypeCount => _typeToAssembly.Count;

    /// <summary>
    /// Number of indexed assemblies.
    /// </summary>
    public int AssemblyCount => _identities.Count;

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
    /// Records every public and forwarded type in each assembly against its owner.
    /// </summary>
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode",
        Justification =
            "MetadataLoadContext is inspection-only: it reads types out of assembly files supplied " +
            "at run time using its own type system, and never asks the runtime loader for anything. " +
            "Trimming this application therefore cannot remove the types being enumerated here, so " +
            "the warning does not apply. Verified working in a published Native AOT binary, which " +
            "round-tripped an assembly through the full rewrite.")]
    private void Index(MetadataLoadContext mlc, string[] assemblies)
    {
        foreach (var asmPath in assemblies)
        {
            try
            {
                var asm = mlc.LoadFromAssemblyPath(asmPath);
                var asmName = asm.GetName();
                var name = asmName.Name!;

                // Skip implementation assemblies
                if (name == CoreLib)
                    continue;

                _identities[name] = ToIdentity(asmName);

                // Handle forwarded types. A facade routinely forwards to assemblies
                // outside the probe directory; the exception still carries the entries
                // that did resolve, so keep those instead of losing the whole facade.
                Type?[] forwardedTypes;
                try
                {
                    forwardedTypes = asm.GetForwardedTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    forwardedTypes = ex.Types;
                }

                foreach (var forwardedType in forwardedTypes)
                {
                    if (forwardedType is { FullName: not null })
                    {
                        _typeToAssembly[forwardedType.FullName] = name;
                    }
                }

                // Map all public types. A reference assembly may name types from
                // assemblies outside the probe directory; ReflectionTypeLoadException
                // still carries everything that did resolve, so take those rather than
                // discarding the assembly's entire contribution.
                Type?[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }

                foreach (var type in types)
                {
                    if (type is { FullName: not null } && (type.IsPublic || type.IsNestedPublic))
                    {
                        _typeToAssembly[type.FullName] = name;
                    }
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException
                                          or FileNotFoundException)
            {
                // The probe directory holds native libraries alongside managed ones
                // (clrjit, hostpolicy, ...), so these are expected and skipped. Anything
                // else is a real fault and is left to propagate rather than degrading the
                // type map without a word.
            }
        }
    }

    /// <summary>
    /// Converts a scanned <see cref="AssemblyName"/> into the identity the rewriter emits.
    /// </summary>
    /// <remarks>
    /// Reference assemblies carry full public keys with the <c>PublicKey</c> flag set, but
    /// the token is what an <c>AssemblyRef</c> row should hold, so the flag is cleared here
    /// to keep the identity self-consistent.
    /// </remarks>
    private static AssemblyIdentity ToIdentity(AssemblyName name) => new(
        Name: name.Name!,
        Version: name.Version ?? new Version(0, 0, 0, 0),
        CultureName: name.CultureName ?? string.Empty,
        PublicKeyToken: [.. name.GetPublicKeyToken() ?? []],
        Flags: (AssemblyFlags)name.Flags & ~AssemblyFlags.PublicKey);
}
