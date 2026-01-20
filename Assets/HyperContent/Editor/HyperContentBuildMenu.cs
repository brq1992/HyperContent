using HyperContent.Editor.Build;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor
{
    /// <summary>
    /// Menu items for HyperContent build operations
    /// </summary>
    public static class HyperContentBuildMenu
    {
        [MenuItem("HyperContent/Build", false, 10)]
        public static void Build()
        {
            var config = GetDefaultConfig();
            var result = HyperContentBuilder.Build(config);
            
            if (result.IsSuccess)
            {
                Debug.Log($"[HyperContent] Build completed successfully! Bundles: {result.Context.BundleToAssets.Count}, Assets: {result.Context.AssetMarkers.Count}");
                AssetDatabase.Refresh();
            }
            else
            {
                Debug.LogError($"[HyperContent] Build failed: {result.Message}");
            }
        }
        
        [MenuItem("HyperContent/Build (Force Rebuild)", false, 11)]
        public static void BuildForceRebuild()
        {
            var config = GetDefaultConfig();
            config.forceRebuild = true;
            var result = HyperContentBuilder.Build(config);
            
            if (result.IsSuccess)
            {
                Debug.Log($"[HyperContent] Build (Force Rebuild) completed successfully!");
                AssetDatabase.Refresh();
            }
            else
            {
                Debug.LogError($"[HyperContent] Build (Force Rebuild) failed: {result.Message}");
            }
        }
        
        [MenuItem("HyperContent/Validate", false, 20)]
        public static void Validate()
        {
            var context = new BuildContext
            {
                Config = GetDefaultConfig()
            };
            
            AssetCollector.CollectAssets(context);
            DependencyAnalyzer.AnalyzeDependencies(context);
            DependencyAnalyzer.AssignBundles(context);
            
            var isValid = BuildValidator.Validate(context);
            
            if (isValid && context.Errors.Count == 0)
            {
                Debug.Log($"[HyperContent] Validation passed! Assets: {context.AssetMarkers.Count}, Bundles: {context.BundleToAssets.Count}");
            }
            else
            {
                Debug.LogError($"[HyperContent] Validation failed! Errors: {context.Errors.Count}, Warnings: {context.Warnings.Count}");
            }
        }
        
        private static BuildConfig GetDefaultConfig()
        {
            return new BuildConfig
            {
                outputDirectory = "Assets/StreamingAssets",
                catalogName = "default_catalog",
                buildTarget = EditorUserBuildSettings.activeBuildTarget,
                compressionType = BundleCompressionType.Lz4,
                includeDependencies = true,
                forceRebuild = false,
                generateReport = true
            };
        }
    }
}
