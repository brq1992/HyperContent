using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEditor;
using UnityEngine;
using com.igg.hypercontent.editor.simulation;
using com.igg.hypercontent.runtime;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// HyperContent: Overview (play mode, common actions) and Settings (full build configuration).
    /// </summary>
    public class HyperContentBuildWindow : EditorWindow
    {
        public const string BuildConfigPath = "ProjectSettings/HyperContentBuildConfig.json";

        private BuildConfig _config;

        private HyperContentMainTab _mainTab = HyperContentMainTab.Overview;
        private Vector2 _scrollOverview;
        private Vector2 _scrollSettings;
        private bool _playSettingsDirty;
        private bool _showAdvanced;
        private bool _showUpdateBuild;
        private bool _planPreviewDirty = true;
        private double _nextPlanPreviewRefreshAt;
        private string _planPreviewCompressionInfo;
        private string _planPreviewErrorMessage;
        private List<string> _groupingToolIdsList = new List<string>();
        private string[] _groupingToolDisplayArray = new string[0];
        private List<string> _executorIdsList = new List<string>();
        private string[] _executorDisplayArray = new string[0];
        private double _lastManifestStatusCheckAt = -1d;
        private string _lastManifestStatusPath;
        private bool _cachedManifestExists;

        [MenuItem("HyperContent/HyperContent Window", false, 0)]
        public static void OpenWindowFromMenu()
        {
            ShowWindow();
        }

        /// <summary>Opens the HyperContent editor window (Overview / Settings tabs).</summary>
        public static void ShowWindow()
        {
            var window = GetWindow<HyperContentBuildWindow>("HyperContent");
            window.minSize = new Vector2(420, 480);
            window.Show();
        }

        /// <summary>Load <see cref="BuildConfig"/> from <see cref="BuildConfigPath"/> with executor id defaults applied.</summary>
        public static BuildConfig LoadSavedBuildConfig()
        {
            if (File.Exists(BuildConfigPath))
            {
                var json = File.ReadAllText(BuildConfigPath);
                var c = JsonUtility.FromJson<BuildConfig>(json);
                if (c != null)
                {
                    NormalizeExecutorIds(c);
                    return c;
                }
            }

            var fresh = new BuildConfig();
            NormalizeExecutorIds(fresh);
            return fresh;
        }

        /// <summary>Regenerates EditorCatalog.json using saved HyperContent build config (no dialogs).</summary>
        public static bool GenerateEditorCatalogUsingSavedConfig()
        {
            var config = LoadSavedBuildConfig();
            return EditorCatalogGenerator.Generate(config);
        }

        private static void NormalizeExecutorIds(BuildConfig c)
        {
            if (c == null)
                return;
            if (string.IsNullOrEmpty(c.buildExecutorId))
                c.buildExecutorId = "default";
            if (string.IsNullOrEmpty(c.updateBuildExecutorId))
                c.updateBuildExecutorId = "update";
        }

        private void OnEnable()
        {
            LoadConfig();
            RefreshToolCaches();
            MarkPlanPreviewDirty(true);
        }

        private void OnGUI()
        {
            if (_config == null)
                _config = CreateDefaultConfig();

            EditorGUILayout.Space(6);
            var tabNames = new[] { "Overview", "Settings" };
            _mainTab = (HyperContentMainTab)GUILayout.Toolbar((int)_mainTab, tabNames);
            EditorGUILayout.Space(8);

            switch (_mainTab)
            {
                case HyperContentMainTab.Overview:
                    _scrollOverview = EditorGUILayout.BeginScrollView(_scrollOverview);
                    DrawOverviewTab();
                    EditorGUILayout.EndScrollView();
                    break;
                case HyperContentMainTab.Settings:
                    _scrollSettings = EditorGUILayout.BeginScrollView(_scrollSettings);
                    DrawSettingsTab();
                    EditorGUILayout.EndScrollView();
                    break;
            }
        }

        private void DrawOverviewTab()
        {
            var headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };
            EditorGUILayout.LabelField("HyperContent — Overview", headerStyle);
            EditorGUILayout.Space(4);
            DrawSeparator();
            EditorGUILayout.Space(8);

            EditorGUILayout.LabelField("Editor Play Mode", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var newMode = (EditorPlayMode)EditorGUILayout.EnumPopup(
                new GUIContent("Play Mode", "How assets are loaded when entering Play Mode in the Editor"),
                _config.editorPlayMode);
            if (EditorGUI.EndChangeCheck())
            {
                _config.editorPlayMode = newMode;
                _playSettingsDirty = true;
            }

            EditorGUILayout.Space(4);
            switch (_config.editorPlayMode)
            {
                case EditorPlayMode.UseAssetDatabase:
                    EditorGUILayout.HelpBox(
                        "Use Asset Database (Fastest)\n\n" +
                        "Assets are loaded via AssetDatabase.LoadAssetAtPath.\n" +
                        "Requires an Editor Catalog (see below).\n" +
                        "Note: Does not simulate real bundle dependencies.",
                        MessageType.Info);
                    break;
                case EditorPlayMode.UseExistingAssetBundle:
                    EditorGUILayout.HelpBox(
                        "Use Existing AssetBundle\n\n" +
                        "Loads from pre-built AssetBundles. Run Full Build on the Settings tab first.\n" +
                        "Matches runtime behavior on device.",
                        MessageType.Info);
                    break;
            }

            EditorGUILayout.Space(8);
            DrawSeparator();
            EditorGUILayout.Space(8);

            EditorGUILayout.LabelField("Editor Catalog", EditorStyles.boldLabel);
            bool editorCatalogExists = EditorCatalogGenerator.CatalogExists();
            if (editorCatalogExists)
                EditorGUILayout.HelpBox("Editor Catalog found. Ready for AssetDatabase mode.", MessageType.Info);
            else
            {
                var msgType = _config.editorPlayMode == EditorPlayMode.UseAssetDatabase
                    ? MessageType.Error
                    : MessageType.Warning;
                EditorGUILayout.HelpBox(
                    "Editor Catalog not found. Click 'Update Editor Catalog' to generate it.",
                    msgType);
            }

            EditorGUILayout.Space(4);
            GUI.enabled = !EditorApplication.isCompiling;
            if (GUILayout.Button("Update Editor Catalog", GUILayout.Height(28)))
            {
                if (_playSettingsDirty)
                    SaveConfig();
                GenerateEditorCatalogWithDialog();
            }
            GUI.enabled = true;

            EditorGUILayout.Space(8);
            DrawSeparator();
            EditorGUILayout.Space(8);

            EditorGUILayout.LabelField("Bundle Status", EditorStyles.boldLabel);
            bool bundlesExist = BundleOutputExists();
            string bundleStatus = bundlesExist ? "Built bundles found." : "No built bundles found.";
            var bundleIcon = bundlesExist ? MessageType.Info : MessageType.Warning;
            if (_config.editorPlayMode == EditorPlayMode.UseExistingAssetBundle && !bundlesExist)
            {
                bundleIcon = MessageType.Error;
                bundleStatus = "No built bundles found — Play Mode will fail! Run Full Build first.";
            }
            EditorGUILayout.HelpBox(bundleStatus, bundleIcon);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Executors (configure on Settings tab)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"Full build: {_config.buildExecutorId}    |    Update build: {_config.updateBuildExecutorId}",
                EditorStyles.miniLabel);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);

            GUI.enabled = !EditorApplication.isCompiling;
            if (GUILayout.Button("Build (Full)", GUILayout.Height(32)))
            {
                Build();
            }
            if (GUILayout.Button("Run Update Build", GUILayout.Height(32)))
            {
                RunUpdateBuild();
            }
            if (GUILayout.Button("Clear SBP Build Cache", GUILayout.Height(28)))
            {
                ClearScriptableBuildPipelineCache();
            }
            if (GUILayout.Button("Validate Only (Full)", GUILayout.Height(28)))
            {
                ValidateOnly();
            }
            GUI.enabled = true;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Quick — Folders", EditorStyles.boldLabel);
            if (GUILayout.Button("Open Bundle Output Directory"))
                RevealOrWarn(HyperContentPaths.BuildBundlePath, "Bundle output");
            if (GUILayout.Button("Open Catalog Output Directory"))
                RevealOrWarn(HyperContentPaths.BuildCatalogPath, "Catalog output");
            if (GUILayout.Button("Open Download Cache Bundle Directory"))
                RevealOrWarn(HyperContentPaths.CacheBundlePath, "Download cache bundle");

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = _playSettingsDirty;
            if (GUILayout.Button("Apply Play Mode", GUILayout.Height(28)))
            {
                SaveConfig();
                _playSettingsDirty = false;
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (_playSettingsDirty)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox("Play Mode changed — click Apply Play Mode to persist.", MessageType.Warning);
            }
        }

        private static void RevealOrWarn(string path, string label)
        {
            if (Directory.Exists(path))
                EditorUtility.RevealInFinder(path);
            else
                EditorUtility.DisplayDialog("Directory Not Found", $"{label} does not exist:\n{path}", "OK");
        }

        private void DrawSettingsTab()
        {
            EditorGUILayout.LabelField("HyperContent — Settings", EditorStyles.boldLabel);
            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField("Basic", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Bundle Output", HyperContentPaths.BuildBundlePath);
            EditorGUILayout.LabelField("Catalog Output", HyperContentPaths.BuildCatalogPath);
            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField("Build", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _config.buildTarget = (BuildTarget)EditorGUILayout.EnumPopup("Build Target", _config.buildTarget);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Compression", GUILayout.Width(EditorGUIUtility.labelWidth));
            EditorGUILayout.LabelField("(Determined by Grouping Tool)", EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndHorizontal();

            TryRefreshPlanPreview();
            if (!string.IsNullOrEmpty(_planPreviewErrorMessage))
                EditorGUILayout.HelpBox(_planPreviewErrorMessage, MessageType.Warning);
            else if (!string.IsNullOrEmpty(_planPreviewCompressionInfo))
                EditorGUILayout.HelpBox(_planPreviewCompressionInfo, MessageType.Info);
            else
                EditorGUILayout.HelpBox("Compression will use default from BuildConfig", MessageType.Info);

            _config.includeDependencies = EditorGUILayout.Toggle("Include Dependencies", _config.includeDependencies);
            _config.forceRebuild = EditorGUILayout.Toggle("Force Rebuild", _config.forceRebuild);
            _config.generateReport = EditorGUILayout.Toggle("Generate Report", _config.generateReport);
            if (EditorGUI.EndChangeCheck())
                MarkPlanPreviewDirty();

            _config.buildRemoteCatalog = EditorGUILayout.Toggle(
            new GUIContent(
                "Build Remote Catalog",
                "When enabled, Full Build writes remoteCatalogRelativePath / remoteCatalogHashRelativePath into hc/settings.json, " +
                "copies versioned .bin/.hash under ServerData (see Remote Catalog Build Folder in BuildConfig), " +
                "and allows runtime catalog hot-update (HasRemoteCatalog). When off, settings are local-only."),
            _config.buildRemoteCatalog);
            if (_config.buildRemoteCatalog)
            {
                EditorGUILayout.HelpBox(
                    $"Remote catalog output folder (project-relative): {_config.remoteCatalogBuildFolder}\n" +
                    "Upload the platform subfolder to CDN together with bundles.",
                    MessageType.Info);
            }

            EditorGUILayout.Space(6);
            EditorGUI.BeginChangeCheck();
            var newCatalogFormat = (CatalogSerializationFormat)EditorGUILayout.EnumPopup(
                new GUIContent(
                    "Catalog Format",
                    "Json — 可肉眼读、便于排查\n" +
                    "Binary — 紧凑二进制 (HCB1)，解析最快、文件 ~30-50% of Json\n" +
                    "BinaryGzip — HCB1 再过 GZip 压缩，文件 ~10-20% of Json，hot-update 流量友好"),
                _config.catalogFormat);
            if (EditorGUI.EndChangeCheck() && newCatalogFormat != _config.catalogFormat)
            {
                var oldFormat = _config.catalogFormat;
                _config.catalogFormat = newCatalogFormat;
                SaveConfig();
                PromptRebuildAfterCatalogFormatChange(oldFormat, newCatalogFormat);
            }
            EditorGUILayout.HelpBox(
                "切换格式必须重新 Full Build 才能生效（catalog 与 settings.json 必须同格式）。\n" +
                "Hot-update 不能跨格式：APK 决定后，下发的 catalog 必须与 APK 内置 settings.catalogFormat 一致。",
                MessageType.Info);

            EditorGUILayout.Space(6);
            EditorGUI.BeginChangeCheck();
            var newLoadMode = (DependencyLoadMode)EditorGUILayout.EnumPopup(
                new GUIContent(
                    "Dependency Load Mode",
                    "AssetLevel — 只加载资源自身真实依赖的 bundle；catalog 缺 asset 级数据时加载失败（默认）\n" +
                    "BundleLevel — 旧行为：加载 owning bundle 的整条 bundle 级传递闭包（全局回滚 / A-B 测试）"),
                _config.dependencyLoadMode);
            if (EditorGUI.EndChangeCheck() && newLoadMode != _config.dependencyLoadMode)
            {
                _config.dependencyLoadMode = newLoadMode;
                SaveConfig();
            }
            EditorGUILayout.HelpBox(
                "切换模式必须重新 Full Build 才能生效（固化进 settings.json.dependencyLoadMode，运行时不随 hot-update 改变）。\n" +
                "注意：SpriteAtlas 间接依赖只在 asset 级补全；BundleLevel 模式下仍可能漏加载 atlas（白图复现）。",
                MessageType.Info);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Grouping Tool", EditorStyles.boldLabel);
            var currentGroupingToolIndex = _groupingToolIdsList.IndexOf(_config.groupingToolId);
            if (currentGroupingToolIndex < 0) currentGroupingToolIndex = 0;

            EditorGUI.BeginChangeCheck();
            var newGroupingToolIndex = EditorGUILayout.Popup(
                "Grouping Tool",
                currentGroupingToolIndex,
                _groupingToolDisplayArray);

            if (newGroupingToolIndex != currentGroupingToolIndex && newGroupingToolIndex >= 0 &&
                newGroupingToolIndex < _groupingToolIdsList.Count)
                _config.groupingToolId = _groupingToolIdsList[newGroupingToolIndex];
            if (EditorGUI.EndChangeCheck())
                MarkPlanPreviewDirty();

            var selectedGroupingTool = BuildToolFactory.GetGroupingTool(_config.groupingToolId);
            EditorGUILayout.HelpBox(
                $"Tool: {selectedGroupingTool.ToolName}\n\n{selectedGroupingTool.Description}",
                MessageType.Info);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Build Executors", EditorStyles.boldLabel);

            var fullIdx = _executorIdsList.IndexOf(_config.buildExecutorId);
            if (fullIdx < 0) fullIdx = 0;
            EditorGUI.BeginChangeCheck();
            var newFullIdx = EditorGUILayout.Popup("Full Build Executor", fullIdx, _executorDisplayArray);
            if (newFullIdx != fullIdx && newFullIdx >= 0 && newFullIdx < _executorIdsList.Count)
                _config.buildExecutorId = _executorIdsList[newFullIdx];
            if (EditorGUI.EndChangeCheck())
                MarkPlanPreviewDirty();

            var fullExecutor = BuildToolFactory.GetBuildExecutor(_config.buildExecutorId);
            EditorGUILayout.HelpBox(
                $"Full: {fullExecutor.ExecutorName}\n\n{fullExecutor.Description}",
                MessageType.Info);

            var updateIdx = _executorIdsList.IndexOf(_config.updateBuildExecutorId);
            if (updateIdx < 0) updateIdx = _executorIdsList.IndexOf("update");
            if (updateIdx < 0) updateIdx = 0;
            EditorGUI.BeginChangeCheck();
            var newUpdateIdx = EditorGUILayout.Popup("Update Build Executor", updateIdx, _executorDisplayArray);
            if (newUpdateIdx != updateIdx && newUpdateIdx >= 0 && newUpdateIdx < _executorIdsList.Count)
                _config.updateBuildExecutorId = _executorIdsList[newUpdateIdx];
            if (EditorGUI.EndChangeCheck())
                MarkPlanPreviewDirty();

            var updateExecutor = BuildToolFactory.GetBuildExecutor(_config.updateBuildExecutorId);
            EditorGUILayout.HelpBox(
                $"Update: {updateExecutor.ExecutorName}\n\n{updateExecutor.Description}",
                MessageType.Info);

            EditorGUILayout.Space(8);

            if (_config.groupingToolId == "default")
            {
                EditorGUILayout.LabelField("Grouping Strategy", EditorStyles.boldLabel);
                _config.groupingStrategy = (BundleGroupingStrategyType)EditorGUILayout.EnumPopup(
                    "Strategy",
                    _config.groupingStrategy);
                var strategyName = BundleGroupingStrategyFactory.GetStrategyName(_config.groupingStrategy);
                EditorGUILayout.HelpBox(
                    $"Using: {strategyName}\n\n" +
                    (_config.groupingStrategy == BundleGroupingStrategyType.Addressable
                        ? "Assets grouped by Addressable group membership."
                        : "Assets grouped by HyperContentAsset marker bundleGroup."),
                    MessageType.Info);
                EditorGUILayout.Space(8);
            }

            _showUpdateBuild = EditorGUILayout.Foldout(_showUpdateBuild, "Content Update Build");
            if (_showUpdateBuild)
            {
                EditorGUI.indentLevel++;
                var manifestPath = _config.BuildManifestPath;
                EditorGUILayout.LabelField("Manifest Path", manifestPath);
                RefreshManifestStatus(manifestPath);
                EditorGUILayout.LabelField("Manifest Status",
                    _cachedManifestExists ? "Found (ready for Update Build)" : "Not found (run Full Build first)");

                _config.updateBundleGroupingStrategy = (UpdateBundleGroupingStrategyType)EditorGUILayout.EnumPopup(
                    "Update Grouping Strategy", _config.updateBundleGroupingStrategy);

                _config.syncAddressableGroupsAfterUpdateBuild = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Sync Addressable update groups after Update Build",
                        "When enabled, after a successful Update Build, syncs Addressable Content Update groups from the B1 update mapping."),
                    _config.syncAddressableGroupsAfterUpdateBuild);

                _config.remoteBundleLoadUrl = EditorGUILayout.TextField(
                    "CDN root (bundles + catalog)", _config.remoteBundleLoadUrl);
                EditorGUILayout.HelpBox(
                    "No platform segment. Runtime builds URLs as {root}/{platform}/… for bundles and remote catalog/hash.",
                    MessageType.None);

                EditorGUILayout.Space(4);
                GUI.enabled = !EditorApplication.isCompiling && _cachedManifestExists;
                if (GUILayout.Button("Preview Update Diff", GUILayout.Height(24)))
                    PreviewUpdateDiff();
                GUI.enabled = true;

                if (!_cachedManifestExists)
                {
                    EditorGUILayout.HelpBox(
                        "Build manifest not found. Run a Full Build first to create the baseline manifest.",
                        MessageType.Warning);
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(8);
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced / Experimental");
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox("Experimental build options. Persisted in ProjectSettings/HyperContentBuildConfig.json.", MessageType.Info);
                _config.stripUnityVersionFromBundleHeaders = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Strip Unity version from bundle headers",
                        "Matches Addressables default (off): omit ContentBuildFlags.StripUnityVersion. " +
                        "Enable only for A/B tests (bundle hash layout, cross-Unity-version compatibility); " +
                        "MuseumUI profiling showed no meaningful load-time win from stripping."),
                    _config.stripUnityVersionFromBundleHeaders);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Save Settings to Disk", GUILayout.Height(26)))
            {
                SaveConfig();
                EditorUtility.DisplayDialog("HyperContent", "Settings saved.", "OK");
            }
        }

        /// <summary>
        /// Catalog format 改变后立刻提示用户跑一次 Full Build，避免出现"settings.json 已写新 format
        /// 但 catalog 文件还是旧 format"的中间过渡态（runtime 会按 CATALOG_INVALID_FORMAT 失败）。
        /// 用户选 "Later" 时新 format 已落盘到 BuildConfig，下次构建自动生效。
        /// </summary>
        private void PromptRebuildAfterCatalogFormatChange(
            CatalogSerializationFormat oldFormat, CatalogSerializationFormat newFormat)
        {
            bool buildNow = EditorUtility.DisplayDialog(
                "Catalog Format Changed",
                $"Catalog format changed from {oldFormat} to {newFormat}.\n\n" +
                "This requires a Full Build to regenerate both catalog and settings.json " +
                "(otherwise runtime will fail with CATALOG_INVALID_FORMAT).\n\n" +
                "Run Build (Full) now?",
                "Build (Full) Now",
                "Later");

            if (buildNow)
                Build();
        }

        private void GenerateEditorCatalogWithDialog()
        {
            bool success = GenerateEditorCatalogUsingSavedConfigFromUiState();

            if (success)
            {
                EditorUtility.DisplayDialog("Editor Catalog",
                    "Editor Catalog generated successfully!\n\n" +
                    $"Path: {EditorCatalogGenerator.CatalogPath}",
                    "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("Editor Catalog",
                    "Editor Catalog generation failed.\nCheck Console for details.",
                    "OK");
            }
        }

        /// <summary>Uses current in-memory config if dirty has been saved; otherwise loads from disk.</summary>
        private bool GenerateEditorCatalogUsingSavedConfigFromUiState()
        {
            SaveConfig();
            return EditorCatalogGenerator.Generate(_config);
        }

        private void Build()
        {
            var bundleOutputDir = HyperContentPaths.BuildBundlePath;
            var catalogOutputDir = HyperContentPaths.BuildCatalogPath;
            if (!Directory.Exists(bundleOutputDir))
                Directory.CreateDirectory(bundleOutputDir);
            if (!Directory.Exists(catalogOutputDir))
                Directory.CreateDirectory(catalogOutputDir);

            SaveConfig();

            Debug.LogWarning($"[HyperContent][FullBuild] >>> 'Build (Full)' button clicked at " +
                $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} — this WILL OVERWRITE build_manifest.json " +
                $"(use 'Run Update Build' instead for hot updates).");

            var result = HyperContentBuilder.Build(_config);

            if (result.IsSuccess)
            {
                EditorUtility.DisplayDialog("Build Success",
                    $"Build completed successfully!\n\n" +
                    $"Bundles: {result.Context.BundleToAssets.Count}\n" +
                    $"Assets: {result.Context.AssetMarkers.Count}\n" +
                    $"Bundles: {HyperContentPaths.BuildBundlePath}\n" +
                    $"Catalog: {HyperContentPaths.BuildCatalogPath}", "OK");
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

        private void PreviewUpdateDiff()
        {
            SaveConfig();

            var groupingTool = BuildToolFactory.GetGroupingTool(_config.groupingToolId);
            var toolErrors = groupingTool.Validate(_config);
            if (toolErrors.Count > 0)
            {
                EditorUtility.DisplayDialog("Preview Update Diff",
                    "Grouping tool validation failed:\n\n" + string.Join("\n", toolErrors), "OK");
                return;
            }

            var plan = groupingTool.GeneratePlan(_config);
            if (plan.Errors.Count > 0)
            {
                EditorUtility.DisplayDialog("Preview Update Diff",
                    "Build plan errors:\n\n" + string.Join("\n", plan.Errors.ConvertAll(e => e.Message)), "OK");
                return;
            }

            var errorList = new List<BuildError>();
            var changeResult = ContentChangeDetector.DetectChanges(_config, plan, errorList);

            if (changeResult == null)
            {
                var msg = errorList.Count > 0
                    ? string.Join("\n", errorList.ConvertAll(e => e.Message))
                    : "Manifest not found or invalid. Run Full Build first.";
                EditorUtility.DisplayDialog("Preview Update Diff", msg, "OK");
                return;
            }

            if (errorList.Count > 0)
            {
                EditorUtility.DisplayDialog("Preview Update Diff",
                    "Change detection reported errors:\n\n" + string.Join("\n", errorList.ConvertAll(e => e.Message)), "OK");
            }

            var planCompareDir = BuildPlanExporter.GetPlanCompareDirectory(_config);
            if (!string.IsNullOrEmpty(planCompareDir))
                BuildPlanExporter.ExportBuildPlan(plan, planCompareDir, "update_build_plan.txt");

            UpdateDiffPreviewWindow.Show(changeResult, planCompareDir);
        }

        private void RunUpdateBuild()
        {
            SaveConfig();

            PrepareAddressableSyncForUpdateBuild(_config);

            var updateId = string.IsNullOrEmpty(_config.updateBuildExecutorId) ? "update" : _config.updateBuildExecutorId;
            _config.buildRemoteCatalog = true;

            Debug.Log($"[HyperContent][UpdateBuild] >>> 'Run Update Build' button clicked at " +
                $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} — executorId='{updateId}', " +
                $"manifestPath='{_config.BuildManifestPath}'");

            var result = HyperContentBuilder.Build(_config, updateId);

            if (result.IsSuccess)
            {
                EditorUtility.DisplayDialog("Update Build Success",
                    "Update Build completed successfully!\n\n" +
                    "Check ServerData/ for update bundles and remote catalog.",
                    "OK");
                AssetDatabase.Refresh();
            }
            else
            {
                EditorUtility.DisplayDialog("Update Build Failed",
                    $"Update Build failed: {result.Message}\n\n" +
                    $"Errors: {result.Context.Errors.Count}\n" +
                    "Check console for details.", "OK");
            }
        }

        private void ValidateOnly()
        {
            var groupingTool = BuildToolFactory.GetGroupingTool(_config.groupingToolId);
            var toolValidationErrors = groupingTool.Validate(_config);
            if (toolValidationErrors.Count > 0)
            {
                EditorUtility.DisplayDialog("Validation Failed",
                    $"Grouping tool validation failed:\n\n" +
                    string.Join("\n", toolValidationErrors),
                    "OK");
                return;
            }

            var plan = groupingTool.GeneratePlan(_config);
            var executor = BuildToolFactory.GetBuildExecutor(_config.buildExecutorId);
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
                buildTarget = EditorUserBuildSettings.activeBuildTarget,
                compressionType = BundleCompressionType.Lz4,
                includeDependencies = true,
                forceRebuild = false,
                generateReport = true,
                groupingStrategy = BundleGroupingStrategyType.MarkerBased,
                groupingToolId = "default",
                buildExecutorId = "default",
                updateBuildExecutorId = "update"
            };
        }

        private void LoadConfig()
        {
            if (File.Exists(BuildConfigPath))
            {
                var json = File.ReadAllText(BuildConfigPath);
                _config = JsonUtility.FromJson<BuildConfig>(json) ?? CreateDefaultConfig();
            }
            else
            {
                _config = CreateDefaultConfig();
            }

            NormalizeExecutorIds(_config);
            _playSettingsDirty = false;
        }

        private void SaveConfig()
        {
            NormalizeExecutorIds(_config);
            var json = JsonUtility.ToJson(_config, true);
            File.WriteAllText(BuildConfigPath, json);
            PlayModeSettings.InvalidateCache();
        }

        private void RefreshToolCaches()
        {
            var groupingTools = BuildToolFactory.GetAllGroupingTools();
            _groupingToolIdsList = groupingTools.Keys.ToList();
            _groupingToolDisplayArray = _groupingToolIdsList
                .Select(pId => $"{pId} - {groupingTools[pId].ToolName}")
                .ToArray();

            var executors = BuildToolFactory.GetAllBuildExecutors();
            _executorIdsList = executors.Keys.ToList();
            _executorDisplayArray = _executorIdsList
                .Select(pId => $"{pId} - {executors[pId].ExecutorName}")
                .ToArray();
        }

        private void MarkPlanPreviewDirty(bool pImmediate = false)
        {
            _planPreviewDirty = true;
            _nextPlanPreviewRefreshAt = pImmediate ? EditorApplication.timeSinceStartup : EditorApplication.timeSinceStartup + 0.35d;
        }

        private void TryRefreshPlanPreview()
        {
            if (!_planPreviewDirty || Application.isPlaying || EditorApplication.isCompiling)
                return;
            if (EditorApplication.timeSinceStartup < _nextPlanPreviewRefreshAt)
                return;

            try
            {
                var groupingTool = BuildToolFactory.GetGroupingTool(_config.groupingToolId);
                var plan = groupingTool.GeneratePlan(_config);
                if (plan.BundleToAssets.Count > 0)
                {
                    var invalid = BuildPlanCompressionValidator.ValidatePlan(plan);
                    if (!string.IsNullOrEmpty(invalid))
                        _planPreviewCompressionInfo = $"Compression: INVALID — {invalid}";
                    else
                    {
                        var uniqueCompressions = plan.BundleCompression.Values.Distinct().ToList();
                        _planPreviewCompressionInfo = $"Compression types in plan: {string.Join(", ", uniqueCompressions)}";
                    }
                }
                else
                    _planPreviewCompressionInfo = null;

                _planPreviewErrorMessage = null;
            }
            catch (Exception pException)
            {
                _planPreviewCompressionInfo = null;
                _planPreviewErrorMessage = $"Compression preview unavailable: {pException.Message}";
            }
            finally
            {
                _planPreviewDirty = false;
            }
        }

        private static void ClearScriptableBuildPipelineCache()
        {
            if (!EditorUtility.DisplayDialog(
                "Clear SBP Build Cache",
                "This clears the Scriptable Build Pipeline build cache (BuildCache.PurgeCache). " +
                "The next HyperContent / Addressables SBP bundle build may take longer.\n\nContinue?",
                "Clear",
                "Cancel"))
                return;

            try
            {
                BuildCache.PurgeCache(false);
                Debug.Log("[HyperContent] SBP BuildCache purged.");
                EditorUtility.DisplayDialog("SBP Build Cache", "SBP build cache was cleared.", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HyperContent] Failed to purge SBP BuildCache: {ex.Message}");
                EditorUtility.DisplayDialog("SBP Build Cache", $"Failed to clear cache:\n{ex.Message}", "OK");
            }
        }

        private bool BundleOutputExists()
        {
            string bundleDir = HyperContentPaths.BuildBundlePath;
            if (!Directory.Exists(bundleDir)) return false;
            return Directory.GetFileSystemEntries(bundleDir).Length > 0;
        }

        private static void DrawSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        }

        private void RefreshManifestStatus(string pManifestPath)
        {
            var now = EditorApplication.timeSinceStartup;
            if (_lastManifestStatusPath != pManifestPath || now - _lastManifestStatusCheckAt > 0.5d)
            {
                _cachedManifestExists = File.Exists(pManifestPath);
                _lastManifestStatusPath = pManifestPath;
                _lastManifestStatusCheckAt = now;
            }
        }

        /// <summary>
        /// Wires <see cref="BuildConfig.onAfterUpdateBuildSucceeded"/> via <c>Assembly_Editor</c> facade (no asm reference from HyperContent.Editor).
        /// </summary>
        public static void PrepareAddressableSyncForUpdateBuild(BuildConfig pConfig)
        {
            if (pConfig == null)
                return;

            Type facadeType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Assembly_Editor")
                    continue;
                facadeType = asm.GetType("com.igg.editor.HyperContentAddressableSyncFacade");
                if (facadeType != null)
                    break;
            }

            if (facadeType != null)
            {
                var method = facadeType.GetMethod(
                    "PrepareForUpdateBuild",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null)
                {
                    try
                    {
                        method.Invoke(null, new object[] { pConfig });
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[HyperContent] PrepareForUpdateBuild failed: {ex.InnerException ?? ex}");
                    }

                    return;
                }
            }

            Debug.LogWarning(
                "[HyperContent] HyperContentAddressableSyncFacade not found; Addressable post–Update Build sync not wired.");
            if (!pConfig.syncAddressableGroupsAfterUpdateBuild)
                pConfig.onAfterUpdateBuildSucceeded = null;
        }
    }

    public enum HyperContentMainTab
    {
        Overview = 0,
        Settings = 1
    }
}
