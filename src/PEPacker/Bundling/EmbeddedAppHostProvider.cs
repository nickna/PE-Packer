namespace PEPacker.Bundling;

/// <summary>
/// Supplies the Windows and Linux apphost templates carried by the package so
/// built-in bundling does not depend on an installed SDK.
/// </summary>
internal static class EmbeddedAppHostProvider
{
    private const string ResourcePrefix = "PEPacker.Resources.apphost.";

    internal static IReadOnlyList<string> SupportedRuntimeIdentifiers { get; } =
    [
        "win-x64",
        "win-x86",
        "win-arm64",
        "linux-x64",
        "linux-arm",
        "linux-arm64",
    ];

    internal static bool TryRead(string runtimeIdentifier, out byte[]? appHost)
    {
        if (!SupportedRuntimeIdentifiers.Contains(runtimeIdentifier, StringComparer.OrdinalIgnoreCase))
        {
            appHost = null;
            return false;
        }

        using Stream? resource = typeof(EmbeddedAppHostProvider).Assembly
            .GetManifestResourceStream(ResourcePrefix + runtimeIdentifier.ToLowerInvariant());
        if (resource is null)
        {
            // Source builds can set PEPackerEmbedAppHosts=false. Treat that the
            // same as an older/minimal package and continue to the installed pack.
            appHost = null;
            return false;
        }

        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        appHost = bytes.ToArray();
        return true;
    }
}
