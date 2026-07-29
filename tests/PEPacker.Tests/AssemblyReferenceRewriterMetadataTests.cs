using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using PEPacker;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Regression tests for metadata and PE-header preservation.
/// <para>
/// The rewriter used to omit the Property, PropertyMap, Event, EventMap and
/// MethodSemantics tables outright. Nothing crashed — the <c>get_</c>/<c>set_</c> methods
/// survived as ordinary MethodDefs — but every property and event disappeared, so a
/// referencing compiler saw only oddly named methods. That defeats the point of the
/// reference-assembly rewrite, whose whole job is compile-time referenceability.
/// </para>
/// <para>
/// It also serialized every output through
/// <see cref="PEHeaderBuilder.CreateExecutableHeader"/>, so a rewritten library came back
/// without <see cref="Characteristics.Dll"/> and lost the source's machine, subsystem,
/// alignments and stack/heap reservations.
/// </para>
/// </summary>
public class AssemblyReferenceRewriterMetadataTests
{
    [Fact]
    public void Rewrite_PreservesProperties_AndTheirAccessorSemantics()
    {
        var rewritten = Rewrite(Build("PropFixture", BuildPropertyType));

        Execute(rewritten, asm =>
        {
            var type = asm.GetType("Fixture.WithProperties")!;

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Assert.Equal(["Count", "Item", "Label", "ReadOnly"],
                properties.Select(p => p.Name).OrderBy(n => n).ToArray());

            // MethodSemantics has to survive too, or the accessors stay orphaned.
            var label = type.GetProperty("Label")!;
            Assert.Equal("get_Label", label.GetGetMethod()!.Name);
            Assert.Equal("set_Label", label.GetSetMethod()!.Name);
            Assert.Equal(typeof(string), label.PropertyType);

            var readOnly = type.GetProperty("ReadOnly")!;
            Assert.NotNull(readOnly.GetGetMethod());
            Assert.Null(readOnly.GetSetMethod());

            // An indexer exercises a PropertySig carrying parameters.
            var indexer = type.GetProperty("Item")!;
            Assert.Equal([typeof(int)], indexer.GetIndexParameters().Select(p => p.ParameterType).ToArray());
            Assert.Equal(typeof(string), indexer.PropertyType);

            Assert.True(type.GetProperty("Count")!.GetGetMethod()!.IsStatic);
        });
    }

    [Fact]
    public void Rewrite_PreservesEvents()
    {
        var rewritten = Rewrite(Build("EventFixture", BuildEventType));

        Execute(rewritten, asm =>
        {
            var type = asm.GetType("Fixture.WithEvents")!;

            var events = type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.Equal(["Changed", "Closed"], events.Select(e => e.Name).OrderBy(n => n).ToArray());

            var changed = type.GetEvent("Changed")!;
            Assert.Equal("add_Changed", changed.GetAddMethod()!.Name);
            Assert.Equal("remove_Changed", changed.GetRemoveMethod()!.Name);
            Assert.Equal(typeof(EventHandler), changed.EventHandlerType);
        });
    }

    /// <summary>
    /// MethodSemantics is sorted by its Association — a HasSemantics coded index that
    /// interleaves events and properties by row number rather than grouping them by kind.
    /// A type carrying both is the case that catches a naive append.
    /// </summary>
    [Fact]
    public void Rewrite_EmitsMethodSemantics_SortedByAssociation()
    {
        var rewritten = Rewrite(Build("MixedFixture", BuildMixedType));

        var pe = new PEReader(new MemoryStream(rewritten));
        var reader = pe.GetMetadataReader();

        // Both MetadataReader.GetAccessors and the runtime binary-search MethodSemantics
        // by Association, so an unsorted table silently resolves to nothing.
        // 3 events x (add + remove) = 6, plus PropA/PropB (get + set) and PropC (get) = 5.
        Assert.Equal(11, reader.GetTableRowCount(TableIndex.MethodSemantics));

        foreach (var handle in reader.PropertyDefinitions)
        {
            var accessors = reader.GetPropertyDefinition(handle).GetAccessors();
            Assert.False(accessors.Getter.IsNil,
                $"property '{reader.GetString(reader.GetPropertyDefinition(handle).Name)}' lost its getter");
        }

        foreach (var handle in reader.EventDefinitions)
        {
            var accessors = reader.GetEventDefinition(handle).GetAccessors();
            Assert.False(accessors.Adder.IsNil);
            Assert.False(accessors.Remover.IsNil);
        }

        // And the runtime must agree.
        Execute(rewritten, asm =>
        {
            var type = asm.GetType("Fixture.Mixed")!;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.NotNull(property.GetGetMethod());
            }
            foreach (var evt in type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.NotNull(evt.GetAddMethod());
                Assert.NotNull(evt.GetRemoveMethod());
            }
        });
    }

    [Fact]
    public void Rewrite_PreservesPEAndCorHeaderCharacteristics()
    {
        var source = Build("HeaderFixture", BuildPropertyType);
        var rewritten = Rewrite(source);

        var before = new PEReader(new MemoryStream(source)).PEHeaders;
        var after = new PEReader(new MemoryStream(rewritten)).PEHeaders;

        // PersistedAssemblyBuilder emits a library; the rewrite used to drop the Dll bit.
        Assert.True(before.CoffHeader.Characteristics.HasFlag(Characteristics.Dll));
        Assert.Equal(before.CoffHeader.Characteristics, after.CoffHeader.Characteristics);
        Assert.Equal(before.CoffHeader.Machine, after.CoffHeader.Machine);

        Assert.Equal(before.PEHeader!.Subsystem, after.PEHeader!.Subsystem);
        Assert.Equal(before.PEHeader.DllCharacteristics, after.PEHeader.DllCharacteristics);
        Assert.Equal(before.PEHeader.SectionAlignment, after.PEHeader.SectionAlignment);
        Assert.Equal(before.PEHeader.FileAlignment, after.PEHeader.FileAlignment);
        Assert.Equal(before.PEHeader.SizeOfStackReserve, after.PEHeader.SizeOfStackReserve);

        Assert.Equal(before.CorHeader!.Flags, after.CorHeader!.Flags);
    }

    // ---- fixtures -------------------------------------------------------------

    private static void BuildPropertyType(ModuleBuilder module)
    {
        var type = module.DefineType("Fixture.WithProperties", TypeAttributes.Public);
        var backing = type.DefineField("_label", typeof(string), FieldAttributes.Private);

        DefineProperty(type, "Label", typeof(string), backing, withSetter: true);
        DefineProperty(type, "ReadOnly", typeof(string), backing, withSetter: false);

        // Static property.
        var countGetter = type.DefineMethod("get_Count",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName,
            typeof(int), Type.EmptyTypes);
        var countIl = countGetter.GetILGenerator();
        countIl.Emit(OpCodes.Ldc_I4_7);
        countIl.Emit(OpCodes.Ret);
        var count = type.DefineProperty("Count", PropertyAttributes.None, typeof(int), Type.EmptyTypes);
        count.SetGetMethod(countGetter);

        // Indexer — a PropertySig with parameters.
        var itemGetter = type.DefineMethod("get_Item",
            MethodAttributes.Public | MethodAttributes.SpecialName,
            typeof(string), [typeof(int)]);
        var itemIl = itemGetter.GetILGenerator();
        itemIl.Emit(OpCodes.Ldarg_0);
        itemIl.Emit(OpCodes.Ldfld, backing);
        itemIl.Emit(OpCodes.Ret);
        var item = type.DefineProperty("Item", PropertyAttributes.None, typeof(string), [typeof(int)]);
        item.SetGetMethod(itemGetter);

        type.CreateType();
    }

    private static void BuildEventType(ModuleBuilder module)
    {
        var type = module.DefineType("Fixture.WithEvents", TypeAttributes.Public);
        DefineEvent(type, "Changed");
        DefineEvent(type, "Closed");
        type.CreateType();
    }

    private static void BuildMixedType(ModuleBuilder module)
    {
        var type = module.DefineType("Fixture.Mixed", TypeAttributes.Public);
        var backing = type.DefineField("_v", typeof(string), FieldAttributes.Private);

        // Interleaved so Event and Property row numbers overlap, which is what makes
        // the HasSemantics coded index interleave rather than group.
        DefineEvent(type, "EventA");
        DefineProperty(type, "PropA", typeof(string), backing, withSetter: true);
        DefineEvent(type, "EventB");
        DefineProperty(type, "PropB", typeof(string), backing, withSetter: true);
        DefineEvent(type, "EventC");
        DefineProperty(type, "PropC", typeof(string), backing, withSetter: false);

        type.CreateType();
    }

    private static void DefineProperty(TypeBuilder type, string name, Type propertyType,
        FieldBuilder backing, bool withSetter)
    {
        var getter = type.DefineMethod($"get_{name}",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            propertyType, Type.EmptyTypes);
        var getIl = getter.GetILGenerator();
        getIl.Emit(OpCodes.Ldarg_0);
        getIl.Emit(OpCodes.Ldfld, backing);
        getIl.Emit(OpCodes.Ret);

        var property = type.DefineProperty(name, PropertyAttributes.None, propertyType, Type.EmptyTypes);
        property.SetGetMethod(getter);

        if (!withSetter) return;

        var setter = type.DefineMethod($"set_{name}",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(void), [propertyType]);
        var setIl = setter.GetILGenerator();
        setIl.Emit(OpCodes.Ldarg_0);
        setIl.Emit(OpCodes.Ldarg_1);
        setIl.Emit(OpCodes.Stfld, backing);
        setIl.Emit(OpCodes.Ret);
        property.SetSetMethod(setter);
    }

    private static void DefineEvent(TypeBuilder type, string name)
    {
        var add = type.DefineMethod($"add_{name}",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(void), [typeof(EventHandler)]);
        add.GetILGenerator().Emit(OpCodes.Ret);

        var remove = type.DefineMethod($"remove_{name}",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(void), [typeof(EventHandler)]);
        remove.GetILGenerator().Emit(OpCodes.Ret);

        var evt = type.DefineEvent(name, EventAttributes.None, typeof(EventHandler));
        evt.SetAddOnMethod(add);
        evt.SetRemoveOnMethod(remove);
    }

    // ---- helpers --------------------------------------------------------------

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

    private static void Execute(byte[] image, Action<Assembly> assertions)
    {
        var alc = new AssemblyLoadContext("rewritten-metadata-fixture", isCollectible: true);
        try
        {
            using var ms = new MemoryStream(image);
            assertions(alc.LoadFromStream(ms));
        }
        finally
        {
            alc.Unload();
        }
    }
}
