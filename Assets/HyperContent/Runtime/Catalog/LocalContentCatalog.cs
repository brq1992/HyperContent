using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using com.igg.core;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Default ICatalog implementation: parses CatalogSchema JSON and provides
    /// O(1) Dictionary-based address resolution (GUID or Name) into ResourceLocation trees.
    /// Also serves content-management queries (TryGetBundleInfo, GetAllBundleNames) for Owner3 update pipeline.
    /// </summary>
    public class LocalContentCatalog : ICatalog
    {
        private CatalogSchema _schema;

        private Dictionary<string, int> _guidToAssetIndex;
        private Dictionary<string, int> _nameToAssetIndex;
        private Dictionary<string, int> _bundleNameToRecordIndex;
        private BundleInfo[] _bundleInfoCache;

        // Prebuilt, type-independent ResourceLocation trees. Built once in BuildLookupStructures();
        // TryGetLocations hits these caches in O(1) and only allocates the per-call asset ResourceLocation.
        // _bundleLocationCache[i]      -> shared ResourceLocation for bundleRecords[i]
        // _bundleFlatDepsCache[i]      -> shared flat transitive-dep list (includes bundle[i] itself)
        // _assetFlatDepsCache[i]       -> asset-level flat dep list for assetRecords[i] (owning bundle LAST);
        //                                 null when the catalog carries no asset-level data for that asset.
        private ResourceLocation[] _bundleLocationCache;
        private IReadOnlyList<ResourceLocation>[] _bundleFlatDepsCache;
        private IReadOnlyList<ResourceLocation>[] _assetFlatDepsCache;

        private const string PROVIDER_BUNDLE_FILE = "BundleFileProvider";
        private const string PROVIDER_REMOTE_BUNDLE = "RemoteBundleProvider";
        private const string PROVIDER_BUNDLE_ASSET = "BundleAssetExtractor";

        /// <summary>
        /// Diagnostic-only override: map of bundleName → extra bundleNames to treat as
        /// transitive dependencies, on top of whatever the baked catalog declares.
        ///
        /// Used to validate hypotheses about missing bundle deps causing slow
        /// LoadAssetAsync (Unity's deserializer burning time on fallback lookups when
        /// cross-bundle PPtrs can't be resolved). Populate BEFORE <see cref="Initialize"/>
        /// is called — the flat-dep cache is built during init and will snapshot this map.
        ///
        /// Example (test harness):
        ///   LocalContentCatalog.DiagnosticExtraDependencies["features_musuem"] =
        ///       new[] { "features_common", "features_activity" };
        ///
        /// Leave empty in production.
        /// </summary>
        public static readonly Dictionary<string, string[]> DiagnosticExtraDependencies =
            new Dictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary>
        /// Dependency resolution mode. Set BEFORE <see cref="Initialize(string)"/> (the per-asset cache is built
        /// during init and snapshots this value). Defaults to <see cref="DependencyLoadMode.AssetLevel"/>.
        /// Populated by the init flow from <c>RuntimeSettings.dependencyLoadMode</c> (settings.json).
        /// </summary>
        public DependencyLoadMode LoadMode { get; set; } = DependencyLoadMode.AssetLevel;

        public bool IsValid => _schema != null;

        public string Version => _schema?.timestamp.ToString() ?? "0";

        /// <summary>
        /// <see cref="ICatalog.Initialize(string)"/> 实现。默认按 Json 格式读取
        /// （历史行为）。新代码应改用 <see cref="Initialize(string, CatalogSerializationFormat)"/>
        /// 并从 <c>RuntimeSettings.catalogFormat</c> 透传格式。
        /// </summary>
        public bool Initialize(string source)
        {
            return Initialize(source, CatalogSerializationFormat.Json);
        }

        /// <summary>
        /// 按显式 <paramref name="format"/> 反序列化 catalog。读取端与构建端
        /// (<c>BuildConfig.catalogFormat</c> → <c>settings.json.catalogFormat</c> →此参数)
        /// 严格对称：不做 magic 探测、不做 fallback。失败按
        /// <see cref="ErrorCode.CATALOG_INVALID_FORMAT"/> 报错。
        ///
        /// 性能埋点全部走 <see cref="IGGProfiler"/>（<c>[Conditional("ENABLE_PROFILERLOG")]</c>），
        /// 未定义该宏的构建会被编译器整段去除，**零运行时成本**。开宏后会按 format 维度输出
        /// IO / Decompress / Deserialize / BuildLookup / Total 五段耗时，并附带 IGGProfiler 的
        /// 阈值警告（300 / 600 / 1000ms）。
        /// </summary>
        public bool Initialize(string source, CatalogSerializationFormat format)
        {
#if ENABLE_PROFILERLOG
            // 各分段名带 format 后缀，避免 IGGProfiler 内部 dict key 冲突，
            // 也方便在 log / Profiler 视图按 format 横向对比。
            // 整体包在 #if ENABLE_PROFILERLOG 内：关宏时变量声明 + 字符串拼接全部不进编译产物，零成本。
            string keyTotal       = $"HC.Catalog.Init.Total[{format}]";
            string keyIO          = $"HC.Catalog.Init.IO[{format}]";
            string keyDecompress  = $"HC.Catalog.Init.Decompress[{format}]";
            string keyDeserial    = $"HC.Catalog.Init.Deserialize[{format}]";
            string keyBuildLookup = $"HC.Catalog.Init.BuildLookup[{format}]";
            IGGProfiler.BeginSample(keyTotal);
#endif
            try
            {
                switch (format)
                {
                    case CatalogSerializationFormat.Binary:
                    case CatalogSerializationFormat.BinaryGzip:
                    {
#if ENABLE_PROFILERLOG
                        IGGProfiler.BeginSample(keyIO);
#endif
                        byte[] bytes = HyperContentPaths.LoadBytesWithStreamingFallback(source);
#if ENABLE_PROFILERLOG
                        IGGProfiler.EndSample(keyIO);
#endif

                        if (bytes == null || bytes.Length == 0)
                        {
                            HCLogger.LogError(ErrorCode.CATALOG_NOT_FOUND,
                                $"Catalog not found: {source}");
                            return false;
                        }

                        if (format == CatalogSerializationFormat.BinaryGzip)
                        {
#if ENABLE_PROFILERLOG
                            IGGProfiler.BeginSample(keyDecompress);
#endif
                            bytes = GzipDecompress(bytes);
#if ENABLE_PROFILERLOG
                            IGGProfiler.EndSample(keyDecompress);
#endif
                        }

#if ENABLE_PROFILERLOG
                        IGGProfiler.BeginSample(keyDeserial);
#endif
                        _schema = CatalogBinaryReader.Read(bytes);
#if ENABLE_PROFILERLOG
                        IGGProfiler.EndSample(keyDeserial);
#endif
                        break;
                    }

                    default:  // Json
                    {
#if ENABLE_PROFILERLOG
                        IGGProfiler.BeginSample(keyIO);
#endif
                        string jsonContent = HyperContentPaths.LoadTextWithStreamingFallback(source);
#if ENABLE_PROFILERLOG
                        IGGProfiler.EndSample(keyIO);
#endif

                        if (string.IsNullOrEmpty(jsonContent))
                        {
                            HCLogger.LogError(ErrorCode.CATALOG_NOT_FOUND,
                                $"Catalog not found: {source}");
                            return false;
                        }

#if ENABLE_PROFILERLOG
                        IGGProfiler.BeginSample(keyDeserial);
#endif
                        _schema = JsonUtility.FromJson<CatalogSchema>(jsonContent);
#if ENABLE_PROFILERLOG
                        IGGProfiler.EndSample(keyDeserial);
#endif
                        break;
                    }
                }

                if (_schema == null || _schema.assetRecords == null || _schema.bundleRecords == null)
                {
                    HCLogger.LogError(ErrorCode.CATALOG_INVALID_FORMAT,
                        $"Failed to parse catalog (format={format})");
                    return false;
                }

                if (_schema.schemaVersion != CatalogSchema.CurrentSchemaVersion)
                {
                    HCLogger.LogError(ErrorCode.CATALOG_VERSION_MISMATCH,
                        $"Catalog schemaVersion={_schema.schemaVersion}, expected {CatalogSchema.CurrentSchemaVersion}. " +
                        "Rebuild and publish catalog (no runtime fallback to older formats).");
                    _schema = null;
                    return false;
                }

#if ENABLE_PROFILERLOG
                IGGProfiler.BeginSample(keyBuildLookup);
#endif
                BuildLookupStructures();
#if ENABLE_PROFILERLOG
                IGGProfiler.EndSample(keyBuildLookup);
#endif

                HCLogger.LogInfo($"Catalog loaded (format={format}): " +
                    $"{_schema.assetRecords.Count} assets, " +
                    $"{_schema.nameAliases?.Count ?? 0} names, " +
                    $"{_schema.bundleRecords.Count} bundles");
                return true;
            }
            catch (Exception e)
            {
                HCLogger.LogError(ErrorCode.CATALOG_LOAD_FAILED,
                    $"Catalog initialization failed: {e.Message}");
                return false;
            }
            finally
            {
#if ENABLE_PROFILERLOG
                IGGProfiler.EndSample(keyTotal);
#endif
            }
        }

        // ── ICatalog: Core address resolution ───────────────────────────────

        public bool TryGetLocations(string address, Type type, out IList<ResourceLocation> locations)
        {
            locations = null;
            if (_schema == null || string.IsNullOrEmpty(address)) return false;

            int assetIndex = ResolveAddressToAssetIndex(address);
            if (assetIndex < 0) return false;

            var record = _schema.assetRecords[assetIndex];
            string assetPath = GetString(record.assetPathIndex);
            if (string.IsNullOrEmpty(assetPath)) return false;

            IReadOnlyList<ResourceLocation> bundleLocations;
            if (LoadMode == DependencyLoadMode.BundleLevel)
            {
                // Legacy bundle-level loading: load the owning bundle's full transitive closure.
                bundleLocations =
                    (_bundleFlatDepsCache != null && record.bundleIndex >= 0 && record.bundleIndex < _bundleFlatDepsCache.Length)
                        ? _bundleFlatDepsCache[record.bundleIndex]
                        : Array.Empty<ResourceLocation>();
            }
            else
            {
                // Asset-level dependency loading: only load THIS asset's own dependency bundles + its owning bundle.
                // No bundle-level fallback — a missing asset-level list is a build-pipeline bug, surfaced loudly so
                // it is not masked by silently over-loading the owning bundle's full bundle-level closure.
                bundleLocations =
                    (_assetFlatDepsCache != null && assetIndex >= 0 && assetIndex < _assetFlatDepsCache.Length)
                        ? _assetFlatDepsCache[assetIndex]
                        : null;

                if (bundleLocations == null)
                {
                    HCLogger.LogError(ErrorCode.CATALOG_ASSET_DEPS_MISSING,
                        $"Asset '{address}' has no asset-level dependency bundles in the catalog " +
                        $"(bundleIndex={record.bundleIndex}). Rebuild the catalog with asset-level deps, " +
                        "or set dependencyLoadMode=BundleLevel. Load fails (no bundle-level fallback in AssetLevel mode).");
                    return false;
                }
            }

#if HYPERCONTENT_LOG_VERBOSE
            // 必须整段 #if 包，不能仅依赖 HCLogger.LogVerbose 的 [Conditional] 守卫：
            // [Conditional] 只擦除调用点 + 参数表达式（即 sb.ToString() 这一调用），
            // 但前面 StringBuilder.Append + for 循环 + bundleLocations[i].InternalId 字符串拼接
            // 是独立语句，编译器不会擦除——会留在 IL 里每次调用 TryGetLocations 都执行。
            //
            // 实测影响（D2 VipTimeBoxItem.prefab 53 dep 资源）：
            //   修复前：HC.Load.Stage.Catalog 段 ≈ 0.662ms（StringBuilder + 53 次字符串拼接 + GC）
            //   修复后预期：< 0.1ms（与 MainChat 8 dep 的 0.07ms 同数量级，O(deps) 退化为 O(1)）
            //
            // 这是 D2 hotfix #3（继 Provide 时间污染 / PAD BundleIO 缺失之后第三处埋点失误）。
            if (bundleLocations.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"[HC.Catalog] Resolve '{address}' → bundle[{record.bundleIndex}] deps({bundleLocations.Count}): ");
                for (int i = 0; i < bundleLocations.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(bundleLocations[i].InternalId);
                }
                HCLogger.LogVerbose(sb.ToString());
            }
#endif

            var assetLocation = new ResourceLocation(
                address: address,
                internalId: assetPath,
                providerId: PROVIDER_BUNDLE_ASSET,
                resourceType: type,
                dependencies: bundleLocations);

            locations = new List<ResourceLocation>(1) { assetLocation };
            return true;
        }

        /// <summary>
        /// Diagnostic helper: resolve <paramref name="address"/> to the ordered set of dependency bundle
        /// names that loading it would pull in under the current <see cref="LoadMode"/> (post-order, owning
        /// bundle LAST). Unlike <see cref="TryGetLocations"/> this is side-effect free — it never logs, never
        /// allocates an asset location, and never raises <c>CATALOG_ASSET_DEPS_MISSING</c>; on missing
        /// asset-level data it returns <c>true</c> with an empty list and a descriptive <paramref name="note"/>.
        /// Intended for the runtime/editor inspectors' "deps by address" view.
        /// </summary>
        /// <returns><c>false</c> only when the address cannot be resolved at all.</returns>
        public bool TryGetDependencyBundleNamesForDiagnostics(string address, out List<string> bundleNames, out string note)
        {
            bundleNames = new List<string>();
            note = null;

            if (_schema == null)
            {
                note = "catalog not initialized";
                return false;
            }
            if (string.IsNullOrEmpty(address))
            {
                note = "empty address";
                return false;
            }

            int assetIndex = ResolveAddressToAssetIndex(address);
            if (assetIndex < 0)
            {
                note = "address not found in catalog";
                return false;
            }

            var record = _schema.assetRecords[assetIndex];

            IReadOnlyList<ResourceLocation> bundleLocations;
            if (LoadMode == DependencyLoadMode.BundleLevel)
            {
                bundleLocations =
                    (_bundleFlatDepsCache != null && record.bundleIndex >= 0 && record.bundleIndex < _bundleFlatDepsCache.Length)
                        ? _bundleFlatDepsCache[record.bundleIndex]
                        : null;
                note = "BundleLevel: owning bundle's full transitive closure";
            }
            else
            {
                bundleLocations =
                    (_assetFlatDepsCache != null && assetIndex >= 0 && assetIndex < _assetFlatDepsCache.Length)
                        ? _assetFlatDepsCache[assetIndex]
                        : null;
                if (bundleLocations == null)
                {
                    note = "AssetLevel: NO asset-level deps in catalog — this load would FAIL " +
                           "(rebuild catalog with asset-level deps, or use BundleLevel mode)";
                    return true;
                }
                note = "AssetLevel: this asset's own dependency bundles + owning bundle";
            }

            if (bundleLocations != null)
            {
                for (int i = 0; i < bundleLocations.Count; i++)
                {
                    var loc = bundleLocations[i];
                    if (loc != null && !string.IsNullOrEmpty(loc.Address))
                        bundleNames.Add(loc.Address);
                }
            }
            return true;
        }

        // ── ICatalog: Bundle queries ─────────────────────────────────────────

        public bool TryGetBundleInfo(string bundleName, out BundleInfo bundleInfo)
        {
            bundleInfo = null;
            if (_schema == null || string.IsNullOrEmpty(bundleName)) return false;
            if (!_bundleNameToRecordIndex.TryGetValue(bundleName, out int idx)) return false;

            bundleInfo = _bundleInfoCache[idx];
            return bundleInfo != null;
        }

        public IEnumerable<string> GetAllBundleNames()
        {
            if (_bundleNameToRecordIndex == null) return Array.Empty<string>();
            return _bundleNameToRecordIndex.Keys;
        }

        // ── Lifecycle ───────────────────────────────────────────────────────

        public void Release()
        {
            _schema = null;
            _guidToAssetIndex = null;
            _nameToAssetIndex = null;
            _bundleNameToRecordIndex = null;
            _bundleInfoCache = null;
            _bundleLocationCache = null;
            _bundleFlatDepsCache = null;
            _assetFlatDepsCache = null;
        }

        // ── Static helpers (catalog hash comparison for hot-update) ─────────

        /// <summary>
        /// 从 catalog 字节内容中提取 catalogHash 用于 hot-update 比较。
        /// 与 <see cref="Initialize(string, CatalogSerializationFormat)"/> 严格对称：
        /// 调用方必须显式传入 <paramref name="format"/>（来自 RuntimeSettings.catalogFormat），
        /// 不做格式探测。
        /// Binary / BinaryGzip 走 <see cref="CatalogBinaryReader.PeekCatalogHash"/>（仅读到 hash 字段，
        /// 不展开 stringTable / records，开销极小）。
        /// </summary>
        public static string GetCatalogHashFromBytes(byte[] catalogBytes, CatalogSerializationFormat format)
        {
            if (catalogBytes == null || catalogBytes.Length == 0) return null;
            try
            {
                switch (format)
                {
                    case CatalogSerializationFormat.Binary:
                        return CatalogBinaryReader.PeekCatalogHash(catalogBytes);

                    case CatalogSerializationFormat.BinaryGzip:
                        return CatalogBinaryReader.PeekCatalogHash(GzipDecompress(catalogBytes));

                    default:  // Json
                    {
                        var json = Encoding.UTF8.GetString(catalogBytes);
                        var schema = JsonUtility.FromJson<CatalogSchema>(json);
                        return schema?.catalogHash;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] GzipDecompress(byte[] compressed)
        {
            using (var inMs = new MemoryStream(compressed, writable: false))
            using (var gz = new GZipStream(inMs, CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            {
                gz.CopyTo(outMs);
                return outMs.ToArray();
            }
        }

        /// <summary>
        /// Compare two catalog hash strings. Returns true if both null or equal.
        /// </summary>
        public static bool CatalogHashEquals(string a, string b)
        {
            if (a == null && b == null) return true;
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        // ── Internal: lookup structures ─────────────────────────────────────

        private void BuildLookupStructures()
        {
            // OrdinalIgnoreCase lets ResolveAddressToAssetIndex pass the raw address straight in,
            // avoiding a per-lookup ToLowerInvariant() allocation on the GUID hot path.
            _guidToAssetIndex = new Dictionary<string, int>(
                _schema.assetRecords.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _schema.assetRecords.Count; i++)
            {
                var guid = _schema.assetRecords[i].guid;
                if (!string.IsNullOrEmpty(guid))
                    _guidToAssetIndex[guid] = i;
            }

            _nameToAssetIndex = new Dictionary<string, int>(
                _schema.nameAliases?.Count ?? 0, StringComparer.Ordinal);
            if (_schema.nameAliases != null)
            {
                for (int i = 0; i < _schema.nameAliases.Count; i++)
                {
                    var alias = _schema.nameAliases[i];
                    string name = GetString(alias.nameStringIndex);
                    if (!string.IsNullOrEmpty(name)
                        && alias.guidIndex >= 0
                        && alias.guidIndex < _schema.assetRecords.Count)
                    {
                        _nameToAssetIndex[name] = alias.guidIndex;
                    }
                }
            }

            _bundleNameToRecordIndex = new Dictionary<string, int>(
                _schema.bundleRecords.Count, StringComparer.Ordinal);
            _bundleInfoCache = new BundleInfo[_schema.bundleRecords.Count];
            for (int i = 0; i < _schema.bundleRecords.Count; i++)
            {
                var rec = _schema.bundleRecords[i];
                string bundleName = GetString(rec.bundleNameIndex);
                if (!string.IsNullOrEmpty(bundleName))
                {
                    NamingRules.RequireCatalogBundleRelativePath(bundleName, $"bundleRecords[{i}].bundleName");
                    _bundleNameToRecordIndex[bundleName] = i;
                }

                int depCount = rec.dependencies?.Count ?? 0;
                if (depCount > 0)
                {
                    var depNames = new string[depCount];
                    for (int d = 0; d < depCount; d++)
                    {
                        int di = rec.dependencies[d];
                        depNames[d] = (di >= 0 && di < _schema.bundleRecords.Count)
                            ? GetString(_schema.bundleRecords[di].bundleNameIndex) : "?";
                    }
                    HCLogger.LogVerbose($"[HC.Catalog] bundle[{i}]={bundleName} deps=[{string.Join(", ", depNames)}]");
                }
                else
                {
                    HCLogger.LogVerbose($"[HC.Catalog] bundle[{i}]={bundleName} deps=NONE");
                }

                var location = rec.contentLocation == (int)ContentLocation.Remote
                    ? ContentLocation.Remote
                    : ContentLocation.StreamingAssets;

                string remoteRel = null;
                if (location == ContentLocation.Remote)
                    remoteRel = ResolveRemoteRelativePathForRecord(rec, bundleName);

                // Disk files use ".bundle"; catalog bundleName must be extensionless (enforced above).
                string localFileName = string.IsNullOrEmpty(bundleName)
                    ? bundleName
                    : bundleName + NamingRules.BUNDLE_FILE_EXTENSION;

                _bundleInfoCache[i] = new BundleInfo
                {
                    Name = bundleName,
                    Size = rec.size,
                    Hash = rec.bundleHash,
                    Version = 0,
                    Location = location,
                    TagFlags = (BundleTagFlags)rec.bundleTagFlags,
                    RemoteRelativePath = remoteRel,
                    LocalPath = location == ContentLocation.StreamingAssets
                        ? HyperContentPaths.CombinePath(HyperContentPaths.BundleBasePath, localFileName)
                        : null,
                    Dependencies = BuildBundleDependencyNames(rec),
                    AssetKeys = Array.Empty<string>()
                };
            }

            BuildBundleLocationCache();
            BuildBundleFlatDepsCache();

            // Per-asset cache is only consumed in AssetLevel mode. In BundleLevel mode we skip building it
            // (avoids wasted work and spurious "missing asset-level data" warnings) and use the bundle-level
            // closure instead.
            if (LoadMode == DependencyLoadMode.AssetLevel)
                BuildAssetFlatDepsCache();
            else
                _assetFlatDepsCache = null;
        }

        /// <summary>
        /// For each asset record, pre-compute its asset-level flat dependency list as an immutable array of
        /// shared bundle <see cref="ResourceLocation"/> instances (reused from <see cref="_bundleLocationCache"/>).
        /// The list follows the catalog's <c>dependencyBundles</c> order (dependency bundles first, owning bundle
        /// LAST), preserving the post-order invariant <c>BundleAssetExtractor.FindLoadedBundle</c> relies on.
        /// Entries with no asset-level data (null/empty <c>dependencyBundles</c>) are left <c>null</c>; the
        /// runtime fails those loads in <see cref="TryGetLocations"/> instead of falling back to bundle-level.
        /// </summary>
        private void BuildAssetFlatDepsCache()
        {
            int count = _schema.assetRecords.Count;
            _assetFlatDepsCache = new IReadOnlyList<ResourceLocation>[count];

            int missing = 0;
            for (int i = 0; i < count; i++)
            {
                var deps = _schema.assetRecords[i].dependencyBundles;
                if (deps == null || deps.Count == 0)
                {
                    _assetFlatDepsCache[i] = null;
                    missing++;
                    continue;
                }

                var arr = new ResourceLocation[deps.Count];
                bool valid = true;
                for (int d = 0; d < deps.Count; d++)
                {
                    int bi = deps[d];
                    if (bi < 0 || bi >= _bundleLocationCache.Length || _bundleLocationCache[bi] == null)
                    {
                        valid = false;
                        break;
                    }
                    arr[d] = _bundleLocationCache[bi];
                }

                _assetFlatDepsCache[i] = valid ? arr : null;
                if (!valid) missing++;
            }

            if (missing > 0)
            {
                HCLogger.LogWarn($"[HC.Catalog] {missing}/{count} asset records have no usable asset-level " +
                    "dependency bundles — those loads will fail (asset-level loading, no bundle-level fallback).");
            }
        }

        /// <summary>
        /// Allocate one shared ResourceLocation per bundleRecord (type-independent).
        /// These instances are reused across all TryGetLocations calls, eliminating
        /// per-call ResourceLocation allocations for bundle dependencies.
        /// </summary>
        private void BuildBundleLocationCache()
        {
            int count = _schema.bundleRecords.Count;
            _bundleLocationCache = new ResourceLocation[count];

            for (int i = 0; i < count; i++)
            {
                var rec = _schema.bundleRecords[i];
                string bundleName = GetString(rec.bundleNameIndex);
                if (string.IsNullOrEmpty(bundleName))
                {
                    _bundleLocationCache[i] = null;
                    continue;
                }

                bool isRemote = rec.contentLocation == (int)ContentLocation.Remote;
                string providerId = isRemote ? PROVIDER_REMOTE_BUNDLE : PROVIDER_BUNDLE_FILE;
                // Remote: internalId = extensionless CDN-relative path (same as Address when flat); HTTP adds .bundle.
                // Local: internalId = catalog bundleName (extensionless); BundleFileProvider adds .bundle for disk IO.
                string internalId = isRemote
                    ? ResolveRemoteRelativePathForRecord(rec, bundleName)
                    : bundleName;

                _bundleLocationCache[i] = new ResourceLocation(
                    address: bundleName,
                    internalId: internalId,
                    providerId: providerId,
                    resourceType: typeof(AssetBundle),
                    data: rec.bundleHash);
            }
        }

        /// <summary>
        /// For each bundle, pre-compute the flat transitive dependency list (post-order, self last)
        /// as an immutable array. Reused across every asset that lives in that bundle, so each
        /// TryGetLocations becomes an O(1) dictionary/array hit with zero DFS or HashSet allocation.
        /// </summary>
        private void BuildBundleFlatDepsCache()
        {
            int count = _schema.bundleRecords.Count;
            _bundleFlatDepsCache = new IReadOnlyList<ResourceLocation>[count];

            var scratch = new List<ResourceLocation>(8);
            var visited = new HashSet<int>();

            for (int i = 0; i < count; i++)
            {
                scratch.Clear();
                visited.Clear();
                CollectBundlesRecursive(i, scratch, visited);

                if (scratch.Count == 0)
                {
                    _bundleFlatDepsCache[i] = Array.Empty<ResourceLocation>();
                }
                else
                {
                    var arr = new ResourceLocation[scratch.Count];
                    scratch.CopyTo(arr);
                    _bundleFlatDepsCache[i] = arr;
                }
            }
        }

        private string[] BuildBundleDependencyNames(CatalogSchema.BundleRecordEntry rec)
        {
            if (rec.dependencies == null || rec.dependencies.Count == 0)
                return Array.Empty<string>();

            var deps = new string[rec.dependencies.Count];
            for (int i = 0; i < rec.dependencies.Count; i++)
            {
                int di = rec.dependencies[i];
                if (di >= 0 && di < _schema.bundleRecords.Count)
                    deps[i] = GetString(_schema.bundleRecords[di].bundleNameIndex);
            }
            return deps;
        }

        /// <summary>
        /// Resolve address to assetRecords index. O(1) Dictionary lookup, zero hash computation.
        /// Tries GUID first (32-char hex), then Name.
        /// </summary>
        private int ResolveAddressToAssetIndex(string address)
        {
            if (address.Length == 32 && IsHex(address))
            {
                if (_guidToAssetIndex != null && _guidToAssetIndex.TryGetValue(address, out int idx))
                    return idx;
            }

            if (_nameToAssetIndex != null && _nameToAssetIndex.TryGetValue(address, out int nameIdx))
                return nameIdx;

            return -1;
        }

        /// <summary>
        /// Post-order DFS that appends the shared bundle ResourceLocation (from <see cref="_bundleLocationCache"/>)
        /// for <paramref name="index"/> and all of its transitive dependencies into <paramref name="result"/>.
        /// Only used during one-shot BuildBundleFlatDepsCache(); hot path (TryGetLocations) never walks this.
        /// </summary>
        private void CollectBundlesRecursive(int index, List<ResourceLocation> result, HashSet<int> visited)
        {
            if (index < 0 || index >= _schema.bundleRecords.Count || !visited.Add(index))
                return;

            var rec = _schema.bundleRecords[index];

            if (rec.dependencies != null)
            {
                for (int i = 0; i < rec.dependencies.Count; i++)
                {
                    int dep = rec.dependencies[i];
                    CollectBundlesRecursive(dep, result, visited);
                }
            }

            // Diagnostic override: pull in any extra dependencies registered via
            // DiagnosticExtraDependencies. Intentionally resolved lazily per bundle so
            // the override applies to both the root bundle being flattened and any of
            // its (direct/indirect) dependencies.
            if (DiagnosticExtraDependencies.Count > 0)
            {
                string bundleName = GetString(rec.bundleNameIndex);
                if (!string.IsNullOrEmpty(bundleName)
                    && DiagnosticExtraDependencies.TryGetValue(bundleName, out var extras)
                    && extras != null)
                {
                    for (int i = 0; i < extras.Length; i++)
                    {
                        var extraName = extras[i];
                        if (string.IsNullOrEmpty(extraName)) continue;
                        if (_bundleNameToRecordIndex != null
                            && _bundleNameToRecordIndex.TryGetValue(extraName, out int extraIdx))
                        {
                            CollectBundlesRecursive(extraIdx, result, visited);
                        }
                        else
                        {
                            HCLogger.LogWarn($"[HC.Catalog] DiagnosticExtraDependencies: " +
                                $"bundle '{extraName}' (extra dep of '{bundleName}') not found — skipped");
                        }
                    }
                }
            }

            var loc = _bundleLocationCache[index];
            if (loc != null)
                result.Add(loc);
        }

        private string GetString(int index)
        {
            if (_schema?.stringTable == null || index < 0 || index >= _schema.stringTable.Length)
                return null;
            return _schema.stringTable[index];
        }

        private string ResolveRemoteRelativePathForRecord(CatalogSchema.BundleRecordEntry rec, string bundleName)
        {
            if (rec.remoteRelativePathIndex >= 0 && rec.remoteRelativePathIndex < _schema.stringTable.Length)
            {
                string path = GetString(rec.remoteRelativePathIndex);
                if (!string.IsNullOrEmpty(path))
                {
                    NamingRules.RequireCatalogBundleRelativePath(path,
                        $"bundleRecords remoteRelativePathIndex={rec.remoteRelativePathIndex}");
                    return path.Trim();
                }
            }

            if (!string.IsNullOrEmpty(bundleName))
                NamingRules.RequireCatalogBundleRelativePath(bundleName, "remote path fallback (bundle name)");
            return string.IsNullOrEmpty(bundleName) ? bundleName : bundleName.Trim();
        }

        private static bool IsHex(string s)
        {
            foreach (char c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }
    }
}
