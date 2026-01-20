using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Main builder class that orchestrates the entire build process
    /// </summary>
    public static class HyperContentBuilder
    {
        /// <summary>
        /// Build HyperContent bundles and catalog
        /// </summary>
        public static BuildResult Build(BuildConfig config)
        {
            var context = new BuildContext
            {
                Config = config,
                Report = config.generateReport ? new BuildReport() : null
            };
            
            try
            {
                Debug.Log("[HyperContent] Starting build process...");
                
                // Step 1: Collect all marked assets
                Debug.Log("[HyperContent] Step 1: Collecting assets...");
                AssetCollector.CollectAssets(context);
                
                if (context.Errors.Count > 0)
                {
                    LogErrors(context);
                    return BuildResult.Failure(context, "Asset collection failed");
                }
                
                Debug.Log($"[HyperContent] Collected {context.AssetMarkers.Count} assets");
                
                // Step 2: Analyze dependencies
                Debug.Log("[HyperContent] Step 2: Analyzing dependencies...");
                DependencyAnalyzer.AnalyzeDependencies(context);
                
                // Step 3: Assign assets to bundles
                Debug.Log("[HyperContent] Step 3: Assigning bundles...");
                DependencyAnalyzer.AssignBundles(context);
                
                Debug.Log($"[HyperContent] Created {context.BundleToAssets.Count} bundles");
                
                // Step 4: Validate before build
                Debug.Log("[HyperContent] Step 4: Validating build configuration...");
                if (!BuildValidator.Validate(context))
                {
                    LogErrors(context);
                    return BuildResult.Failure(context, "Build validation failed");
                }
                
                // Step 5: Build bundles
                Debug.Log("[HyperContent] Step 5: Building AssetBundles...");
                if (!BundleBuilder.BuildBundles(context))
                {
                    LogErrors(context);
                    return BuildResult.Failure(context, "Bundle build failed");
                }
                
                // Step 6: Generate catalog
                Debug.Log("[HyperContent] Step 6: Generating catalog...");
                if (!CatalogGenerator.GenerateCatalog(context))
                {
                    LogErrors(context);
                    return BuildResult.Failure(context, "Catalog generation failed");
                }
                
                // Step 7: Final validation
                Debug.Log("[HyperContent] Step 7: Final validation...");
                BuildValidator.Validate(context);
                
                // Step 8: Generate report
                if (config.generateReport)
                {
                    Debug.Log("[HyperContent] Step 8: Generating build report...");
                    BuildReportGenerator.GenerateReport(context);
                }
                
                // Log warnings
                if (context.Warnings.Count > 0)
                {
                    LogWarnings(context);
                }
                
                // Log errors if any
                if (context.Errors.Count > 0)
                {
                    LogErrors(context);
                    return BuildResult.Failure(context, "Build completed with errors");
                }
                
                Debug.Log("[HyperContent] Build completed successfully!");
                return BuildResult.Success(context);
            }
            catch (Exception e)
            {
                Debug.LogError($"[HyperContent] Build failed with exception: {e}");
                context.Errors.Add(new BuildError($"Build exception: {e.Message}"));
                return BuildResult.Failure(context, $"Build exception: {e.Message}");
            }
        }
        
        /// <summary>
        /// Log all errors
        /// </summary>
        private static void LogErrors(BuildContext context)
        {
            Debug.LogError($"[HyperContent] Build Errors ({context.Errors.Count}):");
            foreach (var error in context.Errors)
            {
                var message = $"  {error.Message}";
                if (!string.IsNullOrEmpty(error.AssetPath))
                {
                    message += $" (Asset: {error.AssetPath})";
                }
                if (!string.IsNullOrEmpty(error.AssetKey))
                {
                    message += $" (Key: {error.AssetKey})";
                }
                Debug.LogError(message);
            }
        }
        
        /// <summary>
        /// Log all warnings
        /// </summary>
        private static void LogWarnings(BuildContext context)
        {
            Debug.LogWarning($"[HyperContent] Build Warnings ({context.Warnings.Count}):");
            foreach (var warning in context.Warnings)
            {
                var message = $"  {warning.Message}";
                if (!string.IsNullOrEmpty(warning.AssetPath))
                {
                    message += $" (Asset: {warning.AssetPath})";
                }
                if (!string.IsNullOrEmpty(warning.AssetKey))
                {
                    message += $" (Key: {warning.AssetKey})";
                }
                Debug.LogWarning(message);
            }
        }
    }
    
    /// <summary>
    /// Build result
    /// </summary>
    public class BuildResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public BuildContext Context { get; set; }
        
        public static BuildResult Success(BuildContext context)
        {
            return new BuildResult
            {
                IsSuccess = true,
                Message = "Build completed successfully",
                Context = context
            };
        }
        
        public static BuildResult Failure(BuildContext context, string message)
        {
            return new BuildResult
            {
                IsSuccess = false,
                Message = message,
                Context = context
            };
        }
    }
}
