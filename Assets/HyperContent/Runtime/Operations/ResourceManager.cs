using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using com.igg.hypercontent.shared;
using com.igg.core;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// 实验开关：ResourceManager.Pump 调度模式。
    ///
    /// 默认 <see cref="DeferAll"/> = 当前行为（入队 + 下一帧 Pump 一次性 drain），业务侧零行为变化。
    /// <see cref="Immediate"/> / <see cref="Throttled"/> 仅供 HyperContentLoadTest 实验对比，用于
    /// 一次性跑出"批量大小 × 调度策略"曲线，定位 inflection point。详见
    /// docs/2026-05_TODO_MILESTONE.md A1 / OPT-1 章节。
    /// </summary>
    public enum PumpMode
    {
        /// <summary>当前默认：ScheduleExecute 入队，下一帧 Pump 一次性 drain 全部 pending op。</summary>
        DeferAll = 0,

        /// <summary>实验：ScheduleExecute 直接同步调用 ExecuteImmediate，跳过队列与拆帧延迟（OPT-1 行为）。
        /// 适用场景：单点 / 小批量 cache miss，省 1+ 帧 Pump 拆帧延迟。
        /// 风险：N+ 个 bundle op 同帧 issue 会把主线程 mmap 头反序列化压力堆到 1 帧上。</summary>
        Immediate = 1,

        /// <summary>实验：入队 + Pump 每帧最多执行 <see cref="ResourceManagerExperiments.ThrottleBundleOpsPerFrame"/>
        /// 个 bundle op，asset extract op 不限（A1 行为）。
        /// 适用场景：30+ prefab 批量加载，把单帧主线程长尖刺拍平。</summary>
        Throttled = 2,
    }

    /// <summary>
    /// 实验配置 — 与 <see cref="PumpMode"/> 配套。所有字段静态，业务侧通过赋值切换模式。
    ///
    /// 默认值（2026-05-29 调整）：<see cref="PumpMode.Throttled"/> + N=4。
    /// 调整理由：单点 cache miss 实测帧时长 30–45ms（22–33fps），即便 1 个 bundle op 也已超 16ms 帧预算；
    /// 默认开 Throttled 把单帧 bundle op 上限设到 4，覆盖"30+ prefab 同帧 LoadAsync"批量场景把主线程长尖刺
    /// 拍平的目标，单点 / 小批量场景下 4 个 budget 用不完，行为等价 DeferAll（同一帧 drain 全部）。
    /// 仍可通过赋值 <see cref="PumpMode"/> 切回 DeferAll / Immediate 做对比实验。
    /// </summary>
    public static class ResourceManagerExperiments
    {
        /// <summary>当前调度模式。默认 <see cref="PumpMode.Throttled"/>。</summary>
        public static PumpMode PumpMode = PumpMode.Throttled;

        /// <summary>仅 <see cref="PumpMode.Throttled"/> 模式生效，单帧 bundle op 上限。默认 4。</summary>
        public static int ThrottleBundleOpsPerFrame = 7;
    }

    /// <summary>
    /// DAG scheduler: builds dependency Operation tree from ResourceLocation,
    /// executes in topological order (parallel where possible), manages lifecycle via OperationCache.
    /// See ARCHITECTURE.md section 5 and LOAD_RELEASE_FLOW.md.
    ///
    /// Execution is pumped from <see cref="HyperContentRunner"/>'s per-frame Update (mirroring
    /// Unity Addressables' ResourceManager.Update). <see cref="ScheduleExecute"/> only enqueues
    /// the operation; the provider's Provide() is invoked one frame later, which staggers
    /// AssetBundle.LoadFromFileAsync / LoadAssetAsync requests across frames and keeps Unity's
    /// single async-loading queue from being flooded in a single frame.
    ///
    /// 实验模式（2026-05-26）：参见 <see cref="PumpMode"/> 和 <see cref="ResourceManagerExperiments"/>。
    /// 默认 DeferAll 行为不变；Immediate / Throttled 用于 HyperContentLoadTest 一次性跑出 inflection 曲线。
    /// </summary>
    internal sealed class ResourceManager
    {
        private readonly OperationCache _cache;
        private readonly ProviderRegistry _providers;

        // Operations whose dependencies are ready but whose provider has not yet been invoked.
        // Drained one batch per frame by Pump().
        private readonly Queue<AsyncOperationBase> _pendingExecute = new Queue<AsyncOperationBase>();
        private bool _pumpRegistered;

        // 实验：Throttled 模式下，单帧 bundle op 计数器，每帧 Pump 入口清零。
        // 仅 Throttled 模式读写；DeferAll / Immediate 模式下字段存在但不参与决策（1 int / instance，可忽略）。
        private int _bundleOpsExecutedThisFrame;

#if ENABLE_PROFILERLOG
        // D2 Schedule sample 跟踪：StartOperation BeginSample 时 Add；ExecuteImmediate（正常路径）或
        // op.OnCompleted 安全网（失败路径，op 在依赖阶段就 SetFailed，永不进 ExecuteImmediate）择一
        // Remove 并 EndSample，二者互斥。直接用 op 实例做 key（引用唯一性 OK，不依赖 LocationHash），
        // 单线程访问（ResourceManager 全程主线程 HyperContentRunner.Update 调度）所以 HashSet 安全。
        private readonly HashSet<AsyncOperationBase> _activeScheduleSamples = new HashSet<AsyncOperationBase>();
#endif

        internal ResourceManager(OperationCache cache, ProviderRegistry providers)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        }

        /// <summary>
        /// Build the Operation DAG from a ResourceLocation tree and start execution.
        /// Dependencies load in parallel; the root operation waits for all deps to succeed.
        /// </summary>
        internal AssetOperation<T> LoadAsync<T>(ResourceLocation location) where T : UnityEngine.Object
        {
            HCLogger.LogVerbose($"[ResourceManager] LoadAsync<{typeof(T).Name}> " +
                $"[{LogFields.LOCATION_HASH}={location.LocationHash}] [{LogFields.ADDRESS}={location.Address}]");

            // Fast path: cache hit must not allocate. The lambda below captures generic param T and
            // would otherwise alloc a closure + Func<> delegate (~60–80 B) on every call even when
            // the factory body is not invoked. Type mismatch on the cast surfaces hash collisions
            // / type misuse loudly — same behavior as the slow path's cast.
            if (_cache.TryGetExisting(location, out var hit))
                return (AssetOperation<T>)hit;

            var op = (AssetOperation<T>)_cache.GetOrCreate(location, () => new AssetOperation<T>(location));
            // D2 § Schedule/Provide：仅业务直接 LoadAsync 触发的 root op 打 stage sample；
            // LoadDependency 内部递归创建的 dep op 仍维持 IsUserFacing == false 默认值，
            // 以免 IGGProfiler 唯一名字字典爆 + 与 dep 自身的 HC.BundleIO_*/HC.Resolve_* 重复采样。
            op.IsUserFacing = true;
            StartOperation(op, location);
            return op;
        }

        /// <summary>
        /// Build a SceneOperation DAG from a ResourceLocation tree and start execution.
        /// </summary>
        internal SceneOperation LoadSceneAsync(ResourceLocation location, LoadSceneMode mode)
        {
            HCLogger.LogVerbose($"[ResourceManager] LoadSceneAsync " +
                $"[{LogFields.LOCATION_HASH}={location.LocationHash}] [{LogFields.ADDRESS}={location.Address}] mode={mode}");

            var op = (SceneOperation)_cache.GetOrCreate(location, () => new SceneOperation(location, mode));

            if (op.Status != OperationStatus.None)
            {
                HCLogger.LogVerbose($"[ResourceManager] Reusing existing scene op [{LogFields.STATUS}={op.Status}] " +
                    $"[{LogFields.LOCATION_HASH}={location.LocationHash}]");
                return op;
            }

            // D2 § Schedule/Provide：仅业务直接 LoadSceneAsync 触发的 root scene op 打 stage sample，
            // 与 LoadAsync 行为一致；复用 cache hit 路径上面已经 early return，不会进到这里。
            op.IsUserFacing = true;
            StartOperation(op, location);
            return op;
        }

        /// <summary>
        /// Build a generic operation for dependency loading (bundles, etc.)
        /// </summary>
        internal AsyncOperationBase LoadDependency(ResourceLocation location)
        {
            HCLogger.LogVerbose($"[ResourceManager] LoadDependency [{LogFields.LOCATION_HASH}={location.LocationHash}] " +
                $"[{LogFields.PROVIDER_ID}={location.ProviderId}] internalId={location.InternalId}");

            // Fast path: bundle dep cache hits dominate during gameplay (every nested dep that
            // shares a parent bundle goes through here). Avoid the closure + Func<> delegate alloc
            // GetOrCreate would otherwise pay every call.
            if (_cache.TryGetExisting(location, out var hit))
                return hit;

            var op = _cache.GetOrCreate(location, () => new AssetOperation<UnityEngine.Object>(location));
            StartOperation(op, location);
            return op;
        }

        private void StartOperation(AsyncOperationBase op, ResourceLocation location)
        {
            int depCount = location.Dependencies.Count;
            op.PendingDepCount = depCount;
            HCLogger.LogVerbose($"[ResourceManager] StartOperation [{LogFields.LOCATION_HASH}={location.LocationHash}] " +
                $"[{LogFields.ADDRESS}={location.Address}] deps={depCount}");

#if ENABLE_PROFILERLOG
            // D2 § Schedule 段 Begin：op.IsUserFacing 字段访问 + if 分支不是 [Conditional] 表达式，
            // 必须 #if 整段包，否则关 ENABLE_PROFILERLOG 时 if 仍会编译进 IL（A2 落地结论）。
            // Begin 在 StartOperation 入口；End 在 ExecuteImmediate 实际跑 op.Execute 之前——
            // 这一段覆盖 "DAG 构建 + 依赖加载等待 + ScheduleExecute 拆帧延迟" 三段合并 wall clock，
            // 与 HC.Load_<addr> 整段（用户调用到业务回调）的差就是 HC.Load.Stage.Provide_<addr>。
            //
            // 失败路径安全网：dep 在加载中就 Failed → HandleDependencyFailure → SetFailed → 永不进
            // ExecuteImmediate。如果不在 OnCompleted 上挂安全网 EndSample，Schedule sample 会卡在
            // IGGProfiler._allNodes 里，下次同地址 LoadAsync 时 BeginSample 报 "already exists" LogError。
            // 用 _activeScheduleSamples 做 ExecuteImmediate 与 OnCompleted 之间的互斥：谁先 Remove 谁
            // 负责 End，避免重复 EndSample 引发的 "not started" LogWarning 噪声。
            if (op.IsUserFacing && !string.IsNullOrEmpty(location.Address))
            {
                string scheduleSample = $"HC.Load.Stage.Schedule_{location.Address}";
                IGGProfiler.BeginSample(scheduleSample);
                _activeScheduleSamples.Add(op);
                op.OnCompleted += completedOp =>
                {
                    if (_activeScheduleSamples.Remove(completedOp))
                        IGGProfiler.EndSample(scheduleSample);
                };
            }
#endif

            if (depCount == 0)
            {
                HCLogger.LogVerbose($"[Load] Step E0: StartOperation no deps, ScheduleExecute provider={location.ProviderId} internalId={location.InternalId}");
                ScheduleExecute(op);
            }
            else
            {
                // Phase 2 · B3: 一次性 new[count] 替代 List.Add()。op 是 cache miss 时新建的，
                // 此处 op.Dependencies 仍是 Array.Empty 占位，直接覆盖；OperationCache 不会复用
                // 已 Disposed 的 op，因此不存在 array 被 shared / 还活着的并发风险。
                // DependencyCount 必须递增填充（与原 List.Count 语义对齐）：循环中途 HandleDependencyFailure
                // 触发回滚时，只能 Release 已 LoadDependency 成功填入的 dep，未填的位置仍是 null。
                op.Dependencies = new AsyncOperationBase[depCount];

                for (int i = 0; i < depCount; i++)
                {
                    var depLocation = location.Dependencies[i];
                    HCLogger.LogVerbose($"[ResourceManager] Building dep {i}/{depCount} " +
                        $"[{LogFields.LOCATION_HASH}={depLocation.LocationHash}] " +
                        $"[{LogFields.PROVIDER_ID}={depLocation.ProviderId}]");
                    var depOp = LoadDependency(depLocation);
                    op.Dependencies[i] = depOp;
                    op.DependencyCount = i + 1;

                    if (depOp.Status == OperationStatus.Succeeded)
                    {
                        op.PendingDepCount--;
                        HCLogger.LogVerbose($"[ResourceManager] Dep already succeeded [{LogFields.LOCATION_HASH}={depLocation.LocationHash}] " +
                            $"remaining={op.PendingDepCount}");
                    }
                    else if (depOp.Status == OperationStatus.Failed)
                    {
                        HCLogger.LogVerbose($"[ResourceManager] Dep already failed [{LogFields.LOCATION_HASH}={depLocation.LocationHash}]");
                        HandleDependencyFailure(op, depOp);
                        return;
                    }
                    else
                    {
                        depOp.OnCompleted += completedDep => OnDependencyCompleted(op, completedDep);
                    }
                }

                if (op.PendingDepCount == 0)
                {
                    HCLogger.LogVerbose($"[ResourceManager] All deps ready, executing [{LogFields.LOCATION_HASH}={location.LocationHash}]");
                    ScheduleExecute(op);
                }
            }

            if (op.Status == OperationStatus.None)
                op.Status = OperationStatus.Pending;
        }

        private void OnDependencyCompleted(AsyncOperationBase parentOp, AsyncOperationBase completedDep)
        {
            if (parentOp.Status == OperationStatus.Failed || parentOp.Status == OperationStatus.Disposed)
            {
                HCLogger.LogVerbose($"[ResourceManager] OnDepCompleted ignored — parent already " +
                    $"[{LogFields.STATUS}={parentOp.Status}] [{LogFields.LOCATION_HASH}={parentOp.LocationHash}]");
                return;
            }

            if (completedDep.Status == OperationStatus.Failed)
            {
                HandleDependencyFailure(parentOp, completedDep);
                return;
            }

            parentOp.PendingDepCount--;
            HCLogger.LogVerbose($"[ResourceManager] Dep completed [{LogFields.LOCATION_HASH}={completedDep.LocationHash}] " +
                $"parent [{LogFields.LOCATION_HASH}={parentOp.LocationHash}] remaining={parentOp.PendingDepCount}");

            if (parentOp.PendingDepCount == 0)
                ScheduleExecute(parentOp);
        }

        /// <summary>
        /// When a dependency fails, release already-succeeded deps to prevent leaks.
        /// See LOAD_RELEASE_FLOW.md (dependency failure handling).
        /// </summary>
        private void HandleDependencyFailure(AsyncOperationBase parentOp, AsyncOperationBase failedDep)
        {
            string depAddress = failedDep.Location?.Address ?? failedDep.LocationHash.ToString();
            string depInternalId = failedDep.Location?.InternalId ?? "?";
            string reason = failedDep.Exception?.Message ?? "unknown";

            HCLogger.LogError(ErrorCode.OPERATION_DEPENDENCY_FAILED,
                $"[parent={parentOp.Location?.Address}] " +
                $"Dependency failed: {depAddress} (internalId={depInternalId}) — {reason}");

            parentOp.SetFailed(failedDep.Exception ?? new Exception($"Dependency failed: {depAddress} — {reason}"));

            for (int i = 0; i < parentOp.DependencyCount; i++)
            {
                var d = parentOp.Dependencies[i];
                if (d.Status == OperationStatus.Succeeded)
                    _cache.Release(d);
            }
        }

        private void ScheduleExecute(AsyncOperationBase op)
        {
            // Mark InProgress immediately so any subsequent dep-completion callbacks don't
            // re-enqueue this op, but defer the actual provider.Provide() call to the next
            // Pump() tick. This single-frame deferral is what gives the scheduler an
            // Addressables-like "one batch per frame" cadence.
            op.Status = OperationStatus.InProgress;

            // 实验：Immediate 模式跳过队列，同帧同步执行 ExecuteImmediate（OPT-1 行为）。
            // 调用上下文有两种：
            //   (a) 业务侧主线程直接 LoadAsync → StartOperation → ScheduleExecute（无递归压力）
            //   (b) bundle dep 完成的 Unity AsyncOperation.completed 回调 → OnDependencyCompleted →
            //       ScheduleExecute → ExecuteImmediate（同步触发 BundleAssetExtractor.Provide）
            // 路径 (b) 是 OPT-1 的核心收益场景：root op 不再等下一帧 Pump，省 1 帧拆帧延迟。
            // 重入保护：ExecuteImmediate → SetSucceeded → OnCompleted → 业务回调可能再触发 LoadAsync，
            // 栈深 = 业务回调链深度，HyperContentLoadTest 实验场景下浅；若将来 Immediate 转正需加深度
            // 守卫（_immediateRecursionDepth > 16 fallback DeferAll），实验阶段先观察。
            if (ResourceManagerExperiments.PumpMode == PumpMode.Immediate)
            {
                ExecuteImmediate(op);
                return;
            }

            _pendingExecute.Enqueue(op);
            EnsurePumpRegistered();
        }

        private void EnsurePumpRegistered()
        {
            if (_pumpRegistered) return;
            _pumpRegistered = true;
            HyperContentRunner.Instance.AddUpdate(Pump);
        }

        private void Pump()
        {
            if (_pendingExecute.Count == 0) return;

            // Snapshot the current batch. Ops enqueued during the Provide() callbacks below
            // (e.g. when a dep completes synchronously because its bundle is already loaded)
            // stay in the queue and will be drained on the next frame. This is the mechanism
            // that naturally staggers bundle loads across frames.
            int batch = _pendingExecute.Count;

            // 实验：Throttled 模式下每帧重置 bundle op 计数器（A1 行为）。DeferAll / Immediate
            // 模式下该字段不参与决策，重置只是保持状态干净，无副作用。
            _bundleOpsExecutedThisFrame = 0;
            var mode = ResourceManagerExperiments.PumpMode;
            int bundleBudget = ResourceManagerExperiments.ThrottleBundleOpsPerFrame;

            for (int i = 0; i < batch; i++)
            {
                if (mode == PumpMode.Throttled)
                {
                    // Throttled 模式：peek 头部，若是 bundle op 且本帧 bundle 配额已满，停止 drain，
                    // 剩余 op（含可能存在的尾部 asset op）留到下一帧。简化设计：不重排队列，
                    // 避免破坏 dep 完成顺序——实际 OnDependencyCompleted 推入的 root op 都在
                    // 其 dep 之后，按 FIFO 取出语义安全。
                    // 实验阶段不做"插队跳过 bundle 优先跑 asset op"的复杂调度，足够回答"限流 vs 不限流"。
                    var peek = _pendingExecute.Peek();
                    if (IsBundleOp(peek) && _bundleOpsExecutedThisFrame >= bundleBudget)
                        break;
                }

                var op = _pendingExecute.Dequeue();
                if (mode == PumpMode.Throttled && IsBundleOp(op))
                    _bundleOpsExecutedThisFrame++;

                ExecuteImmediate(op);
            }
        }

        /// <summary>
        /// 实验辅助：识别"重型主线程 op"。Throttled 模式下仅这类 op 计入帧预算，
        /// 因为 BundleAssetExtractor.Provide 只是 0.1ms 量级的 LoadAssetAsync 入队，
        /// 限流它没意义反而增加帧延迟。bundle IO / PAD / 远程下载都计入。
        /// </summary>
        private static bool IsBundleOp(AsyncOperationBase op)
        {
            return op?.Location != null && op.Location.ProviderId != BundleAssetExtractor.ID;
        }

        private void ExecuteImmediate(AsyncOperationBase op)
        {
            if (op.Status != OperationStatus.InProgress)
            {
                HCLogger.LogVerbose($"[ResourceManager] Pump skip — [{LogFields.STATUS}={op.Status}] " +
                    $"[{LogFields.LOCATION_HASH}={op.LocationHash}]");
                return;
            }

            var provider = _providers.Get(op.Location.ProviderId);
            if (provider == null)
            {
                HCLogger.LogError($"[{LogFields.PROVIDER_ID}={op.Location.ProviderId}] Provider not found");
                op.SetFailed(new InvalidOperationException($"Provider not found: {op.Location.ProviderId}"));
                return;
            }

            HCLogger.LogVerbose($"[Load] Step E: ExecuteProvider provider={op.Location.ProviderId} internalId={op.Location.InternalId}");

            var handle = new ProvideHandle(op, this);
            op._provider = provider;
            op._provideHandle = handle;

#if ENABLE_PROFILERLOG
            // D2 § Schedule End + Provide Begin：
            // - Schedule End：通过 _activeScheduleSamples.Remove 与 StartOperation 入口注册的安全网回调
            //   互斥——这里 Remove 成功说明 op 走的是正常路径（依赖全部 ready，Pump 已取出准备跑 provider），
            //   由 ExecuteImmediate 负责 EndSample；安全网回调发现 op 已被这里 Remove 就直接跳过，避免双 End
            //   的 "not started" LogWarning 噪声。Schedule wall clock = StartOperation 入口到这里之差。
            // - Provide Begin：包 provider 异步链。End 不通过 OnCompleted 订阅（hotfix 2026-05-21）——
            //   订阅时机最末，会被业务回调链时间污染（实测多 100+ ms）。改为把 sample 名挂在 op._provideSampleName
            //   上，由 SetSucceeded/SetFailed 在 InvokeCompleted 之前主动 EndSample，捕获 Provider 完成的
            //   真实时刻。失败路径（HandleDependencyFailure → SetFailed 但 op 永不进 ExecuteImmediate）下
            //   _provideSampleName 仍是 null，SetFailed 内 if 分支自动跳过，与 dep op 不污染语义一致。
            string addressForSample = op.IsUserFacing ? op.Location?.Address : null;
            if (!string.IsNullOrEmpty(addressForSample))
            {
                if (_activeScheduleSamples.Remove(op))
                    IGGProfiler.EndSample($"HC.Load.Stage.Schedule_{addressForSample}");

                string provideSample = $"HC.Load.Stage.Provide_{addressForSample}";
                IGGProfiler.BeginSample(provideSample);
                op._provideSampleName = provideSample;
            }
#endif

            try
            {
                op.Execute(provider, handle);
            }
            catch (Exception e)
            {
                op.SetFailed(e);
            }
        }

        internal void Release(AsyncOperationBase op)
        {
            HCLogger.LogVerbose($"[ResourceManager] Release [{LogFields.LOCATION_HASH}={op.LocationHash}] " +
                $"[{LogFields.ADDRESS}={op.Location?.Address}]");
            _cache.Release(op);
        }

        internal OperationCache Cache => _cache;
    }
}
