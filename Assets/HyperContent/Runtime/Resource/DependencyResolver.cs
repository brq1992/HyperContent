using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HyperContent
{
    /// <summary>
    /// Helper class for resolving bundle dependencies and topological sorting
    /// </summary>
    internal static class DependencyResolver
    {
        /// <summary>
        /// Resolve all dependencies for a bundle using topological sort
        /// Returns bundles in load order (dependencies first)
        /// </summary>
        public static List<string> ResolveDependencies(IContentCatalog catalog, string bundleName, HashSet<string> visited = null)
        {
            if (visited == null)
            {
                visited = new HashSet<string>();
            }
            
            var result = new List<string>();
            
            if (!catalog.TryGetBundleInfo(bundleName, out BundleInfo bundleInfo))
            {
                return result;
            }
            
            // Recursively resolve dependencies first
            if (bundleInfo.Dependencies != null)
            {
                foreach (var depName in bundleInfo.Dependencies)
                {
                    if (!visited.Contains(depName))
                    {
                        visited.Add(depName);
                        var depBundles = ResolveDependencies(catalog, depName, visited);
                        result.AddRange(depBundles);
                    }
                }
            }
            
            // Add current bundle if not already in result
            if (!result.Contains(bundleName))
            {
                result.Add(bundleName);
            }
            
            return result;
        }
        
        /// <summary>
        /// Resolve all bundles needed for an asset key
        /// Returns bundles in load order (dependencies first)
        /// </summary>
        public static List<string> ResolveBundlesForAsset(IContentCatalog catalog, string assetKey)
        {
            if (!catalog.TryGetBundleName(assetKey, out string bundleName))
            {
                return new List<string>();
            }
            
            return ResolveDependencies(catalog, bundleName);
        }
        
        /// <summary>
        /// Topological sort of bundles using Kahn's algorithm
        /// Ensures dependencies are loaded before dependents
        /// </summary>
        public static List<string> TopologicalSort(IContentCatalog catalog, HashSet<string> bundleNames)
        {
            var sorted = new List<string>();
            var inDegree = new Dictionary<string, int>();
            var adjacencyList = new Dictionary<string, List<string>>();
            
            // Initialize in-degree and adjacency list
            foreach (var bundleName in bundleNames)
            {
                inDegree[bundleName] = 0;
                adjacencyList[bundleName] = new List<string>();
            }
            
            // Build graph and calculate in-degrees
            foreach (var bundleName in bundleNames)
            {
                if (catalog.TryGetBundleInfo(bundleName, out BundleInfo bundleInfo))
                {
                    if (bundleInfo.Dependencies != null)
                    {
                        foreach (var depName in bundleInfo.Dependencies)
                        {
                            if (bundleNames.Contains(depName))
                            {
                                adjacencyList[depName].Add(bundleName);
                                inDegree[bundleName]++;
                            }
                        }
                    }
                }
            }
            
            // Kahn's algorithm
            var queue = new Queue<string>();
            foreach (var kvp in inDegree)
            {
                if (kvp.Value == 0)
                {
                    queue.Enqueue(kvp.Key);
                }
            }
            
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                sorted.Add(current);
                
                foreach (var dependent in adjacencyList[current])
                {
                    inDegree[dependent]--;
                    if (inDegree[dependent] == 0)
                    {
                        queue.Enqueue(dependent);
                    }
                }
            }
            
            // Check for cycles
            if (sorted.Count != bundleNames.Count)
            {
                // Circular dependency detected
                Debug.LogWarning("[HyperContent] Circular dependency detected in bundle graph");
                // Return original order as fallback
                return bundleNames.ToList();
            }
            
            return sorted;
        }
    }
}
