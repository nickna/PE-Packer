using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace PEPacker;

/// <summary>
/// The identity the rewriter needs in order to emit an <c>AssemblyRef</c> row.
/// </summary>
/// <param name="Name">Simple name, e.g. <c>System.Runtime</c>.</param>
/// <param name="Version">Assembly version.</param>
/// <param name="CultureName">Culture name, empty for neutral.</param>
/// <param name="PublicKeyToken">
/// The eight-byte token, not the full public key. Reference assemblies carry full keys, but
/// the token is what the rewriter emits, so the conversion belongs to whoever produced this
/// identity rather than to every consumer of it.
/// </param>
/// <param name="Flags">Assembly flags as read from the source of the identity.</param>
public sealed record AssemblyIdentity(
    string Name,
    Version Version,
    string CultureName,
    ImmutableArray<byte> PublicKeyToken,
    AssemblyFlags Flags)
{
    /// <summary>
    /// Compares by value, token contents included.
    /// </summary>
    /// <remarks>
    /// The compiler-generated equality would compare <see cref="PublicKeyToken"/> by reference,
    /// because <see cref="ImmutableArray{T}"/> equality is reference equality of the underlying
    /// array rather than element-wise. Two identities describing the same assembly would then be
    /// unequal, which is the opposite of what a record promises — and it is a quiet failure,
    /// since the tokens print identically.
    /// </remarks>
    public bool Equals(AssemblyIdentity? other) =>
        other is not null
        && Name == other.Name
        && Version == other.Version
        && CultureName == other.CultureName
        && Flags == other.Flags
        && PublicKeyToken.AsSpan().SequenceEqual(other.PublicKeyToken.AsSpan());

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(Version);
        hash.Add(CultureName);
        hash.Add(Flags);
        hash.AddBytes(PublicKeyToken.AsSpan());
        return hash.ToHashCode();
    }
}

/// <summary>
/// Answers the only two questions <see cref="AssemblyReferenceRewriter"/> asks about the
/// target framework: which assembly owns a type, and what a given assembly's identity is.
/// </summary>
/// <remarks>
/// <para>
/// The rewriter previously took a directory path and scanned it eagerly, which made a
/// directory of framework assemblies a hard runtime prerequisite. That is not satisfiable
/// by a Native AOT tool on a machine with no .NET installed, and callers get it wrong
/// under AOT anyway, since <c>RuntimeEnvironment.GetRuntimeDirectory()</c> returns the
/// application's own directory there rather than the empty string.
/// </para>
/// <para>
/// Only types whose resolution scope is <c>System.Private.CoreLib</c> are ever looked up;
/// every other reference is copied verbatim from the source and needs no index at all. An
/// implementation therefore only has to cover the CoreLib public surface and the identities
/// of the facades that own it.
/// </para>
/// </remarks>
public interface IReferenceAssemblyIndex
{
    /// <summary>
    /// Finds the assembly that owns a type, by full name including namespace.
    /// </summary>
    bool TryResolveType(string fullTypeName, [NotNullWhen(true)] out AssemblyIdentity? owner);

    /// <summary>
    /// Finds an assembly's identity by simple name.
    /// </summary>
    bool TryGetIdentity(string simpleName, [NotNullWhen(true)] out AssemblyIdentity? identity);
}
