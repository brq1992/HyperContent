using System.Collections.Generic;
using System.Linq;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Default grouping tool that uses the existing asset collection and grouping strategy system
    /// This tool combines AssetCollector, DependencyAnalyzer, and grouping strategies
    /// </summary>
    public class DefaultGroupingTool : IBundleGroupingTool
    {
        public string ToolName => "Default Grouping Tool";
        
        public string Description => "Collects assets using HyperContentAsset markers or directory conventions, " +
                                    "analyzes dependencies, and groups assets using the configured grouping strategy.";
        
        public BuildPlan GeneratePlan(BuildConfig config)
        {
            var plan = new BuildPlan();
            
            // Step 1: Collect assets
            CollectAssets(plan, config);
            
            if (plan.Errors.Count > 0)
            {
                return plan;
            }
            
            // Step 2: Analyze dependencies
            AnalyzeDependencies(plan);
            
            // Step 3: Assign bundles using grouping strategy
            AssignBundles(plan, config);
            
            // Step 4: Build bundle dependencies
            BuildBundleDependencies(plan);
            
            return plan;
        }
        
        public List<string> Validate(BuildConfig config)
        {
            var errors = new List<string>();
            
            // Validate grouping strategy
            var strategy = BundleGroupingStrategyFactory.CreateStrategy(config.groupingStrategy);
            var strategyErrors = strategy.Validate(new BuildContext { Config = config });
            errors.AddRange(strategyErrors);
            
            return errors;
        }
        
        /// <summary>
        /// Collect all marked assets (reuses AssetCollector logic)
        /// </summary>
        private void CollectAssets(BuildPlan plan, BuildConfig config)
        {
            // Create a temporary context for AssetCollector
            var tempContext = new BuildContext { Config = config };
            AssetCollector.CollectAssets(tempContext);
            
            // Copy to plan
            plan.AssetMarkers = tempContext.AssetMarkers;
            plan.KeyToGuid = tempContext.KeyToGuid;
            plan.GuidToPath = tempContext.GuidToPath;
            plan.Errors = tempContext.Errors;
            plan.Warnings = tempContext.Warnings;
        }
        
        /// <summary>
        /// Analyze dependencies (reuses DependencyAnalyzer logic)
        /// </summary>
        private void AnalyzeDependencies(BuildPlan plan)
        {
            // Create a temporary context for DependencyAnalyzer
            var tempContext = new BuildContext
            {
                AssetMarkers = plan.AssetMarkers,
                GuidToPath = plan.GuidToPath
            };
            
            DependencyAnalyzer.AnalyzeDependencies(tempContext);
            
            // Copy dependencies to plan
            plan.Dependencies = tempContext.Dependencies;
        }
        
        /// <summary>
        /// Assign bundles using grouping strategy
        /// </summary>
        private void AssignBundles(BuildPlan plan, BuildConfig config)
        {
            // Create a temporary context for grouping strategy
            var tempContext = new BuildContext
            {
                Config = config,
                AssetMarkers = plan.AssetMarkers,
                KeyToGuid = plan.KeyToGuid,
                GuidToPath = plan.GuidToPath,
                Dependencies = plan.Dependencies
            };
            
            // Use existing grouping strategy system
            var strategy = BundleGroupingStrategyFactory.CreateStrategy(config.groupingStrategy);
            var validationErrors = strategy.Validate(tempContext);
            if (validationErrors.Count > 0)
            {
                foreach (var error in validationErrors)
                {
                    plan.Errors.Add(new BuildError(error));
                }
                return;
            }
            
            if (!strategy.AssignBundles(tempContext))
            {
                plan.Errors.Add(new BuildError($"Bundle grouping failed using strategy: {strategy.StrategyName}"));
                return;
            }
            
            // Copy bundle assignments to plan
            plan.AssetToBundle = tempContext.AssetToBundle;
            plan.BundleToAssets = tempContext.BundleToAssets;
            
            // Handle dependencies if enabled
            if (config.includeDependencies)
            {
                AddDependenciesToBundles(plan);
            }
        }
        
        /// <summary>
        /// Add dependencies to bundles if includeDependencies is enabled
        /// </summary>
        private void AddDependenciesToBundles(BuildPlan plan)
        {
            foreach (var kvp in plan.Dependencies)
            {
                var assetGuid = kvp.Key;
                var dependencies = kvp.Value;
                
                if (!plan.AssetToBundle.TryGetValue(assetGuid, out var bundleName))
                {
                    continue;
                }
                
                foreach (var depGuid in dependencies)
                {
                    if (!plan.AssetToBundle.ContainsKey(depGuid))
                    {
                        if (!plan.BundleToAssets.ContainsKey(bundleName))
                        {
                            plan.BundleToAssets[bundleName] = new HashSet<string>();
                        }
                        plan.BundleToAssets[bundleName].Add(depGuid);
                        plan.AssetToBundle[depGuid] = bundleName;
                    }
                }
            }
        }
        
        /// <summary>
        /// Build bundle dependencies from asset dependencies
        /// </summary>
        private void BuildBundleDependencies(BuildPlan plan)
        {
            plan.BundleDependencies.Clear();
            
            foreach (var kvp in plan.Dependencies)
            {
                var assetGuid = kvp.Key;
                var dependencies = kvp.Value;
                
                if (!plan.AssetToBundle.TryGetValue(assetGuid, out var bundleName))
                {
                    continue;
                }
                
                foreach (var depGuid in dependencies)
                {
                    if (plan.AssetToBundle.TryGetValue(depGuid, out var depBundleName))
                    {
                        if (depBundleName != bundleName)
                        {
                            if (!plan.BundleDependencies.ContainsKey(bundleName))
                            {
                                plan.BundleDependencies[bundleName] = new HashSet<string>();
                            }
                            plan.BundleDependencies[bundleName].Add(depBundleName);
                        }
                    }
                }
            }
        }
    }
}
