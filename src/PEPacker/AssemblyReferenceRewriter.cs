using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace PEPacker;

/// <summary>
/// Post-processes a compiled assembly to rewrite System.Private.CoreLib references
/// to SDK reference assembly references (System.Runtime, System.Collections, etc.).
/// This enables --ref-asm output for assemblies containing async/generator code.
/// </summary>
/// <remarks>
/// The problem this solves: MetadataLoadContext types are inspection-only and cannot
/// be passed to TypeBuilder.DefineType() for interface implementation. So we compile
/// with runtime types (which works), then post-process to rewrite the references.
/// </remarks>
public partial class AssemblyReferenceRewriter : IDisposable
{
    // Source assembly reading
    private readonly PEReader _peReader;
    private readonly MetadataReader _reader;
    private readonly Stream _sourceStream;

    // Reference assembly resolution
    private readonly string _refAssemblyPath;
    private readonly Dictionary<string, string> _typeToAssembly = new(); // FullTypeName -> AssemblyName
    private readonly Dictionary<string, AssemblyName> _assemblyInfoCache = new(); // AssemblyName -> AssemblyName object

    // Target assembly building
    private readonly MetadataBuilder _metadata = new();
    private readonly BlobBuilder _ilStream = new();
    private readonly BlobBuilder _mappedFieldData = new();

    // Handle mappings (source -> target)
    private readonly Dictionary<AssemblyReferenceHandle, AssemblyReferenceHandle> _assemblyRefMap = new();
    private readonly Dictionary<TypeReferenceHandle, TypeReferenceHandle> _typeRefMap = new();
    private readonly Dictionary<TypeSpecificationHandle, TypeSpecificationHandle> _typeSpecMap = new();
    private readonly Dictionary<MemberReferenceHandle, MemberReferenceHandle> _memberRefMap = new();
    private readonly Dictionary<MethodSpecificationHandle, MethodSpecificationHandle> _methodSpecMap = new();
    private readonly Dictionary<TypeDefinitionHandle, TypeDefinitionHandle> _typeDefMap = new();
    private readonly Dictionary<MethodDefinitionHandle, MethodDefinitionHandle> _methodDefMap = new();
    private readonly Dictionary<FieldDefinitionHandle, FieldDefinitionHandle> _fieldDefMap = new();
    private readonly Dictionary<StandaloneSignatureHandle, StandaloneSignatureHandle> _standAloneSigMap = new();
    private readonly Dictionary<PropertyDefinitionHandle, PropertyDefinitionHandle> _propertyDefMap = new();
    private readonly Dictionary<EventDefinitionHandle, EventDefinitionHandle> _eventDefMap = new();
    private readonly Dictionary<ModuleReferenceHandle, ModuleReferenceHandle> _moduleRefMap = new();
    private readonly Dictionary<GenericParameterHandle, GenericParameterHandle> _genericParamMap = new();

    // Constant and FieldMarshal are sorted by a parent coded index that spans several
    // tables (HasConstant: Field, Param, Property; HasFieldMarshal: Field, Param), so a
    // field row can sort after a property row. Rows are gathered while their owners are
    // copied and emitted in coded-index order by EmitSortedConstantsAndMarshalDescriptors.
    private readonly List<(int SortKey, EntityHandle Parent, object? Value)> _constants = [];
    private readonly List<(int SortKey, EntityHandle Parent, BlobHandle Descriptor)> _marshalDescriptors = [];

    // GenericParam is sorted by (Owner, Number), where Owner is a TypeOrMethodDef coded
    // index. Type-owned and method-owned rows therefore interleave by row number, and
    // methods are copied before type generic parameters, so emission order is not sort
    // order. Gathered here and emitted by EmitSortedGenericParameters.
    private readonly List<(int SortKey, EntityHandle Parent, GenericParameterHandle Source)> _genericParameters = [];
    private readonly Dictionary<UserStringHandle, UserStringHandle> _userStringMap = new();
    private readonly Dictionary<StringHandle, StringHandle> _stringHandleMap = new();
    private readonly Dictionary<GuidHandle, GuidHandle> _guidHandleMap = new();
    private readonly Dictionary<BlobHandle, BlobHandle> _blobHandleMap = new();

    // New assembly references we create
    private readonly Dictionary<string, AssemblyReferenceHandle> _newAssemblyRefs = new();

    // Method body offset tracking
    private readonly Dictionary<MethodDefinitionHandle, int> _methodBodyOffsets = new();

    // Running 1-based Param table row counter. Each method's ParamList must point
    // at its first Param row (run-indexed), so we hand the current value to
    // AddMethodDefinition before appending that method's parameter rows. Methods are
    // copied in MethodDef order with their Param rows emitted contiguously, so this
    // yields correct, contiguous run-pointers (see CopyMethodDefinition).
    private int _nextParamRow = 1;

    // Entry point from source
    private MethodDefinitionHandle _sourceEntryPoint;
    private MethodDefinitionHandle _targetEntryPoint;

    private bool _disposed;

    /// <summary>
    /// Creates a new assembly reference rewriter.
    /// </summary>
    /// <param name="sourceAssembly">Stream containing the compiled assembly to rewrite.</param>
    /// <param name="refAssemblyPath">Path to SDK reference assemblies directory.</param>
    public AssemblyReferenceRewriter(Stream sourceAssembly, string refAssemblyPath)
    {
        _sourceStream = sourceAssembly;
        _refAssemblyPath = refAssemblyPath;

        _peReader = new PEReader(sourceAssembly);
        _reader = _peReader.GetMetadataReader();

        // Get entry point from PE header
        var corHeader = _peReader.PEHeaders.CorHeader;
        if (corHeader != null && corHeader.EntryPointTokenOrRelativeVirtualAddress != 0)
        {
            _sourceEntryPoint = MetadataTokens.MethodDefinitionHandle(
                corHeader.EntryPointTokenOrRelativeVirtualAddress);
        }

        BuildTypeToAssemblyMapping();
    }

    /// <summary>
    /// Builds a mapping from type full names to their SDK reference assembly.
    /// </summary>
    private void BuildTypeToAssemblyMapping()
    {
        // Scan all reference assemblies to find where types are defined
        if (!Directory.Exists(_refAssemblyPath))
        {
            throw new PEPackerException(
                $"Reference assembly directory '{_refAssemblyPath}' does not exist. " +
                RequiredReferenceDirectoryHint);
        }

        var assemblies = Directory.GetFiles(_refAssemblyPath, "*.dll");
        if (assemblies.Length == 0)
        {
            throw new PEPackerException(
                $"Reference assembly directory '{_refAssemblyPath}' contains no .dll files. " +
                RequiredReferenceDirectoryHint);
        }

        var resolver = new PathAssemblyResolver(assemblies);

        // Constructing the load context resolves the core assembly immediately, so a
        // directory of unrelated DLLs fails here with a bare FileNotFoundException naming
        // 'System.Runtime' and nothing about what was actually wanted.
        MetadataLoadContext mlc;
        try
        {
            mlc = new MetadataLoadContext(resolver, "System.Runtime");
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException
                                      or BadImageFormatException)
        {
            throw new PEPackerException(
                $"Reference assembly directory '{_refAssemblyPath}' holds {assemblies.Length} " +
                ".dll file(s) but no usable 'System.Runtime', so the framework type map cannot be " +
                $"built. {RequiredReferenceDirectoryHint}", ex);
        }

        using (mlc)
        {
            BuildTypeToAssemblyMapping(mlc, assemblies);
        }

        // An empty map is not a usable one: every CoreLib-scoped type reference would fall
        // back to System.Runtime and no AssemblyRef row would carry a real identity, so the
        // output would be quietly wrong rather than absent.
        if (_typeToAssembly.Count == 0 || _assemblyInfoCache.Count == 0)
        {
            throw new PEPackerException(
                $"Reference assembly directory '{_refAssemblyPath}' yielded no framework types " +
                $"({assemblies.Length} .dll file(s) scanned). Rewriting would produce an assembly " +
                $"with unresolved references. {RequiredReferenceDirectoryHint}");
        }
    }

    /// <summary>
    /// What a caller has to pass, and the one plausible value that silently is not it.
    /// </summary>
    /// <remarks>
    /// Under Native AOT <c>RuntimeEnvironment.GetRuntimeDirectory()</c> returns the
    /// application's own directory rather than the empty string, so the obvious way to
    /// obtain this path degrades into "scan a folder with no framework assemblies in it".
    /// </remarks>
    private const string RequiredReferenceDirectoryHint =
        "Expected a directory containing the framework assemblies — either a shared framework " +
        "directory (dotnet/shared/Microsoft.NETCore.App/<version>) or a reference pack " +
        "(dotnet/packs/Microsoft.NETCore.App.Ref/<version>/ref/<tfm>). Note that under Native AOT, " +
        "RuntimeEnvironment.GetRuntimeDirectory() returns the running application's own directory, " +
        "which holds no framework assemblies and is not a valid value here.";

    /// <summary>
    /// Populates the type and assembly maps from an already-resolved load context.
    /// </summary>
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode",
        Justification =
            "MetadataLoadContext is inspection-only: it reads types out of assembly files supplied " +
            "at run time using its own type system, and never asks the runtime loader for anything. " +
            "Trimming this application therefore cannot remove the types being enumerated here, so " +
            "the warning does not apply. Verified working in a published Native AOT binary, which " +
            "round-tripped an assembly through the full rewrite.")]
    private void BuildTypeToAssemblyMapping(MetadataLoadContext mlc, string[] assemblies)
    {
        foreach (var asmPath in assemblies)
        {
            try
            {
                var asm = mlc.LoadFromAssemblyPath(asmPath);
                var asmName = asm.GetName();
                var name = asmName.Name!;

                // Skip implementation assemblies
                if (name == "System.Private.CoreLib")
                    continue;

                // Cache assembly info for later
                _assemblyInfoCache[name] = asmName;

                // Handle forwarded types. A facade routinely forwards to assemblies
                // outside the probe directory; the exception still carries the entries
                // that did resolve, so keep those instead of losing the whole facade.
                Type?[] forwardedTypes;
                try
                {
                    forwardedTypes = asm.GetForwardedTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    forwardedTypes = ex.Types;
                }

                foreach (var forwardedType in forwardedTypes)
                {
                    if (forwardedType is { FullName: not null })
                    {
                        _typeToAssembly[forwardedType.FullName] = name;
                    }
                }

                // Map all public types. A reference assembly may name types from
                // assemblies outside the probe directory; ReflectionTypeLoadException
                // still carries everything that did resolve, so take those rather than
                // discarding the assembly's entire contribution.
                Type?[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }

                foreach (var type in types)
                {
                    if (type is { FullName: not null } && (type.IsPublic || type.IsNestedPublic))
                    {
                        _typeToAssembly[type.FullName] = name;
                    }
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException
                                          or FileNotFoundException)
            {
                // The probe directory holds native libraries alongside managed ones
                // (clrjit, hostpolicy, ...), so these are expected and skipped. Anything
                // else is a real fault and is left to propagate rather than degrading the
                // type map without a word.
            }
        }
    }

    /// <summary>
    /// Rewrites the assembly references and saves to the output stream.
    /// </summary>
    /// <exception cref="PEPackerException">
    /// The source assembly uses metadata or PE features the rewriter does not reproduce.
    /// </exception>
    public void Rewrite()
    {
        // Phase 0: Refuse input we would only partially copy.
        ValidateSupportedMetadata();

        // Phase 1: Copy assembly definition
        CopyAssemblyDefinition();

        // Phase 2: Copy module definition
        CopyModuleDefinition();

        // Phase 3: Create needed assembly references
        CreateAssemblyReferences();

        // Phase 3b: Copy module references
        // Must precede type references, whose resolution scope may name one, and method
        // definitions, whose P/Invoke imports resolve through one.
        CopyModuleReferences();

        // Phase 4: Copy type references with rewritten scopes
        CopyTypeReferences();

        // Phase 5: Reserve the target rows for every TypeDef, Field and MethodDef.
        // Nothing is emitted yet; this only fixes the row numbers so the mutually
        // dependent tables below can refer to each other.
        PredictDefinitionHandles();

        // Phase 6: Copy type specifications (generic instantiations)
        // Their signatures may name TypeDefs, whose rows are now known.
        CopyTypeSpecifications();

        // Phase 7: Emit the reserved TypeDef rows.
        // A base type may be a TypeSpec, so this must follow phase 6.
        AddTypeDefinitions();

        // Phase 8: Copy member references
        CopyMemberReferences();

        // Phase 9: Copy method specifications
        // Now has valid _typeDefMap and _methodDefMap entries
        CopyMethodSpecifications();

        // Phase 10: Copy standalone signatures
        // Must precede method bodies: `calli` operands are StandAloneSig tokens, so
        // every row needs its final mapping before any IL is patched. Copying them
        // on demand from CopyMethodBody instead renumbered the table (local-variable
        // signatures landed first) and left calli pointing at a LocalVarSig row.
        CopyStandaloneSignatures();

        // Phase 11: Copy method bodies and finish type definition members
        // Now has valid _methodSpecMap and _standAloneSigMap for IL token patching
        CopyMethodBodiesAndFinishTypes();

        // Phase 12: Copy properties, events and their method semantics
        // Must precede custom attributes so attributes parented to a property or
        // event can be remapped rather than silently keeping a stale row number.
        CopyPropertiesAndEvents();

        // Phase 13: Emit the rows gathered above whose tables are sorted by a parent
        // coded index, so copy order and emission order can differ.
        EmitSortedGenericParameters();
        EmitSortedConstantsAndMarshalDescriptors();

        // Phase 14: Copy custom attributes
        CopyCustomAttributes();
    }

    /// <summary>
    /// Saves the rewritten assembly to the output stream.
    /// </summary>
    public void Save(Stream output)
    {
        var metadataRootBuilder = new MetadataRootBuilder(_metadata);

        // Determine the entry point for the new assembly
        var entryPoint = _sourceEntryPoint.IsNil
            ? default
            : _methodDefMap.GetValueOrDefault(_sourceEntryPoint, default);

        var peBuilder = new ManagedPEBuilder(
            CreateHeaderFromSource(),
            metadataRootBuilder,
            _ilStream,
            mappedFieldData: _mappedFieldData.Count > 0 ? _mappedFieldData : null,
            strongNameSignatureSize: _peReader.PEHeaders.CorHeader?.StrongNameSignatureDirectory.Size ?? 128,
            entryPoint: entryPoint,
            flags: GetSourceCorFlags());

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);
        peBlob.WriteContentTo(output);
    }

    /// <summary>
    /// Reproduces the source image's PE and COFF header fields.
    /// </summary>
    /// <remarks>
    /// The rewriter previously used <see cref="PEHeaderBuilder.CreateExecutableHeader"/>
    /// unconditionally, which stamps every output as a bare executable image — a library
    /// came back without <see cref="Characteristics.Dll"/> — and discarded the source's
    /// machine, subsystem, alignments and stack/heap reservations.
    /// </remarks>
    private PEHeaderBuilder CreateHeaderFromSource()
    {
        var coffHeader = _peReader.PEHeaders.CoffHeader;
        var peHeader = _peReader.PEHeaders.PEHeader;

        if (peHeader is null)
        {
            return PEHeaderBuilder.CreateExecutableHeader();
        }

        return new PEHeaderBuilder(
            machine: coffHeader.Machine,
            sectionAlignment: peHeader.SectionAlignment,
            fileAlignment: peHeader.FileAlignment,
            imageBase: peHeader.ImageBase,
            majorLinkerVersion: peHeader.MajorLinkerVersion,
            minorLinkerVersion: peHeader.MinorLinkerVersion,
            majorOperatingSystemVersion: peHeader.MajorOperatingSystemVersion,
            minorOperatingSystemVersion: peHeader.MinorOperatingSystemVersion,
            majorImageVersion: peHeader.MajorImageVersion,
            minorImageVersion: peHeader.MinorImageVersion,
            majorSubsystemVersion: peHeader.MajorSubsystemVersion,
            minorSubsystemVersion: peHeader.MinorSubsystemVersion,
            subsystem: peHeader.Subsystem,
            dllCharacteristics: peHeader.DllCharacteristics,
            imageCharacteristics: coffHeader.Characteristics,
            sizeOfStackReserve: peHeader.SizeOfStackReserve,
            sizeOfStackCommit: peHeader.SizeOfStackCommit,
            sizeOfHeapReserve: peHeader.SizeOfHeapReserve,
            sizeOfHeapCommit: peHeader.SizeOfHeapCommit);
    }

    /// <summary>
    /// Carries the source CLI header flags across, minus the strong-name bit: the
    /// rewritten image is never re-signed, so claiming a signature would be a lie.
    /// </summary>
    private CorFlags GetSourceCorFlags()
    {
        var corHeader = _peReader.PEHeaders.CorHeader;
        return corHeader is null
            ? CorFlags.ILOnly
            : corHeader.Flags & ~CorFlags.StrongNameSigned;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _peReader.Dispose();
            _sourceStream.Dispose();
            _disposed = true;
        }
    }
}
