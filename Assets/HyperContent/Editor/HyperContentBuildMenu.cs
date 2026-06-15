using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using com.igg.hypercontent.editor.simulation;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Menu items for HyperContent build operations
    /// </summary>
    public static class HyperContentBuildMenu
    {
        /// <summary>
        /// Gates game integration: AddressableManager HyperContent loader, BootStrapper catalog/init steps, batch HyperContent build, etc.
        /// </summary>
        public const string DefineEnableHyperContent = "ENABLE_HYPERCONTENT";

        /// <summary>
        /// Enables HCLogger Info/Warn (plus Error). Use the dedicated menu item to add this symbol.
        /// </summary>
        public const string DefineHyperContentLog = "HYPERCONTENT_LOG";

        /// <summary>
        /// Enables HCLogger Verbose traces. Use the dedicated menu item to add this symbol.
        /// </summary>
        public const string DefineHyperContentLogVerbose = "HYPERCONTENT_LOG_VERBOSE";

        /// <summary>
        /// Registers <see cref="PlayAssetDeliveryBundleProvider"/> on Android player builds
        /// (<c>UNITY_ANDROID &amp;&amp; !UNITY_EDITOR</c>). See <c>HyperContentImpl</c>.
        /// </summary>
        public const string DefineGooglePlayAssetDelivery = "GOOGLE_PLAY_ASSET_DELIVERY";

        private static readonly string[] DefaultIntegrationDefines =
        {
            DefineEnableHyperContent,
            DefineGooglePlayAssetDelivery,
        };

        [MenuItem("HyperContent/Enable Project Integration (Scripting Defines)", false, 0)]
        public static void EnableProjectIntegrationDefines()
        {
            var group = EditorUserBuildSettings.selectedBuildTargetGroup;
            AddDefinesToGroup(group, DefaultIntegrationDefines);
            HCLogger.LogInfo(
                $"HyperContent integration defines added for {group}: {string.Join(", ", DefaultIntegrationDefines)}. " +
                $"Use separate menu items for {DefineHyperContentLog} / {DefineHyperContentLogVerbose}. " +
                "Optional: HYPERCONTENT_TRACK_HANDLES for handle stacks (diagnostics).");
        }

        [MenuItem("HyperContent/Enable Project Integration (Scripting Defines)", true)]
        private static bool ValidateEnableProjectIntegrationDefines()
        {
            var group = EditorUserBuildSettings.selectedBuildTargetGroup;
            var current = GetDefineSymbolSet(group);
            return !DefaultIntegrationDefines.All(current.Contains);
        }

        [MenuItem("HyperContent/Add Scripting Define: HYPERCONTENT_LOG (HC Info & Warn)", false, 1)]
        public static void AddDefineHyperContentLog()
        {
            var group = EditorUserBuildSettings.selectedBuildTargetGroup;
            AddDefinesToGroup(group, DefineHyperContentLog);
            HCLogger.LogInfo($"Added {DefineHyperContentLog} for {group}.");
        }

        [MenuItem("HyperContent/Add Scripting Define: HYPERCONTENT_LOG (HC Info & Warn)", true)]
        private static bool ValidateAddDefineHyperContentLog()
        {
            return !GetDefineSymbolSet(EditorUserBuildSettings.selectedBuildTargetGroup).Contains(DefineHyperContentLog);
        }

        [MenuItem("HyperContent/Add Scripting Define: HYPERCONTENT_LOG_VERBOSE (HC Verbose)", false, 2)]
        public static void AddDefineHyperContentLogVerbose()
        {
            var group = EditorUserBuildSettings.selectedBuildTargetGroup;
            AddDefinesToGroup(group, DefineHyperContentLogVerbose);
            HCLogger.LogInfo($"Added {DefineHyperContentLogVerbose} for {group}.");
        }

        [MenuItem("HyperContent/Add Scripting Define: HYPERCONTENT_LOG_VERBOSE (HC Verbose)", true)]
        private static bool ValidateAddDefineHyperContentLogVerbose()
        {
            return !GetDefineSymbolSet(EditorUserBuildSettings.selectedBuildTargetGroup).Contains(DefineHyperContentLogVerbose);
        }

        private static void AddDefinesToGroup(BuildTargetGroup group, params string[] symbols)
        {
            var merged = GetDefineSymbolSet(group);
            foreach (var d in symbols)
            {
                if (!string.IsNullOrWhiteSpace(d))
                    merged.Add(d.Trim());
            }

            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", merged.OrderBy(s => s, StringComparer.Ordinal)));
            AssetDatabase.Refresh();
        }

        private static HashSet<string> GetDefineSymbolSet(BuildTargetGroup group)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            string raw = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            foreach (string part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = part.Trim();
                if (t.Length > 0)
                    set.Add(t);
            }

            return set;
        }

        [MenuItem("HyperContent/Update Editor Catalog", false, 5)]
        public static void UpdateEditorCatalog()
        {
            var config = GetDefaultConfig();
            bool success = EditorCatalogGenerator.Generate(config);
            if (success)
                HCLogger.LogInfo("Editor Catalog updated successfully.");
            else
                HCLogger.LogError("Editor Catalog update failed. Check Console.");

                
        }

        [MenuItem("HyperContent/Build", false, 10)]
        public static void Build()
        {
            var config = GetDefaultConfig();
            var result = HyperContentBuilder.Build(config);
            
            if (result.IsSuccess)
            {
                HCLogger.LogInfo($"Build completed successfully! Bundles: {result.Context.BundleToAssets.Count}, Assets: {result.Context.AssetMarkers.Count}");
                AssetDatabase.Refresh();
            }
            else
            {
                HCLogger.LogError($"Build failed: {result.Message}");
            }
        }
        
        [MenuItem("HyperContent/Build (Force Rebuild)", false, 11)]
        public static void BuildForceRebuild()
        {
            var config = GetDefaultConfig();
            config.forceRebuild = true;
            var result = HyperContentBuilder.Build(config);
            
            if (result.IsSuccess)
            {
                HCLogger.LogInfo("Build (Force Rebuild) completed successfully!");
                AssetDatabase.Refresh();
            }
            else
            {
                HCLogger.LogError($"Build (Force Rebuild) failed: {result.Message}");
            }
        }
        
        [MenuItem("HyperContent/Update Build", false, 12)]
        public static void UpdateBuild()
        {
            var config = HyperContentBuildWindow.LoadSavedBuildConfig();
            config.buildRemoteCatalog = true;
            HyperContentBuildWindow.PrepareAddressableSyncForUpdateBuild(config);
            var updateId = string.IsNullOrEmpty(config.updateBuildExecutorId) ? "update" : config.updateBuildExecutorId;
            var result = HyperContentBuilder.Build(config, updateId);

            if (result.IsSuccess)
            {
                HCLogger.LogInfo("Update Build completed successfully!");
                AssetDatabase.Refresh();
            }
            else
            {
                HCLogger.LogError($"Update Build failed: {result.Message}");
            }
        }

        [MenuItem("HyperContent/Validate", false, 20)]
        public static void Validate()
        {
            var context = new BuildContext
            {
                Config = GetDefaultConfig()
            };
            
            AssetCollector.CollectAssets(context);
            DependencyAnalyzer.AnalyzeDependencies(context);
            DependencyAnalyzer.AssignBundles(context);
            
            var isValid = BuildValidator.Validate(context);
            
            if (isValid && context.Errors.Count == 0)
            {
                HCLogger.LogInfo($"Validation passed! Assets: {context.AssetMarkers.Count}, Bundles: {context.BundleToAssets.Count}");
            }
            else
            {
                HCLogger.LogError($"Validation failed! Errors: {context.Errors.Count}, Warnings: {context.Warnings.Count}");
            }
        }
        
        private static BuildConfig GetDefaultConfig()
        {
            return new BuildConfig
            {
                buildTarget = EditorUserBuildSettings.activeBuildTarget,
                compressionType = BundleCompressionType.Lz4,
                includeDependencies = true,
                forceRebuild = false,
                generateReport = true
            };
        }
    }
}
