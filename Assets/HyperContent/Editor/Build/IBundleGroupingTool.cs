using System.Collections.Generic;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Interface for grouping tools that convert project assets into a BuildPlan
    /// Grouping tools are responsible for:
    /// 1. Collecting assets
    /// 2. Analyzing dependencies
    /// 3. Assigning assets to bundles
    /// 4. Producing a BuildPlan ready for building
    /// </summary>
    public interface IBundleGroupingTool
    {
        /// <summary>
        /// Get the display name of this grouping tool
        /// </summary>
        string ToolName { get; }
        
        /// <summary>
        /// Get a description of what this tool does
        /// </summary>
        string Description { get; }
        
        /// <summary>
        /// Generate a build plan from project assets.
        /// Implementations must fill <see cref="BuildPlan.BundleCompression"/> with one entry per bundle in <see cref="BuildPlan.BundleToAssets"/> (see grouping strategies).
        /// Implementations may fill <see cref="BuildPlan.BundleTagFlagsFromPlan"/> (e.g. from Addressable group entry labels) for catalog <c>bundleTagFlags</c>; catalog generation ORs this with per-asset marker flags.
        /// </summary>
        /// <param name="config">Build configuration</param>
        /// <returns>Build plan containing asset-to-bundle assignments</returns>
        BuildPlan GeneratePlan(BuildConfig config);
        
        /// <summary>
        /// Validate that this tool can be used with the current project state
        /// </summary>
        /// <param name="config">Build configuration</param>
        /// <returns>List of validation errors, empty if valid</returns>
        List<string> Validate(BuildConfig config);
    }
}
