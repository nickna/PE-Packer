using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;
using static PEPacker.Tests.Infrastructure.RewriterTestHelpers;

namespace PEPacker.Tests;

/// <summary>
/// Covers the rewriter now that framework type resolution is an injected index rather than
/// an eagerly scanned directory.
/// </summary>
public class ReferenceAssemblyIndexTests
{
    /// <summary>
    /// The point of the abstraction: rewriting with no framework assemblies on disk and no
    /// filesystem access of any kind. A Native AOT tool on a machine with no .NET installed
    /// cannot satisfy the directory prerequisite, and callers reaching for
    /// <c>RuntimeEnvironment.GetRuntimeDirectory()</c> get the application's own directory
    /// under AOT, which holds no framework assemblies.
    /// </summary>
    [Fact]
    public void Rewrite_WithAnInMemoryIndex_TouchesNoFilesystemAndRetargetsCoreLib()
    {
        var index = new FakeIndex()
            .WithAssembly("System.Runtime")
            .WithType("System.Object", "System.Runtime")
            .WithType("System.Collections.Generic.List`1", "System.Collections");

        var rewritten = Rewrite(RewriterFixtures.CoreLibReferencingAssembly(), index);
        var references = RewriterFixtures.AssemblyReferenceNames(rewritten);

        Assert.DoesNotContain("System.Private.CoreLib", references);
        Assert.Contains("System.Runtime", references);
    }

    /// <summary>
    /// An index that owns a type in a non-core facade must place the reference there, not
    /// funnel everything into System.Runtime.
    /// </summary>
    [Fact]
    public void Rewrite_WithAnInMemoryIndex_PlacesTypesInTheOwningFacade()
    {
        var index = new FakeIndex()
            .WithAssembly("System.Runtime")
            .WithAssembly("System.Collections")
            .WithType("System.Collections.Generic.List`1", "System.Collections");

        var rewritten = Rewrite(
            RewriterFixtures.CoreLibReferencingAssembly("System.Collections.Generic", "List`1"),
            index);

        Assert.Contains("System.Collections", RewriterFixtures.AssemblyReferenceNames(rewritten));
    }

    /// <summary>
    /// A type the index does not know still has to land somewhere resolvable.
    /// </summary>
    [Fact]
    public void Rewrite_UnknownType_FallsBackToSystemRuntime()
    {
        var index = new FakeIndex().WithAssembly("System.Runtime");

        var rewritten = Rewrite(
            RewriterFixtures.CoreLibReferencingAssembly("Contoso.Unknown", "Widget"), index);

        Assert.Contains("System.Runtime", RewriterFixtures.AssemblyReferenceNames(rewritten));
    }

    [Fact]
    public void Ctor_NullIndex_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AssemblyReferenceRewriter(
            new MemoryStream(RewriterFixtures.CoreLibReferencingAssembly()), referenceIndex: null!));
    }

    /// <summary>
    /// The directory implementation still indexes a real shared framework, so the extraction
    /// did not change what a path-based caller gets.
    /// </summary>
    [Fact]
    public void DirectoryIndex_IndexesTheInstalledFramework()
    {
        var index = new DirectoryReferenceAssemblyIndex(RuntimeEnvironment.GetRuntimeDirectory());

        Assert.True(index.TypeCount > 1000, $"expected a populated index, got {index.TypeCount} types");
        Assert.True(index.AssemblyCount > 10, $"expected many assemblies, got {index.AssemblyCount}");

        Assert.True(index.TryResolveType("System.Object", out var owner));
        Assert.False(string.IsNullOrEmpty(owner.Name));

        Assert.True(index.TryGetIdentity("System.Runtime", out var identity));
        Assert.Equal("System.Runtime", identity.Name);

        // The implementation assembly is never a valid retarget destination.
        Assert.False(index.TryGetIdentity("System.Private.CoreLib", out _));
    }

    /// <summary>
    /// The identity must carry a token, not a full public key, since that is what an
    /// AssemblyRef row holds.
    /// </summary>
    [Fact]
    public void DirectoryIndex_IdentityCarriesATokenNotAFullKey()
    {
        var index = new DirectoryReferenceAssemblyIndex(RuntimeEnvironment.GetRuntimeDirectory());

        Assert.True(index.TryGetIdentity("System.Runtime", out var identity));
        Assert.Equal(8, identity.PublicKeyToken.Length);
        Assert.False(identity.Flags.HasFlag(AssemblyFlags.PublicKey));

        // The well-known ECMA public key token for the Microsoft framework key, which is
        // what SHA-1-of-the-public-key-reversed must produce.
        Assert.Equal("B03F5F7F11D50A3A", Convert.ToHexString(identity.PublicKeyToken.AsSpan()));
    }

    /// <summary>
    /// Only the flags that mean something in an <c>AssemblyRef</c> row may survive.
    /// </summary>
    /// <remarks>
    /// Reading the manifest directly exposes bits that <c>AssemblyName.Flags</c> hid: the
    /// net10.0 reference pack sets 0x70 on <c>System.Runtime</c>, none of it a defined
    /// <see cref="AssemblyFlags"/> value. Propagating those would have changed the emitted
    /// rows relative to every previous release, and changed them differently depending on
    /// whether the caller pointed at a reference pack or a shared framework.
    /// </remarks>
    [Fact]
    public void DirectoryIndex_IdentityFlagsCarryOnlyMeaningfulBits()
    {
        const AssemblyFlags allowed = AssemblyFlags.Retargetable | AssemblyFlags.ContentTypeMask;

        foreach (var dir in ReferenceDirectories())
        {
            var index = new DirectoryReferenceAssemblyIndex(dir);
            Assert.True(index.TryGetIdentity("System.Runtime", out var identity));
            Assert.Equal(default, identity.Flags & ~allowed);
        }
    }

    /// <summary>
    /// Both directory shapes a caller can legitimately pass, when present on this machine.
    /// </summary>
    private static IEnumerable<string> ReferenceDirectories()
    {
        yield return RuntimeEnvironment.GetRuntimeDirectory();

        var refPacks = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(
                RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar)))!)!,
            "packs", "Microsoft.NETCore.App.Ref");

        if (!Directory.Exists(refPacks))
        {
            yield break;
        }

        foreach (var versionDir in Directory.GetDirectories(refPacks))
        {
            var candidate = Path.Combine(versionDir, "ref");
            if (!Directory.Exists(candidate)) continue;

            foreach (var tfm in Directory.GetDirectories(candidate))
            {
                if (Directory.GetFiles(tfm, "System.Runtime.dll").Length > 0)
                {
                    yield return tfm;
                }
            }
        }
    }

    /// <summary>
    /// A type forwarded by several facades must resolve to the specific one, on every platform.
    /// </summary>
    /// <remarks>
    /// A shared framework holds <c>mscorlib</c>, <c>netstandard</c> and <c>System</c> alongside
    /// the granular facades, and all of them forward these types. Indexing is last-wins, so
    /// whichever file came last in directory-enumeration order used to decide — alphabetical on
    /// Windows, filesystem order on Linux. The identical assembly retargeted to
    /// <c>System.Collections</c> on one platform and <c>mscorlib</c> on the other, which was
    /// caught only by running the Native AOT smoke host on linux-x64. Both resolve at run time,
    /// so nothing failed; the output simply was not reproducible.
    /// </remarks>
    [Fact]
    public void DirectoryIndex_PrefersSpecificFacades_OverUmbrellaOnes()
    {
        foreach (var dir in ReferenceDirectories())
        {
            var index = new DirectoryReferenceAssemblyIndex(dir);

            foreach (var (type, expected) in new[]
            {
                ("System.Collections.Generic.Dictionary`2", "System.Collections"),
                ("System.Collections.Generic.List`1", "System.Collections"),
                ("System.Object", "System.Runtime"),
                ("System.Console", "System.Console"),
            })
            {
                Assert.True(index.TryResolveType(type, out var owner), $"{type} unresolved in {dir}");
                Assert.Equal(expected, owner.Name);
            }
        }
    }

    [Fact]
    public void DirectoryIndex_MissingDirectory_ThrowsPEPackerException()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pepacker_idx_" + Guid.NewGuid().ToString("N")[..8]);

        var ex = Assert.Throws<PEPackerException>(() => new DirectoryReferenceAssemblyIndex(missing));

        Assert.Contains(missing, ex.Message);
    }

    /// <summary>
    /// An index with no filesystem behind it, which is what makes the rewriter testable
    /// without an installed framework.
    /// </summary>
    private sealed class FakeIndex : IReferenceAssemblyIndex
    {
        private readonly Dictionary<string, AssemblyIdentity> _identities = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

        public FakeIndex WithAssembly(string name)
        {
            _identities[name] = new AssemblyIdentity(
                name,
                new Version(10, 0, 0, 0),
                string.Empty,
                [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a],
                AssemblyFlags.Retargetable);
            return this;
        }

        public FakeIndex WithType(string fullTypeName, string owningAssembly)
        {
            _owners[fullTypeName] = owningAssembly;
            WithAssembly(owningAssembly);
            return this;
        }

        public bool TryResolveType(string fullTypeName, [NotNullWhen(true)] out AssemblyIdentity? owner)
        {
            owner = null;
            return _owners.TryGetValue(fullTypeName, out var name)
                && _identities.TryGetValue(name, out owner);
        }

        public bool TryGetIdentity(string simpleName, [NotNullWhen(true)] out AssemblyIdentity? identity) =>
            _identities.TryGetValue(simpleName, out identity);
    }
}
