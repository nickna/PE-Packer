using System.Runtime.InteropServices;

namespace PEPacker.Bundling;

/// <summary>
/// Locates the dotnet root directory — the one holding <c>sdk</c>, <c>packs</c> and
/// <c>shared</c>.
/// </summary>
/// <remarks>
/// <para>
/// This used to exist twice, once in <see cref="ManualBundler"/> (for host packs) and once in
/// <see cref="SdkBundlerDetector"/> (for <c>Microsoft.NET.HostModel.dll</c>), probing different
/// directories. Two answers to one question meant a machine could have its apphost template
/// found and its SDK not, for no reason a caller could see. This is the union of both probe
/// sets.
/// </para>
/// <para>
/// There is deliberately no <c>RuntimeEnvironment.GetRuntimeDirectory()</c> fallback. Under
/// Native AOT that returns the running application's own directory rather than a framework
/// directory, so walking three levels up from it yields a path that exists and is not a dotnet
/// root — a wrong answer that looks like a right one. Returning <see langword="null"/> lets the
/// caller say "no .NET installation was found", which is both true and actionable.
/// </para>
/// </remarks>
internal static class DotNetRoot
{
    /// <summary>
    /// Finds the dotnet root, or <see langword="null"/> when no installation is visible.
    /// </summary>
    internal static string? Find()
    {
        foreach (var variable in EnvironmentVariableNames())
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(value) && Directory.Exists(value))
            {
                return value;
            }
        }

        foreach (var path in ProbePaths())
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// The environment variables the host itself honours, most specific first.
    /// </summary>
    /// <remarks>
    /// A 32-bit process on 64-bit Windows reads <c>DOTNET_ROOT(x86)</c>, which points at a
    /// different installation from <c>DOTNET_ROOT</c>. Ignoring it made an x86 build resolve
    /// x64 host packs.
    /// </remarks>
    private static IEnumerable<string> EnvironmentVariableNames()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
        {
            yield return "DOTNET_ROOT(x86)";
        }

        yield return "DOTNET_ROOT";
    }

    /// <summary>
    /// Default install locations per platform, in the order the host probes them.
    /// </summary>
    private static IEnumerable<string> ProbePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
            {
                yield return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet");
            }

            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/usr/local/share/dotnet";
            yield return "/opt/homebrew/opt/dotnet/libexec";
            yield return "/usr/share/dotnet";
            yield return UserLocalInstall();
        }
        else
        {
            yield return "/usr/share/dotnet";
            yield return "/usr/lib/dotnet";
            yield return "/opt/dotnet";
            yield return UserLocalInstall();
        }
    }

    /// <summary>
    /// The per-user install <c>dotnet-install.sh</c> produces.
    /// </summary>
    private static string UserLocalInstall()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? string.Empty : Path.Combine(home, ".dotnet");
    }
}
