using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using PEPacker.Bundling;
using PEPacker.Tests.Infrastructure;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Bundles for a runtime identifier that is NOT the machine's own, which only the embedded
/// apphost templates can serve, and asserts byte-level properties of the result.
/// </summary>
/// <remarks>
/// The execution-based bundling tests can only ever cover the current RID, and the AOT
/// smoke host's embedded-template proof is same-RID too — so before this test, nothing
/// verified that cross-targeting (say, a linux-x64 executable built on Windows) actually
/// patches the embedded template. The output obviously cannot be run here; instead the
/// assertions check exactly what <see cref="ManualBundler"/> promises: the DLL-path
/// placeholder replaced in place by the payload's bundle path, the bundle-header
/// placeholder patched with an in-range manifest offset, a well-formed manifest header,
/// and the payload embedded page-aligned and byte-identical.
/// </remarks>
public class CrossRidBundlingTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("pepacker_xrid_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
    }

    // These two constants are the apphost format's published placeholders, duplicated here
    // deliberately: asserting against the bundler's own private fields would let both drift
    // together. The DLL path placeholder is the lowercase hex SHA-256 of "foobar"; the
    // bundle marker is the SHA-256 of ".net core bundle", preceded in the template by an
    // 8-byte zeroed header-offset field that bundling must patch.
    private static readonly byte[] DllPathPlaceholder =
        Encoding.UTF8.GetBytes("c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2");

    private static readonly byte[] BundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];

    [Fact]
    public void BuiltInBundler_ForAForeignRid_PatchesTheEmbeddedTemplate()
    {
        // A RID this machine is not running, so nothing on disk can accidentally serve it.
        var foreignRid = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";
        Assert.True(EmbeddedAppHostProvider.TryRead(foreignRid, out var template),
            $"no embedded template for '{foreignRid}'");

        var libPath = Path.Combine(_work, "CrossRidLib.dll");
        File.WriteAllBytes(libPath, RewriterTestHelpers.Build("CrossRidLib", EmitTrivialType));

        var suffix = foreignRid.StartsWith("win", StringComparison.Ordinal) ? ".exe" : string.Empty;
        var outPath = Path.Combine(_work, "out", "CrossRidLib" + suffix);

        var result = AppHostGenerator.CreateSingleFileExecutable(new BundleRequest
        {
            EntryAssemblyPath = libPath,
            OutputPath = outPath,
            AssemblyName = "CrossRidLib",
            RuntimeIdentifier = foreignRid,
        }, BundlerMode.BuiltIn);

        Assert.Equal(BundleTechnique.ManualBundler, result.Technique);

        var output = File.ReadAllBytes(outPath);

        // The output must be the template plus payload — never smaller, never a rewrite.
        Assert.True(output.Length > template!.Length,
            $"output ({output.Length} bytes) is not larger than the template ({template.Length} bytes)");

        // Right executable format for the *target*, not the build machine.
        if (foreignRid.StartsWith("win", StringComparison.Ordinal))
        {
            Assert.Equal((byte)'M', output[0]);
            Assert.Equal((byte)'Z', output[1]);
        }
        else
        {
            Assert.Equal([0x7f, (byte)'E', (byte)'L', (byte)'F'], output[..4]);
        }

        // The DLL-path placeholder must be replaced in place with the payload's bundle
        // path, NUL-terminated, and must appear nowhere in the output.
        int placeholderIndex = Find(template, DllPathPlaceholder);
        Assert.True(placeholderIndex >= 0, "the template does not carry the DLL path placeholder");
        Assert.True(Find(output, DllPathPlaceholder) < 0, "the output still carries the DLL path placeholder");
        var expectedPath = Encoding.UTF8.GetBytes("CrossRidLib.dll\0");
        Assert.Equal(expectedPath, output.AsSpan(placeholderIndex, expectedPath.Length).ToArray());

        // The bundle marker survives, and the 8 bytes before it — zero in the template —
        // now hold the manifest offset, which must land inside the appended region.
        int sigIndex = Find(output, BundleSignature);
        Assert.True(sigIndex >= 8, "the output does not carry the bundle signature");
        Assert.Equal(sigIndex, Find(template, BundleSignature));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(template.AsSpan(sigIndex - 8, 8)));

        long manifestOffset = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(sigIndex - 8, 8));
        Assert.InRange(manifestOffset, template.Length, output.Length - 1);

        // Manifest header: bundle format 6.0 with exactly the assembly and its
        // generated runtimeconfig.json.
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan((int)manifestOffset, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan((int)manifestOffset + 4, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan((int)manifestOffset + 8, 4)));

        // The payload itself is embedded byte-identical at a page-aligned offset, since
        // the host memory-maps assemblies straight out of the bundle.
        var payload = File.ReadAllBytes(libPath);
        long payloadOffset = FindAligned(output, payload);
        Assert.True(payloadOffset > 0, "the payload assembly was not found at a 4096-aligned offset");
    }

    /// <summary>
    /// The macOS refusal must hold for the embedded-template path too: no osx template is
    /// embedded, and the guard, not a template-not-found error, is what the caller sees.
    /// </summary>
    [Fact]
    public void BuiltInBundler_ForAMacRid_StillRefuses()
    {
        var libPath = Path.Combine(_work, "CrossRidLib.dll");
        File.WriteAllBytes(libPath, RewriterTestHelpers.Build("CrossRidLib", EmitTrivialType));

        var ex = Assert.Throws<PEPackerException>(() => new ManualBundler().CreateSingleFileExecutable(
            new BundleRequest
            {
                EntryAssemblyPath = libPath,
                OutputPath = Path.Combine(_work, "mac.out"),
                AssemblyName = "CrossRidLib",
                RuntimeIdentifier = "osx-x64",
            }));

        Assert.Contains("osx-x64", ex.Message);
    }

    private static void EmitTrivialType(ModuleBuilder module)
    {
        var type = module.DefineType("CrossRid.Probe", TypeAttributes.Public);
        var method = type.DefineMethod("Answer",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_S, (byte)42);
        il.Emit(OpCodes.Ret);
        type.CreateType();
    }

    private static int Find(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }

    /// <summary>Finds <paramref name="needle"/> at a 4096-aligned offset, or -1.</summary>
    private static long FindAligned(byte[] haystack, byte[] needle)
    {
        for (long offset = 4096; offset + needle.Length <= haystack.Length; offset += 4096)
        {
            if (haystack.AsSpan((int)offset, needle.Length).SequenceEqual(needle)) return offset;
        }
        return -1;
    }
}
