using System.Reflection;
using System.Reflection.Emit;
using PEPacker.Tests.Infrastructure;
using Xunit;
using static PEPacker.Tests.Infrastructure.RewriterTestHelpers;

namespace PEPacker.Tests;

/// <summary>
/// Self-tests for the verification harness.
/// <para>
/// The round-trip tests assert that the rewrite introduces no ILVerify findings. That
/// assertion is only worth anything if the harness reports findings when they exist — a
/// resolver that quietly fails would make every such test pass for the wrong reason.
/// </para>
/// </summary>
public class ILVerifyHarnessTests
{
    [Fact]
    public void Harness_ReportsAnError_ForAMethodThatReturnsWithoutAValue()
    {
        var image = Build("InvalidILFixture", module =>
        {
            var type = module.DefineType("Fx.Invalid", TypeAttributes.Public);

            // Declared to return int, but returns with an empty stack.
            var method = type.DefineMethod("ReturnsNothing",
                MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
            method.GetILGenerator().Emit(OpCodes.Ret);

            type.CreateType();
        });

        using var harness = new ILVerifyHarness();
        var findings = harness.Verify(image);

        Assert.NotEmpty(findings);
        Assert.Contains(findings, finding => finding.StartsWith("Fx.Invalid.ReturnsNothing", StringComparison.Ordinal));
    }

    [Fact]
    public void Harness_ReportsNothing_ForAValidMethod()
    {
        var image = Build("ValidILFixture", module =>
        {
            var type = module.DefineType("Fx.Valid", TypeAttributes.Public);

            var method = type.DefineMethod("ReturnsSeven",
                MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4_7);
            il.Emit(OpCodes.Ret);

            type.CreateType();
        });

        using var harness = new ILVerifyHarness();
        Assert.Empty(harness.Verify(image));
    }
}
