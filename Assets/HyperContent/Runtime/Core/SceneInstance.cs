using UnityEngine.SceneManagement;

namespace com.igg.hypercontent
{
    /// <summary>
    /// Wrapper for a loaded scene, returned by LoadSceneAsync via ContentHandle&lt;SceneInstance&gt;.
    /// Holds the Unity Scene struct and provides validity check.
    /// </summary>
    public struct SceneInstance
    {
        public Scene Scene { get; internal set; }

        public bool IsValid() => Scene.IsValid();

        public string Name => Scene.IsValid() ? Scene.name : string.Empty;
    }
}
