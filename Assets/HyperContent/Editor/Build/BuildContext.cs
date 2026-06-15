using System;
using System.Collections.Generic;
using com.igg.hypercontent.runtime;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Context object that holds build state and configuration
    /// </summary>
    public class BuildContext
    {
        /// <summary>
        /// Build configuration
        /// </summary>
        public BuildConfig Config { get; set; }
        
        /// <summary>
        /// Mapping from asset GUID to HyperContentAsset marker
        /// </summary>
        public Dictionary<string, HyperContentAsset> AssetMarkers { get; set; } = new Dictionary<string, HyperContentAsset>();
        
        /// <summary>
        /// Mapping from asset key to asset GUID
        /// </summary>
        public Dictionary<string, string> KeyToGuid { get; set; } = new Dictionary<string, string>();
        
        /// <summary>
        /// Mapping from asset GUID to asset path
        /// </summary>
        public Dictionary<string, string> GuidToPath { get; set; } = new Dictionary<string, string>();
        
        /// <summary>
        /// Mapping from bundle name to asset GUIDs
        /// </summary>
        public Dictionary<string, HashSet<string>> BundleToAssets { get; set; } = new Dictionary<string, HashSet<string>>();

        /// <summary>
        /// After a successful SBP build: asset paths actually packed into each bundle (includes implicit dependencies).
        /// Used for <c>build_report.json</c> / Bundle Report only. Catalog and markers still use <see cref="BundleToAssets"/>.
        /// </summary>
        public Dictionary<string, HashSet<string>> BundleToPackedAssetPaths { get; set; }
        
        /// <summary>
        /// Mapping from asset GUID to bundle name
        /// </summary>
        public Dictionary<string, string> AssetToBundle { get; set; } = new Dictionary<string, string>();
        
        /// <summary>
        /// Asset dependencies (GUID to GUIDs)
        /// </summary>
        public Dictionary<string, HashSet<string>> Dependencies { get; set; } = new Dictionary<string, HashSet<string>>();
        
        /// <summary>
        /// Bundle-to-bundle dependencies for runtime/catalog use. After
        /// <see cref="DefaultBuildExecutor.RebuildBundleDependenciesFromSbpResults"/> this holds
        /// the SBP <c>BundleDetails.Dependencies</c> set, which SBP itself populates as the
        /// <b>transitive closure</b> of every bundle a referrer can reach (see
        /// <c>ArchiveAndCompressBundles.CalculateBundleDependencies</c> "Recursively combine dependencies").
        /// Catalog generation writes this list verbatim into <c>BundleRecordEntry.dependencies</c>
        /// so the runtime can preload every bundle on the chain — keep it transitive or AssetBundle
        /// loads will silently miss indirect dependencies.
        /// </summary>
        public Dictionary<string, HashSet<string>> BundleDependencies { get; set; } = new Dictionary<string, HashSet<string>>();

        /// <summary>
        /// One-hop bundle dependencies (referrer.bundle → only the bundles its packed objects directly
        /// reference). Filled by <see cref="DefaultBuildExecutor.RebuildBundleDependenciesFromSbpResults"/>
        /// using SBP <c>IBundleWriteData.AssetToFiles</c> + <c>FileToBundle</c>, which is the same
        /// data Addressables turns into <c>aaContext.bundleToImmediateBundleDependencies</c> /
        /// <c>BuildLayout.Bundle.Dependencies</c>. Surfaced for diagnostics
        /// (<c>build_report.json bundleDirectDependencies</c>) so HC's reverse-edge counts can be
        /// compared against Addressables' <c>buildlayout.json DependentBundles</c> on the same
        /// "direct only" footing — independent of <see cref="BundleDependencies"/>, which stays
        /// transitive for the runtime.
        /// </summary>
        public Dictionary<string, HashSet<string>> BundleDirectDependencies { get; set; } = new Dictionary<string, HashSet<string>>();

        /// <summary>
        /// Per-asset dependency bundles for asset-level loading. Maps asset GUID → ordered list of bundle
        /// names that must be loaded to load THAT asset (post-order, owning bundle LAST). Computed from SBP
        /// <c>IBundleWriteData.AssetToFiles</c> + <c>FileToBundle</c> in
        /// <see cref="DefaultBuildExecutor.BuildAssetDependencyBundlesFromSbp"/>; consumed by catalog
        /// generation to fill <c>CatalogSchema.AssetRecordEntry.dependencyBundles</c>.
        /// </summary>
        public Dictionary<string, List<string>> AssetDependencyBundles { get; set; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>
        /// Build errors
        /// </summary>
        public List<BuildError> Errors { get; set; } = new List<BuildError>();
        
        /// <summary>
        /// Build warnings
        /// </summary>
        public List<BuildWarning> Warnings { get; set; } = new List<BuildWarning>();
        
        /// <summary>
        /// Build report data
        /// </summary>
        public BuildReport Report { get; set; } = new BuildReport();
        
        /// <summary>
        /// Mapping from expected bundle name to actual bundle file name (as built by Unity)
        /// Unity might modify bundle names, so we need this mapping
        /// </summary>
        public Dictionary<string, string> ExpectedToActualBundleName { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Per-bundle compression from <see cref="BuildPlan.BundleCompression"/> (filled by grouping strategies). Keys align with <see cref="BundleToAssets"/>.
        /// </summary>
        public Dictionary<string, BundleCompressionType> BundleCompression { get; set; } =
            new Dictionary<string, BundleCompressionType>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Copied from <see cref="BuildPlan.BundleTagFlagsFromPlan"/> when the executor starts; catalog generation ORs this with marker-derived flags.
        /// </summary>
        public Dictionary<string, BundleTagFlags> BundleTagFlagsFromPlan { get; set; } =
            new Dictionary<string, BundleTagFlags>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Safe copy for executors (null/empty plan tags → empty dictionary with case-insensitive keys).
        /// </summary>
        public static Dictionary<string, BundleTagFlags> CloneBundleTagFlagsFromPlan(
            Dictionary<string, BundleTagFlags> pSource)
        {
            if (pSource == null || pSource.Count == 0)
                return new Dictionary<string, BundleTagFlags>(StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, BundleTagFlags>(pSource, StringComparer.OrdinalIgnoreCase);
        }
    }
    
    /// <summary>
    /// Build error information
    /// </summary>
    public class BuildError
    {
        public string Message { get; set; }
        public string AssetPath { get; set; }
        public string AssetKey { get; set; }
        
        public BuildError(string message, string assetPath = null, string assetKey = null)
        {
            Message = message;
            AssetPath = assetPath;
            AssetKey = assetKey;
        }
    }
    
    /// <summary>
    /// Build warning information
    /// </summary>
    public class BuildWarning
    {
        public string Message { get; set; }
        public string AssetPath { get; set; }
        public string AssetKey { get; set; }
        
        public BuildWarning(string message, string assetPath = null, string assetKey = null)
        {
            Message = message;
            AssetPath = assetPath;
            AssetKey = assetKey;
        }
    }
    
    /// <summary>
    /// Build configuration
    /// </summary>
    [Serializable]
    public class BuildConfig
    {
        [Tooltip("Output directory for bundles")]
        public string outputDirectory = "Assets/StreamingAssets";
        
        [Tooltip("Build target platform")]
        public BuildTarget buildTarget = BuildTarget.StandaloneWindows64;
        
        [Tooltip("Compression method for bundles")]
        public BundleCompressionType compressionType = BundleCompressionType.Lz4;
        
        [Tooltip("Include asset dependencies in bundles")]
        public bool includeDependencies = true;
        
        [Tooltip("Force rebuild all bundles")]
        public bool forceRebuild = false;
        
        [Tooltip("Generate build report")]
        public bool generateReport = true;
        
        [Tooltip("Bundle grouping strategy")]
        public BundleGroupingStrategyType groupingStrategy = BundleGroupingStrategyType.MarkerBased;
        
        [Tooltip("Grouping tool ID (e.g., 'default')")]
        public string groupingToolId = "default";
        
        [Tooltip("Build executor ID for full build (e.g., 'default')")]
        public string buildExecutorId = "default";

        [Tooltip("Build executor ID for Update Build (e.g., 'update'). Used when Run Update Build is invoked.")]
        public string updateBuildExecutorId = "update";

        [Tooltip("Editor play mode: UseAssetDatabase (no build needed) or UseExistingAssetBundle")]
        public EditorPlayMode editorPlayMode = EditorPlayMode.UseAssetDatabase;

        [Tooltip("Catalog 序列化格式：\n" +
                 "Json — 可肉眼读、便于排查\n" +
                 "Binary — 紧凑二进制 (HCB1)，解析最快、文件 ~30-50% of Json\n" +
                 "BinaryGzip — HCB1 再过 GZip 压缩，文件 ~10-20% of Json，hot-update 流量友好\n\n" +
                 "切换后必须 Full Build 重打 catalog + settings.json（两端必须一致）。")]
        public CatalogSerializationFormat catalogFormat = CatalogSerializationFormat.Json;

        [Tooltip("依赖加载模式：\n" +
                 "AssetLevel — 只加载资源自身真实依赖的 bundle；catalog 缺 asset 级数据时加载失败（默认）\n" +
                 "BundleLevel — 旧行为：加载 owning bundle 的整条 bundle 级传递闭包（全局回滚 / A-B 用）\n\n" +
                 "写入 settings.json (dependencyLoadMode)，运行时固化、不随 hot-update 改变。")]
        public DependencyLoadMode dependencyLoadMode = DependencyLoadMode.AssetLevel;

        // ── Remote Catalog & Settings Flow ───────────────────────────────

        [Tooltip("Build output root directory (project-level, not inside Assets)")]
        public string buildOutputRoot = "HyperContentBuild";

        [Tooltip("Whether to generate versioned remote catalog files")]
        public bool buildRemoteCatalog = false;

        [Tooltip("Output folder for remote catalog files (for CDN upload)")]
        public string remoteCatalogBuildFolder = "ServerData";

        [Tooltip("Editor reference: unified CDN root (no platform segment). Not written to settings.json; runtime uses SetRemoteBundleBaseUrl.")]
        public string remoteCatalogLoadUrl = "";

        [Tooltip("Override build version (empty = auto UTC timestamp yyyy.MM.dd.HH.mm.ss)")]
        public string overridePlayerVersion = "";

        /// <summary>
        /// Auto timestamp frozen for one <see cref="HyperContentBuilder.Build"/> call when
        /// <see cref="overridePlayerVersion"/> is empty. Without this, each read of
        /// <see cref="ResolvedBuildVersion"/> used <c>UtcNow</c> again — remote catalog could use
        /// second N while <c>build_manifest.json</c> saved second N+1, breaking Update Build naming.
        /// </summary>
        [NonSerialized]
        private string _sessionAutoBuildVersion;

        [Tooltip("Catalog download request timeout in seconds")]
        public int catalogRequestTimeout = 30;

        // ── Content Update Build ─────────────────────────────────────────

        [Tooltip("Update bundle grouping strategy for Update Build")]
        public UpdateBundleGroupingStrategyType updateBundleGroupingStrategy = UpdateBundleGroupingStrategyType.GroupByOriginalBundle;

        [Tooltip("After a successful Update Build, sync Addressable Content Update groups from HyperContent B1 mapping (via AddressableBuilder). Default off.")]
        public bool syncAddressableGroupsAfterUpdateBuild = false;

        /// <summary>
        /// When true, sets <c>ContentBuildFlags.StripUnityVersion</c> on SBP bundle builds (see <c>HyperContentBundleBuildParameters</c>).
        /// Addressables-packed bundles typically keep the Unity-version chunk in headers; default <c>false</c> here matches that.
        /// </summary>
        /// <remarks>
        /// MuseumUI load-time investigation did not show a meaningful win from stripping alone; the field remains for
        /// hash-stability / cross-version A/B and layout comparison (e.g. vs AssetRipper dumps), not as a default perf switch.
        /// </remarks>
        [Tooltip("Strip Unity version chunk from bundle headers (ContentBuildFlags.StripUnityVersion). " +
                 "Addressables leaves it in by default (false). Kept configurable so we can A/B the " +
                 "bundle-layout delta reported via AssetRipper: HC-built bundles were missing the " +
                 "Unity-version block that Addressables-built ones had. Default false to match " +
                 "Addressables and expose any slow-path difference in native AssetBundle loading.")]
        public bool stripUnityVersionFromBundleHeaders = false;

        /// <summary>
        /// Used when <see cref="updateBundleGroupingStrategy"/> is <see cref="UpdateBundleGroupingStrategyType.Custom"/>.
        /// Not serialized; assign from code before Update Build.
        /// </summary>
        [NonSerialized]
        public IUpdateBundleGroupingStrategy customUpdateBundleGroupingStrategy;

        /// <summary>
        /// Invoked after Update Build Phases A–D complete successfully, before returning success.
        /// Receives B1 <c>updateMapping</c> (update bundle name → changed assets) for Addressable sync, etc.
        /// </summary>
        [NonSerialized]
        public Action<Dictionary<string, List<ChangedAssetInfo>>, BuildConfig> onAfterUpdateBuildSucceeded;

        [Tooltip("Editor reference: unified CDN root (no platform segment). Not written to settings.json; runtime uses SetRemoteBundleBaseUrl.")]
        public string remoteBundleLoadUrl = "";

        /// <summary>
        /// Platform-specific output directory: buildOutputRoot + platform subfolder
        /// e.g. "HyperContentBuild/Android"
        /// </summary>
        public string PlatformOutputDirectory =>
            System.IO.Path.Combine(buildOutputRoot, GetPlatformSubfolder(buildTarget));

        /// <summary>
        /// Bundle output directory: Assets/StreamingAssets/{Platform}/Bundles/
        /// See CONVENTIONS.md §3.1
        /// </summary>
        public string BundleOutputDirectory =>
            System.IO.Path.Combine("Assets", "StreamingAssets", GetPlatformSubfolder(buildTarget), "Bundles");

        /// <summary>
        /// Catalog + settings output directory: {buildOutputRoot}/{Platform}/hc/
        /// See CONVENTIONS.md §3.1
        /// </summary>
        public string CatalogOutputDirectory =>
            System.IO.Path.Combine(buildOutputRoot, GetPlatformSubfolder(buildTarget), "hc");

        /// <summary>
        /// Build manifest path: {buildOutputRoot}/{Platform}/build_manifest.json
        /// </summary>
        public string BuildManifestPath =>
            System.IO.Path.Combine(buildOutputRoot, GetPlatformSubfolder(buildTarget), "build_manifest.json");

        /// <summary>
        /// Legacy server output for update bundles when <see cref="buildRemoteCatalog"/> is false: ServerData/{Platform}/Bundles/.
        /// When remote catalog is enabled, Update Build copies bundles next to <c>HyperCatalog_*.bin</c> in
        /// <see cref="GetResolvedRemoteCatalogBuildFolder"/> instead (see UpdateBuildExecutor).
        /// </summary>
        public string ServerDataOutputDirectory =>
            System.IO.Path.Combine("ServerData", GetPlatformSubfolder(buildTarget), "Bundles");

        /// <summary>
        /// Resolved build version: <see cref="overridePlayerVersion"/> if set; otherwise the UTC timestamp
        /// captured when <see cref="BeginNewBuildVersionSession"/> runs (one value per build pipeline).
        /// </summary>
        public string ResolvedBuildVersion
        {
            get
            {
                if (!string.IsNullOrEmpty(overridePlayerVersion))
                    return overridePlayerVersion;
                if (!string.IsNullOrEmpty(_sessionAutoBuildVersion))
                    return _sessionAutoBuildVersion;
                // Fallback when tooling reads version without starting a build session (Editor previews).
                var now = System.DateTime.UtcNow;
                return string.Format("{0:D4}.{1:D2}.{2:D2}.{3:D2}.{4:D2}.{5:D2}",
                    now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
            }
        }

        /// <summary>
        /// Call at the start of <see cref="HyperContentBuilder.Build"/> so auto timestamps stay stable
        /// for remote catalog, manifest, settings.json, and update bundle names within one build.
        /// </summary>
        public void BeginNewBuildVersionSession()
        {
            _sessionAutoBuildVersion = null;
            if (!string.IsNullOrEmpty(overridePlayerVersion))
                return;
            var now = System.DateTime.UtcNow;
            _sessionAutoBuildVersion = string.Format("{0:D4}.{1:D2}.{2:D2}.{3:D2}.{4:D2}.{5:D2}",
                now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
        }
        
        public static string GetPlatformSubfolder(BuildTarget pBuildTarget)
        {
            switch (pBuildTarget)
            {
                case BuildTarget.Android:
                    return "Android";
                case BuildTarget.iOS:
                    return "iOS";
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return "Windows";
                case BuildTarget.StandaloneOSX:
                    return "macOS";
                case BuildTarget.StandaloneLinux64:
                    return "Linux";
                case BuildTarget.WebGL:
                    return "WebGL";
                default:
                    return pBuildTarget.ToString();
            }
        }

        /// <summary>
        /// Resolve remote catalog build folder to an absolute path under project root.
        /// Relative paths (e.g. "ServerData") become projectRoot/ServerData/Production/{Platform} so output
        /// is always under the project regardless of current working directory.
        /// </summary>
        public static string GetResolvedRemoteCatalogBuildFolder(string pFolder, BuildTarget pBuildTarget)
        {
            if (string.IsNullOrEmpty(pFolder)) return pFolder;
            string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
            string basePath = System.IO.Path.IsPathRooted(pFolder)
                ? pFolder
                : System.IO.Path.Combine(projectRoot, pFolder);
            return System.IO.Path.Combine(basePath, "Production", GetPlatformSubfolder(pBuildTarget));
        }

        /// <summary>
        /// CDN root for <see cref="RuntimeSettings.remoteBundleBaseUrl"/> (no per-platform segment). Bundles and catalog/hash use the same base at runtime.
        /// </summary>
        public static string GetUnifiedCdnRootForRuntimeSettings(BuildConfig pConfig)
        {
            if (pConfig == null)
                return null;
            if (!string.IsNullOrEmpty(pConfig.remoteBundleLoadUrl))
                return pConfig.remoteBundleLoadUrl.TrimEnd('/');
            if (!string.IsNullOrEmpty(pConfig.remoteCatalogLoadUrl))
                return pConfig.remoteCatalogLoadUrl.TrimEnd('/');
            return null;
        }
    }
    
    /// <summary>
    /// Compression type for AssetBundles
    /// </summary>
    public enum BundleCompressionType
    {
        None,
        Lz4,
        Lz4HC,
        /// <summary>
        /// Matches Addressables <c>BundledAssetGroupSchema.BundleCompressionMode.LZMA</c>.
        /// </summary>
        Lzma
    }
    
    /// <summary>
    /// Editor play mode: how assets are loaded when entering Play Mode in the Editor.
    /// Stored in HyperContentBuildConfig.json so it persists across sessions.
    /// </summary>
    public enum EditorPlayMode
    {
        /// <summary>
        /// Load assets directly via AssetDatabase — no AB build required, fastest iteration.
        /// </summary>
        UseAssetDatabase = 0,

        /// <summary>
        /// Load assets from pre-built AssetBundles — matches runtime behavior.
        /// </summary>
        UseExistingAssetBundle = 1
    }

    /// <summary>
    /// Bundle grouping strategy type
    /// </summary>
    public enum BundleGroupingStrategyType
    {
        /// <summary>
        /// Use HyperContentAsset markers (bundleGroup field) for grouping
        /// </summary>
        MarkerBased,
        
        /// <summary>
        /// Use Addressable groups for grouping
        /// </summary>
        Addressable
    }
}
