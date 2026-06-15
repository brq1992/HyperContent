using System;
using UnityEngine;
using com.igg.hypercontent.shared;
using com.igg.core;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Extract a concrete asset from an already-loaded AssetBundle.
    /// Does ZERO file IO — only calls LoadAssetAsync on in-memory AssetBundle objects.
    /// See ARCHITECTURE.md section 6 and PROVIDER_FLOW.md.
    /// </summary>
    internal sealed class BundleAssetExtractor : IContentProvider
    {
        public const string ID = "BundleAssetExtractor";

        private readonly IBundleLoader _bundleLoader;

        internal BundleAssetExtractor(IBundleLoader bundleLoader)
        {
            _bundleLoader = bundleLoader ?? throw new ArgumentNullException(nameof(bundleLoader));
        }

        public string ProviderId => ID;

        public void Provide(ProvideHandle handle)
        {
            string assetPath = handle.Location.InternalId;
            Type assetType = handle.Location.ResourceType ?? typeof(UnityEngine.Object);
            HCLogger.LogVerbose($"[BundleAssetExtractor] Provide assetPath={assetPath} type={assetType.Name}");

            AssetBundle bundle = FindLoadedBundle(handle);
            if (bundle == null)
            {
                HCLogger.LogWarn($"[BundleAssetExtractor] No loaded bundle for asset={assetPath} " +
                    $"deps={handle.Operation.DependencyCount}");
                handle.Fail(new Exception($"No loaded AssetBundle found for asset: {assetPath}"));
                return;
            }

            // D2 P1（2026-05-22）— Extract 子段拆分：
            //   外层 HC.Extract_<asset>：保留向下兼容 = Issue + Wait + Complete 三段串行总和。
            //   HC.Extract.Issue_<asset>   ：bundle.LoadAssetAsync 同步调用本身 wall clock
            //                                （创建 AssetBundleRequest 对象 + 入队），预期 < 1ms。
            //   HC.Extract.Wait_<asset>    ：从 Issue 完成到 completed 回调入口，黑盒大头，
            //                                包含 Unity worker 线程反序列化 + GPU upload + 帧延迟。
            //   HC.Extract.Complete_<asset>：completed 回调内、handle.Complete/Fail 调用前的同步段
            //                                （仅业务无关的 verbose log + 错误诊断），预期 < 1ms。
            //
            // 用途：
            //   * Issue ≫ 1ms     → 主线程压力大（很少见，Unity 内部入队应该是常量时间）。
            //   * Wait 占 ~95%+   → 黑盒由 Unity 决定，框架侧无优化空间，要走 Resource 侧瘦身（拆 atlas/降图压缩）
            //                       或 OPT-A 预热 bundle 后单独评估资产 Extract 时间。
            //   * Complete ≫ 1ms  → 错误路径走了 bundle.GetAllAssetNames() + LogError 的诊断流程。
            //
            // ⚠️ Complete 段时序污染防御（D2 P1 hotfix，2026-05-22）：
            //   handle.Complete(asset) → ProvideHandle.Complete → AsyncOperationBase.SetSucceeded
            //   → InvokeCompleted 同步触发整个 OnCompleted 链——包括 HyperContentImpl.InstantiateAsync
            //   注册的业务 lambda（Object.Instantiate 同步段 100~400ms）。如果 Complete 段 EndSample
            //   放在 handle.Complete 之后（首版实现），Complete 段会包整段业务链时间（实测 196ms 污染，
            //   预期 < 1ms）。
            //
            //   解决：成功路径在 handle.Complete 调用之前 EndSample Complete + EndSample Extract，
            //   失败路径在 handle.Fail 调用之前同样处理。这与 D2 § Provide 段 5/21 hotfix（在 SetSucceeded
            //   入口处 EndSample _provideSampleName）的语义一致——所有"以 IGGProfiler 测 wall clock 的
            //   异步段都不能包 handle.Complete/Fail 触发的业务回调链"。
            //
            //   PRINCIPLE：handle.Complete / handle.Fail 是回调链的同步触发开关，任何包它们的 sample
            //   都会被业务链同步执行时间污染。框架侧 wall clock 测量必须停在这两个调用之前。
            //
            // 关 ENABLE_PROFILERLOG 时的零成本保证：
            //   全部用 inline 字符串 IGGProfiler.BeginSample/EndSample($"...")，[Conditional] 关宏后
            //   调用点 + inline 字符串拼接整体擦除（A2 已验证），无需 #if 包。
            IGGProfiler.BeginSample($"HC.Extract_{assetPath}");
            IGGProfiler.BeginSample($"HC.Extract.Issue_{assetPath}");
            HCLogger.LogVerbose($"[HC.ExtractAsset] asset={assetPath} loadKey={assetPath} type={assetType.Name} bundle={bundle.name}");
            var request = bundle.LoadAssetAsync(assetPath, assetType);
            IGGProfiler.EndSample($"HC.Extract.Issue_{assetPath}");
            IGGProfiler.BeginSample($"HC.Extract.Wait_{assetPath}");
            request.completed += _ =>
            {
                IGGProfiler.EndSample($"HC.Extract.Wait_{assetPath}");
                IGGProfiler.BeginSample($"HC.Extract.Complete_{assetPath}");
                HCLogger.LogVerbose($"[HC.ExtractAsset.Done] asset={assetPath} success={request.asset != null}");
                if (request.asset != null)
                {
                    HCLogger.LogVerbose($"[BundleAssetExtractor] Extracted asset={request.asset.name} " +
                        $"type={request.asset.GetType().Name} from bundle={bundle.name}");
                    // 必须在 handle.Complete 之前 EndSample——否则 SetSucceeded 触发的 OnCompleted
                    // 业务链（Object.Instantiate 等）会污染本段时间，详见上方时序污染防御注释。
                    IGGProfiler.EndSample($"HC.Extract.Complete_{assetPath}");
                    IGGProfiler.EndSample($"HC.Extract_{assetPath}");
                    handle.Complete(request.asset);
                    return;
                }

                string[] allNames = bundle.GetAllAssetNames();
                HCLogger.LogError($"[BundleAssetExtractor] Load failed for asset={assetPath} " +
                    $"type={assetType.Name} in bundle={bundle.name}, " +
                    $"bundle contains: [{string.Join(", ", allNames)}]");
                // 失败路径同理：handle.Fail 同步触发 SetFailed → InvokeCompleted 业务链。
                IGGProfiler.EndSample($"HC.Extract.Complete_{assetPath}");
                IGGProfiler.EndSample($"HC.Extract_{assetPath}");
                handle.Fail(new Exception($"Asset not found in bundle: {assetPath}"));
            };
        }

        public void Release(ProvideHandle handle)
        {
            HCLogger.LogVerbose($"[BundleAssetExtractor] Release (no-op) assetPath={handle.Location.InternalId}");
        }

        /// <summary>
        /// The primary bundle (the one actually containing the asset) is always the
        /// LAST entry in the flat dependency list, because LocalContentCatalog uses
        /// post-order traversal (indirect deps first, primary bundle last).
        /// </summary>
        private AssetBundle FindLoadedBundle(ProvideHandle handle)
        {
            int count = handle.Operation.DependencyCount;
            if (count == 0)
            {
                HCLogger.LogVerbose("[BundleAssetExtractor] FindLoadedBundle — no dependencies");
                return null;
            }

            // Must match bundle registration keys: RemoteBundleProvider uses Address when set;
            // BundleFileProvider / PAD use InternalId (catalog bundles use extensionless names; disk/CDN add ".bundle").
            // See BundleDownloadManager.CollectBundleNamesRecursive and LocalContentCatalog.BuildBundleLocationCache.
            var primaryLoc = handle.Operation.Dependencies[count - 1].Location;
            string primaryKey = ResolveBundleLoaderKey(primaryLoc);
            if (!string.IsNullOrEmpty(primaryKey) && _bundleLoader.TryGetBundle(primaryKey, out var primary))
            {
                HCLogger.LogVerbose($"[BundleAssetExtractor] Found primary bundle [{LogFields.BUNDLE_NAME}={primaryKey}]");
                return primary;
            }

            // Primary bundle lookup failed — log diagnostic details at Error level
            HCLogger.LogError($"[BundleAssetExtractor] Primary bundle lookup FAILED for asset={handle.Location.InternalId} " +
                $"loaderKey={primaryKey} primaryInternalId={primaryLoc?.InternalId} primaryAddress={primaryLoc?.Address} " +
                $"primaryProvider={primaryLoc?.ProviderId} depStatus={handle.Operation.Dependencies[count - 1].Status}");

            for (int i = count - 2; i >= 0; i--)
            {
                var dep = handle.Operation.Dependencies[i];
                string depKey = ResolveBundleLoaderKey(dep.Location);
                if (!string.IsNullOrEmpty(depKey) && _bundleLoader.TryGetBundle(depKey, out var depBundle))
                {
                    HCLogger.LogError($"[BundleAssetExtractor] Fallback hit dep[{i}] " +
                        $"loaderKey={depKey} internalId={dep.Location?.InternalId} address={dep.Location?.Address} " +
                        $"bundle.name={depBundle.name} — asset may load from WRONG bundle!");
                    return depBundle;
                }
            }

            HCLogger.LogVerbose("[BundleAssetExtractor] FindLoadedBundle — no bundle found in any dependency");
            return null;
        }

        private static string ResolveBundleLoaderKey(ResourceLocation loc)
        {
            if (loc == null)
                return null;
            if (!string.IsNullOrEmpty(loc.Address))
                return loc.Address;
            return loc.InternalId;
        }
    }
}
