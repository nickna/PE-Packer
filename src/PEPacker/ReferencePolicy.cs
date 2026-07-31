namespace PEPacker;

/// <summary>
/// What <see cref="AssemblyReferenceRewriter"/> does with one assembly reference from
/// the source image.
/// </summary>
public enum ReferenceAction
{
    /// <summary>
    /// Copy the reference to the output unchanged. Type references scoped to it are
    /// remapped onto the copied row.
    /// </summary>
    Keep,

    /// <summary>
    /// Omit the reference from the output. A type reference scoped to a dropped assembly
    /// cannot be remapped, so it is reported as an error rather than silently nulled.
    /// </summary>
    Drop,

    /// <summary>
    /// Omit the reference and resolve each type scoped to it against the SDK facades
    /// instead — <c>System.Runtime</c>, <c>System.Collections</c> and friends. This is
    /// what the rewriter exists to do for <c>System.Private.CoreLib</c>.
    /// </summary>
    RetargetToFacades
}

/// <summary>
/// Decides <see cref="ReferenceAction"/> per assembly simple name.
/// </summary>
/// <remarks>
/// The rewriter previously hardcoded its two decisions — retarget
/// <c>System.Private.CoreLib</c>, drop <c>SharpTS</c> — which put one consumer's
/// deployment choice inside a general-purpose package. Retargeting CoreLib is the
/// rewriter's whole purpose and stays the default; dropping a named application
/// assembly is policy, and now belongs to the caller.
/// </remarks>
public static class ReferencePolicy
{
    /// <summary>
    /// The assembly whose references the rewriter exists to retarget.
    /// </summary>
    private const string CoreLib = WellKnownAssemblies.CoreLib;

    /// <summary>
    /// Preserves the behaviour of releases up to and including 1.0.4: retarget
    /// <c>System.Private.CoreLib</c> onto the SDK facades, drop <c>SharpTS</c>, keep
    /// everything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>SharpTS</c> entry is compatibility, not design. SharpTS decides whether to
    /// rewrite at all partly by testing for that reference
    /// (<c>ILCompiler.cs</c>, <c>hasSharpTsReference</c>), so stripping it is the point of
    /// the pass in that case rather than a side effect — silently keeping it would leave
    /// the dependency in SharpTS's output and make the rewrite pointless work.
    /// </para>
    /// <para>
    /// New callers should pass <see cref="RetargetCoreLibOnly"/> or their own policy. The
    /// special case is expected to be removed once SharpTS passes one explicitly, which it
    /// will need to do anyway when it starts shipping programs that genuinely depend on
    /// <c>SharpTS.dll</c>.
    /// </para>
    /// </remarks>
    public static ReferenceAction Default(string assemblyName) => assemblyName switch
    {
        CoreLib => ReferenceAction.RetargetToFacades,
        "SharpTS" => ReferenceAction.Drop,
        _ => ReferenceAction.Keep
    };

    /// <summary>
    /// Retargets <c>System.Private.CoreLib</c> and keeps every other reference. The policy
    /// a general-purpose caller wants, and what a consumer shipping an assembly its output
    /// genuinely depends on wants.
    /// </summary>
    public static ReferenceAction RetargetCoreLibOnly(string assemblyName) =>
        assemblyName == CoreLib ? ReferenceAction.RetargetToFacades : ReferenceAction.Keep;

    /// <summary>
    /// Retargets <c>System.Private.CoreLib</c>, drops the named assemblies, keeps the rest.
    /// </summary>
    /// <param name="assemblyNames">Simple names to omit from the output.</param>
    public static Func<string, ReferenceAction> DroppingReferences(params string[] assemblyNames)
    {
        ArgumentNullException.ThrowIfNull(assemblyNames);
        var drop = new HashSet<string>(assemblyNames, StringComparer.Ordinal);

        return assemblyName => assemblyName == CoreLib
            ? ReferenceAction.RetargetToFacades
            : drop.Contains(assemblyName) ? ReferenceAction.Drop : ReferenceAction.Keep;
    }
}
