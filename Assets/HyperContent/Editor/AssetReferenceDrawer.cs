using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using com.igg.hypercontent;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Custom PropertyDrawer for AssetReference and AssetReference&lt;T&gt;.
    ///
    /// Renders an ObjectField in the Inspector. When the user assigns an asset,
    /// the drawer resolves its GUID via AssetDatabase and writes it into the
    /// serialized _assetGuid backing field.  The loaded object displayed in the
    /// field is re-resolved from the stored GUID each repaint so that the reference
    /// survives asset moves and renames.
    ///
    /// Type constraint for AssetReference&lt;T&gt; is inferred via reflection so only
    /// assets of the declared type (or a subtype) are accepted in the ObjectField.
    ///
    /// Covers both:
    ///   [SerializeField] AssetReference            _ref;         // non-generic
    ///   [SerializeField] AssetReference&lt;Texture2D&gt;  _ref;         // typed
    /// </summary>
    [CustomPropertyDrawer(typeof(AssetReference), useForChildren: true)]
    [CustomPropertyDrawer(typeof(AssetReference<>), useForChildren: true)]
    public sealed class AssetReferenceDrawer : PropertyDrawer
    {
        private const string GUID_FIELD_NAME = "_assetGuid";

        public override float GetPropertyHeight(SerializedProperty pProperty, GUIContent pLabel)
        {
            return EditorGUIUtility.singleLineHeight;
        }

        public override void OnGUI(Rect pPosition, SerializedProperty pProperty, GUIContent pLabel)
        {
            EditorGUI.BeginProperty(pPosition, pLabel, pProperty);

            var guidProp = pProperty.FindPropertyRelative(GUID_FIELD_NAME);
            if (guidProp == null)
            {
                EditorGUI.HelpBox(pPosition,
                    $"[AssetReference] Cannot find backing field '{GUID_FIELD_NAME}'.", MessageType.Error);
                EditorGUI.EndProperty();
                return;
            }

            Type assetType = ResolveAssetType(fieldInfo);
            UnityEngine.Object currentObj = GuidToObject(guidProp.stringValue, assetType);

            EditorGUI.BeginChangeCheck();
            var newObj = EditorGUI.ObjectField(pPosition, pLabel, currentObj, assetType, allowSceneObjects: false);
            if (EditorGUI.EndChangeCheck())
            {
                guidProp.stringValue = ObjectToGuidValidated(newObj);
            }

            EditorGUI.EndProperty();
        }

        // ── Helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the concrete asset type constraint from the field's declared type.
        /// For AssetReference&lt;T&gt; returns T; for the non-generic base returns UnityEngine.Object.
        /// </summary>
        private static Type ResolveAssetType(FieldInfo pFieldInfo)
        {
            if (pFieldInfo == null)
                return typeof(UnityEngine.Object);

            Type fieldType = pFieldInfo.FieldType;

            // Unwrap arrays and Lists so [SerializeField] List<AssetReference<T>> also works.
            if (fieldType.IsArray)
                fieldType = fieldType.GetElementType();
            else if (fieldType.IsGenericType &&
                     fieldType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.List<>))
                fieldType = fieldType.GetGenericArguments()[0];

            // Walk up the inheritance chain to find AssetReference<T>
            for (Type t = fieldType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(AssetReference<>))
                    return t.GetGenericArguments()[0];
            }

            return typeof(UnityEngine.Object);
        }

        /// <summary>
        /// Resolves the stored GUID back to a Unity asset object for display.
        /// Returns null when the GUID is empty or the asset no longer exists.
        /// </summary>
        private static UnityEngine.Object GuidToObject(string pGuid, Type pAssetType)
        {
            if (string.IsNullOrEmpty(pGuid))
                return null;

            string assetPath = AssetDatabase.GUIDToAssetPath(pGuid);
            if (string.IsNullOrEmpty(assetPath))
                return null;

            return AssetDatabase.LoadAssetAtPath(assetPath, pAssetType);
        }

        /// <summary>
        /// Converts a Unity asset object to its GUID string, with HyperContent catalog
        /// validation. When the asset is not registered, prompts the user to regenerate
        /// the EditorCatalog immediately. Returns empty string (rejects the assignment)
        /// if the asset is still not registered after the prompt is handled.
        /// Returns an empty string when pObject is null (clears the reference).
        /// </summary>
        private static string ObjectToGuidValidated(UnityEngine.Object pObject)
        {
            if (pObject == null)
                return string.Empty;

            string assetPath = AssetDatabase.GetAssetPath(pObject);
            if (string.IsNullOrEmpty(assetPath))
                return string.Empty;

            string guid = AssetDatabase.AssetPathToGUID(assetPath);

            if (AssetReferenceCatalogValidator.IsRegistered(guid))
                return guid;

            TryPromptCatalogRegen(assetPath);

            // Re-check after catalog regen attempt — only accept if now registered.
            return AssetReferenceCatalogValidator.IsRegistered(guid) ? guid : string.Empty;
        }

        /// <summary>
        /// Shows a dialog asking whether to regenerate the EditorCatalog right now.
        /// If the user confirms, runs the generation and returns. The assignment is
        /// always rejected — the user re-drags the asset after the catalog is updated.
        /// </summary>
        private static void TryPromptCatalogRegen(string pAssetPath)
        {
            string message = AssetReferenceCatalogValidator.CatalogExists()
                ? $"'{System.IO.Path.GetFileName(pAssetPath)}' is not registered in the HyperContent catalog.\n\n" +
                  "Update the Editor Catalog now?"
                : $"HyperContent Editor Catalog does not exist yet.\n\n" +
                  "Generate the Editor Catalog now?";

            bool confirmed = EditorUtility.DisplayDialog(
                "HyperContent — Asset Not Registered",
                message,
                "Update Catalog",
                "Skip");

            if (!confirmed)
                return;

            var config = GetDefaultBuildConfig();
            bool success = simulation.EditorCatalogGenerator.Generate(config);

            if (success)
                AssetReferenceCatalogValidator.MenuRefresh();
            else
                Debug.LogError("[HyperContent] Editor Catalog generation failed. Check the Console for details.");
        }

        private static BuildConfig GetDefaultBuildConfig()
        {
            const string CONFIG_PATH = "ProjectSettings/HyperContentBuildConfig.json";
            if (System.IO.File.Exists(CONFIG_PATH))
            {
                string json = System.IO.File.ReadAllText(CONFIG_PATH);
                var saved = JsonUtility.FromJson<BuildConfig>(json);
                if (saved != null)
                    return saved;
            }

            return new BuildConfig
            {
                buildTarget        = EditorUserBuildSettings.activeBuildTarget,
                compressionType    = BundleCompressionType.Lz4,
                includeDependencies = true,
                forceRebuild       = false,
                generateReport     = true,
                groupingStrategy   = BundleGroupingStrategyType.MarkerBased,
                groupingToolId     = "default",
                buildExecutorId    = "default"
            };
        }
    }
}
