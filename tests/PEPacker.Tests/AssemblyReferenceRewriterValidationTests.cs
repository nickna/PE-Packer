using System.Reflection;
using System.Reflection.Emit;
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
    public void Rewrite_Rejects_ExplicitTypeLayout()
    {
        var source = Build("LayoutFixture", module => DefineExplicitLayoutStruct(module, "Fixture.Overlapped"));

        var ex = Assert.Throws<PEPackerException>(() => Rewrite(source));
        Assert.Contains("ClassLayout", ex.Message);
        Assert.Contains("explicit or sequential type layout", ex.Message);
        Assert.Contains("not a general-purpose PE round-tripper", ex.Message);
    }

    [Fact]
    public void Rewrite_ReportsEveryUnsupportedConstruct_NotJustTheFirst()
    {
        var source = Build("MultiFixture", module =>
        {
            DefineExplicitLayoutStruct(module, "Fixture.Overlapped");
            DefineExplicitLayoutStruct(module, "Fixture.AlsoOverlapped");
        });

        var ex = Assert.Throws<PEPackerException>(() => Rewrite(source));

        // One pass over the source should surface the whole list, so a caller fixes
        // everything at once instead of rediscovering it a table at a time.
        Assert.Contains("ClassLayout (2 rows)", ex.Message);
        Assert.Contains("explicit or sequential type layout", ex.Message);
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
