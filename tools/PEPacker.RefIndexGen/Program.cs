using PEPacker;

// Regenerates src/PEPacker/Resources/refindex-net10.0.bin, the precomputed framework type map
// that lets AssemblyReferenceRewriter run with no framework assemblies on disk.
//
//   dotnet run --project tools/PEPacker.RefIndexGen -- <reference-pack-dir> <output-file>
//
// e.g. on Windows:
//   dotnet run --project tools/PEPacker.RefIndexGen -- \
//     "C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/10.0.10/ref/net10.0" \
//     src/PEPacker/Resources/refindex-net10.0.bin
//
// Generate from a *reference pack* rather than a shared framework: that is what a compiler
// references, and its facades define types directly instead of forwarding to
// System.Private.CoreLib. Both produce a working index, but the reference pack is the canonical
// source and resolves nested types as well.
//
// Write is byte-for-byte reproducible for the same input, so regenerating against the same pack
// should produce no diff. A diff means the pack changed, and the change is reviewable.

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "usage: PEPacker.RefIndexGen <reference-pack-dir> <output-file>" + Environment.NewLine +
        "  reference-pack-dir  e.g. <dotnet-root>/packs/Microsoft.NETCore.App.Ref/<ver>/ref/net10.0" +
        Environment.NewLine +
        "  output-file         e.g. src/PEPacker/Resources/refindex-net10.0.bin");
    return 2;
}

var (referencePackDirectory, outputPath) = (args[0], args[1]);

var source = new DirectoryReferenceAssemblyIndex(referencePackDirectory);
Console.WriteLine($"source: {source.TypeCount} types, {source.AssemblyCount} assemblies");
Console.WriteLine($"        {referencePackDirectory}");

var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
if (!string.IsNullOrEmpty(directory))
{
    Directory.CreateDirectory(directory);
}

using (var file = File.Create(outputPath))
{
    EmbeddedReferenceAssemblyIndex.Write(source, source.TypeNames, source.AssemblyNames, file);
}

Console.WriteLine($"written: {outputPath} ({new FileInfo(outputPath).Length:N0} bytes compressed)");

// Read it straight back and confirm it answers identically, so a bad write is caught here rather
// than by a consumer whose rewritten assembly references the wrong facade.
using var readBack = File.OpenRead(outputPath);
var embedded = new EmbeddedReferenceAssemblyIndex(readBack);
Console.WriteLine($"read back: {embedded.TypeCount} types, {embedded.AssemblyCount} assemblies");

var mismatches = 0;

foreach (var typeName in source.TypeNames)
{
    source.TryResolveType(typeName, out var expected);

    if (!embedded.TryResolveType(typeName, out var actual) || actual.Name != expected!.Name)
    {
        if (mismatches++ < 5)
        {
            Console.Error.WriteLine($"  MISMATCH {typeName}: {expected?.Name} -> {actual?.Name}");
        }
    }
}

foreach (var assemblyName in source.AssemblyNames)
{
    source.TryGetIdentity(assemblyName, out var expected);

    if (!embedded.TryGetIdentity(assemblyName, out var actual) || !Equals(expected, actual))
    {
        if (mismatches++ < 10)
        {
            Console.Error.WriteLine($"  MISMATCH identity {assemblyName}");
        }
    }
}

Console.WriteLine(mismatches == 0
    ? "verified: identical for every type and identity"
    : $"FAILED: {mismatches} mismatch(es)");

return mismatches == 0 ? 0 : 1;
