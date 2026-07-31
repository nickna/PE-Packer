using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace PEPacker.Tests.Infrastructure;

/// <summary>
/// The build/rewrite/execute helpers every rewriter test needs, in one place.
/// </summary>
/// <remarks>
/// These were previously copy-pasted near-identically into each rewriter test class. Import
/// with <c>using static PEPacker.Tests.Infrastructure.RewriterTestHelpers;</c> so call sites
/// keep reading as <c>Build(...)</c> / <c>Rewrite(...)</c> / <c>Execute(...)</c>.
/// </remarks>
internal static class RewriterTestHelpers
{
    /// <summary>
    /// Emits an assembly with <see cref="PersistedAssemblyBuilder"/> and returns its image.
    /// </summary>
    internal static byte[] Build(string name, Action<ModuleBuilder> emit)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        emit(ab.DefineDynamicModule(name));

        using var stream = new MemoryStream();
        ab.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Rewrites an image against the installed shared framework with the default policy.
    /// </summary>
    /// <remarks>
    /// Only a directory containing System.Runtime.dll and the BCL facades is needed; the
    /// shared-framework runtime directory always qualifies under the JIT-hosted test run.
    /// (Under Native AOT it would not — see the AotSmoke host — but this suite is managed.)
    /// </remarks>
    internal static byte[] Rewrite(byte[] source) =>
        Rewrite(source, (Func<string, ReferenceAction>?)null);

    /// <summary>
    /// Rewrites an image against the installed shared framework with an explicit policy,
    /// or the default policy when <paramref name="policy"/> is null.
    /// </summary>
    internal static byte[] Rewrite(byte[] source, Func<string, ReferenceAction>? policy)
    {
        using var rewriter = policy is null
            ? new AssemblyReferenceRewriter(
                new MemoryStream(source), RuntimeEnvironment.GetRuntimeDirectory())
            : new AssemblyReferenceRewriter(
                new MemoryStream(source), RuntimeEnvironment.GetRuntimeDirectory(), policy);

        return RewriteAndSave(rewriter);
    }

    /// <summary>
    /// Rewrites an image against an explicit reference index.
    /// </summary>
    internal static byte[] Rewrite(byte[] source, IReferenceAssemblyIndex index)
    {
        using var rewriter = new AssemblyReferenceRewriter(new MemoryStream(source), index);
        return RewriteAndSave(rewriter);
    }

    /// <summary>
    /// Loads an image into a collectible <see cref="AssemblyLoadContext"/>, runs the
    /// assertions, and unloads.
    /// </summary>
    internal static void Execute(byte[] image, Action<Assembly> assertions)
    {
        var alc = new AssemblyLoadContext("rewritten-fixture", isCollectible: true);
        try
        {
            using var ms = new MemoryStream(image);
            assertions(alc.LoadFromStream(ms));
        }
        finally
        {
            alc.Unload();
        }
    }

    private static byte[] RewriteAndSave(AssemblyReferenceRewriter rewriter)
    {
        rewriter.Rewrite();

        using var output = new MemoryStream();
        rewriter.Save(output);
        return output.ToArray();
    }
}
