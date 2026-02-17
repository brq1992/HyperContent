using System.IO;
using System.Linq;
using HyperContent.Editor.Build;
using UnityEditor;
using UnityEngine;

namespace HyperContent.Editor
{
    /// <summary>
    /// Editor window for HyperContent build configuration and execution
    /// </summary>
    public class HyperContentBuildWindow : EditorWindow
    {
        private BuildConfig _config;
        private Vector2 _scrollPosition;
        private bool _showAdvanced = false;
        
        [MenuItem("HyperContent/Build Window", false, 1)]
        public static void ShowWindow()
        {
            var window = GetWindow<HyperContentBuildWindow>("HyperContent Builder");
            window.minSize = new Vector2(400, 500);
            window.Show();
        }
        
        private void OnEnable()
        {
            LoadConfig();
        }
        
        private void OnGUI()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("HyperContent Build Configuration", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            
            if (_config == null)
            {
                _config = CreateDefaultConfig();
            }
            
            // Basic Settings
            EditorGUILayout.LabelField("Basic Settings", EditorStyles.boldLabel);
            _config.catalogName = EditorGUILayout.TextField("Catalog Name", _config.catalogName);
            _config.outputDirectory = EditorGUILayout.TextField("Output Directory", _config.outputDirectory);
            
            EditorGUILayout.Space();
            
            // Build Settings
            EditorGUILayout.LabelField("Build Settings", EditorStyles.boldLabel);
            _config.buildTarget = (BuildTarget)EditorGUILayout.EnumPopup("Build Target", _config.buildTarget);
            
            // Compression is determined by grouping tool, show info from plan if available
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Compression", GUILayout.Width(EditorGUIUtility.labelWidth));
            EditorGUILayout.LabelField("(Determined by Grouping Tool)", EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndHorizontal();
            
            // Show compression info from plan if available
            if (Application.isPlaying == false)
            {
                try
                {
                    var groupingTool = BuildToolFactory.GetGroupingTool(_config.groupingToolId);
                    var plan = groupingTool.GeneratePlan(_config);
                    if (plan.BundleCompression.Count > 0)
                    {
                        var uniqueCompressions = plan.BundleCompression.Values.Distinct().ToList();
                        var compressionInfo = string.Join(", ", uniqueCompressions);
                        EditorGUILayout.HelpBox($"Compression types in plan: {compressionInfo}", MessageType.Info);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("Compression will use default from BuildConfig", MessageType.Info);
                    }
                }
                catch
                {
                    // Ignore errors when generating plan for preview
                }
            }
            
            _config.includeDependencies = EditorGUILayout.Toggle("Include Dependencies", _config.includeDependencies);
            _config.forceRebuild = EditorGUILayout.Toggle("Force Rebuild", _config.forceRebuild);
            _config.generateReport = EditorGUILayout.Toggle("Generate Report", _config.generateReport);
            
            EditorGUILayout.Space();
            
            // Grouping Tool
            EditorGUILayout.LabelField("Grouping Tool", EditorStyles.boldLabel);
            var groupingTools = BuildToolFactory.GetAllGroupingTools();
            var groupingToolIds = groupingTools.Keys.ToList();
            var currentGroupingToolIndex = groupingToolIds.IndexOf(_config.groupingToolId);
            if (currentGroupingToolIndex < 0) currentGroupingToolIndex = 0;
            
            var newGroupingToolIndex = EditorGUILayout.Popup(
                "Grouping Tool",
                currentGroupingToolIndex,
                groupingToolIds.Select(id => $"{id} - {groupingTools[id].ToolName}").ToArray()
            );
            
            if (newGroupingToolIndex != currentGroupingToolIndex)
            {
                _config.groupingToolId = groupingToolIds[newGroupingToolIndex];
            }
            
            // Show tool description
            var selectedGroupingTool = BuildToolFactory.GetGroupingTool(_config.groupingToolId);
            EditorGUILayout.HelpBox(
                $"Tool: {selectedGroupingTool.ToolName}\n\n{selectedGroupingTool.Description}",
                MessageType.Info
            );
            
            EditorGUILayout.Space();
            
            // Build Executor
            EditorGUILayout.LabelField("Build Executor", EditorStyles.boldLabel);
            var executors = BuildToolFactory.GetAllBuildExecutors();
            var executorIds = executors.Keys.ToList();
            var currentExecutorIndex = executorIds.IndexOf(_config.buildExecutorId);
            if (currentExecutorIndex < 0) currentExecutorIndex = 0;
            
            var newExecutorIndex = EditorGUILayout.Popup(
                "Build Executor",
                currentExecutorIndex,
                executorIds.Select(id => $"{id} - {executors[id].ExecutorName}").ToArray()
            );
            
            if (newExecutorIndex != currentExecutorIndex)
            {
                _config.buildExecutorId = executorIds[newExecutorIndex];
            }
            
            // Show executor description
            var selectedExecutor = BuildToolFactory.GetBuildExecutor(_config.buildExecutorId);
            EditorGUILayout.HelpBox(
                $"Executor: {selectedExecutor.ExecutorName}\n\n{selectedExecutor.Description}",
                MessageType.Info
            );
            
            EditorGUILayout.Space();
            
            // Grouping Strategy (for backward compatibility, shown when using default grouping tool)
            if (_config.groupingToolId == "default")
            {
                EditorGUILayout.LabelField("Grouping Strategy", EditorStyles.boldLabel);
                _config.groupingStrategy = (BundleGroupingStrategyType)EditorGUILayout.EnumPopup(
                    "Strategy", 
                    _config.groupingStrategy
                );
                
                // Show strategy description
                var strategyName = BundleGroupingStrategyFactory.GetStrategyName(_config.groupingStrategy);
                EditorGUILayout.HelpBox(
                    $"Using: {strategyName}\n\n" +
                    (_config.groupingStrategy == BundleGroupingStrategyType.Addressable
                        ? "Assets will be grouped based on their Addressable group membership."
                        : "Assets will be grouped based on HyperContentAsset marker's bundleGroup field."),
                    MessageType.Info
                );
                
                EditorGUILayout.Space();
            }
            
            // Advanced Settings
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced Settings");
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox("Advanced settings are for power users only.", MessageType.Info);
                EditorGUI.indentLevel--;
            }
            
            EditorGUILayout.Space();
            
            // Build Buttons
            EditorGUILayout.BeginHorizontal();
            
            GUI.enabled = !EditorApplication.isCompiling;
            if (GUILayout.Button("Build", GUILayout.Height(30)))
            {
                Build();
            }
            
            if (GUILayout.Button("Validate Only", GUILayout.Height(30)))
            {
                ValidateOnly();
            }
            
            GUI.enabled = true;
            
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.Space();
            
            // Quick Actions
            EditorGUILayout.LabelField("Quick Actions", EditorStyles.boldLabel);
            if (GUILayout.Button("Open Output Directory"))
            {
                if (Directory.Exists(_config.outputDirectory))
                {
                    EditorUtility.RevealInFinder(_config.outputDirectory);
                }
                else
                {
                    EditorUtility.DisplayDialog("Directory Not Found", 
                        $"Output directory does not exist: {_config.outputDirectory}", "OK");
                }
            }
            
            EditorGUILayout.EndScrollView();
        }
        
        private void Build()
        {
            // Validate output directory
            if (string.IsNullOrEmpty(_config.outputDirectory))
            {
                EditorUtility.DisplayDialog("Invalid Configuration", "Output directory cannot be empty", "OK");
                return;
            }
            
            // Create output directory if it doesn't exist
            if (!Directory.Exists(_config.outputDirectory))
            {
                Directory.CreateDirectory(_config.outputDirectory);
            }
            
            // Save config
            SaveConfig();
            
            // Run build
            var result = HyperContentBuilder.Build(_config);
            
            if (result.IsSuccess)
            {
                EditorUtility.DisplayDialog("Build Success", 
                    $"Build completed successfully!\n\n" +
                    $"Bundles: {result.Context.BundleToAssets.Count}\n" +
                    $"Assets: {result.Context.AssetMarkers.Count}\n" +
                    $"Output: {_config.outputDirectory}", "OK");
                
                // Refresh asset database
                AssetDatabase.Refresh();
            }
            else
            {
                var errorCount = result.Context.Errors.Count;
                var warningCount = result.Context.Warnings.Count;
                
                EditorUtility.DisplayDialog("Build Failed", 
                    $"Build failed: {result.Message}\n\n" +
                    $"Errors: {errorCount}\n" +
                    $"Warnings: {warningCount}\n\n" +
                    "Check console for details.", "OK");
            }
        }
        
        private void ValidateOnly()
        {
            // Get grouping tool
            var groupingTool = BuildToolFactory.GetGroupingTool(_config.groupingToolId);
            
            // Validate tool
            var toolValidationErrors = groupingTool.Validate(_config);
            if (toolValidationErrors.Count > 0)
            {
                EditorUtility.DisplayDialog("Validation Failed", 
                    $"Grouping tool validation failed:\n\n" +
                    string.Join("\n", toolValidationErrors),
                    "OK");
                return;
            }
            
            // Generate plan
            var plan = groupingTool.GeneratePlan(_config);
            
            // Get executor
            var executor = BuildToolFactory.GetBuildExecutor(_config.buildExecutorId);
            
            // Validate executor
            var executorValidationErrors = executor.Validate(plan, _config);
            
            if (plan.Errors.Count == 0 && executorValidationErrors.Count == 0)
            {
                EditorUtility.DisplayDialog("Validation Success", 
                    $"Validation passed!\n\n" +
                    $"Assets: {plan.AssetMarkers.Count}\n" +
                    $"Bundles: {plan.BundleToAssets.Count}\n" +
                    $"Warnings: {plan.Warnings.Count}", "OK");
            }
            else
            {
                var errorCount = plan.Errors.Count + executorValidationErrors.Count;
                var warningCount = plan.Warnings.Count;
                
                EditorUtility.DisplayDialog("Validation Failed", 
                    $"Validation found issues:\n\n" +
                    $"Errors: {errorCount}\n" +
                    $"Warnings: {warningCount}\n\n" +
                    "Check console for details.", "OK");
            }
        }
        
        private BuildConfig CreateDefaultConfig()
        {
            return new BuildConfig
            {
                outputDirectory = "Assets/StreamingAssets",
                catalogName = "HyperContent_Catalog",
                buildTarget = EditorUserBuildSettings.activeBuildTarget,
                compressionType = BundleCompressionType.Lz4,
                includeDependencies = true,
                forceRebuild = false,
                generateReport = true,
                groupingStrategy = BundleGroupingStrategyType.MarkerBased,
                groupingToolId = "default",
                buildExecutorId = "default"
            };
        }
        
        private void LoadConfig()
        {
            var configPath = "ProjectSettings/HyperContentBuildConfig.json";
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                _config = JsonUtility.FromJson<BuildConfig>(json);
            }
            else
            {
                _config = CreateDefaultConfig();
            }
        }
        
        private void SaveConfig()
        {
            var configPath = "ProjectSettings/HyperContentBuildConfig.json";
            var json = JsonUtility.ToJson(_config, true);
            File.WriteAllText(configPath, json);
        }
    }
}
