using System.IO;
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
            _config.compressionType = (BundleCompressionType)EditorGUILayout.EnumPopup("Compression", _config.compressionType);
            _config.includeDependencies = EditorGUILayout.Toggle("Include Dependencies", _config.includeDependencies);
            _config.forceRebuild = EditorGUILayout.Toggle("Force Rebuild", _config.forceRebuild);
            _config.generateReport = EditorGUILayout.Toggle("Generate Report", _config.generateReport);
            
            EditorGUILayout.Space();
            
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
            var context = new BuildContext
            {
                Config = _config
            };
            
            // Collect assets
            AssetCollector.CollectAssets(context);
            
            // Analyze dependencies
            DependencyAnalyzer.AnalyzeDependencies(context);
            
            // Assign bundles
            DependencyAnalyzer.AssignBundles(context);
            
            // Validate
            var isValid = BuildValidator.Validate(context);
            
            if (isValid && context.Errors.Count == 0)
            {
                EditorUtility.DisplayDialog("Validation Success", 
                    $"Validation passed!\n\n" +
                    $"Assets: {context.AssetMarkers.Count}\n" +
                    $"Bundles: {context.BundleToAssets.Count}\n" +
                    $"Warnings: {context.Warnings.Count}", "OK");
            }
            else
            {
                var errorCount = context.Errors.Count;
                var warningCount = context.Warnings.Count;
                
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
                catalogName = "default_catalog",
                buildTarget = EditorUserBuildSettings.activeBuildTarget,
                compressionType = BundleCompressionType.Lz4,
                includeDependencies = true,
                forceRebuild = false,
                generateReport = true
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
