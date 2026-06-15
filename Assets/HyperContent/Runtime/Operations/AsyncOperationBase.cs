using System;
using UnityEngine;
using com.igg.hypercontent.shared;

namespace com.igg.hypercontent.runtime
{
    /// <summary>
    /// Base class for all async operations in the DAG.
    /// Manages RefCount, status transitions, dependency tracking, and completion events.
    /// See ARCHITECTURE.md section 5.2.
    /// </summary>
    public abstract class AsyncOperationBase
    {
        internal int RefCount;
        public OperationStatus Status { get; internal set; }
        internal int LocationHash;
        internal ResourceLocation Location;
        // Phase 2 · B3: 改为定长 array + count。叶子 op（无依赖）共用 Array.Empty<>() singleton，
        // 0 alloc；有依赖的 op 在 ResourceManager.StartOperation 内一次性 new[count] 后直接索引赋值。
        // 之前 `= new List<AsyncOperationBase>()` 让每个 op（含大量叶子 bundle op）都 alloc 一个 List
        // 实例 + 内部 T[]。`DependencyCount` 是有效长度（== Dependencies.Length 在当前实现下，预留给
        // 未来若改为 pooled-array 复用 buffer 的扩展）。所有读访问点必须用 `DependencyCount` 取数，
        // 不能依赖 `Dependencies.Length`，规则统一以便后续若引入 pool 不至于改一圈调用方。
        internal AsyncOperationBase[] Dependencies = Array.Empty<AsyncOperationBase>();
        internal int DependencyCount;
        internal int PendingDepCount;
        public Exception Exception { get; internal set; }
        internal float ProgressValue;

        // D2 阶段化埋点用：业务直接 LoadAsync / LoadSceneAsync / InstantiateAsync 触发的 root op == true，
        // ResourceManager.LoadDependency 内部递归创建的依赖 op == false（保持默认）。ResourceManager 在
        // StartOperation / ExecuteImmediate 用它来决定是否打 HC.Load.Stage.Schedule_<addr> /
        // HC.Load.Stage.Provide_<addr> sample——只关心业务可见的端到端 wall clock，dep op 内部时间已被
        // HC.BundleIO_<bundle> / HC.Resolve_<bundle> 覆盖，再叠 sample 会爆 IGGProfiler 唯一名字字典。
        // 关 ENABLE_PROFILERLOG 时此字段仍然存在但永远是 false，体积代价 1 byte / op，可忽略。
        internal bool IsUserFacing;

        // Synthetic ops are created already in a terminal Failed state for entry-level failures
        // (invalid address, catalog miss, declined remote download). They are NEVER inserted into
        // OperationCache and hold no real resource, so they must not flow through the refcount /
        // cache-release path: HyperContentImpl.AllocateHandleId skips tracking them and
        // HyperContentImpl.Release treats releasing them as an idempotent no-op. The point of a
        // synthetic op (vs a default/null ContentHandle) is to give callers a real terminal handle
        // whose Completed callback fires, whose IsDone is true (so coroutines / await don't hang),
        // and whose await surfaces the error.
        internal bool IsSynthetic;

#if ENABLE_PROFILERLOG
        // D2 hotfix（2026-05-21）— Provide End 时机修正：
        // 原方案在 ResourceManager.ExecuteImmediate 通过 op.OnCompleted += _ => EndSample(provideSample)
        // 订阅 EndSample，但订阅时机是 OnCompleted 链最末（晚于 HyperContentImpl.StartLoad 的 LoadSucceeded
        // callback、HyperContentImpl.StartLoadAssetOp 的 HC.Load_ EndSample、以及业务侧通过 ContentHandle
        // 注册的 _LoadWindowPrefab → InstantiateWindow → UIControllerBase.Init/Prepare/Show 等回调链）。
        // .NET event 严格按 += 顺序触发，导致 Provide stopwatch 被业务回调耗时污染。SevenDayRewardWindow
        // 真机实测：HC.Extract_*.prefab = 166ms（真实 LoadAssetAsync 耗时），HC.Load_* = 192ms（业务 wall
        // clock），但 HC.Load.Stage.Provide_* = 293ms（多出的 127ms 全是业务 callback 链时间）。
        //
        // 修复：把 EndSample 移到 SetSucceeded/SetFailed 内部、InvokeCompleted 调用之前——这是 Provider
        // 完成的真实时刻、回调链还没启动。字段由 ResourceManager.ExecuteImmediate 设置（仅 root op）；
        // dep op 不会经过 ExecuteImmediate 的设置点，字段保持 null，SetSucceeded/SetFailed 内的 if 分支
        // 自然跳过，零干扰。失败路径（HandleDependencyFailure → SetFailed）下 op 还未进 ExecuteImmediate，
        // 字段也仍是 null，安全。
        internal string _provideSampleName;
#endif

        internal IContentProvider _provider;
        internal ProvideHandle _provideHandle;

        private event Action<AsyncOperationBase> _onCompleted;

        /// <summary>
        /// Fires when the operation reaches a terminal state (Succeeded / Failed).
        /// Safe to subscribe after completion — the callback is invoked immediately
        /// if the operation has already finished, so callers never miss the signal
        /// even when providers complete synchronously inside StartOperation.
        /// </summary>
        public event Action<AsyncOperationBase> OnCompleted
        {
            add
            {
                if (Status == OperationStatus.Succeeded || Status == OperationStatus.Failed)
                    value?.Invoke(this);
                else
                    _onCompleted += value;
            }
            remove { _onCompleted -= value; }
        }

        internal abstract void Execute(IContentProvider provider, ProvideHandle handle);

        internal virtual void Dispose()
        {
            HCLogger.LogVerbose($"[Op] Dispose [{LogFields.LOCATION_HASH}={LocationHash}] " +
                $"[{LogFields.ADDRESS}={Location?.Address}]");

            if (_provider != null && _provideHandle != null)
            {
                try
                {
                    HCLogger.LogVerbose($"[Op] Provider.Release [{LogFields.PROVIDER_ID}={_provider.ProviderId}] " +
                        $"[{LogFields.LOCATION_HASH}={LocationHash}]");
                    _provider.Release(_provideHandle);
                }
                catch (Exception e)
                {
                    HCLogger.LogError($"Exception in Provider.Release: {e.Message}");
                }
            }

            Status = OperationStatus.Disposed;
            _onCompleted = null;
            _provider = null;
            _provideHandle = null;
        }

        public virtual float GetProgress()
        {
            if (Status == OperationStatus.Succeeded || Status == OperationStatus.Failed)
                return 1f;
            if (Status == OperationStatus.None)
                return 0f;

            if (DependencyCount == 0)
                return ProgressValue;

            float total = ProgressValue;
            for (int i = 0; i < DependencyCount; i++)
                total += Dependencies[i].GetProgress();
            return total / (DependencyCount + 1);
        }

        internal virtual bool TrySetResult(UnityEngine.Object result) => false;

        internal void SetSucceeded()
        {
#if ENABLE_PROFILERLOG
            // D2 hotfix：Provide End 必须在 InvokeCompleted 之前触发，避免被业务回调链时间污染。
            // 详见上方 _provideSampleName 字段注释。
            if (_provideSampleName != null)
            {
                com.igg.core.IGGProfiler.EndSample(_provideSampleName);
                _provideSampleName = null;
            }
#endif
            HCLogger.LogVerbose($"[Op] Succeeded [{LogFields.LOCATION_HASH}={LocationHash}] " +
                $"[{LogFields.ADDRESS}={Location?.Address}]");
            Status = OperationStatus.Succeeded;
            ProgressValue = 1f;
            InvokeCompleted();
        }

        internal void SetFailed(Exception exception)
        {
#if ENABLE_PROFILERLOG
            if (_provideSampleName != null)
            {
                com.igg.core.IGGProfiler.EndSample(_provideSampleName);
                _provideSampleName = null;
            }
#endif
            HCLogger.LogError($"[Op] Failed [{LogFields.LOCATION_HASH}={LocationHash}] " +
                $"[{LogFields.ADDRESS}={Location?.Address}] error={exception?.ToString()}");
            Status = OperationStatus.Failed;
            Exception = exception;
            ProgressValue = 1f;
            InvokeCompleted();
        }

        private void InvokeCompleted()
        {
            // Walk the invocation list manually so one throwing subscriber can't truncate the rest:
            // a single Invoke() aborts the whole chain on the first exception, silently skipping
            // later callbacks (e.g. the system LoadSucceeded/LoadFailed forwarders). Isolating each
            // delegate keeps the chain resilient. GetInvocationList() allocates a Delegate[] but only
            // when there are subscribers and only once per terminal transition — acceptable cost.
            var invocationList = _onCompleted?.GetInvocationList();
            if (invocationList == null) return;
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<AsyncOperationBase>)invocationList[i])(this);
                }
                catch (Exception e)
                {
                    HCLogger.LogError($"Exception in operation callback: {e.ToString()}");
                }
            }
        }
    }
}
