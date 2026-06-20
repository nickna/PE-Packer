using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using PEPacker;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Regression tests for the MethodDef <c>ParamList</c> row-pointer reconstruction.
/// The rewriter used to pass <c>default</c> (nil) for every method's parameter list,
/// so the runtime mis-linked methods to the wrong <c>Param</c> rows and
/// <c>MethodInfo.GetParameters()</c> threw
/// <c>BadImageFormatException: The parameters and the signature of the method don't match</c>
/// — acute for zero-arg methods ordered before parameterized ones (e.g. compiler-generated
/// display-class <c>Invoke</c> methods). See SharpTS #343 / #738.
/// </summary>
public class AssemblyReferenceRewriterParamTests
{
    [Fact]
    public void Rewrite_PreservesParameterMetadata_ForAllMethods()
    {
        // Emit a fixture assembly whose methods deliberately interleave zero-arg and
        // parameterized methods, so a broken ParamList run-pointer corrupts at least one.
        using var sourceStream = new MemoryStream();
        BuildFixtureAssembly(sourceStream);
        sourceStream.Position = 0;

        // The rewriter only needs a directory that contains System.Runtime.dll (plus the
        // BCL facades) to build its type->assembly map. The shared-framework runtime
        // directory always qualifies and avoids depending on SDK ref-assembly packs.
        var refPath = RuntimeEnvironment.GetRuntimeDirectory();

        byte[] rewritten;
        using (var rewriter = new AssemblyReferenceRewriter(sourceStream, refPath))
        {
            rewriter.Rewrite();
            using var outStream = new MemoryStream();
            rewriter.Save(outStream);
            rewritten = outStream.ToArray();
        }

        // Load via real runtime reflection (a collectible context) — only the runtime
        // metadata reader throws the BadImageFormatException this test guards against.
        var alc = new AssemblyLoadContext("rewritten-fixture", isCollectible: true);
        try
        {
            using var ms = new MemoryStream(rewritten);
            var asm = alc.LoadFromStream(ms);
            var foo = asm.GetType("Fixture.Foo");
            Assert.NotNull(foo);

            // GetParameters() on every method (and constructor) must succeed and report
            // the arity the signature declares. Pre-fix, at least one of these threw.
            foreach (var method in foo!.GetMethods(BindingFlags.Public | BindingFlags.Instance
                         | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var ps = method.GetParameters(); // must not throw
                Assert.Equal(ExpectedArity(method.Name), ps.Length);
            }

            // Spot-check that names survived the rewrite for a parameterized method.
            var beta = foo.GetMethod("Beta")!;
            Assert.Equal(["a", "b"], beta.GetParameters().Select(p => p.Name!).ToArray());
        }
        finally
        {
            alc.Unload();
        }
    }

    private static int ExpectedArity(string methodName) => methodName switch
    {
        "Alpha" => 0,
        "Beta" => 2,
        "Gamma" => 0,
        "Delta" => 1,
        "StaticZero" => 0,
        _ => 0, // object's inherited members are excluded by DeclaredOnly
    };

    /// <summary>
    /// Emits <c>Fixture.Foo</c> with methods ordered to expose the ParamList bug:
    /// a zero-arg method precedes a two-arg one, then another zero-arg, then a one-arg.
    /// </summary>
    private static void BuildFixtureAssembly(Stream output)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName("Fixture"), typeof(object).Assembly);
        var module = ab.DefineDynamicModule("Fixture");
        var type = module.DefineType("Fixture.Foo", TypeAttributes.Public | TypeAttributes.Class);

        // Alpha(): void — zero params, FIRST.
        EmitReturnVoid(type.DefineMethod("Alpha", MethodAttributes.Public, typeof(void), Type.EmptyTypes));

        // Beta(int a, int b): int — parameterized, with named params.
        var beta = type.DefineMethod("Beta", MethodAttributes.Public, typeof(int), [typeof(int), typeof(int)]);
        beta.DefineParameter(1, ParameterAttributes.None, "a");
        beta.DefineParameter(2, ParameterAttributes.None, "b");
        EmitReturnDefaultInt(beta);

        // Gamma(): int — zero params again, between parameterized methods.
        EmitReturnDefaultInt(type.DefineMethod("Gamma", MethodAttributes.Public, typeof(int), Type.EmptyTypes));

        // Delta(string s): string — one param.
        var delta = type.DefineMethod("Delta", MethodAttributes.Public, typeof(string), [typeof(string)]);
        delta.DefineParameter(1, ParameterAttributes.None, "s");
        EmitReturnArg1(delta);

        // StaticZero(): void — a static zero-arg method for good measure.
        EmitReturnVoid(type.DefineMethod("StaticZero",
            MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes));

        type.CreateType();
        ab.Save(output);
    }

    private static void EmitReturnVoid(MethodBuilder m)
    {
        var il = m.GetILGenerator();
        il.Emit(OpCodes.Ret);
    }

    private static void EmitReturnDefaultInt(MethodBuilder m)
    {
        var il = m.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitReturnArg1(MethodBuilder m)
    {
        var il = m.GetILGenerator();
        il.Emit(OpCodes.Ldarg_1); // instance method: arg0=this, arg1=s
        il.Emit(OpCodes.Ret);
    }
}
