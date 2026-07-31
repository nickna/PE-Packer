namespace PEPacker.Bundling;

/// <summary>
/// Factory for creating the appropriate bundler based on SDK availability.
/// Uses SDK bundler when available, falls back to manual bundler otherwise.
/// </summary>
public static class BundlerFactory
{
    private static readonly Lazy<IBundler> _cachedBundler = new(CreateBundler, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets a bundler instance, preferring the SDK bundler when available.
    /// The bundler instance is cached for reuse.
    /// </summary>
    /// <remarks>
    /// Every automatic selection shares this instance, including
    /// <see cref="GetBundler(BundlerMode)"/> with <see cref="BundlerMode.Auto"/>, which used to
    /// construct a fresh one per call and made the documented caching apply only to callers who
    /// happened to use the parameterless overload. The implementations hold no per-bundle state,
    /// so sharing one is safe across threads; <see cref="CreateBundler"/> remains for a caller
    /// that wants an unshared instance anyway.
    /// </remarks>
    /// <returns>An IBundler implementation.</returns>
    public static IBundler GetBundler() => _cachedBundler.Value;

    /// <summary>
    /// Creates a new bundler without caching.
    /// Useful for testing or when you need a fresh instance.
    /// </summary>
    /// <returns>An IBundler implementation.</returns>
    public static IBundler CreateBundler()
    {
        // No try/catch around the SDK path: SdkBundler's constructor throws only when detection
        // reports unavailable, and detection is a cached result that cannot disagree with the
        // check just made. The old catch could not run, and being unreachable it also swallowed
        // anything genuinely unexpected.
        return SdkBundlerDetector.IsSdkAvailable
            ? new FallbackBundler(new SdkBundler(), new ManualBundler())
            : new ManualBundler();
    }

    /// <summary>
    /// Gets a specific bundler type, ignoring the automatic selection.
    /// </summary>
    /// <param name="technique">The desired bundling technique.</param>
    /// <returns>An IBundler for the specified technique.</returns>
    /// <exception cref="PEPackerException">
    /// The SDK bundler was requested and is unavailable. The message distinguishes "disabled by
    /// feature switch", "no SDK found" and "found but unloadable, as under Native AOT".
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="technique"/> is not a defined <see cref="BundleTechnique"/>.
    /// </exception>
    public static IBundler GetBundler(BundleTechnique technique)
    {
        return technique switch
        {
            BundleTechnique.SdkBundler => CreateSdkBundler(),
            BundleTechnique.ManualBundler => new ManualBundler(),
            _ => throw new ArgumentOutOfRangeException(nameof(technique))
        };
    }

    /// <summary>
    /// Gets a bundler based on the specified mode.
    /// </summary>
    /// <param name="mode">The bundler selection mode.</param>
    /// <returns>An IBundler for the specified mode.</returns>
    /// <exception cref="PEPackerException">Thrown if SDK bundler is requested but not available.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="mode"/> is not a defined <see cref="BundlerMode"/>.
    /// </exception>
    public static IBundler GetBundler(BundlerMode mode)
    {
        return mode switch
        {
            BundlerMode.Sdk => CreateSdkBundler(),
            BundlerMode.BuiltIn => new ManualBundler(),
            BundlerMode.Auto => GetBundler(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    /// <summary>
    /// Gets information about which bundler would be selected.
    /// </summary>
    /// <returns>The technique that would be used by GetBundler().</returns>
    public static BundleTechnique GetPreferredTechnique()
    {
        return SdkBundlerDetector.IsSdkAvailable
            ? BundleTechnique.SdkBundler
            : BundleTechnique.ManualBundler;
    }

    /// <summary>
    /// Keeps every factory path to <see cref="SdkBundler"/> behind the feature-switched
    /// availability check so Native AOT can remove the implementation.
    /// </summary>
    private static IBundler CreateSdkBundler()
    {
        if (!SdkBundlerDetector.IsSdkAvailable)
        {
            throw SdkBundlerDetector.CreateUnavailableException();
        }

        return new SdkBundler();
    }
}
