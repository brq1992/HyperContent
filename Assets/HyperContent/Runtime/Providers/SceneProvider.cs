using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Async scene loading provider. Loads scenes via SceneManager.LoadSceneAsync.
    /// See ARCHITECTURE.md section 6.4.
    /// </summary>
    internal sealed class SceneProvider : IContentProvider
    {
        public const string ID = "SceneProvider";

        public string ProviderId => ID;

        public void Provide(ProvideHandle handle)
        {
            string scenePath = handle.Location.InternalId;
            var sceneOp = handle.Operation as SceneOperation;
            LoadSceneMode mode = sceneOp?.Mode ?? LoadSceneMode.Single;
            HCLogger.LogVerbose($"[SceneProvider] Provide scenePath={scenePath} mode={mode}");

            var asyncOp = SceneManager.LoadSceneAsync(scenePath, mode);
            if (asyncOp == null)
            {
                HCLogger.LogWarn($"[SceneProvider] LoadSceneAsync returned null scenePath={scenePath}");
                handle.Fail(new Exception($"Failed to start scene load: {scenePath}"));
                return;
            }

            asyncOp.completed += _ =>
            {
                var scene = SceneManager.GetSceneByPath(scenePath);
                if (!scene.IsValid())
                    scene = SceneManager.GetSceneByName(System.IO.Path.GetFileNameWithoutExtension(scenePath));

                if (scene.IsValid())
                {
                    HCLogger.LogInfo($"[SceneProvider] Scene loaded: {scene.name} (path={scenePath})");
                    if (sceneOp != null)
                        sceneOp.ResultScene = scene;
                    handle.Complete();
                }
                else
                {
                    HCLogger.LogWarn($"[SceneProvider] Scene not valid after load: {scenePath}");
                    handle.Fail(new Exception($"Scene not valid after load: {scenePath}"));
                }
            };
        }

        public void Release(ProvideHandle handle)
        {
            if (handle.Operation is SceneOperation sceneOp && sceneOp.ResultScene.IsValid())
            {
                HCLogger.LogInfo($"[SceneProvider] Unloading scene: {sceneOp.ResultScene.name}");
                SceneManager.UnloadSceneAsync(sceneOp.ResultScene);
            }
            else
            {
                HCLogger.LogVerbose($"[SceneProvider] Release (no scene to unload) path={handle.Location.InternalId}");
            }
        }
    }
}
