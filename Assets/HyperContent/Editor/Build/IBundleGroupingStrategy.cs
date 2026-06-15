using System.Collections.Generic;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Strategy interface for grouping assets into bundles
    /// This allows different grouping approaches (Addressable, Marker-based, etc.)
    /// </summary>
    public interface IBundleGroupingStrategy
    {
        /// <summary>
        /// Assign assets to bundles based on the grouping strategy
        /// </summary>
        /// <param name="context">Build context containing assets and configuration</param>
        /// <returns>True if grouping succeeded, false otherwise</returns>
        bool AssignBundles(BuildContext context);
        
        /// <summary>
        /// Get the display name of this strategy
        /// </summary>
        string StrategyName { get; }
        
        /// <summary>
        /// Validate that the strategy can be used with the current configuration
        /// </summary>
        /// <param name="context">Build context</param>
        /// <returns>List of validation errors, empty if valid</returns>
        List<string> Validate(BuildContext context);
    }
}
