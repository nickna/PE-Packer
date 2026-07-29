using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace PEPacker.Tests.Infrastructure;

/// <summary>
/// Renders a signature as a stable string so signatures from two different assemblies
/// can be compared by meaning rather than by token.
/// </summary>
/// <remarks>
/// Assembly identity is deliberately omitted from type names: retargeting
/// <c>System.Private.CoreLib</c> onto SDK facades is the rewriter's entire purpose, so
/// including the defining assembly would report every rewritten reference as a
/// difference. <see cref="MetadataDiffer"/> checks that retargeting separately.
/// </remarks>
internal sealed class SignatureStringProvider : ISignatureTypeProvider<string, object?>
{
    private readonly MetadataReader _reader;

    public SignatureStringProvider(MetadataReader reader) => _reader = reader;

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeDefinition(handle);
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return ns.Length > 0 ? $"{ns}.{name}" : name;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeReference(handle);
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return ns.Length > 0 ? $"{ns}.{name}" : name;
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext,
        TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPinnedType(string elementType) => "pinned " + elementType;

    public string GetArrayType(string elementType, ArrayShape shape) =>
        $"{elementType}[{new string(',', Math.Max(shape.Rank - 1, 0))}]";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        $"{genericType}<{string.Join(",", typeArguments)}>";

    public string GetFunctionPointerType(MethodSignature<string> signature) =>
        $"fnptr {signature.ReturnType}({string.Join(",", signature.ParameterTypes)})";

    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        $"{unmodifiedType} {(isRequired ? "modreq" : "modopt")}({modifier})";

    public string GetTypeFromSerializedName(string name) => name;
}
