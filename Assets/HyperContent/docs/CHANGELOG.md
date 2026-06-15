# 变更记录

## 2026-06-02 - 修复 SpriteAtlas 间接依赖在 asset 级丢失（白图）+ 构建窗口 DependencyLoadMode 开关

> 承接 asset 级依赖加载：实测发现一个 prefab（HandIndicator）的子节点引用了打进 `UICommonIcon` atlas 的 Sprite，AssetLevel 模式下渲染成**白图**。根因是 SBP 的 **per-asset** 依赖数据（`AssetToFiles`）漏掉了"sprite→atlas"这条**运行时晚绑定**边，而 **per-bundle** 数据（`BundleInfos.Dependencies`）不漏——所以只有 AssetLevel 受影响。修复已实测有效（AssetLevel 下 sprite 正常）。

### § 1. SpriteAtlas 间接依赖恢复（核心修复）

- `DefaultBuildExecutor.AugmentAssetDependencyBundlesFromAssetDatabase` 扩展为两类：
  - **Case 1 直接引用**（原有）：prefab 直接序列化了 `SpriteAtlas` 对象等显式分组资源 → 经 `AssetToBundle` 补 bundle。
  - **Case 2 间接 atlas**（新增）：prefab 引用的是被打进 atlas 的 **Sprite/源图**，`GetDependencies` 只返回源图、不返回 atlas。新增 `BuildSpriteSourceToAtlasMap()`（枚举所有 `SpriteAtlas.GetPackables()`，文件夹展开，建"源图 GUID → atlas GUID"反查表）→ 解析出 atlas → 经 `AssetToBundle` 补其 bundle。
- 保持 owning-bundle-LAST 的 post-order 不变量。Full / Update（changed asset 走本轮 SBP；unchanged asset 走重烘焙 manifest）两条路径都覆盖。

### § 2. 为什么只有 AssetLevel 丢

| 数据源 | 粒度 | 计算时机 | 含 atlas 边 |
|---|---|---|---|
| `IBundleWriteData.AssetToFiles` | per-asset | 早期依赖计算 | ❌ 漏（跟到 Sprite，但不折入晚绑定 atlas 写文件）|
| `IBundleBuildResults.BundleInfos[].Dependencies` | per-bundle | archive 写盘后 | ✅ 含（此时 atlas 打包已解析）|

> 补充：BundleLevel "不丢" 也可能部分是侥幸——公共 atlas 常被其他资源的宽闭包通过 `OperationCache`（按 LocationHash 共享 + 引用计数）一直常驻，从而掩盖了某条真正缺失的 per-bundle 边。无论如何，正解是把依赖在 **asset 级显式化**。

### § 3. 构建窗口加 DependencyLoadMode 开关

- `HyperContentBuildWindow` 在 Catalog Format 下新增 **Dependency Load Mode** 下拉（`AssetLevel` / `BundleLevel`），改完 `SaveConfig` 写入 `BuildConfig`；**注意 `settings.json` 是构建产物，只有 Full/Update Build 成功跑完才会写入新模式**。

### § 4. 文档

- `CATALOG_SCHEMA.md` 新增 [2.4.1 SpriteAtlas dependency recovery]，并把 2.4 的 subset 注记改为中性的诊断说明（去掉之前"atlas 必然超集 / BundleLevel 必然漏 atlas"的不准确表述）。

---

## 2026-05-29 - Asset 级依赖加载（schema v2）：per-asset 依赖 bundle + 全局 DependencyLoadMode 开关 + 构建期校验/报告/Inspector

> 把运行时依赖加载从 **Bundle 级** 扩展为 **Asset 级**：加载资源 `a` 时只加载 `a` 的 owning bundle + `a` 自己真实依赖的 bundle，而不是 owning bundle 的整条 bundle 级传递闭包。通过全局 `DependencyLoadMode` 开关保留旧 bundle 级行为作为回滚/A-B。**破坏性升版：`CatalogSchema.schemaVersion` 1→2、`CatalogBinaryReader/Writer` BINARY_FORMAT_VERSION 1→2，必须整体重打 catalog。**

### § 1. Schema / 序列化（破坏性升版）

- `CatalogSchema.AssetRecordEntry` 新增 `List<int> dependencyBundles`（内联存储，owning bundle 排最后，保持 post-order 不变量；`null`/空 = 无 asset 级数据）。`CurrentSchemaVersion` 1→2。
- `CatalogBinaryWriter` / `CatalogBinaryReader` BINARY_FORMAT_VERSION 1→2，新增 `WriteIntList` / `ReadIntList`（`-1` count 作 null 哨兵）。
- 新增 `ErrorCode.CATALOG_ASSET_DEPS_MISSING = 1010`：AssetLevel 模式下 asset 缺依赖数据时**大声报错并让该次加载失败**，不静默回退 bundle 级。

### § 2. 构建期：从 SBP 计算 per-asset 依赖

- `DefaultBuildExecutor.BuildAssetDependencyBundlesFromSbp`：从 `IBundleWriteData.AssetToFiles` + `FileToBundle` 算每个 asset 的自洽最小 bundle 集（owning LAST），并用 `AugmentAssetDependencyBundlesFromAssetDatabase` 补 SBP 漏报的跨 bundle 引用（典型：prefab 直接引用的 `SpriteAtlas`）。
- `CatalogGenerator` 把 bundle 名解析为索引写入 `dependencyBundles`。

### § 3. 全局 DependencyLoadMode 开关

- 新增 `DependencyLoadMode` 枚举（`AssetLevel=0` / `BundleLevel=1`），固化进 `settings.json`（`RuntimeSettings.dependencyLoadMode`），由 `BuildConfig.dependencyLoadMode` 在 Full / Update 两条 settings 写出路径落地。
- `LocalContentCatalog` 按模式构建并选用 `_assetFlatDepsCache`（AssetLevel）或 `_bundleFlatDepsCache`（BundleLevel，旧逻辑原样保留）。`HyperContent.InitializeInternal` 接线，`HyperContentImpl.TryReplaceRuntimeCatalog` 在 hot-update 重载时沿用同一模式。

### § 4. Update Build：未变更 asset 依赖恢复

- `BuildManifest.CachedAssetState` 新增 `dependencyBundleNames`（Full Build 持久化 asset 级依赖 bundle 名，owning LAST）。
- `UpdateBuildExecutor.GenerateMixedCatalog`：变更/新增 asset 用本轮 SBP 数据，未变更 asset 从 manifest 恢复，统一经 `ResolveDepBundleIndices` 映射为索引填入 catalog。

### § 5. 三道防护（可观测性）

- **子集校验** `DefaultBuildExecutor.ValidateAssetDepsSubsetOfBundleClosure`：asset 级 ⊄ owning bundle 的 bundle 级闭包时以 **Error 日志报出但不阻断构建**（因 atlas 增强可能合法地超出纯 SBP 闭包）。
- **diff 报告**：`build_report.json` 新增 `assetLevelDiff`（每 asset 相对 bundle 级收窄了哪些 bundle + 汇总）。
- **Inspector**：`HyperContentRuntimeInspector` / `HyperContentLiveInspectorWindow` 新增 "Deps" 页，按 address 查 asset 级依赖 bundle 集（经 `HyperContentDiagnostics.TryQueryDependencyBundles` / `LocalContentCatalog.TryGetDependencyBundleNamesForDiagnostics`）。

### 注意

- 编辑器默认 `UseAssetDatabase` Play 模式不经 bundle / `LocalContentCatalog`，不受影响；`UseExistingAssetBundle` 与真机共用 Full Build catalog，走新逻辑。
- 升级后**必须 Full Build 重打 catalog**（schemaVersion 已升）才能在真机/UseExistingAssetBundle 下加载。

---

## 2026-05-22 - D3 前置：bundle 元数据日志 + Extract/Instantiate 子段拆分 + catalog Verbose alloc hotfix（合并条目）

> 本期 D2 落地后业务侧用 VipTimeBoxItem.prefab（53 个依赖 bundle 的极端 UI 样本）跑测试场景采数，暴露 4 个新维度的盲区（bundle 实际字节数 / Extract 黑盒分布 / Instantiate Track 是否偷成本 / catalog 查表异常 0.662ms 比 MainChat 快 10×）；同步发现 D2 § Catalog 段还有一处 `HCLogger.LogVerbose` 调用前 StringBuilder + for 循环 alloc 残留——`[Conditional]` 守卫不擦除前置语句，关 `HYPERCONTENT_LOG_VERBOSE` 后仍按 deps 数量 O(n) 浪费时间，可能就是 0.662ms 的根因。
>
> 三件事合并到本条目：(1) bundle 元数据日志（LogDiagnostic/LogBundleSize 基础设施 + 三个 provider 调用点）；(2) Extract / Instantiate 子段拆分（不动逻辑只加 sample）；(3) catalog Verbose alloc hotfix（真 bug 修复）。三件事打包是因为它们都是为了下一轮 D3 测试矩阵采数能拿到更精细的数据维度——下面 § 各自独立可读。

---

### § 1. bundle 元数据日志：LogDiagnostic / LogBundleSize 基础设施 + 三 provider 调用点

#### 背景

D2 落地后，`HC.BundleIO_*` sample 给出每个 bundle mmap 的 wall clock，但**缺乏字节数维度**——VipTimeBoxItem 的 `built_in_data` (74ms) vs `duplicateassets16` (72ms) 时间几乎一致，但是因为前者真的大、还是后者本身小但 worker pool 被挤导致同样耗时？无法区分。需要 bundle 字节数关联耗时分析"小 bundle 大 IO" vs "大 bundle 大 IO"。

#### 核心设计决策：复用 `ENABLE_PROFILERLOG` 而非新增宏

为元数据日志设计输出层时考察过 4 个方案：

| 方案 | 评价 |
|---|---|
| 直接 `Debug.Log + #if ENABLE_PROFILERLOG` 散落在调用点 | ✗ 多处复制粘贴格式不统一，将来加新元数据要复制 |
| 复用 HCLogger.LogVerbose（HYPERCONTENT_LOG_VERBOSE 宏） | ✗ Verbose 是业务调试通道，性能元数据要跟 IGGProfiler 同步开关 |
| 新增独立宏 HC_DIAGNOSTIC_LOG | ✗ 业务侧测试时已习惯单开 ENABLE_PROFILERLOG，新增宏多此一举 |
| **复用 ENABLE_PROFILERLOG，HCLogger 加 LogDiagnostic 方法** | ✓ 元数据 + 性能 sample 强相关（开 ENABLE_PROFILERLOG 跑测试时两者一起看），单开关易用 |

最终方案：在 `HCLog.cs` 加 3 个新方法（`LogDiagnostic` / `LogBundleSize` / `LogBundleSizeBytes`），全部 `[Conditional("ENABLE_PROFILERLOG")]` 守卫。输出格式与 IGGProfiler 视觉对齐，`grep "HC\."` 能一次性抓到所有 HyperContent 性能诊断输出：

```
IGGProfiler:    [yyyy-MM-dd,HH:mm:ss.fff][性能统计]   HC.X_y: Z ms ...
LogDiagnostic:  [yyyy-MM-dd,HH:mm:ss.fff][性能元数据] HC.X_y: Z unit ...
```

#### 关键实现细节

##### 为什么 bundle size 必须有 LogBundleSize / LogBundleSizeBytes 两个方法

PAD 主路径下 `_bundleLoader.LoadFromFileAsync` 收到的 `filePath` 是 `base.apk` 整包路径，**不能用 `FileInfo` 取 size**——会拿到几百 MB 的整 apk 字节数，是错误数据。但 PAD API 已经在 `assetLocation.Size` 把 bundle 真实字节数喂到嘴边，可直接传入。

| 方法 | 适用 provider | size 来源 | IO 行为 |
|---|---|---|---|
| `LogBundleSize(name, filePath, source)` | BundleFileProvider / PAD store | `new FileInfo(filePath).Length` | 同步 IO（关宏后 `[Conditional]` 整体擦除，不发生）|
| `LogBundleSizeBytes(name, bytes, source)` | PAD 主路径 | 调用方传 `assetLocation.Size` | 无 IO |

##### IO 计算必须包在方法体内（避免调用前一行 alloc 不擦除）

`[Conditional]` 只擦除调用点 + 参数表达式（A2 已验证），但调用前一行的 `new FileInfo(...)` IO 是独立语句，**不会被擦除**。所以 `LogBundleSize` 内部把 `FileInfo.Length` 调用放进方法体，整段被 `[Conditional]` 守卫保护：

```csharp
[Conditional("ENABLE_PROFILERLOG")]
public static void LogBundleSize(string bundleName, string filePath, string source = "disk")
{
    if (string.IsNullOrEmpty(filePath)) return;
    if (filePath.IndexOf("://", System.StringComparison.Ordinal) >= 0) return;  // jar URI 旁路
    try
    {
        long bytes = new System.IO.FileInfo(filePath).Length;
        LogBundleSizeBytes(bundleName, bytes, source);
    }
    catch (System.Exception e) { ... }
}
```

##### `source` 字段区分三条加载路径

`source` 取值约定：`disk`（普通磁盘 / store / streamingAssets 解压副本）/ `pad`（PAD apk 内 mmap）/ `store`（PAD hot-update CDN 副本）/ `remote`（远程 CDN 内存路径，本期未接入）。分析时可一眼看出 IO 特征差异（mmap apk vs 普通文件 vs CDN 缓存）。

#### 改动清单

| 文件 | 改动 | 行数 |
|---|---|---|
| `Shared/HCLog.cs` | 新增 `LogDiagnostic` / `LogBundleSize` / `LogBundleSizeBytes` 三方法 + 设计决策注释 | +52 |
| `Runtime/Providers/BundleFileProvider.cs` | `Provide` 中 `LoadFromFileAsync` 调用前加 `LogBundleSize(internalId, filePath, "disk")` | +2 |
| `Runtime/Providers/PlayAssetDeliveryBundleProvider.cs` | `OnPackReady` 加 `LogBundleSizeBytes(pInternalId, assetLocation.Size, "pad")`；`TryLoadFromBundleStore` 加 `LogBundleSize(pInternalId, localPath, "store")` | +6 |

#### 预期数据变化

下一次跑 D2 测试，每个 `HC.BundleIO_*` 之前会先看到对应的 `HC.BundleSize_*`：

```
[2026-05-22,...][性能元数据] HC.BundleSize_built_in_data: 856.3 KB source=pad
[2026-05-22,...][性能统计]   HC.BundleIO_built_in_data: 74.103ms ...

[2026-05-22,...][性能元数据] HC.BundleSize_duplicateassets16: 12340.5 KB source=pad   ← 注意大小异常
[2026-05-22,...][性能统计]   HC.BundleIO_duplicateassets16: 72.862ms ...
```

`grep "HC\."` 一把抓元数据 + 耗时，然后 `awk` 算每条 bundle 的 KB/ms 比值，立刻能识别"小 bundle 但 IO 慢"（说明 worker pool 被挤）vs"大 bundle IO 慢"（数据量本身大），这是之前一直缺的数据维度。

---

### § 2. catalog Verbose alloc hotfix（D2 hotfix #3，VipTimeBoxItem 0.662ms 异常根因）

#### 背景

VipTimeBoxItem.prefab（53 dep）首次加载日志显示 `HC.Load.Stage.Catalog_*` 段 0.662ms，比 MainChatWindow（8 dep）的 0.07ms **慢 10 倍**。`LocalContentCatalog.TryGetLocations` 内部仅 1 次 dictionary lookup + 1 次数组索引，理论上 O(1) 与 deps 数量无关，0.662ms 与 deps 数量比线性增长——**说明有按 deps 数量伸缩的隐藏成本**。

#### 根因

`LocalContentCatalog.TryGetLocations` 在解析完 deps 后构造一段 verbose 日志：

```csharp
if (bundleLocations.Count > 0)
{
    var sb = new System.Text.StringBuilder();
    sb.Append($"[HC.Catalog] Resolve '{address}' → bundle[{record.bundleIndex}] deps({bundleLocations.Count}): ");
    for (int i = 0; i < bundleLocations.Count; i++)
    {
        if (i > 0) sb.Append(", ");
        sb.Append(bundleLocations[i].InternalId);
    }
    HCLogger.LogVerbose(sb.ToString());
}
```

`HCLogger.LogVerbose` 是 `[Conditional("HYPERCONTENT_LOG_VERBOSE")]`——关宏后**只擦除调用点本身 + 参数表达式（即 `sb.ToString()` 这一调用）**，但前面 7 行的 `StringBuilder.Append` + for 循环 + `bundleLocations[i].InternalId` 字符串拼接是独立语句，编译器不会擦除——会留在 IL 里每次调用 `TryGetLocations` 都执行。VipTimeBoxItem 53 dep × 字符串拼接 + GC alloc + `$"..."` interpolation ≈ 0.6ms，与实测完全吻合。

这是 D2 落地后第三处埋点设计失误（前两处分别是 5/21 的 Provide 段 End 时机污染、PAD 路径 BundleIO 缺失），都属于"`[Conditional]` 边界认知偏差"——`[Conditional]` 不是万能开关，前置 statements 必须显式 `#if` 或包到方法体内。

#### 修复

整段（含 `if (bundleLocations.Count > 0)` 判断）用 `#if HYPERCONTENT_LOG_VERBOSE` 包，关宏后整段从 IL 擦除。

```csharp
#if HYPERCONTENT_LOG_VERBOSE
if (bundleLocations.Count > 0)
{
    var sb = new System.Text.StringBuilder();
    ...
    HCLogger.LogVerbose(sb.ToString());
}
#endif
```

#### 改动清单

| 文件 | 改动 | 行数 |
|---|---|---|
| `Runtime/Catalog/LocalContentCatalog.cs` | `TryGetLocations` 中 verbose 日志整段加 `#if HYPERCONTENT_LOG_VERBOSE` 包 + 决策注释 | +12（注释为主）|

#### 预期数据变化

修复后 VipTimeBoxItem 的 `HC.Load.Stage.Catalog_*` 应从 0.662ms 降到 ~0.05ms（与 MainChat 8 dep 的 0.07ms 同数量级，O(deps) 退化为 O(1)）。

---

### § 3. Extract 子段拆分（D2 P1：Issue / Wait / Complete 三段）

#### 背景

D2 落地后 `HC.Extract_*` 给出整段 wall clock，但其中：(1) `bundle.LoadAssetAsync` 同步入队耗时；(2) Unity worker 反序列化 + GPU upload + 帧延迟；(3) `request.completed` 回调内 handle.Complete 耗时——这三段合并不可见。VipTimeBoxItem `HC.Extract_VipTimeBoxItem.prefab: 155ms` / PlayerIcon `HC.Extract_PlayerIcon_default02.png: 80ms` 等数据无法判断是 Unity 内部黑盒 vs 业务回调链 vs 入队压力。

#### 设计

不动外层 `HC.Extract_<asset>` sample 名（向下兼容），内部插入三个子段：

| Sample | 段含义 | 实现位置 | 预期 |
|---|---|---|---|
| `HC.Extract.Issue_<asset>` | `bundle.LoadAssetAsync` 同步调用本身（创建 `AssetBundleRequest` 对象 + 入队）| 同作用域，BeginSample / EndSample 紧贴调用前后 | < 1ms |
| `HC.Extract.Wait_<asset>` | 从 Issue 完成到 `request.completed` 回调入口（黑盒大头）| 跨 lambda：Begin 在 Issue 段后，End 在 lambda 入口最早处 | 占整段 95%+ |
| `HC.Extract.Complete_<asset>` | `request.completed` 回调内同步段（`handle.Complete` / `Fail`）| 跨 lambda：Begin 在 Wait End 后，End 在 lambda 末尾 | < 1ms |

外层 `HC.Extract_<asset>` = Issue + Wait + Complete 三段串行总和，三段之和应 ≈ 外层 ±2%。

#### 数据 → 决策映射

| 信号 | 业务侧含义 | 触发的优化项 |
|---|---|---|
| `Issue ≫ 1ms` | 主线程压力大（很少见，Unity 内部入队应是常量时间）| 异常排查，看主线程是否被其他工作阻塞 |
| `Wait` 占 95%+ | 黑盒由 Unity 决定，框架侧无优化空间 | Resource 侧瘦身（拆 atlas / 降图压缩 / 降模型 LOD），或 OPT-A 预热 bundle 后单独评估资产 Extract |
| `Complete ≫ 1ms` | `handle.Complete` 链上业务侧 OnCompleted 回调有重活 | 看堆栈定位具体业务回调 |

#### 关键实现细节

跨 lambda 必须 `#if ENABLE_PROFILERLOG` 整段包：lambda body 还有非 `[Conditional]` 的业务逻辑（`handle.Complete` / `Fail` / 异常路径 `bundle.GetAllAssetNames`），不能用 `#if` 包整个 lambda；BeginSample / EndSample 必须各自单独 `#if` 包，规则与 D2 PAD BundleIO 埋点 / 5/21 hotfix 一致。

`request.completed` lambda 的两条退出路径（asset != null 走 `handle.Complete` / asset == null 走 `handle.Fail`）必须**各自独立** `EndSample Complete + EndSample Extract`，不能依赖 lambda 末尾统一收——因为 `handle.Complete` 后有 `return`，统一收会错过成功路径。

#### 改动清单

| 文件 | 改动 | 行数 |
|---|---|---|
| `Runtime/Providers/BundleAssetExtractor.cs` | `Provide` 中外层 `HC.Extract_*` 不动，内部插入 Issue / Wait / Complete 三段；跨 lambda 段 `#if ENABLE_PROFILERLOG` 包；成功 / 失败两路径各自独立 EndSample | +24（含决策注释）|

---

### § 4. Instantiate Track 拆分：把 `_instanceRegistry.Track` 包进总段 + 加 UnityInst 子段

#### 背景

VipTimeBoxItem 的 `HC.Instantiate_*` 段 409ms，但当前埋点只包了 `Object.Instantiate` 一行，**`_instanceRegistry.Track(instanceResult, assetOp)` 在 sample 外侧**——这导致两个问题：(1) 业务视角的"实例化总成本"应该包 Track，但 sample 拿不到；(2) 如果 Track 出现意外开销（dictionary rehash / weakref 注册），现有埋点完全看不到。

进一步拆分 UnityInst（反序列化 vs Awake 链）超出框架埋点能力——`Object.Instantiate` 是黑盒同步 API，必须用 Unity Profiler attach 看 Player 内部 Marker，或在测试侧做控制变量 baseline（空 prefab vs 业务 prefab）通过差值法分离。这是 D3 测试矩阵的事，不在本期埋点范围。

#### 设计

| Sample | 段含义 | 改动 |
|---|---|---|
| `HC.Instantiate_<addr>`（外层）| Object.Instantiate + Track 总段 | 含义扩展（之前只包 Object.Instantiate）|
| `HC.Instantiate.UnityInst_<addr>`（新增）| 纯 Unity Object.Instantiate（prefab 反序列化 + Awake 链 + 业务异常）| 内层子段 |

向下兼容性：Track 实现是 dictionary 注册（< 1ms），所以外层 wall clock 几乎不变（差 < 1ms）。

#### 数据 → 决策映射

| 信号 | 含义 | 行动 |
|---|---|---|
| `UnityInst ≈ 总段（差 < 1ms）` | 框架开销可忽略，409ms 全在 Unity API 内部 | 资源侧排查（prefab 节点数 / 组件数 / 子图引用）或业务侧排查 Awake 链（VipTimeBoxItem 的 NRE 就属此类）|
| `总段 - UnityInst > 5ms` | Track 出现意外开销 | 内部审计 `_instanceRegistry.Track`（dictionary rehash / weakref 注册）|

#### 改动清单

| 文件 | 改动 | 行数 |
|---|---|---|
| `Runtime/Operations/HyperContentImpl.cs` | 两个 `InstantiateAsync` 实现重载（parent + worldSpace / position + rotation + parent）的 `assetOp.OnCompleted` lambda 内：把 `_instanceRegistry.Track` 包进 `HC.Instantiate_*` 总段；新增内部 `HC.Instantiate.UnityInst_*` 紧贴 `Object.Instantiate` 调用前后；跨 lambda 段 `#if ENABLE_PROFILERLOG` 包 | +30（含决策注释）|

---

### 整体改动清单（4 个 § 合并）

| 文件 | 改动 | 行数 |
|---|---|---|
| `Shared/HCLog.cs` | 新增 LogDiagnostic / LogBundleSize / LogBundleSizeBytes 三方法 | +52 |
| `Runtime/Providers/BundleFileProvider.cs` | LogBundleSize 调用 | +2 |
| `Runtime/Providers/PlayAssetDeliveryBundleProvider.cs` | OnPackReady + TryLoadFromBundleStore 各加 size 日志 | +6 |
| `Runtime/Catalog/LocalContentCatalog.cs` | Verbose alloc hotfix（整段 `#if` 包） | +12 |
| `Runtime/Providers/BundleAssetExtractor.cs` | Extract 三子段 | +24 |
| `Runtime/Operations/HyperContentImpl.cs` | Instantiate UnityInst 子段 + Track 包入总段 | +30 |
| **合计** | | **+126** |

### 关 ENABLE_PROFILERLOG 时的零成本保证

- **`HCLogger.LogDiagnostic` / `LogBundleSize` / `LogBundleSizeBytes`** 全部 `[Conditional("ENABLE_PROFILERLOG")]`，调用点 + 参数表达式整体擦除。`LogBundleSize` 内 `new FileInfo(...)` IO 包在方法体内，关宏后不发生
- **catalog Verbose 日志整段** `#if HYPERCONTENT_LOG_VERBOSE` 包（独立宏，与 ENABLE_PROFILERLOG 解耦），关 Verbose 时 StringBuilder + for 循环从 IL 完全擦除
- **Extract 三子段 + Instantiate UnityInst 子段** 全部用 inline 字符串 `IGGProfiler.BeginSample/EndSample($"...")`，`[Conditional]` 关宏后调用点 + inline 字符串拼接整体擦除（A2 已验证）。**不需要 `#if ENABLE_PROFILERLOG` 包**——见下方 § Errata

### Errata（首次提交时的过度保护，已修正）

首次提交时受 D2 文档「跨 lambda 必须 `#if ENABLE_PROFILERLOG` 包」规则的过度影响，把 BundleAssetExtractor / HyperContentImpl 里 inline 字符串参数的 `IGGProfiler.BeginSample/EndSample($"...")` 也用 `#if` 包了一遍——形成**双重守卫**。修正：删除冗余 `#if`，保持 inline 字符串 + 单层 `[Conditional]` 守卫。

**两种合法写法的边界**：

| 写法 | 是否需要 `#if ENABLE_PROFILERLOG` 包 | 适用 |
|---|---|---|
| **inline 字符串参数** `IGGProfiler.BeginSample($"HC.X_{key}");` | **不需要**，`[Conditional]` 自动擦除调用点 + 参数表达式 | sample 名只用一次 / 同 lambda 内多处使用但接受拼接 N 次 |
| **变量声明 + 多处使用** `string s = $"HC.X_{key}"; BeginSample(s); ... EndSample(s);` | **必需**，`string s = $"..."` 是普通变量声明、不是 `[Conditional]` 调用，关宏不擦除 | sample 名跨 lambda 捕获复用，需要避免字符串拼接 N 次 |

**项目内现状**（修正后）：

| 调用点 | 写法 | 理由 |
|---|---|---|
| `BundleAssetExtractor.Provide`（Extract 三子段） | inline 字符串 | 同 lambda 内，每处 BeginSample/EndSample 各自 inline 拼接，开宏时拼接 ~5 次（关宏全擦），换代码简洁 |
| `HyperContentImpl.InstantiateAsync`（Instantiate 拆分，两个重载） | inline 字符串 | 同上，与 BundleAssetExtractor 风格统一 |
| `PlayAssetDeliveryBundleProvider`（PAD BundleIO + Pack） | 变量声明 + `#if` 包 | sample 名被 lambda 捕获，且 5/21 hotfix 已稳定，不在本批改动范围 |
| `ResourceManager`（D2 Schedule + Provide 段） | 变量声明 + `#if` 包 | 同上，5/21 已稳定 |

**学到的教训**：D2 文档原条款「跨 lambda 必须 `#if` 包」**应当被修正为更精确的版本**——「跨 lambda 复用变量必须 `#if` 包；inline 字符串参数不需要」。原条款在 PAD / ResourceManager 那种"变量被 lambda 捕获"的场景下是对的，但被机械应用到所有跨 lambda 场景就过度保护。现存 PAD / ResourceManager 用变量声明 + `#if` 是合理的（避免开宏时多次字符串拼接），不需要回头改。

> 后续埋点决策心智模型：先问 "sample 名要不要被 lambda 捕获 / 跨语句复用？"——是 → 变量 + `#if`；否 → inline + `[Conditional]`。两套写法功能等价，关宏后都零开销，差异只在开宏时字符串拼接次数 + 代码行数。

### 待业务侧验证

下一轮 D3 测试矩阵采数前，先用 VipTimeBoxItem.prefab 重跑一次现有 D2 测试场景，确认：

1. `HC.Load.Stage.Catalog_*` 从 0.662ms 降到 ~0.05ms（catalog hotfix 生效）
2. 每个 `HC.BundleIO_*` 之前都有对应 `HC.BundleSize_*`（disk size 元数据落地）
3. 每个 `HC.Extract_*` 拆出 Issue / Wait / Complete 三段，三段之和 ≈ 外层 ±2%
4. `HC.Instantiate_*` 段拆出 UnityInst 子段，差值 ≈ Track 耗时（预期 < 1ms）

如果 (1) 不成立（仍 0.6ms 量级），说明 catalog 段还有别的隐藏成本，需要进一步拆 RootLookup vs DepWalk sub-stage。其他三项预期都会按设计落地。

注：VipTimeBoxItem.prefab 实例化时 `UIConfigUtil.ApplyLocalizedFontTextSize:215` 抛 3 次 NRE 仍未修，会污染 Instantiate 段时间，业务侧自行权衡先修 NRE 还是换样本资源。

---

## 2026-05-21 - D2 hotfix（合并条目：Provide 段 End 时机修正 + PAD 路径 BundleIO 埋点补齐）

> 同一天发现的两个 D2 初版设计盲区，合并到一个条目记录避免文档碎片化。下方 § "Provide End 时机" 与 § "PAD BundleIO 缺失" 是两个独立子修复，可分别阅读。

---

### § PAD 路径 BundleIO 埋点补齐（用户复盘 MainChat 数据时发现）

#### 背景

D2 hotfix（Provide 段 End 时机修正）落地后，业务侧用 MainChatWindow + ChatWorldTabWidget 复跑采数。MainChat（主窗、首次加载、依赖 bundle 必须 cache miss）的日志里 **完全没有 `HC.Resolve_<bundle>` / `HC.BundleIO_<bundle>` sample**——但 MainChat 105ms 的 Schedule 段（跨 2 帧）从设计上必然包含真实的 bundle mmap + 头反序列化耗时，这段不可能为 0。

#### 根因

D2 初版只在 `BundleFileProvider.Provide` 里埋了 `HC.Resolve_<bundle>` / `HC.BundleIO_<bundle>`：

```
Assets/HyperContent/Runtime/Providers/BundleFileProvider.cs:83-97
    IGGProfiler.BeginSample($"HC.BundleIO_{internalId}");
    _bundleLoader.LoadFromFileAsync(internalId, filePath, bundle =>
    {
        IGGProfiler.EndSample($"HC.BundleIO_{internalId}");
        ...
```

但 Android 真机开 `GOOGLE_PLAY_ASSET_DELIVERY` 宏后，`HyperContentImpl` 注册的是 `PlayAssetDeliveryBundleProvider`（共用 `ProviderId = BundleFileProvider.ID`）替代 `BundleFileProvider` 作主路径，`BundleFileProvider` 仅在 PAD 失败时作 fallback。PAD provider 内部两个 `_bundleLoader.LoadFromFileAsync` 调用点（`OnPackReady` 主路径 + `TryLoadFromBundleStore` hot-update 路径）**完全没有埋点**——意味着 Android 真机主路径下 bundle 的 IO wall clock 全部隐身，全部叠到 `HC.Load.Stage.Schedule_*` 段里看不出明细。

#### 修复

`PlayAssetDeliveryBundleProvider` 两条 `_bundleLoader.LoadFromFileAsync` 路径都补 `HC.BundleIO_<internalId>` 埋点，与 `BundleFileProvider` 同名（业务侧分析时无须区分两条路径）。

| 路径 | 触发条件 | 实际做的 IO |
|---|---|---|
| `OnPackReady` 主路径 | pack ready 后 mmap split-APK | 真 bundle IO |
| `TryLoadFromBundleStore` | hot-update 缓存命中 | 真 bundle IO（CDN 文件） |

不另加 `HC.Resolve_<bundle>` 段：PAD 路径的"路径解析"语义已被 `HC.PAD.Pack_<packName>`（首次 pack 拉取）+ 同步几微秒的 `ResolvePackName` / `GetAssetLocation` 覆盖，再加 sample 会和 PAD pack 段重叠且没意义。

| Provider | 路径解析段 | bundle IO 段 |
|---|---|---|
| `BundleFileProvider` | `HC.Resolve_<bundle>`（包 `ResolveFilePath`） | `HC.BundleIO_<bundle>` |
| `PlayAssetDeliveryBundleProvider` | `HC.PAD.Pack_<packName>`（首次拉 pack；复用不重） | `HC.BundleIO_<bundle>`（**本次补**） |

#### 关键实现细节

##### EndSample 必须放在 lambda 入口最早处

PAD `OnPackReady` 主路径下 `LoadFromFileAsync` 失败时会走 `_fallbackProvider.Provide(pHandle)` 转入 `BundleFileProvider.Provide`——后者会**再次** `BeginSample` 同名 sample。如果 PAD BundleIO 没在 lambda 入口先 EndSample，会和 fallback 那次 BeginSample 在 IGGProfiler `_allNodes` 字典上冲突报 "already exists"。

`TryLoadFromBundleStore` hot-update 路径下 `LoadFromFileAsync` 失败时会重试走 `OnPackReady`——后者也会 BeginSample 同名 sample，同样要求 store 路径的 EndSample 必须在 lambda 入口最早处。

实现方式（两条路径一致）：

```csharp
#if ENABLE_PROFILERLOG
string padBundleIOSample = $"HC.BundleIO_{pInternalId}";
IGGProfiler.BeginSample(padBundleIOSample);
#endif
_bundleLoader.LoadFromFileAsync(pInternalId, ..., pBundle =>
{
#if ENABLE_PROFILERLOG
    IGGProfiler.EndSample(padBundleIOSample);  // 必须最早，先于任何分支
#endif
    if (pBundle != null) { ... }
    else { _fallbackProvider.Provide(pHandle); }
});
```

##### 跨 lambda 必须 #if 整段包（与现有 PAD pack 埋点一致）

PAD provider 的 lambda body 还有非 `[Conditional]` 的业务逻辑（`pHandle.Complete` / `_fallbackProvider.Provide` / fallback retry），**不能**用 `#if` 包整个 lambda。BeginSample / EndSample 必须各自单独 `#if` 包，关 `ENABLE_PROFILERLOG` 时调用点 + closure 字符串变量声明都从 IL 擦除。

#### 改动清单

| 文件 | 改动 | 行数 |
|---|---|---|
| `Runtime/Providers/PlayAssetDeliveryBundleProvider.cs` | `OnPackReady` 主路径 + `TryLoadFromBundleStore` hot-update 路径各加 `HC.BundleIO_<internalId>` Begin/End（跨 lambda `#if` 包，EndSample 在 lambda 入口最早处兼容 fallback 重试） | +18 |

#### 预期数据变化

修复后，下次 MainChat 真机采数应该能看到（按 logcat 顺序）：

```
HC.Load.Stage.Catalog_<MainChat>
HC.PAD.Pack_<packName>           ← 如果是首次拉 pack（前面没访问过），否则不出现
HC.BundleIO_<dep_bundle_1>       ← 第一个依赖 bundle 的 PAD mmap，新增
HC.BundleIO_<dep_bundle_2>       ← 第二个依赖（如果有），新增
...
HC.BundleIO_<MainChat_bundle>    ← root bundle 自己的 PAD mmap，新增
HC.Extract_MainChatWindow.prefab
HC.Load_<MainChat>
HC.Load.Stage.Provide_<MainChat>
```

`HC.Load.Stage.Schedule_*` 段（约 100ms 那一段）会被 `HC.BundleIO_*` 拆出明细——bundle IO 是大头还是 Pump 拆帧延迟是大头，一目了然。

#### 真机闭环验证（2026-05-21 修复后采数）

业务侧重新打包后跑同一组流程（冷启动 → 进主城 → 点开聊天主窗 MainChat → 切到子 tab ChatWorld），数据归档：

**MainChat 的 8 个依赖 bundle 全部可见**（hotfix 前是完全隐身的）：

| Bundle internalId | wall clock | BeginFrame → EndFrame |
|---|---|---|
| `built_in_data` | **74.103ms** | 2557 → 2558 |
| `duplicateassets16` | 72.862ms | 2557 → 2558 |
| `featurewindowrule1` | 65.254ms | 2557 → 2558 |
| `ui_mailwindow_general_atlas` | 54.263ms | 2557 → 2558 |
| `ui_uicommon_atlas_guildicon_atlas` | 42.830ms | 2557 → 2558 |
| `ui_uicommon_chatbubble_atlas` | 31.488ms | 2557 → 2558 |
| `ui_uicommon_worldmapbookmark_atlas` | 20.143ms | 2557 → 2558 |
| `mainui_window_mainchatwindow`（root bundle） | 29.332ms | 2557 → 2559 |

**关键观察**：

1. 8 个 bundle 全部 **同帧 BeginSample（Frame 2557）+ Unity AsyncOperation worker pool 并发执行**——Schedule 段实际 wall clock = 101.585ms ≈ 最长 bundle IO（built_in_data 74ms）+ Pump 拆帧 + DAG 构建。如果走串行加载，wall clock 会变 ~390ms（累加），D2 现有调度在做正确的事情。
2. ChatWorld 作为 MainChat 子 tab，Schedule 段 6.371ms 同帧完成且无 BundleIO sample——印证了"父窗加载已经把所有依赖 bundle 加载到内存"的预期，子 tab 走 cache hit fast path（`_bundleLoader.IsLoaded == true`，BundleFileProvider/PAD 直接 `handle.Complete(existing)` 不进 BundleIO 段）。
3. `HC.PAD.Pack_<packName>` 在本次日志里**没出现**——说明本次冷启动到 MainChat 加载之前，相关 pack 已经被前面其他流程触发过 retrieve（`_packRequests` cache hit 复用已完成的 request 不重 sample）。这是预期行为。

#### 闭环结论

✓ PAD BundleIO 补齐子项闭环。Android 真机主路径下 bundle mmap wall clock 现在完全可见，业务侧分析任何 cache miss prefab 的"Schedule 段为什么慢"时数据完整。

---

### § Provide 段 End 时机修正（首次真机采数即暴露的设计 bug）

### 背景

D2 落地后第一次真机冷启动 → 主城 → 点开 SevenDayRewardWindow 采数，从 logcat 提取核心数据：

| sample | 耗时 | BeginFrame → EndFrame | 解读 |
|---|---|---|---|
| `HC.Load.Stage.Catalog_8ac4...` | 0.068ms | 582→582 | catalog 解析正常 ✓ |
| `HC.Load.Stage.Schedule_8ac4...` | 22.114ms | 582→583 | DAG + Pump 1 帧延迟，符合预期 ✓ |
| `HC.Extract_SevenDayRewardWindow.prefab` | **166.115ms** | 583→588 | 真实 `bundle.LoadAssetAsync` 耗时 |
| `HC.Load_8ac4...`（业务 wall clock） | **192.674ms** | 582→588 | OnCompleted 链早段位置（StartLoadAssetOp 注册） |
| `HC.Load.Stage.Provide_8ac4...` | **293.412ms** | 583→588 | **比 HC.Load_ 还多 100+ ms，时序违反预期** |

期间业务侧 callback 链跑了一长串：`LoadWindowPrefab_SevenDayRewardWindow_FromAddressable` 198ms → `CreateWindow_PrefabLoading_SevenDayRewardWindow` 313ms（轻微性能警告）→ `UIControllerBase_Init` 3.9ms / `_Prepare` 1.9ms / `_PrepareFinish` 1.4ms → `ShowStackWindow_SevenDayRewardWindow` 558ms（轻微性能警告）→ `AddressableManager_AssetLoad_8ac4...` 320ms。

### 根因：OnCompleted 订阅顺序导致的时间污染

D2 初版在 `ResourceManager.ExecuteImmediate` 通过 `op.OnCompleted += _ => EndSample(provideSample)` 订阅 Provide End。但订阅时机 **是该 op OnCompleted 链的最末**：

| 订阅顺序（早 → 晚） | 注册位置 | 触发时执行的工作 |
|---|---|---|
| #1 | `ResourceManager.StartOperation` Schedule 安全网（已被 ExecuteImmediate `_activeScheduleSamples.Remove` 主动 Remove，跳过） | — |
| #2 | `HyperContentImpl.StartLoad` | `LoadSucceeded?.Invoke(address)` |
| #3 | `HyperContentImpl.StartLoadAssetOp` | `HC.Load_<addr>` EndSample → 输出 log（带 stack trace 几十微秒到 ms 级） |
| #4..#N | 业务侧通过 `ContentHandle.OnCompleted` 注册 | `_LoadWindowPrefab` → `InstantiateWindow` → `CreateWindow` → `UIControllerBase.Init/Prepare/Show` 整段 prefab 实例化 + 初始化（实测 100+ ms） |
| #N+1（最末） | `ResourceManager.ExecuteImmediate`（D2 初版） | `HC.Load.Stage.Provide_<addr>` EndSample → **stopwatch 此时已被业务回调耗时污染** |

`.NET MulticastDelegate` 严格按 `+=` 顺序触发，所以 Provide End 永远是最末——这导致 Provide 段语义从设计意图的"Provider 异步链 wall clock"（应 ≈ 166ms，与 Extract 段对齐）漂移成了"Provider + 业务 OnCompleted 回调链总耗时"（实测 293ms）。

**真相**：HC.Load_ ≈ 192ms 已经包含了 #1..#3 的全部 + 一点儿系统开销，是业务可见 wall clock 的合理近似；HC.Load.Stage.Provide_ 多出的 ~100ms 全是 #4..#N 业务 callback 的污染。

### 修复方案

把 Provide End 从 OnCompleted 链移到 `op.SetSucceeded` / `SetFailed` **内部、`InvokeCompleted` 调用之前**——这是 Provider 完成的真实时刻，回调链还没启动。

实现：
1. **`AsyncOperationBase`** 新增 `internal string _provideSampleName`（`#if ENABLE_PROFILERLOG` 包），`SetSucceeded` / `SetFailed` 头部加 `if (_provideSampleName != null) { com.igg.core.IGGProfiler.EndSample(_provideSampleName); _provideSampleName = null; }`，置 null 防双 End。
2. **`ResourceManager.ExecuteImmediate`** 把 `op.OnCompleted += _ => IGGProfiler.EndSample(provideSample);` 替换为 `op._provideSampleName = provideSample;`，BeginSample 仍在 op.Execute 之前。

### 边界 case 心智检查

| 路径 | `_provideSampleName` 是否被设置 | SetSucceeded/SetFailed 行为 |
|---|---|---|
| Root op 正常成功（业务 LoadAsync → Provider 完成 → SetSucceeded） | ExecuteImmediate 设置 | EndSample → 置 null → InvokeCompleted ✓ |
| Root op Provider 内部抛 Exception（ExecuteImmediate try-catch → SetFailed） | 已设置 | EndSample → 置 null → InvokeCompleted ✓ |
| Root op 依赖加载失败（HandleDependencyFailure → SetFailed，op 永不进 ExecuteImmediate） | 未设置 | if 跳过，不污染失败语义 ✓ |
| Dep op（LoadDependency 创建，IsUserFacing == false） | ExecuteImmediate 中 `if (op.IsUserFacing && ...)` 不满足，未设置 | if 跳过 ✓ |
| 业务提前 Release → cache.Release → op.Dispose（op 还未 SetSucceeded） | ExecuteImmediate 已设置 | Dispose 不调 SetSucceeded/SetFailed → sample 残留 → 下次同地址 LoadAsync 时 BeginSample 报 "already exists" 诊断信号（与 Schedule 段策略一致——业务侧已存在严重 bug，残留 sample 反而是有用诊断） |

### 改动清单

| 文件 | 改动 | 行数 |
|---|---|---|
| `Runtime/Operations/AsyncOperationBase.cs` | +`_provideSampleName` 字段（`#if` 包），SetSucceeded / SetFailed 头部各加 4 行 EndSample 块 | +21 |
| `Runtime/Operations/ResourceManager.cs` | `ExecuteImmediate` 中 `op.OnCompleted += ...` 替换为 `op._provideSampleName = provideSample`，注释更新解释 hotfix 原因 | ~5 |

### 关 ENABLE_PROFILERLOG 时零成本验证

- `_provideSampleName` 字段声明在 `#if` 内，关宏时字段不存在
- `SetSucceeded` / `SetFailed` 内的 if + EndSample 调用在 `#if` 内，关宏整段擦除
- `ResourceManager.ExecuteImmediate` 中设置点本来就在已有 `#if ENABLE_PROFILERLOG` 块内（D2 初版结构），关宏时一并擦除

### 预期数据变化

修复后，重新采数（采数后追加到本条目下方）：

- `HC.Load.Stage.Provide_*` 应从 ~293ms 降到 ~170ms 上下，与同 root op 的 `HC.Extract_*.prefab` (~166ms) 接近
- `HC.Load.Stage.Catalog_* + Schedule_* + Provide_*` 之和应 ≈ `HC.Load_*`（误差 < 5%）
- 这条等式恢复后，"瓶颈在哪一段"才能从 D2 数据精确判读，后续 A1 / OPT-1 / OPT-3 决策才有数据基础

### 真机闭环验证（2026-05-21 修复后采数）

业务侧重新打包后跑同一组流程（冷启动 → 进主城 → 点开聊天主窗 MainChat → 切到子 tab ChatWorld → 业务侧加载头像 PlayerIcon），三个 root op 数据对比：

| 窗 / 段 | hotfix 前 | hotfix 后 | 验证 |
|---|---|---|---|
| **MainChat** | | | |
| `HC.Load_*` | 373.277ms | 371.417ms | 不变（HC.Load_ 不动） ✓ |
| `Catalog` | 0.068ms | 0.070ms | 不变 ✓ |
| `Schedule` | 104.814ms | 101.585ms | 不变 ✓ |
| `Provide` | **395.699ms ❌** | **267.121ms ✓** | ≈ Extract 266.148ms（差 ~1ms） |
| 等式偏差 (`C+S+P` − `Load`) | +127.3ms | **+2.6ms (0.7%)** | ≤ ±5% ✓ |
| **ChatWorld**（MainChat 子 tab，依赖 cache hit） | | | |
| `HC.Load_*` | 97.377ms | 82.965ms | 不变 ✓ |
| `Catalog` | 0.040ms | 0.040ms | 不变 ✓ |
| `Schedule` | 6.943ms | 6.371ms | 不变 ✓ |
| `Provide` | **163.188ms ❌** | **74.733ms ✓** | ≈ Extract 73.992ms（差 ~0.7ms） |
| 等式偏差 | +72.8ms | **+1.8ms (2.2%)** | ≤ ±5% ✓ |
| **PlayerIcon_default02.png**（新增 case，业务侧 head icon） | | | |
| `HC.Load_*` | — | 105.569ms | — |
| `Catalog` | — | 0.057ms | — |
| `Schedule` | — | 21.151ms | 跨 1 帧（Pump 拆帧延迟，依赖 bundle 全 cache hit） |
| `Provide` | — | 81.621ms | ≈ Extract 80.270ms（差 ~1.4ms） ✓ |
| 等式偏差 | — | **+2.7ms (2.6%)** | ≤ ±5% ✓ |

### 闭环结论

✓ Provide 段 End 时机修正子项闭环。三个 root op 全部满足 `Catalog + Schedule + Provide ≈ Load` 等式，误差均 ≤ ±5%（实测 0.7%、2.2%、2.6%）。Provide 段时间污染问题彻底解决，hotfix 后该段精确等于 Provider 异步链 wall clock，与业务侧 OnCompleted 回调链时间完全解耦。

### 学到的教训（写给后续维护）

D2 初版没考虑到 `OnCompleted` 链上业务订阅会被插在 ExecuteImmediate 注册的 EndSample 之前，是 **设计层面的盲区**。相同思路（用 `OnCompleted += EndSample`）的埋点都有这个风险——以后类似设计要先问一句"这个 EndSample 是不是 OnCompleted 链上最后注册的"，如果是，必须换成 `op.SetSucceeded/SetFailed` 内部主动 End 才能保证语义。

`HC.Load_<addr>` 在 D2 之前就是 OnCompleted += 模式，但因为它的注册位置在 ExecuteImmediate 之前（StartLoadAssetOp 中），所以在业务 callback 之前触发——侥幸躲过这个问题。但其语义其实也不严格是"业务可见 wall clock"（业务真实可见的 wall clock 应该包含业务自己的 OnCompleted callback），是"用户调用到框架层 OnCompleted 早段"的近似值。本期 hotfix 不动 HC.Load_，避免改动面扩大；如果未来要把 HC.Load_ 也改成"业务真实可见 wall clock"，需要在业务侧 ContentHandle.OnCompleted 完成后另行打 sample（D1 任务范畴）。

### Owner / 状态

- 状态：✅ 已完成 + 已闭环验证（2026-05-21）
- Follow-up：无遗留——Provide End 时机 + PAD BundleIO 两个子项的真机数据均已归档到上方"真机闭环验证"区，等式 ≤ ±5% 全部满足

---

## 2026-05-21 - D2 加载链路阶段化 wall clock 埋点（独立先行项 Phase 0'）

### 背景与定位

5 月 milestone 总目标 "HyperContent 加载更快、更稳"，AAB 整包阶段聚焦 catalog Init → bundle load → asset extract → instantiate 四段。先前已落地的埋点（A2、B1 hotfix、P0-6）覆盖了 **Provider 内部各段绝对耗时** 和 **整段 wall clock**：

- 整段：`HC.Load_<addr>` / `HC.Instantiate_<addr>`
- Provider 内部：`HC.Resolve_<bundle>` / `HC.BundleIO_<bundle>` / `HC.Extract_<assetPath>`
- Catalog Init：`HC.Catalog.Init.{IO,Decompress,Deserialize,BuildLookup,Total}[<format>]`

但有 **4 个业务可见 wall clock 盲区** 仍不可观测，直接导致 Phase 2/3 后续优化决策 "拍脑袋"：

1. **Catalog 解析（hot path）**：每次 LoadAsync/InstantiateAsync 都调 `_catalog.TryGetLocations`，Address 表退化或 hash 冲突会被掩盖在 `HC.Load_<addr>` 整段里
2. **DAG 构建 + 依赖等待 + Pump 拆帧延迟**：业务一帧 LoadAsync N 个 prefab、依赖深 K 层时，最坏 wall clock = K 帧 × frameTime；这是 A1（拆帧限流）vs OPT-1（去拆帧延迟）方向选择的关键判据，但 milestone 文档至今承认 "前提不知道"
3. **Provider 内部完整异步链作为整体的 wall clock**：D/E/G 三段加起来到底占整段多少，目前只能从已有埋点拼凑，且未覆盖 root op 完成回调链路
4. **PAD pack 拉取首次延迟**：AAB InstallTime 模式下 `PlayAssetDelivery.RetrieveAssetPackAsync` 在中低端设备首次 200ms+，完全不可见

D2 是 D1 的轻量先行版——D1 需要业务接入上报 SDK 才能拿到 P50/P95，D2 只用 IGGProfiler 现有日志就能读，**零业务侧适配成本**。优先级 D2 > D1：所有 Phase 2/3 后续决策都以 D2 数据为基线，特别是 A1 / OPT-1 / OPT-3 / B2 的触发条件。

### 改动清单

| 文件 | 改动 | 行数 |
|---|---|---|
| `Runtime/Operations/AsyncOperationBase.cs` | 新增 `internal bool IsUserFacing` 字段 | +9 |
| `Runtime/Operations/HyperContentImpl.cs` | `StartLoadAssetOp<T>` / `LoadSceneAsync` 把 `_catalog.TryGetLocations` 单行拆出来，前后包 `HC.Load.Stage.Catalog_<addr>` Begin/End | +14 |
| `Runtime/Operations/ResourceManager.cs` | +`using com.igg.core`；新增 `_activeScheduleSamples` HashSet（`#if` 包）；`LoadAsync<T>` / `LoadSceneAsync` cache miss 后设 `op.IsUserFacing = true`；`StartOperation` 入口 `HC.Load.Stage.Schedule_<addr>` Begin + 失败路径安全网；`ExecuteImmediate` Schedule End + `HC.Load.Stage.Provide_<addr>` Begin/End（订阅 OnCompleted 在 op.Execute 之前以兼容同步完成） | +52 |
| `Runtime/Providers/PlayAssetDeliveryBundleProvider.cs` | +`using com.igg.core`；`GetOrCreatePackRequest` 包 `RetrieveAssetPackAsync` → `Completed`，加 `HC.PAD.Pack_<packName>` Begin/End | +18 |
| `docs/2026-05_TODO_MILESTONE.md` | 总览表新增 Phase 0' 行；§4 D1 后插入完整 D2 章节（含改造前事实 + 数据 → 决策映射表）；§6 验证流程 sample 列表分组重排 | +90 |

总计 5 文件 / ~80 行（不含文档）。

### sample 设计

4 个新 sample 的 Begin/End 位置和覆盖范围：

| Sample | Begin 位置 | End 位置 | 覆盖含义 |
|---|---|---|---|
| `HC.Load.Stage.Catalog_<addr>` | `StartLoadAssetOp` 内 `_catalog.TryGetLocations` 调用前 | 同一调用之后 | catalog 查表纯耗时（hot path） |
| `HC.Load.Stage.Schedule_<addr>` | `ResourceManager.StartOperation` 入口 | `ExecuteImmediate` 实际跑 `op.Execute` 之前（正常路径）；或 dep 失败时 op.OnCompleted 安全网（失败路径） | DAG 构建 + 所有依赖等待 + ScheduleExecute → Pump 拆帧延迟 三段合并 |
| `HC.Load.Stage.Provide_<addr>` | `ExecuteImmediate` 注册 OnCompleted 之前 | op.SetSucceeded/SetFailed 触发的 OnCompleted 回调 | provider 内部完整异步链（含 Resolve + BundleIO + Extract 等已有 sample 之和） |
| `HC.PAD.Pack_<packName>` | `GetOrCreatePackRequest` 调 `RetrieveAssetPackAsync` 之前 | `PlayAssetPackRequest.Completed` 回调；或 `fresh.IsDone` 兜底主动 End | 首次 PAD pack 拉取 wall clock；后续同 pack 复用 `_packRequests` 不会再触发 |

业务可见 wall clock 等式（root op 路径上）：

```
HC.Load_<addr>
  ≈ HC.Load.Stage.Catalog_<addr>
  + HC.Load.Stage.Schedule_<addr>
  + HC.Load.Stage.Provide_<addr>
  + 业务侧 OnCompleted 回调耗时（通常 < 1ms，未单独埋）

HC.Load.Stage.Provide_<addr>
  ⊇ HC.Resolve_<bundle> + HC.BundleIO_<bundle> + HC.Extract_<assetPath>
  + 各 Provider 间状态切换 + IBundleLoader 内部状态查询
```

### 关键实现细节

#### root op vs dep op 区分（IsUserFacing 字段）

Schedule / Provide 两段 sample 必须只在 **业务直接 LoadAsync/LoadSceneAsync/InstantiateAsync 触发的 root op** 上打——dep op（递归 LoadDependency 创建）的耗时已被 `HC.BundleIO_*` / `HC.Resolve_*` 完全覆盖，再叠 sample 会：

1. 爆 IGGProfiler 唯一名字字典（同一 bundle 既被业务直接加载、又被多个 root op 当依赖加载，sample 名重复）
2. 数据冗余且互相干扰（dep op 的 wall clock 已包在父 root op 的 Schedule 段里）

实现：`AsyncOperationBase` 加 `internal bool IsUserFacing` 字段，默认 false（dep op 保持默认）；`ResourceManager.LoadAsync<T>` / `LoadSceneAsync` 在 cache miss 后设为 true，`LoadDependency` 不设。fast path `_cache.TryGetExisting` 命中时直接返回已有 op，不进入 GetOrCreate + StartOperation，所以"dep op 后来被业务直接 LoadAsync"的边界 case 不会重置 IsUserFacing—— sample 漏埋（这种 case 业务不应直接加载 bundle internalId，是错误用法，漏埋可接受）。

字段成本：1 byte / op，关 `ENABLE_PROFILERLOG` 时仍存在但永远是 false（保留字段以避免 #if 包字段声明引入额外的条件编译噪声），可忽略。

#### Schedule 段失败路径安全网（`_activeScheduleSamples` HashSet）

正常路径下 Schedule 段在 ExecuteImmediate 中 EndSample。但 op 在依赖加载阶段 Failed（`HandleDependencyFailure → SetFailed`）会 **永远不进 ExecuteImmediate**——如果不处理，Schedule sample 卡在 IGGProfiler `_allNodes` 字典中：下次同地址 LoadAsync 时 `BeginSample` 检测同名 sample 已存在 → `LogError "already exists"` → 后续也不再正常采样。

修复：`ResourceManager` 加 `_activeScheduleSamples = new HashSet<AsyncOperationBase>()`（整段 `#if ENABLE_PROFILERLOG` 包），StartOperation BeginSample 时 Add(op)、订阅 op.OnCompleted 安全网回调；ExecuteImmediate 主动 Remove(op)。两端 Remove **互斥**——谁先 Remove 谁负责 EndSample，避免双 End 引发 IGGProfiler "not started" LogWarning：

- 正常路径：ExecuteImmediate Remove 成功 → EndSample；OnCompleted 安全网 Remove 失败 → 跳过
- dep 失败路径：op 永不进 ExecuteImmediate，Remove 不成功；OnCompleted 安全网 Remove 成功 → EndSample
- Provider 内部异常路径：ExecuteImmediate try-catch 抓 SetFailed，但 Schedule End 已经在 try 之前先 Remove + EndSample 完成；OnCompleted 安全网回调来自 SetFailed 的 InvokeCompleted，Remove 失败 → 跳过

未覆盖的极端 corner case：业务在 op 还没 SetSucceeded/SetFailed 时主动 Release(handle) → RefCount 归零 → cache.Release → op.Dispose → `_onCompleted = null` 直接清空（参见 `AsyncOperationBase.Dispose`）→ 安全网回调被丢弃 → sample 残留。这种"提前 Release"业务侧本身已是严重 bug，IGGProfiler "already exists" 错误反而是有用的诊断信号，不额外处理。

#### Provide 段同步完成兼容（OnCompleted 订阅必须先于 op.Execute）

`AsyncOperationBase.OnCompleted` 的 add 访问器在 op 已终态（Succeeded/Failed）时 **立即 invoke** 回调；订阅必须先于 op.Execute 才能正确处理 Provider 同步完成路径（典型场景：BundleFileProvider IsLoaded fast path 直接 `handle.Complete(existing)`）。代码顺序：

```
IGGProfiler.BeginSample(provideSample);  // push 进 _allNodes
op.OnCompleted += _ => IGGProfiler.EndSample(provideSample);  // 挂订阅
try { op.Execute(provider, handle); }    // 同步路径会立即跑 handle.Complete → SetSucceeded → InvokeCompleted → 触发 EndSample（pop _allNodes）
```

如果反过来——op.Execute 先于订阅——同步完成时 SetSucceeded 已跑过，`_onCompleted` 还是 null，回调丢失，sample 永远不 End。

#### PAD `Completed` 已完成 add 不自动 invoke

与 AsyncOperationBase.OnCompleted 不同，`PlayAssetPackRequest.Completed` 在 `IsDone == true` 时 += 不会自动触发回调（与 PAD 现有代码模式一致：`if (packRequest.IsDone) OnPackReady(...) else packRequest.Completed += ...`）。所以 D2 PAD 段必须显式 `if (fresh.IsDone) IGGProfiler.EndSample(packSample); else fresh.Completed += _ => IGGProfiler.EndSample(packSample);` 二选一兜底。

#### 关 ENABLE_PROFILERLOG 时的零成本保证

| 段 | 实现方式 | 关宏后 IL 残留 |
|---|---|---|
| Catalog（同作用域） | `IGGProfiler.BeginSample` 是 `[Conditional]`，调用点 + 字符串拼接表达式整体擦除 | 仅 `bool catalogResolved = _catalog.TryGetLocations(...);` 一行（业务必须的） |
| Schedule Begin（StartOperation） | 整段 `#if ENABLE_PROFILERLOG` 包（含 `if (op.IsUserFacing && ...)` 分支 + HashSet.Add + lambda 订阅） | 0 |
| Schedule End / Provide Begin/End（ExecuteImmediate） | 整段 `#if ENABLE_PROFILERLOG` 包 | 0 |
| `_activeScheduleSamples` HashSet 字段 | 字段声明本身在 `#if` 内 | 字段不存在，HashSet 实例不创建 |
| PAD（GetOrCreatePackRequest） | 整段 `#if ENABLE_PROFILERLOG` 包 | 0 |
| `IsUserFacing` 字段 | 字段声明本身**不**在 `#if` 内（避免泄露条件编译到 AsyncOperationBase 公共结构） | 字段保留，1 byte / op，永远 false 不影响逻辑 |

跨 lambda 段必须整段 `#if` 包的原因（与 A2 落地结论一致）：lambda body 仅是 `[Conditional]` 调用时，关宏后 body 为空，但 **lambda 实例 + closure（捕获 sampleName 字符串变量）仍会编译进 IL**——必须把 `+= ...` 订阅本身和 closure 字符串变量声明都纳入 `#if` 才能彻底擦除。

### 验证

D2 自身只是埋点，**行为变更 = 0**。验证策略：

1. 编辑器/Standalone 关 `ENABLE_PROFILERLOG`：反编译看新增 4 个 sample 的 IL 是否完全擦除（或用 `if (false)` dead code elimination 后等价于 baseline）；`AsyncOperationBase.IsUserFacing` 字段保留 + 永远 false（不影响逻辑）
2. 开 `ENABLE_PROFILERLOG`：跑冷启动 / 战斗开局 / UI 大窗切换 各 3 次，从日志按 sample 名分组取 P50 / P95；验证：
   - `HC.Load.Stage.Catalog_*` + `HC.Load.Stage.Schedule_*` + `HC.Load.Stage.Provide_*` ≈ `HC.Load_*`（误差 < 5%）
   - `HC.Load.Stage.Provide_*` ≥ 该地址下所有 `HC.BundleIO_*` + `HC.Extract_*`（理论上 Provide 段包含这些）
   - 失败用例（手动制造 dep 缺失）：Schedule sample 仍能正确 End（不出现 "already exists" / "not started" 日志）
3. 真机 Android（中低端）跑同一组流程，对比编辑器数据是否合理（编辑器无 PAD，`HC.PAD.Pack_*` 仅真机有数据）

### 数据 → 决策映射

D2 落地后，后续优化项的触发条件：

| D2 数据信号 | 触发优化项 | 备注 |
|---|---|---|
| `HC.Load.Stage.Schedule_*` P50 > 16ms 且业务调用集中在 loading screen | A1（Pump 帧预算限流，已在 milestone Phase 3） | 与"加载更快"目标一致 |
| `HC.Load.Stage.Schedule_*` P50 > 16ms 且业务调用分散在玩家可交互场景 | **新增 OPT-1**：root op 在依赖全部 Succeeded 时 ExecuteImmediate 跳过 Pump 直接同步执行 | A1 反向，二选一 |
| `HC.PAD.Pack_*` 首次 P50 > 50ms 且玩家可见路径上 | **新增 OPT-3**：启动 idle 帧预热已知 InstallTime pack | 中风险 |
| `HC.Catalog.Init.Total[*]` ≥ 200ms 且业务登录 RTT ≥ 100ms | B2（已在 milestone Phase 2） | 无新动作 |
| `HC.Load.Stage.Catalog_*` P50 异常凸起（hot path 上 > 1ms） | 新增 catalog lookup 表退化诊断 | 当前未规划，看数据 |

**采数后按映射表触发，本期 D2 仅给数据不动逻辑**。

### Owner / 状态

- 状态：✅ 已完成（2026-05-21）
- 后续 follow-up：D2 落地后第一次冷启动 + 战斗 + UI 真机采数表归档到本 CHANGELOG（待业务侧跑数后追加）

---

## 2026-05-20 - B1 代码完整回滚（决策升级：从"代码资产化保留"改为"完整删除回到 pre-B1"）

### 决策升级背景

上一期"退出路径 A"决定永久走 hotfix 2 全同步、保留 `InstantiateOperation` 包装架构作为"未来重启异步路径的资产"。复盘时审计了 `InstantiateOperation` 的实际代码占比：

- **真实在用代码**：26%（状态机字段 + 3 个 Mode 构造工厂 + Start() 同步分发 + OnAssetCompleted + InstantiateSync + Dispose）
- **永远不会执行的 dead code**：19%（`OnAssetCompletedAsync` adapter / `TriggerInstantiate` async 分支 / `StartUnityInstantiateAsync` / `OnInstantiateCompleted` / late-completion guard，因 `forceSync` 恒为 `true` 永不进入）
- **解释 dead code 为什么保留的注释**：42%（worldPositionStays 根因 / 3 个否决精确修复方向 / 真机数据分桶 / 未来重启 4 个条件…）

也即：**426 行里 74% 是 dead code 加上解释 dead code 为什么保留的注释**。"未来重启异步路径"的 4 个条件（prefab 分布漂移到 50+ ms 重 prefab 为主 + Unity 修 worldPositionStays + Unity 缩 wall-clock 地板 ≤ 0.5 帧 + 业务场景明确接受 wall-clock trade-off）几乎不可能同时满足；即使将来确实要重启，届时 Unity API 与 wall-clock 行为都会变，重新设计比修改保留的 dead code 更可能。

继续保留的成本（426 行 dead code 的认知负担、调用栈深 1–2 层 / debug 时多踩断点、维护者反复评估"为什么不删"）明显大于收益。决策升级——把代码也完整回滚到 pre-B1 形态，**仅保留与 B1 决策无关的代码组织改善（`StartLoadAssetOp<T>` helper + IGGProfiler 埋点）**。

### 删除清单

| 项 | 改动 |
|---|---|
| `Runtime/Operations/InstantiateOperation.cs`（426 行整个文件 + `.meta`） | **删除** |
| `HyperContentImpl.StartInstantiateOperation` private helper | **删除** |
| `HyperContentImpl.InstantiateAsync` 3 个重载里 `new InstantiateOperation(...) + StartInstantiateOperation(...)` 调用 | **内联回 pre-B1 写法**：`var assetOp = StartLoadAssetOp<GameObject>(...); GameObject instanceResult = null; assetOp.OnCompleted += pOp => { ...HC.Instantiate_ 埋点 + Object.Instantiate + Track }; return new ContentHandle(assetOp, handleId, () => instanceResult);` |
| `OperationCache.Release` 的 `LocationHash=0` 防御 `ReferenceEquals` identity check | **删除**——pre-B1 cache 里所有 op 的 `LocationHash` 都非 0（InstantiateOperation 是唯一会用 `LocationHash=0` 流过 Release 的 op，删了 InstantiateOperation 后该场景不再存在）；恢复 pre-B1 的裸 `_cache.Remove(op.LocationHash)` |

### 保留清单（与 B1 决策无关的代码组织改善）

| 项 | 保留理由 |
|---|---|
| `HyperContentImpl.StartLoadAssetOp<T>` private helper | 与 LoadAsync 共用入口校验 + catalog 解析 + `HC.Load_<address>` 埋点。**与 B1 无关的代码组织改善**——pre-B1 时 LoadAsync 和 InstantiateAsync 各自 inline 同款逻辑，抽出 helper 减少重复，对 LoadAsync 也仍然有简化价值 |
| `HC.Load_<address>` IGGProfiler 埋点（在 `StartLoadAssetOp<T>` 内） | 业务侧 prefab 加载 hot triage 工具，长期有用，与 B1 决策无关 |
| `HC.Instantiate_<address>` IGGProfiler 埋点 | 业务侧 prefab 实例化 hot triage 工具，长期有用。已经从 `InstantiateOperation.TriggerInstantiate` 搬到 `HyperContentImpl.InstantiateAsync` 3 个重载内联（在 `Object.Instantiate` 调用前后 `BeginSample` / `EndSample`，与 `HC.Load_<address>` 同款 `#if ENABLE_PROFILERLOG` 包法） |
| B3 改动（`AsyncOperationBase.Dependencies` `List` → `array` + `DependencyCount`） | **完全独立于 B1**，依然完整生效，本期回滚不动一行 |

### 代码改动统计

| 文件 | 改动 |
|---|---|
| `Runtime/Operations/InstantiateOperation.cs` | **删除**（−426 行 + `.meta`） |
| `Runtime/Operations/HyperContentImpl.cs` | 3 个 `InstantiateAsync` 重载内联回 pre-B1 + `StartInstantiateOperation` helper 删除（净 −1 helper / 3 个重载结构调整；逻辑等价 pre-B1） |
| `Runtime/Operations/OperationCache.cs` | `Release` 内 `if (_cache.TryGetValue ... && ReferenceEquals ...)` 恢复为裸 `_cache.Remove(op.LocationHash);` + 删除关于 B1 / 碰撞防御的多行注释 |
| `Runtime/Lifecycle/InstanceRegistry.cs` / `Runtime/Operations/AsyncOperationBase.cs` / `Runtime/Operations/ResourceManager.cs` / `Runtime/Providers/*.cs` / `Runtime/Diagnostics/HyperContentDiagnostics.cs` | **不动**——这些文件 B3 改动（`Dependencies` `List` → `array` + `DependencyCount`）仍然完整生效，与 B1 无关 |

### 行为等价性

回滚后业务可见行为与 **pre-B1 + 加埋点**完全一致：

| 维度 | 状态 |
|---|---|
| `HyperContent.InstantiateAsync(addr)` 同步走 `Object.Instantiate` | ✅ 等价 pre-B1 |
| Cache hit 时 `handle.Result` 同步可读 | ✅ 等价 pre-B1（`assetOp.OnCompleted += lambda` 在 cache hit terminal 时由 `AsyncOperationBase.OnCompleted.add` 同步触发） |
| Cache miss 时 `handle.Result` 异步可读 | ✅ 等价 pre-B1 |
| `worldPositionStays = false` 默认值（UI 按 prefab 布局） | ✅ 等价 pre-B1 |
| `Awake` / `OnEnable` 时 `transform.parent` 正确 | ✅ 等价 pre-B1 |
| Wall-clock 表现 | ✅ 等价 pre-B1 |
| `Release(handle)` + `ReleaseInstance(go)` 双计数 | ✅ 等价 pre-B1（`InstanceRegistry.Track` 内部 `op.RefCount++` 提供第 2 计数，与改造前一致） |
| `HC.Load_<address>` / `HC.Instantiate_<address>` IGGProfiler 埋点 | ✅ 保留（与 B1 决策无关的工具价值） |

### 行为不变性 / 不再存在的事项

- pre-B1 业务代码 100% 等价
- B3 完整生效，不变
- `InstantiateOperation` 不再存在；`OperationCache.Release` 不再需要 `LocationHash=0` 防御
- `InstantiateOperation.cs.meta` 已删除，Unity 编辑器下次刷新自动重建 .meta 索引

### 后续工作建议

- **B1 永久关闭**——代码、决策、文档全部归零，B1 在 milestone 进度由 ✅ 改为 ✗（代码完整回滚，与改造前 100% 等价）
- Phase 2 推进重点回到 **B2（Catalog 后台线程化）**——milestone 文档已经量化 ~130–200 ms 收益，ROI 清晰，与 B1 完全独立，与 B3 也无冲突
- 历史归档：本期之前 5 个 B1 相关 CHANGELOG 条目（B1 落地 / hotfix 1 / hotfix 2 / 实验性回滚 / 退出路径 A）全部保留作为决策演进史，本条目顶部链接已说明各条目状态

### Follow-up

- **无**——B1 全链路决策已收敛到代码完整回滚，无任何遗留问题。`HC.Load_<address>` / `HC.Instantiate_<address>` 埋点是工具性资产，未来业务侧 prefab 加载 / 实例化 hot triage 时可继续使用

---

## 2026-05-20 - B1 退出路径 A · 采数结束，永久回 hotfix 2（B1 与 milestone 目标根本性冲突）

> **历史保留**：退出路径 A 的真机数据分析、决策依据、行为变化矩阵都是准确的、长期有效的，但"代码资产化保留 `InstantiateOperation` 作为未来重启入口"的决定经复盘后被推翻——见上方"B1 代码完整回滚"条目。`InstantiateOperation.cs` 整个文件已删除，3 个 `InstantiateAsync` 重载内联回 pre-B1 写法。本条目的真机数据表 + 物理解释（wall-clock 地板 / prefab 分布不支持 / opt-in 候选不足）仍然是"为什么 B1 不可行"的权威归档。

### TL;DR

真机数据出来了，结论很硬：**B1 cache miss 异步路径与 milestone "加载更快"目标在这个项目的真实 prefab 分布下根本性冲突**——Object.InstantiateAsync 有 1–2 帧 wall-clock 地板（≥ 16–33 ms @60fps），而项目里绝大多数 prefab 同步 Instantiate < 5 ms，没有主线程切片预算来摊销这个地板，导致**异步路径在每一个采样 prefab 上都退化 wall-clock**，轻量 prefab 退化 16–310×。直接走退出路径 A，把 hotfix 2 升级为永久状态：所有路径（cache hit + cache miss + worldSpace）一律走同步 Object.Instantiate，行为 100% 等价 pre-B1。`InstantiateOperation` 包装架构 + IGGProfiler 埋点 + 双路径分发代码全部保留作为资产，未来若项目 prefab 分布剧烈漂移到"50+ ms 重 prefab 为主"再考虑一行 toggle 重启。

### 真机数据（30+ prefab，2026-05-20 实测）

业务侧用上一期"实验性回滚 hotfix 2"打的包做真机采样，对比 `HC.Instantiate_<address>` IGGProfiler sample 同步 baseline（cache hit 同步 Instantiate ms）vs 异步 wall-clock（cache miss `Object.InstantiateAsync` 触发到 instance ready 总 wall-clock ms）：

| Prefab | 同步 baseline（ms） | 异步 wall-clock（ms） | 退化倍数 |
|---|---|---|---|
| **极轻量（同步 < 2 ms，全面退化 16–170×）** | | | |
| Level_CampaignIdle.prefab | 0.25 / 0.19 | 43 | **172×** |
| HeroSpecialRedHeroTab_Attr_FX.prefab | 0.49 / 0.52 | 53.23 | **106×** |
| HeroSpecialRedHeroCard_HeroRankUpFX (×2 实例) | 0.7–0.91 | 61.61 | **~70×** |
| UI_VFX_Cloud_Transition.prefab | 1.484 / 0.634 | 17 | 16× |
| 5343b08f40d2fe74dae4b9b31543f4c6 | 1.32 / 1.34 | 30 | 22× |
| MainActivityGuildDuelWidget.prefab | 1.23 / 1.13 | 64 | **55×** |
| HeroSpecialRedHeroCard_HeroLvUpFX (×2 实例) | 1.08–1.51 | 23.93 | 18× |
| **轻量 UI（同步 1–5 ms，退化 16–310×）** | | | |
| MainActivityCommonItemWidget.prefab (×3 实例) | 1.36–1.75 | 30.4 | 20× |
| MainActivityPopupPackageWidget.prefab | 1.19 / 1.13 | **351** | **310× ⚠** |
| MainActivityPremiumEventWidget.prefab | 2.4 / 2.7 | 34 | 14× |
| 147118bdac62b564d8957c21b77cb4f5 | 1.96 / 2.59 | 32 | 14× |
| b13531c4807c3cd4fae8a5eebce33418 | 4.1 / 5 | 31 | 7× |
| **中等（同步 5–20 ms，退化 2–4×）** | | | |
| 5894d768e69ef9149bb30236f3a5807b | 7.43 / 7.83 | 33 | 4× |
| 228d4121cb46de845b88723b1218f586 | 6.11 / 8.7 | 33 | 5× |
| 394f0b4b6821ac84ca7c80183d2324b6 | 7.9 / 8.5 | 33 | 4× |
| 9e4541957bd0dc04448e6f8f5f32c4e81 | 8.5 / 8.78 | 35 | 4× |
| Hero_1002_Enoch.prefab | 9.32 / 9.45 | 32.86 | 3.5× |
| e7b63f50f67ba6743a6fad089917d058 | 15.86 / 16.36 | 39 | 2.4× |
| ca1274b45b1ec074598d128d7fc17963 | 15.7 / 17.52 | 34 | 2.1× |
| 806bb176d11372b4cbbaa864d6ce2f30 | 17.4 / 17.9 | 41 | 2.3× |
| **重（同步 20–50 ms，退化 1.3–1.7×）** | | | |
| 26948c8ba2232bc4eb8113e6a879560d | 22 / 22.6 | 33 | 1.5× |
| 3494106c559dcb54aa5053bdc58eb6b5 | 23 / 27 | 46 | 1.9× |
| 64dc8a3575f48884d92d1173a3819dbf | 27 / 27 | 45 | 1.7× |
| b4d2c84bf95df39448ad43e83cfb6cc1 | 32.6 / 50 | 55.33 | **1.3× ✅（接近持平）** |
| 24ffd29b66d4abb4fa3145430a8039fe | 35 / 38 | 41 | 1.1× |
| df80e83fb3c00474dbb0ec6dcec721df | 36 / 42 | 101 | 2.7× |
| DailySalesHeroBGPrefab_1203.prefab | 41 / 56 | 60 | 1.2× |
| 6d5e8f2925b9ee843842a67937ee6760 | 42 / 42 | 70 | 1.7× |

### 数据解读 + 退出路径 A 决策依据

**关键澄清：sample 时长的含义**
- 同步路径下 `HC.Instantiate_<addr>` sample 时长 = 主线程占用 ms（BeginSample/EndSample 同帧成对包住同步 `Object.Instantiate`）
- 异步路径下 sample 时长 = **wall-clock**（从 BeginSample 触发 → 跨 worker 线程 deserialize → 跨帧等待 → 完成回调 EndSample，覆盖业务侧"从触发到拿到 instance"整段）
- 异步 wall-clock **不等于**异步主线程占用——异步主线程占用 = sample 时长 − worker 时长 − 跨帧空闲时长

**所以这份数据无法直接证明 B1 主线程切片有没有收益，但揭示了 3 个比"主线程是否有收益"更重要的事实**：

**事实 1：异步路径有 1–2 帧 wall-clock 地板（不可消除）**

Object.InstantiateAsync 至少跨 1–2 帧（@60fps 地板 ≈ 16–33 ms）—— worker 线程调度 + 跨帧 await + completed 回调派发都需要时间。数据中所有异步 wall-clock 都落在 17–60 ms 区间（约 1–4 帧），完美符合预测。极端值 351 ms 是 Object.InstantiateAsync 在 worker 繁忙时的调度延迟，进一步证明 wall-clock 不可控。

**事实 2：极轻量 prefab 退化在物理上无法避免**

`Level_CampaignIdle.prefab` 同步 0.25 ms → 异步 43 ms。即使 Object.InstantiateAsync 在 worker 线程把这 0.25 ms 的主线程占用降到 0，wall-clock 也"省"不出 43 ms——**主线程零收益换 43 ms 业务等待**。`MainActivityPopupPackageWidget.prefab` 同步 1.2 ms → 异步 351 ms（玩家点开礼包要等 350 ms 才出现，明显卡顿）。这一类（同步 < 5 ms）在项目中占样本绝大多数——**没有任何场景下"切异步划算"**。

**事实 3：重 prefab 数量不足以支撑 opt-in**

唯一接近持平的 `b4d2c84bf95df39448ad43e83cfb6cc1` 同步 32–50 ms → 异步 55 ms（退化 1.3×），是 opt-in 唯一候选。但是：
- 仅 1 个候选 prefab → 引入 opt-in API + RuntimeSettings 白名单 + QA 验证（worldPositionStays + Awake-time parent 语义）的工程成本远大于潜在收益（即使主线程占用真的从 50 ms 降到 5 ms，节省 45 ms × 偶尔加载 1 次 = 极有限的总收益）
- 项目长期趋势：业务侧 prefab 越做越拆分（粒度更细，每个更小、更轻），不会朝"50+ ms 重 prefab 为主"漂移
- 业务体感 trade-off：即使主线程节省 45 ms，wall-clock 还是退化 5 ms（55 vs 50），业务侧仍然是"点了 50 ms 后窗口出现"变"点了 55 ms 后窗口出现"——主线程帧率收益对玩家不可感，wall-clock 退化可感

**结论**：B1 的设计假设（"deserialize 重的 prefab 通过 Job 系统并行化主线程开销 → 加载更快"）在这个项目的真实 prefab 分布下**前提不成立**——milestone 目标说的"加载更快"在业务体感语境下 = **wall-clock 减少**，而 B1 异步路径必然导致 wall-clock 增加。

退出路径 A 是唯一与 milestone 目标对齐的选择。

### 改动

`Runtime/Operations/InstantiateOperation.cs`：`OnAssetCompletedAsync` 适配器 `forceSync: false → true`——**单行**。此变更让 cache miss 路径也走同步 `Object.Instantiate`，与 cache hit + worldSpace=true 路径汇合成"全同步"统一行为。

代码注释整段重写（覆盖文件顶部 doc comment / `Start()` / `TriggerInstantiate` / Status semantics 段 / `OnAssetCompletedAsync` adapter）：
- 全局语境从"实验性回滚 + 采数中"改为"hotfix 2 永久（exit path A after real-device data）"
- `Start()` 长注释保留 hotfix 2 关于 worldPositionStays / 3 个否决精确修复方向的完整根因分析（作为 long-term reference 防止后人重新尝试），追加完整的真机数据分桶概览 + 物理解释 + "为什么 B1 假设在本项目不成立"的决策依据
- 文件顶部 doc comment 明确"DO NOT flip `forceSync: true` back to `false` without redoing the real-device measurement"
- `TriggerInstantiate` / Status semantics 段恢复 hotfix 2 时期"all paths sync"语境

`Assets/HyperContent/docs/2026-05_TODO_MILESTONE.md`：
- 总览表 Phase 2 进度 `B1 ✅（hotfix 2 已临时回滚，cache miss 异步采数中⚠）` → `B1 ✅（hotfix 2 永久，B1 与目标根本性冲突，价值=0）`
- B1 hotfix 2 blockquote 追加"决策已基于真机数据收敛"段，归档数据表格 + 决策依据 + 永久状态确认
- 更新历史追加 entry

### 行为变化

| 路径 | 改造前同步 | B1 初版 | hotfix 1 | hotfix 2（首发） | 实验性回滚（已结束） | 退出路径 A（当前永久状态） |
|---|---|---|---|---|---|---|
| Cache hit + 普通 parent | 同步 ✅ | 异步（UI silent fail） | 同步 ✅ | 同步 ✅ | 同步 ✅ | 同步 ✅ |
| Cache miss + 普通 parent | 同步 ✅ | 异步（UI 错位 + wall-clock 退化） | 异步（同上） | 同步 ✅ | 异步（采数用，已知问题） | **同步 ✅** |
| `worldSpace=true` 重载 | 同步 ✅ | 同步 ✅ | 同步 ✅ | 同步 ✅ | 同步 ✅ | 同步 ✅ |
| `PositionRotationParent` 模式 | 同步 ✅ | 异步 | 异步 | 同步 ✅ | 异步 | **同步 ✅** |

退出路径 A 后行为与改造前 100% 等价（含 worldPositionStays 默认值、Awake 时 parent 语义、`handle.Result` 同步可用契约、wall-clock 表现、refcount 双计数）。

### 行为不变性

- pre-B1 业务代码 100% 等价：cache hit + cache miss + worldSpace=true + PositionRotationParent 全部走同步 Object.Instantiate
- `Release(handle)` + `ReleaseInstance(go)` 双计数模型：未变
- IGGProfiler `HC.Instantiate_<address>` sample：覆盖范围未变（同步路径 Begin/End 同步包 sync Instantiate）——业务侧后续 prefab 主线程 hot triage 数据采集继续可用
- `InstantiateOperation` 仍然 `LocationHash=0` + 不入 `OperationCache`，OperationCache.Release identity check（hotfix 2 期间加的）继续生效
- B3（`AsyncOperationBase.Dependencies` `List` → `array`）不受任何影响，依然完整生效

### 长期状态：B1 资产化、价值=0

- **代码**：`InstantiateOperation` 包装类 + `StartUnityInstantiateAsync` 异步分支 + 双路径分发 + late-completion guard + AssetOperation 借用 refcount 模型全部保留
- **埋点**：`HC.Instantiate_<address>` IGGProfiler sample 保留，业务侧继续可用作 prefab 主线程 hot triage 工具
- **价值**：B1 文档原本承诺的"cache miss 主线程切片"收益在本项目 prefab 分布下 = **0**（异步 wall-clock 退化已经否决全开；opt-in 重 prefab 数量不足以支撑工程成本）
- **未来重启条件**（全部满足才考虑）：
  1. 项目 prefab 分布剧烈漂移到"50+ ms 同步 Instantiate prefab 为主"（不太可能）
  2. Unity 修复 `Object.InstantiateAsync` 与 `Object.Instantiate` 的 `worldPositionStays` 默认值不一致问题
  3. Unity 缩小 `Object.InstantiateAsync` 的 wall-clock 地板（≤ 0.5 帧）
  4. 业务侧场景明确接受"用 wall-clock 延迟换主线程切片"的 trade-off（如战斗场景重模型 spawn）

### Follow-up

- 无 follow-up（本期已是 B1 的决策终点）
- 后续 Phase 2 推进重点：B2（Catalog 后台线程化，~150 ms 收益可量化、ROI 清晰）—— 与 B1 完全独立
- B3（`AsyncOperationBase.Dependencies` `List` → `array`）已完整生效，继续保留

---

## 2026-05-20 - B1 实验性回滚 hotfix 2：cache miss 路径恢复异步，采集主线程切片真实收益

> **历史保留**：本期回滚的目的（采集 cache miss 异步 wall-clock vs 同步 baseline 真实数据）已达成，结果见上方"B1 退出路径 A"条目的真机数据表。基于这份数据，B1 的实验性回滚已结束，决策走退出路径 A（永久 hotfix 2）。本条目的"退出路径 A / B"假设和数据采集计划已被上方条目落地，**仅作为决策过程归档保留**。

### 决策背景

hotfix 2（B1 完全降级同步）上线后业务恢复正常，但**留下一个开放问题**：B1 改造的核心价值是 cache miss `Object.InstantiateAsync` 主线程切片，hotfix 2 把这部分价值完全降到 0。要决定 B1 长期是"永久回滚 hotfix 2" 还是"按 prefab 分流（opt-in / 白名单）保留部分收益"，**必须先有 cache miss 主线程切片的真实数据**——否则两个方向都是 ROI 拍脑袋。

提议方向（业务侧也提过）："把异步打开测一下加载速度提升"。这就是本期改动：临时把 hotfix 2 回滚到 hotfix 1 状态（cache hit 同步 + cache miss 异步），用现有的 `HC.Instantiate_<address>` IGGProfiler 埋点采数，按 prefab address 聚合 cache miss 主线程占用分布，再决策是否值得为 B1 引入 opt-in / 白名单架构。

### 改动

`Runtime/Operations/InstantiateOperation.cs`：`OnAssetCompletedAsync` 适配器的 `forceSync` 参数从 `true` 改回 `false`——**单行**。Cache hit 路径仍然走 `Start()` 内 `forceSync: true` 分支（hotfix 1 路径保留），cache miss 路径恢复走 `Object.InstantiateAsync`。

代码注释相应整段重写：
- 顶部 doc comment「Async vs sync dispatch」段从"all paths sync"语境改回"cache hit 同步 + cache miss 异步 + 采数中"语境
- `Start()` 长注释保留 hotfix 2 关于 worldPositionStays / 3 个否决方案的完整根因分析（作为已知副作用 warning 和未来回头查的"为什么不能精确修"），加上"⚠ KNOWN side effect during this window (do NOT file as a new bug)"明显标注 + 两条退出路径说明（A 数据弱 → 回 hotfix 2；B 部分 prefab 有可观收益 → opt-in / 白名单）
- `OnAssetCompletedAsync` adapter 注释明确"实验性回滚 + 测量目的 + hotfix 2 root cause 仍然存在 + 一行 toggle 即可回到 hotfix 2"
- Status semantics 段恢复"cache miss 异步 + cache hit 同步"描述
- `TriggerInstantiate` 内 useSync 分支注释从"async branch is dead code"改回"async branch handles cache-miss path"

### ⚠ 业务侧实测期间须知

**cache miss UI prefab 仍然会出现位置错乱**（hotfix 2 修复的就是这个问题，本期为采数临时回滚）：
- **不要 report 为新 bug**——已知现象，根因是 Unity API `worldPositionStays` 默认不一致，参见上方 hotfix 2 条目根因分析
- **影响范围**：只影响 cache miss（**首次**打开）+ 普通 parent 模式 + RectTransform 有非零 anchoredPosition 的 UI prefab。重复打开同一个 UI（cache hit）走同步路径不受影响
- **影响时长**：仅本次数据采集窗口期间，目标 ≤ 1 周；采数完成立刻按数据做退出决策
- 3D / 特效 / 场景物体（业务侧本来就显式 spawn 在 world 坐标）一般不受影响

### 数据采集计划

**指标**：`HC.Instantiate_<address>` 主线程占用 ms（IGGProfiler sample，B1 落地时已埋好，覆盖业务侧"触发 InstantiateAsync → 拿到 instance"整段主线程占用）

**对比维度**：
- 同一 address 改造前同步 `Object.Instantiate` vs 改造后 cache miss `Object.InstantiateAsync` 的主线程 ms 差值
- 按 prefab 类别聚合（UI / 3D 模型 / 特效 / 场景物体）

**关键守门**（milestone B1 §「关键守门」原文）：净省主线程帧时间需 > 测量噪声，**建议 ≥ 5 ms 才认收益**；简单 prefab 若净省 < 2ms 则保持同步实现，避免给业务侧引入「Result 暂时为 null」的时序适配成本

### 退出路径（采数后必走其一）

**(A) 数据弱**——cache miss 整体或绝大多数 prefab 净省 < 5ms：
- 把 `OnAssetCompletedAsync` 的 `forceSync` 改回 `true`，**回到 hotfix 2 全同步**
- B1 进入"代码保留、价值=0"长期状态
- 优势：业务侧零负担，UI 错位 bug 永久消除

**(B) 数据强**——部分 prefab（很可能集中在 3D / 特效 / 场景物体）净省 ≥ 5ms：
- 引入 opt-in API（如 `HyperContent.InstantiateAsync(addr, parent, trueAsync: true)`）或 RuntimeSettings 内 address 白名单
- 仅对收益明显且 QA 过 worldPositionStays / Awake-time parent 语义的 prefab 启用异步路径
- 其他 prefab（含全部 UI）继续走 hotfix 2 全同步
- 优势：拿到重 prefab 的全部 B1 收益，业务侧只对 opt-in 的几个调用点关心异步语义（这些 prefab 业务侧本来就显式设位置，零额外负担）

无论 A / B，**都要走出本期实验性回滚状态**——避免长期把 UI 错位 bug 留在 cache miss 路径

### 行为不变性

- pre-B1 业务代码的行为：cache hit 100% 一致（hotfix 1 路径保留同步），cache miss UI 错位（已知，本期实验性引入）
- `Release(handle)` + `ReleaseInstance(go)` 双计数模型：未变，两条路径 Track 都在 SetSucceeded 之前调用
- IGGProfiler `HC.Instantiate_<address>` sample：覆盖范围未变（同步路径 Begin/End 同步包 sync Instantiate；异步路径 Begin 在 Trigger、End 在 OnInstantiateCompleted），改造前同步 baseline 与本期异步采数**可以直接对齐**

### Follow-up

- 数据采集计划由 owner 推进，目标 ≤ 1 周完成
- 数据出来后按 §「退出路径」走 A 或 B，**两个路径都需要再发一次 CHANGELOG 条目**记录最终决策与改动
- 本期回滚的代码注释（特别是 `Start()` 长注释的 3 个否决精确修复方向）需要在最终决策的 CHANGELOG 条目里再次引用或归档，避免后续工程师在不知情下重新尝试这些方向

---

## 2026-05-20 - B1 hotfix 2：完整降级同步 Instantiate，修复"UI 加载位置错乱"问题

> **历史保留**：hotfix 2 完整修复了"UI 加载后位置错乱"问题，根因分析（Unity `Object.InstantiateAsync` 与 `Object.Instantiate` 的 `worldPositionStays` 默认值不一致）和 3 个否决精确修复方向的分析都是准确的、长期有效的——但出于"B1 cache miss 主线程切片真实收益需要量化"的需求，hotfix 2 已被上方"实验性回滚"条目临时回滚。回滚期间 UI 错位 bug 会复现，是已知行为。本条目作为完整的"修复方案 + 否决方案分析"留档，**采数完成后无论走退出路径 A 或 B 都需要再次参照**。

### 现象

hotfix 1（cache-hit 同步降级）上线后业务反馈：**问题没解决**，且复测后修正了对症状的描述——**不是"UI 没加载出来"，而是"UI 加载出来后位置不对"**。复现规律：

- 首次打开 UI（cache miss）：UI 出现，但出现在错误的屏幕位置（屏幕中心 / 屏幕外 / 父节点以外某处）
- 重复打开同一 UI（cache hit）：hotfix 1 已经走同步 → 位置正常
- 现象集中在带 `RectTransform` 的 UI prefab（非零 `anchoredPosition` / `localPosition`）

### 根因（与 hotfix 1 完全不同）

是 **Unity 自身 API 行为不一致**，不是 HC 的 bug，也不是 hotfix 1 假设的"业务侧同步读 `handle.Result` 契约破坏"：

| API | `worldPositionStays` 默认 | 结果 |
|---|---|---|
| `Object.Instantiate(prefab, parent)` | **false** | instance.localPosition = prefab.localPosition → UI 按 prefab 编辑器布局显示 |
| `Object.InstantiateAsync(prefab, parent)` | **true** | instance.worldPosition = prefab.localPosition；reparent 时反算 instance.localPosition = `parent.InverseTransformPoint(prefab.localPosition)` → RectTransform 的 anchoredPosition 被改成奇怪值 → UI 出现在错误位置 |

Unity 2022.2+ 引入 `InstantiateAsync` 时为优化重定向行为默认开了 `worldPositionStays = true`，与同步 `Instantiate` 的默认 `false` 不一致。带 RectTransform 的 UI prefab 因为 `anchoredPosition` 通常非零，**所有 cache miss + parent 模式的 UI 都会出现位置错乱**。hotfix 1 只覆盖了 cache hit 路径，cache miss 的"首次打开 UI"仍然走异步 → 仍然错位 → 业务反馈"hotfix 1 没用"。

### 为什么不能精确修复（保留 cache miss 异步收益）

考虑了几种精确修复方向，都有不可接受风险：

| 修复方向 | 致命问题 |
|---|---|
| `InstantiateAsync(prefab)` 不传 parent + 完成后 `SetParent(parent, false)` | instance 创建瞬间在 hierarchy 根、无 parent；prefab 脚本的 `Awake` / `OnEnable` 在那一瞬间触发，**业务脚本读 `transform.parent` 会拿到 null**，破坏隐性 Awake 顺序契约 |
| 完成后手动 reset `localPosition` / `Rotation` / `Scale` 到 prefab 值 | RectTransform 不止 localPosition，还有 anchoredPosition / sizeDelta / pivot / anchors / offsetMin/offsetMax，Unity 内部细节难精确镜像；且仍解决不了 Awake 时序问题 |
| 传 prefab world position 给 `InstantiateAsync(prefab, parent, pos, rot)` 四参重载 | 数学上等价于 `Instantiate(prefab, prefab.pos, prefab.rot, parent)`，**不等于** `Instantiate(prefab, parent, false)`，除非 parent 在世界原点 |

### 修复

`Runtime/Operations/InstantiateOperation.cs`：`OnAssetCompletedAsync` adapter 的 `forceSync` 参数从 `false` 改为 `true`，cache miss 路径也走同步 `Object.Instantiate`。所有路径回退到 pre-B1 同步语义。

具体改动：
1. `OnAssetCompletedAsync(AsyncOperationBase) => OnAssetCompleted(completed, forceSync: true)`——单点开关
2. `Start()` 注释整段重写，明确根因（worldPositionStays 不一致）、否决的精确修复方案、未来重启入口
3. 文件顶部 doc comment 「Async vs sync dispatch」段重写为 hotfix 2 状态描述
4. `TriggerInstantiate` / Status semantics 注释同步更新，标注异步分支当前是 dead code 但保留
5. `InstantiateOperation` 类、`InstantiateSync` / `StartUnityInstantiateAsync` 双路径分发等架构**全部保留**，未来若 Unity 修复 API 行为或引入业务侧 prefab 白名单分流时可一行重启

### 行为矩阵

| 路径 | 改造前 | B1 初版 | hotfix 1 | hotfix 2 |
|---|---|---|---|---|
| Cache hit + 普通 parent | 同步 Instantiate，位置正确 | 异步 InstantiateAsync，handle.Result=null **(UI silent fail)** | 同步 Instantiate，位置正确 ✅ | 同步 Instantiate，位置正确 ✅（与 hotfix 1 等价） |
| Cache miss + 普通 parent + UI prefab | 异步 asset load + 同步 Instantiate，**位置正确** | 异步 asset load + 异步 InstantiateAsync，**位置错乱** | 异步 asset load + 异步 InstantiateAsync，**位置错乱**（hotfix 1 没覆盖到） | 异步 asset load + 同步 Instantiate，**位置正确** ✅ |
| Cache miss + 普通 parent + 3D prefab（localPos=0） | 异步 asset load + 同步 Instantiate | 异步 asset load + 异步 InstantiateAsync，位置碰巧正确 | 同 B1 初版 | 异步 asset load + 同步 Instantiate ✅（与 pre-B1 等价） |
| `worldSpace=true` 重载 | 同步 Instantiate | 同步 Instantiate（Unity 原生 InstantiateAsync 无此参数） | 同步 Instantiate | 同步 Instantiate（行为不变） |
| `PositionRotationParent` 模式（显式 world pos） | 同步 Instantiate | 异步 InstantiateAsync(prefab, parent, pos, rot) | 同 B1 初版 | 同步 Instantiate ✅ |

### B1 价值变化

- ❌ 完全放弃：cache miss `Object.InstantiateAsync` 主线程切片收益。B1 文档原本最看重的"deserialize 重 prefab 首次实例化的主线程并行化"——本期无法兑现。
- ✅ 保留：`InstantiateOperation` 架构本体、`HC.Instantiate_<address>` IGGProfiler 埋点（已实测可用）、双路径分发代码、`AssetOperation` 借用 refcount 模型——未来若引入 prefab 白名单或 Unity 修复 API 行为时一行可重启异步路径
- ⚠️ Phase 2 · B1 进度仍标 ✅ 但**价值被完全降级**——milestone 已同步追加说明，避免后续 follow-up 误认为"B1 已经吃满"
- ✅ B3（AsyncOperationBase.Dependencies List → array）不受影响，依然完整生效

### 行为不变性

- pre-B1 能 work 的全部业务代码，hotfix 2 后行为 100% 一致：worldPositionStays 一致、Awake 时 parent 一致、handle.Result 同步可用一致、refcount 双计数一致
- IGGProfiler `HC.Instantiate_<address>` sample 名 + 时间覆盖不变（同步路径 Begin/End 在 sync Instantiate 两侧成对包裹）
- `Release(handle)` + `ReleaseInstance(go)` 双计数仍然成对触发；Late-completion guard（Status==Disposed 时 Destroy `aio.Result[0]`）当前不可达但代码保留
- `InstantiateOperation` 仍然 `LocationHash = 0` + 不入 `OperationCache`，cache 唯一识别仍然只是 `AssetOperation`

### Follow-up（不在本期）

- 等 Unity 后续版本 fix `Object.InstantiateAsync` 与 `Object.Instantiate` 的 `worldPositionStays` 默认值不一致问题（issue tracker 已多个报告）后，把 `OnAssetCompletedAsync` 的 `forceSync: true` 改回 `false` 即可一行重启 cache miss 异步路径
- 备选方案：业务侧对**确定无 Awake-time parent 依赖**且**确定 worldPositionStays 差异可接受**的特定 prefab 列出白名单 → `InstantiateOperation.Start()` 内根据 address 决定是否走异步，对剩余 prefab 继续走同步降级。需要 D1 埋点（Phase 3）证明白名单内 prefab 的 cache miss Instantiate 主线程切片确实有可观收益，否则不值得动这套白名单
- hotfix 2 已通过 `InstantiateOperation` 顶部 doc comment + `Start()` 长注释把否决的修复方向 + 未来重启入口完整记录在代码侧，未来排查时不需要再翻文档考古

---

## 2026-05-20 - B1 hotfix：cache-hit fast path 同步降级，修复"个别 UI 不显示"问题

> **历史保留**：此 hotfix 上线后被证伪——业务实际症状不是"UI 没加载出来"而是"UI 加载后位置错乱"，根因是 Unity `Object.InstantiateAsync` 与 `Object.Instantiate` 的 `worldPositionStays` 默认值不一致，与此处假设的"`handle.Result` 隐性同步契约"无关。本条目记录的诊断、改动与代码现状都不是错的（cache-hit 同步降级本身仍然生效，作为 hotfix 2 的子集），但**单独不足以解决业务症状**。完整修复见上方 hotfix 2 条目。

### 现象

B1 落地后业务反馈：**个别 UI 不显示但无报错**。规律：第一次打开 UI 正常，**重复打开同一个 prefab** 时 UI 加载失败、无异常日志。

### 根因

改造前的隐性契约（来自同步实现的副作用）：

```text
HyperContent.InstantiateAsync(addr)  →  cache hit  →  AssetOp 已 Succeeded
  → op.OnCompleted += lambda
       ↑ AsyncOperationBase.OnCompleted.add 检测 Succeeded → 立即同步触发 lambda
       ↓ lambda 内同步 Object.Instantiate → instanceResult 立即赋值 → Track
  → return handle      ← 此时 handle.Result 已是 alive instance
```

业务侧多个 UI 工厂依赖这条同步路径写出了 `handle.Result` 立即读的代码：

```csharp
var h = HyperContent.InstantiateAsync(addr);
var go = h.Result;          // 改造前 cache hit：alive instance
go.GetComponent<...>();     // 改造后 cache hit：null，silent fail
```

改造后 `Object.InstantiateAsync` 是**真异步**（≥ 1 帧），`handle.Result` 在 InstantiateAsync 同步返回时是 null；业务侧"假定同步可用"的代码全部走空逻辑——这就是"个别 UI 不显示且无报错"的现象。
- 首次打开 UI：cache miss，业务原本就用 await/Completed → 正常
- 重复打开同一 UI：cache hit，业务"假定同步" → 触发

### 修复

`Runtime/Operations/InstantiateOperation.cs`：在 `Start()` 检测 `_assetOp.Status == Succeeded/Failed` 时直接 `OnAssetCompleted(_assetOp, forceSync: true)`，`TriggerInstantiate(forceSync)` 走同步 `Object.Instantiate` 而非 `Object.InstantiateAsync`。Cache miss 路径继续走异步（`+= OnAssetCompletedAsync` 适配器 → `forceSync: false`）。

具体改动：
1. 新增 `OnAssetCompletedAsync(AsyncOperationBase) => OnAssetCompleted(completed, forceSync: false)` adapter（`event Action<AsyncOperationBase>` 签名带不了 bool 参数，用 1 行适配器代替 lambda 闭包）
2. `OnAssetCompleted` 加 `bool forceSync` 参数透传给 `TriggerInstantiate`
3. `TriggerInstantiate(bool forceSync)`：`useSync = forceSync || (Mode.ParentWithWorldSpace && worldSpace=true)`；同步路径调用新抽出的 `InstantiateSync()` helper，与之前 worldSpace=true 降级共用
4. `InstantiateSync()` helper：3 个 Mode 各自映射到对应同步 `Object.Instantiate` 重载，复制 `StartUnityInstantiateAsync` 的结构以保持对称
5. 顶部 doc comment 更新「Async vs sync dispatch」小节明确两类路径

### 行为矩阵

| 路径 | 改造前 | B1 初版 | hotfix 后 |
|---|---|---|---|
| Cache hit + 普通 parent | 同步 Instantiate，handle 返回时 Result 可用 | 异步 InstantiateAsync，handle 返回时 Result=null **(导致 silent fail)** | 同步 Instantiate，等价改造前 ✅ |
| Cache miss + 普通 parent | 异步 asset load + 同步 Instantiate | 异步 asset load + 异步 InstantiateAsync | 异步 asset load + 异步 InstantiateAsync ✅（B1 主要价值保留） |
| `worldSpace=true` 重载 | 同步 Instantiate | 同步 Instantiate（Unity 原生 InstantiateAsync 无此参数） | 同步 Instantiate（行为不变） |
| Asset Failed cache hit | OnCompleted 同步触发 → SetFailed 透传 | 同上 | 同上 ✅ |

### B1 收益变化

- ❌ 放弃：cache hit 场景的 `Object.InstantiateAsync` 主线程切片。但 cache hit 场景下 prefab 已在内存、Material 已反序列化、Animator 已初始化，`Object.Instantiate` 实际是浅 copy + 主组件 clone，主线程开销远小于 cache miss 首次实例化；这部分 ROI 本来就低。
- ✅ 保留：cache miss 场景（首次加载 deserialize 重 prefab）的主线程切片——这才是 B1 文档「对 deserialize 重的 Prefab 通过 Job 系统并行化主线程开销」的真实价值所在。
- 实际净影响：B1 大部分价值保留；放弃的部分收益换业务侧零改动。

### 行为不变性

- 改造前能 work 的所有业务代码，hotfix 后行为完全一致（cache hit 同步、cache miss 业务必然已经 await）
- `Release(handle)` + `ReleaseInstance(go)` 双计数语义未变（同步路径下 Track 仍在 `SetSucceeded` 之前调用）
- IGGProfiler `HC.Instantiate_<address>` sample 名 + 时间覆盖范围未变（同步路径下 Begin/End 同步成对包 sync Instantiate；异步路径下 Begin 在 Trigger，End 在 OnInstantiateCompleted）
- Late-completion guard（`Status == Disposed` 时 Destroy `aio.Result[0]`）继续生效——仅在异步路径才可能进入，cache hit 同步路径下 SetSucceeded 在 Start 调用栈内完成，业务此时还没拿到 handle，无法 Release

### Follow-up（不在本期）

- 业务侧后续可逐步排查 `InstantiateAsync` 后立即同步读 `.Result` 的代码点，统一改为 `await handle` / `handle.Completed +=` 模式。全部排查完成后可以考虑去掉这个同步降级，回归"cache hit 也异步"的完整 B1 收益。但要靠 D1 埋点（Phase 3）验证 cache hit 主线程切片确实有可观收益再说，否则不值得动业务代码。
- 当前 hotfix 已通过 doc comment + Start() 注释明确标注了这是「向后兼容路径」，未来调整时容易找到入口。

---

## 2026-05-20 - Phase 2 · B3 落地：`AsyncOperationBase.Dependencies` `List` → `array`（叶子 op 零 alloc）

### 背景

`docs/2026-05_TODO_MILESTONE.md` Phase 2 · B3：`AsyncOperationBase` 字段 `internal List<AsyncOperationBase> Dependencies = new List<AsyncOperationBase>();` 在每个 op 构造时都 alloc 一个 List 实例（含内部 T[] backing）。但叶子 op（含**大量** leaf bundle op）的 `Dependencies` 永远是空，这部分 alloc 完全是浪费。

### 变更

**Runtime — `Runtime/Operations/AsyncOperationBase.cs`**
- 字段类型：`internal List<AsyncOperationBase> Dependencies = new List<AsyncOperationBase>();` → `internal AsyncOperationBase[] Dependencies = Array.Empty<AsyncOperationBase>();`
- 新增 `internal int DependencyCount;`，表示数组里**有效**位置数（不是 array `.Length`，预留给未来若引入 pooled-array 共享 buffer 的扩展）。所有读访问点统一用 `DependencyCount`，禁止直接用 `Dependencies.Length`。
- `GetProgress()` 把 `Dependencies.Count` → `DependencyCount`，索引访问保持。
- 去掉冗余 `using System.Collections.Generic`。

**Runtime — `Runtime/Operations/ResourceManager.cs`**
- `StartOperation`：把 `op.Dependencies.Add(depOp)` 改成 `op.Dependencies = new AsyncOperationBase[depCount]; op.Dependencies[i] = depOp; op.DependencyCount = i + 1;`。`DependencyCount` **递增填充**（不是循环开始就赋满 `depCount`）——这一点关键：循环中途若某个 dep 同步 Failed 触发 `HandleDependencyFailure`，回滚遍历只会扫到已成功填入的 dep，与原 `List.Count` 语义完全对齐。
- `HandleDependencyFailure`：`parentOp.Dependencies.Count` → `parentOp.DependencyCount`，遍历内部不需要 null check（按上一条 invariant 保证 i ∈ [0, DependencyCount) 处必非 null）。

**Runtime — `Runtime/Operations/OperationCache.cs`**
- `Release`：`op.Dependencies.Count` → `op.DependencyCount`（2 处，含 LogVerbose 字符串）。

**Runtime — `Runtime/Providers/ProvideHandle.cs`**
- `GetDependencyResult<TDep>` / `GetDependencyBundle`：`_op.Dependencies.Count` → `_op.DependencyCount`（2 处边界检查）。

**Runtime — `Runtime/Providers/BundleAssetExtractor.cs`**
- `Provide` 警告 + `FindLoadedBundle`：`handle.Operation.Dependencies.Count` → `handle.Operation.DependencyCount`（2 处）。

**Runtime — `Runtime/Diagnostics/HyperContentDiagnostics.cs`**
- `CollectOperations`：`op.Dependencies?.Count ?? 0` → `op.DependencyCount`（新 array 字段总非 null）；遍历边界 `j < op.Dependencies.Count` → `j < op.DependencyCount`；去掉 `if (op.Dependencies == null) continue;`（不再需要，array 字段 default 是 `Array.Empty`）。

### 关键事实

- `Array.Empty<AsyncOperationBase>()` 是 BCL 提供的全局 singleton（实际是 `EmptyArray<T>.Value`），每个 `T` 类型共享一个实例 → **叶子 op 0 alloc**。
- 字段 default `Array.Empty<>()` 比 default null 更安全：所有遍历 `for (int i = 0; i < DependencyCount; i++) Dependencies[i]` 自动正确（`DependencyCount=0` 时直接跳过），不需要每个调用方都加 null check。
- 改造前每个 op `new List<AsyncOperationBase>()` ≈ List header 40 B + 默认 T[] capacity 0（lazy）≈ 40 B/op。100 个 leaf bundle op 节省 ≈ 4 KB。
- 改造后 leaf op 用 `Array.Empty` singleton 0 alloc；有依赖的 op 仍然 `new[depCount]` 一次（与原 List 内部 T[] 一致，但少 List header 的 40 B）。

### 行为不变性

- 索引访问 `Dependencies[i]` 完全等价（List 和 array 都是 `[]`）。
- `DependencyCount` 语义与原 `Count` 完全对齐（递增填充）：循环中途 `HandleDependencyFailure` 回滚的 dep 集合不变。
- `OperationCache.Release` 的递归 dep 释放遍历 `for (int i = 0; i < op.DependencyCount; i++) Release(op.Dependencies[i]);` 与原 `for (int i = 0; i < op.Dependencies.Count; i++) Release(op.Dependencies[i]);` 在所有路径上行为等价。
- Diagnostics dump 输出 `DependencyCount` 等价于原 `Dependencies?.Count ?? 0`。

### 验证（待做）

- `GC.GetAllocatedBytesForCurrentThread()` 夹取 100 次 `LoadAsync` 含至少 1 个有依赖的 bundle，对比改造前后净省字节。预期净省 ≈ 100 × 40 B = 4 KB（视依赖深度）。
- Unity Profiler Window 看 `List<AsyncOperationBase>..ctor` alloc 次数：改造后应为 0（仅在 `StartOperation` 内 `new[]` array，不再 List）。
- 行为回归：`HandleDependencyFailure` 路径单元测试（深 dep 树某层 dep 同步 Failed），验证已成功填入的 dep 全部回滚 Release，未填的位置不被遍历。

### Follow-up（不在本期）

- `DependencyCount` 与 `Dependencies.Length` 当前实现下完全等同（每个 op 都是按需 `new[depCount]`）。未来若引入 pooled-array 共享 buffer（多个 op 复用同一段 `AsyncOperationBase[]`），`DependencyCount` 才会真正区别于 `.Length`，所有现有调用点已经统一用 `DependencyCount`，迁移成本为 0。

---

## 2026-05-20 - Phase 2 · B1 落地：`InstantiateAsync` 切 Unity 原生 `AsyncInstantiateOperation<GameObject>`

### 背景

`docs/2026-05_TODO_MILESTONE.md` Phase 2 · B1：`HyperContentImpl.InstantiateAsync` 3 个实际实现重载内部都是同步 `UnityEngine.Object.Instantiate(...)`，对 deserialize 重的 Prefab（带 Material / Animator / ParticleSystem）会在主线程长卡。Unity 2022.2+ 提供 `Object.InstantiateAsync<T>` 用 Job 系统并行化主线程开销，项目当前 `2022.3.62f2` 可用。

### 关键设计决策（必读，写给后人）

设计期间识别了 6 个隐藏问题，每个都靠**显式约定 + 文件内注释**解决：

| # | 问题 | 决策 |
|---|---|---|
| 1 | `InstantiateOp` 要不要进 `OperationCache`？ | **不进**。每次 `InstantiateAsync` 都需要新 GameObject，缓存无意义；改成手动 `RefCount = 1`，handle Release 时 `RefCount--` 触发 `Dispose`。`InstantiateOp` 走 `_resourceManager.Release(iop)` → `_cache.Release(iop)`，借 `Release` 的统一路径处理 `RefCount` + `Dispose`。 |
| 2 | `InstantiateOp.LocationHash` 借 AssetOp 的还是用 0？ | **用 0**。借的话 `OperationCache.Release` 内 `_cache.Remove(LocationHash)` 会**误删 cached AssetOp**（下次 LoadAsync 同 addr 触发 cache miss 重新加载，bundle 被重复 LoadFromFileAsync）。`Location` 字段则借 AssetOp 的（HyperContentDiagnostics 看 Address 仍有意义）。 |
| 3 | （配合 #2 的防御）`OperationCache.Release` 的 `_cache.Remove` 万一被错误 op 触发怎么办？ | 同步加 **identity check**：`if (_cache.TryGetValue(op.LocationHash, out var cached) && ReferenceEquals(cached, op)) _cache.Remove(op.LocationHash);`。这一行让 #2 的"用 0"决策即使 `ComputeHash` 极小概率算出 0 也安全；同时也保护 `ResourceLocation.ComputeHash` 极端碰撞场景下不会误删 cached entry。 |
| 4 | Unity 原生 `InstantiateAsync` 没有 `instantiateInWorldSpace` 参数 | `worldSpace==true` 路径**降级同步 `Instantiate`**。这条路径 B1 不优化（主线程开销保持原样），但维持业务 API 完全等价；其他路径（含 worldSpace=false）走异步。已在 `InstantiateOperation.TriggerInstantiate` 注释明确，避免后人误以为这是漏写。 |
| 5 | 业务在 instance ready 前 `Release(handle)`（新增的并发场景） | `OnInstantiateCompleted` 检测 `Status==Disposed` → 主动 `UnityEngine.Object.Destroy(aio.Result[0])` 兜底。不 destroy 的话 GameObject 没有任何引用持有，会泄漏到下次场景切换。 |
| 6 | RefCount 双计数（`Release(handle)` + `ReleaseInstance(go)` 各 −1）的契约怎么保持？ | InstantiateOp **借** 1 个 AssetOp refcount（构造时来自 `StartLoadAssetOp<GameObject>` 的 `GetOrCreate/TryGetExisting` +1），`Dispose` 时 `_resourceManager.Release(_assetOp)` 还回去；Track 时另 +1（持有 instance），`ReleaseInstance` 在 `InstanceRegistry.Release` 内还回去。业务行为完全等价于改造前。 |

### 变更

**Runtime — `Runtime/Operations/InstantiateOperation.cs` (新增, ~230 行)**
- 包装 `AsyncInstantiateOperation<GameObject>`，3 种 Mode（`Parent` / `ParentWithWorldSpace` / `PositionRotationParent`）对应 3 个 `HyperContentImpl.InstantiateAsync` 重载。
- 静态工厂 `CreateParent` / `CreateParentWithWorldSpace` / `CreatePositionRotationParent`：私有 ctor + 字段赋值在 object initializer 内完成，避免 4 个长 ctor 重载导致的可读性退化。
- `Start()` 注册 `_assetOp.OnCompleted += OnAssetCompleted`（AsyncOperationBase.OnCompleted.add 已 succeeded 时同步触发——所以 cache-hit-succeeded 时 Start 内即可走完整条链）。
- `OnAssetCompleted` 状态守门：`Disposed/Failed` 早退；asset op 失败时 `SetFailed` 透传；成功调 `TriggerInstantiate`。
- `TriggerInstantiate`：worldSpace=true 同步降级；其他路径 `Object.InstantiateAsync` + IGGProfiler `HC.Instantiate_<addr>` 同步 Begin。
- `OnInstantiateCompleted`：IGGProfiler EndSample（**异步段也计入 sample 时间**，与改造前同步路径的 sample 语义对齐——直接对比就是净省）；`Status==Disposed` 走 destroy guard；result 空 → SetFailed；否则 `Track(instance, _assetOp)` + `SetSucceeded`。
- `GetProgress` override：asset 0..0.9 + instantiate 0.9..1.0；不轮询 `aio.progress`（要每帧 ticker，工程成本高 ROI 低；业务要细粒度自己看 Completed）。
- `Execute(...)` throw `NotSupportedException`：InstantiateOp 不走 provider DAG，调到这里说明被 OperationCache 误当作普通 op，是 bug。
- `Dispose`：`_resourceManager.Release(_assetOp)` 还借的 refcount；**不 Destroy `Instance`**（lifetime 归 InstanceRegistry，`ReleaseInstance(go)` 调用方负责）。

**Runtime — `Runtime/Operations/HyperContentImpl.cs`**
- 抽取 `private AssetOperation<T> StartLoadAssetOp<T>(string, LoadNetworkOptions)` helper：把入口校验（address 空 / QueryOnly 拒绝 / catalog miss）+ `IGGProfiler.HC.Load_<addr>` 包裹 + `StartLoad<T>` 调用整段抽出。`LoadAsync<T>` 和 3 个 `InstantiateAsync` 实际实现共用这个 helper，避免重复 IGGProfiler `#if ENABLE_PROFILERLOG` 块。
- `LoadAsync<T>` 简化为 helper 调用 + `AllocateHandleId` + new handle。
- 3 个 `InstantiateAsync` 实际实现重载（`(string, Transform, LoadNetworkOptions)` / `(..., bool, LoadNetworkOptions)` / `(string, Vector3, Quaternion, Transform, LoadNetworkOptions)`）改造：
  - `StartLoadAssetOp<GameObject>` 拿 assetOp
  - `InstantiateOperation.CreateXxx(assetOp, ..., _resourceManager, _instanceRegistry)` 包装
  - 走 `StartInstantiateOperation(iop)` 统一收尾：`RefCount = 1` + `Start()` + `AllocateHandleId(iop)` + new handle (`() => iop.Instance` getter)
- **删除** 5/20 直接加在 lambda 里的 `IGGProfiler.BeginSample($"HC.Instantiate_{pAddress}")` / `EndSample` 同步埋点——sample 已移到 `InstantiateOperation` 内部（覆盖同步降级路径和异步路径两条），sample 名 `HC.Instantiate_<addr>` 与改造前 100% 一致，**改造前后数据可直接对比**（这是 5/20 milestone 文档里 B1 改造的 follow-up 硬性要求）。

**Runtime — `Runtime/Operations/OperationCache.cs`**
- `Release(op)`：`_cache.Remove(op.LocationHash)` 前加 identity check（见上方决策表 #3）。注释解释了 InstantiateOp + ComputeHash 碰撞两个触发场景，避免后人觉得这一行是 "防御性过度编程" 而删掉。

### 关键事实

- Unity 2022.2+ `Object.InstantiateAsync<T>(T, ...)` 返回 `AsyncInstantiateOperation<T>`，`completed` 事件触发时 `Result` 是 `T[]`（批量 API，即使 count=1 也是数组）。
- `AsyncInstantiateOperation.completed` 回调在主线程触发（Unity callback dispatch），所以 InstantiateOp 的 `SetSucceeded` / `Track` / `Status` 切换都在主线程，与原同步实现的线程模型一致。
- `AsyncOperationBase.OnCompleted` 的 add 实现：op 已 terminal 时 lambda **同步立即触发**。所以 `_assetOp.OnCompleted += OnAssetCompleted` 在 cache-hit-succeeded 场景下会立即同步进入 `OnAssetCompleted` → `TriggerInstantiate` → 异步 `InstantiateAsync` 启动；`Start()` 返回时业务的 ContentHandle 已经在异步等 instance。

### 行为不变性

- 业务 API 签名完全不变（3 个 InstantiateAsync 实际实现 + 3 个 wrapper + facade 层 6 个 `HyperContent.InstantiateAsync` 重载）。
- `ContentHandle<GameObject>.IsSuccess` / `IsDone` / `Result` 语义按 milestone B1 §1 设计调整：`Result` 在 succeed 前为 null（即异步 Instantiate 还在执行时业务读 `.Result` 返回 null，与改造前 asset 加载中的行为一致）；business 的 `Completed += handler` 注册的回调只在 `SetSucceeded` 时触发，此时 `Instance` 已 alive、Track 已完成——业务回调时序未变。
- `Release(handle)` + `ReleaseInstance(go)` 双计数语义未变（见决策表 #6）。
- worldSpace=true 路径行为完全等价（同步 Instantiate，未变）。

### 验证（待做，需真机数据）

- 选 3 个代表性 Prefab（轻 UI / 中等英雄 / 重场景），开启 `ENABLE_PROFILERLOG`，跑同一组 `InstantiateAsync` 各 3 次，对比改造前后 `HC.Instantiate_<address>` 主线程耗时
- 关键守门：净省主线程帧时间需 > 5ms 才认收益；简单 prefab 若净省 < 2ms 则**说明该 prefab 不该走异步路径**（业务侧 evaluate 是否值得加 `Instantiate` vs `InstantiateAsync` 分流，本期不做）
- 行为回归：业务在 instance 还没 ready 时立刻 `Release(handle)` 的场景，验证 `aio.completed` 触发时 Destroy guard 生效，instance 不泄漏到下次场景

### Follow-up（不在本期）

- B1 改造让 InstantiateAsync 变成"真异步"，业务侧如果有"加载完立刻读 handle.Result"的同步代码需要适配为 await handle 或 handle.Completed。已通过 `HyperContent.InstantiateAsync` facade 层向上传播，业务代码改动量预计 0—1 处（之前 OnCompleted 模型本来就要 await）；如果业务真有 ".Result 同步读"的死代码，B1 PR review 时按需修。
- `IInstancePool` (Phase 3 C2) 落地时，`InstantiateOperation` 可以 fast-path 直接从 pool 取 instance 跳过 `Object.InstantiateAsync` 本体；本期不动。

---

## 2026-05-20 - Phase 2 基线采集前置：B1 同步 Instantiate 段加 IGGProfiler 埋点

> 已被同日 B1 落地条目取代——sample 名 `HC.Instantiate_<addr>` 沿用，但实施位置从 `HyperContentImpl.InstantiateAsync` lambda 内迁移到 `InstantiateOperation.TriggerInstantiate` / `OnInstantiateCompleted`，关宏零成本规则保持。本条目保留以记录"先埋点后改造"的方法论。

### 背景

`docs/2026-05_TODO_MILESTONE.md` Phase 2 改造（B1 切 `Object.InstantiateAsync` / B2 catalog 后台化 / B3 `Dependencies` List → array）需要可对比的基线数据，否则"收益数值"全是猜测。本次盘点现有埋点覆盖情况，仅对**唯一缺口** B1 补齐基线，B2 / B3 复用现有埋点 / 走 `GC.GetAllocatedBytesForCurrentThread()`，不重复造轮子。

### 现状盘点

| Phase 2 项 | 现有埋点 | 决策 |
|---|---|---|
| **B1** `InstantiateAsync` 切 Unity 原生 API | `HC.Load_<addr>` 覆盖到加载完成；后面 lambda 里**同步 `Object.Instantiate`** 这段（B1 真正要优化的部分）**未埋** | ✅ 本次新增 `HC.Instantiate_<addr>` |
| **B2** Catalog 后台线程化 | `LocalContentCatalog.Initialize` 已按 5 段（IO / Decompress / Deserialize / BuildLookup / Total）全埋（2026-05-18 P0-6 落地） | ❌ 现有数据已够用，不改 |
| **B3** `Dependencies` List → array | 优化对象是 GC alloc（每个 leaf op 省 1 个 `List<>` ≈ 40–60 B），耗时影响低于 IGGProfiler 噪声 | ❌ 走 `GC.GetAllocatedBytesForCurrentThread()` 夹取（方案对齐 A3 follow-up 测试） |

### 变更

**Runtime — `Runtime/Operations/HyperContentImpl.cs`**

3 个 `InstantiateAsync` 实际实现重载里（`(string, Transform, LoadNetworkOptions)` / `(string, Transform, bool, LoadNetworkOptions)` / `(string, Vector3, Quaternion, Transform, LoadNetworkOptions)`），在 `op.OnCompleted` lambda 内部、`UnityEngine.Object.Instantiate(...)` 同步调用前后加：

```csharp
IGGProfiler.BeginSample($"HC.Instantiate_{pAddress}");
instanceResult = UnityEngine.Object.Instantiate(assetOp.Result, ...);
IGGProfiler.EndSample($"HC.Instantiate_{pAddress}");
```

`InstantiateAsync(string, Transform)` / `(string, Transform, bool)` / `(string, Vector3, Quaternion, Transform)` 3 个 wrapper 重载未改动（它们只是转调实际实现，埋点跟着实际实现走）。

### 关键事实（为什么这样写）

- `IGGProfiler.BeginSample` / `EndSample` 都标了 `[System.Diagnostics.Conditional("ENABLE_PROFILERLOG")]`。C# 规范：`[Conditional]` 方法在条件 symbol 未定义时，**调用点连同所有参数表达式（含 `$"..."` 字符串插值）整段从 IL 擦除**。
- 因此 hot path 字符串**直接内联到方法实参里**即可——不需要 `string sampleName = $"..."` 局部变量声明，也不需要 `#if ENABLE_PROFILERLOG` 包裹。规则总结见 2026-05-19 A2 条目。
- **不会出现 sampleName 冲突**：`IGGProfiler._allNodes` 是 `Dictionary<string, SampleNode>`，同名同时 Begin 会 LogError 跳过。但 `Object.Instantiate` 是**同步阻塞**调用，`op.OnCompleted` 由 `AsyncOperationBase.InvokeCompleted()` 在主线程串行触发，单次回调内 Begin → Instantiate → End 同步成对完成，下次回调（哪怕是同一个 op 上注册的另一个 lambda）来时 dict 已清空——无并发冲突。
- 与 `LoadAsync` 处的差异：`LoadAsync` 那段 `op.OnCompleted += _ => EndSample(sampleName)` 必须用 `#if ENABLE_PROFILERLOG` 包，因为 lambda 闭包注册不是 `[Conditional]` 调用，编译器不会擦除闭包对象 + 委托对象的 alloc；而本次 `Instantiate` 段没有跨 lambda、没有局部变量，纯靠 `[Conditional]` 自动擦除就够。

### 行为不变性

- 关 `ENABLE_PROFILERLOG`：3 处新增代码整段从 IL 消失，热路径行为完全等价。
- 开 `ENABLE_PROFILERLOG`：lambda 内多了同步 `BeginSample` / `EndSample` 调用，外加 IGGProfiler 内部一次 dict 写 + 一次写后删；测量本身的开销远小于 `Object.Instantiate` 本体，不影响数据准确性。

### 文档同步

- `docs/2026-05_TODO_MILESTONE.md`：B1 / B2 / B3 各新增「基线埋点 / 基线采集」小节，写明 sample 名 + 采集步骤 + 关键守门条件；§6 「验证流程」补齐"现有可直接采数的 sample"清单；§8 更新历史加 2026-05-20 条目。

### Follow-up（本期不做）

- B1 改造落地时，新的 `InstantiateOperation`（包 `AsyncInstantiateOperation<GameObject>`）需要**用同名 `HC.Instantiate_<addr>` 包整段**（从触发 `Object.InstantiateAsync` 到 instance 拿到），否则改造前后对比的不是同一段语义——这条要在 B1 PR 的 review 时硬性检查。
- B2 改造落地时，主线程实际等待时间需要新增 `HC.Catalog.Init.MainThreadWait` 类埋点（包"业务侧触发 Init Task → await 返回"整段），五段基线本身（IO 等）改造后**仍然有效**（IGGProfiler `Stopwatch` 跨线程安全；唯一注意点是子线程 `Time.frameCount` 取不到，IGGProfiler 内部需要按 milestone B2 §3.1 要求兜底为 -1）。

---

## 2026-05-19 - OperationCache cache-hit fast path 去 lambda 闭包 (Phase 1 · A3)

### 背景
`docs/2026-05_TODO_MILESTONE.md` A3 项：`ResourceManager.LoadAsync<T>` / `LoadDependency` 调用 `OperationCache.GetOrCreate(location, () => new AssetOperation<T>(location))` 时，因为 lambda 体内引用了泛型方法参数 `T`（或 `LoadDependency` 里捕获的 `location`），编译器无法把它折叠成 static 委托，**每次调用都会 alloc**：
- closure object（捕获 `location`，约 24–32 B）
- `Func<AsyncOperationBase>` delegate（绑定到新 closure，约 40–48 B）

合计 ≈ 60–80 B/次。cache hit 路径 `factory()` 根本不会被调用，这两个 alloc 完全是浪费。业务高频 LoadAsync 场景下（战斗内召唤、UI 大窗切换 prefetch）累积可观。

### 变更

**Runtime — `Runtime/Operations/OperationCache.cs`**
- 新增 `internal bool TryGetExisting(ResourceLocation location, out AsyncOperationBase existing)`：cache-hit 快路径。命中时执行与 `GetOrCreate` 完全一致的副作用（`AssertCacheHitIdentityMatch` + `RefCount++` + `LogVerbose`）后返回 true；miss 时返回 false 让调用方走原 slow path。
- `RefCount++` 与 `AssertCacheHitIdentityMatch` 仍封装在 `OperationCache` 内，不下放到 `ResourceManager`，保留封装边界。
- `GetOrCreate` 本体未改动，hot path 调用方两段式调用即可拿到零 alloc 收益。

**Runtime — `Runtime/Operations/ResourceManager.cs`**
- `LoadAsync<T>`：在 `GetOrCreate` 之前先 `if (_cache.TryGetExisting(location, out var hit)) return (AssetOperation<T>)hit;`。类型不匹配时**保持强转抛 `InvalidCastException`**，行为与原 slow path 一致，便于发现 hash 碰撞或类型错配 bug。
- `LoadDependency`：同样的 fast path 改造（dep 命中场景在嵌套 bundle 复用时占比尤其高）。
- 删除 slow path 里冗余的 `if (op.Status != OperationStatus.None) return op;` 判断：`GetOrCreate` 现在只会在 cache miss 时被调用，新建 op 必然 `Status == None`，旧分支永远走不到。
- `LoadSceneAsync` **未改动**：场景切换是冷路径，且 lambda 同时捕获 `location + mode` 双变量，改造复杂度高、ROI 低（详见 milestone 文档 A3 章节方案 B 否决说明）。

### 关键事实
- C# 闭包优化规则：lambda 体内引用泛型方法参数（如 `T`） 或 局部参数（如 `location`） 时，编译器必须为每次调用生成新的 closure 实例 + 新的 delegate 实例；只有不捕获任何外部上下文的 lambda 才能被 cache 成 static 委托。
- 因此 `_cache.GetOrCreate(location, () => new AssetOperation<T>(location))` 即便 cache hit 也无法避免 alloc——闭包必须先被构造出来才能传参，至于内部是否调用是 OperationCache 的运行时决定。
- 解法只有两条：(a) 调用方提前判断 cache 命中后跳过 lambda（本期方案 C，cache hit 0 alloc）；(b) 把 factory 签名改成 `Func<ResourceLocation, AsyncOperationBase>` 并用嵌套 generic helper class 缓存 static 委托（方案 D，cache miss 也 0 alloc，本期不做，cache miss 是冷路径 ROI 低）。

### 行为不变性
- cache hit 副作用完全等价：identity 断言、`RefCount++`、verbose 日志输出顺序与原 GetOrCreate 一致。
- cache miss 路径未改：依旧 `factory()` → 设置 `RefCount=1` / `LocationHash` → 入字典 → `StartOperation`。
- `OperationCache.TryGet(int locationHash, out AsyncOperationBase op)` 这个旧的 diagnostics 用法保持不变（不读 `ResourceLocation`、不做 RefCount++、不做断言），与新 `TryGetExisting` 各司其职。

### 验证（待做）
- 新增单元测试 `Tests/Runtime/OperationCacheFastPathTests.cs`：用 `GC.GetAllocatedBytesForCurrentThread()` 在连续 10 次 cache-hit `LoadAsync` 前后断言增量为 0（Profiler 看肉眼不够硬，单元测试做硬断言）。需要先在 HyperContent 下铺设 Unity Test Framework asmdef，待业务侧确认后单独提交。
- IGGProfiler 跑同 prefab 100 次 `LoadAsync`：预期每次省约 60–80 B（closure + delegate），100 次省 6–8 KB。
- LoadDependency 同样验证 bundle dep 命中 fast path 时 0 alloc。

### Follow-up（不在本期）
- **方案 D**：把 `GetOrCreate` 的 factory 签名改为 `Func<ResourceLocation, AsyncOperationBase>`，配合 `private static class OpFactory<T> { public static readonly Func<ResourceLocation, AsyncOperationBase> Create = static loc => new AssetOperation<T>(loc); }` 缓存 static 委托，让 cache miss 也 0 alloc。每个 `T` 只 alloc 1 个 Func 实例并复用。

---

## 2026-05-19 - LoadAsync hot path Profiler 埋点零 GC 改造 (Phase 1 · A2)

### 背景
`docs/2026-05_TODO_MILESTONE.md` A2 项：业务每次 `LoadAsync` / `Extract` 都会产生 1 个 `string` + 1 个 `Stopwatch` + 1 个 lambda 闭包的 GC alloc。原因是 `IGGProfiler.BeginSample` 虽然标了 `[Conditional("ENABLE_PROFILERLOG")]`，但 hot path 上的 `string sampleName = $"..."` 是局部变量赋值、`var sw = Stopwatch.StartNew()` 是 class 分配 —— 这两类语句不在 `[Conditional]` 的擦除范围内，关宏后仍然会被编译进 release 产物。

### 变更

**Runtime — `Runtime/Operations/HyperContentImpl.cs`**
- `LoadAsync<T>` 里 `string sampleName = $"HC.Load_{pAddress}"` + `BeginSample(sampleName)` + `op.OnCompleted += _ => EndSample(sampleName)` 整段用 `#if ENABLE_PROFILERLOG` / `#endif` 包起来。原因：`op.OnCompleted += lambda` 这个委托注册不是 `[Conditional]` 调用，编译器不会擦除；lambda 即便 body 在关宏时变成 no-op，**闭包对象 + 委托对象的 alloc 仍然会发生**。所以这一处不能仅靠 `[Conditional]` 自动剥离，必须条件编译。

**Runtime — `Runtime/Providers/BundleAssetExtractor.cs`**
- 删除 `var sw = Stopwatch.StartNew();` / `sw.Stop();` 以及对应 `using System.Diagnostics;`。理由：`IGGProfiler.BeginSample` 内部已 `new Stopwatch()` + `Timer.Start()`，`EndSample` 自动计算 `ElapsedTicks / TicksPerMillisecond`（带小数）；外层再 new 一份 Stopwatch 是纯粹重复 alloc。
- `IGGProfiler.BeginSample($"HC.Extract_{assetPath}")` / `EndSample(...)` 保持字符串直接内联：因为 `[Conditional]` 会在关宏时擦除"调用点连同所有参数表达式"，包括 `$"..."` 的字符串拼接。
- `HCLogger.LogVerbose($"[HC.ExtractAsset.Done] ...elapsed={sw.ElapsedMilliseconds}ms ...")` 去掉 `elapsed=` 字段，由 `IGGProfiler.OutputResults` 负责打印耗时（更精确，且关宏时跟随 `[Conditional]` 一同消失）。

**Runtime — `Runtime/Providers/BundleFileProvider.cs`**
- 复核：当前已是字符串直接内联，无 `Stopwatch`、无局部 `sampleName` 变量；`bundle =>` lambda 闭包是功能性代码（`handle.Complete`/`Fail` 必须存在），其捕获的 `internalId` / `handle` / `filePath` 也是功能性使用。`[Conditional]` 已能在关宏时完全剥离 Profiler 部分。**未改动**。

### 关键事实

- `IGGProfiler.BeginSample` / `EndSample` / `Clear` 在 `Assets/Scripts/Main/Tools/Debug/IGGProfiler.cs` 都标了 `[System.Diagnostics.Conditional("ENABLE_PROFILERLOG")]`。C# 语言规范规定：`[Conditional]` 方法在条件 symbol 未定义时，**调用点连同所有参数表达式（含字符串插值）整段从 IL 中擦除**。
- 所以 hot path 上能内联到方法实参里的字符串，**不需要** `#if ENABLE_PROFILERLOG` —— 关宏即零成本。
- 但 `[Conditional]` **不擦除**：(a) `string x = $"..."` 这样的局部变量声明 + 赋值；(b) 包含 `[Conditional]` 调用的 lambda / 委托表达式；(c) 类似 `var sw = Stopwatch.StartNew()` 的非 `[Conditional]` 方法调用。这些场景需要手动 `#if ENABLE_PROFILERLOG`。
- 命名规则总结：**Begin/End 同作用域** → 直接内联；**Begin/End 跨 lambda 或需 sampleName 局部变量** → `#if ENABLE_PROFILERLOG`。

### 验证（待做）

- release 包（关 `ENABLE_PROFILERLOG`）跑 100 次 `LoadAsync`，对比 `Profiler.GC.Alloc`：预期每次省 1 个 `string`（~64B） + 1 个 `Stopwatch`（~32B） + 1 个 lambda 闭包（~24B）= 约 120 B / 次。
- 业务侧功能行为不变（lambda 在 dev 包仍正常注册到 `OnCompleted`，release 包下整个 lambda 注册语句被 `#if` 剥离）。

---

## 2026-05-18 - Catalog Initialize 接入 IGGProfiler + 三格式实测对比 (P0-6 验证)

### 背景
P0-6 三轨序列化落地后，需要拿到三种 catalog 格式在 `LocalContentCatalog.Initialize` 的各阶段实测耗时，验证设计预期、为后续优化指明方向。本次在 `Initialize` 主流程上接入项目自有的 `IGGProfiler`，并保证关 `ENABLE_PROFILERLOG` 时**零编译产物 / 零字符串分配**。

### 变更

**Runtime — `Runtime/Catalog/LocalContentCatalog.cs`**
- 在 `Initialize(string source, CatalogSerializationFormat format)` 主流程按 5 个 sample 段标记：`HC.Catalog.Init.Total[{format}]` / `IO` / `Decompress` / `Deserialize` / `BuildLookup`，sample 名带 `[{format}]` 后缀便于在 Profiler 视图按格式横向对比。
- **关宏零成本**：所有 sample key 的 `string` 局部声明 + `$"..."` 拼接 + `IGGProfiler.BeginSample` / `EndSample` 调用全部包在 `#if ENABLE_PROFILERLOG` / `#endif` 内（不仅是调用、连变量声明都包进去）。`IGGProfiler` 方法自带 `[Conditional("ENABLE_PROFILERLOG")]`，但 `[Conditional]` 关宏时只删除"调用"、不删除"参数表达式里出现的标识符"，所以变量声明必须同步条件编译，否则会因"未声明"编译失败。
- 移除了早期临时排查用的 `System.Diagnostics.Stopwatch` + 无条件 `Debug.Log` 汇总。Profile 现在仅靠 `IGGProfiler`，开宏时去 Profiler 看，关宏时 0 开销。

### 实测数据（编辑器基准 · PC SSD · 多次均值 · catalog ~8.7 MB JSON 体量）

| Format | IO | Decompress | Deserialize | BuildLookup | Other (un-sampled) | Total | 文件大小 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Json | 60 ms | — | 138 ms | 74 ms | ~340 ms | **612 ms** | 8740 KB |
| Binary | 27 ms | — | 36 ms | 74 ms | ~344 ms | **481 ms** | 3918 KB (-55%) |
| BinaryGzip | 6 ms | 28 ms | 32 ms | 74 ms | ~350 ms | **490 ms** | 1470 KB (-83%) |

### 关键结论

- **Deserialize 是 Json 的主要瓶颈**：138 ms → Binary 36 ms（-74%），是切换格式带来的最大、最确定收益。
- **Binary 综合最优（本地命中）**：Total 481 ms 最快、无解压抖动、文件比 Json 已经省 55%；适合启动性能优先场景。
- **BinaryGzip 文件最小**：1.44 MB（比 Json 省 83%、比 Binary 再省 62%），Total 仅比 Binary 慢 ~9 ms。本地命中场景下解压代价（~28 ms）吃掉了 IO 节省（21 ms），到移动端弱 CPU + 慢 IO + 远端拉取场景结论会反转，BinaryGzip 优势放大。
- **BuildLookup 74 ms 跨格式完全一致**：合理，这一步是在内存 schema 上建索引字典，与序列化方式无关。
- **未被 sample 的 "Other" ~345 ms 跨格式恒定，占 Total 70%**：是 `CatalogLocator` 路径解析 / hash 校验 / Provider 注册 / HCLogger 同步 IO / 首次 JIT 等。下一波想再压 Initialize 总时间，**真正的大头不在序列化里**。

### 推荐选型

| 场景 | 推荐 |
|---|---|
| 启动性能优先（热启 / 本地缓存命中） | **Binary** |
| 包体 / 下载流量优先（首启 / PAD / 弱网） | **BinaryGzip** |
| 调试 / 排查 | Json（人眼可读临时切回） |

### 后续待办（与本次解耦，未落地）
- **真机复测**：当前数据来自编辑器 + PC SSD，到中低端 Android 真机需重测一次以确认结论，预期 IO 差距进一步放大、`BinaryGzip` 综合优势更明显。
- **拆解那 ~345 ms 的 "Other"**：在 `CatalogLocator` / hash 校验 / Provider 注册阶段补 IGGProfiler 段位，是下一个数量级优化点。
- **`BuildLookup` 74 ms 微调**：预设 `Dictionary` 容量、跳过冗余字段，估计可再砍 10–20 ms。

---

## 2026-05-15 - Catalog 三轨序列化（Json / Binary / BinaryGzip）+ settings.json 驱动 (P0-6)

### 背景
Catalog 反序列化（`JsonUtility.FromJson` over StreamingAssets JSON）在中端 Android 上对 ~1MB catalog 大约耗时 100–300ms，是冷启动 init 阶段的主要单点。本次引入紧凑二进制 + 可选 GZip 压缩两条新路径，目标是"解析快 + 文件小"，同时保留 JSON 通路便于排查 / diff。

设计原则：**写入端按 `BuildConfig.catalogFormat` 选格式；读取端按 `RuntimeSettings.catalogFormat` 选格式。两边各自直接进入对应分支，零 magic 探测、零 fallback。**`settings.json` 在 APK 出包时固化、不通过 hot-update 更新，确保跨格式必须出新 APK。

### 变更

**Runtime（仅"读"，HyperContent.Runtime 程序集）**
- 新增 `Runtime/Catalog/CatalogSerializationFormat.cs`：枚举 `Json = 0` / `Binary = 1` / `BinaryGzip = 2`，开放扩展（注释里预留 `BinaryLz4` / `BinaryZstd`）。
- 新增 `Runtime/Catalog/CatalogBinaryReader.cs`：HCB1（HyperContent Binary v1）反序列化，`Read(byte[])` 完整解析 + `PeekCatalogHash(byte[])` 仅读到 hash 字段（hot-update 比对场景）。处理"未压缩"字节，与压缩算法解耦。
- `Runtime/Core/RuntimeSettings.cs`：增加 `int catalogFormat` 字段（用 `int` 而非 enum 以确保 JsonUtility 跨 Unity 版本稳定）。
- `Runtime/Core/HyperContentPaths.cs`：新增 `LoadBytes(string)` 方法（同步），与现有 `LoadText(string)` 对称，Android `StreamingAssets`（`jar:file://`）走 `UnityWebRequest`。
- `Runtime/Catalog/LocalContentCatalog.cs`：
  - 新增 `Initialize(string source, CatalogSerializationFormat format)` 重载，按 format 显式分发。BinaryGzip 在 dispatcher 层用 `System.IO.Compression.GZipStream` 解压后再交给 `CatalogBinaryReader`。
  - 旧 `Initialize(string source)`（`ICatalog` 接口实现）保留，默认走 Json，向后兼容。
  - `GetCatalogHashFromJson` 改名为 `GetCatalogHashFromBytes(byte[], format)`，符合"调用方显式传 format"原则。
- `Runtime/Operations/HyperContent.cs`：`InitializeBundleMode` 把 `resolution.settings?.catalogFormat` 透传给 `catalog.Initialize`。

**Editor（仅"写"，HyperContent.Editor 程序集）**
- 新增 `Editor/Build/CatalogBinaryWriter.cs`：HCB1 序列化，字段顺序与 `CatalogBinaryReader.Read` 严格对称。
- `Editor/Build/BuildContext.cs` 的 `BuildConfig` 增加 `catalogFormat` 字段（默认 `Json`，与现有发布行为一致）。
- `Editor/Build/CatalogGenerator.cs`：
  - `Serialize(schema, format)` / `Deserialize(bytes, format)` 改签名按 format 分发。
  - 内置 `GzipCompress` / `GzipDecompress`（BCL `System.IO.Compression.GZipStream`，零第三方依赖）。
  - `GenerateCatalog` 用 `context.Config.catalogFormat`，hash 两轮序列化都按同一 format。
- `Editor/Build/DefaultBuildExecutor.cs` / `UpdateBuildExecutor.cs`：写 `settings.json` 时填入 `catalogFormat = (int)config.catalogFormat`；UpdateBuildExecutor 的混合 catalog 也按同一 format。
- `Editor/HyperContentBuildWindow.cs`：Settings 标签 "Build Remote Catalog" 下方新增 "Catalog Format" 下拉（自然展示三个枚举值，未来扩 LZ4 等只需加枚举值，UI 自动跟上）+ 切换后 `EditorUtility.DisplayDialog` 询问"Build (Full) Now / Later"，避免出现 catalog 与 settings.json 中间过渡态；Info HelpBox 提示跨格式必须出新 APK。

**文档**
- `docs/CATALOG_SCHEMA.md`：新增 § 1.1 "Serialization Formats"，含三格式对比表、配置流向 mermaid、错配矩阵、HCB1 二进制布局表、可扩展性说明。
- `docs/TODO.md`：在顶部追加 P0–P2 全量优化路线图，P0-6 标注本次完成。

### 行为约束
- `settings.catalogFormat` 与 catalog 实际格式必须一致；不一致 → `CATALOG_INVALID_FORMAT`。
- Hot-update 不能跨格式：APK 决定后，下发的 catalog 必须与 APK 内置 `settings.catalogFormat` 一致。
- 切换 catalog format 后必须 Full Build（构建窗口已强制弹窗提示）。

### 性能预期 → 已实测
> 编辑器基准已落地，见 `2026-05-18` 条目实测数据表。简述：Binary Total -21% / 文件 -55%；BinaryGzip Total -20% / 文件 -83%；Deserialize 阶段 Json → Binary 直接 -74%。真机数据待补。

### 不破坏 / 不影响
- PAD（Google Play Asset Delivery）通路完全不动。
- `CatalogLocator`、Provider 系列、`*.hash` hot-update 流程、`ICatalog` 接口均无破坏性改动。

---

## 2026-04-23 - Android bundle 扩展名 + SBP 任务对齐 + 构建可观测性 (Owner1 构建侧)

### 背景
与 Addressables 对齐的 SBP 管线在输出 **`.bundle` 磁盘文件名**、共享 **`monoscripts` / `unitybuiltinassets`** bundle 后，Android APK 可对匹配 `aaptOptions.noCompress` 的条目避免 deflate，显著改善 `LoadFromFileAsync` / 大 Prefab 加载表现（MuseumUI 等场景）。Catalog 中逻辑 `bundleName` 仍无扩展；磁盘名由构建与 runtime 路径解析统一为 `bundleName + ".bundle"`。

### 变更（构建 / 编辑器）

**DefaultBuildExecutor.cs**
- `CreateBuildTaskListForUpdate()` 已包含与 Addressables 同类的扩展任务：`StripUnusedSpriteSources`、`CreateBuiltInBundle`、`CreateMonoScriptBundle`、`UpdateBundleObjectLayout`（顺序与职责见 `docs/BUILD_LIFECYCLE.md`）。
- `StoreActualBundleNames`：默认仅一行汇总日志；仅在映射失败、SBP 多出未映射产物或与逻辑 bundle 数量不一致时输出差集警告。
- `RebuildBundleDependenciesFromSbpResults`：移除逐 bundle 依赖的 `Debug.Log`，保留汇总行与未知依赖的 `LogWarning`。

**BuildConfig / 构建窗口**
- `stripUnityVersionFromBundleHeaders`（默认 `false`，与 Addressables 一致）写入 `HyperContentBundleBuildParameters` 的 `ContentBuildFlags.StripUnityVersion`；补充 XML 说明。
- `HyperContentBuildWindow`：**Advanced / Experimental** 折叠内提供「Strip Unity version from bundle headers」开关，持久化到 `ProjectSettings/HyperContentBuildConfig.json`。

**文档**
- `BUILD_LIFECYCLE.md`、`ADDRESSABLE_BUILD_FLOW.md`：补充上述 SBP 任务列表与 HyperContent / Addressables 对齐说明；配置表增加 `stripUnityVersionFromBundleHeaders`。
- `BUILD_SYSTEM.md`：构建窗口说明中增加 Advanced 开关描述。

---

## 2026-03-30 - 完整 SBP 迁移 + Bundle 依赖精确化 (Owner1)

### 背景
原构建管线使用 `CompatibilityBuildPipeline.BuildAssetBundles`（SBP 兼容模式），bundle 间依赖通过 `AssetDatabase.GetDependencies` 预估，无法保证完全准确——例如 Prefab → Material → Shader 的间接依赖关系可能缺失（shader bundle 无入边），导致运行时渲染异常（粉红 shader）或提前卸载被依赖 bundle。

### 变更

**DefaultBuildExecutor.cs**
- 从 `CompatibilityBuildPipeline.BuildAssetBundles` 切换为完整 SBP `ContentPipeline.BuildAssetBundles`
- 新增自定义 task 列表（`CreateBuildTaskListForUpdate()`），包含 `CalculateAssetDependencyData` → `GenerateBundlePacking` → `WriteSerializedFiles` → `ArchiveAndCompressBundles` 等，与 Addressables 同级别 SBP 集成
- 构建后从 `IBundleBuildResults.BundleInfos` 提取对象级精确 bundle 依赖，覆写 `BuildContext.BundleDependencies`（`RebuildBundleDependenciesFromSbpResults()`）
- 新增 `GetSbpCompression()` 将 `BundleCompressionType` 映射为 SBP `BuildCompression`
- `StoreActualBundleNames` 适配 `IBundleBuildResults`（原为 `CompatibilityAssetBundleManifest`）

**UpdateBuildExecutor.cs**
- `ExecuteFullContextBuild` 同步切换为 `ContentPipeline.BuildAssetBundles`，复用 `DefaultBuildExecutor.CreateBuildTaskListForUpdate()` 和 `RebuildBundleDependenciesFromSbpResults()`
- `CopyUpdateBundlesToServerData`：拷贝 update bundle 到 ServerData 后，从 StreamingAssets 删除（含 `.meta` 文件），确保 update bundle 仅存在于远端

**DependencyAnalyzer.cs**
- `BuildBundleDependencies` 改为使用递归 `AssetDatabase.GetDependencies(path, true)`
- 注释明确标注此方法为**预构建估算**（用于验证/编辑器预览），实际构建时由 SBP 结果覆写

**文档更新**
- `BUILD_SYSTEM.md`：更新架构图、打包执行器描述、依赖分析说明、详细步骤
- `CONTENT_UPDATE_BUILD_FLOW.md`：SBP Integration 章节、Phase B3 流程图、比较表
- `OWNERS.md`：Build Pipeline Steps、Update Build 步骤
- `HOT_UPDATE_TODO.md`：O1-1、O1-7 描述、设计规则、Notes
- `ADDRESSABLE_BUILD_FLOW.md`：HyperContent 并行管线描述
- `TODO.md`：SBP 迁移里程碑标记完成
- `ARCHITECTURE.md`：Layer 1 SBP Build Cache 描述
- `CHANGELOG.md`：本条记录

---

## 2026-02-25 - Settings Flow 实现 (Owner3)

### 背景
实现 settings.json + catalog 加载机制，参考 Unity Addressables 设计。提供稳定的 settings.json 入口、Hash 比较的 catalog 更新检测、Android StreamingAssets 支持。

### 变更（Owner3）

**新增**
- `Runtime/Core/RuntimeSettings.cs`：settings.json 反序列化结构
- `Runtime/Core/HyperContentPaths.cs`：路径常量、Android StreamingAssets 支持（LoadText、FileExistsOrIsStreamingAssets）
- `Runtime/Catalog/CatalogLocator.cs`：Catalog 发现逻辑（settings.json + hash 比较 + 下载 + 回退）
- `Runtime/Catalog/BundleDownloadManager.cs`：Bundle 下载管理（CheckAllPendingDownloads、CheckDownloadsForAsset、DownloadAllAsync、DownloadBundlesAsync）

**修改**
- `LocalContentCatalog.cs`：Initialize 使用 `HyperContentPaths.LoadText()` 支持 Android StreamingAssets
- `BundleFileProvider.cs`：ResolveFilePath 使用 `FileExistsOrIsStreamingAssets()` 支持 Android
- `UnityBundleLoader.cs`：LoadFromFileAsync 使用 `FileExistsOrIsStreamingAssets()` 支持 Android

**删除**
- `RemoteContentCatalog.cs`：由 CatalogLocator + LocalContentCatalog 替代
- `ContentUpdateManager.cs`：由 BundleDownloadManager 替代

**文档**
- `ARCHITECTURE.md`：更新默认 catalog 解析路径（settings.json flow）
- `INITIALIZATION_FLOW.md`, `LOAD_RELEASE_FLOW.md`, `CONTENT_UPDATE_FLOW.md`, `PROVIDER_FLOW.md`：由原 RUNTIME_FLOWS 拆分，更新初始化流程、CatalogLocator、Android StreamingAssets 等说明
- `OWNERS.md`：更新 Owner3 Key Files、Incremental Update 机制
- `DIRECTORY_STRUCTURE.md`：更新 Catalog 目录结构、Owner 责任映射

---

## 2026-02-25 - HyperContentPlayerBuildProcessor 日志增强 + 文档更新

### 背景
打包时难以定位 settings.json 找不到的问题，需要更详细的日志输出和排查文档。

### 变更（Owner1）

**HyperContentPlayerBuildProcessor.cs**
- 添加入口/出口日志：`PrepareForBuild START/END`
- 输出 BuildTarget、Expected BuildPath、Directory exists 状态
- 列出 build 目录下所有文件及大小
- 检查 settings.json、HyperCatalog.bin 是否存在，缺失时输出 Error
- 目录不存在时列出父目录下的可用子目录

**文档**
- `docs/CATALOG_LIFECYCLE.md`：新增 Troubleshooting 章节
  - Player Build 日志格式说明
  - Runtime settings.json 找不到的排查表
  - Build 顺序 checklist
- `docs/DIRECTORY_STRUCTURE.md`：补充 HyperContentPlayerBuildProcessor.cs、CATALOG_LIFECYCLE.md

---

## 2025-02-19 - addressableNames 短名 + InternalId 同步改为短名

### 背景
实测确认：设置 `addressableNames` 后，Unity `BuildPipeline` 用 `addressableNames` 完全替换 Bundle 内部 key，`assetNames` 的完整路径不再可用于 `LoadAsset`。

### 变更（Owner1）

- `BundleBuilder.cs` / `DefaultBuildExecutor.cs`: `AssetBundleBuild.addressableNames` 从 `marker.assetKey` 改为 `Path.GetFileNameWithoutExtension(assetPath)`（资源名不带后缀）。
- `CatalogGenerator.cs`: `assetPathIndex` 从完整路径改为 `Path.GetFileNameWithoutExtension(assetPath)`（与 `addressableNames` 一致），运行时 `bundle.LoadAsset(InternalId)` 直接命中。
- `BuildPipeline` API 暂不变更，`CompatibilityBuildPipeline` 切换待验证后实施。
- 详见 `docs/BUILD_PIPELINE_DECISION.md` 第 7 节。

---

## 2025-02-19 - IContentCatalog 废弃，统一迁移至 ICatalog

### 背景
Owner0 更新了设计文档：catalogHash 类型从 byte[] 统一为 string，新增 ICatalog 接口替代旧的 IContentCatalog。

### 变更（Owner3 执行）

**编译修复**
- `RemoteContentCatalog.cs`: catalogHash 相关变量类型从 `byte[]` 改为 `string`，与 `LocalContentCatalog.GetCatalogHashFromJson()` / `CatalogHashEquals()` 的新签名对齐。

**接口迁移 IContentCatalog → ICatalog**
- `RemoteContentCatalog.cs`: 实现 `ICatalog`；字段 `_localCatalog` / `_remoteCatalog` 改为 `ICatalog`；`Version` 返回 `string`；新增 `TryGetLocations`（委托内部 catalog）；删除 `TryGetBundleName`、`GetAllAssetKeys`。
- `ContentUpdateManager.cs`: 字段和构造函数参数从 `IContentCatalog` 迁移至 `ICatalog`。
- `BundleProvider.cs`: 同上。
- `ResourceProvider.cs`: 同上，并新增 `ResolveAssetBundleName()` 辅助方法（通过 `TryGetLocations` 提取 bundle 名，替代旧的 `TryGetBundleName` 调用）。
- `DependencyResolver.cs`: 参数迁移至 `ICatalog`；删除 `ResolveBundlesForAsset`（已由 ResourceProvider 内部实现替代）。

**清理**
- `LocalContentCatalog.cs`: 移除 `IContentCatalog` 实现、`#pragma` 抑制、旧版 `Version`(int)、`TryGetBundleName`、`GetAllAssetKeys`。
- 删除 `Runtime/Core/IContentCatalog.cs` 废弃接口文件。

---

## 2025-01-29 - 文档整理；移除 Catalog v1

- 重新整理 HyperContent 目录下文档，去除冗余。
- **移除 Catalog v1**：仅保留 Catalog v2（schemaVersion=2）作为当前 Schema；SPECIFICATION 中删除 v1 Schema 章节，Catalog 结构以 RESOURCE_LOADING_SYSTEM_SPEC 与 CatalogSchemaV2.cs 为准。
- **README.md**: 精简为入口索引，仅引用当前 Catalog；文档表与快速开始更新。
- **SPECIFICATION.md**: 删除「Catalog Schema (v1)」整节；目录结构更新为 CatalogSchemaV2、IAssetLoader、LocalContentCatalogV2；章节重新编号。
- **OWNERS.md** / **OWNER0_GUIDE.md**: Schema 仅引用 CatalogSchemaV2.cs；接口变更控制中 Catalog Schema 仅指 v2。
- **ARCHITECTURE.md**: 此前已补充 IAssetLoader；内存/错误处理引用 SPECIFICATION。

---

## 2024-01-XX - 修复编译错误

### 问题
1. Unity JsonUtility不支持Dictionary类型
2. Runtime程序集未引用Shared程序集

### 修复
1. 修改`CatalogSchema.cs`：将Dictionary改为数组结构
   - `assetToBundle`: Dictionary → AssetBundleMapping[]
   - `bundles`: Dictionary → BundleInfoData[]
   
2. 修改`HyperContent.Runtime.asmdef`：添加对Shared程序集的引用

3. 更新示例Catalog JSON：使用数组格式替代字典格式

### 注意事项
- 如果Unity仍显示编译错误，请尝试：
  1. 在Unity中右键点击`Assets/HyperContent`文件夹 → Reimport
  2. 或者关闭并重新打开Unity编辑器
  3. 或者删除`Library/ScriptAssemblies`文件夹（Unity会自动重建）
