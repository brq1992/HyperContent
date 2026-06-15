using System;
using System.Collections.Generic;
using System.Linq;
using com.igg.hypercontent.runtime;
using com.igg.hypercontent.shared;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Resolves the configured update bundle grouping strategy and assigns
    /// dependency-expanded changed assets into update bundles.
    /// </summary>
    public static class UpdateBundleAssigner
    {
        /// <summary>
        /// Assign changed assets to update bundles using the configured strategy.
        /// </summary>
        /// <param name="pChangedAssetList">Dependency-expanded changed asset list from Phase A.</param>
        /// <param name="pVersion">Build version string for unique naming.</param>
        /// <param name="pStrategyType">Strategy type from BuildConfig.</param>
        /// <param name="pCustomStrategy">Custom strategy instance (only used when type is Custom).</param>
        /// <param name="pGroupingStrategy">Full Build grouping strategy (only used when type is ReRunGrouping).</param>
        /// <returns>Mapping from update bundle name to list of assets.</returns>
        public static Dictionary<string, List<ChangedAssetInfo>> AssignUpdateBundles(
            IReadOnlyList<ChangedAssetInfo> pChangedAssetList,
            string pVersion,
            UpdateBundleGroupingStrategyType pStrategyType,
            IUpdateBundleGroupingStrategy pCustomStrategy = null,
            IBundleGroupingStrategy pGroupingStrategy = null)
        {
            if (pChangedAssetList == null || pChangedAssetList.Count == 0)
                return new Dictionary<string, List<ChangedAssetInfo>>();

            Dictionary<string, List<ChangedAssetInfo>> mapping;

            switch (pStrategyType)
            {
                case UpdateBundleGroupingStrategyType.GroupByOriginalBundle:
                    mapping = GroupByOriginalBundle(pChangedAssetList, pVersion);
                    break;

                case UpdateBundleGroupingStrategyType.SingleBundle:
                    mapping = GroupAsSingleBundle(pChangedAssetList, pVersion);
                    break;

                case UpdateBundleGroupingStrategyType.ReRunGrouping:
                    mapping = ReRunGrouping(pChangedAssetList, pVersion);
                    break;

                case UpdateBundleGroupingStrategyType.Custom:
                    if (pCustomStrategy == null)
                    {
                        Debug.LogError("[HyperContent] Custom update strategy is null, falling back to GroupByOriginalBundle");
                        mapping = GroupByOriginalBundle(pChangedAssetList, pVersion);
                    }
                    else
                    {
                        mapping = pCustomStrategy.GroupChangedAssets(pChangedAssetList, pVersion);
                    }
                    break;

                default:
                    mapping = GroupByOriginalBundle(pChangedAssetList, pVersion);
                    break;
            }

            ValidateMapping(mapping, pChangedAssetList);
            return mapping;
        }
        /// <summary>
        /// Same partitioning as built-in <see cref="UpdateBundleGroupingStrategyType.GroupByOriginalBundle"/>; use from custom <see cref="IUpdateBundleGroupingStrategy"/> implementations.
        /// </summary>
        public static Dictionary<string, List<ChangedAssetInfo>> GroupChangedAssetsByOriginalBundle(
            IReadOnlyList<ChangedAssetInfo> pChangedAssetList,
            string pVersion)
        {
            return GroupByOriginalBundle(pChangedAssetList, pVersion);
        }

        /// <summary>
        /// HyperContent update bundle name: <c>{sourceBundleName}_update_{version}</c> (sanitized).
        /// </summary>
        public static string FormatHyperContentUpdateBundleName(string pSourceBundleName, string pVersion)
        {
            return MakeUpdateBundleName(pSourceBundleName, pVersion);
        }


        /// <summary>
        /// GroupByOriginalBundle: one update bundle per source bundle name.
        /// Name format: {sourceBundleName}_update_{version}
        /// </summary>
        private static Dictionary<string, List<ChangedAssetInfo>> GroupByOriginalBundle(
            IReadOnlyList<ChangedAssetInfo> pChangedAssetList,
            string pVersion)
        {
            var groupDict = new Dictionary<string, List<ChangedAssetInfo>>();

            foreach (var asset in pChangedAssetList)
            {
                var sourceName = string.IsNullOrEmpty(asset.sourceBundleName)
                    ? "unknown"
                    : asset.sourceBundleName;
                var updateBundleName = MakeUpdateBundleName(sourceName, pVersion);

                if (!groupDict.TryGetValue(updateBundleName, out var assetList))
                {
                    assetList = new List<ChangedAssetInfo>();
                    groupDict[updateBundleName] = assetList;
                }
                assetList.Add(asset);
            }

            return groupDict;
        }

        /// <summary>
        /// SingleBundle: all changed assets go into one update bundle.
        /// Name format: content_update_{version}
        /// Catalog policy: this bundle merges entries from multiple source bundles/groups. Tags are only
        /// <see cref="BundleTagFlags.None"/> vs <see cref="BundleTagFlags.Blocking"/>. If any included entry
        /// contributes Blocking (marker or future group table), the entire <c>content_update_*</c> bundle record is Blocking.
        /// </summary>
        private static Dictionary<string, List<ChangedAssetInfo>> GroupAsSingleBundle(
            IReadOnlyList<ChangedAssetInfo> pChangedAssetList,
            string pVersion)
        {
            var updateBundleName = $"content_update_{pVersion}";
            if (updateBundleName.Length > NamingRules.MAX_BUNDLE_NAME_LENGTH)
                updateBundleName = updateBundleName.Substring(0, NamingRules.MAX_BUNDLE_NAME_LENGTH);

            return new Dictionary<string, List<ChangedAssetInfo>>
            {
                { updateBundleName, new List<ChangedAssetInfo>(pChangedAssetList) }
            };
        }

        /// <summary>
        /// ReRunGrouping: re-run the same IBundleGroupingStrategy on the changed set,
        /// then suffix each resulting bundle name with _update_{version}.
        /// Falls back to GroupByOriginalBundle when no strategy is available.
        /// </summary>
        private static Dictionary<string, List<ChangedAssetInfo>> ReRunGrouping(
            IReadOnlyList<ChangedAssetInfo> pChangedAssetList,
            string pVersion)
        {
            // ReRunGrouping uses sourceBundleName as a proxy for the grouping result
            // since we cannot easily re-run the full grouping strategy on a subset.
            // This produces the same result as GroupByOriginalBundle in most cases.
            Debug.LogWarning("[HyperContent] ReRunGrouping falls back to GroupByOriginalBundle. " +
                "For custom grouping, use IUpdateBundleGroupingStrategy.Custom.");
            return GroupByOriginalBundle(pChangedAssetList, pVersion);
        }

        private static string MakeUpdateBundleName(string pSourceBundleName, string pVersion)
        {
            var name = $"{pSourceBundleName}_update_{pVersion}";
            // Remove path separators per CONVENTIONS
            name = name.Replace("/", "_").Replace("\\", "_");
            if (name.Length > NamingRules.MAX_BUNDLE_NAME_LENGTH)
                name = name.Substring(0, NamingRules.MAX_BUNDLE_NAME_LENGTH);
            return name;
        }

        private static void ValidateMapping(
            Dictionary<string, List<ChangedAssetInfo>> pMapping,
            IReadOnlyList<ChangedAssetInfo> pOriginalList)
        {
            var totalAssigned = pMapping.Values.Sum(v => v.Count);
            if (totalAssigned != pOriginalList.Count)
            {
                Debug.LogWarning($"[HyperContent] Update bundle assignment mismatch: " +
                    $"expected {pOriginalList.Count} assets, assigned {totalAssigned}");
            }

            foreach (var kvp in pMapping)
            {
                if (kvp.Key.Length > NamingRules.MAX_BUNDLE_NAME_LENGTH)
                {
                    Debug.LogWarning($"[HyperContent] Update bundle name exceeds max length: " +
                        $"'{kvp.Key}' ({kvp.Key.Length} > {NamingRules.MAX_BUNDLE_NAME_LENGTH})");
                }
            }
        }
    }
}
