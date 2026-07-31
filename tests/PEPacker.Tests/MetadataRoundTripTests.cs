using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using PEPacker;
using PEPacker.Tests.Infrastructure;
using Xunit;
using static PEPacker.Tests.Infrastructure.RewriterTestHelpers;

namespace PEPacker.Tests;

/// <summary>
/// Differential round-trip tests: rewrite an assembly, then compare it against its input
/// table by table and row by row.
/// <para>
/// Every defect found in this rewriter shared one shape — metadata was rebuilt from
/// scratch, something was omitted or mis-ordered, and the output still loaded, so the
/// loss only surfaced later as missing accessors, a wrong string literal or a P/Invoke
/// bound to nothing. Targeted tests catch those one at a time, after a symptom appears.
/// This catches the class.
/// </para>
/// <para>
/// See <see cref="MetadataDiffer"/> for what counts as a difference; retargeting
/// CoreLib onto SDK facades is expected and excluded.
/// </para>
/// </summary>
public class MetadataRoundTripTests
{
    [Fact]
    public void RoundTrip_PreservesEverything_ForABroadFixture()
    {
        AssertRoundTrips("BroadFixture", BuildBroadFixture);
    }

    [Fact]
    public void RoundTrip_PreservesEverything_ForControlFlow()
    {
        AssertRoundTrips("ControlFlowFixture", BuildControlFlowFixture);
    }

    [Fact]
    public void RoundTrip_PreservesEverything_ForGenerics()
    {
        AssertRoundTrips("GenericFixture", BuildGenericFixture);
    }

    [Fact]
    public void RoundTrip_PreservesEverything_ForInterop()
    {
        AssertRoundTrips("InteropFixture", BuildInteropFixture);
    }

    /// <summary>
    /// Reports which tables the rewriter claims to support but no fixture actually
    /// produces, so a supported-but-unexercised table is visible rather than assumed.
    /// </summary>
    /// <remarks>
    /// Every defect so far lived in a table nothing tested. This is the cheapest guard
    /// against the next one: adding a table to the allow-list without a fixture that
    /// produces it fails here.
    /// </remarks>
    [Fact]
    public void Fixtures_Exercise_EverySupportedTable()
    {
        var fixtures = new (string Name, Action<ModuleBuilder> Emit)[]
        {
            ("BroadFixture", BuildBroadFixture),
            ("ControlFlowFixture", BuildControlFlowFixture),
            ("GenericFixture", BuildGenericFixture),
            ("InteropFixture", BuildInteropFixture),
        };

        var covered = new HashSet<TableIndex>();
        foreach (var (name, emit) in fixtures)
        {
            var reader = new PEReader(new MemoryStream(Build(name, emit))).GetMetadataReader();
            foreach (TableIndex table in Enum.GetValues<TableIndex>())
            {
                if (reader.GetTableRowCount(table) > 0) covered.Add(table);
            }
        }

        var missing = AssemblyReferenceRewriter.SupportedTables
            .Except(covered)
            .OrderBy(t => t.ToString())
            .ToList();

        Assert.True(missing.Count == 0,
            "supported but never exercised by a round-trip fixture: " +
            string.Join(", ", missing));
    }

    private static void AssertRoundTrips(string name, Action<ModuleBuilder> emit)
    {
        var source = Build(name, emit);
        var rewritten = Rewrite(source);

        var differences = MetadataDiffer.Compare(source, rewritten);

        Assert.True(differences.Count == 0,
            $"{differences.Count} difference(s) after rewrite:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", differences.Take(40)));

        AssertNoVerificationErrorsIntroduced(source, rewritten);
    }

    /// <summary>
    /// Runs ILVerify over both images and fails on findings the rewrite introduced.
    /// </summary>
    /// <remarks>
    /// Compared against a baseline rather than asserted clean outright, because
    /// PersistedAssemblyBuilder output is not guaranteed verifiable on its own — the point
    /// is to isolate what the rewriter is responsible for. This catches what a metadata
    /// diff cannot: a well-formed copy of ill-formed input still verifies the same, but a
    /// copy that breaks a stack or handler invariant does not.
    /// </remarks>
    private static void AssertNoVerificationErrorsIntroduced(byte[] source, byte[] rewritten)
    {
        using var verifier = new ILVerifyHarness();

        var before = verifier.Verify(source)
            .GroupBy(finding => finding)
            .ToDictionary(group => group.Key, group => group.Count());

        var introduced = new List<string>();
        foreach (var group in verifier.Verify(rewritten).GroupBy(finding => finding))
        {
            before.TryGetValue(group.Key, out int alreadyPresent);
            for (int i = alreadyPresent; i < group.Count(); i++)
            {
                introduced.Add(group.Key);
            }
        }

        Assert.True(introduced.Count == 0,
            $"the rewrite introduced {introduced.Count} IL verification error(s):{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", introduced.Take(30)));
    }

    // ---- fixtures -------------------------------------------------------------

    /// <summary>
    /// Types, members and attributes across the surface SharpTS emits.
    /// </summary>
    private static void BuildBroadFixture(ModuleBuilder module)
    {
        var iface = module.DefineType("Fx.IShape",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        iface.DefineMethod("Area",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual |
            MethodAttributes.NewSlot | MethodAttributes.HideBySig,
            typeof(double), Type.EmptyTypes);
        var ifaceType = iface.CreateType();

        var type = module.DefineType("Fx.Circle", TypeAttributes.Public);
        type.AddInterfaceImplementation(ifaceType);

        var radius = type.DefineField("_radius", typeof(double), FieldAttributes.Private);

        var literal = type.DefineField("Sides", typeof(int),
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
        literal.SetConstant(1);

        // Static initialized data lands in FieldRVA.
        var data = type.DefineInitializedData("Lookup", [1, 2, 3, 4], FieldAttributes.Private | FieldAttributes.Static);
        _ = data;

        var area = type.DefineMethod("Area",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(double), Type.EmptyTypes);
        var areaIl = area.GetILGenerator();
        areaIl.Emit(OpCodes.Ldarg_0);
        areaIl.Emit(OpCodes.Ldfld, radius);
        areaIl.Emit(OpCodes.Ret);
        type.DefineMethodOverride(area, ifaceType.GetMethod("Area")!);

        // Property with both accessors, plus an indexer carrying parameters.
        var getter = type.DefineMethod("get_Radius",
            MethodAttributes.Public | MethodAttributes.SpecialName, typeof(double), Type.EmptyTypes);
        var getIl = getter.GetILGenerator();
        getIl.Emit(OpCodes.Ldarg_0);
        getIl.Emit(OpCodes.Ldfld, radius);
        getIl.Emit(OpCodes.Ret);

        var setter = type.DefineMethod("set_Radius",
            MethodAttributes.Public | MethodAttributes.SpecialName, typeof(void), [typeof(double)]);
        var setIl = setter.GetILGenerator();
        setIl.Emit(OpCodes.Ldarg_0);
        setIl.Emit(OpCodes.Ldarg_1);
        setIl.Emit(OpCodes.Stfld, radius);
        setIl.Emit(OpCodes.Ret);

        var property = type.DefineProperty("Radius", PropertyAttributes.None, typeof(double), Type.EmptyTypes);
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);

        // Event.
        var add = type.DefineMethod("add_Resized",
            MethodAttributes.Public | MethodAttributes.SpecialName, typeof(void), [typeof(EventHandler)]);
        add.GetILGenerator().Emit(OpCodes.Ret);
        var remove = type.DefineMethod("remove_Resized",
            MethodAttributes.Public | MethodAttributes.SpecialName, typeof(void), [typeof(EventHandler)]);
        remove.GetILGenerator().Emit(OpCodes.Ret);
        var resized = type.DefineEvent("Resized", EventAttributes.None, typeof(EventHandler));
        resized.SetAddOnMethod(add);
        resized.SetRemoveOnMethod(remove);

        // Optional parameters with defaults, including null.
        var optional = type.DefineMethod("Describe", MethodAttributes.Public | MethodAttributes.Static,
            typeof(string), [typeof(int), typeof(string), typeof(string)]);
        optional.DefineParameter(1, ParameterAttributes.Optional | ParameterAttributes.HasDefault, "count").SetConstant(3);
        optional.DefineParameter(2, ParameterAttributes.Optional | ParameterAttributes.HasDefault, "label").SetConstant("circle");
        optional.DefineParameter(3, ParameterAttributes.Optional | ParameterAttributes.HasDefault, "extra").SetConstant(null);
        var optionalIl = optional.GetILGenerator();
        optionalIl.Emit(OpCodes.Ldarg_1);
        optionalIl.Emit(OpCodes.Ret);

        // Custom attribute, exercising the CustomAttribute table and its blob.
        type.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(ObsoleteAttribute).GetConstructor([typeof(string)])!, ["legacy shape"]));

        // Nested type.
        var nested = type.DefineNestedType("Inner", TypeAttributes.NestedPublic);
        nested.DefineField("Value", typeof(int), FieldAttributes.Public);
        nested.CreateType();

        type.CreateType();
    }

    /// <summary>
    /// The IL shapes that broke the decoder: switch tables, token-bearing array access,
    /// calli, string literals, and every exception-handler kind.
    /// </summary>
    private static void BuildControlFlowFixture(ModuleBuilder module)
    {
        var type = module.DefineType("Fx.Flow", TypeAttributes.Public);

        // A range of switch arities, including the ones that desynchronised the old walk.
        foreach (var arity in new[] { 1, 3, 8, 17, 19, 31, 46 })
        {
            var method = type.DefineMethod($"Switch{arity}",
                MethodAttributes.Public | MethodAttributes.Static, typeof(string), [typeof(int)]);
            var il = method.GetILGenerator();
            var labels = new Label[arity];
            for (int i = 0; i < arity; i++) labels[i] = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Switch, labels);
            il.Emit(OpCodes.Ldstr, $"A-{arity}");
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldstr, $"default-{arity}");
            il.Emit(OpCodes.Ret);
            for (int i = 0; i < arity; i++)
            {
                il.MarkLabel(labels[i]);
                il.Emit(OpCodes.Ldstr, $"case{i}-{arity}");
                il.Emit(OpCodes.Ret);
            }
        }

        // Token-bearing ldelem/stelem.
        var arrays = type.DefineMethod("Arrays", MethodAttributes.Public | MethodAttributes.Static,
            typeof(string), Type.EmptyTypes);
        var arraysIl = arrays.GetILGenerator();
        arraysIl.Emit(OpCodes.Ldc_I4_2);
        arraysIl.Emit(OpCodes.Newarr, typeof(string));
        arraysIl.Emit(OpCodes.Dup);
        arraysIl.Emit(OpCodes.Ldc_I4_0);
        arraysIl.Emit(OpCodes.Ldstr, "first");
        arraysIl.Emit(OpCodes.Stelem, typeof(string));
        arraysIl.Emit(OpCodes.Ldc_I4_0);
        arraysIl.Emit(OpCodes.Ldelem, typeof(string));
        arraysIl.Emit(OpCodes.Ret);

        // calli, whose operand is a StandAloneSig row.
        var target = type.DefineMethod("Target", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), Type.EmptyTypes);
        var targetIl = target.GetILGenerator();
        targetIl.Emit(OpCodes.Ldc_I4_S, (byte)42);
        targetIl.Emit(OpCodes.Ret);

        var calli = type.DefineMethod("ViaCalli", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), Type.EmptyTypes);
        var calliIl = calli.GetILGenerator();
        calliIl.Emit(OpCodes.Ldftn, target);
        calliIl.EmitCalli(OpCodes.Calli, CallingConventions.Standard, typeof(int), Type.EmptyTypes, null);
        calliIl.Emit(OpCodes.Ret);

        // Locals plus catch / filter / finally, so every ExceptionRegionKind appears.
        var handlers = type.DefineMethod("Handlers", MethodAttributes.Public | MethodAttributes.Static,
            typeof(string), [typeof(int)]);
        var hIl = handlers.GetILGenerator();
        hIl.DeclareLocal(typeof(string));
        hIl.DeclareLocal(typeof(int));

        hIl.BeginExceptionBlock();
        hIl.Emit(OpCodes.Ldstr, "tried");
        hIl.Emit(OpCodes.Stloc_0);
        hIl.BeginCatchBlock(typeof(InvalidOperationException));
        hIl.Emit(OpCodes.Pop);
        hIl.Emit(OpCodes.Ldstr, "caught");
        hIl.Emit(OpCodes.Stloc_0);
        hIl.BeginFinallyBlock();
        hIl.Emit(OpCodes.Ldc_I4_1);
        hIl.Emit(OpCodes.Stloc_1);
        hIl.EndExceptionBlock();
        hIl.Emit(OpCodes.Ldloc_0);
        hIl.Emit(OpCodes.Ret);

        type.CreateType();
    }

    private static void BuildGenericFixture(ModuleBuilder module)
    {
        var type = module.DefineType("Fx.Box`1", TypeAttributes.Public);
        var typeParams = type.DefineGenericParameters("T");
        typeParams[0].SetGenericParameterAttributes(GenericParameterAttributes.ReferenceTypeConstraint);

        var items = type.DefineField("_items", typeof(List<>).MakeGenericType(typeParams[0]), FieldAttributes.Private);

        var add = type.DefineMethod("Add", MethodAttributes.Public, typeof(void), [typeParams[0]]);
        var addIl = add.GetILGenerator();
        addIl.Emit(OpCodes.Ldarg_0);
        addIl.Emit(OpCodes.Ldfld, items);
        addIl.Emit(OpCodes.Ldarg_1);
        addIl.Emit(OpCodes.Callvirt, TypeBuilder.GetMethod(
            typeof(List<>).MakeGenericType(typeParams[0]),
            typeof(List<>).GetMethod("Add")!));
        addIl.Emit(OpCodes.Ret);

        // Generic method with its own parameter and a constraint.
        var convert = type.DefineMethod("Convert", MethodAttributes.Public | MethodAttributes.Static);
        var methodParams = convert.DefineGenericParameters("U");
        methodParams[0].SetInterfaceConstraints(typeof(IDisposable));
        convert.SetReturnType(methodParams[0]);
        convert.SetParameters(methodParams[0]);
        var convertIl = convert.GetILGenerator();
        convertIl.Emit(OpCodes.Ldarg_0);
        convertIl.Emit(OpCodes.Ret);

        type.CreateType();

        // Calls to instantiated generic methods, which are what produce MethodSpec rows:
        // one over this module's own MethodDef and one over a MemberRef into CoreLib, so
        // both shapes of MethodSpec.Method survive the rewrite.
        var user = module.DefineType("Fx.SpecUser", TypeAttributes.Public);

        var identity = user.DefineMethod("Identity",
            MethodAttributes.Public | MethodAttributes.Static);
        var identityParams = identity.DefineGenericParameters("V");
        identity.SetReturnType(identityParams[0]);
        identity.SetParameters(identityParams[0]);
        var identityIl = identity.GetILGenerator();
        identityIl.Emit(OpCodes.Ldarg_0);
        identityIl.Emit(OpCodes.Ret);

        var useSpecs = user.DefineMethod("UseSpecs",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int[]), Type.EmptyTypes);
        var useSpecsIl = useSpecs.GetILGenerator();
        useSpecsIl.Emit(OpCodes.Ldstr, "spec");
        useSpecsIl.Emit(OpCodes.Call, identity.MakeGenericMethod(typeof(string)));
        useSpecsIl.Emit(OpCodes.Pop);
        useSpecsIl.Emit(OpCodes.Call, typeof(Array).GetMethod("Empty")!.MakeGenericMethod(typeof(int)));
        useSpecsIl.Emit(OpCodes.Ret);

        user.CreateType();
    }

    private static void BuildInteropFixture(ModuleBuilder module)
    {
        var type = module.DefineType("Fx.Native", TypeAttributes.Public);

        var pinvoke = type.DefinePInvokeMethod(
            "GetCurrentProcessId", "kernel32.dll",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl,
            CallingConventions.Standard, typeof(uint), Type.EmptyTypes,
            CallingConvention.Winapi, CharSet.Auto);
        pinvoke.SetImplementationFlags(MethodImplAttributes.PreserveSig);

        var second = type.DefinePInvokeMethod(
            "GetTickCount", "kernel32.dll",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl,
            CallingConventions.Standard, typeof(uint), Type.EmptyTypes,
            CallingConvention.Winapi, CharSet.Unicode);
        second.SetImplementationFlags(MethodImplAttributes.PreserveSig);

        type.CreateType();

        // An explicit-layout struct: ClassLayout for the type, FieldLayout for each
        // positioned field. Both are copied, so both belong in the differential check
        // rather than only in the guard's accept test.
        var overlapped = module.DefineType("Fx.Overlapped",
            TypeAttributes.Public | TypeAttributes.ExplicitLayout, typeof(ValueType));
        overlapped.DefineField("AsInt", typeof(int), FieldAttributes.Public).SetOffset(0);
        overlapped.DefineField("AsFloat", typeof(float), FieldAttributes.Public).SetOffset(0);
        overlapped.DefineField("Tail", typeof(short), FieldAttributes.Public).SetOffset(4);
        overlapped.CreateType();

        // [MarshalAs] is a pseudo-attribute: PersistedAssemblyBuilder lowers it to a
        // FieldMarshal row rather than a CustomAttribute row. The padding fields push the
        // marshalled field to a high row so its HasFieldMarshal coded index (Field = tag 0)
        // sorts AFTER the marshalled parameter's (Param row 1, tag 1) even though fields
        // are copied first — the emit-sorted path is exercised, not just the copy.
        var marshalled = module.DefineType("Fx.Marshalled", TypeAttributes.Public);
        for (int i = 0; i < 3; i++)
        {
            marshalled.DefineField($"_pad{i}", typeof(int), FieldAttributes.Private);
        }

        var message = marshalled.DefineField("Message", typeof(string), FieldAttributes.Public);
        message.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(MarshalAsAttribute).GetConstructor([typeof(UnmanagedType)])!,
            [UnmanagedType.LPWStr]));

        var format = marshalled.DefineMethod("Format",
            MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(string)]);
        format.DefineParameter(1, ParameterAttributes.None, "text").SetCustomAttribute(
            new CustomAttributeBuilder(
                typeof(MarshalAsAttribute).GetConstructor([typeof(UnmanagedType)])!,
                [UnmanagedType.LPStr]));
        format.GetILGenerator().Emit(OpCodes.Ret);

        marshalled.CreateType();
    }

}
