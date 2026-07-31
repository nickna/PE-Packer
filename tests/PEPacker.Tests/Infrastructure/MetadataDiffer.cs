using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace PEPacker.Tests.Infrastructure;

/// <summary>
/// Compares an assembly before and after the reference rewrite and reports everything
/// that changed beyond the retargeting the rewriter exists to perform.
/// </summary>
/// <remarks>
/// <para>
/// Every defect found in this rewriter so far had the same shape: metadata was rebuilt
/// from scratch, something was omitted or mis-ordered, and the output still loaded. A
/// per-table, per-row comparison is the invariant that catches that class as a class,
/// rather than one table at a time once a symptom shows up downstream.
/// </para>
/// <para>
/// Entities are compared by resolved name, not by token, because handles legitimately
/// move. Assembly identity is stripped from type names for the same reason —
/// <c>System.Private.CoreLib</c> becoming <c>System.Runtime</c> is the point of the
/// exercise — and is checked separately by <see cref="CompareRetargeting"/>.
/// </para>
/// <para>
/// What is compared: the assembly's own definition row (which the rewrite must copy
/// verbatim); table row counts; every type with its fields (including marshalling, layout
/// and the actual mapped FieldRVA bytes), methods, properties and events; every custom
/// attribute's parent, constructor and verbatim value blob (the rewriter copies CA blobs
/// byte-for-byte); and every method body — header, exception regions, token operands
/// resolved to names, and all remaining IL bytes (opcodes, branch targets, inline
/// constants, switch tables) byte-for-byte with only the token operand sites masked.
/// </para>
/// </remarks>
internal static class MetadataDiffer
{
    public static IReadOnlyList<string> Compare(byte[] before, byte[] after)
    {
        var differences = new List<string>();

        using var beforePe = new PEReader(new MemoryStream(before));
        using var afterPe = new PEReader(new MemoryStream(after));
        var a = beforePe.GetMetadataReader();
        var b = afterPe.GetMetadataReader();

        CompareAssemblyDefinition(a, b, differences);
        CompareTableCounts(a, b, differences);
        CompareRetargeting(b, differences);
        CompareTypes(a, b, differences);
        CompareCustomAttributes(a, b, differences);
        CompareFieldRvaData(beforePe, a, afterPe, b, differences);
        CompareMethodBodies(beforePe, a, afterPe, b, differences);
        ComparePEHeaders(beforePe, afterPe, differences);

        return differences;
    }

    /// <summary>
    /// The output's own Assembly row must be a verbatim copy: the rewrite retargets
    /// references, never the assembly's own identity.
    /// </summary>
    private static void CompareAssemblyDefinition(MetadataReader a, MetadataReader b, List<string> differences)
    {
        if (!Check(differences, "Assembly", "row present", a.IsAssembly, b.IsAssembly)) return;
        if (!a.IsAssembly) return;

        var da = a.GetAssemblyDefinition();
        var db = b.GetAssemblyDefinition();

        Check(differences, "Assembly", "name", a.GetString(da.Name), b.GetString(db.Name));
        Check(differences, "Assembly", "version", da.Version, db.Version);
        Check(differences, "Assembly", "culture", a.GetString(da.Culture), b.GetString(db.Culture));
        Check(differences, "Assembly", "flags", da.Flags, db.Flags);
        Check(differences, "Assembly", "hash algorithm", da.HashAlgorithm, db.HashAlgorithm);
        Check(differences, "Assembly", "public key", DescribeBlob(a, da.PublicKey), DescribeBlob(b, db.PublicKey));
    }

    /// <summary>
    /// AssemblyRef is expected to grow: one CoreLib reference fans out into the SDK
    /// facades that actually define the types. Every other table must match exactly.
    /// </summary>
    private static void CompareTableCounts(MetadataReader a, MetadataReader b, List<string> differences)
    {
        foreach (TableIndex table in Enum.GetValues<TableIndex>())
        {
            if (table == TableIndex.AssemblyRef) continue;

            // A ClassLayout row of all zeros carries no information — ECMA-335 II.22.8
            // has size 0 mean "compute from the fields" — and TypeLayout.IsDefault cannot
            // tell such a row from an absent one, so the row count is not meaningful.
            // CompareTypes checks each type's effective layout instead, which is stricter
            // about the thing that matters.
            if (table == TableIndex.ClassLayout) continue;

            int countA = a.GetTableRowCount(table);
            int countB = b.GetTableRowCount(table);
            if (countA != countB)
            {
                differences.Add($"table {table}: {countA} row(s) before, {countB} after");
            }
        }
    }

    private static void CompareRetargeting(MetadataReader b, List<string> differences)
    {
        foreach (var handle in b.AssemblyReferences)
        {
            var name = b.GetString(b.GetAssemblyReference(handle).Name);
            if (name == "System.Private.CoreLib")
            {
                differences.Add("rewritten assembly still references System.Private.CoreLib");
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in b.AssemblyReferences)
        {
            var reference = b.GetAssemblyReference(handle);
            var key = $"{b.GetString(reference.Name)}, {reference.Version}";
            if (!seen.Add(key))
            {
                differences.Add($"duplicate AssemblyRef row: {key}");
            }
        }
    }

    private static void CompareTypes(MetadataReader a, MetadataReader b, List<string> differences)
    {
        var typesA = a.TypeDefinitions.ToList();
        var typesB = b.TypeDefinitions.ToList();
        if (typesA.Count != typesB.Count) return; // already reported by the count pass

        for (int i = 0; i < typesA.Count; i++)
        {
            var typeA = a.GetTypeDefinition(typesA[i]);
            var typeB = b.GetTypeDefinition(typesB[i]);
            var name = FullName(a, typeA);

            Check(differences, name, "name", FullName(a, typeA), FullName(b, typeB));
            Check(differences, name, "attributes", typeA.Attributes, typeB.Attributes);
            Check(differences, name, "base type", Describe(a, typeA.BaseType), Describe(b, typeB.BaseType));
            Check(differences, name, "layout", DescribeLayout(typeA), DescribeLayout(typeB));

            Check(differences, name, "interfaces",
                Join(typeA.GetInterfaceImplementations().Select(h => Describe(a, a.GetInterfaceImplementation(h).Interface))),
                Join(typeB.GetInterfaceImplementations().Select(h => Describe(b, b.GetInterfaceImplementation(h).Interface))));

            Check(differences, name, "nested types",
                Join(typeA.GetNestedTypes().Select(h => FullName(a, a.GetTypeDefinition(h)))),
                Join(typeB.GetNestedTypes().Select(h => FullName(b, b.GetTypeDefinition(h)))));

            Check(differences, name, "generic parameters",
                DescribeGenericParameters(a, typeA.GetGenericParameters()),
                DescribeGenericParameters(b, typeB.GetGenericParameters()));

            Check(differences, name, "method impls",
                Join(typeA.GetMethodImplementations().Select(h => DescribeMethodImpl(a, h))),
                Join(typeB.GetMethodImplementations().Select(h => DescribeMethodImpl(b, h))));

            CompareFields(a, typeA, b, typeB, name, differences);
            CompareMethods(a, typeA, b, typeB, name, differences);
            CompareProperties(a, typeA, b, typeB, name, differences);
            CompareEvents(a, typeA, b, typeB, name, differences);
        }
    }

    private static void CompareFields(MetadataReader a, TypeDefinition typeA,
        MetadataReader b, TypeDefinition typeB, string owner, List<string> differences)
    {
        var fieldsA = typeA.GetFields().ToList();
        var fieldsB = typeB.GetFields().ToList();
        if (!Check(differences, owner, "field count", fieldsA.Count, fieldsB.Count)) return;

        for (int i = 0; i < fieldsA.Count; i++)
        {
            var fa = a.GetFieldDefinition(fieldsA[i]);
            var fb = b.GetFieldDefinition(fieldsB[i]);
            var name = $"{owner}.{a.GetString(fa.Name)}";

            Check(differences, name, "field name", a.GetString(fa.Name), b.GetString(fb.Name));
            Check(differences, name, "field attributes", fa.Attributes, fb.Attributes);
            Check(differences, name, "field signature",
                fa.DecodeSignature(SignatureStringProvider.Instance, null),
                fb.DecodeSignature(SignatureStringProvider.Instance, null));
            Check(differences, name, "field constant", DescribeConstant(a, fa.GetDefaultValue()), DescribeConstant(b, fb.GetDefaultValue()));
            Check(differences, name, "field offset", fa.GetOffset(), fb.GetOffset());
            Check(differences, name, "field marshalling",
                DescribeBlob(a, fa.GetMarshallingDescriptor()), DescribeBlob(b, fb.GetMarshallingDescriptor()));
            // RVA presence and the mapped data bytes are compared by CompareFieldRvaData,
            // which has the PEReaders needed to actually read the section contents.
        }
    }

    /// <summary>
    /// Compares each RVA-carrying field's mapped data bytes, not just their presence. A
    /// FieldRVA row pointing at zeroed or truncated data used to pass the old
    /// "has RVA" check while every static initializer quietly changed value.
    /// </summary>
    private static void CompareFieldRvaData(PEReader peA, MetadataReader a,
        PEReader peB, MetadataReader b, List<string> differences)
    {
        var fieldsA = a.FieldDefinitions.ToList();
        var fieldsB = b.FieldDefinitions.ToList();
        if (fieldsA.Count != fieldsB.Count) return; // already reported by the count pass

        for (int i = 0; i < fieldsA.Count; i++)
        {
            var fa = a.GetFieldDefinition(fieldsA[i]);
            var fb = b.GetFieldDefinition(fieldsB[i]);
            var name = $"{FullName(a, a.GetTypeDefinition(fa.GetDeclaringType()))}.{a.GetString(fa.Name)}";

            int rvaA = fa.GetRelativeVirtualAddress();
            int rvaB = fb.GetRelativeVirtualAddress();
            if (!Check(differences, name, "field has RVA", rvaA != 0, rvaB != 0)) continue;
            if (rvaA == 0) continue;

            int sizeA = FieldDataSize(peA, a, fa);
            int sizeB = FieldDataSize(peB, b, fb);
            if (!Check(differences, name, "field RVA data size", sizeA, sizeB)) continue;
            if (sizeA <= 0) continue; // unsizable from the signature; presence was checked

            Check(differences, name, "field RVA data",
                Convert.ToHexString(peA.GetSectionData(rvaA).GetContent(0, sizeA).AsSpan()),
                Convert.ToHexString(peB.GetSectionData(rvaB).GetContent(0, sizeB).AsSpan()));
        }
    }

    /// <summary>
    /// Sizes a field's RVA data from its signature, or -1 when the signature cannot be
    /// sized (a shape none of the fixtures produce; presence is still compared).
    /// </summary>
    private static int FieldDataSize(PEReader pe, MetadataReader reader, FieldDefinition field)
    {
        try
        {
            int pointerSize = pe.PEHeaders.PEHeader is { Magic: PEMagic.PE32 } ? 4 : 8;
            int size = field.DecodeSignature(new FieldDataSizeProvider(pointerSize), null);
            return size > 0 ? size : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static void CompareMethods(MetadataReader a, TypeDefinition typeA,
        MetadataReader b, TypeDefinition typeB, string owner, List<string> differences)
    {
        var methodsA = typeA.GetMethods().ToList();
        var methodsB = typeB.GetMethods().ToList();
        if (!Check(differences, owner, "method count", methodsA.Count, methodsB.Count)) return;

        for (int i = 0; i < methodsA.Count; i++)
        {
            var ma = a.GetMethodDefinition(methodsA[i]);
            var mb = b.GetMethodDefinition(methodsB[i]);
            var name = $"{owner}.{a.GetString(ma.Name)}";

            Check(differences, name, "method name", a.GetString(ma.Name), b.GetString(mb.Name));
            Check(differences, name, "method attributes", ma.Attributes, mb.Attributes);
            Check(differences, name, "method impl attributes", ma.ImplAttributes, mb.ImplAttributes);
            Check(differences, name, "method signature",
                DescribeMethodSignature(a, ma), DescribeMethodSignature(b, mb));
            Check(differences, name, "generic parameters",
                DescribeGenericParameters(a, ma.GetGenericParameters()),
                DescribeGenericParameters(b, mb.GetGenericParameters()));
            Check(differences, name, "parameters",
                DescribeParameters(a, ma), DescribeParameters(b, mb));
            Check(differences, name, "P/Invoke import", DescribeImport(a, ma), DescribeImport(b, mb));
            Check(differences, name, "has body",
                ma.RelativeVirtualAddress != 0, mb.RelativeVirtualAddress != 0);
        }
    }

    private static void CompareProperties(MetadataReader a, TypeDefinition typeA,
        MetadataReader b, TypeDefinition typeB, string owner, List<string> differences)
    {
        var propsA = typeA.GetProperties().ToList();
        var propsB = typeB.GetProperties().ToList();
        if (!Check(differences, owner, "property count", propsA.Count, propsB.Count)) return;

        for (int i = 0; i < propsA.Count; i++)
        {
            var pa = a.GetPropertyDefinition(propsA[i]);
            var pb = b.GetPropertyDefinition(propsB[i]);
            var name = $"{owner}.{a.GetString(pa.Name)}";

            Check(differences, name, "property name", a.GetString(pa.Name), b.GetString(pb.Name));
            Check(differences, name, "property attributes", pa.Attributes, pb.Attributes);
            Check(differences, name, "property signature",
                DescribePropertySignature(a, pa), DescribePropertySignature(b, pb));
            Check(differences, name, "property constant",
                DescribeConstant(a, pa.GetDefaultValue()), DescribeConstant(b, pb.GetDefaultValue()));

            var accessorsA = pa.GetAccessors();
            var accessorsB = pb.GetAccessors();
            Check(differences, name, "getter", Describe(a, accessorsA.Getter), Describe(b, accessorsB.Getter));
            Check(differences, name, "setter", Describe(a, accessorsA.Setter), Describe(b, accessorsB.Setter));
        }
    }

    private static void CompareEvents(MetadataReader a, TypeDefinition typeA,
        MetadataReader b, TypeDefinition typeB, string owner, List<string> differences)
    {
        var eventsA = typeA.GetEvents().ToList();
        var eventsB = typeB.GetEvents().ToList();
        if (!Check(differences, owner, "event count", eventsA.Count, eventsB.Count)) return;

        for (int i = 0; i < eventsA.Count; i++)
        {
            var ea = a.GetEventDefinition(eventsA[i]);
            var eb = b.GetEventDefinition(eventsB[i]);
            var name = $"{owner}.{a.GetString(ea.Name)}";

            Check(differences, name, "event name", a.GetString(ea.Name), b.GetString(eb.Name));
            Check(differences, name, "event attributes", ea.Attributes, eb.Attributes);
            Check(differences, name, "event type", Describe(a, ea.Type), Describe(b, eb.Type));

            var accessorsA = ea.GetAccessors();
            var accessorsB = eb.GetAccessors();
            Check(differences, name, "adder", Describe(a, accessorsA.Adder), Describe(b, accessorsB.Adder));
            Check(differences, name, "remover", Describe(a, accessorsA.Remover), Describe(b, accessorsB.Remover));
        }
    }

    /// <summary>
    /// Row-by-row CustomAttribute comparison. The old count-only check let a swapped
    /// parent, a wrong constructor or a corrupted argument blob pass unremarked.
    /// </summary>
    /// <remarks>
    /// The rewriter copies CA value blobs verbatim (<c>CopyCustomAttributes</c> passes the
    /// source bytes straight to <c>GetOrAddBlob</c>), so the blob comparison is verbatim
    /// too. Parents and constructors are compared by resolved name, since their handles
    /// legitimately move.
    /// </remarks>
    private static void CompareCustomAttributes(MetadataReader a, MetadataReader b, List<string> differences)
    {
        var attrsA = a.CustomAttributes.ToList();
        var attrsB = b.CustomAttributes.ToList();
        if (attrsA.Count != attrsB.Count) return; // already reported by the count pass

        for (int i = 0; i < attrsA.Count; i++)
        {
            var ca = a.GetCustomAttribute(attrsA[i]);
            var cb = b.GetCustomAttribute(attrsB[i]);
            var name = $"CustomAttribute #{i + 1}";

            Check(differences, name, "parent", Describe(a, ca.Parent), Describe(b, cb.Parent));
            Check(differences, name, "constructor", Describe(a, ca.Constructor), Describe(b, cb.Constructor));
            Check(differences, name, "value blob", DescribeBlob(a, ca.Value), DescribeBlob(b, cb.Value));
        }
    }

    /// <summary>
    /// Compares each method body: header fields, exception regions, token operands
    /// resolved to names (so a shifted heap or renumbered table shows up as a changed
    /// operand), and every remaining IL byte verbatim with only the token operand sites
    /// masked — so a same-length corruption of a branch target, an inline constant or a
    /// switch table cannot pass.
    /// </summary>
    private static void CompareMethodBodies(PEReader peA, MetadataReader a,
        PEReader peB, MetadataReader b, List<string> differences)
    {
        var methodsA = a.MethodDefinitions.ToList();
        var methodsB = b.MethodDefinitions.ToList();
        if (methodsA.Count != methodsB.Count) return;

        for (int i = 0; i < methodsA.Count; i++)
        {
            var ma = a.GetMethodDefinition(methodsA[i]);
            var mb = b.GetMethodDefinition(methodsB[i]);
            if (ma.RelativeVirtualAddress == 0 || mb.RelativeVirtualAddress == 0) continue;

            var name = $"{FullName(a, a.GetTypeDefinition(ma.GetDeclaringType()))}.{a.GetString(ma.Name)}";
            var bodyA = peA.GetMethodBody(ma.RelativeVirtualAddress);
            var bodyB = peB.GetMethodBody(mb.RelativeVirtualAddress);

            Check(differences, name, "max stack", bodyA.MaxStack, bodyB.MaxStack);
            Check(differences, name, "init locals", bodyA.LocalVariablesInitialized, bodyB.LocalVariablesInitialized);
            Check(differences, name, "local signature",
                DescribeStandaloneSignature(a, bodyA.LocalSignature),
                DescribeStandaloneSignature(b, bodyB.LocalSignature));
            Check(differences, name, "exception regions",
                DescribeExceptionRegions(a, bodyA), DescribeExceptionRegions(b, bodyB));

            var ilA = bodyA.GetILBytes();
            var ilB = bodyB.GetILBytes();
            if (ilA is null || ilB is null) continue;

            Check(differences, name, "IL length", ilA.Length, ilB.Length);
            if (ilA.Length != ilB.Length) continue;

            // Token operands are compared semantically: their row numbers legitimately
            // change, so the four operand bytes are masked out of the verbatim pass below.
            var tokenOperandBytes = new bool[ilA.Length];
            foreach (var (offset, token) in ILTokenSites(ilA))
            {
                for (int j = 0; j < 4; j++) tokenOperandBytes[offset + j] = true;

                var describedA = DescribeToken(a, token);
                var describedB = DescribeToken(b, BitConverter.ToInt32(ilB, offset));
                if (describedA != describedB)
                {
                    differences.Add($"{name}: IL operand at 0x{offset - 1:X4} was {describedA}, now {describedB}");
                }
            }

            // Everything that is not a token operand — opcodes, branch targets, inline
            // constants, switch tables — must be byte-identical.
            for (int j = 0; j < ilA.Length; j++)
            {
                if (!tokenOperandBytes[j] && ilA[j] != ilB[j])
                {
                    differences.Add(
                        $"{name}: IL byte at 0x{j:X4} was 0x{ilA[j]:X2}, now 0x{ilB[j]:X2}");
                    break; // one finding per body is enough to fail the diff
                }
            }
        }
    }

    private static void ComparePEHeaders(PEReader a, PEReader b, List<string> differences)
    {
        Check(differences, "PE", "machine", a.PEHeaders.CoffHeader.Machine, b.PEHeaders.CoffHeader.Machine);
        Check(differences, "PE", "characteristics",
            a.PEHeaders.CoffHeader.Characteristics, b.PEHeaders.CoffHeader.Characteristics);

        if (a.PEHeaders.PEHeader is { } headerA && b.PEHeaders.PEHeader is { } headerB)
        {
            Check(differences, "PE", "subsystem", headerA.Subsystem, headerB.Subsystem);
            Check(differences, "PE", "DLL characteristics", headerA.DllCharacteristics, headerB.DllCharacteristics);
            Check(differences, "PE", "section alignment", headerA.SectionAlignment, headerB.SectionAlignment);
            Check(differences, "PE", "file alignment", headerA.FileAlignment, headerB.FileAlignment);
        }

        if (a.PEHeaders.CorHeader is { } corA && b.PEHeaders.CorHeader is { } corB)
        {
            // The strong-name bit is intentionally cleared: the rewrite invalidates any
            // signature, so claiming one would be false.
            Check(differences, "PE", "CorFlags",
                corA.Flags & ~CorFlags.StrongNameSigned, corB.Flags & ~CorFlags.StrongNameSigned);
            Check(differences, "PE", "has entry point",
                corA.EntryPointTokenOrRelativeVirtualAddress != 0,
                corB.EntryPointTokenOrRelativeVirtualAddress != 0);
        }
    }

    // ---- describers -----------------------------------------------------------

    private static string DescribeLayout(TypeDefinition type)
    {
        var layout = type.GetLayout();
        return $"size={layout.Size} packing={layout.PackingSize}";
    }

    private static string FullName(MetadataReader reader, TypeDefinition type)
    {
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return ns.Length > 0 ? $"{ns}.{name}" : name;
    }

    private static string Describe(MetadataReader reader, EntityHandle handle)
    {
        if (handle.IsNil) return "<nil>";

        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                return FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle));

            case HandleKind.TypeReference:
            {
                var type = reader.GetTypeReference((TypeReferenceHandle)handle);
                var ns = reader.GetString(type.Namespace);
                var name = reader.GetString(type.Name);
                return ns.Length > 0 ? $"{ns}.{name}" : name;
            }

            case HandleKind.TypeSpecification:
                return reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                    .DecodeSignature(SignatureStringProvider.Instance, null);

            case HandleKind.MethodDefinition:
            {
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                var declaring = FullName(reader, reader.GetTypeDefinition(method.GetDeclaringType()));
                return $"{declaring}::{reader.GetString(method.Name)}";
            }

            case HandleKind.FieldDefinition:
            {
                var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                return $"{FullName(reader, reader.GetTypeDefinition(field.GetDeclaringType()))}::{reader.GetString(field.Name)}";
            }

            case HandleKind.MemberReference:
            {
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                return $"{Describe(reader, member.Parent)}::{reader.GetString(member.Name)}";
            }

            case HandleKind.MethodSpecification:
            {
                var spec = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                var args = spec.DecodeSignature(SignatureStringProvider.Instance, null);
                return $"{Describe(reader, spec.Method)}<{string.Join(",", args)}>";
            }

            case HandleKind.ModuleReference:
                return reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)handle).Name);

            case HandleKind.StandaloneSignature:
                return DescribeStandaloneSignature(reader, (StandaloneSignatureHandle)handle);

            // The kinds below matter as custom-attribute parents: without them two
            // attributes on different rows of the same kind compared as equal.
            case HandleKind.Parameter:
            {
                var parameter = reader.GetParameter((ParameterHandle)handle);
                return $"param #{parameter.SequenceNumber} {reader.GetString(parameter.Name)}";
            }

            case HandleKind.GenericParameter:
            {
                var parameter = reader.GetGenericParameter((GenericParameterHandle)handle);
                return $"genericparam {parameter.Index}:{reader.GetString(parameter.Name)}";
            }

            case HandleKind.GenericParameterConstraint:
            {
                var constraint = reader.GetGenericParameterConstraint((GenericParameterConstraintHandle)handle);
                return $"constraint {Describe(reader, constraint.Type)}";
            }

            case HandleKind.AssemblyDefinition:
                return $"assembly {reader.GetString(reader.GetAssemblyDefinition().Name)}";

            case HandleKind.ModuleDefinition:
                return $"module {reader.GetString(reader.GetModuleDefinition().Name)}";

            case HandleKind.PropertyDefinition:
                return $"property {reader.GetString(reader.GetPropertyDefinition((PropertyDefinitionHandle)handle).Name)}";

            case HandleKind.EventDefinition:
                return $"event {reader.GetString(reader.GetEventDefinition((EventDefinitionHandle)handle).Name)}";

            default:
                return handle.Kind.ToString();
        }
    }

    private static string DescribeToken(MetadataReader reader, int token)
    {
        int table = (token >> 24) & 0xFF;
        int row = token & 0x00FFFFFF;
        if (row == 0) return "<nil>";

        // #US tokens are heap offsets, not rows: the literal itself is what must match.
        if (table == 0x70)
        {
            try { return "\"" + reader.GetUserString(MetadataTokens.UserStringHandle(row)) + "\""; }
            catch (Exception ex) { return $"<unresolvable user string: {ex.GetType().Name}>"; }
        }

        try { return Describe(reader, MetadataTokens.EntityHandle(token)); }
        catch (Exception ex) { return $"<unresolvable token 0x{token:X8}: {ex.GetType().Name}>"; }
    }

    private static string DescribeStandaloneSignature(MetadataReader reader, StandaloneSignatureHandle handle)
    {
        if (handle.IsNil) return "<nil>";

        var signature = reader.GetStandaloneSignature(handle);
        var blob = reader.GetBlobReader(signature.Signature);
        if (blob.Length == 0) return "<empty>";

        var provider = SignatureStringProvider.Instance;
        try
        {
            return blob.ReadByte() == 0x07
                ? "locals(" + string.Join(",", signature.DecodeLocalSignature(provider, null)) + ")"
                : "methodsig " + Render(signature.DecodeMethodSignature(provider, null));
        }
        catch (Exception ex)
        {
            return $"<undecodable signature: {ex.GetType().Name}>";
        }
    }

    private static string DescribeMethodSignature(MetadataReader reader, MethodDefinition method) =>
        Render(method.DecodeSignature(SignatureStringProvider.Instance, null));

    private static string DescribePropertySignature(MetadataReader reader, PropertyDefinition property) =>
        Render(property.DecodeSignature(SignatureStringProvider.Instance, null));

    private static string Render(MethodSignature<string> signature) =>
        $"{signature.ReturnType}({string.Join(",", signature.ParameterTypes)}) " +
        $"conv={signature.Header.CallingConvention} generic={signature.GenericParameterCount}";

    private static string DescribeParameters(MetadataReader reader, MethodDefinition method) =>
        Join(method.GetParameters().Select(h =>
        {
            var parameter = reader.GetParameter(h);
            return $"#{parameter.SequenceNumber} {reader.GetString(parameter.Name)} " +
                   $"[{parameter.Attributes}] const={DescribeConstant(reader, parameter.GetDefaultValue())} " +
                   $"marshal={DescribeBlob(reader, parameter.GetMarshallingDescriptor())}";
        }));

    private static string DescribeImport(MetadataReader reader, MethodDefinition method)
    {
        var import = method.GetImport();
        if (import.Module.IsNil) return "<none>";

        return $"{reader.GetString(reader.GetModuleReference(import.Module).Name)}!" +
               $"{reader.GetString(import.Name)} [{import.Attributes}]";
    }

    private static string DescribeConstant(MetadataReader reader, ConstantHandle handle)
    {
        if (handle.IsNil) return "<none>";

        var constant = reader.GetConstant(handle);
        var blob = reader.GetBlobReader(constant.Value);
        return $"{constant.TypeCode}:{Convert.ToHexString(blob.ReadBytes(blob.RemainingBytes))}";
    }

    private static string DescribeBlob(MetadataReader reader, BlobHandle handle) =>
        handle.IsNil ? "<none>" : Convert.ToHexString(reader.GetBlobBytes(handle));

    private static string DescribeGenericParameters(MetadataReader reader, GenericParameterHandleCollection handles) =>
        Join(handles.Select(h =>
        {
            var parameter = reader.GetGenericParameter(h);
            var constraints = parameter.GetConstraints()
                .Select(c => Describe(reader, reader.GetGenericParameterConstraint(c).Type));
            return $"{parameter.Index}:{reader.GetString(parameter.Name)} " +
                   $"[{parameter.Attributes}] : {Join(constraints)}";
        }));

    private static string DescribeMethodImpl(MetadataReader reader, MethodImplementationHandle handle)
    {
        var impl = reader.GetMethodImplementation(handle);
        return $"{Describe(reader, impl.MethodBody)} implements {Describe(reader, impl.MethodDeclaration)}";
    }

    private static string DescribeExceptionRegions(MetadataReader reader, MethodBodyBlock body) =>
        Join(body.ExceptionRegions.Select(r =>
            $"{r.Kind} try=[{r.TryOffset},{r.TryOffset + r.TryLength}) " +
            $"handler=[{r.HandlerOffset},{r.HandlerOffset + r.HandlerLength}) " +
            $"catch={Describe(reader, r.CatchType)} filter={r.FilterOffset}"));

    private static string Join(IEnumerable<string> values) => string.Join(" | ", values);

    // ---- IL walking -----------------------------------------------------------

    private static readonly (int Size, bool IsToken, bool IsSwitch)[] SingleByte = BuildSingleByte();
    private static readonly (int Size, bool IsToken)[] Extended = BuildExtended();

    /// <summary>
    /// Yields (operandOffset, token) for every token-bearing instruction, decoding with a
    /// table derived from <see cref="OpCodes"/> so the comparison does not depend on the
    /// rewriter's own opcode table being right.
    /// </summary>
    private static IEnumerable<(int Offset, int Token)> ILTokenSites(byte[] il)
    {
        int offset = 0;
        while (offset < il.Length)
        {
            byte op = il[offset++];
            int size;
            bool isToken, isSwitch = false;

            if (op == 0xFE)
            {
                if (offset >= il.Length) yield break;
                (size, isToken) = Extended[il[offset++]];
            }
            else
            {
                (size, isToken, isSwitch) = SingleByte[op];
            }

            if (isSwitch)
            {
                if (offset + 4 > il.Length) yield break;
                uint cases = BitConverter.ToUInt32(il, offset);
                offset += 4 + (int)(4 * cases);
                continue;
            }

            if (isToken)
            {
                if (offset + 4 > il.Length) yield break;
                yield return (offset, BitConverter.ToInt32(il, offset));
                offset += 4;
                continue;
            }

            offset += size;
        }
    }

    private static (int, bool, bool)[] BuildSingleByte()
    {
        var table = new (int, bool, bool)[256];
        foreach (var (raw, size, isToken, isSwitch) in EnumerateOpCodes())
        {
            if (raw <= 0xFF) table[raw] = (size, isToken, isSwitch);
        }
        return table;
    }

    private static (int, bool)[] BuildExtended()
    {
        var table = new (int, bool)[256];
        foreach (var (raw, size, isToken, _) in EnumerateOpCodes())
        {
            if (raw > 0xFF) table[raw & 0xFF] = (size, isToken);
        }
        return table;
    }

    private static IEnumerable<(ushort Raw, int Size, bool IsToken, bool IsSwitch)> EnumerateOpCodes()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.Name.StartsWith("Prefix", StringComparison.Ordinal)) continue;
            if (field.GetValue(null) is not OpCode opCode) continue;

            var (size, isToken, isSwitch) = opCode.OperandType switch
            {
                OperandType.InlineNone => (0, false, false),
                OperandType.ShortInlineVar or OperandType.ShortInlineI or
                    OperandType.ShortInlineBrTarget => (1, false, false),
                OperandType.InlineVar => (2, false, false),
                OperandType.InlineI or OperandType.ShortInlineR or
                    OperandType.InlineBrTarget => (4, false, false),
                OperandType.InlineI8 or OperandType.InlineR => (8, false, false),
                OperandType.InlineSwitch => (0, false, true),
                _ => (4, true, false),
            };

            yield return ((ushort)opCode.Value, size, isToken, isSwitch);
        }
    }

    // ---- comparison -----------------------------------------------------------

    private static bool Check<T>(List<string> differences, string subject, string aspect, T before, T after)
    {
        if (EqualityComparer<T>.Default.Equals(before, after)) return true;

        differences.Add($"{subject}: {aspect} was '{before}', now '{after}'");
        return false;
    }

    /// <summary>
    /// Sizes a field type for FieldRVA data comparison: primitives by width, value types
    /// defined in the module by their ClassLayout size (the shape
    /// <c>DefineInitializedData</c> produces), pointers by the image's pointer width.
    /// Anything else returns 0, which the caller treats as "unsizable".
    /// </summary>
    /// <remarks>
    /// Deliberately independent of the rewriter's internal size provider, for the same
    /// reason <see cref="ILTokenSites"/> does not reuse the rewriter's opcode table: a
    /// checker sharing the code under test inherits its bugs.
    /// </remarks>
    private sealed class FieldDataSizeProvider : ISignatureTypeProvider<int, object?>
    {
        private readonly int _pointerSize;

        public FieldDataSizeProvider(int pointerSize) => _pointerSize = pointerSize;

        public int GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean or PrimitiveTypeCode.Byte or PrimitiveTypeCode.SByte => 1,
            PrimitiveTypeCode.Char or PrimitiveTypeCode.Int16 or PrimitiveTypeCode.UInt16 => 2,
            PrimitiveTypeCode.Int32 or PrimitiveTypeCode.UInt32 or PrimitiveTypeCode.Single => 4,
            PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 or PrimitiveTypeCode.Double => 8,
            PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => _pointerSize,
            _ => 0,
        };

        public int GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            reader.GetTypeDefinition(handle).GetLayout().Size;

        public int GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => 0;
        public int GetTypeFromSpecification(MetadataReader reader, object? genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind) => 0;
        public int GetSZArrayType(int elementType) => 0;
        public int GetArrayType(int elementType, ArrayShape shape) => 0;
        public int GetByReferenceType(int elementType) => 0;
        public int GetPointerType(int elementType) => _pointerSize;
        public int GetFunctionPointerType(MethodSignature<int> signature) => _pointerSize;
        public int GetGenericInstantiation(int genericType, System.Collections.Immutable.ImmutableArray<int> typeArguments) => 0;
        public int GetGenericMethodParameter(object? genericContext, int index) => 0;
        public int GetGenericTypeParameter(object? genericContext, int index) => 0;
        public int GetModifiedType(int modifier, int unmodifiedType, bool isRequired) => unmodifiedType;
        public int GetPinnedType(int elementType) => elementType;
        public int GetTypeFromSerializedName(string name) => 0;
    }
}
