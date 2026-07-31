using System.IO.Compression;
using System.Text;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Covers the embedded index reader against data that is not what it says it is.
/// </summary>
/// <remarks>
/// The constructor documents a single failure mode — <see cref="PEPackerException"/> — and every
/// one of these used to break it differently: a corrupt count sized an allocation before anything
/// was read, a bad version escaped as an <see cref="ArgumentException"/> from
/// <see cref="Version.Parse(string)"/>, and surplus data was not detected at all, so an index
/// silently loaded with entries missing.
/// </remarks>
public class ReferenceIndexCorruptionTests
{
    private const string Marker = "pepacker-refindex-1";
    private const string ValidAssembly = "System.Runtime|10.0.0.0||B03F5F7F11D50A3A|256";

    /// <summary>
    /// The smallest well-formed index, so every corruption below differs from a known-good file by
    /// exactly the thing under test.
    /// </summary>
    private static string[] Valid() =>
        [Marker, "1", ValidAssembly, "1", "System.Object|0"];

    [Fact]
    public void Baseline_IsActuallyValid()
    {
        using var data = Compress(Valid());
        var index = new EmbeddedReferenceAssemblyIndex(data);

        Assert.Equal(1, index.AssemblyCount);
        Assert.Equal(1, index.TypeCount);
        Assert.True(index.TryResolveType("System.Object", out var owner));
        Assert.Equal("System.Runtime", owner.Name);
    }

    /// <summary>
    /// A count is used to size a list and a dictionary, so an absurd one exhausted memory before a
    /// single entry had been parsed.
    /// </summary>
    [Fact]
    public void AbsurdAssemblyCount_IsRejectedWithoutAllocatingForIt()
    {
        using var data = Compress([Marker, int.MaxValue.ToString(), ValidAssembly, "0"]);

        var ex = Assert.Throws<PEPackerException>(() => new EmbeddedReferenceAssemblyIndex(data));
        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AbsurdTypeCount_IsRejected()
    {
        using var data = Compress([Marker, "1", ValidAssembly, int.MaxValue.ToString(), "System.Object|0"]);

        var ex = Assert.Throws<PEPackerException>(() => new EmbeddedReferenceAssemblyIndex(data));
        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Surplus lines mean the declared counts do not describe the file, so entries are being
    /// dropped. That used to load "successfully".
    /// </summary>
    [Fact]
    public void TrailingData_IsRejected()
    {
        using var data = Compress([.. Valid(), "System.String|0"]);

        var ex = Assert.Throws<PEPackerException>(() => new EmbeddedReferenceAssemblyIndex(data));
        Assert.Contains("after its last type entry", ex.Message);
    }

    /// <summary>
    /// <see cref="Version.Parse(string)"/> raises three different exception types depending on how
    /// the string is wrong, and only one of them was in the constructor's catch filter.
    /// </summary>
    [Theory]
    [InlineData("10", "too few components: ArgumentException")]
    [InlineData("abc", "not a number: FormatException")]
    [InlineData("99999999999999999999.0", "out of range: OverflowException")]
    public void MalformedFacadeVersion_SurfacesAsPEPackerException(string version, string why)
    {
        using var data = Compress([Marker, "1", $"System.Runtime|{version}||B03F5F7F11D50A3A|256", "0"]);

        var ex = Assert.Throws<PEPackerException>(() => new EmbeddedReferenceAssemblyIndex(data));
        Assert.NotNull(ex.InnerException);
        Assert.False(string.IsNullOrEmpty(why));
    }

    /// <summary>
    /// The assembly ordinal on a type line is parsed as an int, which overflows rather than
    /// failing the range check that follows it.
    /// </summary>
    [Fact]
    public void OverflowingAssemblyOrdinal_SurfacesAsPEPackerException()
    {
        using var data = Compress([Marker, "1", ValidAssembly, "1", "System.Object|99999999999999999999"]);

        Assert.Throws<PEPackerException>(() => new EmbeddedReferenceAssemblyIndex(data));
    }

    [Fact]
    public void OutOfRangeAssemblyOrdinal_IsRejected()
    {
        using var data = Compress([Marker, "1", ValidAssembly, "1", "System.Object|7"]);

        var ex = Assert.Throws<PEPackerException>(() => new EmbeddedReferenceAssemblyIndex(data));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void MalformedPublicKeyToken_SurfacesAsPEPackerException()
    {
        using var data = Compress([Marker, "1", "System.Runtime|10.0.0.0||ZZZZ|256", "0"]);

        Assert.Throws<PEPackerException>(() => new EmbeddedReferenceAssemblyIndex(data));
    }

    [Fact]
    public void TruncatedAfterTheCount_IsRejected()
    {
        using var data = Compress([Marker, "2", ValidAssembly]);

        var ex = Assert.Throws<PEPackerException>(() => new EmbeddedReferenceAssemblyIndex(data));
        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public void UnknownFormatMarker_IsRejected()
    {
        using var data = Compress(["pepacker-refindex-99", "0", "0"]);

        var ex = Assert.Throws<PEPackerException>(() => new EmbeddedReferenceAssemblyIndex(data));
        Assert.Contains("pepacker-refindex-99", ex.Message);
    }

    /// <summary>
    /// The checked-in resource must satisfy the same end-of-data check the tests above exercise —
    /// <see cref="EmbeddedReferenceAssemblyIndex.Write"/> must not emit anything after its last
    /// line.
    /// </summary>
    [Fact]
    public void TheEmbeddedResource_StillLoads()
    {
        var index = EmbeddedReferenceAssemblyIndex.Default;

        Assert.True(index.TypeCount > 1000);
        Assert.True(index.AssemblyCount > 100);
    }

    /// <summary>
    /// Writes lines in the on-disk format: deflate-compressed UTF-8, newline-separated.
    /// </summary>
    private static MemoryStream Compress(string[] lines)
    {
        var buffer = new MemoryStream();

        using (var compressor = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new StreamWriter(compressor, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            foreach (var line in lines)
            {
                writer.WriteLine(line);
            }
        }

        buffer.Position = 0;
        return buffer;
    }
}
