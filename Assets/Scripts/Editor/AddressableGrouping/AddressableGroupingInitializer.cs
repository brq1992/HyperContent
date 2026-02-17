using UnityEditor;
using HyperContent.Editor.Build;

namespace AddressableGrouping
{
    /// <summary>
    /// Initializer that registers AddressableGroupingTool with BuildToolFactory
    /// This class is automatically initialized when Unity loads the editor
    /// </summary>
    [InitializeOnLoad]
    public static class AddressableGroupingInitializer
    {
        static AddressableGroupingInitializer()
        {
            // Register Addressable grouping tool
            var addressableTool = new AddressableGroupingTool();
            BuildToolFactory.RegisterGroupingTool("addressable", addressableTool);
        }
    }
}
