using System.Collections.Generic;
using com.igg.hypercontent.runtime;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Info about a changed asset for update bundle grouping.
    /// </summary>
    public class ChangedAssetInfo
    {
        /// <summary>Asset GUID (32-char lowercase hex).</summary>
        public string guid;

        /// <summary>Current asset path in the project.</summary>
        public string assetPath;

        /// <summary>
        /// Source bundle name. For modified assets: from manifest (cachedInfo.bundleName).
        /// For new assets: from current BuildPlan (AssetToBundle[guid]).
        /// </summary>
        public string sourceBundleName;
    }

    /// <summary>
    /// Interface for custom update-bundle grouping strategies.
    /// Determines how dependency-expanded changed assets are partitioned into update bundles.
    /// Built-in strategies: GroupByOriginalBundle, SingleBundle, ReRunGrouping.
    /// </summary>
    public interface IUpdateBundleGroupingStrategy
    {
        /// <summary>
        /// Group the dependency-expanded changed assets into update bundles.
        /// Each asset in the input must appear in exactly one output bundle.
        /// Update bundle names must follow CONVENTIONS (max 128 chars, no path separators).
        /// </summary>
        /// <param name="pChangedAssetList">Dependency-expanded changed assets with guid, assetPath, sourceBundleName.</param>
        /// <param name="pVersion">Build version string for unique naming (e.g. timestamp).</param>
        /// <returns>Mapping from update bundle name to list of assets in that bundle.</returns>
        Dictionary<string, List<ChangedAssetInfo>> GroupChangedAssets(
            IReadOnlyList<ChangedAssetInfo> pChangedAssetList,
            string pVersion);

        /// <summary>
        /// Required when <see cref="UpdateBundleGroupingStrategyType.Custom"/> is used.
        /// Must supply one <see cref="BundleCompressionType"/> per key in <paramref name="pUpdateMapping"/>.
        /// </summary>
        /// <param name="pUpdateMapping">Result of <see cref="GroupChangedAssets"/> for this strategy.</param>
        /// <param name="pPlan">Current full build plan (for lookups).</param>
        /// <param name="pConfig">Build configuration.</param>
        /// <param name="pCompressionByUpdateBundleName">Update bundle name → compression for SBP.</param>
        /// <param name="pError">Human-readable error when validation fails.</param>
        /// <returns>True if <paramref name="pCompressionByUpdateBundleName"/> is complete and valid.</returns>
        bool TryGetUpdateBundleCompressionMap(
            IReadOnlyDictionary<string, List<ChangedAssetInfo>> pUpdateMapping,
            BuildPlan pPlan,
            BuildConfig pConfig,
            out IReadOnlyDictionary<string, BundleCompressionType> pCompressionByUpdateBundleName,
            out string pError);
    }

    /// <summary>
    /// Built-in update bundle grouping strategy identifiers.
    /// </summary>
    public enum UpdateBundleGroupingStrategyType
    {
        /// <summary>
        /// One update bundle per source bundle name.
        /// Name format: {sourceBundleName}_update_{version}
        /// </summary>
        GroupByOriginalBundle = 0,

        /// <summary>
        /// All changed assets go into one update bundle.
        /// Name format: content_update_{version}
        /// Catalog: <see cref="BundleTagFlags"/> is None or Blocking only; if any included entry contributes Blocking, the whole bundle record is Blocking.
        /// </summary>
        SingleBundle = 1,

        /// <summary>
        /// Re-run the same IBundleGroupingStrategy on the changed set,
        /// then suffix each resulting bundle name with _update_{version}.
        /// </summary>
        ReRunGrouping = 2,

        /// <summary>
        /// Use a user-provided IUpdateBundleGroupingStrategy implementation.
        /// </summary>
        Custom = 3
    }
}
