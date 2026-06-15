using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Displays Update Build change detection result (Phase A only).
    /// Shows modified, new, removed, and dependency-expanded asset lists without running the build.
    /// </summary>
    public class UpdateDiffPreviewWindow : EditorWindow
    {
        private ChangeDetectionResult _result;
        private string _planExportDir;
        private Vector2 _scrollPosition;
        private bool _showModified = true;
        private bool _showNew = true;
        private bool _showRemoved = true;
        private bool _showExpanded = true;
        private readonly HashSet<string> _modifiedEntryExpanded = new HashSet<string>();

        /// <param name="pPlanExportDir">If set, full_build_plan.txt / update_build_plan.txt were written here for comparison.</param>
        public static void Show(ChangeDetectionResult pResult, string pPlanExportDir = null)
        {
            if (pResult == null)
                return;
            var window = GetWindow<UpdateDiffPreviewWindow>("Update Build — Diff Preview");
            window.minSize = new Vector2(420, 320);
            window._result = pResult;
            window._planExportDir = pPlanExportDir;
            window.Show();
        }

        private void OnGUI()
        {
            if (_result == null)
            {
                EditorGUILayout.HelpBox("No change detection result.", MessageType.Info);
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Content Update — Change Detection (Phase A)", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            if (!_result.HasChanges)
            {
                EditorGUILayout.HelpBox("No changes detected. Current assets match the last Full Build manifest.", MessageType.Info);
                EditorGUILayout.Space();
                DrawRemovedSection();
                DrawPlanExportDir();
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.HelpBox(
                "These are the assets that would be included in an Update Build. " +
                "Run \"Run Update Build\" on the HyperContent window Overview tab to execute the update build.",
                MessageType.Info);
            EditorGUILayout.Space();

            DrawModifiedSection();

            DrawSection("New (entry-level; not in last Full Build)", _result.newAssetList.Count,
                ref _showNew,
                _result.newAssetList,
                a => a.assetPath ?? a.guid,
                () => { foreach (var a in _result.newAssetList) PingAsset(a.guid); },
                a => PingAsset(a.guid));

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Expanded includes all Modified and New entries, plus any entries pulled in by dependency rules. The same asset can appear in both Modified and Expanded.",
                MessageType.None);
            DrawSection("Expanded (entry-level shipping set after dependency expansion)", _result.expandedChangedAssetList.Count,
                ref _showExpanded,
                _result.expandedChangedAssetList,
                a => a.assetPath ?? a.guid,
                () => { foreach (var a in _result.expandedChangedAssetList) PingAsset(a.guid); },
                a => PingAsset(a.guid));

            DrawRemovedSection();

            DrawPlanExportDir();

            EditorGUILayout.EndScrollView();
        }

        private void DrawPlanExportDir()
        {
            if (string.IsNullOrEmpty(_planExportDir))
                return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("BuildPlan comparison (GeneratePlan output)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "full_build_plan.txt = plan at Full Build; update_build_plan.txt = plan at Update/Preview. " +
                "Diff them to see if the issue is manifest vs change-detection logic.",
                MessageType.None);
            EditorGUILayout.LabelField("Directory: " + _planExportDir);
            if (GUILayout.Button("Open Plan Compare Folder"))
            {
                if (System.IO.Directory.Exists(_planExportDir))
                    EditorUtility.RevealInFinder(_planExportDir);
            }
        }

        private void DrawModifiedSection()
        {
            _showModified = EditorGUILayout.Foldout(_showModified,
                $"Modified (entry-level; content or deps changed): {_result.modifiedAssetList.Count}", true);
            if (!_showModified || _result.modifiedAssetList.Count == 0)
                return;
            EditorGUI.indentLevel++;
            if (_result.modifiedAssetList.Count > 0 && GUILayout.Button("Ping all in Project", GUILayout.Width(140)))
            {
                foreach (var a in _result.modifiedAssetList)
                    PingAsset(a.guid);
            }
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            foreach (var a in _result.modifiedAssetList)
            {
                var line = a.assetPath ?? a.guid;
                var hasCausingDeps = a.causingDependencyGuids != null && a.causingDependencyGuids.Count > 0;

                if (hasCausingDeps)
                {
                    var isExpanded = _modifiedEntryExpanded.Contains(a.guid);
                    var newExpanded = EditorGUILayout.Foldout(isExpanded, line, true);
                    if (newExpanded != isExpanded)
                    {
                        if (newExpanded) _modifiedEntryExpanded.Add(a.guid);
                        else _modifiedEntryExpanded.Remove(a.guid);
                    }
                    if (newExpanded)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.LabelField("Causing dependencies (only these changed):", EditorStyles.miniLabel);
                        foreach (var depGuid in a.causingDependencyGuids)
                        {
                            var depPath = AssetDatabase.GUIDToAssetPath(depGuid);
                            var depLine = string.IsNullOrEmpty(depPath) ? depGuid : depPath;
                            if (GUILayout.Button(depLine, EditorStyles.label))
                                PingAsset(depGuid);
                        }
                        EditorGUI.indentLevel--;
                    }
                }
                else
                {
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(line, EditorStyles.label))
                        PingAsset(a.guid);
                    GUILayout.Label("(entry itself changed)", EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUI.indentLevel--;
        }

        private void DrawRemovedSection()
        {
            DrawSection("Removed (in manifest, not in current plan)", _result.removedAssetList.Count,
                ref _showRemoved,
                _result.removedAssetList,
                a => a.guid,
                () => { foreach (var a in _result.removedAssetList) PingAsset(a.guid); },
                a => PingAsset(a.guid));
        }

        private void DrawSection<T>(string pTitle, int pCount, ref bool pFoldout,
            IReadOnlyList<T> pList, Func<T, string> pDisplay, Action pPingAll, Action<T> pPingOne)
        {
            pFoldout = EditorGUILayout.Foldout(pFoldout, $"{pTitle}: {pCount}", true);
            if (!pFoldout || pCount == 0)
                return;
            EditorGUI.indentLevel++;
            if (pCount > 0 && GUILayout.Button("Ping all in Project", GUILayout.Width(140)))
                pPingAll();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            foreach (var item in pList)
            {
                var line = pDisplay(item);
                if (GUILayout.Button(line, EditorStyles.label) && pPingOne != null)
                    pPingOne(item);
            }
            EditorGUILayout.EndVertical();
            EditorGUI.indentLevel--;
        }

        private static void PingAsset(string pGuid)
        {
            var path = AssetDatabase.GUIDToAssetPath(pGuid);
            if (!string.IsNullOrEmpty(path))
            {
                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (obj != null)
                    EditorGUIUtility.PingObject(obj);
            }
        }
    }
}
