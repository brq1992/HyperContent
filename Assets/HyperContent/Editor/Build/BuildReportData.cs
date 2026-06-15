using System;
using System.Collections.Generic;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Persistent build report data written to BuildReports/build_report.json after each build.
    /// Contains SBP-derived bundle dependencies (matching runtime catalog), bundle sizes, and per-asset paths
    /// (when available, all assets SBP packed into the bundle, not only HyperContent root entries).
    /// Used by <see cref="HyperContentBundleReportWindow"/> for visualization.
    /// </summary>
    [Serializable]
    public class BuildReportData
    {
        public string buildVersion;
        public long buildTimestamp;
        public long buildDurationMs;
        public List<BuildReportBundleEntry> bundles = new List<BuildReportBundleEntry>();

        /// <summary>
        /// Asset-level vs bundle-level dependency diff: how many dependency bundles asset-level loading
        /// avoids relative to the legacy bundle-level closure. Summary + per-asset entries (assets that
        /// save at least one bundle). Helps quantify the win and spot assets that save nothing.
        /// </summary>
        public AssetLevelDiffReport assetLevelDiff = new AssetLevelDiffReport();
    }

    /// <summary>
    /// Aggregate + per-asset asset-level vs bundle-level dependency comparison.
    /// </summary>
    [Serializable]
    public class AssetLevelDiffReport
    {
        /// <summary>Assets that had asset-level dependency data this build.</summary>
        public int assetsAnalyzed;
        /// <summary>Assets that load strictly fewer bundles under asset-level than bundle-level.</summary>
        public int assetsWithSavings;
        /// <summary>Sum over all analyzed assets of bundles loaded under asset-level (incl. owning).</summary>
        public long totalAssetLevelBundleRefs;
        /// <summary>Sum over all analyzed assets of bundles loaded under bundle-level (incl. owning).</summary>
        public long totalBundleLevelBundleRefs;
        /// <summary>totalBundleLevelBundleRefs - totalAssetLevelBundleRefs (total bundles avoided).</summary>
        public long totalBundlesSaved;
        /// <summary>Per-asset detail (only assets with savings &gt; 0), sorted by savedBundleCount desc.</summary>
        public List<BuildReportAssetDiffEntry> entries = new List<BuildReportAssetDiffEntry>();
    }

    /// <summary>
    /// One asset's asset-level vs bundle-level dependency diff.
    /// </summary>
    [Serializable]
    public class BuildReportAssetDiffEntry
    {
        public string guid;
        public string address;
        public string owningBundle;
        /// <summary>Bundles loaded under asset-level (incl. owning).</summary>
        public int assetLevelBundleCount;
        /// <summary>Bundles loaded under bundle-level closure (incl. owning).</summary>
        public int bundleLevelBundleCount;
        /// <summary>bundleLevelBundleCount - assetLevelBundleCount.</summary>
        public int savedBundleCount;
        /// <summary>Bundle names loaded by bundle-level but NOT by asset-level (the ones avoided).</summary>
        public List<string> savedBundles = new List<string>();
    }

    /// <summary>
    /// Per-bundle entry in the build report.
    /// </summary>
    /// <remarks>
    /// Two dependency lists are emitted for every bundle to match Addressables'
    /// <c>BuildLayout.Bundle.Dependencies</c> / <c>ExpandedDependencies</c> distinction:
    /// <list type="bullet">
    ///   <item><description><see cref="bundleDirectDependencies"/> — one-hop only,
    ///   diagnostic. Same source as Addressables' <c>buildlayout.json Dependencies</c> array,
    ///   so reverse-edge counts compare apples-to-apples (e.g. <c>townmainscene1</c>'s
    ///   <c>DependentBundles</c>).</description></item>
    ///   <item><description><see cref="bundleDependencies"/> — transitive closure as
    ///   produced by SBP <c>BundleDetails.Dependencies</c>; this is what catalog generation
    ///   writes into the runtime catalog so AssetBundle loads cover every chain
    ///   bundle.</description></item>
    /// </list>
    /// </remarks>
    [Serializable]
    public class BuildReportBundleEntry
    {
        public string bundleName;
        public long sizeBytes;
        /// <summary>One-hop / direct edges only — see remarks on <see cref="BuildReportBundleEntry"/>.</summary>
        public List<string> bundleDirectDependencies = new List<string>();
        /// <summary>Transitive closure (matches what the runtime catalog ships) — see remarks on <see cref="BuildReportBundleEntry"/>.</summary>
        public List<string> bundleDependencies = new List<string>();
        public List<BuildReportAssetEntry> assets = new List<BuildReportAssetEntry>();
    }

    /// <summary>
    /// Per-asset entry within a bundle in the build report.
    /// </summary>
    [Serializable]
    public class BuildReportAssetEntry
    {
        public string assetPath;
        public long sizeBytes;
    }
}
