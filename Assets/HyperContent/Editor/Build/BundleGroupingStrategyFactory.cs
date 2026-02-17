using System;
using UnityEngine;

namespace HyperContent.Editor.Build
{
    /// <summary>
    /// Factory for creating bundle grouping strategies
    /// </summary>
    public static class BundleGroupingStrategyFactory
    {
        /// <summary>
        /// Create a grouping strategy based on the strategy type
        /// </summary>
        public static IBundleGroupingStrategy CreateStrategy(BundleGroupingStrategyType strategyType)
        {
            switch (strategyType)
            {
                case BundleGroupingStrategyType.MarkerBased:
                    return new MarkerBasedGroupingStrategy();
                    
                case BundleGroupingStrategyType.Addressable:
                    return new AddressableGroupingStrategy();
                    
                default:
                    throw new ArgumentException($"Unknown grouping strategy type: {strategyType}");
            }
        }
        
        /// <summary>
        /// Get the display name for a strategy type
        /// </summary>
        public static string GetStrategyName(BundleGroupingStrategyType strategyType)
        {
            var strategy = CreateStrategy(strategyType);
            return strategy.StrategyName;
        }
    }
}
