using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Generates build report: console summary + persistent JSON for the Bundle Report window.
    /// The JSON uses SBP-derived bundle dependencies (same source as runtime catalog).
    /// </summary>
    public static class BuildReportGenerator
    {
        public const string REPORT_DIRECTORY_NAME = "BuildReports";
        public const string REPORT_FILENAME = "build_report.json";

        /// <summary>
        /// Generate in-memory report, log summary, and write persistent JSON.
        /// </summary>
        public static void GenerateReport(BuildContext context)
        {
            if (context.Report == null)
                context.Report = new BuildReport();

            var report = context.Report;

            report.TotalAssets = context.AssetMarkers.Count;
            report.TotalBundles = context.BundleToAssets.Count;

            GenerateAssetAggregation(context, report);
            GenerateDuplicateDependencies(context, report);
            LogReportSummary(context, report);

            SaveReportJson(context);
        }

        /// <summary>
        /// Build report output directory: {buildOutputRoot}/{Platform}/BuildReports/
        /// </summary>
        public static string GetReportDirectory(BuildConfig pConfig)
        {
            return Path.Combine(pConfig.PlatformOutputDirectory, REPORT_DIRECTORY_NAME);
        }

        /// <summary>
        /// Full path to the report JSON file.
        /// </summary>
        public static string GetReportPath(BuildConfig pConfig)
        {
            return Path.Combine(GetReportDirectory(pConfig), REPORT_FILENAME);
        }

        private static void SaveReportJson(BuildContext context)
        {
            try
            {
                var bundleDir = context.Config.BundleOutputDirectory;
                var data = new BuildReportData
                {
                    buildVersion = context.Config.ResolvedBuildVersion,
                    buildTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    buildDurationMs = context.Report?.BuildDurationMs ?? 0
                };

                foreach (var kvp in context.BundleToAssets)
                {
                    var bundleName = kvp.Key;
                    var assetGuids = kvp.Value;

                    string bundleFileName;
                    if (context.ExpectedToActualBundleName != null &&
                        context.ExpectedToActualBundleName.TryGetValue(bundleName, out var actualName))
                        bundleFileName = actualName;
                    else
                        bundleFileName = bundleName.EndsWith(".bundle") ? bundleName : bundleName + ".bundle";

                    var bundlePath = Path.Combine(bundleDir, bundleFileName).Replace("\\", "/");
                    long bundleSize = File.Exists(bundlePath) ? new FileInfo(bundlePath).Length : 0;

                    var depList = new List<string>();
                    if (context.BundleDependencies.TryGetValue(bundleName, out var depSet))
                        depList.AddRange(depSet.OrderBy(d => d, StringComparer.Ordinal));

                    var directDepList = new List<string>();
                    if (context.BundleDirectDependencies != null &&
                        context.BundleDirectDependencies.TryGetValue(bundleName, out var directDepSet))
                        directDepList.AddRange(directDepSet.OrderBy(d => d, StringComparer.Ordinal));

                    var assetEntries = new List<BuildReportAssetEntry>();
                    if (context.BundleToPackedAssetPaths != null &&
                        context.BundleToPackedAssetPaths.TryGetValue(bundleName, out var packedPaths) &&
                        packedPaths != null && packedPaths.Count > 0)
                    {
                        foreach (var assetPath in packedPaths)
                        {
                            if (string.IsNullOrEmpty(assetPath))
                                continue;
                            long assetSize = 0;
                            if (File.Exists(assetPath))
                                assetSize = new FileInfo(assetPath).Length;
                            assetEntries.Add(new BuildReportAssetEntry
                            {
                                assetPath = assetPath,
                                sizeBytes = assetSize
                            });
                        }
                    }
                    else
                    {
                        foreach (var guid in assetGuids)
                        {
                            if (!context.GuidToPath.TryGetValue(guid, out var assetPath))
                                continue;

                            long assetSize = 0;
                            if (!string.IsNullOrEmpty(assetPath) && File.Exists(assetPath))
                                assetSize = new FileInfo(assetPath).Length;

                            assetEntries.Add(new BuildReportAssetEntry
                            {
                                assetPath = assetPath,
                                sizeBytes = assetSize
                            });
                        }
                    }
                    assetEntries.Sort((a, b) => string.Compare(a.assetPath, b.assetPath, StringComparison.OrdinalIgnoreCase));

                    data.bundles.Add(new BuildReportBundleEntry
                    {
                        bundleName = bundleName,
                        sizeBytes = bundleSize,
                        bundleDirectDependencies = directDepList,
                        bundleDependencies = depList,
                        assets = assetEntries
                    });
                }

                data.bundles.Sort((a, b) => string.Compare(a.bundleName, b.bundleName, StringComparison.OrdinalIgnoreCase));

                BuildAssetLevelDiff(context, data);

                var reportDir = GetReportDirectory(context.Config);
                if (!Directory.Exists(reportDir))
                    Directory.CreateDirectory(reportDir);

                var reportPath = Path.Combine(reportDir, REPORT_FILENAME);
                var json = JsonUtility.ToJson(data, true);
                File.WriteAllText(reportPath, json);

                Debug.Log($"[HyperContent] Build report saved: {reportPath} ({data.bundles.Count} bundles)");
            }
            catch (Exception e)
            {
                Debug.LogError($"[HyperContent] Failed to save build report: {e.Message}");
            }
        }

        /// <summary>
        /// Populate <see cref="BuildReportData.assetLevelDiff"/> by comparing, for each asset that has
        /// asset-level dependency data, the bundle set it loads under asset-level loading against the
        /// owning bundle's bundle-level closure (the set BundleLevel mode would load). Records which
        /// bundles asset-level avoids, plus aggregate totals.
        /// </summary>
        private static void BuildAssetLevelDiff(BuildContext context, BuildReportData data)
        {
            var diff = data.assetLevelDiff;
            if (context.AssetDependencyBundles == null || context.AssetDependencyBundles.Count == 0)
                return;

            // guid → addressable key for readable report rows.
            var guidToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (context.KeyToGuid != null)
            {
                foreach (var kvp in context.KeyToGuid)
                {
                    var g = kvp.Value?.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(g) && !guidToKey.ContainsKey(g))
                        guidToKey[g] = kvp.Key;
                }
            }

            foreach (var kvp in context.AssetDependencyBundles)
            {
                var deps = kvp.Value;
                if (deps == null || deps.Count == 0)
                    continue;

                diff.assetsAnalyzed++;

                var owningBundle = deps[deps.Count - 1];
                var assetLevelSet = new HashSet<string>(deps, StringComparer.Ordinal);

                // Bundle-level set = owning bundle + its transitive closure.
                var bundleLevelSet = new HashSet<string>(StringComparer.Ordinal) { owningBundle };
                if (context.BundleDependencies != null &&
                    context.BundleDependencies.TryGetValue(owningBundle, out var closure) && closure != null)
                {
                    foreach (var b in closure)
                        bundleLevelSet.Add(b);
                }

                // Bundles avoided by asset-level (in bundle-level but not asset-level).
                var saved = new List<string>();
                foreach (var b in bundleLevelSet)
                {
                    if (!assetLevelSet.Contains(b))
                        saved.Add(b);
                }
                saved.Sort(StringComparer.Ordinal);

                diff.totalAssetLevelBundleRefs += assetLevelSet.Count;
                diff.totalBundleLevelBundleRefs += bundleLevelSet.Count;
                diff.totalBundlesSaved += saved.Count;

                if (saved.Count > 0)
                {
                    diff.assetsWithSavings++;
                    guidToKey.TryGetValue(kvp.Key, out var address);
                    diff.entries.Add(new BuildReportAssetDiffEntry
                    {
                        guid = kvp.Key,
                        address = address,
                        owningBundle = owningBundle,
                        assetLevelBundleCount = assetLevelSet.Count,
                        bundleLevelBundleCount = bundleLevelSet.Count,
                        savedBundleCount = saved.Count,
                        savedBundles = saved
                    });
                }
            }

            diff.entries.Sort((a, b) =>
            {
                int c = b.savedBundleCount.CompareTo(a.savedBundleCount);
                return c != 0 ? c : string.CompareOrdinal(a.guid, b.guid);
            });

            Debug.Log($"[HyperContent] Asset-level diff: {diff.assetsAnalyzed} assets analyzed, " +
                      $"{diff.assetsWithSavings} save bundles, {diff.totalBundlesSaved} total bundle loads avoided " +
                      $"(asset-level refs={diff.totalAssetLevelBundleRefs}, bundle-level refs={diff.totalBundleLevelBundleRefs}).");
        }

        private static void GenerateAssetAggregation(BuildContext context, BuildReport report)
        {
            report.AssetAggregation.Clear();

            foreach (var kvp in context.BundleToAssets)
            {
                var bundleName = kvp.Key;
                var assetGuids = kvp.Value;
                var assetKeys = new List<string>();

                foreach (var assetGuid in assetGuids)
                {
                    if (context.AssetMarkers.TryGetValue(assetGuid, out var marker))
                        assetKeys.Add(marker.assetKey);
                    else if (context.GuidToPath.TryGetValue(assetGuid, out var path))
                        assetKeys.Add(path);
                }

                report.AssetAggregation[bundleName] = assetKeys;
            }
        }

        private static void GenerateDuplicateDependencies(BuildContext context, BuildReport report)
        {
            report.DuplicateDependencies.Clear();

            var dependencyUsage = new Dictionary<string, List<string>>();

            foreach (var kvp in context.BundleDependencies)
            {
                var bundleName = kvp.Key;
                foreach (var dep in kvp.Value)
                {
                    if (!dependencyUsage.ContainsKey(dep))
                        dependencyUsage[dep] = new List<string>();
                    dependencyUsage[dep].Add(bundleName);
                }
            }

            foreach (var kvp in dependencyUsage)
            {
                if (kvp.Value.Count > 1)
                {
                    report.DuplicateDependencies.Add(new DuplicateDependencyInfo
                    {
                        DependencyBundle = kvp.Key,
                        DependentBundles = kvp.Value
                    });
                }
            }
        }

        private static void LogReportSummary(BuildContext context, BuildReport report)
        {
            Debug.Log("=== HyperContent Build Report ===");
            Debug.Log($"Build Time: {report.BuildTimestamp:yyyy-MM-dd HH:mm:ss}");
            Debug.Log($"Build Duration: {report.BuildDurationMs}ms");
            Debug.Log($"Total Assets: {report.TotalAssets}");
            Debug.Log($"Total Bundles: {report.TotalBundles}");
            Debug.Log($"Total Bundle Size: {FormatBytes(report.TotalBundleSize)}");
            Debug.Log($"Average Bundle Size: {FormatBytes(report.AverageBundleSize)}");

            if (report.LargestBundle != null)
                Debug.Log($"Largest Bundle: {report.LargestBundle.BundleName} ({FormatBytes(report.LargestBundle.SizeBytes)}, {report.LargestBundle.AssetCount} assets)");

            if (report.SmallestBundle != null)
                Debug.Log($"Smallest Bundle: {report.SmallestBundle.BundleName} ({FormatBytes(report.SmallestBundle.SizeBytes)}, {report.SmallestBundle.AssetCount} assets)");

            LogBundleDependencies(context);

            if (report.DuplicateDependencies.Count > 0)
            {
                Debug.Log($"\nDuplicate Dependencies ({report.DuplicateDependencies.Count}):");
                foreach (var dup in report.DuplicateDependencies)
                    Debug.Log($"  {dup.DependencyBundle} is used by: {string.Join(", ", dup.DependentBundles)}");
            }

            Debug.Log("=================================");
        }

        private static void LogBundleDependencies(BuildContext context)
        {
            var bundlesWithDeps = 0;
            long totalTransitiveEdges = 0;
            foreach (var kvp in context.BundleDependencies)
            {
                if (kvp.Value == null || kvp.Value.Count == 0)
                    continue;
                bundlesWithDeps++;
                totalTransitiveEdges += kvp.Value.Count;
                int directCount = 0;
                if (context.BundleDirectDependencies != null &&
                    context.BundleDirectDependencies.TryGetValue(kvp.Key, out var directSet) &&
                    directSet != null)
                {
                    directCount = directSet.Count;
                }
                Debug.Log($"[BuildReport]   {kvp.Key} direct={directCount} transitive={kvp.Value.Count}");
            }

            long totalDirectEdges = 0;
            if (context.BundleDirectDependencies != null)
            {
                foreach (var kvp in context.BundleDirectDependencies)
                {
                    if (kvp.Value != null)
                        totalDirectEdges += kvp.Value.Count;
                }
            }

            var bundlesWithoutDeps = context.BundleToAssets.Count - bundlesWithDeps;
            Debug.Log($"[BuildReport] {bundlesWithDeps} bundles have dependencies, " +
                      $"{bundlesWithoutDeps} bundles have none. " +
                      $"Edges: direct={totalDirectEdges} (one-hop, diagnostic), " +
                      $"transitive={totalTransitiveEdges} (closure, used by catalog).");
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }
    }
}
