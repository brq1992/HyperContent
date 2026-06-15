namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Maps a HyperContent bundle (catalog <c>internalId</c>) to the Google Play Asset Delivery
    /// asset pack that physically contains it.
    ///
    /// v1 default: every locally-shipped bundle resolves to one global pack (whose name comes from
    /// <see cref="RuntimeSettings.playAssetDeliveryPackName"/>). Implement a custom router only when
    /// you need to spread bundles across multiple packs (e.g. install-time vs fast-follow vs on-demand
    /// delivery). The router is queried once per bundle load, so impls should be O(1) / hashtable-backed.
    /// </summary>
    public interface IPlayAssetPackRouter
    {
        /// <summary>
        /// Resolve the asset pack name for the given bundle. Return null/empty to indicate the bundle
        /// is NOT in any PAD pack (the provider will skip PAD and go straight to the file-based fallback).
        /// </summary>
        /// <param name="pBundleInternalId">Catalog internalId of the bundle (extensionless, e.g. "videofiles").</param>
        string ResolvePackName(string pBundleInternalId);

        /// <summary>
        /// Resolve the file name as it sits inside the asset pack (relative to the pack's <c>assets/</c> root).
        /// Default impl appends ".bundle" to the bundleName, matching how AABBuilder flat-packs files via
        /// <see cref="Google.Android.AppBundle.Editor.AssetPacks.AssetPackConfig.AddAssetsFolder"/>. Override
        /// when your AAB build keeps subfolder structure inside the pack (e.g. "Android/foo.bundle").
        /// </summary>
        string ResolveAssetName(string pBundleInternalId);
    }
}
