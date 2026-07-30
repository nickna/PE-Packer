namespace PEPacker.Bundling;

/// <summary>
/// Framework roll-forward policy written into a bundled application's
/// <c>runtimeconfig.json</c>.
/// </summary>
/// <remarks>
/// Values mirror the host's <c>rollForward</c> setting. See
/// <see href="https://learn.microsoft.com/dotnet/core/versions/selection"/>.
/// </remarks>
internal enum RollForwardPolicy
{
    /// <summary>Highest patch of the requested major.minor; never a different minor.</summary>
    LatestPatch,

    /// <summary>The host default: nearest higher minor if the requested one is absent.</summary>
    Minor,

    /// <summary>Highest available minor within the requested major.</summary>
    LatestMinor,

    /// <summary>Nearest higher major if the requested one is absent.</summary>
    Major,

    /// <summary>Highest available major.</summary>
    LatestMajor,

    /// <summary>Exact match only.</summary>
    Disable
}

/// <summary>
/// Builds the <c>runtimeconfig.json</c> embedded in a bundled executable.
/// </summary>
/// <remarks>
/// <para>
/// Both bundlers previously carried an identical copy of this that derived the
/// framework version from <see cref="Environment.Version"/> and emitted
/// <c>Major.Minor.Build</c> with no roll-forward policy. That pins the exact patch
/// level of whatever produced the bundle, so a target machine with an older patch is
/// rejected — the host does not roll *backward*. Under Native AOT it is worse:
/// <see cref="Environment.Version"/> reports the ILCompiler runtime pack the tool was
/// built against, a build-machine artifact unrelated to any target. A tool published
/// against pack 10.0.9 stamped <c>"version": "10.0.9"</c> into every bundle its users
/// produced.
/// </para>
/// <para>
/// The patch component is therefore always zero: any <c>10.0.x</c> satisfies a request
/// for <c>10.0.0</c>. Only the major and minor are taken from the running framework,
/// which is stable across patches and correct under AOT.
/// </para>
/// </remarks>
internal static class RuntimeConfig
{
    /// <summary>
    /// Generates the <c>runtimeconfig.json</c> content for a framework-dependent bundle.
    /// </summary>
    /// <param name="frameworkVersion">
    /// Target framework version. Only <see cref="Version.Major"/> and
    /// <see cref="Version.Minor"/> are honoured; the patch is deliberately emitted as
    /// zero. When <see langword="null"/>, the running framework's major and minor are
    /// used.
    /// </param>
    /// <param name="rollForward">Roll-forward policy to record.</param>
    internal static string Generate(
        Version? frameworkVersion = null,
        RollForwardPolicy rollForward = RollForwardPolicy.LatestMinor)
    {
        var version = frameworkVersion ?? Environment.Version;
        int major = version.Major;
        int minor = version.Minor < 0 ? 0 : version.Minor;

        return $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{major}}.{{minor}}",
                "rollForward": "{{ToJsonValue(rollForward)}}",
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "{{major}}.{{minor}}.0"
                }
              }
            }
            """;
    }

    /// <summary>
    /// Maps a policy to the camelCase spelling the host expects.
    /// </summary>
    private static string ToJsonValue(RollForwardPolicy policy) => policy switch
    {
        RollForwardPolicy.LatestPatch => "latestPatch",
        RollForwardPolicy.Minor => "minor",
        RollForwardPolicy.LatestMinor => "latestMinor",
        RollForwardPolicy.Major => "major",
        RollForwardPolicy.LatestMajor => "latestMajor",
        RollForwardPolicy.Disable => "disable",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };
}
