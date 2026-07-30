using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace PEPacker;

public partial class AssemblyReferenceRewriter
{
    private void CopyAssemblyDefinition()
    {
        var assemblyDef = _reader.GetAssemblyDefinition();

        _metadata.AddAssembly(
            GetOrAddString(_reader.GetString(assemblyDef.Name)),
            assemblyDef.Version,
            GetOrAddString(_reader.GetString(assemblyDef.Culture)),
            GetOrAddBlob(_reader.GetBlobBytes(assemblyDef.PublicKey)),
            assemblyDef.Flags,
            assemblyDef.HashAlgorithm);
    }

    private void CopyModuleDefinition()
    {
        var moduleDef = _reader.GetModuleDefinition();

        _metadata.AddModule(
            moduleDef.Generation,
            GetOrAddString(_reader.GetString(moduleDef.Name)),
            GetOrAddGuid(_reader.GetGuid(moduleDef.Mvid)),
            GetOrAddGuid(moduleDef.GenerationId.IsNil ? default : _reader.GetGuid(moduleDef.GenerationId)),
            GetOrAddGuid(moduleDef.BaseGenerationId.IsNil ? default : _reader.GetGuid(moduleDef.BaseGenerationId)));
    }

    /// <summary>
    /// Copies the ModuleRef table in source order, preserving row numbering.
    /// </summary>
    /// <remarks>
    /// ModuleRef names the native library behind a P/Invoke and can also serve as a
    /// TypeRef resolution scope. It was previously not copied at all, so every
    /// <c>DllImport</c> lost the library it pointed at.
    /// </remarks>
    private void CopyModuleReferences()
    {
        int moduleRefCount = _reader.GetTableRowCount(TableIndex.ModuleRef);
        for (int row = 1; row <= moduleRefCount; row++)
        {
            var handle = MetadataTokens.ModuleReferenceHandle(row);
            var moduleRef = _reader.GetModuleReference(handle);

            _moduleRefMap[handle] = _metadata.AddModuleReference(
                GetOrAddString(_reader.GetString(moduleRef.Name)));
        }
    }

    private void CreateAssemblyReferences()
    {
        // Determine which target assemblies we need based on types used
        HashSet<string> neededAssemblies = [];

        foreach (var typeRefHandle in _reader.TypeReferences)
        {
            var typeRef = _reader.GetTypeReference(typeRefHandle);
            var scope = typeRef.ResolutionScope;

            // Only rewrite references from System.Private.CoreLib
            if (scope.Kind == HandleKind.AssemblyReference)
            {
                var asmRef = _reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
                var asmName = _reader.GetString(asmRef.Name);

                if (_referencePolicy(asmName) == ReferenceAction.RetargetToFacades)
                {
                    var typeName = GetFullTypeName(typeRef);

                    // Default to System.Runtime for types the index does not know.
                    neededAssemblies.Add(_referenceIndex.TryResolveType(typeName, out var owner)
                        ? owner.Name
                        : "System.Runtime");
                }
            }
        }

        // Always include System.Runtime as the core runtime assembly. A directory-backed
        // index sees files in different orders on Windows (alphabetical) and Linux (inode
        // order), which changes which assembly wins for a type defined in more than one, so
        // pinning the core facade keeps the output deterministic.
        neededAssemblies.Add("System.Runtime");

        // Create references for all needed SDK assemblies. The identity already carries a
        // public key token rather than a full key, with the PublicKey flag cleared to match.
        foreach (var asmName in neededAssemblies)
        {
            if (_referenceIndex.TryGetIdentity(asmName, out var identity))
            {
                var handle = _metadata.AddAssemblyReference(
                    GetOrAddString(identity.Name),
                    identity.Version,
                    GetOrAddString(identity.CultureName),
                    GetOrAddBlob(identity.PublicKeyToken.IsDefaultOrEmpty ? [] : [.. identity.PublicKeyToken]),
                    identity.Flags,
                    default);

                _newAssemblyRefs[asmName] = handle;
            }
        }

        // Copy the references the policy keeps. Both Drop and RetargetToFacades mean the
        // source row is not reproduced: a retargeted reference is replaced by the facade
        // rows created above, and a dropped one is simply absent.
        foreach (var asmRefHandle in _reader.AssemblyReferences)
        {
            var asmRef = _reader.GetAssemblyReference(asmRefHandle);
            var name = _reader.GetString(asmRef.Name);

            if (_referencePolicy(name) != ReferenceAction.Keep)
                continue;

            // The facade set above may already have created a row for this name — the
            // source can reference an assembly directly and also, through a CoreLib type,
            // have it selected as a retarget destination. Emitting both left two
            // AssemblyRef rows for one assembly; point the source handle at the existing
            // row instead.
            if (_newAssemblyRefs.TryGetValue(name, out var existing))
            {
                _assemblyRefMap[asmRefHandle] = existing;
                continue;
            }

            var newHandle = _metadata.AddAssemblyReference(
                GetOrAddString(name),
                asmRef.Version,
                GetOrAddString(_reader.GetString(asmRef.Culture)),
                GetOrAddBlob(_reader.GetBlobBytes(asmRef.PublicKeyOrToken)),
                asmRef.Flags,
                GetOrAddBlob(_reader.GetBlobBytes(asmRef.HashValue)));

            _assemblyRefMap[asmRefHandle] = newHandle;
            _newAssemblyRefs[name] = newHandle;
        }
    }
}
