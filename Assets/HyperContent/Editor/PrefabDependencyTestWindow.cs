using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Quick test window: drag a prefab in, list all its dependencies, and split them into
    /// direct dependencies vs indirect (transitive) dependencies.
    /// Menu: HyperContent / Test / Prefab Dependency Viewer.
    /// </summary>
    public class PrefabDependencyTestWindow : EditorWindow
    {
        private Object _targetPrefab;
        private Vector2 _scrollPosition;
        private bool _includeSelf = false;

        private readonly List<string> _directDependencies = new List<string>();
        private readonly List<string> _indirectDependencies = new List<string>();
        private readonly HashSet<string> _atlasPaths = new HashSet<string>();
        private bool _showDirect = true;
        private bool _showIndirect = true;

        [MenuItem("HyperContent/Test/Prefab Dependency Viewer")]
        public static void Open()
        {
            var window = GetWindow<PrefabDependencyTestWindow>("Prefab Dependencies");
            window.minSize = new Vector2(460, 360);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Prefab Dependency Viewer (Test)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Drag a prefab into the field (or the drop area) below. The window lists every dependency, " +
                "separating direct dependencies from indirect (transitive) ones.",
                MessageType.Info);

            EditorGUILayout.Space();
            DrawDropArea();

            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            _targetPrefab = EditorGUILayout.ObjectField("Target Prefab", _targetPrefab, typeof(Object), false);
            bool includeSelf = EditorGUILayout.ToggleLeft("Include the prefab itself in results", _includeSelf);
            if (EditorGUI.EndChangeCheck())
            {
                _includeSelf = includeSelf;
                Analyze();
            }

            using (new EditorGUI.DisabledScope(_targetPrefab == null))
            {
                if (GUILayout.Button("Analyze Dependencies"))
                {
                    Analyze();
                }
            }

            EditorGUILayout.Space();

            if (_targetPrefab == null)
            {
                EditorGUILayout.HelpBox("No prefab selected.", MessageType.None);
                return;
            }

            EditorGUILayout.LabelField(
                $"Total: {_directDependencies.Count + _indirectDependencies.Count}  " +
                $"(Direct: {_directDependencies.Count}, Indirect: {_indirectDependencies.Count})",
                EditorStyles.miniBoldLabel);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            DrawSection($"Direct Dependencies ({_directDependencies.Count})", ref _showDirect, _directDependencies);
            EditorGUILayout.Space();
            DrawSection($"Indirect Dependencies ({_indirectDependencies.Count})", ref _showIndirect, _indirectDependencies);

            EditorGUILayout.EndScrollView();
        }

        private void DrawDropArea()
        {
            var dropRect = GUILayoutUtility.GetRect(0f, 48f, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, "Drop Prefab Here", EditorStyles.helpBox);

            var evt = Event.current;
            if (!dropRect.Contains(evt.mousePosition))
                return;

            switch (evt.type)
            {
                case EventType.DragUpdated:
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    evt.Use();
                    break;
                case EventType.DragPerform:
                    DragAndDrop.AcceptDrag();
                    if (DragAndDrop.objectReferences.Length > 0)
                    {
                        _targetPrefab = DragAndDrop.objectReferences[0];
                        Analyze();
                    }
                    evt.Use();
                    break;
            }
        }

        private void DrawSection(string pTitle, ref bool pFoldout, List<string> pPaths)
        {
            pFoldout = EditorGUILayout.Foldout(pFoldout, pTitle, true);
            if (!pFoldout)
                return;

            if (pPaths.Count == 0)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("(none)");
                EditorGUI.indentLevel--;
                return;
            }

            EditorGUI.indentLevel++;
            for (int i = 0; i < pPaths.Count; i++)
            {
                string path = pPaths[i];
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{i + 1}.", GUILayout.Width(32f));

                var asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (asset != null)
                {
                    var icon = AssetDatabase.GetCachedIcon(path);
                    string label = _atlasPaths.Contains(path) ? " " + path + "   [SpriteAtlas]" : " " + path;
                    var content = new GUIContent(label, icon);
                    var prevIconSize = EditorGUIUtility.GetIconSize();
                    EditorGUIUtility.SetIconSize(new Vector2(16f, 16f));
                    if (GUILayout.Button(content, EditorStyles.label, GUILayout.ExpandWidth(true)))
                    {
                        EditorGUIUtility.PingObject(asset);
                        Selection.activeObject = asset;
                    }
                    EditorGUIUtility.SetIconSize(prevIconSize);
                }
                else
                {
                    EditorGUILayout.LabelField(path);
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;
        }

        private void Analyze()
        {
            _directDependencies.Clear();
            _indirectDependencies.Clear();
            _atlasPaths.Clear();

            if (_targetPrefab == null)
                return;

            string assetPath = AssetDatabase.GetAssetPath(_targetPrefab);
            if (string.IsNullOrEmpty(assetPath))
                return;

            // Direct dependencies only (recursive: false).
            var direct = new HashSet<string>(AssetDatabase.GetDependencies(assetPath, false));
            // All dependencies (recursive: true) = direct + indirect.
            var all = new HashSet<string>(AssetDatabase.GetDependencies(assetPath, true));

            if (!_includeSelf)
            {
                direct.Remove(assetPath);
                all.Remove(assetPath);
            }

            // GetDependencies only returns the source texture of a packed sprite, not the SpriteAtlas
            // that actually carries it at runtime. Resolve those atlases and include them as dependencies
            // in the same category (direct/indirect) as their source texture.
            var sourceToAtlas = BuildSpriteSourceToAtlasMap();
            if (sourceToAtlas.Count > 0)
            {
                foreach (var path in all.ToArray())
                {
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrEmpty(guid))
                        continue;
                    if (!sourceToAtlas.TryGetValue(guid, out var atlasGuid))
                        continue;

                    string atlasPath = AssetDatabase.GUIDToAssetPath(atlasGuid);
                    if (string.IsNullOrEmpty(atlasPath))
                        continue;

                    _atlasPaths.Add(atlasPath);
                    all.Add(atlasPath);
                    // The atlas is referenced at the same level as its source sprite.
                    if (direct.Contains(path))
                        direct.Add(atlasPath);
                }
            }

            foreach (var path in all)
            {
                if (direct.Contains(path))
                    _directDependencies.Add(path);
                else
                    _indirectDependencies.Add(path);
            }

            _directDependencies.Sort(System.StringComparer.OrdinalIgnoreCase);
            _indirectDependencies.Sort(System.StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Maps each packed source-texture GUID to the SpriteAtlas GUID that contains it.
        /// Mirrors the logic in DefaultBuildExecutor so the test window resolves the same atlas references.
        /// </summary>
        private static Dictionary<string, string> BuildSpriteSourceToAtlasMap()
        {
            var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

            var atlasGuids = AssetDatabase.FindAssets("t:SpriteAtlas");
            if (atlasGuids == null || atlasGuids.Length == 0)
                return map;

            foreach (var atlasGuid in atlasGuids)
            {
                var atlasPath = AssetDatabase.GUIDToAssetPath(atlasGuid);
                if (string.IsNullOrEmpty(atlasPath))
                    continue;

                var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
                if (atlas == null)
                    continue;

                var packables = atlas.GetPackables();
                if (packables == null)
                    continue;

                foreach (var packable in packables)
                {
                    if (packable == null)
                        continue;
                    var packablePath = AssetDatabase.GetAssetPath(packable);
                    if (string.IsNullOrEmpty(packablePath))
                        continue;

                    if (AssetDatabase.IsValidFolder(packablePath))
                    {
                        // Folder packable: every texture under it is packed into this atlas.
                        var inFolder = AssetDatabase.FindAssets("t:Texture", new[] { packablePath });
                        if (inFolder == null) continue;
                        foreach (var texGuid in inFolder)
                            map[texGuid] = atlasGuid;
                    }
                    else
                    {
                        var texGuid = AssetDatabase.AssetPathToGUID(packablePath);
                        if (!string.IsNullOrEmpty(texGuid))
                            map[texGuid] = atlasGuid;
                    }
                }
            }

            return map;
        }
    }
}
