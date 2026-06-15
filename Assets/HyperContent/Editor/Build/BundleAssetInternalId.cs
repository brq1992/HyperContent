using System.IO;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Asset name inside a built AssetBundle (Scriptable Build Pipeline <c>AssetBundleBuild.addressableNames</c>),
    /// catalog asset path string, and runtime <c>BundleAssetExtractor</c> LoadAsset key. Keep build, catalog, and manifest aligned.
    /// Uses the asset file name <b>with extension</b> so e.g. <c>Foo.prefab</c> and <c>Foo.controller</c> in the same bundle do not collide.
    /// </summary>
    public static class BundleAssetInternalId
    {
        public static string FromAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return assetPath;
            return Path.GetFileName(assetPath);
        }
    }
}
