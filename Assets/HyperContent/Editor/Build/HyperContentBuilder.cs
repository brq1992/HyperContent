using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Main builder class that orchestrates the entire build process
    /// Uses grouping tools and build executors for modular architecture
    /// </summary>
    public static class HyperContentBuilder
    {
        /// <summary>
        /// Build HyperContent bundles and catalog using grouping tool and build executor
        /// </summary>
        public static BuildResult Build(BuildConfig config)
        {
            try
            {
                Debug.Log("[HyperContent] Starting build process...");
                
                // Step 1: Get grouping tool
                var groupingTool = BuildToolFactory.GetGroupingTool(config.groupingToolId);
                Debug.Log($"[HyperContent] Using grouping tool: {groupingTool.ToolName}");
                
                // Step 2: Validate grouping tool
                var toolValidationErrors = groupingTool.Validate(config);
                if (toolValidationErrors.Count > 0)
                {
                    var context = new BuildContext { Config = config };
                    foreach (var error in toolValidationErrors)
                    {
                        context.Errors.Add(new BuildError(error));
                    }
                    LogErrors(context);
                    return BuildResult.Failure(context, "Grouping tool validation failed");
                }
                
                // Step 3: Generate build plan
                Debug.Log("[HyperContent] Generating build plan...");
                var plan = groupingTool.GeneratePlan(config);
                
                if (plan.Errors.Count > 0)
                {
                    var context = new BuildContext
                    {
                        Config = config,
                        Errors = plan.Errors,
                        Warnings = plan.Warnings
                    };
                    LogErrors(context);
                    return BuildResult.Failure(context, "Build plan generation failed");
                }
                
                Debug.Log($"[HyperContent] Build plan generated: {plan.BundleToAssets.Count} bundles, {plan.AssetMarkers.Count} assets");
                
                // Step 4: Get build executor
                var executor = BuildToolFactory.GetBuildExecutor(config.buildExecutorId);
                Debug.Log($"[HyperContent] Using build executor: {executor.ExecutorName}");
                
                // Step 5: Validate executor
                var executorValidationErrors = executor.Validate(plan, config);
                if (executorValidationErrors.Count > 0)
                {
                    var context = new BuildContext
                    {
                        Config = config,
                        Errors = plan.Errors,
                        Warnings = plan.Warnings
                    };
                    foreach (var error in executorValidationErrors)
                    {
                        context.Errors.Add(new BuildError(error));
                    }
                    LogErrors(context);
                    return BuildResult.Failure(context, "Build executor validation failed");
                }
                
                // Step 6: Execute build
                Debug.Log("[HyperContent] Executing build...");
                var result = executor.Execute(plan, config);
                
                // Log warnings
                if (result.Context.Warnings.Count > 0)
                {
                    LogWarnings(result.Context);
                }
                
                // Log errors if any
                if (result.Context.Errors.Count > 0)
                {
                    LogErrors(result.Context);
                }
                
                if (result.IsSuccess)
                {
                    Debug.Log("[HyperContent] Build completed successfully!");
                }
                else
                {
                    Debug.LogError($"[HyperContent] Build failed: {result.Message}");
                }
                
                return result;
            }
            catch (Exception e)
            {
                Debug.LogError($"[HyperContent] Build failed with exception: {e}");
                var context = new BuildContext
                {
                    Config = config,
                    Errors = { new BuildError($"Build exception: {e.Message}") }
                };
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
