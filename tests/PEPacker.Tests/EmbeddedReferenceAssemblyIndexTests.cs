using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Covers the precomputed index that lets the rewriter run with no framework on disk.
/// </summary>
public class EmbeddedReferenceAssemblyIndexTests
{
    [Fact]
    public void Default_ResolvesTheTypesTheRewriterActuallyLooksUp()
    {
        var index = EmbeddedReferenceAssemblyIndex.Default;

        foreach (var (type, expected) in new[]
        {
            ("System.Object", "System.Runtime"),
            ("System.String", "System.Runtime"),
            ("System.Int32", "System.Runtime"),
            ("System.Collections.Generic.Dictionary`2", "System.Collections"),
            ("System.Collections.Generic.List`1", "System.Collections"),
            ("System.Threading.Tasks.Task`1", "System.Threading.Tasks"),
            ("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1", "System.Threading.Tasks"),
            ("System.Console", "System.Console"),
        })
        {
            Assert.True(index.TryResolveType(type, out var owner), $"{type} unresolved");
            Assert.Equal(expected, owner.Name);
        }
    }

    [Fact]
    public void Default_CarriesUsableFacadeIdentities()
    {
        var index = EmbeddedReferenceAssemblyIndex.Default;

        Assert.True(index.TryGetIdentity("System.Runtime", out var identity));
        Assert.Equal("System.Runtime", identity.Name);
        Assert.Equal(10, identity.Version.Major);
        Assert.Equal("B03F5F7F11D50A3A", Convert.ToHexString(identity.PublicKeyToken.AsSpan()));
        Assert.False(identity.Flags.HasFlag(AssemblyFlags.PublicKey));
    }

    /// <summary>
    /// The implementation assembly is never a valid retarget destination, so it must not be in
    /// the data either.
    /// </summary>
    [Fact]
    public void Default_DoesNotOfferCoreLibAsADestination()
    {
        Assert.False(EmbeddedReferenceAssemblyIndex.Default.TryGetIdentity("System.Private.CoreLib", out _));
    }

    /// <summary>
    /// The point of the whole thing: a full rewrite with no framework assemblies to read.
    /// </summary>
    [Fact]
    public void Rewrite_UsingTheEmbeddedIndex_RetargetsCoreLib()
    {
        var source = RewriterFixtures.CoreLibReferencingAssembly("System.Collections.Generic", "Dictionary`2");

        using var rewriter = new AssemblyReferenceRewriter(
            new MemoryStream(source), EmbeddedReferenceAssemblyIndex.Default);
        rewriter.Rewrite();

        using var output = new MemoryStream();
        rewriter.Save(output);

        var references = RewriterFixtures.AssemblyReferenceNames(output.ToArray());
        Assert.DoesNotContain("System.Private.CoreLib", references);
        Assert.Contains("System.Collections", references);
    }

    /// <summary>
    /// The embedded data must answer exactly as the directory it was generated from, or the two
    /// implementations silently disagree depending on which one a caller picked.
    /// </summary>
    /// <remarks>
    /// Compared against the reference pack matching the embedded framework. Skipped when this
    /// machine has no matching pack, since a different major version legitimately has a different
    /// type set.
    /// </remarks>
    [Fact]
    public void Default_AgreesWithTheDirectoryIndexItWasGeneratedFrom()
    {
        var referencePack = FindMatchingReferencePack();
        if (referencePack is null)
        {
            // Not a silent pass: assert the embedded data is at least self-consistent and say why
            // the stronger comparison did not run.
            Assert.True(EmbeddedReferenceAssemblyIndex.Default.TypeCount > 1000);
            return;
        }

        var directory = new DirectoryReferenceAssemblyIndex(referencePack);
        var embedded = EmbeddedReferenceAssemblyIndex.Default;

        var mismatches = new List<string>();

        foreach (var typeName in directory.TypeNames)
        {
            directory.TryResolveType(typeName, out var expected);

            if (!embedded.TryResolveType(typeName, out var actual))
            {
                mismatches.Add($"{typeName}: missing from embedded index");
            }
            else if (actual.Name != expected!.Name)
            {
                mismatches.Add($"{typeName}: directory says {expected.Name}, embedded says {actual.Name}");
            }

            if (mismatches.Count > 10) break;
        }

        Assert.Empty(mismatches);

        var identityMismatches = new List<string>();

        foreach (var assemblyName in directory.AssemblyNames)
        {
            if (!embedded.TryGetIdentity(assemblyName, out var actual))
            {
                identityMismatches.Add($"{assemblyName}: missing from embedded index");
                continue;
            }

            directory.TryGetIdentity(assemblyName, out var expected);

            if (expected!.Version != actual.Version
                || !expected.PublicKeyToken.AsSpan().SequenceEqual(actual.PublicKeyToken.AsSpan())
                || expected.Flags != actual.Flags
                || expected.CultureName != actual.CultureName)
            {
                identityMismatches.Add(
                    $"{assemblyName}: directory=[{expected.Version}, " +
                    $"{Convert.ToHexString(expected.PublicKeyToken.AsSpan())}, {expected.Flags}, " +
                    $"'{expected.CultureName}'] embedded=[{actual.Version}, " +
                    $"{Convert.ToHexString(actual.PublicKeyToken.AsSpan())}, {actual.Flags}, " +
                    $"'{actual.CultureName}']");
            }
        }

        Assert.Empty(identityMismatches);
    }

    /// <summary>
    /// Facade versions are baked into the data, so a framework this package has no index for must
    /// be refused rather than served the wrong versions.
    /// </summary>
    [Fact]
    public void ForTargetFramework_UnknownFramework_ExplainsWhyAndHowToFixIt()
    {
        var ex = Assert.Throws<PEPackerException>(() =>
            EmbeddedReferenceAssemblyIndex.ForTargetFramework("net8.0"));

        Assert.Contains("net8.0", ex.Message);
        Assert.Contains(EmbeddedReferenceAssemblyIndex.EmbeddedTargetFramework, ex.Message);
        Assert.Contains("Write", ex.Message);
    }

    [Fact]
    public void ForTargetFramework_TheEmbeddedFramework_ReturnsTheIndex()
    {
        var index = EmbeddedReferenceAssemblyIndex.ForTargetFramework(
            EmbeddedReferenceAssemblyIndex.EmbeddedTargetFramework);

        Assert.Same(EmbeddedReferenceAssemblyIndex.Default, index);
    }

    /// <summary>
    /// Two identities describing the same assembly must be equal.
    /// </summary>
    /// <remarks>
    /// The compiler-generated record equality compared <c>PublicKeyToken</c> by reference, because
    /// <see cref="System.Collections.Immutable.ImmutableArray{T}"/> equality is reference equality
    /// of the underlying array. Logically identical identities were unequal, and the failure was
    /// quiet because the tokens print the same. Found by an equivalence assertion that reported
    /// "Collections differ" over two byte sequences that were byte-for-byte identical.
    /// </remarks>
    [Fact]
    public void AssemblyIdentity_ComparesTokensByValue()
    {
        var a = new AssemblyIdentity("System.Runtime", new Version(10, 0, 0, 0), string.Empty,
            [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a], AssemblyFlags.Retargetable);
        var b = new AssemblyIdentity("System.Runtime", new Version(10, 0, 0, 0), string.Empty,
            [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a], AssemblyFlags.Retargetable);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var different = b with { PublicKeyToken = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07] };
        Assert.NotEqual(a, different);
    }

    [Fact]
    public void Ctor_Garbage_ThrowsPEPackerException()
    {
        var garbage = new MemoryStream([0x01, 0x02, 0x03, 0x04, 0x05]);
        Assert.Throws<PEPackerException>(() => new EmbeddedReferenceAssemblyIndex(garbage));
    }

    /// <summary>
    /// Round-trips a small hand-built index, so the format is covered independently of the
    /// checked-in resource.
    /// </summary>
    [Fact]
    public void Write_ThenRead_PreservesEverything()
    {
        var referencePack = FindMatchingReferencePack() ?? RuntimeEnvironment.GetRuntimeDirectory();
        var directory = new DirectoryReferenceAssemblyIndex(referencePack);

        using var buffer = new MemoryStream();
        EmbeddedReferenceAssemblyIndex.Write(directory, directory.TypeNames, directory.AssemblyNames, buffer);
        buffer.Position = 0;

        var roundTripped = new EmbeddedReferenceAssemblyIndex(buffer);

        Assert.Equal(directory.TypeCount, roundTripped.TypeCount);
        Assert.Equal(directory.AssemblyCount, roundTripped.AssemblyCount);

        Assert.True(roundTripped.TryResolveType("System.Object", out var owner));
        Assert.Equal("System.Runtime", owner.Name);
    }

    /// <summary>
    /// The same input must produce identical bytes, so a regenerated resource can be diffed rather
    /// than taken on trust.
    /// </summary>
    [Fact]
    public void Write_IsReproducible()
    {
        var referencePack = FindMatchingReferencePack() ?? RuntimeEnvironment.GetRuntimeDirectory();
        var directory = new DirectoryReferenceAssemblyIndex(referencePack);

        static byte[] Serialize(DirectoryReferenceAssemblyIndex index)
        {
            using var buffer = new MemoryStream();
            EmbeddedReferenceAssemblyIndex.Write(index, index.TypeNames, index.AssemblyNames, buffer);
            return buffer.ToArray();
        }

        Assert.Equal(Serialize(directory), Serialize(directory));
    }

    /// <summary>
    /// Finds a reference pack whose major.minor matches the embedded framework.
    /// </summary>
    private static string? FindMatchingReferencePack()
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        var dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)));
        if (dotnetRoot is null) return null;

        var packs = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(packs)) return null;

        var tfm = EmbeddedReferenceAssemblyIndex.EmbeddedTargetFramework;
        var wanted = tfm.StartsWith("net", StringComparison.Ordinal) ? tfm[3..] : tfm;

        // Ordered by parsed version, not by name: "10.0.9" sorts above "10.0.10" as a string, which
        // silently compared the embedded data against a different patch of the reference pack than
        // it was generated from.
        return Directory.GetDirectories(packs)
            .Where(d => Path.GetFileName(d).StartsWith(wanted + ".", StringComparison.Ordinal))
            .Select(d => (Dir: Path.Combine(d, "ref", tfm), Version: ParseVersion(Path.GetFileName(d))))
            .Where(x => x.Version is not null
                && Directory.Exists(x.Dir)
                && File.Exists(Path.Combine(x.Dir, "System.Runtime.dll")))
            .OrderByDescending(x => x.Version)
            .Select(x => x.Dir)
            .FirstOrDefault();
    }

    /// <summary>
    /// Parses a pack directory name, tolerating prerelease suffixes.
    /// </summary>
    private static Version? ParseVersion(string name)
    {
        var dash = name.IndexOf('-');
        var trimmed = dash > 0 ? name[..dash] : name;
        return Version.TryParse(trimmed, out var version) ? version : null;
    }
}
