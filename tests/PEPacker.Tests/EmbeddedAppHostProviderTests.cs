using PEPacker.Bundling;
using Xunit;

namespace PEPacker.Tests;

public class EmbeddedAppHostProviderTests
{
    [Fact]
    public void CarriesEverySupportedWindowsAndLinuxTemplate()
    {
        Assert.Equal(
            [
                "win-x64",
                "win-x86",
                "win-arm64",
                "linux-x64",
                "linux-arm",
                "linux-arm64",
            ],
            EmbeddedAppHostProvider.SupportedRuntimeIdentifiers);

        foreach (string rid in EmbeddedAppHostProvider.SupportedRuntimeIdentifiers)
        {
            Assert.True(
                EmbeddedAppHostProvider.TryRead(rid, out byte[]? appHost),
                $"No embedded apphost resource was found for {rid}");
            Assert.NotNull(appHost);
            Assert.True(appHost.Length > 50_000, $"{rid} apphost was unexpectedly small");

            if (rid.StartsWith("win", StringComparison.Ordinal))
            {
                Assert.Equal((byte)'M', appHost[0]);
                Assert.Equal((byte)'Z', appHost[1]);
            }
            else
            {
                Assert.Equal([0x7f, (byte)'E', (byte)'L', (byte)'F'], appHost[..4]);
            }
        }
    }

    [Fact]
    public void UnknownRuntimeIdentifier_HasNoEmbeddedTemplate()
    {
        Assert.False(EmbeddedAppHostProvider.TryRead("plan9-sparc", out byte[]? appHost));
        Assert.Null(appHost);
    }
}
