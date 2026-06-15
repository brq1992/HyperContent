#if ENABLE_HYPERCONTENT
using UnityEditor;
#endif

namespace com.igg.hypercontent.editor
{
#if ENABLE_HYPERCONTENT
    /// <summary>
    /// After RuleToAddressableGenerator finishes Create All or ReImport All, regenerates Editor catalog
    /// the same way as "Update Editor Catalog" in HyperContent / HyperContent Window, Overview tab (saved BuildConfig).
    /// </summary>
    [InitializeOnLoad]
    internal static class AddressableGeneratorHyperContentHooks
    {
        static AddressableGeneratorHyperContentHooks()
        {
            AddressableGeneratorEvents.OnCreateAllCompleted += OnAddressableGeneratorFinished;
            AddressableGeneratorEvents.OnReimportAllCompleted += OnAddressableGeneratorFinished;
        }

        private static void OnAddressableGeneratorFinished()
        {
            // Defer until after Addressables finish SaveAssets/Refresh to avoid re-entrancy.
            EditorApplication.delayCall += RunUpdateEditorCatalog;
        }

        private static void RunUpdateEditorCatalog()
        {
            // Ensure Addressables / disk changes are visible before EditorCatalogGenerator runs.
            AssetDatabase.Refresh();
            HyperContentBuildWindow.GenerateEditorCatalogUsingSavedConfig();
        }
    }
#else
    // No hooks when ENABLE_HYPERCONTENT is not in scripting defines.
    internal static class AddressableGeneratorHyperContentHooks { }
#endif
}
