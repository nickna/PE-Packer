using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace PEPacker;

/// <summary>
/// A <see cref="IReferenceAssemblyIndex"/> backed by precomputed data rather than a directory
/// of assemblies, so the rewriter works with no framework on disk.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DirectoryReferenceAssemblyIndex"/> requires framework assemblies to read, which a
/// Native AOT tool on a machine with no .NET installed cannot provide. This ships the same
/// information as a compressed resource.
/// </para>
/// <para>
/// It stays small because of what the rewriter actually asks. Only types whose resolution scope
/// is <c>System.Private.CoreLib</c> are ever looked up — every other reference is copied verbatim
/// from the source — and a nested type resolves through its parent type reference rather than
/// through the index. So the content is the framework's public surface mapped to owning facade,
/// around 5000 entries, not the full closure of everything a compiler can see.
/// </para>
/// <para>
/// The format is public via <see cref="Write"/> so a caller targeting a framework version this
/// package does not embed can generate their own. Facade <em>versions</em> are part of the data,
/// so an index must match the target framework: emitting 10.0.0.0 references into an assembly
/// targeting net9.0 would be wrong.
/// </para>
/// </remarks>
public sealed class EmbeddedReferenceAssemblyIndex : IReferenceAssemblyIndex
{
    /// <summary>
    /// Format marker. Bumped if the layout below changes.
    /// </summary>
    private const string FormatVersion = "pepacker-refindex-1";

    /// <summary>
    /// The one target framework this package embeds data for.
    /// </summary>
    public const string EmbeddedTargetFramework = "net10.0";

    private const string ResourceName = "PEPacker.Resources.refindex-net10.0.bin";

    private static readonly Lazy<EmbeddedReferenceAssemblyIndex> Net10 =
        new(() => FromResource(ResourceName), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, AssemblyIdentity> _identities;
    private readonly Dictionary<string, AssemblyIdentity> _owners;

    /// <summary>
    /// Reads an index from data produced by <see cref="Write"/>.
    /// </summary>
    /// <param name="data">A stream positioned at the start of the compressed index.</param>
    /// <exception cref="PEPackerException">The data is not a readable index.</exception>
    public EmbeddedReferenceAssemblyIndex(Stream data)
    {
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            using var decompressor = new DeflateStream(data, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new StreamReader(decompressor, Encoding.UTF8);

            var marker = reader.ReadLine();
            if (marker != FormatVersion)
            {
                throw new PEPackerException(
                    $"Unrecognised reference index format '{marker}'; expected '{FormatVersion}'.");
            }

            var assemblies = ReadAssemblies(reader, out _identities);
            _owners = ReadOwners(reader, assemblies);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException)
        {
            throw new PEPackerException("The reference index data could not be read.", ex);
        }
    }

    /// <summary>
    /// The index embedded in this package, covering <see cref="EmbeddedTargetFramework"/>.
    /// </summary>
    public static EmbeddedReferenceAssemblyIndex Default => Net10.Value;

    /// <summary>
    /// The index embedded for a target framework.
    /// </summary>
    /// <param name="targetFramework">A TFM such as <c>net10.0</c>.</param>
    /// <exception cref="PEPackerException">
    /// No index is embedded for that framework. Generate one with <see cref="Write"/> instead of
    /// silently using a different version's facade identities.
    /// </exception>
    public static EmbeddedReferenceAssemblyIndex ForTargetFramework(string targetFramework)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetFramework);

        if (string.Equals(targetFramework, EmbeddedTargetFramework, StringComparison.OrdinalIgnoreCase))
        {
            return Default;
        }

        throw new PEPackerException(
            $"No reference index is embedded for '{targetFramework}'; this package embeds only " +
            $"'{EmbeddedTargetFramework}'. Facade versions are part of the data, so a different " +
            "framework needs its own index — generate one with " +
            "EmbeddedReferenceAssemblyIndex.Write over a DirectoryReferenceAssemblyIndex built " +
            "from that framework's reference pack.");
    }

    /// <summary>
    /// Serialises an index so it can be embedded and read back later.
    /// </summary>
    /// <param name="source">The index to capture, typically a directory-backed one.</param>
    /// <param name="typeNames">Every type name <paramref name="source"/> should be asked about.</param>
    /// <param name="assemblyNames">
    /// Every assembly name to capture an identity for. Deriving these from the types alone would
    /// drop facades that own no publicly resolvable type — 36 of 167 in the net10.0 reference pack
    /// — and quietly make the serialised index answer <see cref="TryGetIdentity"/> differently
    /// from the one it was built from.
    /// </param>
    /// <param name="destination">Where to write the compressed data.</param>
    /// <remarks>
    /// The name collections are explicit because <see cref="IReferenceAssemblyIndex"/> is a lookup,
    /// not an enumeration — an implementation reading from a blob has no reason to be able to list
    /// itself. <see cref="DirectoryReferenceAssemblyIndex.TypeNames"/> and
    /// <see cref="DirectoryReferenceAssemblyIndex.AssemblyNames"/> supply them.
    /// </remarks>
    public static void Write(
        IReferenceAssemblyIndex source,
        IEnumerable<string> typeNames,
        IEnumerable<string> assemblyNames,
        Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(typeNames);
        ArgumentNullException.ThrowIfNull(assemblyNames);
        ArgumentNullException.ThrowIfNull(destination);

        // Sorted so the output is byte-for-byte reproducible, which lets a checked-in resource be
        // diffed and lets a regeneration be compared rather than trusted.
        var owners = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var identities = new SortedDictionary<string, AssemblyIdentity>(StringComparer.Ordinal);

        foreach (var assemblyName in assemblyNames)
        {
            if (source.TryGetIdentity(assemblyName, out var identity))
            {
                identities[identity.Name] = identity;
            }
        }

        foreach (var typeName in typeNames)
        {
            if (source.TryResolveType(typeName, out var owner))
            {
                owners[typeName] = owner.Name;
                identities[owner.Name] = owner;
            }
        }

        var assemblyOrder = identities.Keys.ToList();
        var assemblyIndex = assemblyOrder
            .Select((name, i) => (name, i))
            .ToDictionary(x => x.name, x => x.i, StringComparer.Ordinal);

        using var compressor = new DeflateStream(destination, CompressionLevel.SmallestSize, leaveOpen: true);
        using var writer = new StreamWriter(compressor, new UTF8Encoding(false)) { NewLine = "\n" };

        writer.WriteLine(FormatVersion);
        writer.WriteLine(assemblyOrder.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var name in assemblyOrder)
        {
            var identity = identities[name];
            writer.WriteLine(string.Join('|',
                identity.Name,
                identity.Version.ToString(),
                identity.CultureName,
                Convert.ToHexString(identity.PublicKeyToken.AsSpan()),
                ((int)identity.Flags).ToString(CultureInfo.InvariantCulture)));
        }

        writer.WriteLine(owners.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var (typeName, owningAssembly) in owners)
        {
            writer.WriteLine($"{typeName}|{assemblyIndex[owningAssembly].ToString(CultureInfo.InvariantCulture)}");
        }
    }

    /// <inheritdoc/>
    public bool TryResolveType(string fullTypeName, [NotNullWhen(true)] out AssemblyIdentity? owner) =>
        _owners.TryGetValue(fullTypeName, out owner);

    /// <inheritdoc/>
    public bool TryGetIdentity(string simpleName, [NotNullWhen(true)] out AssemblyIdentity? identity) =>
        _identities.TryGetValue(simpleName, out identity);

    /// <summary>
    /// Number of indexed types.
    /// </summary>
    public int TypeCount => _owners.Count;

    /// <summary>
    /// Number of indexed assemblies.
    /// </summary>
    public int AssemblyCount => _identities.Count;

    private static EmbeddedReferenceAssemblyIndex FromResource(string resourceName)
    {
        var assembly = typeof(EmbeddedReferenceAssemblyIndex).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new PEPackerException(
                $"Embedded reference index '{resourceName}' is missing from {assembly.GetName().Name}.");

        return new EmbeddedReferenceAssemblyIndex(stream);
    }

    private static List<AssemblyIdentity> ReadAssemblies(
        TextReader reader,
        out Dictionary<string, AssemblyIdentity> byName)
    {
        var count = ReadCount(reader, "assembly count");
        var assemblies = new List<AssemblyIdentity>(count);
        byName = new Dictionary<string, AssemblyIdentity>(count, StringComparer.Ordinal);

        for (int i = 0; i < count; i++)
        {
            var line = reader.ReadLine()
                ?? throw new PEPackerException($"Reference index truncated: expected {count} assemblies.");

            var parts = line.Split('|');
            if (parts.Length != 5)
            {
                throw new PEPackerException($"Malformed assembly entry in reference index: '{line}'.");
            }

            var identity = new AssemblyIdentity(
                parts[0],
                Version.Parse(parts[1]),
                parts[2],
                [.. Convert.FromHexString(parts[3])],
                (AssemblyFlags)int.Parse(parts[4], CultureInfo.InvariantCulture));

            assemblies.Add(identity);
            byName[identity.Name] = identity;
        }

        return assemblies;
    }

    private static Dictionary<string, AssemblyIdentity> ReadOwners(
        TextReader reader,
        List<AssemblyIdentity> assemblies)
    {
        var count = ReadCount(reader, "type count");
        var owners = new Dictionary<string, AssemblyIdentity>(count, StringComparer.Ordinal);

        for (int i = 0; i < count; i++)
        {
            var line = reader.ReadLine()
                ?? throw new PEPackerException($"Reference index truncated: expected {count} types.");

            var separator = line.LastIndexOf('|');
            if (separator <= 0)
            {
                throw new PEPackerException($"Malformed type entry in reference index: '{line}'.");
            }

            var assemblyOrdinal = int.Parse(line.AsSpan(separator + 1), CultureInfo.InvariantCulture);
            if ((uint)assemblyOrdinal >= (uint)assemblies.Count)
            {
                throw new PEPackerException(
                    $"Reference index names assembly {assemblyOrdinal}, which does not exist.");
            }

            owners[line[..separator]] = assemblies[assemblyOrdinal];
        }

        return owners;
    }

    private static int ReadCount(TextReader reader, string what)
    {
        var line = reader.ReadLine();
        return int.TryParse(line, CultureInfo.InvariantCulture, out var count) && count >= 0
            ? count
            : throw new PEPackerException($"Reference index has no valid {what}.");
    }
}
