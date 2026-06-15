using System.Collections.Generic;
using System.Linq;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Factory for creating grouping tools and build executors
    /// </summary>
    public static class BuildToolFactory
    {
        private static readonly Dictionary<string, IBundleGroupingTool> _groupingTools = new Dictionary<string, IBundleGroupingTool>();
        private static readonly Dictionary<string, IBuildExecutor> _buildExecutors = new Dictionary<string, IBuildExecutor>();
        
        static BuildToolFactory()
        {
            // Register default tools
            RegisterDefaultTools();
        }
        
        /// <summary>
        /// Register default grouping tools and build executors
        /// </summary>
        private static void RegisterDefaultTools()
        {
            // Register default grouping tool
            var defaultGroupingTool = new DefaultGroupingTool();
            RegisterGroupingTool("default", defaultGroupingTool);
            
            // Register default build executor
            var defaultExecutor = new DefaultBuildExecutor();
            RegisterBuildExecutor("default", defaultExecutor);
            
            // Register update build executor
            var updateExecutor = new UpdateBuildExecutor();
            RegisterBuildExecutor("update", updateExecutor);
        }
        
        /// <summary>
        /// Register a grouping tool
        /// </summary>
        public static void RegisterGroupingTool(string id, IBundleGroupingTool tool)
        {
            _groupingTools[id] = tool;
        }
        
        /// <summary>
        /// Register a build executor
        /// </summary>
        public static void RegisterBuildExecutor(string id, IBuildExecutor executor)
        {
            _buildExecutors[id] = executor;
        }
        
        /// <summary>
        /// Get a grouping tool by ID
        /// </summary>
        public static IBundleGroupingTool GetGroupingTool(string id)
        {
            if (_groupingTools.TryGetValue(id, out var tool))
            {
                return tool;
            }
            
            // Fallback to default
            return _groupingTools["default"];
        }
        
        /// <summary>
        /// Get a build executor by ID
        /// </summary>
        public static IBuildExecutor GetBuildExecutor(string id)
        {
            if (_buildExecutors.TryGetValue(id, out var executor))
            {
                return executor;
            }
            
            // Fallback to default
            return _buildExecutors["default"];
        }
        
        /// <summary>
        /// Get all registered grouping tool IDs
        /// </summary>
        public static List<string> GetGroupingToolIds()
        {
            return _groupingTools.Keys.ToList();
        }
        
        /// <summary>
        /// Get all registered build executor IDs
        /// </summary>
        public static List<string> GetBuildExecutorIds()
        {
            return _buildExecutors.Keys.ToList();
        }
        
        /// <summary>
        /// Get all registered grouping tools
        /// </summary>
        public static Dictionary<string, IBundleGroupingTool> GetAllGroupingTools()
        {
            return new Dictionary<string, IBundleGroupingTool>(_groupingTools);
        }
        
        /// <summary>
        /// Get all registered build executors
        /// </summary>
        public static Dictionary<string, IBuildExecutor> GetAllBuildExecutors()
        {
            return new Dictionary<string, IBuildExecutor>(_buildExecutors);
        }
    }
}
