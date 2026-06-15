using System;
using UnityEditor;
using UnityEngine;
using com.igg.hypercontent.runtime;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.editor.simulation
{
    /// <summary>
    /// IContentProvider that loads assets directly via AssetDatabase.LoadAssetAtPath.
    /// Only available in Editor when EditorPlayMode == UseAssetDatabase.
    /// No AssetBundle build required — the fastest iteration path.
    /// </summary>
    internal sealed class AssetDatabaseProvider : IContentProvider
    {
        public const string ID = "AssetDatabaseProvider";

        public string ProviderId => ID;

        public void Provide(ProvideHandle handle)
        {
            string assetPath = handle.Location.InternalId;
            Type assetType = handle.Location.ResourceType ?? typeof(UnityEngine.Object);

            HCLogger.LogVerbose($"[AssetDatabaseProvider] Provide path={assetPath} type={assetType.Name}");

            var asset = AssetDatabase.LoadAssetAtPath(assetPath, assetType);
            if (asset != null)
            {
                HCLogger.LogVerbose($"[AssetDatabaseProvider] Loaded asset={asset.name} type={asset.GetType().Name}");
                handle.Complete(asset);
            }
            else
            {
                HCLogger.LogError($"[AssetDatabaseProvider] Asset not found at path={assetPath} type={assetType.Name}");
                handle.Fail(new Exception($"AssetDatabase: asset not found at {assetPath}"));
            }
        }

        public void Release(ProvideHandle handle)
        {
            HCLogger.LogVerbose($"[AssetDatabaseProvider] Release (no-op) path={handle.Location.InternalId}");
        }
    }
}
