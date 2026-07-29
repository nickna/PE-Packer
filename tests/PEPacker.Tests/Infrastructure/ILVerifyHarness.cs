using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ILVerify;

namespace PEPacker.Tests.Infrastructure;

/// <summary>
/// Runs Microsoft.ILVerification over an assembly image.
/// </summary>
/// <remarks>
/// <para>
/// This checks something <see cref="MetadataDiffer"/> cannot. The differ proves the
/// rewritten metadata says the same thing as the input; ILVerify proves the result is
/// well-formed by an independent implementation. Those come apart: <c>$Runtime.ConsoleClear</c>
/// in real SharpTS output has byte-identical IL before and after the rewrite — so the
/// differ is satisfied — yet declares MaxStack 0 while its catch handler needs 1, which
/// the CoreCLR JIT tolerates and a strict consumer does not.
/// </para>
/// <para>
/// Everything resolves from the shared-framework runtime directory rather than from
/// reference assemblies. Mixing the two gives core types two identities and ILVerify then
/// objects to nearly every stack interaction.
/// </para>
/// </remarks>
internal sealed class ILVerifyHarness : IResolver, IDisposable
{
    private readonly Dictionary<string, PEReader> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _probeDirectory;
    private bool _disposed;

    public ILVerifyHarness()
    {
        _probeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)
            ?? throw new InvalidOperationException("Could not locate the runtime directory.");
    }

    /// <summary>
    /// Verifies every method with a body and returns one entry per finding, keyed by
    /// declaring type, method and error code so results are comparable across two builds
    /// of the same source.
    /// </summary>
    public List<string> Verify(byte[] image)
    {
        var findings = new List<string>();

        using var peReader = new PEReader(new MemoryStream(image));
        var reader = peReader.GetMetadataReader();

        var verifier = new Verifier(this, new VerifierOptions
        {
            IncludeMetadataTokensInErrorMessages = false,
            SanityChecks = true,
        });
        verifier.SetSystemModuleName(new AssemblyNameInfo("System.Runtime"));

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) continue;

            var name = $"{TypeName(reader, method.GetDeclaringType())}.{reader.GetString(method.Name)}";

            try
            {
                foreach (var result in verifier.Verify(peReader, methodHandle))
                {
                    findings.Add($"{name}: {result.Code}");
                }
            }
            catch (Exception ex)
            {
                findings.Add($"{name}: verifier threw {ex.GetType().Name}");
            }
        }

        return findings;
    }

    private static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        if (handle.IsNil) return "<unknown>";

        var type = reader.GetTypeDefinition(handle);
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return ns.Length > 0 ? $"{ns}.{name}" : name;
    }

    public PEReader? ResolveAssembly(AssemblyNameInfo assemblyName)
    {
        var name = assemblyName.Name ?? string.Empty;
        if (_cache.TryGetValue(name, out var cached)) return cached;

        var path = Path.Combine(_probeDirectory, name + ".dll");
        if (!File.Exists(path)) return null;

        var reader = new PEReader(File.OpenRead(path));
        _cache[name] = reader;
        return reader;
    }

    public PEReader? ResolveModule(AssemblyNameInfo referencingAssembly, string fileName) => null;

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var reader in _cache.Values) reader.Dispose();
        _cache.Clear();
        _disposed = true;
    }
}
