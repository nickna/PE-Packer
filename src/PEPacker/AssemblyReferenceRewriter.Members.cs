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
                CopyGenericParameter(genParamHandle, newTypeDefHandle);
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

        // Copy field RVA if present (for field data)
        var rva = field.GetRelativeVirtualAddress();
        if (rva != 0)
        {
            // Get field data from the source PE
            var fieldData = GetFieldData(rva, field);
            if (fieldData.Length > 0)
            {
                _metadata.AddFieldRelativeVirtualAddress(newHandle, _mappedFieldData.Count);
                _mappedFieldData.WriteBytes(fieldData);
            }
        }

        // Copy default value if present
        var defaultValue = field.GetDefaultValue();
        if (!defaultValue.IsNil)
        {
            var constant = _reader.GetConstant(defaultValue);
            _metadata.AddConstant(newHandle, constant.Value);
        }

        // Copy marshal info if present
        var marshalInfo = field.GetMarshallingDescriptor();
        if (!marshalInfo.IsNil)
        {
            _metadata.AddMarshallingDescriptor(newHandle, GetOrAddBlob(_reader.GetBlobBytes(marshalInfo)));
        }

        // Copy field layout if present
        var offset = field.GetOffset();
        if (offset >= 0)
        {
            _metadata.AddFieldLayout(newHandle, offset);
        }
    }

    private byte[] GetFieldData(int rva, FieldDefinition field)
    {
        try
        {
            // Get the size from the signature
            var sig = field.DecodeSignature(new FieldDataSizeProvider(_reader), null);
            if (sig > 0)
            {
                var sectionData = _peReader.GetSectionData(rva);
                return sectionData.GetContent(0, sig).ToArray();
            }
        }
        catch
        {
            // Failed to get field data size
        }
        return [];
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

        // Track entry point
        if (methodHandle == _sourceEntryPoint)
        {
            _targetEntryPoint = newHandle;
        }

        // Copy parameters (including any return-value Param row at sequence 0),
        // advancing the run-pointer counter for each row emitted.
        foreach (var paramHandle in method.GetParameters())
        {
            var param = _reader.GetParameter(paramHandle);
            _metadata.AddParameter(
                param.Attributes,
                GetOrAddString(_reader.GetString(param.Name)),
                param.SequenceNumber);
            _nextParamRow++;
        }

        // Copy generic parameters
        foreach (var genParamHandle in method.GetGenericParameters())
        {
            CopyGenericParameter(genParamHandle, newHandle);
        }
    }

    private void CopyGenericParameter(GenericParameterHandle genParamHandle, EntityHandle parent)
    {
        var genParam = _reader.GetGenericParameter(genParamHandle);

        var newHandle = _metadata.AddGenericParameter(
            parent,
            genParam.Attributes,
            GetOrAddString(_reader.GetString(genParam.Name)),
            genParam.Index);

        // Copy constraints
        foreach (var constraintHandle in genParam.GetConstraints())
        {
            var constraint = _reader.GetGenericParameterConstraint(constraintHandle);
            var newConstraintType = MapEntityHandle(constraint.Type);
            _metadata.AddGenericParameterConstraint(newHandle, newConstraintType);
        }
    }

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
