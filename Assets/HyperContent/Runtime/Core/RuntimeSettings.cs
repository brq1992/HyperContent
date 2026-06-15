using System;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Selects how <see cref="LocalContentCatalog"/> resolves an asset's dependency bundles at load time.
    /// Baked into settings.json at build time (as <see cref="RuntimeSettings.dependencyLoadMode"/>); not hot-updatable.
    /// </summary>
    public enum DependencyLoadMode
    {
        /// <summary>
        /// Default. Load only the asset's own per-asset dependency bundles (<c>AssetRecordEntry.dependencyBundles</c>).
        /// If an asset has no asset-level data, the load FAILS with <c>ErrorCode.CATALOG_ASSET_DEPS_MISSING</c>
        /// (no bundle-level fallback) so missing build data is surfaced loudly.
        /// </summary>
        AssetLevel = 0,

        /// <summary>
        /// Legacy behavior. Load the owning bundle's full bundle-level transitive closure, ignoring per-asset data.
        /// Kept as a global rollback / A-B switch in case asset-level loading misbehaves in production.
        /// </summary>
        BundleLevel = 1,
    }

    /// <summary>
    /// Runtime settings deserialized from settings.json (baked into StreamingAssets at build time).
    /// Equivalent to Addressables' ResourceManagerRuntimeData.
    /// </summary>
    [Serializable]
    public class RuntimeSettings
    {
        /// <summary>Build version string, e.g. "2026.02.24.03.02.10"</summary>
        public string buildVersion;

        /// <summary>Unix timestamp of the build</summary>
        public long buildTimestamp;

        /// <summary>Package catalog relative path, always "HyperCatalog.bin"</summary>
        public string localCatalogPath;

        /// <summary>
        /// Remote catalog <c>.bin</c> path relative to CDN root **after** the per-platform segment (same layout as published <c>ServerData/{Platform}/</c>), e.g. <c>HyperCatalog_2026.03.25.17.57.05.bin</c>.
        /// Does not mirror the local <c>StreamingAssets/hc/</c> folder on the CDN — use the versioned filename only.
        /// Absolute URL is <see cref="HyperContentPaths.CombineRemoteCdnRequestUrl"/> with <see cref="remoteBundleBaseUrl"/>.
        /// </summary>
        public string remoteCatalogRelativePath;

        /// <summary>
        /// Remote catalog hash file path (same layout as <see cref="remoteCatalogRelativePath"/>), e.g. <c>HyperCatalog_2026.03.25.17.57.05.hash</c>.
        /// </summary>
        public string remoteCatalogHashRelativePath;

        /// <summary>Local cache catalog path (versioned), e.g. "HyperCatalog_2026.02.24.03.02.10.bin"</summary>
        public string cachedCatalogPath;

        /// <summary>Local cache hash path (versioned), e.g. "HyperCatalog_2026.02.24.03.02.10.hash"</summary>
        public string cachedCatalogHashPath;

        /// <summary>Timeout in seconds for catalog download requests</summary>
        public int catalogRequestTimeout;

        /// <summary>
        /// CDN base URL for bundle downloads, **without** the per-platform folder.
        /// Example: <c>https://cdn.example.com/bundles/</c> — runtime resolves each bundle as
        /// <c>{base}{platform}/{catalogRelativePath}</c> (platform = Android, iOS, Windows, macOS; same as local StreamingAssets layout).
        /// </summary>
        public string remoteBundleBaseUrl;

        /// <summary>
        /// Android only — name of the Google Play Asset Delivery (PAD) asset pack that contains
        /// the locally-shipped (StreamingAssets-mode) bundles. Must match the pack name registered
        /// in the AAB build (<see cref="Google.Android.AppBundle.Editor.AssetPacks.AssetPackConfig.AddAssetsFolder"/>'s
        /// first argument, e.g. "Bundles"). Empty disables PAD lookups; runtime falls back to direct
        /// StreamingAssets file IO (works for non-Play installs / universal APK / non-Android).
        /// </summary>
        public string playAssetDeliveryPackName;

        /// <summary>
        /// Catalog 序列化格式 — 与构建期 <c>BuildConfig.catalogFormat</c> 严格对称。
        /// 取值映射 <see cref="CatalogSerializationFormat"/>：
        /// <c>0</c> = Json，<c>1</c> = Binary（HCB1，无压缩），<c>2</c> = BinaryGzip（HCB1 + GZip）。
        ///
        /// 用 <c>int</c> 而非 enum：JsonUtility 对 enum 序列化在不同 Unity 版本表现不一致；
        /// 用 int 直接以数字写入 settings.json，跨版本稳定。
        ///
        /// 重要：settings.json 在 APK 出包时固化，不通过 hot-update 更新。Runtime 按此值
        /// 严格分发到对应反序列化路径，**不做格式探测、不做 fallback**——cdn 下发 catalog
        /// 必须与 APK 内置该字段一致，否则按 <c>CATALOG_INVALID_FORMAT</c> 失败。
        /// </summary>
        public int catalogFormat;

        /// <summary>
        /// Dependency loading mode — maps to <see cref="DependencyLoadMode"/> (<c>0</c> = AssetLevel, <c>1</c> = BundleLevel).
        /// Stored as <c>int</c> for the same cross-version JsonUtility reason as <see cref="catalogFormat"/>.
        /// <c>0</c> (AssetLevel) is the default for both new builds and any older settings.json that lacks this field.
        /// </summary>
        public int dependencyLoadMode;

        /// <summary>Check if remote catalog update is configured (relative hash path in JSON; base may be filled at runtime).</summary>
        public bool HasRemoteCatalog =>
            !string.IsNullOrEmpty(remoteCatalogHashRelativePath);

        /// <summary>Check if remote bundle download is configured</summary>
        public bool HasRemoteBundles =>
            !string.IsNullOrEmpty(remoteBundleBaseUrl);

        /// <summary>Check if Google Play Asset Delivery is enabled for local bundles.</summary>
        public bool HasPlayAssetDelivery =>
            !string.IsNullOrEmpty(playAssetDeliveryPackName);
    }
}
