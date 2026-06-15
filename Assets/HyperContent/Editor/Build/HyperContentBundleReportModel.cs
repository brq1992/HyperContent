using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// In-memory snapshot for the Bundle Report window.
    /// Primary source: <see cref="BuildReportData"/> JSON (SBP-derived deps, matches runtime catalog).
    /// Legacy fallback: <see cref="BuildManifest"/> JSON (asset-level deps reconstruction).
    /// </summary>
    public sealed class HyperContentBundleReportSnapshot
    {
        public string SourcePath { get; private set; }
        public string BuildVersion { get; private set; }
        public long BuildTimestampUnix { get; private set; }

        public IReadOnlyList<HyperContentBundleReportEntry> Bundles => _bundles;

        private readonly List<HyperContentBundleReportEntry> _bundles;

        private HyperContentBundleReportSnapshot(
            string pSourcePath,
            string pBuildVersion,
            long pBuildTimestampUnix,
            List<HyperContentBundleReportEntry> pBundles)
        {
            SourcePath = pSourcePath;
            BuildVersion = pBuildVersion ?? "";
            BuildTimestampUnix = pBuildTimestampUnix;
            _bundles = pBundles ?? new List<HyperContentBundleReportEntry>();
        }

        /// <summary>
        /// Load from build_report.json (preferred). Dependencies match runtime catalog exactly.
        /// </summary>
        public static HyperContentBundleReportSnapshot FromReportJsonFile(string pAbsolutePath)
        {
            if (string.IsNullOrEmpty(pAbsolutePath))
                throw new ArgumentException("Path is empty.", nameof(pAbsolutePath));
            var normalized = pAbsolutePath.Replace("\\", "/");
            if (!File.Exists(normalized))
                throw new FileNotFoundException("Report file not found.", normalized);

            var json = File.ReadAllText(normalized);
            var data = JsonUtility.FromJson<BuildReportData>(json);
            if (data == null || data.bundles == null)
                throw new InvalidOperationException("Invalid or empty build report JSON.");

            return FromReportData(normalized, data);
        }

        /// <summary>
        /// Build snapshot from <see cref="BuildReportData"/>. Two outgoing/incoming graphs are
        /// reconstructed in parallel:
        /// <list type="bullet">
        ///   <item><description>"transitive" (catalog-shipped) from
        ///   <see cref="BuildReportBundleEntry.bundleDependencies"/>.</description></item>
        ///   <item><description>"direct" (one-hop, diagnostic) from
        ///   <see cref="BuildReportBundleEntry.bundleDirectDependencies"/>.</description></item>
        /// </list>
        /// Both feed the entry so callers can compare against Addressables'
        /// <c>buildlayout.json Dependencies</c>/<c>DependentBundles</c> on the same footing.
        /// </summary>
        public static HyperContentBundleReportSnapshot FromReportData(string pSourcePath, BuildReportData pData)
        {
            var outgoingDict = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var incomingDict = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var outgoingDirectDict = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var incomingDirectDict = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var bundle in pData.bundles)
            {
                if (bundle == null || string.IsNullOrEmpty(bundle.bundleName)) continue;

                var sortedDeps = bundle.bundleDependencies ?? new List<string>();
                outgoingDict[bundle.bundleName] = sortedDeps;
                foreach (var dep in sortedDeps)
                {
                    if (!incomingDict.TryGetValue(dep, out var fromSet))
                    {
                        fromSet = new HashSet<string>(StringComparer.Ordinal);
                        incomingDict[dep] = fromSet;
                    }
                    fromSet.Add(bundle.bundleName);
                }

                var sortedDirectDeps = bundle.bundleDirectDependencies ?? new List<string>();
                outgoingDirectDict[bundle.bundleName] = sortedDirectDeps;
                foreach (var dep in sortedDirectDeps)
                {
                    if (!incomingDirectDict.TryGetValue(dep, out var fromSet))
                    {
                        fromSet = new HashSet<string>(StringComparer.Ordinal);
                        incomingDirectDict[dep] = fromSet;
                    }
                    fromSet.Add(bundle.bundleName);
                }
            }

            var entries = new List<HyperContentBundleReportEntry>();
            foreach (var bundle in pData.bundles)
            {
                if (bundle == null || string.IsNullOrEmpty(bundle.bundleName)) continue;

                var assetPaths = new List<string>();
                if (bundle.assets != null)
                {
                    foreach (var asset in bundle.assets)
                    {
                        if (asset != null && !string.IsNullOrEmpty(asset.assetPath))
                            assetPaths.Add(asset.assetPath);
                    }
                }

                outgoingDict.TryGetValue(bundle.bundleName, out var refsTo);
                incomingDict.TryGetValue(bundle.bundleName, out var refsBySet);
                var refsBy = refsBySet != null ? refsBySet.OrderBy(s => s, StringComparer.Ordinal).ToList() : new List<string>();

                outgoingDirectDict.TryGetValue(bundle.bundleName, out var refsToDirect);
                incomingDirectDict.TryGetValue(bundle.bundleName, out var refsByDirectSet);
                var refsByDirect = refsByDirectSet != null
                    ? refsByDirectSet.OrderBy(s => s, StringComparer.Ordinal).ToList()
                    : new List<string>();

                entries.Add(new HyperContentBundleReportEntry(
                    bundle.bundleName,
                    bundle.sizeBytes,
                    assetPaths,
                    refsTo ?? new List<string>(),
                    refsBy,
                    refsToDirect ?? new List<string>(),
                    refsByDirect));
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.BundleName, b.BundleName));

            return new HyperContentBundleReportSnapshot(
                pSourcePath,
                pData.buildVersion,
                pData.buildTimestamp,
                entries);
        }

        /// <summary>
        /// Legacy: load from build_manifest.json. Dependencies are reconstructed from asset-level deps
        /// and may differ from runtime catalog.
        /// </summary>
        public static HyperContentBundleReportSnapshot FromManifestJsonFile(string pAbsolutePath)
        {
            if (string.IsNullOrEmpty(pAbsolutePath))
                throw new ArgumentException("Path is empty.", nameof(pAbsolutePath));
            var normalized = pAbsolutePath.Replace("\\", "/");
            if (!File.Exists(normalized))
                throw new FileNotFoundException("Manifest file not found.", normalized);

            var json = File.ReadAllText(normalized);
            var manifest = JsonUtility.FromJson<BuildManifest>(json);
            if (manifest == null || manifest.cachedBundles == null || manifest.cachedAssets == null)
                throw new InvalidOperationException("Invalid or empty build manifest JSON.");

            return FromManifest(normalized, manifest);
        }

        /// <summary>
        /// Legacy: build snapshot from an in-memory manifest.
        /// </summary>
        public static HyperContentBundleReportSnapshot FromManifest(string pSourcePath, BuildManifest pManifest)
        {
            var guidToBundleDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in pManifest.cachedAssets)
            {
                if (asset == null || string.IsNullOrEmpty(asset.guid)) continue;
                guidToBundleDict[asset.guid.Trim()] = asset.bundleName;
            }

            var outgoingDict = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var asset in pManifest.cachedAssets)
            {
                if (asset == null || string.IsNullOrEmpty(asset.bundleName)) continue;
                var fromBundle = asset.bundleName;
                if (!outgoingDict.TryGetValue(fromBundle, out var toSet))
                {
                    toSet = new HashSet<string>(StringComparer.Ordinal);
                    outgoingDict[fromBundle] = toSet;
                }

                if (asset.dependencies == null) continue;
                foreach (var dep in asset.dependencies)
                {
                    if (dep == null || string.IsNullOrEmpty(dep.guid)) continue;
                    if (!guidToBundleDict.TryGetValue(dep.guid.Trim(), out var toBundle))
                        continue;
                    if (string.Equals(toBundle, fromBundle, StringComparison.Ordinal))
                        continue;
                    toSet.Add(toBundle);
                }
            }

            var incomingDict = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var kvp in outgoingDict)
            {
                var from = kvp.Key;
                foreach (var to in kvp.Value)
                {
                    if (!incomingDict.TryGetValue(to, out var fromSet))
                    {
                        fromSet = new HashSet<string>(StringComparer.Ordinal);
                        incomingDict[to] = fromSet;
                    }
                    fromSet.Add(from);
                }
            }

            var entries = new List<HyperContentBundleReportEntry>();
            foreach (var cb in pManifest.cachedBundles)
            {
                if (cb == null || string.IsNullOrEmpty(cb.bundleName)) continue;

                var bundleName = cb.bundleName;
                outgoingDict.TryGetValue(bundleName, out var outgoingSet);
                incomingDict.TryGetValue(bundleName, out var incomingSet);

                var assetPathList = new List<string>();
                if (cb.assetGuids != null)
                {
                    foreach (var guid in cb.assetGuids)
                    {
                        if (string.IsNullOrEmpty(guid)) continue;
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        if (string.IsNullOrEmpty(path))
                            path = $"[{guid}] (missing)";
                        assetPathList.Add(path);
                    }
                    assetPathList.Sort(StringComparer.OrdinalIgnoreCase);
                }

                // Manifest-based fallback: only the asset-level (path) graph is available,
                // which is closer to "direct" than to SBP transitive closure. We surface it as
                // both lists so the Bundle Report window still has data to render — callers
                // who specifically need post-SBP truth must use FromReportJsonFile.
                entries.Add(new HyperContentBundleReportEntry(
                    bundleName,
                    cb.size,
                    assetPathList,
                    SortSet(outgoingSet),
                    SortSet(incomingSet),
                    SortSet(outgoingSet),
                    SortSet(incomingSet)));
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.BundleName, b.BundleName));

            return new HyperContentBundleReportSnapshot(
                pSourcePath,
                pManifest.buildVersion,
                pManifest.buildTimestamp,
                entries);
        }

        private static List<string> SortSet(HashSet<string> pSet)
        {
            if (pSet == null || pSet.Count == 0)
                return new List<string>();
            var list = pSet.ToList();
            list.Sort(StringComparer.Ordinal);
            return list;
        }
    }

    /// <summary>
    /// One row in the report: one bundle, its assets, and both views of its bundle-level
    /// dependency edges.
    /// </summary>
    /// <remarks>
    /// Two parallel edge sets are stored per bundle:
    /// <list type="bullet">
    ///   <item><description><see cref="RefsToBundleNames"/> / <see cref="RefsByBundleNames"/>
    ///   — transitive closure (matches the runtime catalog's
    ///   <c>BundleRecordEntry.dependencies</c>; same source as SBP
    ///   <c>BundleDetails.Dependencies</c>).</description></item>
    ///   <item><description><see cref="RefsToDirectBundleNames"/> /
    ///   <see cref="RefsByDirectBundleNames"/> — one-hop only (matches Addressables'
    ///   <c>buildlayout.json Dependencies</c>/<c>DependentBundles</c>; same source as SBP
    ///   <c>IBundleWriteData.AssetToFiles</c>+<c>FileToBundle</c>).</description></item>
    /// </list>
    /// </remarks>
    public sealed class HyperContentBundleReportEntry
    {
        public string BundleName { get; }
        public long SizeBytes { get; }
        public IReadOnlyList<string> AssetPaths { get; }
        public IReadOnlyList<string> RefsToBundleNames { get; }
        public IReadOnlyList<string> RefsByBundleNames { get; }
        public IReadOnlyList<string> RefsToDirectBundleNames { get; }
        public IReadOnlyList<string> RefsByDirectBundleNames { get; }

        public int AssetCount => AssetPaths.Count;
        public int RefsToCount => RefsToBundleNames.Count;
        public int RefsByCount => RefsByBundleNames.Count;
        public int RefsToDirectCount => RefsToDirectBundleNames.Count;
        public int RefsByDirectCount => RefsByDirectBundleNames.Count;

        public HyperContentBundleReportEntry(
            string pBundleName,
            long pSizeBytes,
            IReadOnlyList<string> pAssetPaths,
            IReadOnlyList<string> pRefsToBundleNames,
            IReadOnlyList<string> pRefsByBundleNames,
            IReadOnlyList<string> pRefsToDirectBundleNames = null,
            IReadOnlyList<string> pRefsByDirectBundleNames = null)
        {
            BundleName = pBundleName;
            SizeBytes = pSizeBytes;
            AssetPaths = pAssetPaths ?? Array.Empty<string>();
            RefsToBundleNames = pRefsToBundleNames ?? Array.Empty<string>();
            RefsByBundleNames = pRefsByBundleNames ?? Array.Empty<string>();
            RefsToDirectBundleNames = pRefsToDirectBundleNames ?? Array.Empty<string>();
            RefsByDirectBundleNames = pRefsByDirectBundleNames ?? Array.Empty<string>();
        }
    }
}
