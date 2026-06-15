using com.igg.hypercontent.editor.simulation;

namespace com.igg.hypercontent.editor
{
    /// <summary>
    /// Static helpers; UI lives in <see cref="HyperContentBuildWindow"/> (menu HyperContent / HyperContent Window).
    /// </summary>
    public static class HyperContentRuntimeWindow
    {
        /// <summary>Same as <see cref="HyperContentBuildWindow.BuildConfigPath"/>.</summary>
        public const string ConfigPath = HyperContentBuildWindow.BuildConfigPath;

        /// <inheritdoc cref="HyperContentBuildWindow.LoadSavedBuildConfig"/>
        public static BuildConfig LoadSavedBuildConfig()
        {
            return HyperContentBuildWindow.LoadSavedBuildConfig();
        }

        /// <inheritdoc cref="HyperContentBuildWindow.GenerateEditorCatalogUsingSavedConfig"/>
        public static bool GenerateEditorCatalogUsingSavedConfig()
        {
            return HyperContentBuildWindow.GenerateEditorCatalogUsingSavedConfig();
        }
    }
}
