using System.Collections.Generic;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Interface for build executors that convert BuildPlan into bundles and catalog
    /// Build executors are responsible for:
    /// 1. Reading BuildPlan
    /// 2. Building Unity AssetBundles
    /// 3. Generating catalog.json
    /// 4. Producing build reports (optional)
    /// </summary>
    public interface IBuildExecutor
    {
        /// <summary>
        /// Get the display name of this build executor
        /// </summary>
        string ExecutorName { get; }
        
        /// <summary>
        /// Get a description of what this executor does
        /// </summary>
        string Description { get; }
        
        /// <summary>
        /// Execute the build process
        /// </summary>
        /// <param name="plan">Build plan from grouping tool</param>
        /// <param name="config">Build configuration</param>
        /// <returns>Build result</returns>
        BuildResult Execute(BuildPlan plan, BuildConfig config);
        
        /// <summary>
        /// Validate that this executor can be used with the given plan and config
        /// </summary>
        /// <param name="plan">Build plan</param>
        /// <param name="config">Build configuration</param>
        /// <returns>List of validation errors, empty if valid</returns>
        List<string> Validate(BuildPlan plan, BuildConfig config);
    }
}
