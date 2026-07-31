namespace PEPacker;

/// <summary>
/// Assembly simple names the library reasons about by name.
/// </summary>
internal static class WellKnownAssemblies
{
    /// <summary>
    /// The runtime's implementation assembly, and the one the rewriter exists to retarget away
    /// from. It is never a valid retarget destination, so it is also skipped when indexing a
    /// framework directory.
    /// </summary>
    internal const string CoreLib = "System.Private.CoreLib";

    /// <summary>
    /// The core facade, which must resolve for a reference index to be usable at all.
    /// </summary>
    internal const string SystemRuntime = "System.Runtime";
}
