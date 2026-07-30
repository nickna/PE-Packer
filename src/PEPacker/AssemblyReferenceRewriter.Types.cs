using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace PEPacker;

public partial class AssemblyReferenceRewriter
{
    private void CopyTypeReferences()
    {
        // Process in order to handle nested types correctly
        foreach (var typeRefHandle in _reader.TypeReferences)
        {
            var typeRef = _reader.GetTypeReference(typeRefHandle);
            var name = _reader.GetString(typeRef.Name);
            var ns = _reader.GetString(typeRef.Namespace);
            var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

            EntityHandle newResolutionScope;

            switch (typeRef.ResolutionScope.Kind)
            {
                case HandleKind.AssemblyReference:
                    {
                        var oldAsmRef = (AssemblyReferenceHandle)typeRef.ResolutionScope;
                        var oldAsmName = _reader.GetString(_reader.GetAssemblyReference(oldAsmRef).Name);

                        if (_referencePolicy(oldAsmName) == ReferenceAction.RetargetToFacades)
                        {
                            // Redirect to appropriate SDK assembly
                            var targetAsm = _typeToAssembly.GetValueOrDefault(fullName, "System.Runtime");
                            newResolutionScope = _newAssemblyRefs.GetValueOrDefault(targetAsm,
                                _newAssemblyRefs.GetValueOrDefault("System.Runtime", default));
                        }
                        else
                        {
                            // A dropped reference has no row, so this map can legitimately have no
                            // entry. A bare indexer turned that into a KeyNotFoundException naming
                            // neither the type nor the reference — matching the ModuleReference arm
                            // below is the least a caller needs.
                            if (!_assemblyRefMap.TryGetValue(oldAsmRef, out var newAsmRef))
                            {
                                throw new PEPackerException(
                                    $"Type reference '{fullName}' is scoped to assembly reference " +
                                    $"'{oldAsmName}' (0x{MetadataTokens.GetToken(oldAsmRef):X8}), " +
                                    "which the reference policy dropped, so the type reference " +
                                    "cannot be remapped. Either the source should not depend on " +
                                    $"'{oldAsmName}', or pass a policy that keeps it — see " +
                                    "ReferencePolicy.RetargetCoreLibOnly.");
                            }
                            newResolutionScope = newAsmRef;
                        }
                        break;
                    }

                case HandleKind.TypeReference:
                    {
                        // Nested type - resolve through parent
                        newResolutionScope = _typeRefMap[(TypeReferenceHandle)typeRef.ResolutionScope];
                        break;
                    }

                case HandleKind.ModuleReference:
                    {
                        // A nil scope means "search the ExportedType table" (ECMA-335
                        // II.22.38), which is a different lookup entirely — so map the
                        // reference rather than dropping it.
                        var oldModuleRef = (ModuleReferenceHandle)typeRef.ResolutionScope;
                        if (!_moduleRefMap.TryGetValue(oldModuleRef, out var newModuleRef))
                        {
                            throw new PEPackerException(
                                $"Type reference '{fullName}' is scoped to module reference " +
                                $"0x{MetadataTokens.GetToken(oldModuleRef):X8}, which was not copied.");
                        }
                        newResolutionScope = newModuleRef;
                        break;
                    }

                case HandleKind.ModuleDefinition:
                default:
                    // Scoped to this module, or genuinely nil.
                    newResolutionScope = default;
                    break;
            }

            var newHandle = _metadata.AddTypeReference(
                newResolutionScope,
                GetOrAddString(ns),
                GetOrAddString(name));

            _typeRefMap[typeRefHandle] = newHandle;
        }
    }

    private void CopyTypeSpecifications()
    {
        // Iterate through TypeSpec table
        int typeSpecCount = _reader.GetTableRowCount(TableIndex.TypeSpec);
        for (int row = 1; row <= typeSpecCount; row++)
        {
            var typeSpecHandle = MetadataTokens.TypeSpecificationHandle(row);
            var typeSpec = _reader.GetTypeSpecification(typeSpecHandle);
            var reader = _reader.GetBlobReader(typeSpec.Signature);

            // Rewrite the signature blob to use new type tokens
            var newSignature = RewriteTypeSignature(reader);

            var newHandle = _metadata.AddTypeSpecification(
                _metadata.GetOrAddBlob(newSignature));

            _typeSpecMap[typeSpecHandle] = newHandle;
        }
    }

    // Run-pointer starts, computed by PredictDefinitionHandles and consumed by
    // AddTypeDefinitions once type specifications exist.
    private readonly Dictionary<TypeDefinitionHandle, int> _typeFirstField = [];
    private readonly Dictionary<TypeDefinitionHandle, int> _typeFirstMethod = [];

    /// <summary>
    /// Assigns every TypeDef, Field and MethodDef its target row up front, without
    /// emitting anything.
    /// </summary>
    /// <remarks>
    /// These three tables are copied one-for-one in source order, so their target rows
    /// are known in advance. Reserving them first breaks the cycle between type
    /// definitions and type specifications: a TypeDef's base type may be a TypeSpec, and
    /// a TypeSpec's signature may name a TypeDef, so neither can be emitted first.
    /// Emitting TypeDefs before TypeSpecs existed used to leave the base-type lookup
    /// unmapped, which went unnoticed only because the fallback returned the source
    /// handle and the TypeSpec table happened to be numbered identically.
    /// </remarks>
    private void PredictDefinitionHandles()
    {
        int typeRow = 1;
        int fieldRow = 1;
        int methodRow = 1;

        foreach (var typeDefHandle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(typeDefHandle);

            _typeDefMap[typeDefHandle] = MetadataTokens.TypeDefinitionHandle(typeRow++);
            _typeFirstField[typeDefHandle] = fieldRow;
            _typeFirstMethod[typeDefHandle] = methodRow;

            foreach (var fieldHandle in typeDef.GetFields())
            {
                _fieldDefMap[fieldHandle] = MetadataTokens.FieldDefinitionHandle(fieldRow++);
            }

            foreach (var methodHandle in typeDef.GetMethods())
            {
                _methodDefMap[methodHandle] = MetadataTokens.MethodDefinitionHandle(methodRow++);
            }
        }
    }

    /// <summary>
    /// Emits the TypeDef rows reserved by <see cref="PredictDefinitionHandles"/>.
    /// </summary>
    private void AddTypeDefinitions()
    {
        foreach (var typeDefHandle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(typeDefHandle);

            var newHandle = _metadata.AddTypeDefinition(
                typeDef.Attributes,
                GetOrAddString(_reader.GetString(typeDef.Namespace)),
                GetOrAddString(_reader.GetString(typeDef.Name)),
                MapEntityHandle(typeDef.BaseType),
                MetadataTokens.FieldDefinitionHandle(_typeFirstField[typeDefHandle]),
                MetadataTokens.MethodDefinitionHandle(_typeFirstMethod[typeDefHandle]));

            // Everything referring to this type already points at the reserved row.
            if (newHandle != _typeDefMap[typeDefHandle])
            {
                throw new PEPackerException(
                    $"Type '{_reader.GetString(typeDef.Name)}' landed at row " +
                    $"{MetadataTokens.GetRowNumber(newHandle)} but was reserved at row " +
                    $"{MetadataTokens.GetRowNumber(_typeDefMap[typeDefHandle])}.");
            }

            // ClassLayout carries explicit size and packing. Besides interop structs it
            // is how static initialized data is declared, whose bytes FieldRVA already
            // carries — copying one without the other describes the data with no type.
            // The table is sorted by Parent, and types are emitted in order, so appending
            // here keeps it sorted.
            var layout = typeDef.GetLayout();
            if (!layout.IsDefault)
            {
                _metadata.AddTypeLayout(newHandle, (ushort)layout.PackingSize, (uint)layout.Size);
            }
        }
    }
}
