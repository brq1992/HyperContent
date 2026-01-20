using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Generates detailed build report with analysis
    /// </summary>
    public static class BuildReportGenerator
    {
        /// <summary>
        /// Generate comprehensive build report
        /// </summary>
        public static void GenerateReport(BuildContext context)
        {
            if (context.Report == null)
            {
                context.Report = new BuildReport();
            }
            
            var report = context.Report;
            
            // Basic statistics
            report.TotalAssets = context.AssetMarkers.Count;
            report.TotalBundles = context.BundleToAssets.Count;
            
            // Asset aggregation by bundle
            GenerateAssetAggregation(context, report);
            
            // Duplicate dependencies analysis
            GenerateDuplicateDependencies(context, report);
            
            // Log report summary
            LogReportSummary(report);
        }
        
        /// <summary>
        /// Generate asset aggregation report
        /// </summary>
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
                    {
                        assetKeys.Add(marker.assetKey);
                    }
                    else if (context.GuidToPath.TryGetValue(assetGuid, out var path))
                    {
                        // Use path as fallback
                        assetKeys.Add(path);
                    }
                }
                
                report.AssetAggregation[bundleName] = assetKeys;
            }
        }
        
        /// <summary>
        /// Generate duplicate dependencies report
        /// </summary>
        private static void GenerateDuplicateDependencies(BuildContext context, BuildReport report)
        {
            report.DuplicateDependencies.Clear();
            
            // Find bundles that are dependencies of multiple other bundles
            var dependencyUsage = new Dictionary<string, List<string>>();
            
            foreach (var kvp in context.BundleDependencies)
            {
                var bundleName = kvp.Key;
                var dependencies = kvp.Value;
                
                foreach (var dep in dependencies)
                {
                    if (!dependencyUsage.ContainsKey(dep))
                    {
                        dependencyUsage[dep] = new List<string>();
                    }
                    dependencyUsage[dep].Add(bundleName);
                }
            }
            
            // Report dependencies that are used by multiple bundles
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
        
        /// <summary>
        /// Log build report summary
        /// </summary>
        private static void LogReportSummary(BuildReport report)
        {
            Debug.Log("=== HyperContent Build Report ===");
            Debug.Log($"Build Time: {report.BuildTimestamp:yyyy-MM-dd HH:mm:ss}");
            Debug.Log($"Build Duration: {report.BuildDurationMs}ms");
            Debug.Log($"Total Assets: {report.TotalAssets}");
            Debug.Log($"Total Bundles: {report.TotalBundles}");
            Debug.Log($"Total Bundle Size: {FormatBytes(report.TotalBundleSize)}");
            Debug.Log($"Average Bundle Size: {FormatBytes(report.AverageBundleSize)}");
            
            if (report.LargestBundle != null)
            {
                Debug.Log($"Largest Bundle: {report.LargestBundle.BundleName} ({FormatBytes(report.LargestBundle.SizeBytes)}, {report.LargestBundle.AssetCount} assets)");
            }
            
            if (report.SmallestBundle != null)
            {
                Debug.Log($"Smallest Bundle: {report.SmallestBundle.BundleName} ({FormatBytes(report.SmallestBundle.SizeBytes)}, {report.SmallestBundle.AssetCount} assets)");
            }
            
            if (report.DuplicateDependencies.Count > 0)
            {
                Debug.Log($"\nDuplicate Dependencies ({report.DuplicateDependencies.Count}):");
                foreach (var dup in report.DuplicateDependencies)
                {
                    Debug.Log($"  {dup.DependencyBundle} is used by: {string.Join(", ", dup.DependentBundles)}");
                }
            }
            
            Debug.Log("=================================");
        }
        
        /// <summary>
        /// Format bytes to human-readable string
        /// </summary>
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
