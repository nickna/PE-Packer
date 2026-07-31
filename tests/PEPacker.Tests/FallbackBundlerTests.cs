using PEPacker.Bundling;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Covers the compose-two-bundlers wrapper, using fakes rather than real bundling: every
/// behaviour that matters here is about which bundler is called and what surfaces when one
/// fails, none of which needs a real apphost.
/// </summary>
public class FallbackBundlerTests
{
    private static BundleRequest Request(CancellationToken cancellationToken = default) =>
        new()
        {
            EntryAssemblyPath = "irrelevant.dll",
            OutputPath = "irrelevant.exe",
            AssemblyName = "irrelevant",
            CancellationToken = cancellationToken,
        };

    [Fact]
    public void PrimarySucceeds_FallbackIsNotConsulted()
    {
        var primary = FakeBundler.Succeeding(BundleTechnique.SdkBundler);
        var fallback = FakeBundler.Succeeding(BundleTechnique.ManualBundler);

        var result = new FallbackBundler(primary, fallback).CreateSingleFileExecutable(Request());

        Assert.Equal(BundleTechnique.SdkBundler, result.Technique);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public void PrimaryFails_FallbackProducesTheResult()
    {
        var primary = FakeBundler.Failing(BundleTechnique.SdkBundler, new PEPackerException("sdk exploded"));
        var fallback = FakeBundler.Succeeding(BundleTechnique.ManualBundler);

        var result = new FallbackBundler(primary, fallback).CreateSingleFileExecutable(Request());

        Assert.Equal(BundleTechnique.ManualBundler, result.Technique);
        Assert.Equal(1, fallback.Calls);
    }

    /// <summary>
    /// Both reasons have to reach the caller: "the SDK bundler could not load" and "the built-in
    /// bundler cannot target macOS" are different problems, and only the second one being visible
    /// sends the reader after the wrong fix.
    /// </summary>
    [Fact]
    public void BothFail_ReportsBothReasons()
    {
        var primary = FakeBundler.Failing(BundleTechnique.SdkBundler, new PEPackerException("sdk exploded"));
        var fallback = FakeBundler.Failing(BundleTechnique.ManualBundler, new PEPackerException("no apphost"));

        var ex = Assert.Throws<PEPackerException>(() =>
            new FallbackBundler(primary, fallback).CreateSingleFileExecutable(Request()));

        Assert.Contains("sdk exploded", ex.Message);
        Assert.Contains("no apphost", ex.Message);

        var aggregate = Assert.IsType<AggregateException>(ex.InnerException);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
    }

    /// <summary>
    /// Cancellation is not a bundler failure. It used to be caught as one, which retried the
    /// fallback with an already-cancelled token and then reported a PEPackerException, so a
    /// caller that cancelled got told its bundlers were broken.
    /// </summary>
    [Fact]
    public void Cancellation_PropagatesAndDoesNotReachTheFallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var primary = FakeBundler.Failing(
            BundleTechnique.SdkBundler, new OperationCanceledException(cts.Token));
        var fallback = FakeBundler.Succeeding(BundleTechnique.ManualBundler);

        Assert.Throws<OperationCanceledException>(() =>
            new FallbackBundler(primary, fallback).CreateSingleFileExecutable(Request(cts.Token)));

        Assert.Equal(0, fallback.Calls);
    }

    /// <summary>
    /// A derived cancellation exception is still cancellation.
    /// </summary>
    [Fact]
    public void DerivedCancellation_AlsoPropagates()
    {
        var primary = FakeBundler.Failing(BundleTechnique.SdkBundler, new TaskCanceledException());
        var fallback = FakeBundler.Succeeding(BundleTechnique.ManualBundler);

        Assert.Throws<TaskCanceledException>(() =>
            new FallbackBundler(primary, fallback).CreateSingleFileExecutable(Request()));

        Assert.Equal(0, fallback.Calls);
    }

    /// <summary>
    /// Cancellation during the fallback attempt surfaces as cancellation too, rather than being
    /// folded into a "both bundlers failed" message.
    /// </summary>
    [Fact]
    public void CancellationInTheFallback_PropagatesAsCancellation()
    {
        var primary = FakeBundler.Failing(BundleTechnique.SdkBundler, new PEPackerException("sdk exploded"));
        var fallback = FakeBundler.Failing(BundleTechnique.ManualBundler, new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() =>
            new FallbackBundler(primary, fallback).CreateSingleFileExecutable(Request()));
    }

    /// <summary>
    /// <see cref="FallbackBundler.Technique"/> describes the primary, and keeps doing so after a
    /// bundle was served by the fallback. It used to report whichever call finished most
    /// recently — unsynchronized mutable state on an instance the factory shares.
    /// </summary>
    [Fact]
    public void Technique_IsThePrimarys_EvenAfterTheFallbackServedABundle()
    {
        var primary = FakeBundler.Failing(BundleTechnique.SdkBundler, new PEPackerException("sdk exploded"));
        var fallback = FakeBundler.Succeeding(BundleTechnique.ManualBundler);
        var bundler = new FallbackBundler(primary, fallback);

        Assert.Equal(BundleTechnique.SdkBundler, bundler.Technique);

        var result = bundler.CreateSingleFileExecutable(Request());

        Assert.Equal(BundleTechnique.ManualBundler, result.Technique);
        Assert.Equal(BundleTechnique.SdkBundler, bundler.Technique);
    }

    /// <summary>
    /// The three-string convenience overload is the default interface method, so it must still
    /// reach the wrapper's request-based implementation after the duplicated copies were removed.
    /// </summary>
    [Fact]
    public void ConvenienceOverload_ReachesTheRequestImplementation()
    {
        var primary = FakeBundler.Succeeding(BundleTechnique.SdkBundler);
        IBundler bundler = new FallbackBundler(primary, FakeBundler.Succeeding(BundleTechnique.ManualBundler));

        bundler.CreateSingleFileExecutable("a.dll", "a.exe", "a");

        Assert.Equal(1, primary.Calls);
        Assert.Equal("a.dll", primary.LastRequest!.EntryAssemblyPath);
        Assert.Equal("a.exe", primary.LastRequest.OutputPath);
        Assert.Equal("a", primary.LastRequest.AssemblyName);
    }

    /// <summary>
    /// A bundler that records what it was asked and then does what it was told.
    /// </summary>
    private sealed class FakeBundler : IBundler
    {
        private readonly Exception? _failure;

        private FakeBundler(BundleTechnique technique, Exception? failure)
        {
            Technique = technique;
            _failure = failure;
        }

        internal static FakeBundler Succeeding(BundleTechnique technique) => new(technique, null);

        internal static FakeBundler Failing(BundleTechnique technique, Exception failure) =>
            new(technique, failure);

        public BundleTechnique Technique { get; }

        internal int Calls { get; private set; }

        internal BundleRequest? LastRequest { get; private set; }

        public BundleResult CreateSingleFileExecutable(BundleRequest request)
        {
            Calls++;
            LastRequest = request;

            return _failure is null
                ? new BundleResult(request.OutputPath, Technique)
                : throw _failure;
        }
    }
}
