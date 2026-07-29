using System.Reflection.Metadata.Ecma335;

namespace PEPacker;

public partial class AssemblyReferenceRewriter
{
    /// <summary>
    /// Every metadata table the rewriter reproduces. Anything outside this set is
    /// rejected rather than silently dropped.
    /// </summary>
    /// <remarks>
    /// This is an allow-list on purpose: it fails closed. A table added to ECMA-335, or
    /// one that simply never came up in testing, surfaces as a clear error instead of
    /// vanishing from the output.
    /// </remarks>
    private static readonly TableIndex[] SupportedTables =
    [
        TableIndex.Module,
        TableIndex.TypeRef,
        TableIndex.TypeDef,
        TableIndex.Field,
        TableIndex.MethodDef,
        TableIndex.Param,
        TableIndex.InterfaceImpl,
        TableIndex.MemberRef,
        TableIndex.Constant,
        TableIndex.CustomAttribute,
        TableIndex.FieldMarshal,
        TableIndex.FieldLayout,
        TableIndex.ClassLayout,
        TableIndex.StandAloneSig,
        TableIndex.EventMap,
        TableIndex.Event,
        TableIndex.PropertyMap,
        TableIndex.Property,
        TableIndex.MethodSemantics,
        TableIndex.MethodImpl,
        TableIndex.TypeSpec,
        TableIndex.ModuleRef,
        TableIndex.ImplMap,
        TableIndex.FieldRva,
        TableIndex.Assembly,
        TableIndex.AssemblyRef,
        TableIndex.NestedClass,
        TableIndex.GenericParam,
        TableIndex.MethodSpec,
        TableIndex.GenericParamConstraint,
    ];

    /// <summary>
    /// Plain-language descriptions for the constructs behind the tables most likely to
    /// show up, so the failure names the feature rather than a table number.
    /// </summary>
    private static string DescribeTable(TableIndex table) => table switch
    {
        TableIndex.DeclSecurity => "declarative security attributes",
        TableIndex.ModuleRef => "module references",
        TableIndex.ImplMap => "P/Invoke method definitions",
        TableIndex.File => "multi-file assembly manifests",
        TableIndex.ExportedType => "exported or forwarded types",
        TableIndex.ManifestResource => "embedded or linked managed resources",
        TableIndex.EncLog or TableIndex.EncMap => "edit-and-continue deltas",

        TableIndex.FieldPtr or TableIndex.MethodPtr or TableIndex.ParamPtr or
        TableIndex.EventPtr or TableIndex.PropertyPtr =>
            "uncompressed metadata, whose indirection tables break row-order assumptions",

        TableIndex.AssemblyProcessor or TableIndex.AssemblyOS or
        TableIndex.AssemblyRefProcessor or TableIndex.AssemblyRefOS =>
            "obsolete assembly platform metadata",

        _ => "metadata this rewriter does not reproduce",
    };

    /// <summary>
    /// Rejects source assemblies whose metadata the rewriter cannot faithfully reproduce.
    /// </summary>
    /// <remarks>
    /// The rewriter targets assemblies emitted by SharpTS via PersistedAssemblyBuilder.
    /// Outside that shape it previously produced output that loaded but had quietly lost
    /// whatever it did not know how to copy, which is far worse than refusing the input.
    /// </remarks>
    /// <exception cref="PEPackerException">
    /// The source uses metadata or PE features that would be dropped.
    /// </exception>
    private void ValidateSupportedMetadata()
    {
        var unsupported = new List<string>();

        foreach (TableIndex table in Enum.GetValues<TableIndex>())
        {
            if (Array.IndexOf(SupportedTables, table) >= 0)
            {
                continue;
            }

            int rows = _reader.GetTableRowCount(table);
            if (rows > 0)
            {
                unsupported.Add($"  {table} ({rows} row{(rows == 1 ? "" : "s")}) — {DescribeTable(table)}");
            }
        }

        // ManagedPEBuilder is given no native resource section, so a source carrying one
        // would come back without it.
        var resourceDirectory = _peReader.PEHeaders.PEHeader?.ResourceTableDirectory;
        if (resourceDirectory is { Size: > 0 })
        {
            unsupported.Add($"  native Win32 resources ({resourceDirectory.Value.Size} bytes)");
        }

        if (unsupported.Count == 0)
        {
            return;
        }

        throw new PEPackerException(
            "The source assembly uses metadata this rewriter does not reproduce, and " +
            "rewriting it would silently discard the following:" + Environment.NewLine +
            string.Join(Environment.NewLine, unsupported) + Environment.NewLine +
            "This rewriter supports assemblies emitted by SharpTS via " +
            "PersistedAssemblyBuilder; it is not a general-purpose PE round-tripper.");
    }
}
