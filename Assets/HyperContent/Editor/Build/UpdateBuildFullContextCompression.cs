using System;
using System.Collections.Generic;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Builds per-bundle compression for Update full-context SBP build: baseline from <see cref="BuildPlan.BundleCompression"/>
    /// plus entries for update bundle names (not present in the baseline plan).
    /// Resolution follows <see cref="BuildConfig.updateBundleGroupingStrategy"/> (not Full Build <see cref="BuildConfig.groupingStrategy"/>).
    ///
    /// Bundle name convention (see CONVENTIONS.md): all keys here are <b>logical (extensionless)</b>
    /// bundle names, matching <see cref="BuildPlan.BundleToAssets"/> / <see cref="BuildPlan.BundleCompression"/>
    /// and <see cref="UpdateBundleAssigner"/> output. Callers must pass logical names — physical
    /// <c>.bundle</c>-suffixed forms from <see cref="AssetBundleBuild.assetBundleName"/> are rejected.
    /// </summary>
    internal static class UpdateBuildFullContextCompression
    {
        public static bool TryMerge(
            BuildPlan pPlan,
            BuildConfig pConfig,
            UpdateBundleGroupingStrategyType pUpdateStrategy,
            IReadOnlyDictionary<string, BundleCompressionType> pCustomCompressionMap,
            IEnumerable<string> pLogicalBundleNames,
            Dictionary<string, List<ChangedAssetInfo>> pUpdateMapping,
            out Dictionary<string, BundleCompressionType> pMerged,
            out string pError)
        {
            pMerged = new Dictionary<string, BundleCompressionType>(StringComparer.OrdinalIgnoreCase);
            pError = null;

            if (pPlan?.BundleCompression != null)
            {
                foreach (var kvp in pPlan.BundleCompression)
                    pMerged[kvp.Key] = kvp.Value;
            }

            foreach (var name in pLogicalBundleNames)
            {
                if (string.IsNullOrEmpty(name))
                    continue;

                // Hard convention: logical (extensionless) names only. Callers that hand us a
                // physical AssetBundleBuild.assetBundleName ('.bundle') are doing it wrong and
                // we want it to surface immediately, not silently re-normalize.
                if (name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                {
                    pError =
                        $"Full-context Update build: bundle name '{name}' has a physical '.bundle' suffix. " +
                        "Pass logical (extensionless) bundle names to UpdateBuildFullContextCompression.TryMerge.";
                    return false;
                }

                if (pMerged.ContainsKey(name))
                    continue;

                if (pUpdateMapping != null &&
                    pUpdateMapping.TryGetValue(name, out var changed) &&
                    changed != null &&
                    changed.Count > 0)
                {
                    if (!TryResolveNewUpdateBundleCompression(
                            pPlan,
                            pConfig,
                            pUpdateStrategy,
                            pCustomCompressionMap,
                            name,
                            changed,
                            out var compression,
                            out pError))
                    {
                        return false;
                    }

                    pMerged[name] = compression;
                    continue;
                }

                pError =
                    $"Full-context Update build: no compression entry for bundle '{name}'. " +
                    "It is not in BuildPlan.BundleCompression and is not listed as an update bundle.";
                return false;
            }

            return true;
        }

        static bool TryResolveNewUpdateBundleCompression(
            BuildPlan pPlan,
            BuildConfig pConfig,
            UpdateBundleGroupingStrategyType pUpdateStrategy,
            IReadOnlyDictionary<string, BundleCompressionType> pCustomCompressionMap,
            string pUpdateBundleName,
            List<ChangedAssetInfo> pChanged,
            out BundleCompressionType pCompression,
            out string pError)
        {
            pCompression = default;
            pError = null;

            switch (pUpdateStrategy)
            {
                case UpdateBundleGroupingStrategyType.GroupByOriginalBundle:
                case UpdateBundleGroupingStrategyType.ReRunGrouping:
                    return TryResolveFromSourceBundle(pPlan, pChanged, pUpdateBundleName, out pCompression, out pError);

                case UpdateBundleGroupingStrategyType.SingleBundle:
                    // Plan: single patch bundle uses project-wide compression from BuildConfig.
                    pCompression = pConfig.compressionType;
                    return true;

                case UpdateBundleGroupingStrategyType.Custom:
                    if (pCustomCompressionMap == null)
                    {
                        pError = "Custom update strategy requires a compression map from TryGetUpdateBundleCompressionMap.";
                        return false;
                    }

                    if (!pCustomCompressionMap.TryGetValue(pUpdateBundleName, out pCompression))
                    {
                        pError =
                            $"Custom update bundle '{pUpdateBundleName}' has no entry in the custom compression map. " +
                            "Implement TryGetUpdateBundleCompressionMap for every update bundle name.";
                        return false;
                    }

                    return true;

                default:
                    pError = $"Unknown UpdateBundleGroupingStrategyType: {pUpdateStrategy}";
                    return false;
            }
        }

        static bool TryResolveFromSourceBundle(
            BuildPlan pPlan,
            List<ChangedAssetInfo> pChanged,
            string pUpdateBundleName,
            out BundleCompressionType pCompression,
            out string pError)
        {
            pCompression = default;
            pError = null;

            if (pChanged == null || pChanged.Count == 0)
            {
                pError = $"No changed assets for update bundle '{pUpdateBundleName}'.";
                return false;
            }

            var src = pChanged[0].sourceBundleName;
            if (string.IsNullOrEmpty(src) || string.Equals(src, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                pError =
                    $"Update bundle '{pUpdateBundleName}' has invalid sourceBundleName (empty or 'unknown'). " +
                    "Cannot resolve compression from BuildPlan.BundleCompression.";
                return false;
            }

            for (int i = 1; i < pChanged.Count; i++)
            {
                var other = pChanged[i].sourceBundleName;
                if (!string.Equals(src, other, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning(
                        $"[HyperContent] Update bundle '{pUpdateBundleName}' mixes sourceBundleName '{src}' and '{other}'. " +
                        "Using first entry for compression.");
                }
            }

            if (pPlan?.BundleCompression == null || !pPlan.BundleCompression.TryGetValue(src, out pCompression))
            {
                pError =
                    $"BuildPlan.BundleCompression has no entry for source bundle '{src}' (update bundle '{pUpdateBundleName}').";
                return false;
            }

            return true;
        }
    }
}
