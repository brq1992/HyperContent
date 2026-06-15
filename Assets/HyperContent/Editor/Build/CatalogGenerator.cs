using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using com.igg.hypercontent.runtime;
using com.igg.hypercontent.shared;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Generates single catalog: stringTable + AssetRecord + NameAlias + BundleRecord.
    /// Owns all catalog serialization — other classes must use Serialize/Deserialize
    /// instead of calling JsonUtility directly, so the format can be swapped in one place.
    /// </summary>
    public static class CatalogGenerator
    {
        /// <summary>
        /// 按显式 <paramref name="pFormat"/> 序列化 <see cref="CatalogSchema"/>。
        /// 写入端单一入口；与读取端 <c>LocalContentCatalog.Initialize(source, format)</c> 严格对称。
        ///
        /// 压缩在 dispatcher 层（这里）完成，不污染 <see cref="CatalogBinaryWriter"/>，
        /// 未来扩展 LZ4 / Zstd 等只需新增 case + 引入对应库。
        /// </summary>
        public static byte[] Serialize(CatalogSchema pCatalog, CatalogSerializationFormat pFormat)
        {
            switch (pFormat)
            {
                case CatalogSerializationFormat.Binary:
                    return CatalogBinaryWriter.Write(pCatalog);

                case CatalogSerializationFormat.BinaryGzip:
                    return GzipCompress(CatalogBinaryWriter.Write(pCatalog));

                default:  // Json
                    return Encoding.UTF8.GetBytes(JsonUtility.ToJson(pCatalog, true));
            }
        }

        /// <summary>
        /// 与 <see cref="Serialize"/> 对称的反序列化（Editor 自检 round-trip 用，运行时走
        /// <c>LocalContentCatalog</c>）。
        /// </summary>
        public static CatalogSchema Deserialize(byte[] pData, CatalogSerializationFormat pFormat)
        {
            if (pData == null || pData.Length == 0) return null;

            switch (pFormat)
            {
                case CatalogSerializationFormat.Binary:
                    return CatalogBinaryReader.Read(pData);

                case CatalogSerializationFormat.BinaryGzip:
                    return CatalogBinaryReader.Read(GzipDecompress(pData));

                default:  // Json
                {
                    var json = Encoding.UTF8.GetString(pData);
                    return JsonUtility.FromJson<CatalogSchema>(json);
                }
            }
        }

        private static byte[] GzipCompress(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                // 全限定 System.IO.Compression.CompressionLevel —— UnityEngine 也有同名类型，
                // 这里要的是 BCL GZipStream 接受的那个。
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                    gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
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
        /// Generate catalog: stringTable + AssetRecord + NameAlias + BundleRecord for O(log n) lookup and hot-update (catalogHash).
        /// </summary>
        public static bool GenerateCatalog(BuildContext context)
        {
            try
            {
                var bundleDir = context.Config.BundleOutputDirectory;
                var catalogDir = context.Config.CatalogOutputDirectory;
                if (!Directory.Exists(catalogDir))
                    Directory.CreateDirectory(catalogDir);
                var stringList = new List<string>();
                var stringToIndex = new Dictionary<string, int>();

                int GetOrAddString(string s)
                {
                    if (string.IsNullOrEmpty(s)) return -1;
                    if (stringToIndex.TryGetValue(s, out var idx)) return idx;
                    idx = stringList.Count;
                    stringList.Add(s);
                    stringToIndex[s] = idx;
                    return idx;
                }

                // Bundle order: same as BundleToAssets iteration
                var bundleNames = context.BundleToAssets.Keys.ToList();
                var bundleNameToIndex = new Dictionary<string, int>();
                for (int i = 0; i < bundleNames.Count; i++)
                {
                    GetOrAddString(bundleNames[i]);
                    bundleNameToIndex[bundleNames[i]] = i;
                }

                // Build guid→key dictionary upfront for O(1) reverse lookup in nameAlias generation.
                // Use lowercase GUID as key to match the lowercased guid stored in assetRecords.
                var guidToKey = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kvp in context.KeyToGuid)
                    guidToKey[kvp.Value.ToLowerInvariant()] = kvp.Key;

                // Asset records: guid (string), bundleIndex, assetPathIndex. Sort by guid for binary search.
                var assetList = new List<CatalogSchema.AssetRecordEntry>();
                foreach (var kvp in context.KeyToGuid)
                {
                    var guidStr = kvp.Value;
                    if (!IsValid32HexGuid(guidStr))
                        continue;
                    if (!context.AssetToBundle.TryGetValue(guidStr, out var bundleName))
                        continue;
                    if (!context.GuidToPath.TryGetValue(guidStr, out var assetPath))
                        continue;
                    if (!bundleNameToIndex.TryGetValue(bundleName, out var bundleIndex))
                        continue;

                    var assetPathIndex = GetOrAddString(BundleAssetInternalId.FromAssetPath(assetPath));
                    var depBundleIndices = ResolveAssetDependencyBundleIndices(
                        context, guidStr, bundleName, bundleNameToIndex);
                    assetList.Add(new CatalogSchema.AssetRecordEntry
                    {
                        guid = guidStr.ToLowerInvariant(),
                        bundleIndex = bundleIndex,
                        assetPathIndex = assetPathIndex,
                        dependencyBundles = depBundleIndices
                    });
                }
                assetList.Sort((a, b) => string.CompareOrdinal(a.guid, b.guid));

                // Name aliases: nameHash → assetRecordIndex. Sort by nameHash for binary search.
                var nameAliasList = new List<CatalogSchema.NameAliasEntry>();
                for (int i = 0; i < assetList.Count; i++)
                {
                    if (!guidToKey.TryGetValue(assetList[i].guid, out var name))
                        continue;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (IsValid32HexGuid(name)) continue;

                    var nameHash = NameHashUtil.Compute(name);
                    if (string.IsNullOrEmpty(nameHash)) continue;

                    nameAliasList.Add(new CatalogSchema.NameAliasEntry
                    {
                        nameStringIndex = GetOrAddString(name),
                        nameHash = nameHash,
                        guidIndex = i
                    });
                }
                nameAliasList.Sort((a, b) => string.CompareOrdinal(a.nameHash, b.nameHash));

                // Bundle records: bundleNameIndex, bundleHash, size, dependencies (indices), assetCount, bundleTagFlags.
                var bundleTagByName = BuildBundleTagFlagsByBundleName(context, bundleNames, null);
                var bundleRecordsList = new List<CatalogSchema.BundleRecordEntry>();
                for (int bi = 0; bi < bundleNames.Count; bi++)
                {
                    var bundleName = bundleNames[bi];
                    var bundleNameIndex = GetOrAddString(bundleName);
                    string bundleFileName;
                    if (context.ExpectedToActualBundleName != null && context.ExpectedToActualBundleName.TryGetValue(bundleName, out var actualName))
                        bundleFileName = actualName;
                    else
                        bundleFileName = bundleName.EndsWith(".bundle") ? bundleName : bundleName + ".bundle";
                    var bundlePath = Path.Combine(bundleDir, bundleFileName).Replace("\\", "/");
                    if (!File.Exists(bundlePath))
                        bundlePath = FindActualBundleFile(bundleDir, bundleName) ?? bundlePath;
                    var size = File.Exists(bundlePath) ? new FileInfo(bundlePath).Length : 0L;
                    var bundleHash = File.Exists(bundlePath) ? CalculateFileHash(bundlePath) : "";
                    var depNames = context.BundleDependencies.TryGetValue(bundleName, out var deps) ? deps : new HashSet<string>();
                    var depIndices = new List<int>();
                    foreach (var d in depNames)
                    {
                        if (bundleNameToIndex.TryGetValue(d, out int depIdx))
                        {
                            depIndices.Add(depIdx);
                        }
                        else
                        {
                            Debug.LogWarning($"[HyperContent] CatalogGenerator: bundle '{bundleName}' dependency '{d}' " +
                                             "not found in bundleNameToIndex — dropped from catalog");
                        }
                    }
                    var assetCount = context.BundleToAssets.TryGetValue(bundleName, out var guids) ? guids.Count : 0;
                    bundleRecordsList.Add(new CatalogSchema.BundleRecordEntry
                    {
                        bundleNameIndex = bundleNameIndex,
                        bundleHash = bundleHash,
                        size = size,
                        dependencies = depIndices,
                        assetCount = assetCount,
                        contentLocation = (int)ContentLocation.StreamingAssets,
                        bundleTagFlags = (int)bundleTagByName[bundleName],
                        remoteRelativePathIndex = -1
                    });
                }

                var catalogNameIndex = GetOrAddString(HyperContentPaths.LOCAL_CATALOG_NAME);

                var catalog = new CatalogSchema
                {
                    schemaVersion = CatalogSchema.CurrentSchemaVersion,
                    catalogNameIndex = catalogNameIndex,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    stringTable = stringList.ToArray(),
                    assetRecords = assetList,
                    nameAliases = nameAliasList,
                    bundleRecords = bundleRecordsList
                };

                // catalogHash 是对 schema 内容的指纹，与外层封装格式无关：
                // 两轮 Serialize 都按同一 format 走，保持 hot-update hash 比对稳定。
                var format = context.Config.catalogFormat;
                var bytes = Serialize(catalog, format);
                catalog.catalogHash = ComputeCatalogHash(bytes);
                bytes = Serialize(catalog, format);

                var catalogPath = Path.Combine(catalogDir, HyperContentPaths.LOCAL_CATALOG_FILENAME);
                File.WriteAllBytes(catalogPath, bytes);
                Debug.Log($"[HyperContent] Catalog generated (format={format}): {catalogPath} — " +
                    $"{assetList.Count} assets, {nameAliasList.Count} nameAliases, " +
                    $"{bundleRecordsList.Count} bundles, {stringList.Count} strings, " +
                    $"size={bytes.Length} bytes");
                return true;
            }
            catch (Exception e)
            {
                context.Errors.Add(new BuildError($"Failed to generate catalog: {e.Message}"));
                Debug.LogError($"[HyperContent] Catalog generation failed: {e}");
                return false;
            }
        }

        /// <summary>
        /// Resolve an asset's per-asset dependency bundle list (from <see cref="BuildContext.AssetDependencyBundles"/>,
        /// keyed by lowercase GUID) into catalog bundle indices for <c>AssetRecordEntry.dependencyBundles</c>.
        /// Preserves order (owning bundle LAST). Returns <c>null</c> when the asset has no computed asset-level
        /// data — the runtime then fails that load with <c>ErrorCode.CATALOG_ASSET_DEPS_MISSING</c> (no fallback).
        /// </summary>
        private static List<int> ResolveAssetDependencyBundleIndices(
            BuildContext pContext,
            string pGuidStr,
            string pOwningBundleName,
            Dictionary<string, int> pBundleNameToIndex)
        {
            if (pContext?.AssetDependencyBundles == null)
                return null;
            if (!pContext.AssetDependencyBundles.TryGetValue(pGuidStr.ToLowerInvariant(), out var depNames)
                || depNames == null || depNames.Count == 0)
                return null;

            var indices = new List<int>(depNames.Count);
            foreach (var name in depNames)
            {
                if (pBundleNameToIndex.TryGetValue(name, out int idx))
                {
                    indices.Add(idx);
                }
                else
                {
                    Debug.LogWarning($"[HyperContent] CatalogGenerator: asset '{pGuidStr}' dependency bundle '{name}' " +
                                     "not found in bundleNameToIndex — dropped from asset-level deps");
                }
            }

            return indices.Count > 0 ? indices : null;
        }

        /// <summary>
        /// Maps label strings (e.g. Addressable entry labels) to <see cref="BundleTagFlags"/> using the same rules as <see cref="HyperContentAsset.labels"/> in catalog generation.
        /// </summary>
        public static BundleTagFlags BundleTagFlagsFromLabelSet(IEnumerable<string> pLabels)
        {
            if (pLabels == null)
                return BundleTagFlags.None;
            var flags = BundleTagFlags.None;
            foreach (var l in pLabels)
            {
                if (string.IsNullOrEmpty(l))
                    continue;
                if (string.Equals(l.Trim(), "blocking", StringComparison.OrdinalIgnoreCase))
                    flags |= BundleTagFlags.Blocking;
            }
            return flags;
        }

        /// <summary>
        /// Per-bundle <see cref="BundleTagFlags"/>:
        /// (1) <see cref="BuildContext.BundleTagFlagsFromPlan"/> from <see cref="IBundleGroupingTool"/> (e.g. Addressable group entry labels),
        /// (2) OR across asset GUIDs that have a <see cref="HyperContentAsset"/> marker requesting Blocking.
        /// Only <see cref="BundleTagFlags.None"/> and <see cref="BundleTagFlags.Blocking"/> are used today.
        /// Update Build <c>SingleBundle</c>: one physical bundle contains many groups' entries — same OR rule applies.
        /// When <paramref name="pMergeGuidsFromManifest"/> is set (Update Build), manifest per-bundle GUID lists are merged with
        /// <see cref="BuildContext.BundleToAssets"/> so unchanged StreamingAssets bundles still resolve tags.
        /// </summary>
        public static Dictionary<string, BundleTagFlags> BuildBundleTagFlagsByBundleName(
            BuildContext pContext,
            List<string> pBundleNamesOrdered,
            BuildManifest pMergeGuidsFromManifest)
        {
            var map = new Dictionary<string, BundleTagFlags>(StringComparer.Ordinal);
            foreach (var name in pBundleNamesOrdered)
                map[name] = BundleTagFlags.None;

            if (pContext?.BundleTagFlagsFromPlan != null)
            {
                foreach (var name in pBundleNamesOrdered)
                {
                    if (pContext.BundleTagFlagsFromPlan.TryGetValue(name, out var fromPlan))
                        map[name] |= fromPlan;
                }
            }

            if (pContext?.AssetMarkers == null)
                return map;

            var mergedBundleToGuids = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            void AddGuids(string pBundleName, IEnumerable<string> pGuids)
            {
                if (string.IsNullOrEmpty(pBundleName) || pGuids == null)
                    return;
                if (!mergedBundleToGuids.TryGetValue(pBundleName, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    mergedBundleToGuids[pBundleName] = set;
                }
                foreach (var g in pGuids)
                {
                    if (string.IsNullOrEmpty(g))
                        continue;
                    set.Add(g);
                }
            }

            if (pContext.BundleToAssets != null)
            {
                foreach (var kvp in pContext.BundleToAssets)
                    AddGuids(kvp.Key, kvp.Value);
            }

            if (pMergeGuidsFromManifest?.cachedBundles != null)
            {
                foreach (var cb in pMergeGuidsFromManifest.cachedBundles)
                    AddGuids(cb.bundleName, cb.assetGuids);
            }

            foreach (var bundleName in pBundleNamesOrdered)
            {
                if (!mergedBundleToGuids.TryGetValue(bundleName, out var guids))
                    continue;
                var flags = map[bundleName];
                foreach (var guid in guids)
                {
                    if (TryGetAssetMarker(pContext.AssetMarkers, guid, out var marker) && MarkerRequestsBlockingBundle(marker))
                        flags |= BundleTagFlags.Blocking;
                }
                map[bundleName] = flags;
            }

            return map;
        }

        private static bool TryGetAssetMarker(Dictionary<string, HyperContentAsset> pMarkers, string pGuid, out HyperContentAsset pMarker)
        {
            pMarker = null;
            if (pMarkers == null || string.IsNullOrEmpty(pGuid))
                return false;
            if (pMarkers.TryGetValue(pGuid, out pMarker))
                return true;
            var lower = pGuid.ToLowerInvariant();
            if (lower != pGuid && pMarkers.TryGetValue(lower, out pMarker))
                return true;
            var upper = pGuid.ToUpperInvariant();
            if (upper != pGuid && pMarkers.TryGetValue(upper, out pMarker))
                return true;
            return false;
        }

        private static bool MarkerRequestsBlockingBundle(HyperContentAsset pMarker)
        {
            if (pMarker == null)
                return false;
            if (pMarker.markBundleBlocking)
                return true;
            if (pMarker.labels == null)
                return false;
            foreach (var label in pMarker.labels)
            {
                if (string.IsNullOrEmpty(label))
                    continue;
                if (string.Equals(label.Trim(), "blocking", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsValid32HexGuid(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 32) return false;
            for (int i = 0; i < 32; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        private static string CalculateFileHash(string filePath)
        {
            try
            {
                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    var hash = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HyperContent] Failed to calculate hash for {filePath}: {e.Message}");
                return "";
            }
        }

        private static string FindActualBundleFile(string outputDir, string bundleName)
        {
            if (!Directory.Exists(outputDir))
                return null;

            var exactPath = Path.Combine(outputDir, bundleName + ".bundle").Replace("\\", "/");
            if (File.Exists(exactPath))
                return exactPath;

            var noExtPath = Path.Combine(outputDir, bundleName).Replace("\\", "/");
            if (File.Exists(noExtPath))
                return noExtPath;

            try
            {
                var files = Directory.GetFiles(outputDir, bundleName + "*", SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.EndsWith(".manifest") || fileName.EndsWith(".catalog.json"))
                        continue;
                    return file.Replace("\\", "/");
                }
            }
            catch
            {
                // Ignore
            }

            return null;
        }

        /// <summary>SHA256 hash as 64-char lowercase hex string for catalogHash.</summary>
        private static string ComputeCatalogHash(byte[] pData)
        {
            if (pData == null || pData.Length == 0) return "";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(pData);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
