namespace PEPacker.Bundling;

/// <summary>
/// A bundler that tries a primary bundler first, then falls back to a secondary bundler if the
/// primary fails.
/// </summary>
public class FallbackBundler : IBundler
{
    private readonly IBundler _primary;
    private readonly IBundler _fallback;

    /// <summary>
    /// Creates a new fallback bundler.
    /// </summary>
    /// <param name="primary">The primary bundler to try first.</param>
    /// <param name="fallback">The fallback bundler to use if primary fails.</param>
    public FallbackBundler(IBundler primary, IBundler fallback)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);

        _primary = primary;
        _fallback = fallback;
    }

    /// <summary>
    /// The primary bundler's technique — the one a bundle will be attempted with.
    /// </summary>
    /// <remarks>
    /// It is deliberately not "whichever was used last". A single instance is shared (see
    /// <see cref="BundlerFactory"/>) and may be used concurrently, so a last-used field made this
    /// property report whichever unrelated call happened to finish most recently. The technique
    /// a particular bundle actually used is on that bundle's
    /// <see cref="BundleResult.Technique"/>, which cannot be raced.
    /// </remarks>
    public BundleTechnique Technique => _primary.Technique;

    /// <inheritdoc/>
    public BundleResult CreateSingleFileExecutable(BundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return _primary.CreateSingleFileExecutable(request);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a bundler failure. Retrying the fallback with an
            // already-cancelled token could only cancel again, and reporting it as
            // "both bundlers failed" hid the cancellation from the caller entirely.
            throw;
        }
        catch (Exception primaryFailure)
        {
            // Primary failed, try fallback. If the fallback also fails, the caller needs both
            // reasons: "the SDK bundler could not load" and "the built-in bundler cannot target
            // macOS" are different problems and reporting only the second hides the first.
            try
            {
                return _fallback.CreateSingleFileExecutable(request);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception fallbackFailure)
            {
                throw new PEPackerException(
                    $"Both bundlers failed. {_primary.Technique}: {primaryFailure.Message} " +
                    $"{_fallback.Technique}: {fallbackFailure.Message}",
                    new AggregateException(primaryFailure, fallbackFailure));
            }
        }
    }
}
