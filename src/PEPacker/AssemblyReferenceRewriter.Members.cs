using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace PEPacker;

public partial class AssemblyReferenceRewriter
{
    private void CopyMemberReferences()
    {
        foreach (var memberRefHandle in _reader.MemberReferences)
        {
            var memberRef = _reader.GetMemberReference(memberRefHandle);
            var name = _reader.GetString(memberRef.Name);
            var reader = _reader.GetBlobReader(memberRef.Signature);

            // Map the parent
            var newParent = MapEntityHandle(memberRef.Parent);

            // Rewrite the signature
            var newSignature = RewriteMethodOrFieldSignature(reader);

            var newHandle = _metadata.AddMemberReference(
                newParent,
                GetOrAddString(name),
                _metadata.GetOrAddBlob(newSignature));

            _memberRefMap[memberRefHandle] = newHandle;
        }
    }

    private void CopyMethodSpecifications()
    {
        // Iterate through MethodSpec table
        int methodSpecCount = _reader.GetTableRowCount(TableIndex.MethodSpec);
        for (int row = 1; row <= methodSpecCount; row++)
        {
            var methodSpecHandle = MetadataTokens.MethodSpecificationHandle(row);
            var methodSpec = _reader.GetMethodSpecification(methodSpecHandle);
            var reader = _reader.GetBlobReader(methodSpec.Signature);

            // Map the method
            var newMethod = MapEntityHandle(methodSpec.Method);

            // Rewrite the instantiation signature
            var newSignature = RewriteMethodSpecSignature(reader);

            var newHandle = _metadata.AddMethodSpecification(
                newMethod,
                _metadata.GetOrAddBlob(newSignature));

            _methodSpecMap[methodSpecHandle] = newHandle;
        }
    }

    /// <summary>
    /// Third phase of type definition copying: copy all members (fields, methods with bodies, etc.).
    /// This must run after CopyMethodSpecifications so that IL tokens can be mapped.
    /// </summary>
    private void CopyMethodBodiesAndFinishTypes()
    {
        var typeDefHandles = _reader.TypeDefinitions.ToList();

        // Copy fields
        foreach (var typeDefHandle in typeDefHandles)
        {
            var typeDef = _reader.GetTypeDefinition(typeDefHandle);
            foreach (var fieldHandle in typeDef.GetFields())
            {
                CopyFieldDefinition(fieldHandle);
            }
        }

        // Copy methods with bodies
        foreach (var typeDefHandle in typeDefHandles)
        {
            var typeDef = _reader.GetTypeDefinition(typeDefHandle);
            foreach (var methodHandle in typeDef.GetMethods())
            {
                CopyMethodDefinition(methodHandle);
            }
        }

        // Copy interface implementations
        foreach (var typeDefHandle in typeDefHandles)
        {
            var typeDef = _reader.GetTypeDefinition(typeDefHandle);
            var newTypeDefHandle = _typeDefMap[typeDefHandle];

            foreach (var ifaceImplHandle in typeDef.GetInterfaceImplementations())
            {
                var ifaceImpl = _reader.GetInterfaceImplementation(ifaceImplHandle);
                var newInterface = MapEntityHandle(ifaceImpl.Interface);

                _metadata.AddInterfaceImplementation(newTypeDefHandle, newInterface);
            }
        }

        // Copy nested type relationships
        foreach (var typeDefHandle in typeDefHandles)
        {
            var typeDef = _reader.GetTypeDefinition(typeDefHandle);
            foreach (var nestedHandle in typeDef.GetNestedTypes())
            {
                var newEnclosing = _typeDefMap[typeDefHandle];
                var newNested = _typeDefMap[nestedHandle];
                _metadata.AddNestedType(newNested, newEnclosing);
            }
        }

        // Copy method implementations (overrides)
        foreach (var typeDefHandle in typeDefHandles)
        {
            var typeDef = _reader.GetTypeDefinition(typeDefHandle);
            var newTypeDefHandle = _typeDefMap[typeDefHandle];

            foreach (var methodImplHandle in typeDef.GetMethodImplementations())
            {
                var methodImpl = _reader.GetMethodImplementation(methodImplHandle);
                var newMethodBody = MapEntityHandle(methodImpl.MethodBody);
                var newMethodDecl = MapEntityHandle(methodImpl.MethodDeclaration);

                _metadata.AddMethodImplementation(newTypeDefHandle, newMethodBody, newMethodDecl);
            }
        }

        // Copy generic parameters for types
        foreach (var typeDefHandle in typeDefHandles)
        {
            var typeDef = _reader.GetTypeDefinition(typeDefHandle);
            var newTypeDefHandle = _typeDefMap[typeDefHandle];

            foreach (var genParamHandle in typeDef.GetGenericParameters())
            {
                CollectGenericParameter(genParamHandle, newTypeDefHandle);
            }
        }
    }

    private void CopyFieldDefinition(FieldDefinitionHandle fieldHandle)
    {
        var field = _reader.GetFieldDefinition(fieldHandle);
        var reader = _reader.GetBlobReader(field.Signature);
        var newSignature = RewriteFieldSignature(reader);

        var newHandle = _metadata.AddFieldDefinition(
            field.Attributes,
            GetOrAddString(_reader.GetString(field.Name)),
            _metadata.GetOrAddBlob(newSignature));

        _fieldDefMap[fieldHandle] = newHandle;

        // Copy field RVA data (static initialized data).
        var rva = field.GetRelativeVirtualAddress();
        if (rva != 0)
        {
            _metadata.AddFieldRelativeVirtualAddress(newHandle, _mappedFieldData.Count);
            _mappedFieldData.WriteBytes(GetFieldData(rva, field));
        }

        CollectConstant(field.GetDefaultValue(), newHandle);
        CollectMarshallingDescriptor(field.GetMarshallingDescriptor(), newHandle);

        // Copy field layout if present
        var offset = field.GetOffset();
        if (offset >= 0)
        {
            _metadata.AddFieldLayout(newHandle, offset);
        }
    }

    /// <summary>
    /// Reads a field's RVA data from the source image.
    /// </summary>
    /// <remarks>
    /// This used to swallow every failure and return an empty array, and the caller then
    /// skipped the FieldRVA row entirely — so an unrecognised field type silently cost the
    /// data. Failing here is the honest outcome: the alternative is an assembly whose
    /// static initializers quietly became empty.
    /// </remarks>
    private byte[] GetFieldData(int rva, FieldDefinition field)
    {
        int size;
        try
        {
            size = field.DecodeSignature(new FieldDataSizeProvider(SourcePointerSize), null);
        }
        catch (Exception ex)
        {
            throw new PEPackerException(
                $"Could not decode the signature of field '{_reader.GetString(field.Name)}' " +
                "to size its RVA data.", ex);
        }

        if (size <= 0)
        {
            throw new PEPackerException(
                $"Field '{_reader.GetString(field.Name)}' carries RVA data whose size could " +
                "not be determined from its signature, so the data cannot be copied.");
        }

        return _peReader.GetSectionData(rva).GetContent(0, size).ToArray();
    }

    private void CopyMethodDefinition(MethodDefinitionHandle methodHandle)
    {
        var method = _reader.GetMethodDefinition(methodHandle);
        var reader = _reader.GetBlobReader(method.Signature);
        var newSignature = RewriteMethodSignature(reader);

        // Get IL body offset if present
        int bodyOffset = -1;
        if (method.RelativeVirtualAddress != 0)
        {
            bodyOffset = CopyMethodBody(method);
        }

        // ParamList must point at this method's first Param row. MetadataBuilder does
        // not derive run-pointer columns (FieldList/MethodList are likewise assigned
        // explicitly), so a nil here mis-links every method to the wrong Param rows —
        // causing GetParameters() to throw BadImageFormatException at runtime. Hand it
        // the current counter; param rows for this method are appended immediately below.
        var firstParam = MetadataTokens.ParameterHandle(_nextParamRow);

        var newHandle = _metadata.AddMethodDefinition(
            method.Attributes,
            method.ImplAttributes,
            GetOrAddString(_reader.GetString(method.Name)),
            _metadata.GetOrAddBlob(newSignature),
            bodyOffset,
            firstParam);

        _methodDefMap[methodHandle] = newHandle;

        // P/Invoke target. ImplMap is sorted by MemberForwarded, and methods are copied
        // in MethodDef order, so appending here keeps it sorted. Without this row the
        // method keeps its PinvokeImpl flag but names no native entry point.
        var import = method.GetImport();
        if (!import.Module.IsNil)
        {
            if (!_moduleRefMap.TryGetValue(import.Module, out var newModule))
            {
                throw new PEPackerException(
                    $"P/Invoke on '{_reader.GetString(method.Name)}' names module reference " +
                    $"0x{MetadataTokens.GetToken(import.Module):X8}, which was not copied.");
            }

            _metadata.AddMethodImport(
                newHandle,
                import.Attributes,
                GetOrAddString(_reader.GetString(import.Name)),
                newModule);
        }

        // Copy parameters (including any return-value Param row at sequence 0),
        // advancing the run-pointer counter for each row emitted.
        foreach (var paramHandle in method.GetParameters())
        {
            var param = _reader.GetParameter(paramHandle);
            var newParamHandle = _metadata.AddParameter(
                param.Attributes,
                GetOrAddString(_reader.GetString(param.Name)),
                param.SequenceNumber);
            _nextParamRow++;

            // Optional-parameter defaults live in the Constant table. Dropping them
            // while keeping ParameterAttributes.HasDefault leaves the parameter
            // claiming a default the metadata no longer carries.
            CollectConstant(param.GetDefaultValue(), newParamHandle);
            CollectMarshallingDescriptor(param.GetMarshallingDescriptor(), newParamHandle);
        }

        // Copy generic parameters
        foreach (var genParamHandle in method.GetGenericParameters())
        {
            CollectGenericParameter(genParamHandle, newHandle);
        }
    }

    /// <summary>
    /// Records a generic parameter for later emission in sorted order.
    /// </summary>
    /// <remarks>
    /// Emitting these as they are encountered produced a GenericParam table sorted by
    /// nothing in particular — method-owned rows were appended while copying methods and
    /// type-owned rows afterwards, whereas the table must be ordered by the Owner coded
    /// index, which interleaves the two kinds. MetadataBuilder rejects the result with
    /// "Metadata table GenericParam not sorted" for some shapes and silently produces an
    /// unsearchable table for others.
    /// </remarks>
    private void CollectGenericParameter(GenericParameterHandle genParamHandle, EntityHandle parent)
    {
        _genericParameters.Add((TypeOrMethodDefCodedIndex(parent), parent, genParamHandle));
    }

    private void EmitSortedGenericParameters()
    {
        // Ordered by Owner, then by the parameter's own position within that owner.
        var ordered = _genericParameters
            .OrderBy(g => g.SortKey)
            .ThenBy(g => _reader.GetGenericParameter(g.Source).Index);

        foreach (var entry in ordered)
        {
            var genParam = _reader.GetGenericParameter(entry.Source);

            var newHandle = _metadata.AddGenericParameter(
                entry.Parent,
                genParam.Attributes,
                GetOrAddString(_reader.GetString(genParam.Name)),
                genParam.Index);

            _genericParamMap[entry.Source] = newHandle;

            // GenericParamConstraint is sorted by Owner too. Adding each parameter's
            // constraints immediately keeps those owners ascending. The mapping is
            // recorded because a constraint can parent a custom attribute (Roslyn emits
            // NullableAttribute there), which CopyCustomAttributes must remap.
            foreach (var constraintHandle in genParam.GetConstraints())
            {
                var constraint = _reader.GetGenericParameterConstraint(constraintHandle);
                _genericParamConstraintMap[constraintHandle] =
                    _metadata.AddGenericParameterConstraint(newHandle, MapEntityHandle(constraint.Type));
            }
        }
    }

    /// <summary>ECMA-335 II.24.2.6 TypeOrMethodDef: TypeDef = 0, MethodDef = 1.</summary>
    private static int TypeOrMethodDefCodedIndex(EntityHandle owner) =>
        (MetadataTokens.GetRowNumber(owner) << 1) | owner.Kind switch
        {
            HandleKind.TypeDefinition => 0,
            HandleKind.MethodDefinition => 1,
            _ => throw new PEPackerException($"'{owner.Kind}' cannot own a GenericParam row.")
        };

    /// <summary>
    /// Copies every StandAloneSig row in source order, so the table keeps its row
    /// numbering and both local-variable signatures and <c>calli</c> operands resolve.
    /// </summary>
    private void CopyStandaloneSignatures()
    {
        int sigCount = _reader.GetTableRowCount(TableIndex.StandAloneSig);
        for (int row = 1; row <= sigCount; row++)
        {
            var sigHandle = MetadataTokens.StandaloneSignatureHandle(row);
            var sig = _reader.GetStandaloneSignature(sigHandle);
            var reader = _reader.GetBlobReader(sig.Signature);
            var newSigBytes = RewriteStandaloneSignature(reader);

            var newHandle = _metadata.AddStandaloneSignature(_metadata.GetOrAddBlob(newSigBytes));
            _standAloneSigMap[sigHandle] = newHandle;
        }
    }

    /// <summary>
    /// Copies the Property, PropertyMap, Event, EventMap and MethodSemantics tables.
    /// </summary>
    /// <remarks>
    /// These were previously omitted entirely, so every property and event vanished from
    /// the rewritten image: the <c>get_</c>/<c>set_</c> methods survived as ordinary
    /// MethodDefs but nothing tied them together, and a referencing compiler saw no
    /// properties at all.
    /// </remarks>
    private void CopyPropertiesAndEvents()
    {
        // MethodSemantics is sorted by its Association, a HasSemantics coded index
        // (ECMA-335 II.24.2.6: Event = tag 0, Property = tag 1). That interleaves the two
        // kinds by row number rather than grouping them, so rows are gathered here and
        // emitted in coded-index order once every property and event has its handle.
        var semantics = new List<(int Association, MethodSemanticsAttributes Attributes, MethodDefinitionHandle Method, EntityHandle Parent)>();

        foreach (var typeDefHandle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(typeDefHandle);
            var newTypeDefHandle = _typeDefMap[typeDefHandle];

            CopyProperties(typeDef, newTypeDefHandle, semantics);
            CopyEvents(typeDef, newTypeDefHandle, semantics);
        }

        foreach (var entry in semantics.OrderBy(s => s.Association))
        {
            _metadata.AddMethodSemantics(entry.Parent, entry.Attributes, entry.Method);
        }
    }

    private void CopyProperties(
        TypeDefinition typeDef,
        TypeDefinitionHandle newTypeDefHandle,
        List<(int, MethodSemanticsAttributes, MethodDefinitionHandle, EntityHandle)> semantics)
    {
        var properties = typeDef.GetProperties();
        if (properties.Count == 0)
        {
            return;
        }

        // PropertyMap.PropertyList is a run-pointer: it must name this type's first
        // Property row, with the type's rows contiguous after it. MetadataBuilder does
        // not derive it, exactly as with MethodDef.ParamList.
        PropertyDefinitionHandle firstProperty = default;

        foreach (var propertyHandle in properties)
        {
            var property = _reader.GetPropertyDefinition(propertyHandle);

            // A PropertySig (II.23.2.5) is shaped like a method signature after its
            // header: ParamCount, the property type, then any indexer parameters.
            var newHandle = _metadata.AddProperty(
                property.Attributes,
                GetOrAddString(_reader.GetString(property.Name)),
                _metadata.GetOrAddBlob(RewriteMethodOrFieldSignature(_reader.GetBlobReader(property.Signature))));

            if (firstProperty.IsNil)
            {
                firstProperty = newHandle;
            }

            _propertyDefMap[propertyHandle] = newHandle;

            CollectConstant(property.GetDefaultValue(), newHandle);

            var accessors = property.GetAccessors();
            AddSemantics(semantics, newHandle, MethodSemanticsAttributes.Getter, accessors.Getter);
            AddSemantics(semantics, newHandle, MethodSemanticsAttributes.Setter, accessors.Setter);
            foreach (var other in accessors.Others)
            {
                AddSemantics(semantics, newHandle, MethodSemanticsAttributes.Other, other);
            }
        }

        _metadata.AddPropertyMap(newTypeDefHandle, firstProperty);
    }

    private void CopyEvents(
        TypeDefinition typeDef,
        TypeDefinitionHandle newTypeDefHandle,
        List<(int, MethodSemanticsAttributes, MethodDefinitionHandle, EntityHandle)> semantics)
    {
        var events = typeDef.GetEvents();
        if (events.Count == 0)
        {
            return;
        }

        EventDefinitionHandle firstEvent = default;

        foreach (var eventHandle in events)
        {
            var eventDef = _reader.GetEventDefinition(eventHandle);

            var newHandle = _metadata.AddEvent(
                eventDef.Attributes,
                GetOrAddString(_reader.GetString(eventDef.Name)),
                MapEntityHandle(eventDef.Type));

            if (firstEvent.IsNil)
            {
                firstEvent = newHandle;
            }

            _eventDefMap[eventHandle] = newHandle;

            var accessors = eventDef.GetAccessors();
            AddSemantics(semantics, newHandle, MethodSemanticsAttributes.Adder, accessors.Adder);
            AddSemantics(semantics, newHandle, MethodSemanticsAttributes.Remover, accessors.Remover);
            AddSemantics(semantics, newHandle, MethodSemanticsAttributes.Raiser, accessors.Raiser);
            foreach (var other in accessors.Others)
            {
                AddSemantics(semantics, newHandle, MethodSemanticsAttributes.Other, other);
            }
        }

        _metadata.AddEventMap(newTypeDefHandle, firstEvent);
    }

    private void AddSemantics(
        List<(int, MethodSemanticsAttributes, MethodDefinitionHandle, EntityHandle)> semantics,
        EntityHandle parent,
        MethodSemanticsAttributes attributes,
        MethodDefinitionHandle accessor)
    {
        if (accessor.IsNil)
        {
            return;
        }

        if (!_methodDefMap.TryGetValue(accessor, out var newAccessor))
        {
            throw new PEPackerException(
                $"Accessor 0x{MetadataTokens.GetToken(accessor):X8} has no mapping; " +
                "method handles must be created before properties and events are copied.");
        }

        // HasSemantics coded index (ECMA-335 II.24.2.6): (row << 1) | tag,
        // where Event = 0 and Property = 1.
        int tag = parent.Kind == HandleKind.EventDefinition ? 0 : 1;
        int association = (MetadataTokens.GetRowNumber(parent) << 1) | tag;

        semantics.Add((association, attributes, newAccessor, parent));
    }

    /// <summary>
    /// Records a Constant row for later emission, decoding the blob into the CLR value
    /// <see cref="MetadataBuilder.AddConstant"/> expects.
    /// </summary>
    /// <remarks>
    /// Handing that method the source <c>BlobHandle</c> instead throws
    /// <c>ArgumentException: Value of type 'BlobHandle' is not a constant</c>, so any
    /// assembly carrying a literal field or an optional parameter failed to rewrite.
    /// </remarks>
    private void CollectConstant(ConstantHandle constantHandle, EntityHandle newParent)
    {
        if (constantHandle.IsNil)
        {
            return;
        }

        var constant = _reader.GetConstant(constantHandle);
        _constants.Add((HasConstantCodedIndex(newParent), newParent, DecodeConstantValue(constant)));
    }

    private void CollectMarshallingDescriptor(BlobHandle descriptor, EntityHandle newParent)
    {
        if (descriptor.IsNil)
        {
            return;
        }

        _marshalDescriptors.Add((
            HasFieldMarshalCodedIndex(newParent),
            newParent,
            GetOrAddBlob(_reader.GetBlobBytes(descriptor))));
    }

    private void EmitSortedConstantsAndMarshalDescriptors()
    {
        foreach (var entry in _constants.OrderBy(c => c.SortKey))
        {
            _metadata.AddConstant(entry.Parent, entry.Value);
        }

        foreach (var entry in _marshalDescriptors.OrderBy(m => m.SortKey))
        {
            _metadata.AddMarshallingDescriptor(entry.Parent, entry.Descriptor);
        }
    }

    private object? DecodeConstantValue(Constant constant)
    {
        var blob = _reader.GetBlobReader(constant.Value);

        return constant.TypeCode switch
        {
            ConstantTypeCode.Boolean => blob.ReadBoolean(),
            ConstantTypeCode.Char => blob.ReadChar(),
            ConstantTypeCode.SByte => blob.ReadSByte(),
            ConstantTypeCode.Byte => blob.ReadByte(),
            ConstantTypeCode.Int16 => blob.ReadInt16(),
            ConstantTypeCode.UInt16 => blob.ReadUInt16(),
            ConstantTypeCode.Int32 => blob.ReadInt32(),
            ConstantTypeCode.UInt32 => blob.ReadUInt32(),
            ConstantTypeCode.Int64 => blob.ReadInt64(),
            ConstantTypeCode.UInt64 => blob.ReadUInt64(),
            ConstantTypeCode.Single => blob.ReadSingle(),
            ConstantTypeCode.Double => blob.ReadDouble(),
            ConstantTypeCode.String => blob.ReadUTF16(blob.RemainingBytes),
            ConstantTypeCode.NullReference => null,
            _ => throw new PEPackerException(
                $"Unsupported constant type code '{constant.TypeCode}'.")
        };
    }

    /// <summary>ECMA-335 II.24.2.6 HasConstant: Field = 0, Param = 1, Property = 2.</summary>
    private static int HasConstantCodedIndex(EntityHandle parent) =>
        (MetadataTokens.GetRowNumber(parent) << 2) | parent.Kind switch
        {
            HandleKind.FieldDefinition => 0,
            HandleKind.Parameter => 1,
            HandleKind.PropertyDefinition => 2,
            _ => throw new PEPackerException($"'{parent.Kind}' cannot own a Constant row.")
        };

    /// <summary>ECMA-335 II.24.2.6 HasFieldMarshal: Field = 0, Param = 1.</summary>
    private static int HasFieldMarshalCodedIndex(EntityHandle parent) =>
        (MetadataTokens.GetRowNumber(parent) << 1) | parent.Kind switch
        {
            HandleKind.FieldDefinition => 0,
            HandleKind.Parameter => 1,
            _ => throw new PEPackerException($"'{parent.Kind}' cannot own a FieldMarshal row.")
        };

    private void CopyCustomAttributes()
    {
        foreach (var attrHandle in _reader.CustomAttributes)
        {
            var attr = _reader.GetCustomAttribute(attrHandle);
            var parent = MapEntityHandle(attr.Parent);
            var constructor = MapEntityHandle(attr.Constructor);
            var value = GetOrAddBlob(_reader.GetBlobBytes(attr.Value));

            _metadata.AddCustomAttribute(parent, constructor, value);
        }
    }
}
