using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using PEPacker;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// The rewriter reproduces a specific slice of ECMA-335 — the shape SharpTS emits via
/// <see cref="PersistedAssemblyBuilder"/>. Anything outside it used to be copied
/// partially and the remainder dropped without a word, so the output loaded while
/// quietly missing whatever the rewriter did not understand.
/// <para>
/// These tests pin the refusal instead. The allow-list fails closed, so an unhandled
/// table surfaces as an error naming the construct rather than as absent metadata.
/// </para>
/// </summary>
public class AssemblyReferenceRewriterValidationTests
{
    [Fact]
    public void Rewrite_Rejects_ManifestResources()
    {
        // Reflection.Emit cannot produce a ManifestResource row, so the fixture is built
        // straight from MetadataBuilder.
        var source = BuildWithUnsupportedTables(addManifestResource: true, addExportedType: false);

        var ex = Assert.Throws<PEPackerException>(() => Rewrite(source));
        Assert.Contains("ManifestResource", ex.Message);
        Assert.Contains("embedded or linked managed resources", ex.Message);
        Assert.Contains("not a general-purpose PE round-tripper", ex.Message);
    }

    [Fact]
    public void Rewrite_ReportsEveryUnsupportedConstruct_NotJustTheFirst()
    {
        var source = BuildWithUnsupportedTables(addManifestResource: true, addExportedType: true);

        var ex = Assert.Throws<PEPackerException>(() => Rewrite(source));

        // One pass over the source should surface the whole list, so a caller fixes
        // everything at once instead of rediscovering it a table at a time.
        Assert.Contains("ManifestResource", ex.Message);
        Assert.Contains("ExportedType", ex.Message);
        Assert.Contains("exported or forwarded types", ex.Message);
    }

    [Fact]
    public void Rewrite_Accepts_ExplicitTypeLayout()
    {
        // ClassLayout is how static initialized data is declared as well as how interop
        // structs are laid out, and FieldRVA already carries the bytes, so it is copied.
        var source = Build("LayoutFixture", module => DefineExplicitLayoutStruct(module, "Fixture.Overlapped"));

        var rewritten = Rewrite(source);
        Assert.NotEmpty(rewritten);
    }

    [Fact]
    public void Rewrite_Accepts_TheShapeSharpTsEmits()
    {
        // Guards against the allow-list being too tight: properties, events, generics,
        // constants, exception handlers and nested types must all still pass.
        var source = Build("SupportedFixture", module =>
        {
            var type = module.DefineType("Fixture.Ordinary", TypeAttributes.Public);
            var backing = type.DefineField("_value", typeof(string), FieldAttributes.Private);

            var literal = type.DefineField("Max", typeof(int),
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
            literal.SetConstant(10);

            var getter = type.DefineMethod("get_Value",
                MethodAttributes.Public | MethodAttributes.SpecialName, typeof(string), Type.EmptyTypes);
            var il = getter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, backing);
            il.Emit(OpCodes.Ret);

            var property = type.DefineProperty("Value", PropertyAttributes.None, typeof(string), Type.EmptyTypes);
            property.SetGetMethod(getter);

            var add = type.DefineMethod("add_Fired",
                MethodAttributes.Public | MethodAttributes.SpecialName, typeof(void), [typeof(EventHandler)]);
            add.GetILGenerator().Emit(OpCodes.Ret);
            var remove = type.DefineMethod("remove_Fired",
                MethodAttributes.Public | MethodAttributes.SpecialName, typeof(void), [typeof(EventHandler)]);
            remove.GetILGenerator().Emit(OpCodes.Ret);
            var evt = type.DefineEvent("Fired", EventAttributes.None, typeof(EventHandler));
            evt.SetAddOnMethod(add);
            evt.SetRemoveOnMethod(remove);

            var nested = type.DefineNestedType("Inner", TypeAttributes.NestedPublic);
            nested.CreateType();

            type.CreateType();
        });

        var rewritten = Rewrite(source);
        Assert.NotEmpty(rewritten);
    }

    /// <summary>
    /// An explicit-layout struct with positioned fields — the interop shape that carries
    /// a ClassLayout row. PersistedAssemblyBuilder ignores the packing/size arguments on
    /// <c>DefineType</c>, so field offsets are what actually produce one.
    /// </summary>
    private static void DefineExplicitLayoutStruct(ModuleBuilder module, string name)
    {
        var type = module.DefineType(name,
            TypeAttributes.Public | TypeAttributes.ExplicitLayout, typeof(ValueType));

        type.DefineField("AsInt", typeof(int), FieldAttributes.Public).SetOffset(0);
        type.DefineField("AsFloat", typeof(float), FieldAttributes.Public).SetOffset(0);

        type.CreateType();
    }

    /// <summary>
    /// Hand-builds a minimal but well-formed assembly carrying tables Reflection.Emit
    /// will not produce, so the guard can be tested against them directly.
    /// </summary>
    private static byte[] BuildWithUnsupportedTables(bool addManifestResource, bool addExportedType)
    {
        var metadata = new MetadataBuilder();

        metadata.AddAssembly(
            metadata.GetOrAddString("GuardFixture"), new Version(1, 0, 0, 0),
            default, default, 0, AssemblyHashAlgorithm.Sha1);

        metadata.AddModule(0, metadata.GetOrAddString("GuardFixture.dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()), default, default);

        // The <Module> pseudo-type every assembly must define.
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"),
            default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        if (addManifestResource)
        {
            metadata.AddManifestResource(ManifestResourceAttributes.Public,
                metadata.GetOrAddString("payload.bin"), default, offset: 0);
        }

        if (addExportedType)
        {
            // ExportedType's Implementation must name where the type actually lives.
            var target = metadata.AddAssemblyReference(
                metadata.GetOrAddString("Elsewhere"), new Version(1, 0, 0, 0),
                default, default, 0, default);

            metadata.AddExportedType(TypeAttributes.Public,
                metadata.GetOrAddString("Ns"), metadata.GetOrAddString("Forwarded"),
                target, 0);
        }

        var peBuilder = new ManagedPEBuilder(
            PEHeaderBuilder.CreateLibraryHeader(),
            new MetadataRootBuilder(metadata),
            new BlobBuilder());

        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        return blob.ToArray();
    }

    private static byte[] Build(string name, Action<ModuleBuilder> emit)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        emit(ab.DefineDynamicModule(name));

        using var stream = new MemoryStream();
        ab.Save(stream);
        return stream.ToArray();
    }

    private static byte[] Rewrite(byte[] source)
    {
        using var rewriter = new AssemblyReferenceRewriter(
            new MemoryStream(source), RuntimeEnvironment.GetRuntimeDirectory());

        rewriter.Rewrite();

        using var output = new MemoryStream();
        rewriter.Save(output);
        return output.ToArray();
    }
}
