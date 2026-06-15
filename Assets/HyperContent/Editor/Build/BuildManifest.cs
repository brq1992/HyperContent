using System;
using System.Collections.Generic;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Snapshot of the original Full Build state. Written once at the first Full Build
    /// of a release line, then treated as immutable input for all later Update Builds.
    /// Counterpart of Addressables' addressables_content_state.bin.
    /// </summary>
    [Serializable]
    public class BuildManifest
    {
        /// <summary>Build version at Full Build time.</summary>
        public string buildVersion;

        /// <summary>UTC Unix timestamp at Full Build time.</summary>
        public long buildTimestamp;

        /// <summary>Per-asset snapshot for change detection.</summary>
        public List<CachedAssetState> cachedAssets = new List<CachedAssetState>();

        /// <summary>Per-bundle snapshot for hash comparison.</summary>
        public List<CachedBundleState> cachedBundles = new List<CachedBundleState>();
    }

    /// <summary>
    /// Single asset identity + hash for comparison.
    /// Path is always resolved from GUID via AssetDatabase.GUIDToAssetPath(guid).
    /// </summary>
    [Serializable]
    public class AssetState
    {
        /// <summary>Asset GUID (32-char lowercase hex).</summary>
        public string guid;

        /// <summary>AssetDatabase.GetAssetDependencyHash(path) for this asset (Hash128 string).</summary>
        public string hash;

        public bool Equals(AssetState pOther)
        {
            if (pOther == null) return false;
            return guid == pOther.guid && hash == pOther.hash;
        }
    }

    /// <summary>
    /// Per-asset snapshot at Full Build time. Comparison uses GUID only to resolve path and state.
    /// Reference: Addressables ContentUpdateScript.CachedAssetState.
    /// </summary>
    [Serializable]
    public class CachedAssetState
    {
        /// <summary>Asset GUID (32-char lowercase hex).</summary>
        public string guid;

        /// <summary>AssetDatabase.GetAssetDependencyHash(path) for this asset.</summary>
        public string hash;

        /// <summary>Bundle this asset was assigned to at Full Build time.</summary>
        public string bundleName;

        /// <summary>The InternalId (SBP addressableNames / LoadAsset key), typically the asset file name including extension.</summary>
        public string internalId;

        /// <summary>Dependency list for comparison. Each entry: { guid, hash }.</summary>
        public List<AssetState> dependencies = new List<AssetState>();

        /// <summary>
        /// Asset-level dependency bundle NAMES captured at Full Build time (ordered, owning bundle LAST),
        /// from <see cref="BuildContext.AssetDependencyBundles"/>. Lets Update Build restore the asset-level
        /// dependency list for UNCHANGED assets (whose SBP write-data is not regenerated this build) so the
        /// mixed catalog carries <c>AssetRecordEntry.dependencyBundles</c> for them too. May be empty for
        /// assets that had no asset-level data at Full Build time.
        /// </summary>
        public List<string> dependencyBundleNames = new List<string>();

        /// <summary>
        /// Equality check: asset guid/hash + all dependency guid/hash must match.
        /// Reference: Addressables ContentUpdateScript.HasAssetOrDependencyChanged.
        /// </summary>
        public bool Equals(CachedAssetState pOther)
        {
            if (pOther == null) return false;
            if (guid != pOther.guid || hash != pOther.hash) return false;
            if (dependencies.Count != pOther.dependencies.Count) return false;
            for (int i = 0; i < dependencies.Count; i++)
            {
                if (!dependencies[i].Equals(pOther.dependencies[i]))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Per-bundle snapshot at Full Build time.
    /// </summary>
    [Serializable]
    public class CachedBundleState
    {
        /// <summary>Bundle identifier.</summary>
        public string bundleName;

        /// <summary>SHA256 of bundle file content.</summary>
        public string bundleHash;

        /// <summary>Bundle file size in bytes.</summary>
        public long size;

        /// <summary>GUIDs of all assets in this bundle.</summary>
        public List<string> assetGuids = new List<string>();
    }
}
