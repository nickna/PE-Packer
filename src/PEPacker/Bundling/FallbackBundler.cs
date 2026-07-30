namespace PEPacker.Bundling;

/// <summary>
/// A bundler that tries a primary bundler first, then falls back to a secondary bundler if the primary fails.
/// </summary>
public class FallbackBundler : IBundler
{
    private readonly IBundler _primary;
    private readonly IBundler _fallback;
    private BundleTechnique? _lastUsedTechnique;

    /// <summary>
    /// Creates a new fallback bundler.
    /// </summary>
    /// <param name="primary">The primary bundler to try first.</param>
    /// <param name="fallback">The fallback bundler to use if primary fails.</param>
    public FallbackBundler(IBundler primary, IBundler fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    /// <inheritdoc/>
    public BundleTechnique Technique => _lastUsedTechnique ?? _primary.Technique;

    /// <inheritdoc/>
    public BundleResult CreateSingleFileExecutable(BundleRequest request)
    {
        try
        {
            var result = _primary.CreateSingleFileExecutable(request);
            _lastUsedTechnique = result.Technique;
            return result;
        }
        catch (Exception primaryFailure)
        {
            // Primary failed, try fallback. If the fallback also fails, the caller needs both
            // reasons: "the SDK bundler could not load" and "the built-in bundler cannot target
            // macOS" are different problems and reporting only the second hides the first.
            try
            {
                var result = _fallback.CreateSingleFileExecutable(request);
                _lastUsedTechnique = result.Technique;
                return result;
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

    /// <inheritdoc/>
    public BundleResult CreateSingleFileExecutable(string dllPath, string exePath, string assemblyName) =>
        CreateSingleFileExecutable(new BundleRequest
        {
            EntryAssemblyPath = dllPath,
            OutputPath = exePath,
            AssemblyName = assemblyName
        });
}
