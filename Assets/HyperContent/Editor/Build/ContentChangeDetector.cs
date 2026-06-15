using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Result of modified asset detection (entry-level only).
    /// When modified due to dependency changes, <see cref="causingDependencyGuids"/> lists the deps that changed (expandable in UI).
    /// When empty, the entry itself changed and there is nothing to expand.
    /// </summary>
    public class ModifiedAssetInfo
    {
        public string guid;
        public string oldBundleName;
        public string assetPath;
        /// <summary>Dependency GUIDs that caused this entry to be marked modified. Empty if the entry itself changed (no expand).</summary>
        public List<string> causingDependencyGuids = new List<string>();
    }

    /// <summary>
    /// Result of new asset detection.
    /// </summary>
    public class NewAssetInfo
    {
        public string guid;
        public string assetPath;
    }

    /// <summary>
    /// Result of removed asset detection (reporting only, not used for packaging).
    /// </summary>
    public class RemovedAssetInfo
    {
        public string guid;
    }

    /// <summary>
    /// Summary of change detection results.
    /// </summary>
    public class ChangeDetectionResult
    {
        public List<ModifiedAssetInfo> modifiedAssetList = new List<ModifiedAssetInfo>();
        public List<NewAssetInfo> newAssetList = new List<NewAssetInfo>();
        public List<RemovedAssetInfo> removedAssetList = new List<RemovedAssetInfo>();
        public List<ChangedAssetInfo> expandedChangedAssetList = new List<ChangedAssetInfo>();
        public bool HasChanges => modifiedAssetList.Count > 0 || newAssetList.Count > 0;
    }

    /// <summary>
    /// Compares current project assets against the immutable Full Build manifest
    /// to detect modified, new, and removed assets. Implements Phase A of
    /// CONTENT_UPDATE_BUILD_FLOW.md including dependency expansion.
    /// </summary>
    public static class ContentChangeDetector
    {
        /// <summary>
        /// Run full Phase A: load manifest, get current assets, detect changes,
        /// expand via dependency rules, detect removed.
        /// </summary>
        public static ChangeDetectionResult DetectChanges(
            BuildConfig pConfig,
            BuildPlan pCurrentPlan,
            List<BuildError> pErrorList)
        {
            var result = new ChangeDetectionResult();

            // A1. Load Build Manifest
            var manifest = BuildManifestManager.Load(pConfig, pErrorList);
            if (manifest == null)
                return null;

            // Build manifest lookup: GUID → CachedAssetState
            var manifestGuidToAsset = new Dictionary<string, CachedAssetState>(StringComparer.OrdinalIgnoreCase);
            foreach (var cachedAsset in manifest.cachedAssets)
            {
                manifestGuidToAsset[cachedAsset.guid] = cachedAsset;
            }

            // A2. Current asset set: entry-level only (KeyToGuid = addressable entries)
            var entryGuids = new HashSet<string>(pCurrentPlan.KeyToGuid.Values, StringComparer.OrdinalIgnoreCase);

            // A3. Compare entries vs Manifest (GUID-based); only entries appear in modified/new
            foreach (var guid in entryGuids)
            {
                var guidLower = guid.ToLowerInvariant();

                if (!manifestGuidToAsset.TryGetValue(guidLower, out var cachedInfo))
                {
                    // New entry: in current plan but not in manifest
                    var assetPath = GetAssetPathFromGuid(guidLower);
                    result.newAssetList.Add(new NewAssetInfo
                    {
                        guid = guidLower,
                        assetPath = assetPath ?? ""
                    });
                    continue;
                }

                // Existing entry: check if changed
                if (HasAssetOrDependencyChanged(cachedInfo))
                {
                    var assetPath = GetAssetPathFromGuid(guidLower);
                    var causingDeps = GetCausingDependencyGuids(cachedInfo);
                    result.modifiedAssetList.Add(new ModifiedAssetInfo
                    {
                        guid = guidLower,
                        oldBundleName = cachedInfo.bundleName,
                        assetPath = assetPath ?? "",
                        causingDependencyGuids = causingDeps
                    });
                }
            }

            // A3b. Dependency Expansion (entry-level only: only add entries to expanded set)
            var explicitChangedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in result.modifiedAssetList)
                explicitChangedGuids.Add(m.guid);
            foreach (var n in result.newAssetList)
                explicitChangedGuids.Add(n.guid);

            var expandedGuids = ExpandChangedSet(explicitChangedGuids, pCurrentPlan, entryGuids, manifestGuidToAsset);

            // Build the unified expanded changed asset list
            foreach (var guid in expandedGuids)
            {
                var guidLower = guid.ToLowerInvariant();
                var assetPath = GetAssetPathFromGuid(guidLower);
                string sourceBundleName;

                if (manifestGuidToAsset.TryGetValue(guidLower, out var cached))
                    sourceBundleName = cached.bundleName;
                else if (pCurrentPlan.AssetToBundle.TryGetValue(guid, out var planBundle))
                    sourceBundleName = planBundle;
                else
                    sourceBundleName = "";

                result.expandedChangedAssetList.Add(new ChangedAssetInfo
                {
                    guid = guidLower,
                    assetPath = assetPath ?? "",
                    sourceBundleName = sourceBundleName
                });
            }

            // A4. Detect Removed Assets (reporting only); compare against full plan (entry + deps)
            var fullPlanGuids = new HashSet<string>(pCurrentPlan.AssetToBundle.Keys, StringComparer.OrdinalIgnoreCase);
            var manifestGuids = new HashSet<string>(manifestGuidToAsset.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var manifestGuid in manifestGuids)
            {
                if (!fullPlanGuids.Contains(manifestGuid))
                {
                    result.removedAssetList.Add(new RemovedAssetInfo { guid = manifestGuid });
                }
            }

            // A5. Summary
            Debug.Log($"[HyperContent] Change Detection Summary: " +
                $"Modified={result.modifiedAssetList.Count}, " +
                $"New={result.newAssetList.Count}, " +
                $"Expanded shipping set={result.expandedChangedAssetList.Count}, " +
                $"Removed={result.removedAssetList.Count}");

            return result;
        }

        /// <summary>
        /// Returns dependency GUIDs that caused this entry to be modified (hash changed).
        /// Empty if the entry itself changed (no expand in UI). Used for expand-to-show causing deps only.
        /// </summary>
        private static List<string> GetCausingDependencyGuids(CachedAssetState pCachedInfo)
        {
            var causing = new List<string>();
            var currentAssetState = GetCurrentAssetState(pCachedInfo.guid);
            if (currentAssetState == null)
                return causing;

            var cachedAssetState = new AssetState { guid = pCachedInfo.guid, hash = pCachedInfo.hash };
            if (!cachedAssetState.Equals(currentAssetState))
                return causing; // Entry itself changed; no causing-deps to show

            if (pCachedInfo.dependencies == null)
                return causing;

            foreach (var cachedDep in pCachedInfo.dependencies)
            {
                var currentDepState = GetCurrentAssetState(cachedDep.guid);
                if (currentDepState == null || !cachedDep.Equals(currentDepState))
                    causing.Add(cachedDep.guid.ToLowerInvariant());
            }
            return causing;
        }

        /// <summary>
        /// Check if an asset or any of its dependencies changed relative to the manifest.
        /// Reference: Addressables ContentUpdateScript.HasAssetOrDependencyChanged.
        /// All resolution by GUID.
        /// </summary>
        private static bool HasAssetOrDependencyChanged(CachedAssetState pCachedInfo)
        {
            // Get current asset state
            var currentAssetState = GetCurrentAssetState(pCachedInfo.guid);
            if (currentAssetState == null)
                return true; // Asset deleted or path resolution failed

            // Check asset itself
            var cachedAssetState = new AssetState { guid = pCachedInfo.guid, hash = pCachedInfo.hash };
            if (!cachedAssetState.Equals(currentAssetState))
                return true;

            // Check each dependency
            if (pCachedInfo.dependencies == null)
                return false;

            foreach (var cachedDep in pCachedInfo.dependencies)
            {
                var currentDepState = GetCurrentAssetState(cachedDep.guid);
                if (currentDepState == null)
                    return true; // Dependency deleted
                if (!cachedDep.Equals(currentDepState))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Get current state for an asset by GUID.
        /// Returns null if GUID cannot be resolved to a valid path.
        /// </summary>
        private static AssetState GetCurrentAssetState(string pGuid)
        {
            var path = AssetDatabase.GUIDToAssetPath(pGuid);
            if (string.IsNullOrEmpty(path))
                return null;

            var hash = AssetDatabase.GetAssetDependencyHash(path);
            if (!hash.isValid)
                return null;

            return new AssetState
            {
                guid = pGuid.ToLowerInvariant(),
                hash = hash.ToString()
            };
        }

        /// <summary>
        /// Expand the changed set via dependency rules (Phase A3b); only entries are added (entry-level only).
        /// Loop 1 (downward / GetStaticContentDependenciesForEntries): add entry dependencies that must follow,
        ///   filtered by <see cref="ShouldSkipStaticDependencyEntry"/> — aligned with Addressables
        ///   <c>GetGroupGuidsWithUnchangedBundleName</c>.
        /// Loop 2 (upward / GetEntriesDependentOnModifiedEntries): add entries that depend on changed assets
        ///   (no filter — asset correctness requirement, same as Addressables).
        /// Uses fixed-point iteration (while loop) for transitive closure completeness,
        /// intentionally more conservative than Addressables' single-pass execution.
        /// </summary>
        private static HashSet<string> ExpandChangedSet(
            HashSet<string> pExplicitChangedGuids,
            BuildPlan pPlan,
            HashSet<string> pEntryGuids,
            Dictionary<string, CachedAssetState> pManifestByGuid)
        {
            // Pre-build entry-only bundle reverse map (BundleToAssets includes non-entry deps, so we can't use it).
            var bundleToEntryGuids = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entryGuid in pEntryGuids)
            {
                if (!pPlan.AssetToBundle.TryGetValue(entryGuid, out var bn))
                    continue;
                if (!bundleToEntryGuids.TryGetValue(bn, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bundleToEntryGuids[bn] = set;
                }
                set.Add(entryGuid);
            }

            var expanded = new HashSet<string>(pExplicitChangedGuids, StringComparer.OrdinalIgnoreCase);
            bool changed = true;

            while (changed)
            {
                changed = false;
                var toAdd = new List<string>();

                // Loop 1 — Downward (GetStaticContentDependenciesForEntries):
                // add dependencies that are also entries, unless their bundle is unchanged.
                foreach (var guid in expanded)
                {
                    if (!pPlan.Dependencies.TryGetValue(guid, out var deps))
                        continue;
                    foreach (var depGuid in deps)
                    {
                        if (expanded.Contains(depGuid)) continue;
                        if (!pPlan.AssetToBundle.ContainsKey(depGuid)) continue;
                        if (!pEntryGuids.Contains(depGuid)) continue;

                        if (ShouldSkipStaticDependencyEntry(
                                depGuid, pPlan, pExplicitChangedGuids,
                                bundleToEntryGuids, pManifestByGuid))
                            continue;

                        toAdd.Add(depGuid);
                    }
                }

                // Loop 2 — Upward (GetEntriesDependentOnModifiedEntries):
                // add entries that depend on changed assets — NO filter (asset correctness).
                foreach (var kvp in pPlan.Dependencies)
                {
                    var assetGuid = kvp.Key;
                    if (expanded.Contains(assetGuid)) continue;
                    if (!pEntryGuids.Contains(assetGuid)) continue;
                    var deps = kvp.Value;
                    foreach (var depGuid in deps)
                    {
                        if (expanded.Contains(depGuid))
                        {
                            toAdd.Add(assetGuid);
                            break;
                        }
                    }
                }

                foreach (var g in toAdd)
                {
                    if (expanded.Add(g))
                        changed = true;
                }
            }

            return expanded;
        }

        /// <summary>
        /// Determine whether a dependency entry can be skipped from the expanded shipping set.
        /// Aligned with Addressables <c>GetGroupGuidsWithUnchangedBundleName</c>:
        /// if the dep entry's bundle name is predicted to be unchanged after excluding
        /// explicitly modified entries, the dep does not need to move to an update bundle.
        /// <para>
        /// Phase 1 (current): <c>PredictedBundleName = current AssetToBundle[depGuid]</c>.
        /// This is safe for all HC grouping strategies whose bundle names do not depend on
        /// the entry set composition (MarkerBased, AddressableGroupingStrategy,
        /// HyperContentAddressableGroupingTool with PackTogether/PackSeparately/PackTogetherByLabel).
        /// </para>
        /// </summary>
        /// <returns><c>true</c> to skip (bundle unchanged); <c>false</c> to keep adding.</returns>
        private static bool ShouldSkipStaticDependencyEntry(
            string pDepEntryGuid,
            BuildPlan pPlan,
            HashSet<string> pExplicitChangedGuids,
            Dictionary<string, HashSet<string>> pBundleToEntryGuids,
            Dictionary<string, CachedAssetState> pManifestByGuid)
        {
            // Step 1: current bundle name for this dep entry.
            if (!pPlan.AssetToBundle.TryGetValue(pDepEntryGuid, out var bn))
                return false;

            // Step 8 (conservative): dep not in manifest → don't skip (likely New, already explicit).
            if (!pManifestByGuid.TryGetValue(pDepEntryGuid, out var cachedState))
                return false;

            // Step 8 (conservative): bundle assignment changed since Full Build → don't skip.
            if (!string.Equals(bn, cachedState.bundleName, StringComparison.OrdinalIgnoreCase))
                return false;

            // Step 2: entries sharing the same bundle (entry-only reverse map).
            if (!pBundleToEntryGuids.TryGetValue(bn, out var entriesInBundle))
                return false;

            // Step 3–4: remainder = entriesInBundle minus explicit modifications.
            bool hasRemainder = false;
            foreach (var e in entriesInBundle)
            {
                if (!pExplicitChangedGuids.Contains(e))
                {
                    hasRemainder = true;
                    break;
                }
            }

            if (!hasRemainder)
                return false;

            // Step 5: redundant safety net — depEntryGuid should be in remainder.
            Debug.Assert(
                !pExplicitChangedGuids.Contains(pDepEntryGuid),
                $"[HyperContent] ShouldSkipStaticDependencyEntry: depEntryGuid {pDepEntryGuid} " +
                "is in explicitChangedGuids — should have been caught by expanded.Contains() earlier.");

            // Step 6–7: PredictedBundleName (Phase 1) = bn (unchanged for all current HC strategies).
            // Already verified bn == cachedState.bundleName above → skip.
            Debug.Log($"[HyperContent] A3b skip: dep entry {pDepEntryGuid} stays in unchanged bundle '{bn}'");
            return true;
        }

        private static string GetAssetPathFromGuid(string pGuid)
        {
            return AssetDatabase.GUIDToAssetPath(pGuid);
        }
    }

    /// <summary>
    /// Phase 2 extension point: simulate the bundle name that a grouping strategy would produce
    /// for a given set of remaining entries, aligned with Addressables
    /// <c>PrepGroupBundlePacking</c> + <c>HandleBundleNames</c>.
    /// <para>
    /// Phase 1 uses <c>AssetToBundle[depGuid]</c> directly (safe when bundle names don't
    /// depend on entry set composition). Implement this interface when bit-exact parity
    /// with Addressables <c>GetGroupGuidsWithUnchangedBundleName</c> is required —
    /// e.g. when <c>BundleInternalIdMode = GroupGuidProjectIdEntriesHash</c>.
    /// </para>
    /// </summary>
    public interface IBundleNameSimulator
    {
        /// <summary>
        /// Predict the bundle name for <paramref name="pBundleName"/> after removing
        /// <paramref name="pExcludedEntryGuids"/> from the entry set.
        /// </summary>
        /// <param name="pBundleName">Original (Full Build) logical bundle name.</param>
        /// <param name="pRemainingEntryGuids">Entry GUIDs that remain after excluding explicit modifications.</param>
        /// <param name="pExcludedEntryGuids">Entry GUIDs excluded (A3 explicit Modified/New).</param>
        /// <returns>Predicted bundle name after re-simulation.</returns>
        string PredictBundleName(
            string pBundleName,
            IReadOnlyCollection<string> pRemainingEntryGuids,
            IReadOnlyCollection<string> pExcludedEntryGuids);
    }
}
