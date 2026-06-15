using System;
using UnityEngine;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Editor mode direct load — loads assets from the local filesystem without bundles.
    /// Used in editor for fast iteration (no bundle build required).
    /// See ARCHITECTURE.md section 6.4.
    /// </summary>
    internal sealed class LocalFileProvider : IContentProvider
    {
        public const string ID = "LocalFileProvider";

        public string ProviderId => ID;

        public void Provide(ProvideHandle handle)
        {
#if UNITY_EDITOR
            string assetPath = handle.Location.InternalId;
            Type assetType = handle.Location.ResourceType ?? typeof(UnityEngine.Object);
            HCLogger.LogVerbose($"[LocalFileProvider] Provide assetPath={assetPath} type={assetType.Name}");

            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath(assetPath, assetType);
            if (asset != null)
            {
                HCLogger.LogVerbose($"[LocalFileProvider] Loaded asset={asset.name} from {assetPath}");
                handle.Complete(asset);
            }
            else
            {
                HCLogger.LogWarn($"[LocalFileProvider] Asset not found at path={assetPath}");
                handle.Fail(new Exception($"Asset not found at path: {assetPath}"));
            }
#else
            HCLogger.LogError("LocalFileProvider is only available in Editor mode");
            handle.Fail(new NotSupportedException("LocalFileProvider is only available in Editor mode."));
#endif
        }

        public void Release(ProvideHandle handle)
        {
            HCLogger.LogVerbose($"[LocalFileProvider] Release (no-op) assetPath={handle.Location.InternalId}");
        }
    }
}
