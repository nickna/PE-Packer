using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace PEPacker;

public partial class AssemblyReferenceRewriter
{
    #region Helper Methods

    private string GetFullTypeName(TypeReference typeRef)
    {
        var name = _reader.GetString(typeRef.Name);
        var ns = _reader.GetString(typeRef.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    /// <summary>
    /// Translates a source handle into its counterpart in the assembly being built.
    /// </summary>
    /// <remarks>
    /// Every mapped kind now fails on a miss. These lookups previously fell back to the
    /// source handle, which silently kept the old row number — correct only while a table
    /// happened to be copied in source order. That assumption is exactly what broke when
    /// StandAloneSig rows were renumbered and <c>calli</c> followed a stale row into a
    /// local-variable signature.
    /// </remarks>
    private EntityHandle MapEntityHandle(EntityHandle handle)
    {
        if (handle.IsNil)
            return handle;

        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                if (_typeRefMap.TryGetValue((TypeReferenceHandle)handle, out var typeRef)) return typeRef;
                break;

            case HandleKind.TypeDefinition:
                if (_typeDefMap.TryGetValue((TypeDefinitionHandle)handle, out var typeDef)) return typeDef;
                break;

            case HandleKind.TypeSpecification:
                if (_typeSpecMap.TryGetValue((TypeSpecificationHandle)handle, out var typeSpec)) return typeSpec;
                break;

            case HandleKind.MemberReference:
                if (_memberRefMap.TryGetValue((MemberReferenceHandle)handle, out var memberRef)) return memberRef;
                break;

            case HandleKind.MethodDefinition:
                if (_methodDefMap.TryGetValue((MethodDefinitionHandle)handle, out var methodDef)) return methodDef;
                break;

            case HandleKind.FieldDefinition:
                if (_fieldDefMap.TryGetValue((FieldDefinitionHandle)handle, out var fieldDef)) return fieldDef;
                break;

            case HandleKind.MethodSpecification:
                if (_methodSpecMap.TryGetValue((MethodSpecificationHandle)handle, out var methodSpec)) return methodSpec;
                break;

            case HandleKind.AssemblyReference:
                if (_assemblyRefMap.TryGetValue((AssemblyReferenceHandle)handle, out var assemblyRef)) return assemblyRef;
                break;

            case HandleKind.PropertyDefinition:
                if (_propertyDefMap.TryGetValue((PropertyDefinitionHandle)handle, out var property)) return property;
                break;

            case HandleKind.EventDefinition:
                if (_eventDefMap.TryGetValue((EventDefinitionHandle)handle, out var eventDef)) return eventDef;
                break;

            case HandleKind.ModuleReference:
                if (_moduleRefMap.TryGetValue((ModuleReferenceHandle)handle, out var moduleRef)) return moduleRef;
                break;

            case HandleKind.GenericParameter:
                if (_genericParamMap.TryGetValue((GenericParameterHandle)handle, out var genericParam)) return genericParam;
                break;

            case HandleKind.GenericParameterConstraint:
                if (_genericParamConstraintMap.TryGetValue((GenericParameterConstraintHandle)handle, out var genericParamConstraint)) return genericParamConstraint;
                break;

            case HandleKind.StandaloneSignature:
                if (_standAloneSigMap.TryGetValue((StandaloneSignatureHandle)handle, out var standaloneSig)) return standaloneSig;
                break;

            // Single-row tables, and tables emitted strictly in source order, so their
            // row numbers are unchanged by construction.
            case HandleKind.AssemblyDefinition:
            case HandleKind.ModuleDefinition:
            case HandleKind.Parameter:
            case HandleKind.InterfaceImplementation:
                return handle;

            default:
                throw new PEPackerException(
                    $"Handle kind '{handle.Kind}' (token 0x{MetadataTokens.GetToken(handle):X8}) " +
                    "has no mapping; it cannot be carried into the rewritten assembly.");
        }

        throw new PEPackerException(
            $"{handle.Kind} 0x{MetadataTokens.GetToken(handle):X8} was never copied, " +
            "so it has no row in the rewritten assembly.");
    }

    private StringHandle GetOrAddString(string value)
    {
        return _metadata.GetOrAddString(value);
    }

    private BlobHandle GetOrAddBlob(byte[] value)
    {
        return _metadata.GetOrAddBlob(value);
    }

    private GuidHandle GetOrAddGuid(Guid value)
    {
        return _metadata.GetOrAddGuid(value);
    }

    #endregion

    /// <summary>
    /// The source image's pointer width: 4 for PE32, 8 for PE32+ (ECMA-335 II.25.2.3.1).
    /// </summary>
    private int SourcePointerSize =>
        _peReader.PEHeaders.PEHeader is { Magic: System.Reflection.PortableExecutable.PEMagic.PE32 } ? 4 : 8;

    /// <summary>
    /// Helper to get field data size from signature.
    /// </summary>
    /// <remarks>
    /// Pointer-sized types take the width from the source PE header rather than a
    /// hardcoded 8: on a 32-bit image an assumed 8-byte pointer over-read adjacent
    /// section data into the copied FieldRVA blob.
    /// </remarks>
    private class FieldDataSizeProvider : ISignatureTypeProvider<int, object?>
    {
        private readonly int _pointerSize;

        public FieldDataSizeProvider(int pointerSize) => _pointerSize = pointerSize;

        public int GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => 1,
            PrimitiveTypeCode.Byte => 1,
            PrimitiveTypeCode.SByte => 1,
            PrimitiveTypeCode.Char => 2,
            PrimitiveTypeCode.Int16 => 2,
            PrimitiveTypeCode.UInt16 => 2,
            PrimitiveTypeCode.Int32 => 4,
            PrimitiveTypeCode.UInt32 => 4,
            PrimitiveTypeCode.Int64 => 8,
            PrimitiveTypeCode.UInt64 => 8,
            PrimitiveTypeCode.Single => 4,
            PrimitiveTypeCode.Double => 8,
            PrimitiveTypeCode.IntPtr => _pointerSize,
            PrimitiveTypeCode.UIntPtr => _pointerSize,
            _ => 0
        };

        // Static initialized data is typed by a compiler-generated value type whose only
        // size information is its ClassLayout. Returning 0 here made the field's RVA data
        // look zero-length, and it was dropped without a word.
        public int GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var layout = reader.GetTypeDefinition(handle).GetLayout();
            return layout.IsDefault ? 0 : layout.Size;
        }

        public int GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => 0;
        public int GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => 0;
        public int GetSZArrayType(int elementType) => 0;
        public int GetPointerType(int elementType) => _pointerSize;
        public int GetByReferenceType(int elementType) => _pointerSize;
        public int GetGenericInstantiation(int genericType, ImmutableArray<int> typeArguments) => 0;
        public int GetArrayType(int elementType, ArrayShape shape) => 0;
        public int GetFunctionPointerType(MethodSignature<int> signature) => _pointerSize;
        public int GetGenericMethodParameter(object? genericContext, int index) => 0;
        public int GetGenericTypeParameter(object? genericContext, int index) => 0;
        public int GetModifiedType(int modifier, int unmodifiedType, bool isRequired) => unmodifiedType;
        public int GetPinnedType(int elementType) => elementType;
        public int GetTypeFromSerializedName(string name) => 0;
    }
}
