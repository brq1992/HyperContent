using System;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Default <see cref="IPlayAssetPackRouter"/>: routes every locally-shipped bundle into a single
    /// configured asset pack. Pack name comes from <see cref="RuntimeSettings.playAssetDeliveryPackName"/>
    /// (typically "Bundles", matching <c>AABBuilder</c>'s
    /// <see cref="Google.Android.AppBundle.Editor.AssetPacks.AssetPackConfig.AddAssetsFolder"/> call).
    ///
    /// Asset name = <c>bundleInternalId + ".bundle"</c>, matching the flat layout produced by
    /// <c>AABBuilder.MoveAssetbundle</c> (top-level files only, no platform subfolder).
    /// </summary>
    public sealed class DefaultPlayAssetPackRouter : IPlayAssetPackRouter
    {
        private readonly string _packName;

        public DefaultPlayAssetPackRouter(string pPackName)
        {
            _packName = pPackName;
        }

        public string ResolvePackName(string pBundleInternalId)
        {
            return _packName;
        }

        public string ResolveAssetName(string pBundleInternalId)
        {
            if (string.IsNullOrEmpty(pBundleInternalId))
                return pBundleInternalId;
            return pBundleInternalId.EndsWith(NamingRules.BUNDLE_FILE_EXTENSION, StringComparison.Ordinal)
                ? pBundleInternalId
                : pBundleInternalId + NamingRules.BUNDLE_FILE_EXTENSION;
        }
    }
}
