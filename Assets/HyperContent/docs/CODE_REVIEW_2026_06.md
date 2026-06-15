# HyperContent 代码审查报告（2026-06）

> 审查范围：`Runtime/` 与 `Runtime/Catalog/` 核心代码。整体设计扎实，分层清晰，接口正确，文档齐全。
> 以下问题按优先级分类，每条包含：位置、问题描述、修复建议。

---

## 优先级总览

| # | 优先级 | 文件 | 问题 | 影响 |
|---|--------|------|------|------|
| 1 | 🔴 高 | `HyperContentImpl.cs:710` | `LogLocationTree` 无条件分配 StringBuilder | 每次 LoadAsync 都 alloc，即使 Verbose 关闭 |
| 2 | 🔴 高 | `AsyncOperationBase.cs:169` | `InvokeCompleted` 异常截断后续回调 | 正确性风险：回调链部分静默失败 |
| 3 | 🔴 高 | `LocalContentCatalog.cs:706` | `ToLowerInvariant()` 热路径分配 | 每次 GUID LoadAsync 都 alloc |
| 4 | 🟡 中 | `HyperContentImpl.cs:292~423` | 三个 `InstantiateAsync` 重载大量重复代码 | 可维护性差，改一处需改三处 |
| 5 | 🟡 中 | `BundleDownloadQueue.cs:329` | `NotifyProgressListenersLocked` O(N) 扫描 `_inFlight` | 下载期间每次 Enqueue 都扫全表 |
| 6 | 🟡 中 | `BundleDownloadQueue.cs:225` | 进度回调内 `new List<>` 分配 | 高频 GC 压力 |
| 7 | 🟡 中 | `HyperContent.cs:508` | Init 前事件订阅静默丢失 | 业务隐患 |
| 8 | 🟡 中 | `LocalContentCatalog.cs:107` | StreamingAssets fallback 逻辑重复 | 微小维护成本 |
| 9 | 🟢 低 | `ContentHandle.cs:117` | `Completed.remove {}` 静默无效 | 调试困扰，误用无提示 |
| 10 | 🟢 低 | `HyperContent.cs:29` | `_isInitializing` 无 `volatile` 修饰 | 理论线程安全隐患 |
| 11 | 🟢 低 | `BundleDownloadQueue.cs:145` | `WaiterCancelled` 中 `IndexOf` O(N) | 低频路径，影响小 |

---

## 🔴 高优先级问题

### 问题 1：`LogLocationTree` 无条件分配 StringBuilder

**文件：** `Runtime/Operations/HyperContentImpl.cs:710–726`

**问题描述：**

`HCLogger.LogVerbose` 是 `[Conditional("HYPERCONTENT_LOG_VERBOSE")]`，调用点在宏关闭时被编译器擦除。
但 `LogLocationTree` 函数体本身未做任何条件保护，导致 `new StringBuilder(512)` 和 `AppendLocation` 递归遍历在每次 `LoadAsync` 中都被执行，即使 Verbose 日志完全关闭。

```csharp
// 当前代码 — StringBuilder 永远分配
private static void LogLocationTree(string address, string typeName, ResourceLocation root)
{
    var sb = new System.Text.StringBuilder(512);   // ← 每次 LoadAsync 都 new，与日志开关无关
    sb.AppendLine($"StartLoad<{typeName}> [{LogFields.ADDRESS}={address}]");
    AppendLocation(sb, root, depth: 0);
    HCLogger.LogVerbose(sb.ToString());            // ← 只有这行被 [Conditional] 擦除
}
```

`LocalContentCatalog.TryGetLocations:272` 中同类问题已用 `#if HYPERCONTENT_LOG_VERBOSE` 整段包裹，并附有注释说明原因，应对 `LogLocationTree` 做同样处理。

**修复建议：**

```csharp
private static void LogLocationTree(string address, string typeName, ResourceLocation root)
{
#if HYPERCONTENT_LOG_VERBOSE
    var sb = new System.Text.StringBuilder(512);
    sb.AppendLine($"StartLoad<{typeName}> [{LogFields.ADDRESS}={address}]");
    AppendLocation(sb, root, depth: 0);
    HCLogger.LogVerbose(sb.ToString());
#endif
}
```

同时 `AppendLocation` 方法也只在 `LogLocationTree` 中被调用，可同样用 `#if` 包裹或标记 `[Conditional]`。

---

### 问题 2：`InvokeCompleted` 异常截断后续回调

**文件：** `Runtime/Operations/AsyncOperationBase.cs:169–179`

**问题描述：**

`_onCompleted` 是 multicast delegate，`Invoke` 按注册顺序依次调用所有订阅者，但一旦任意回调抛出异常，后续注册的回调全部被跳过，且异常会被外层 try/catch 吞掉。

```csharp
// 当前代码 — 回调 A 抛异常则 B、C 被静默跳过
private void InvokeCompleted()
{
    try
    {
        _onCompleted?.Invoke(this);  // A 抛异常 → B、C 不执行
    }
    catch (Exception e)
    {
        HCLogger.LogError($"Exception in operation callback: {e.ToString()}");
    }
}
```

**实际影响举例：**

- `HyperContentImpl.StartLoad` 在 `op.OnCompleted` 上注册了触发 `LoadSucceeded`/`LoadFailed` 事件的回调
- 业务代码（如 UI 层）通过 `ContentHandle.Completed` 间接在 `op.OnCompleted` 上注册资源使用回调
- 注册顺序取决于代码执行顺序。若业务回调先于系统回调注册并抛异常，`LoadSucceeded`/`LoadFailed` 不会触发，导致静默的状态不一致

**修复建议：** 手动遍历 invocation list，对每个委托单独 try/catch：

```csharp
private void InvokeCompleted()
{
    var invocationList = _onCompleted?.GetInvocationList();
    if (invocationList == null) return;
    foreach (Delegate d in invocationList)
    {
        try
        {
            ((Action<AsyncOperationBase>)d)(this);
        }
        catch (Exception e)
        {
            HCLogger.LogError($"Exception in operation callback: {e}");
        }
    }
}
```

> **注意：** `GetInvocationList()` 会分配一个 `Delegate[]`，但仅在有订阅者时执行，且每次操作完成只触发一次，代价可接受。若希望零分配，可通过维护显式 `List<Action<AsyncOperationBase>>` 替代 event 实现。

---

### 问题 3：`ResolveAddressToAssetIndex` 热路径 `ToLowerInvariant()` 分配

**文件：** `Runtime/Catalog/LocalContentCatalog.cs:703–715`

**问题描述：**

每次通过 GUID 加载资源时（即每次 `LoadAsync` 传入 32 位 hex 字符串），都会调用 `ToLowerInvariant()` 分配一个新字符串：

```csharp
// 当前代码 — 每次 GUID 查找都 alloc
private int ResolveAddressToAssetIndex(string address)
{
    if (address.Length == 32 && IsHex(address))
    {
        string lowerGuid = address.ToLowerInvariant();  // ← 热路径 alloc
        if (_guidToAssetIndex != null && _guidToAssetIndex.TryGetValue(lowerGuid, out int idx))
            return idx;
    }
    // ...
}
```

**修复建议：**

将 `_guidToAssetIndex` 的 comparer 改为 `StringComparer.OrdinalIgnoreCase`，直接传入原始 `address`，无需 `ToLowerInvariant()`：

```csharp
// BuildLookupStructures 中
_guidToAssetIndex = new Dictionary<string, int>(
    _schema.assetRecords.Count, StringComparer.OrdinalIgnoreCase);  // 改这里

// ResolveAddressToAssetIndex 中
if (address.Length == 32 && IsHex(address))
{
    if (_guidToAssetIndex != null && _guidToAssetIndex.TryGetValue(address, out int idx))  // 直接传 address
        return idx;
}
```

`OrdinalIgnoreCase` 的哈希/比较开销极小，远低于每次分配新字符串的 GC 压力。

---

## 🟡 中优先级问题

### 问题 4：三个 `InstantiateAsync` 重载大量代码重复

**文件：** `Runtime/Operations/HyperContentImpl.cs:292–423`

**问题描述：**

三个重载各约 30 行，共享代码约占 80%，唯一区别仅是 `Object.Instantiate` 的调用变体：

```csharp
// 重载 1：parent
instanceResult = pParent != null
    ? UnityEngine.Object.Instantiate(assetOp.Result, pParent)
    : UnityEngine.Object.Instantiate(assetOp.Result);

// 重载 2：parent + worldSpace
instanceResult = pParent != null
    ? UnityEngine.Object.Instantiate(assetOp.Result, pParent, pInstantiateInWorldSpace)
    : UnityEngine.Object.Instantiate(assetOp.Result);

// 重载 3：position + rotation + parent
instanceResult = pParent != null
    ? UnityEngine.Object.Instantiate(assetOp.Result, pPosition, pRotation, pParent)
    : UnityEngine.Object.Instantiate(assetOp.Result, pPosition, pRotation);
```

以下公共逻辑在三个重载中完全相同：
- `StartLoadAssetOp<GameObject>(pAddress, pNetworkOptions)` 调用
- `assetOp.OnCompleted += ...` 订阅结构（含 profiler sample 开关）
- `_instanceRegistry.Track(instanceResult, assetOp)`
- `AllocateHandleId(assetOp)` 调用
- 返回 `new ContentHandle<GameObject>(...)` 构造

**修复建议：** 提取私有核心方法，接受 `Func<GameObject, GameObject>` 参数：

```csharp
private ContentHandle<GameObject> InstantiateAsyncCore(
    string pAddress,
    LoadNetworkOptions pNetworkOptions,
    Func<GameObject, GameObject> pInstantiator)
{
    var assetOp = StartLoadAssetOp<GameObject>(pAddress, pNetworkOptions);
    if (assetOp == null) return default;

    GameObject instanceResult = null;
    assetOp.OnCompleted += pOp =>
    {
        if (pOp.Status != OperationStatus.Succeeded || assetOp.Result == null) return;
        IGGProfiler.BeginSample($"HC.Instantiate_{pAddress}");
        IGGProfiler.BeginSample($"HC.Instantiate.UnityInst_{pAddress}");
        instanceResult = pInstantiator(assetOp.Result);
        IGGProfiler.EndSample($"HC.Instantiate.UnityInst_{pAddress}");
        _instanceRegistry.Track(instanceResult, assetOp);
        IGGProfiler.EndSample($"HC.Instantiate_{pAddress}");
    };

    int handleId = AllocateHandleId(assetOp);
    return new ContentHandle<GameObject>(assetOp, handleId, () => instanceResult);
}

// 各重载简化为一行
internal ContentHandle<GameObject> InstantiateAsync(string pAddress, Transform pParent, LoadNetworkOptions opts)
    => InstantiateAsyncCore(pAddress, opts, go => pParent != null
        ? UnityEngine.Object.Instantiate(go, pParent)
        : UnityEngine.Object.Instantiate(go));
```

> **注意：** `Func<GameObject, GameObject>` 会产生 lambda 闭包分配，但 `InstantiateAsync` 本身是低频操作（对比 `LoadAsync`），可接受。若不可接受，可改为接口或具体结构体。

---

### 问题 5：`NotifyProgressListenersLocked` O(N) 遍历 `_inFlight`

**文件：** `Runtime/Bundle/BundleDownloadQueue.cs:329–358`

**问题描述：**

每次进度通知（`Enqueue`、`WaiterCancelled`、下载完成回调）都在锁内遍历所有 in-flight 下载以统计 `activeLogical`：

```csharp
// 当前代码 — 每次通知都 O(N) 遍历
foreach (var kv in _inFlight)
    activeLogical += kv.Value.Waiters.Count;
```

下载密集时，`_inFlight` 可能有数十条记录，而 `NotifyProgressListenersLocked` 在每次 `Enqueue` 时都被调用。

**修复建议：** 维护独立计数器 `_activeLogicalCount`，在 waiter 加减时同步更新：

```csharp
private int _activeLogicalCount;  // 新增字段

// AddWaiterLocked 末尾
_activeLogicalCount++;

// 移除 waiter 时（WaiterCancelled、OnComplete 回调）
_activeLogicalCount--;

// NotifyProgressListenersLocked 中
var snapshot = new DownloadQueueProgressSnapshot
{
    ActiveLogicalCount = _activeLogicalCount,  // 直接读字段，O(1)
    // ...
};
```

---

### 问题 6：进度回调内 `new List<>` 高频分配

**文件：** `Runtime/Bundle/BundleDownloadQueue.cs:225–233`

**问题描述：**

`StartPhysicalDownload` 的进度回调在每次收到进度更新时都分配一个新 `List`：

```csharp
pProgress =>
{
    List<BundleDownloadEnqueueOptions> snapshot;
    lock (_gate)
        snapshot = new List<BundleDownloadEnqueueOptions>(pGroup.Waiters);  // ← 每帧进度都 alloc
    for (int i = 0; i < snapshot.Count; i++)
        snapshot[i].OnProgress?.Invoke(pProgress);
},
```

下载进度频繁时（每秒数十次），这会产生持续的 GC 压力。

**修复建议 A（简单）：** 直接在锁内遍历，避免 snapshot 分配（前提：`OnProgress` 回调保证不抛异常或重新进入锁）：

```csharp
pProgress =>
{
    lock (_gate)
    {
        for (int i = 0; i < pGroup.Waiters.Count; i++)
            pGroup.Waiters[i].OnProgress?.Invoke(pProgress);
    }
},
```

**修复建议 B（安全）：** 使用 `ArrayPool<BundleDownloadEnqueueOptions>` 租用临时数组：

```csharp
pProgress =>
{
    BundleDownloadEnqueueOptions[] rented;
    int count;
    lock (_gate)
    {
        count = pGroup.Waiters.Count;
        rented = ArrayPool<BundleDownloadEnqueueOptions>.Shared.Rent(count);
        pGroup.Waiters.CopyTo(rented, 0);
    }
    try
    {
        for (int i = 0; i < count; i++)
            rented[i].OnProgress?.Invoke(pProgress);
    }
    finally
    {
        ArrayPool<BundleDownloadEnqueueOptions>.Shared.Return(rented, clearArray: true);
    }
},
```

---

### 问题 7：Init 前事件订阅静默丢失

**文件：** `Runtime/Operations/HyperContent.cs:508–518`

**问题描述：**

`OnLoadFailed`/`OnLoadSucceeded` 的 `add` 访问器在 `Impl == null` 时静默丢弃订阅：

```csharp
public static event Action<string, Exception> OnLoadFailed
{
    add    { if (Impl != null) Impl.LoadFailed += value; }  // ← Init 前调用时 value 被丢弃
    remove { if (Impl != null) Impl.LoadFailed -= value; }
}
```

业务代码可能在初始化前（如 `Awake` 阶段）注册事件监听器，导致永远收不到回调，且无任何日志或异常提示。

**修复建议：** 在 facade 层维护 pending list，Init 完成后 forward：

```csharp
private static event Action<string, Exception> _pendingLoadFailed;
private static event Action<string>            _pendingLoadSucceeded;

public static event Action<string, Exception> OnLoadFailed
{
    add
    {
        if (Impl != null) Impl.LoadFailed += value;
        else              _pendingLoadFailed += value;
    }
    remove
    {
        if (Impl != null) Impl.LoadFailed -= value;
        _pendingLoadFailed -= value;
    }
}

// InitializeBundleMode 成功时 forward
if (_pendingLoadFailed != null)    { impl.LoadFailed    += _pendingLoadFailed;    _pendingLoadFailed    = null; }
if (_pendingLoadSucceeded != null) { impl.LoadSucceeded += _pendingLoadSucceeded; _pendingLoadSucceeded = null; }
```

---

### 问题 8：StreamingAssets fallback 逻辑重复

**文件：** `Runtime/Catalog/LocalContentCatalog.cs:107–168`

**问题描述：**

Binary 和 Json 两个 case 各自独立实现了相同的 "先 `source`，再尝试 `StreamingAssets/source`" 回退逻辑：

```csharp
// Binary case
byte[] bytes = HyperContentPaths.LoadBytes(source);
if (bytes == null || bytes.Length == 0)
{
    string streamingPath = Path.Combine(Application.streamingAssetsPath, source);
    bytes = HyperContentPaths.LoadBytes(streamingPath);
}

// Json case（完全相同的结构，只是类型换成 string）
string jsonContent = HyperContentPaths.LoadText(source);
if (string.IsNullOrEmpty(jsonContent))
{
    string streamingPath = Path.Combine(Application.streamingAssetsPath, source);
    jsonContent = HyperContentPaths.LoadText(streamingPath);
}
```

**修复建议：** 将 fallback 路径逻辑收拢到 `HyperContentPaths`：

```csharp
// HyperContentPaths.cs 新增
internal static byte[] LoadBytesWithStreamingFallback(string source)
{
    var bytes = LoadBytes(source);
    if (bytes == null || bytes.Length == 0)
        bytes = LoadBytes(Path.Combine(Application.streamingAssetsPath, source));
    return bytes;
}

internal static string LoadTextWithStreamingFallback(string source)
{
    var text = LoadText(source);
    if (string.IsNullOrEmpty(text))
        text = LoadText(Path.Combine(Application.streamingAssetsPath, source));
    return text;
}
```

---

## 🟢 低优先级问题

### 问题 9：`ContentHandle<T>.Completed` event `remove` 静默无效

**文件：** `Runtime/Core/ContentHandle.cs:116–118`

```csharp
remove { }  // ← 完全静默的无操作
```

文档注释说明了不支持退订（"Note: -= is unsupported on struct-based handles"），但 `remove {}` 是完全静默的，业务代码写 `handle.Completed -= callback;` 会误以为退订成功，实际上内存泄漏风险依然存在。

**建议：** 在 `remove` 中加一行日志警告：

```csharp
remove
{
    HCLogger.LogWarn("[ContentHandle] Completed -= is not supported on struct handles. " +
                     "The handler was NOT removed. Capture a flag in the callback instead.");
}
```

---

### 问题 10：`_isInitializing` 无 `volatile` 修饰

**文件：** `Runtime/Operations/HyperContent.cs:29`

```csharp
private static bool _isInitializing;  // ← 无 volatile
```

虽然 Unity 实际运行在单主线程，从多线程并发角度无实际风险，但从 C# 内存模型角度，编译器或 JIT 在理论上可优化掉对这个字段的读取。

**建议：** 改为 `private static volatile bool _isInitializing;`，或使用 `Interlocked.CompareExchange` 实现原子化 init guard，成本极低且消除理论隐患。

---

### 问题 11：`BundleDownloadQueue.WaiterCancelled` 中 `IndexOf` O(N) 查找

**文件：** `Runtime/Bundle/BundleDownloadQueue.cs:145`

```csharp
int idx = group.Waiters.IndexOf(pOpt);  // ← O(N)，N = 同 URL 的 waiter 数量
```

实际上同一 URL 的并发 waiter 数量通常极少（< 10），性能影响微乎其微。记录为已知问题，若日后出现大量同 URL 并发场景，可改为用 `Dictionary<BundleDownloadEnqueueOptions, int>` 维护索引，或将 `Waiters` 改为 `LinkedList<>` 实现 O(1) 移除。

---

## 关联 TODO 已有跟踪

以下问题在 `docs/TODO.md` 中已有明确条目，此处仅补充与上述分析的关联性：

| TODO ID | 描述 | 与本报告关联 |
|---------|------|-------------|
| P0-5 | CatalogLocator / LocalContentCatalog 解析切到后台线程 | 与问题 3（GUID alloc）同属 catalog 热路径优化 |
| P1-4 | `TryGetLocations` 缓存 `ResourceLocation` 减少 GC alloc | 与问题 3 同为 catalog 层 GC 优化，应一并实施 |
| P1-2 | 自适应并发 + `maxConcurrentDownloads` / `maxBytesInFlight` | 与问题 5、6 属同一下载管线优化方向 |

---

*由 Claude Code 生成 · 审查日期：2026-06-02*
