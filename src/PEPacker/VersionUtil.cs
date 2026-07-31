namespace PEPacker;

/// <summary>
/// One place that turns a directory name such as <c>10.0.100</c> or <c>10.0.100-rc.1.25451.107</c>
/// into something comparable.
/// </summary>
/// <remarks>
/// <para>
/// Framework, SDK and pack directories are named with NuGet versions, and comparing those names
/// as strings puts <c>10.0.9</c> above <c>10.0.10</c> and <c>9.0.17</c> above both. That bug has
/// been fixed three separate times in this repository, each time in a private copy of the same
/// four lines. This is the copy.
/// </para>
/// <para>
/// The prerelease suffix is stripped rather than ordered, because <see cref="Version"/> cannot
/// represent it — so <c>10.0.100-rc.1</c> parses equal to <c>10.0.100</c>.
/// <see cref="Parsed.IsPrerelease"/> is carried alongside so the tie can be broken the way NuGet
/// would: a stable release wins over a prerelease of the same number.
/// </para>
/// </remarks>
internal static class VersionUtil
{
    /// <summary>
    /// A directory version, with enough information to order it against another.
    /// </summary>
    /// <param name="Version">The numeric part.</param>
    /// <param name="IsPrerelease">Whether a <c>-suffix</c> was stripped to get there.</param>
    internal readonly record struct Parsed(Version Version, bool IsPrerelease)
        : IComparable<Parsed>
    {
        /// <summary>
        /// Orders by number, then puts a stable release above a prerelease of the same number.
        /// </summary>
        public int CompareTo(Parsed other)
        {
            var byNumber = Version.CompareTo(other.Version);
            return byNumber != 0
                ? byNumber
                : (IsPrerelease ? 0 : 1).CompareTo(other.IsPrerelease ? 0 : 1);
        }

        /// <summary>Whether <paramref name="left"/> sorts above <paramref name="right"/>.</summary>
        public static bool operator >(Parsed left, Parsed right) => left.CompareTo(right) > 0;

        /// <summary>Whether <paramref name="left"/> sorts below <paramref name="right"/>.</summary>
        public static bool operator <(Parsed left, Parsed right) => left.CompareTo(right) < 0;

        /// <summary>Whether <paramref name="left"/> sorts at or above <paramref name="right"/>.</summary>
        public static bool operator >=(Parsed left, Parsed right) => left.CompareTo(right) >= 0;

        /// <summary>Whether <paramref name="left"/> sorts at or below <paramref name="right"/>.</summary>
        public static bool operator <=(Parsed left, Parsed right) => left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Parses a version directory name, tolerating a prerelease suffix.
    /// </summary>
    /// <param name="text">A name such as <c>10.0.100</c> or <c>10.0.100-preview.3</c>.</param>
    /// <param name="parsed">The parsed version when this returns <see langword="true"/>.</param>
    /// <returns>Whether <paramref name="text"/> held a parseable version.</returns>
    internal static bool TryParse(string? text, out Parsed parsed)
    {
        parsed = default;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var dash = text.IndexOf('-');
        var numeric = dash > 0 ? text[..dash] : text;

        if (!Version.TryParse(numeric, out var version))
        {
            return false;
        }

        parsed = new Parsed(version, dash > 0);
        return true;
    }
}
