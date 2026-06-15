# 5月TODO目标

> 目标：让 HyperContent 加载更快、更稳。AAB 整包阶段聚焦 **本地加载 + 实例化**，下载链路相关项延后。
>
> 文档维护：每完成一项更新状态列；新增项写到对应 Phase 并附事实依据。

---

## 0. 范围与约束

- **阶段**：AAB 整包上线（PAD InstallTime / StreamingAssets 命中）
- **不在范围**：`HttpBundleTransport` / `BundleDownloadQueue` / `LocalBundleStore` 远端写盘 / 断点续传 / 自适应并发
- **在范围**：`catalog Init` → `bundle load` → `asset extract` → `instantiate` 四段
- **校验方式**：所有改动用 `IGGProfiler` 跑前后基线，数据驱动而非感觉
- **真机要求**：至少 1 台中低端 Android 设备复测，不依赖编辑器/PC SSD 数据

---

## 1. 5月排期总览

| Phase | 内容 | 工期 | 阻塞依赖 | 进度 |
|---|---|---|---|---|
| Phase 1 | A 系列：GC alloc 优化 | ~0.5 工作日 | 无 | A2 ✅ / A3 ✅ |
| Phase 2 | B 系列：拆帧 + 冷启动并行 | ~3–5 工作日 | Phase 1 完成（数据基线） | B1 ✗（代码完整回滚到 pre-B1，行为 100% 等价改造前，仅保留与 B1 无关的埋点资产） / B2 ☐ / B3 ✅ |
| Phase 3 | C 系列 + D1 埋点 + A1（条件项） | 业务节奏配合 | Phase 2 完成 + D1 数据 | 全部 ☐ |
| Phase 0' | D2 阶段化 wall clock 埋点 | ~0.5 工作日 | 无（独立先行项） | D2 ✅ 已闭环（2026-05-21）；Follow-up：补充战斗开局 / 大场景切换采数后决定 OPT-A / OPT-1 / A1 实施 |

---

## 2. Phase 1 — 立即可做（事实充足 + 改动小 + 风险低）

> A1（`ResourceManager.Pump` 帧预算限流）已移到 **Phase 3**：定位修正为「帧时间平滑」而非「加快加载」，且最差情况会反向劣化总加载 wall clock，须由 D1 埋点数据决定是否启动。详见 §4 A1（条件项）。

### ~~A2. hot path 字符串 / Stopwatch 用 `#if ENABLE_PROFILERLOG` 包起来~~ ✅ 已完成（2026-05-19）

> 详见 [`CHANGELOG.md` — 2026-05-19 条目](./CHANGELOG.md)。核心结论：`[Conditional]` 会擦除"调用点 + 所有参数表达式"，但**不擦除**局部变量声明、含 `[Conditional]` 调用的 lambda、非 `[Conditional]` 的 alloc（如 `new Stopwatch()`），故按"Begin/End 同作用域 vs 跨 lambda"分别采用字符串直接内联 / `#if ENABLE_PROFILERLOG` 包整段两种手段。

---

### ~~A3. `OperationCache.GetOrCreate` 去 lambda 闭包（cache hit 路径零 alloc）~~ ✅ 已完成（2026-05-19）

> 详见 [`CHANGELOG.md` — 2026-05-19 OperationCache 条目](./CHANGELOG.md)。核心结论：lambda 捕获泛型参数 `T` / 局部 `location` 时编译器无法折叠成 static 委托，cache hit 也会白白 alloc closure + delegate ≈ 60–80 B/次；在 `OperationCache` 内部新增 `TryGetExisting`（保留 `RefCount++` + identity 断言封装），调用方 `LoadAsync<T>` / `LoadDependency` 加 fast path，cache hit 现在零 alloc。`LoadSceneAsync` 因冷路径 + 双变量捕获不改。
>
> **Follow-up（本期不做）**：方案 D — 把 `GetOrCreate` 的 factory 签名改为 `Func<ResourceLocation, AsyncOperationBase>` + 嵌套 generic helper class 缓存 static 委托，让 cache miss 也 0 alloc。cache miss 是冷路径 ROI 低，已记录到 CHANGELOG。
>
> **验证（待做）**：单元测试 `Tests/Runtime/OperationCacheFastPathTests.cs`（连续 10 次 cache-hit `LoadAsync` 用 `GC.GetAllocatedBytesForCurrentThread()` 硬断言 0 alloc），需要先铺 HyperContent 下的 Unity Test Framework asmdef，待业务侧确认后单独提交。

---

## 3. Phase 2 — 中期推荐（事实充足 + 改动中等 + ROI 明确）

### ~~B1. `Object.InstantiateAsync` 切 Unity 原生 API（Unity 2022.2+）~~ ✅ 已完成（2026-05-20）

> 详见 [`CHANGELOG.md` — 2026-05-20 B1 条目](./CHANGELOG.md)。核心结论：新增 `InstantiateOperation`（不进 OperationCache，每次新建；`LocationHash=0` 避免 hash 误命中；持有 1 个 AssetOp 借用 refcount 维持原"`Release(handle)` + `ReleaseInstance(go)` 各 −1"双计数语义）。3 个 `HyperContentImpl.InstantiateAsync` 实际实现重载统一走 `StartLoadAssetOp<T>` helper（与 `LoadAsync` 共用入口校验 + catalog 解析 + `HC.Load` 埋点）。Unity 原生 `InstantiateAsync` 无 `instantiateInWorldSpace` 参数，`worldSpace=true` 路径降级同步 `Instantiate`；其他路径走 `AsyncInstantiateOperation<GameObject>`。Late-completion guard：业务在 instance ready 前 `Release(handle)` 时，`OnInstantiateCompleted` 检测 `Status==Disposed` 主动 `Destroy(aio.Result[0])` 兜底。`OperationCache.Release` 同步加了 identity check（仅当 dict 里存的就是这个 op 才 Remove），防止 InstantiateOp 借的 `LocationHash` 与 cached AssetOp 的极小概率 hash 碰撞导致误删 cached entry。

> **2026-05-20 hotfix 2**（最终状态，必读）：hotfix 1 上线后**症状未消除**，业务复测后修正描述——**不是"UI 没加载出来"而是"UI 加载后位置不对"**。真正的根因不是上面 hotfix 1 假设的"`handle.Result` 隐性契约"，而是 **Unity 自身 API 行为不一致**：`Object.Instantiate(prefab, parent)` 默认 `worldPositionStays = false`（instance.localPosition = prefab.localPosition，UI 按 prefab 布局），而 `Object.InstantiateAsync(prefab, parent)` 默认 `worldPositionStays = true`（instance.worldPosition = prefab.localPosition，reparent 时反算导致 RectTransform.anchoredPosition 错位）。所有 cache miss + parent 模式的 UI 都受影响——hotfix 1 只覆盖了 cache hit 路径所以无效。考察过 3 类精确修复方案（不传 parent + 完成后 SetParent / 完成后手动 reset transform 字段 / 传 prefab.position 给 4 参重载），每种都有 Awake-time `parent==null` / RectTransform 字段镜像难做精确 / 数学语义不等价的致命问题。最终决定：**`OnAssetCompletedAsync` 也把 `forceSync` 改为 `true`，cache miss 路径也走同步 `Object.Instantiate`**，所有路径完整回退 pre-B1 行为。**B1 cache miss 主线程切片收益完全放弃**，但 `InstantiateOperation` 包装架构、`HC.Instantiate_<address>` 埋点、双路径分发代码全部保留，未来 Unity 修复 API 行为或业务侧引入 prefab 白名单时一行 `false` 即可重启异步路径。详见 [`CHANGELOG.md` — 2026-05-20 B1 hotfix 2 条目](./CHANGELOG.md)。
>
> **B1 进度仍标 ✅（代码改造完整落地），但 B1 价值已被本期完全降级**——未来回看时请勿误认为"B1 主线程切片收益已经吃满"，真实收益当前 = 0。
>
> **2026-05-20 实验性回滚 hotfix 2（采数中⚠）**：hotfix 2 上线后业务恢复正常，但**留下一个开放问题**——B1 cache miss 主线程切片的真实收益数值始终未量化，"永久 hotfix 2 vs 引入 opt-in/白名单保留部分收益"两个方向都是 ROI 拍脑袋。本期临时把 hotfix 2 回滚到 hotfix 1 状态（cache hit 同步 + cache miss 异步），用现有 `HC.Instantiate_<address>` IGGProfiler 埋点采集 cache miss `Object.InstantiateAsync` 的真实主线程占用。**回滚期间 cache miss UI prefab 仍会出现位置错乱**——是已知行为（hotfix 2 root cause 仍然存在，参见上方 blockquote），**业务侧实测期间不要 report 为新 bug**。退出路径：(A) 数据弱（净省 < 5ms）→ 回到 hotfix 2 全同步、B1 长期"价值=0"；(B) 数据强（部分 prefab 净省 ≥ 5ms，很可能集中在 3D/特效/场景物体）→ 引入 opt-in API 或 RuntimeSettings 白名单，仅对 QA 过的 prefab 启用异步、其他继续同步。无论 A/B 都要走出本期实验状态，避免 UI 错位 bug 长期留在 cache miss 路径。代码变更：`InstantiateOperation.OnAssetCompletedAsync` 单行 `forceSync: true → false` + 注释整段重写（保留 hotfix 2 worldPositionStays 根因分析 + 3 个否决精确修复方向作为 long-term reference）。详见 [`CHANGELOG.md` — 2026-05-20 B1 实验性回滚 hotfix 2 条目](./CHANGELOG.md)。
>
> **2026-05-20 退出路径 A · 决策收敛（B1 当前价值 = 0，hotfix 2 永久）**：上一期实验采数已完成，30+ 真机 prefab 样本的关键结论——`Object.InstantiateAsync` 有 1–2 帧 wall-clock 地板（≥ 16–33 ms @60fps），而本项目 prefab 分布以"同步 < 5 ms"为主，没有主线程切片预算来摊销这个地板，导致**异步路径在每一个采样 prefab 上都退化 wall-clock**：极轻量 prefab（`Level_CampaignIdle.prefab` 0.25 ms → 43 ms / `MainActivityPopupPackageWidget.prefab` 1.2 ms → 351 ms）退化 100–310 倍，中等 prefab 退化 2–4 倍，唯一接近持平的 `b4d2c84bf95df...`（32–50 ms → 55 ms，退化 1.3×）仅 1 个候选不足以支撑 opt-in 工程成本。**结论**：B1 的设计假设（"deserialize 重的 prefab 通过 Job 系统并行化主线程开销 → 加载更快"）在本项目 prefab 分布下**前提不成立**——milestone "加载更快"在业务体感语境下 = wall-clock 减少，而 B1 异步路径必然 wall-clock 增加，**目标根本性冲突**。走退出路径 A：`InstantiateOperation.OnAssetCompletedAsync` 单行 `forceSync: false → true`，hotfix 2 升级为永久状态，所有路径（cache hit + cache miss + worldSpace + PositionRotationParent）全部走同步 `Object.Instantiate`，行为 100% 等价 pre-B1。`InstantiateOperation` 包装架构 + IGGProfiler 埋点 + 双路径分发代码全部保留作为资产；未来 prefab 分布漂移到"50+ ms 重 prefab 为主" + Unity 修复 worldPositionStays + Unity 缩小 wall-clock 地板（≤ 0.5 帧）+ 业务场景明确接受 wall-clock trade-off 4 个条件全部满足时才考虑一行 toggle 重启。详细真机数据表 + 决策依据见 [`CHANGELOG.md` — 2026-05-20 B1 退出路径 A 条目](./CHANGELOG.md)。**B1 自此进入"代码资产化、价值=0"长期终态**——总览表 Phase 2 进度同步标注，无 follow-up。
>
> **2026-05-20 代码完整回滚（决策升级：B1 进度由 ✅ 资产化保留 改为 ✗ 完整删除）**：复盘"退出路径 A"决定后审计 `InstantiateOperation` 实际代码占比——**426 行里 74% 是 dead code 加上解释 dead code 为什么保留的注释**（真实在用代码 26% / 永远不会执行的异步分支 19% / 解释 dead code 为什么保留的注释 42%），"未来重启"的 4 个条件几乎不可能同时满足，且即使将来要重启时 Unity API 与 wall-clock 行为都会变，重新设计比修改保留的 dead code 更可能。继续保留的成本（认知负担、调用栈深 1–2 层、维护者反复评估"为什么不删"）明显大于收益。决策升级——把代码也完整回滚到 pre-B1 形态：(1) 删除 `Runtime/Operations/InstantiateOperation.cs` 整个文件（426 行 + `.meta`）；(2) `HyperContentImpl.InstantiateAsync` 3 个重载内联回 pre-B1 写法（`var assetOp = StartLoadAssetOp; assetOp.OnCompleted += pOp => { ...HC.Instantiate_ 埋点 + Object.Instantiate + Track }`），删除 `StartInstantiateOperation` helper；(3) `OperationCache.Release` 删除 B1 引入的 `LocationHash=0` 防御 `ReferenceEquals` identity check，恢复 pre-B1 裸 `_cache.Remove(op.LocationHash)`。**保留与 B1 决策无关的代码组织改善**：`StartLoadAssetOp<T>` private helper（LoadAsync 仍用、避免代码重复）+ `HC.Load_<address>` / `HC.Instantiate_<address>` IGGProfiler 埋点（业务侧 prefab 加载/实例化 hot triage 长期工具）+ B3 改动（与 B1 完全独立）。回滚后业务可见行为与 pre-B1 + 埋点 100% 等价。**B1 进入"代码完整回滚 ✗"终态**——总览表同步调整为 ✗。详见 [`CHANGELOG.md` — 2026-05-20 B1 代码完整回滚条目](./CHANGELOG.md)。

> **基线已就绪**：`HC.Instantiate_<address>` sample 名沿用 5/20 埋点，复用后改造前后可直接对比。

**保留信息（改造前事实，已落地）**：

### B1. `Object.InstantiateAsync` 切 Unity 原生 API（Unity 2022.2+）— 改造前记录

**对应 TODO**：与原 P0-4 "Prefab Pool" 互补

**事实**：
- Unity 项目版本 `2022.3.62f2`（`ProjectVersion.txt`），`Object.InstantiateAsync<T>` 可用
- 当前 `HyperContentImpl.InstantiateAsync` 内部用同步 `Object.Instantiate`：

```230:230:Assets/HyperContent/Runtime/Operations/HyperContentImpl.cs
instanceResult = UnityEngine.Object.Instantiate(assetOp.Result, pParent);
```

Unity 文档原话："Allows the creation of multiple GameObjects asynchronously without blocking the main thread"。对 deserialize 重的 Prefab（带 Material / Animator / Particle）通过 Job 系统并行化主线程开销。

**收益声明**（诚实度）：
- ✅ API 存在、改造可行
- ⚠ **具体收益数值未知**，**需要真机实测**，不同 Prefab 差异极大；简单 prefab 几乎没差距

**改造**：
- `HyperContentImpl` 新增 `InstantiateOperation` 类型，包装 `AsyncInstantiateOperation<GameObject>`
- 加载链路：`AssetOperation<GameObject>` succeeded → 串联触发 `InstantiateOperation` → 完成后 `Track` 到 `InstanceRegistry`
- `ContentHandle.Status` 语义扩展：要等到 Instantiate 完成才算成功
- 业务侧适配：`ContentHandle.Result` 在 succeed 前为 null，已有的 `OnCompleted` 回调时序保持不变

**改动范围**：~80 行（新增 `InstantiateOperation.cs` + `HyperContentImpl.InstantiateAsync` 系列 3 个重载改造）

**基线埋点**（2026-05-20 已落地，改造前可直接采数）：
- `HyperContentImpl.InstantiateAsync` 3 个实际实现重载里、`UnityEngine.Object.Instantiate(...)` 同步调用前后已加 `IGGProfiler.BeginSample($"HC.Instantiate_{pAddress}")` / `EndSample(...)`
- 字符串内联到 `[Conditional("ENABLE_PROFILERLOG")]` 实参里，关宏时整段（含 `$"..."` 拼接）从 IL 擦除，零运行时成本（规则同 A2，详见 CHANGELOG 2026-05-19 A2 条目）
- Begin/End 同步成对，`Object.Instantiate` 是阻塞调用，`op.OnCompleted` 主线程串行触发，不会出现同 sampleName 的并发冲突

**验证**：
1. **基线**：开 `ENABLE_PROFILERLOG`，选 3 个代表性 Prefab（轻 UI / 中等英雄 / 重场景），冷启动后跑同一组 `InstantiateAsync` 各 3 次，从 IGGProfiler 日志读 `HC.Instantiate_<address>` 主线程耗时
2. **改造后**：切到 `Object.InstantiateAsync` + `InstantiateOperation` 之后，在新链路里**用同一个 sampleName** 包裹「触发 `AsyncInstantiateOperation` → 完成回调 Track」整段，确保两组数据对的是「业务侧从触发到拿到 instance 的主线程占用」
3. **关键守门**：净省主线程帧时间需 > 测量噪声（建议 ≥ 5ms 才认收益）；简单 prefab 若净省 < 2ms 则保持同步实现，避免给业务侧引入「Result 暂时为 null」的时序适配成本

**状态**：✅ 已完成（2026-05-20）
**Owner**：

---

### B2. Catalog 后台线程化（仅反序列化 + BuildLookup）

**对应 TODO**：P0-5（"切到后台线程，与登录并行"）

**事实**：
- `CatalogBinaryReader.Read` 是纯 C#，无 Unity API（`CatalogBinaryReader.cs:29-100` 全程 MemoryStream + BinaryReader + UTF8.GetString）
- `LocalContentCatalog.BuildLookupStructures` 也是纯 C#，但**内部调了 `HCLogger.LogVerbose`**，而 `HCLogger.Prefix => $"... [{Time.frameCount}]"` 访问 `Time.frameCount` → **不能跨线程**
- `HyperContentPaths.LoadBytes` 在 Android StreamingAssets 路径下走 `UnityWebRequest`（`HyperContentPaths.cs:257-269`），**必须主线程**

**真实可后台化范围**：
- ❌ `LoadBytes`（Android 必须主线程，单文件 1–2MB，几十 ms 不可避免）
- ✅ `GZipDecompress`（仅 BinaryGzip 格式，~30ms）
- ✅ `CatalogBinaryReader.Read`（~30ms）
- ✅ `BuildLookupStructures`（~74ms，前提是处理 HCLogger 跨线程）

**收益声明**（诚实度）：
- ✅ 反序列化路径确实可后台
- ⚠ **真实净省 130–200ms**（基于你们 P0-6 实测数据推算），**不是上一轮我说的 ~300ms**
- ⚠ 必须修复 HCLogger 跨线程问题（dev 包不修会崩；release 包 LogVerbose 已 [Conditional] 剥离，但 LogInfo 还会留下来）

**改造**：
1. 修 `HCLogger.Prefix` 跨线程安全：访问 `Time.frameCount` 前判断 `UnityEngine.Application.isPlaying && System.Threading.Thread.CurrentThread.ManagedThreadId == 主线程 id`，否则用 `-1` 兜底
2. `LocalContentCatalog` 新增 `InitializeAsync(string source, CatalogSerializationFormat format, CancellationToken)`：
   - 主线程：`LoadBytes`
   - `Task.Run`：`GzipDecompress` + `CatalogBinaryReader.Read` + `BuildLookupStructures`
3. `HyperContent.InitializeBundleMode` 把 catalog Init 包成 Task，让业务侧的登录 Task 与它 `Task.WhenAll`

**改动范围**：`HCLogger` + `LocalContentCatalog` + `HyperContent.InitializeBundleMode`，约 50 行

**基线埋点**（**已存在，无需新增**）：
- `LocalContentCatalog.Initialize(source, format)` 已按 5 段全埋（2026-05-18 P0-6 落地）：
  - `HC.Catalog.Init.IO[{format}]` — `LoadBytes`（Android 主线程 UnityWebRequest，B2 改造后这段**仍在主线程**）
  - `HC.Catalog.Init.Decompress[{format}]` — `GZipDecompress`（B2 改造后**移到后台线程**）
  - `HC.Catalog.Init.Deserialize[{format}]` — `CatalogBinaryReader.Read` / `JsonUtility.FromJson`（B2 改造后**移到后台线程**）
  - `HC.Catalog.Init.BuildLookup[{format}]` — `BuildLookupStructures`（B2 改造后**移到后台线程**）
  - `HC.Catalog.Init.Total[{format}]` — Init 总耗时（用于交叉验证）
- 关宏时所有 sample 字符串拼接整段从 IL 擦除，零成本

**验证**：
1. **基线**：冷启动 Android 设备，跑 3 次，从 IGGProfiler 日志读 5 段数据；记录"主线程实际占用 = Total（基线）"
2. **改造后**：业务侧需要新追加一个**主线程 wall clock 埋点**（建议 `HC.Catalog.Init.MainThreadWait`，包"主线程触发 Init Task → await 返回"整段），用于度量净省；后台 3 段（Decompress / Deserialize / BuildLookup）的 IGGProfiler 仍会工作（IGGProfiler `Stopwatch` 是跨线程安全的，只是输出 log 可能跨帧）但日志里的 `Time.frameCount` 在子线程为 -1，不影响耗时数据
3. **关键守门**：
   - 主线程净省 ≥ 100ms（按文档收益声明）才认改造收益；< 50ms 则保持原同步实现，避免引入跨线程复杂度
   - 必须确认 `HCLogger.Prefix` 跨线程兜底已生效（dev 包不崩；release 包 `LogVerbose` 已 `[Conditional]` 剥离）
   - `Task.WhenAll(loginTask, catalogInitTask)` 的实际净省受登录 RTT 影响，登录 RTT < 100ms 时 catalog 后台化没有收益，需在改造前先确认业务侧 RTT 分布

**状态**：☐ 待开
**Owner**：

---

### ~~B3. `AsyncOperationBase.Dependencies` `List` → `array`（按需 lazy）~~ ✅ 已完成（2026-05-20）

> 详见 [`CHANGELOG.md` — 2026-05-20 B3 条目](./CHANGELOG.md)。核心结论：`AsyncOperationBase.Dependencies` 字段类型 `List<AsyncOperationBase>` → `AsyncOperationBase[]`，叶子 op（含大量叶子 bundle op）共用 `Array.Empty<AsyncOperationBase>()` singleton 实现 0 alloc；有依赖的 op 在 `ResourceManager.StartOperation` 内一次性 `new[depCount]` 后索引赋值。新增 `internal int DependencyCount` 字段，**必须**用它取数（不能用 `Dependencies.Length`），与原 `List.Count` 语义对齐——循环中途 `HandleDependencyFailure` 回滚时只回到当前已成功填入的 dep 数。6 个文件 / 30 行：`AsyncOperationBase` + `ResourceManager` + `OperationCache` + `BundleAssetExtractor` + `ProvideHandle` + `HyperContentDiagnostics`。

**保留信息（改造前事实，已落地）**：

### B3. `AsyncOperationBase.Dependencies` `List` → `array`（按需 lazy）— 改造前记录

**问题事实**：

```19:19:Assets/HyperContent/Runtime/Operations/AsyncOperationBase.cs
internal List<AsyncOperationBase> Dependencies = new List<AsyncOperationBase>();
```

每个 op（包括叶子 bundle op）都 new 一个 List。叶子 op deps == 0，永远用不到。

**改造**：
- 改为 `internal AsyncOperationBase[] Dependencies;` + `internal int DependencyCount;`
- `ResourceManager.StartOperation` 已经知道 `location.Dependencies.Count`，一次性 `new AsyncOperationBase[count]`
- `ProvideHandle.GetDependencyResult/GetDependencyBundle`（用 `Dependencies[i]` 索引）逻辑保持
- `HandleDependencyFailure` / `Dispose` 改 for 循环

**改动范围**：`AsyncOperationBase.cs` + `ResourceManager.cs` + `ProvideHandle.cs` + `OperationCache.cs` 约 30 行

**基线采集**（**不走 IGGProfiler**）：
- B3 优化的是 GC alloc（每个 leaf op 省 1 个 `List<AsyncOperationBase>` 实例 ≈ 40–60 B），耗时影响小到 IGGProfiler 测不出（信号低于噪声）
- 走 `GC.GetAllocatedBytesForCurrentThread()` 夹取，方案与 A3 的 follow-up 单元测试（`OperationCacheFastPathTests.cs`）对齐：

```csharp
// 在测试或 diagnostics 一次性 helper 里：
long before = System.GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 100; i++) {
    var h = HyperContent.LoadAsync<GameObject>(addr);  // 同一 addr，命中 cache fast path（A3）
    HyperContent.Release(h);
}
long after = System.GC.GetAllocatedBytesForCurrentThread();
UnityEngine.Debug.Log($"[HC.GCBase] LoadAsync x100 alloc = {after - before} B");
```

- 也可在 Unity Profiler Window 里看 `List<AsyncOperationBase>..ctor` / `AsyncOperationBase..ctor` 的 alloc 次数，作为二次交叉验证

**验证**：
1. **基线**：改造前跑上述 100 次 LoadAsync（含至少 1 个有依赖的 bundle，触发非叶子 op），记录总 alloc 字节
2. **改造后**：同一组 LoadAsync 再跑，对比净省字节；预期净省 ≈ `叶子 op 数 × sizeof(List<>)` ≈ 100 × 40+ ≈ 4 KB+（视依赖深度）
3. **行为不变性**：`Dependencies[i]` 索引、`HandleDependencyFailure` 遍历、`Dispose` 遍历 在改 array + count 后必须保持等价；建议加单元测试覆盖 `op.Dependencies.Count > 0` 的失败回滚路径

**状态**：✅ 已完成（2026-05-20）
**Owner**：

---

## 4. Phase 3 — 业务侧 + 埋点

### C1. ShaderVariantCollection.WarmUp

**事实**：行业经验值，首次出现的 shader variant 在 Android 上编译 100–500ms。无项目实测数据。

**改造**：
- 打包前收集战斗 / UI 场景的 SVC（Project Settings → Graphics → Save to asset）
- 启动后 idle 帧 `ShaderVariantCollection.WarmUp()`，分批多帧调用
- HyperContent 提供 `IShaderWarmupHook` 挂钩点（可选）

**改动范围**：业务侧主要，HyperContent 占位 API
**状态**：☐ 待开
**Owner**：

---

### C2. Prefab Pool + `PrewarmInstanceAsync` API

**对应 TODO**：原 P1-3 + P0-4 业务部分

**事实**：开战斗瞬间生成多个英雄是典型场景；池化是 Unity 行业标准做法。

**改造**：
- `HyperContent` 新增 `PrewarmInstanceAsync(address, count)` API：预先 Instantiate + SetActive(false) 入池
- `InstantiateAsync` 新增可选 `IInstancePool pool` 参数；`ReleaseInstance` 命中 pool 时归还而不是 Destroy
- HyperContent 不实现 pool 本体（业务自己实现），只提供挂钩

**改动范围**：HyperContent facade + `HyperContentImpl` + 业务侧实现池
**状态**：☐ 待开
**Owner**：

---

### D1. 关键节点 P50 / P95 埋点

**对应 TODO**：原 P2-3，**提前到 Phase 3**

**为什么必做**：没有真机数据，前面所有"性能数字"都是猜测。本文档里所有标注 "⚠ 估算" 的数值都依赖这一项落地后才能验证。

**埋点位置**：
- `HyperContent.Initialize` 入口 / 出口
- `HyperContent.LoadAsync` 调用 → AssetOperation.OnCompleted
- `BundleDownloadQueue` 完成
- `InstantiateAsync` 完成

**上报指标**：P50 / P95 耗时 + 命中率（catalog cache、bundle cache）+ 失败率

**改动范围**：约 80 行（新增埋点类 + 上报接入业务 SDK）
**状态**：☐ 待开
**Owner**：

---

### ~~D2. 加载链路阶段化 wall clock 埋点（独立先行项）~~ ✅ 已完成（2026-05-21）

> 详见 [`CHANGELOG.md` — 2026-05-21 D2 条目](./CHANGELOG.md)。核心结论：在 `HC.Load_<addr>` 整段、`HC.BundleIO_<bundle>`、`HC.Resolve_<bundle>`、`HC.Extract_<assetPath>`、`HC.Instantiate_<addr>` 等绝对耗时基础上，再加 4 个 **业务可见 wall clock 拆段** sample，让 "瓶颈在哪一段" 可以直接从 IGGProfiler 日志读出来，**不需要先动业务调用模式数据**。
>
> **2026-05-21 hotfix（首次真机采数即修复，合并两个子项）**：详见 [`CHANGELOG.md` — 2026-05-21 D2 hotfix 合并条目](./CHANGELOG.md)。
>
> **子项 1：Provide 段 End 时机修正**（SevenDayRewardWindow / MainChatWindow 真机实测发现）。D2 初版把 `HC.Load.Stage.Provide_<addr>` 的 EndSample 通过 `op.OnCompleted += ...` 订阅，但订阅时机是 OnCompleted 链最末——晚于 `HC.Load_<addr>` EndSample 和业务侧 ContentHandle 上注册的 `_LoadWindowPrefab → InstantiateWindow → UIControllerBase.Init/Prepare/Show` 整段实例化回调。`.NET MulticastDelegate` 按 `+=` 顺序触发导致 Provide stopwatch 被业务回调链耗时污染（SevenDay 污染 +127ms / MainChat +127ms / ChatWorld +73ms，规律：业务回调链工作量越大污染越多）。修复：`AsyncOperationBase` 加 `internal string _provideSampleName`（#if 包），`SetSucceeded` / `SetFailed` 在 `InvokeCompleted` 之前主动 EndSample；ResourceManager.ExecuteImmediate 把 `op.OnCompleted += ...` 改为 `op._provideSampleName = provideSample`。
>
> **子项 2：PAD 路径 BundleIO 埋点补齐**（MainChat 数据复盘时发现）。D2 初版只在 `BundleFileProvider.Provide` 包了 `HC.Resolve_<bundle>` / `HC.BundleIO_<bundle>`，但 Android 真机开 `GOOGLE_PLAY_ASSET_DELIVERY` 宏后 PAD provider 替代 BundleFileProvider 作主路径，PAD 内部 `OnPackReady` + `TryLoadFromBundleStore` 两条 `_bundleLoader.LoadFromFileAsync` 调用点都没埋点——意味着 Android 真机主路径下 bundle mmap + 头反序列化 wall clock 完全隐身，全部叠到 `HC.Load.Stage.Schedule_*` 段里看不出明细（实测 MainChat 105ms Schedule 段中大头都在这里）。修复：PAD provider 两条路径都补 `HC.BundleIO_<internalId>`（与 BundleFileProvider 同名，业务侧无须区分两条路径），EndSample 放在 lambda 入口最早处兼容 fallback 重试场景。不另加 `HC.Resolve_<bundle>`：PAD 路径"路径解析"语义已被 `HC.PAD.Pack_<packName>` + 同步几微秒的 `ResolvePackName`/`GetAssetLocation` 覆盖。
>
> **修复后预期**：(1) `Catalog + Schedule + Provide ≈ Load` 等式 ≤ ±5%（Provide ≈ Extract 同量级，与业务回调链时间解耦）；(2) Schedule 段大头（如果是 cache miss 路径）会被 `HC.BundleIO_*` 拆出明细，bundle IO 占多少 / Pump 拆帧延迟占多少一目了然，后续 A1 / OPT-1 / OPT-3 / OPT 提前预热 bundle 等优化决策才能精确判读。
>
> **2026-05-21 闭环验证**（重新打包真机采数）：MainChat / ChatWorld / PlayerIcon 三个 root op 等式偏差分别 0.7% / 2.2% / 2.6%，全部 ≤ ±5%。MainChat 8 个依赖 bundle（built_in_data 74ms / duplicateassets16 73ms / featurewindowrule1 65ms / ui_mailwindow_general_atlas 54ms / ui_uicommon_atlas_guildicon_atlas 43ms / ui_uicommon_chatbubble_atlas 31ms / ui_uicommon_worldmapbookmark_atlas 20ms / mainui_window_mainchatwindow root 29ms）全部同帧 BeginSample + Unity worker pool 并发执行，Schedule 段 wall clock ≈ 最长 IO（74ms）+ Pump 拆帧 + DAG 构建。详细数据归档见 [`CHANGELOG.md` — D2 hotfix § 真机闭环验证](./CHANGELOG.md)。

**保留信息（改造前事实，已落地）**：

### D2. 加载链路阶段化 wall clock 埋点 — 改造前记录

**对应 TODO**：D1 的轻量先行版，**优先级高于 D1**——D1 需要业务接入上报 SDK 才能拿数据，D2 只用现有 IGGProfiler 即可读，零业务侧适配成本。**所有 Phase 2 / Phase 3 后续优化决策都以 D2 数据为基线**（特别是 A1 vs OPT-1/2 谁该做的方向选择）。

**事实**：现有埋点覆盖了 Provider 内部各段绝对耗时（`HC.BundleIO_*` / `HC.Resolve_*` / `HC.Extract_*`）和整段 wall clock（`HC.Load_<addr>`），但 **业务 wall clock = HC.Load_<addr> 被哪一段吃掉** 是不可观测的——具体 4 个盲区：
1. **Catalog 解析**：每次 LoadAsync/InstantiateAsync 都调 `_catalog.TryGetLocations`，hot path 上 hash 表退化或冲突会一并被掩盖
2. **DAG 构建 + 依赖等待 + Pump 拆帧延迟**：业务一帧 LoadAsync N 个 prefab、依赖深 K 层时，最坏 wall clock = K 帧 × frameTime；这是当前肉眼看不见的隐形成本，**直接决定 A1（拆帧限流）vs OPT-1（去拆帧延迟）的方向**
3. **Provider 内部纯工作时间**：D/E/G 三段合起来到底占整段多少，目前只能拼凑
4. **PAD pack 拉取首次延迟**：AAB InstallTime 模式下 `PlayAssetDelivery.RetrieveAssetPackAsync` 在中低端设备上首次 200ms+，完全不可见

**埋点设计**（4 个 sample）：

| Sample | Begin / End 位置 | 含义 | 实现细节 |
|---|---|---|---|
| `HC.Load.Stage.Catalog_<addr>` | `HyperContentImpl.StartLoadAssetOp<T>` / `LoadSceneAsync` 包 `_catalog.TryGetLocations` 单行 | catalog 查表纯耗时 | 同作用域，字符串内联到 [Conditional] 实参，关宏整段擦除（规则同 A2） |
| `HC.Load.Stage.Schedule_<addr>` | Begin: `ResourceManager.StartOperation` 入口；End: `ExecuteImmediate` 实际跑 `op.Execute` 之前 | DAG 构建 + 所有依赖等待 + ScheduleExecute → Pump 拆帧延迟 三段合并 | 跨函数 + 跨 lambda，整段 `#if ENABLE_PROFILERLOG` 包；用 `_activeScheduleSamples` HashSet + op.OnCompleted 安全网处理 dep 失败路径（否则 sample 会卡死，下次同地址 LoadAsync 报 "already exists"） |
| `HC.Load.Stage.Provide_<addr>` | Begin: `ExecuteImmediate` 注册 OnCompleted 之前；End: op SetSucceeded/SetFailed 触发的 OnCompleted 回调 | provider 内部完整异步链（`Resolve` + `BundleIO` + `Extract` 等已有埋点之和） | 跨 lambda 必须 `#if` 包；订阅 OnCompleted 必须发生在 op.Execute 之前以兼容 BundleFileProvider IsLoaded fast path 的同步完成路径 |
| `HC.PAD.Pack_<packName>` | `PlayAssetDeliveryBundleProvider.GetOrCreatePackRequest` 包 `RetrieveAssetPackAsync` → `PlayAssetPackRequest.Completed` | 首次 PAD pack 拉取 wall clock；后续同 pack 复用 `_packRequests` 不会再触发 sample，反映"首次拉取"耗时 | 跨 lambda `#if` 包；`fresh.IsDone` 兜底同步路径（PAD `Completed` 在已完成时 += 不会自动 invoke） |

**root op vs dep op 区分**：`AsyncOperationBase` 加 `internal bool IsUserFacing` 字段；`ResourceManager.LoadAsync<T>` / `LoadSceneAsync` cache miss 后设为 true，`LoadDependency` 默认 false。Schedule / Provide 两个 sample 仅 root op 打——dep op 的耗时已被 `HC.BundleIO_*` / `HC.Resolve_*` 覆盖，再叠 sample 会爆 IGGProfiler 唯一名字字典。

**改动范围**：5 文件 / ~80 行
- `Runtime/Operations/AsyncOperationBase.cs`：+1 字段 `IsUserFacing`
- `Runtime/Operations/HyperContentImpl.cs`：`StartLoadAssetOp` / `LoadSceneAsync` 各加 Catalog 段埋点
- `Runtime/Operations/ResourceManager.cs`：+1 字段 `_activeScheduleSamples`、`LoadAsync` / `LoadSceneAsync` 设 `IsUserFacing = true`、`StartOperation` 入口 + `ExecuteImmediate` 中 Schedule/Provide 埋点
- `Runtime/Providers/PlayAssetDeliveryBundleProvider.cs`：`GetOrCreatePackRequest` PAD 段埋点

**关 ENABLE_PROFILERLOG 时的零成本保证**：
- 同作用域调用（Catalog 段）：`[Conditional]` 擦除调用点 + 字符串拼接表达式，零残留
- 跨 lambda（Schedule / Provide / PAD 三段）：整段 `#if ENABLE_PROFILERLOG` 包，包括 lambda 订阅本身（`+= ...`）和 closure 字符串变量声明，关宏后整段从 IL 擦除
- `IsUserFacing` 字段保留（1 byte / op），无逻辑代价
- `_activeScheduleSamples` HashSet 整个用 `#if` 包，关宏时字段不存在、`HashSet` 实例不创建

**基线采集流程**（首次跑通用）：
1. 工程开 `ENABLE_PROFILERLOG` 宏
2. 中低端 Android 真机冷启动 → 主城进入 → 战斗开局 → UI 大窗切换，每步骤连跑 3 次
3. 从 IGGProfiler 日志按 `[性能统计]` 前缀 grep，按 sample 名分组取 P50 / P95：
   - `HC.Load.Stage.Catalog_*`（hot path 频次高，看分布）
   - `HC.Load.Stage.Schedule_*`（决定 A1 vs OPT 方向）
   - `HC.Load.Stage.Provide_*`（与 D/E/G 已有埋点交叉验证）
   - `HC.PAD.Pack_*`（首次延迟数据）
4. 数据归档到 `CHANGELOG.md`，作为后续所有 Phase 决策的基线

**数据 → 决策映射**（D2 落地后 Phase 决策的判据）：

| D2 数据信号 | 触发的优化项 | 备注 |
|---|---|---|
| `HC.Load.Stage.Schedule_*` P50 > 16ms 且业务调用集中在 loading screen | A1（Pump 帧预算限流，已在文档） | A1 与"加载更快"目标一致 |
| `HC.Load.Stage.Schedule_*` P50 > 16ms 且业务调用分散在玩家可交互场景 | **新增 OPT-1**：root op 在依赖全部已 Succeeded 时 ExecuteImmediate 跳过 Pump 直接同步执行 | A1 反向，二选一 |
| `HC.PAD.Pack_*` 首次 P50 > 50ms 且玩家可见路径上 | **新增 OPT-3**：启动 idle 帧预热已知 InstallTime pack | 中风险 |
| `HC.Catalog.Init.Total[*]` ≥ 200ms 且业务登录 RTT ≥ 100ms | B2（已在 Phase 2 文档） | 无新动作 |
| Schedule 段 wall clock ≈ 最长 BundleIO（依赖多窗共用且 cache miss 主导） | **新增 OPT-A**：HyperContent 提供 `PreloadBundlesAsync` API，业务 idle 帧预热 hot bundle | 已被 MainChat 数据触发（8 dep bundle / Schedule 101ms / 最长 IO 74ms），待补充更多场景验证收益面 |

**数据 → 决策映射 — 已观测信号**（2026-05-21 首次真机采数）：

| Sample | 实测数据 | 触发的优化项 | 状态 |
|---|---|---|---|
| MainChat Schedule | 101.585ms（8 dep bundle 同帧并发，最长 built_in_data 74ms） | OPT-A bundle 预热 | 数据已支持，待更多场景验证 |
| ChatWorld Schedule | 6.371ms（依赖全 cache hit，同帧完成） | — | 印证 OPT-A 收益上界（cache hit 后 ~6ms） |
| PlayerIcon Schedule | 21.151ms（依赖全 cache hit，跨 1 帧 Pump 拆帧） | OPT-1 同帧 fast-path | 数据已支持，待更多场景验证 |
| MainChat 全段 Load | 371.417ms（Schedule 27% + Provide 72%） | Provide 段优化重点：业务 PrewarmInstance 池化（C2） | 进入 Phase 3 范畴 |

**未观测到的信号 / 待补充采数场景**：

| 场景 | 期望确认的 D2 信号 | 业务侧观察 | 关联优化项 |
|---|---|---|---|
| **战斗开局**（推测 A1 高发场景） | 1) 大量 `HC.Load_*` BeginFrame 集中同帧（≥ 30 个）<br>2) 单个 `HC.Load.Stage.Schedule_*` wall clock > 200ms<br>3) `HC.BundleIO_*` 同帧 BeginSample ≥ 30 且最长 IO > 100ms | 开局瞬间帧率骤降 / 卡顿 | A1（如果触发） vs OPT-1（如果不触发但有 Pump 拆帧延迟） |
| **大场景切换**（主城 → 战斗 / 商城 / 公会） | 1) `HC.PAD.Pack_*` 首次拉取 wall clock（首次进入新 pack 的特殊成本）<br>2) Schedule 段 BundleIO 是否互相阻塞（最长 IO 占 Schedule 80%+） | 切换瞬间黑屏 / 长 loading | OPT-3（PAD 预热）/ OPT-A（hot bundle 预热）|
| **MainChat 关闭再打开** | 第二次 Schedule 应接近 ChatWorld 的 6ms（cache hit 全部命中） | — | 验证 IBundleLoader 引用计数 / cache 释放策略稳定性 |
| **冷启动到首屏** | 累加每个 root op 的 Load wall clock vs 业务侧 LoadingScreen 总时长 | — | 后续优化的"北极星指标"基线 |

**采数操作建议**：

- 业务侧在每个采数场景前后打 marker（如 `Debug.Log("=== 战斗开局采数开始 ===")`），方便日志按场景拆分
- 每场景连跑 3 次取 P50 / P95
- 数据归档到 `CHANGELOG.md` 同条目末尾"真机闭环验证"区下方
- 拿到补充数据后再决定 OPT-A / OPT-1 / A1 的实施顺序

**状态**：✅ 已完成 + 已闭环验证（2026-05-21）；Follow-up：补充战斗开局 + 大场景切换数据后再决定 OPT-A / OPT-1 / A1 实施优先级
**Owner**：

---

### A1. `ResourceManager.Pump` 帧预算限流（**条件项**：依赖 D1 数据）

**对应 TODO**：原 P0-1，**降级 + 后置**（2026-05-19 重评，原属 Phase 1）

**定位修正**（关键）：
- ❌ **不是** 为了"加快加载总时间"。Unity worker pool 并发上限就 2–4，一次性触发 30 个 `LoadFromFileAsync` 和拆 4 帧触发，**worker thread 实际工作量相同**
- ✅ **目标是** 把单帧主线程长尖刺（100ms+）拍平，让加载期间业务侧（UI 动画 / 网络 callback / 点击反馈）能继续响应
- 原文档「验证」段已声明 "加载总时间不变，只是拆均匀"，本轮把这条提到目标里写清楚

**何时启动**（满足其一）：
1. D1 埋点显示长帧 100ms+ 发生在 **玩家可交互场景**（战斗中召唤 / UI 大窗切换中 prefetch），不只在 loading screen
2. QA 实测确认 loading screen 期间长帧已影响进度条流畅度

> 如果 D1 数据显示长帧只发生在转圈 loading screen，**本项可砍**。

**问题事实**：

```216:230:Assets/HyperContent/Runtime/Operations/ResourceManager.cs
private void Pump()
{
    if (_pendingExecute.Count == 0) return;

    int batch = _pendingExecute.Count;
    for (int i = 0; i < batch; i++)
    {
        var op = _pendingExecute.Dequeue();
        ExecuteImmediate(op);
    }
}
```

单帧把所有 pending op 全部 Execute。业务一帧 LoadAsync 30 个 prefab → 下一帧一次性触发 30 个 `AssetBundle.LoadFromFileAsync`，主线程被 mmap + 头部反序列化卡到 100ms+ 长帧。

**真实风险**（解释为什么要谨慎）：

| 风险场景 | 后果 |
|---|---|
| `maxOpsPerFrame` 设小导致 worker thread 出现空闲 | 总加载 wall clock 变长 N × 16ms |
| 用 `frameBudgetMs` 硬截断，遇上单 op 同步段 > 预算 | 每帧仅做 1 个，反向劣化 |
| asset op 也一起限流 | asset op 主线程开销 1–2ms 本就轻，限流没收益反而增加链路延迟 |

**保守改造方案**（与原方案差异）：
- **只限流 bundle op**，asset op 不限（asset op 不是大头）
- `maxBundleOpsPerFrame` 默认 4，可调
- **不做 `frameBudgetMs` 硬截断**；改为"做完当前 op 后再检查累计耗时"，避免单 op 死循环
- `RuntimeSettings.ThrottlePump` 开关：loading screen 阶段业务侧关闭限流，火力全开
- 暴露到 `RuntimeSettings` 让 QA / 业务可调

**改动范围**：`ResourceManager.cs` 单文件 ~40 行
**验证**（必含 wall clock 回归保护）：
- 用 IGGProfiler 标 `HC.Pump.Frame`
- Frame Debugger 看玩家可交互场景的最长帧从 100ms+ 降到 < 16ms
- **关键守门**：同一组 LoadAsync 调用，**总加载 wall clock 不能回退**（误差 ≤ 5%）；若回退，立刻调高 `maxBundleOpsPerFrame` 或关闭限流
- 验证 worker thread 占用率：限流前后 AssetBundle worker 应仍然饱和

**状态**：☐ 待开（等 D1 数据触发）
**Owner**：

---

## 5. 已重新评估 / 不做

| 原始建议 | 决定 | 原因 |
|---|---|---|
| TryGetLocations 缓存 ResourceLocation | **降到 P2 / 暂不做** | `ResourceLocation.ResourceType` 是 `{ get; }` 只读但参与功能（`BundleAssetExtractor.cs:32` 读取），无法零 alloc 缓存；每次 LoadAsync 节省 ≈ 100 bytes，相对 ROI 低 |
| BundleFileProvider 用 catalog `LocalPath` | **取消** | `HyperContentPaths.FileExistsOrIsStreamingAssets` 在 Android StreamingAssets 路径下直接 `return true`（`HyperContentPaths.cs:393-398`），零 IO；AAB 阶段更直接走 PAD provider 不调此方法。预期收益 ≈ 0 |
| 同 bundle 多 asset 合批 LoadAsset | **保持观察** | 需要业务侧 profile 数据驱动；目前业务调用模式未知，不主动做 |
| LocalBundleStore SHA256 启动校验 | **AAB 阶段不做** | AAB 整包阶段缓存目录为空，`VerifyAndCleanCache` 遍历返回空 list；走到下载阶段才有意义 |
| HttpBundleTransport / BundleDownloadQueue 改造 | **AAB 阶段不做** | 下载链路不在范围 |

---

## 6. 验证流程（每个 Phase 完成后必走）

1. **基线**：执行前用 IGGProfiler 开启 `ENABLE_PROFILERLOG`，跑代表性流程 3 次（冷启动 / 战斗开局 / UI 大窗切换），记录关键 sample 耗时
   - **现有可直接采数的 sample**（已埋点，不需要新增代码）：
     - **整段 wall clock**：
       - `HC.Load_<address>` — `HyperContentImpl.LoadAsync<T>` 整段加载（含依赖 bundle 链）
       - `HC.Instantiate_<address>` — `HyperContentImpl.InstantiateAsync` 同步 `Object.Instantiate` 段（2026-05-20 落地）
     - **D2 阶段化拆段**（业务可见 wall clock 拆段，2026-05-21 落地）：
       - `HC.Load.Stage.Catalog_<address>` — `_catalog.TryGetLocations` 单段（LoadAsync / LoadSceneAsync 共用）
       - `HC.Load.Stage.Schedule_<address>` — DAG 构建 + 依赖等待 + Pump 拆帧延迟合并段
       - `HC.Load.Stage.Provide_<address>` — provider 内部完整异步链（包含下面 Resolve/BundleIO/Extract）
       - `HC.PAD.Pack_<packName>` — PAD 首次 `RetrieveAssetPackAsync` 段（仅 Android PAD 路径）
     - **Provider 内部分段绝对耗时**：
       - `HC.Resolve_<bundle>` — `BundleFileProvider.ResolveFilePath` 路径解析
       - `HC.BundleIO_<bundle>` — `_bundleLoader.LoadFromFileAsync` mmap + 头反序列化
       - `HC.Extract_<assetPath>` — `BundleAssetExtractor.Provide` 单 asset `LoadAssetAsync` 提取
     - **Catalog 初始化分段**（2026-05-18 P0-6 落地）：
       - `HC.Catalog.Init.{IO,Decompress,Deserialize,BuildLookup,Total}[<format>]` — B2 五段基线
   - **GC alloc 类基线**（B3 / A3 follow-up）走 `GC.GetAllocatedBytesForCurrentThread()` 夹取，不走 IGGProfiler
2. **改造**：按 Phase 内顺序逐项落地，每项独立 commit；每项必须**复用同名 sample** 才能直接对比
3. **回归**：每项完成后跑同一组流程，对比 sample 耗时；不通过守门条件（各项「关键守门」段）即回滚
4. **数据归档**：把对比表追加到 `docs/CHANGELOG.md`，参考 2026-05-18 条目的格式（按 format / 阶段拆段）
5. **真机复测**：至少 1 台中低端 Android 设备复测，确保编辑器数据不被误判为真机收益

---

## 7. 诚实度声明

本文档的"预期收益 / 改动范围"按以下分类：

| 标记 | 含义 |
|---|---|
| ✅ 事实 | 有代码引用 / 官方文档 / 你们项目实测数据支撑 |
| ⚠ 估算 | 行业经验值或基于事实推算，**没有该项目真机数据**，落地前不要当承诺数字 |
| ❌ 推翻 | 上一轮我说错、本轮核实后纠正的项 |

**所有"⚠ 估算"项的真实收益必须靠 D1 埋点 + Phase 完成后真机实测验证**。本文档不承诺任何具体性能数字。

---

## 8. 更新历史

- 2026-05-19：初版，基于 `docs/TODO.md` 的 P0/P1/P2 路线图 + 代码 review 重新校准范围（聚焦本地加载 / 实例化，下载链路降级）
- 2026-05-19：A2 落地（`HyperContentImpl.LoadAsync` + `BundleAssetExtractor.Provide` 去除 hot path GC alloc），详细内容已折叠为单行链接到 `CHANGELOG.md`；同步总览表进度列
- 2026-05-19：A1 重评——定位从「加快加载」修正为「帧时间平滑」，识别 3 个反向劣化风险（worker 空闲 / 单 op 超预算死循环 / asset op 误限流），改为只限流 bundle op + 无硬截断 + loading screen 全开关；从 Phase 1 后置到 Phase 3，作为依赖 D1 数据的条件项；总览表 Phase 1 内容相应缩窄为「GC alloc 优化」，工期从 ~1 工作日 → ~0.5 工作日
- 2026-05-19：A3 方案细化——补充 closure + Func<> delegate 双 alloc 事实（≈ 60–80 B/次）；示例代码改为 `OperationCache.TryGetExisting` 内封装 fast path（含 `RefCount++` 与 `AssertCacheHitIdentityMatch`），删除冗余的 `Status != None` 判断；标记方案 B（泛型 `where new()`）为否决并说明原因；明确 LoadSceneAsync 不改（冷路径 + 双捕获）；验证升级为 `GC.GetAllocatedBytesForCurrentThread` 单元测试硬断言；新增 Follow-up 方案 D（cache miss 也 0 alloc，本期不做）
- 2026-05-19：A3 落地（`OperationCache.TryGetExisting` + `ResourceManager.LoadAsync<T>` / `LoadDependency` fast path），详细内容已折叠为单行链接到 `CHANGELOG.md`；同步总览表进度列 A3 ✅；单元测试 `OperationCacheFastPathTests.cs` 因需先铺 Unity Test Framework asmdef 暂未提交，列入 follow-up
- 2026-05-20：Phase 2 基线采集前置——B1 在 `HyperContentImpl.InstantiateAsync` 3 个实际实现重载里给同步 `Object.Instantiate` 段加 `HC.Instantiate_{address}` 埋点（字符串内联到 `[Conditional]` 实参里，关宏零成本，规则同 A2）；B2 五段埋点已存在（2026-05-18 P0-6 落地）直接复用；B3 GC alloc 不走 IGGProfiler 改走 `GC.GetAllocatedBytesForCurrentThread()`；在 B1/B2/B3 各加「基线埋点 / 基线采集」小节明确 sample 名 + 关键守门条件，并把 §6 验证流程的「现有可直接采数的 sample」列表补齐
- 2026-05-20：B1 + B3 落地（B2 暂缓）。B1 新增 `InstantiateOperation` 包装 `AsyncInstantiateOperation<GameObject>`，3 个 `InstantiateAsync` 实际实现重载改造 + 提取 `StartLoadAssetOp<T>` helper（与 `LoadAsync` 共用入口逻辑）；同步加 `OperationCache.Release` identity check 防御 `LocationHash` 极小概率碰撞误删 cached entry；`worldSpace=true` 降级同步 `Instantiate`（Unity 原生 `InstantiateAsync` 无此参数）。B3 `Dependencies` 字段 `List<>` → `array` + `DependencyCount`，叶子 op 共用 `Array.Empty` singleton。详细内容已折叠为单行链接到 `CHANGELOG.md`；总览表 Phase 2 进度列同步为 `B1 ✅ / B2 ☐ / B3 ✅`
- 2026-05-20：B1 hotfix。业务侧报告"个别 UI 不显示无报错"，根因为 cache hit 时改造前同步 `Instantiate` 形成的隐性契约（handle 返回时 `Result` 立即可用）被 `Object.InstantiateAsync` 的真异步打破，业务 UI 工厂"假定 cache hit 同步可用"的代码 silent fail。修复方案：`InstantiateOperation.Start()` 检测 asset op 已终态时走同步 `Object.Instantiate` 降级；cache miss 路径仍走异步保留 B1 主要价值。详见 [`CHANGELOG.md` — B1 hotfix 条目](./CHANGELOG.md)
- 2026-05-20：B1 hotfix 2。hotfix 1 上线后症状未消除，业务复测后修正描述——**不是"UI 不显示"而是"UI 加载后位置不对"**。真正根因是 Unity API 不一致：`Object.Instantiate(prefab, parent)` 默认 `worldPositionStays=false` 而 `Object.InstantiateAsync(prefab, parent)` 默认 `worldPositionStays=true`，UI prefab（RectTransform 非零 anchoredPosition）走异步后位置反算错乱。考察过 3 类精确修复方向（不传 parent + SetParent 后置 / 完成后 reset transform 字段 / 传 prefab.position 给 4 参重载）都有 Awake-time `parent==null` / RectTransform 字段难精确镜像 / 数学语义不等价的致命问题。最终方案：`OnAssetCompletedAsync` 也把 `forceSync` 改为 `true`，cache miss 路径也走同步 `Object.Instantiate`，所有路径完整回退 pre-B1 行为。**B1 主线程切片价值本期完全降级为 0**；`InstantiateOperation` 包装架构 + IGGProfiler 埋点 + 双路径分发代码全部保留，未来一行 `false` 即可重启。总览表 Phase 2 进度同步追加「价值=0」标记。详见 [`CHANGELOG.md` — B1 hotfix 2 条目](./CHANGELOG.md)
- 2026-05-20：B1 实验性回滚 hotfix 2（cache miss 异步采数中⚠）。hotfix 2 上线后业务恢复正常，但留下开放问题——B1 cache miss 主线程切片的真实收益数值始终未量化，无法 ROI 判定是该永久 hotfix 2 还是引入 opt-in/白名单。临时把 hotfix 2 回滚到 hotfix 1 状态（cache hit 同步 + cache miss 异步），用现有 `HC.Instantiate_<address>` 埋点采数；**业务侧 cache miss UI 错位本期会复现，是已知行为，不要 report**。采数后按数据走退出路径 A（数据弱 → 回 hotfix 2）或 B（部分 prefab 数据强 → 引入 opt-in/白名单）。代码改动：`InstantiateOperation.OnAssetCompletedAsync` 单行 `forceSync: true → false` + 注释整段重写（保留 hotfix 2 worldPositionStays 根因 + 3 个否决精确修复方向作为 long-term reference）。总览表 Phase 2 进度同步标注「hotfix 2 已临时回滚，采数中⚠」。详见 [`CHANGELOG.md` — B1 实验性回滚 hotfix 2 条目](./CHANGELOG.md)
- 2026-05-20：B1 退出路径 A · 决策收敛（hotfix 2 永久）。上一期实验采数完成，30+ 真机 prefab 样本一致结论——`Object.InstantiateAsync` 有 1–2 帧 wall-clock 地板（≥ 16–33 ms @60fps），本项目 prefab 分布以"同步 < 5 ms"为主、没有主线程切片预算来摊销地板，**异步路径在每一个采样 prefab 上都退化 wall-clock**：极轻量 prefab 退化 100–310×（如 0.25 ms → 43 ms / 1.2 ms → 351 ms），中等 prefab 退化 2–4×，唯一接近持平的重 prefab 仅 1 个候选不足以支撑 opt-in 工程成本。**B1 设计假设（"deserialize 重 prefab 通过 Job 系统并行化主线程开销 → 加载更快"）在本项目 prefab 分布下前提不成立——milestone "加载更快"在业务体感语境 = wall-clock 减少，而 B1 异步路径必然 wall-clock 增加，目标根本性冲突**。走退出路径 A：`InstantiateOperation.OnAssetCompletedAsync` 单行 `forceSync: false → true`，hotfix 2 升级为永久状态，所有路径全部走同步 `Object.Instantiate`，行为 100% 等价 pre-B1。`InstantiateOperation` 包装架构 + IGGProfiler 埋点 + 双路径分发代码全部保留作为资产；未来重启需 4 个条件同时满足（prefab 分布漂移到 50+ ms 重 prefab 为主 + Unity 修 worldPositionStays + Unity 缩 wall-clock 地板 ≤ 0.5 帧 + 业务场景明确接受 wall-clock trade-off）。**B1 自此进入"代码资产化、价值=0"长期终态，无 follow-up**。总览表 Phase 2 进度同步更新为「hotfix 2 永久，B1 与《加载更快》目标根本性冲突，价值=0」。详细真机数据表 + 决策依据见 [`CHANGELOG.md` — 2026-05-20 B1 退出路径 A 条目](./CHANGELOG.md)
- 2026-05-20：B1 代码完整回滚（决策升级：从"代码资产化保留"改为"完整删除回到 pre-B1"）。复盘退出路径 A 的"代码资产化"决定时审计 `InstantiateOperation` 实际代码占比——**426 行里 74% 是 dead code 加上解释 dead code 为什么保留的注释**（真实在用 26% / 永远不会执行的异步分支 19% / 解释 dead code 为什么保留的注释 42%），"未来重启"4 个条件几乎不可能同时满足、即使将来要重启时 Unity API 与 wall-clock 行为都会变（重新设计 > 修改保留代码）；继续保留的成本（认知负担、调用栈深 1–2 层、维护者反复评估"为什么不删"）明显大于收益。决策升级：(1) 删除 `Runtime/Operations/InstantiateOperation.cs` 整个文件（426 行 + `.meta`）；(2) `HyperContentImpl.InstantiateAsync` 3 个重载内联回 pre-B1 写法 + 删除 `StartInstantiateOperation` helper；(3) `OperationCache.Release` 删除 B1 引入的 `LocationHash=0` 防御 identity check，恢复 pre-B1 裸 `_cache.Remove`。**保留与 B1 决策无关的代码组织改善**：`StartLoadAssetOp<T>` private helper（LoadAsync 仍用、避免代码重复）+ `HC.Load_<address>` / `HC.Instantiate_<address>` IGGProfiler 埋点（业务侧 prefab 加载/实例化 hot triage 长期工具）+ B3 改动（与 B1 完全独立）。**回滚后业务可见行为 100% 等价 pre-B1 + 埋点**。总览表 Phase 2 进度由 ✅（资产化保留）改为 ✗（代码完整回滚，与改造前 100% 等价）。**B1 全链路决策收敛，无任何遗留问题**。详细删除/保留清单 + 行为等价性见 [`CHANGELOG.md` — 2026-05-20 B1 代码完整回滚条目](./CHANGELOG.md)
- 2026-05-21：D2 阶段化 wall clock 埋点落地（独立先行项 Phase 0'，优先级高于 D1）。新增 4 个 sample 把 `HC.Load_<addr>` 整段拆成 `Catalog → Schedule → Provide` 三段 + PAD 首次拉取段；目标是给后续所有 Phase 决策（特别是 A1 拆帧限流 vs 新增 OPT-1 去拆帧延迟的方向选择）提供数据基线。改动 5 文件 / ~80 行：`AsyncOperationBase` +1 字段 `IsUserFacing` 区分 root op vs dep op；`HyperContentImpl.StartLoadAssetOp` / `LoadSceneAsync` 包 `_catalog.TryGetLocations` 加 `HC.Load.Stage.Catalog_<addr>`；`ResourceManager` 在 `LoadAsync<T>` / `LoadSceneAsync` cache miss 后设 `IsUserFacing = true`，`StartOperation` 入口 + `ExecuteImmediate` 中加 `HC.Load.Stage.Schedule_<addr>` / `HC.Load.Stage.Provide_<addr>`，`_activeScheduleSamples` HashSet + op.OnCompleted 安全网处理 dep 失败路径下 sample 残留问题；`PlayAssetDeliveryBundleProvider.GetOrCreatePackRequest` 加 `HC.PAD.Pack_<packName>`，仅首次拉取打 sample 复用 pack 不重复。所有跨 lambda 段（Schedule / Provide / PAD）整段 `#if ENABLE_PROFILERLOG` 包，关宏后 IL 完全擦除（含订阅 +=、closure 字符串变量、HashSet 字段），规则与 A2 一致。同步 §6 验证流程更新 sample 列表分组（整段 wall clock / D2 阶段化 / Provider 内部分段 / Catalog 初始化分段）；§4 新增 D2 完整章节（含改造前事实 + 数据 → 决策映射表）；总览表新增 Phase 0' 进度行。**采数后按"数据 → 决策映射"表触发 A1 / OPT-1 / OPT-3 / B2 等具体优化项，本期 D2 仅给数据不动逻辑**。详见 [`CHANGELOG.md` — 2026-05-21 D2 条目](./CHANGELOG.md)
- 2026-05-21：D2 hotfix 合并条目真机闭环验证。业务侧重新打包跑同一组流程（冷启动 → 主城 → MainChat → ChatWorld → PlayerIcon），三个 root op 全部满足 `Catalog + Schedule + Provide ≈ Load` 等式，偏差 0.7% / 2.2% / 2.6%（≤ ±5% 全部达标），`HC.Load.Stage.Provide_*` 段污染问题彻底解决（≈ Extract 同量级）；MainChat 8 个依赖 bundle 全部可见（hotfix 前完全隐身），最长 IO `built_in_data` 74ms，Schedule 段 wall clock 101ms ≈ 最长 IO + Pump 拆帧 + DAG 构建（worker pool 并发健康，串行假设下应是 390ms）。**首次观测到的优化信号**：(1) MainChat cache miss 路径 Schedule 占 27% / Provide 占 72%——Schedule 段可被 OPT-A bundle 预热从 100ms 削到 ~5ms（ChatWorld 实证 cache hit 路径 6ms 上界）；(2) PlayerIcon 依赖全 cache hit 但 Schedule 21ms 跨 1 帧——是 OPT-1 同帧 fast-path 的精确触发数据；(3) MainChat 8 dep bundle 同帧并发已经做对，**A1 拆帧限流在 UI 主窗这一类场景没触发**，需要补充战斗开局 / 大场景切换数据再决定 A1 是否实施。**新增决策映射表行**：Schedule 段 wall clock ≈ 最长 BundleIO 时触发 OPT-A（HyperContent 提供 `PreloadBundlesAsync` API + 业务侧 hot bundle 名单）。**Follow-up 采数计划归档到 D2 章节**：战斗开局（A1 怀疑场景）/ 大场景切换（OPT-3 + OPT-A 收益验证）/ 关闭再打开 MainChat（cache 释放策略稳定性）/ 冷启动到首屏（北极星指标基线）四类场景，业务侧 marker + P50/P95 取样。CHANGELOG `2026-05-21 D2 hotfix` 条目下"真机闭环验证"区已归档完整数据表，本期 D2 全链路至此闭环。详见 [`CHANGELOG.md` — D2 hotfix § 真机闭环验证](./CHANGELOG.md)
- 2026-05-22：D3 前置数据基础设施落地（合并条目，4 件事打包到一个 CHANGELOG）。**(1) bundle 元数据日志**：`HCLog.cs` 新增 `LogDiagnostic` / `LogBundleSize` / `LogBundleSizeBytes` 三方法（`[Conditional("ENABLE_PROFILERLOG")]` 守卫，与 IGGProfiler 共用开关），`BundleFileProvider` / `PlayAssetDeliveryBundleProvider`（OnPackReady 主路径 + TryLoadFromBundleStore hot-update 路径）三处接入 size 日志，输出 `HC.BundleSize_<bundle>: X KB source={disk|pad|store}`，关联 `HC.BundleIO_*` 可识别"小 bundle 大 IO"（worker 被挤）vs"大 bundle 大 IO"（数据量本身大）。**(2) catalog Verbose alloc hotfix（D2 hotfix #3）**：VipTimeBoxItem 53 dep 的 `HC.Load.Stage.Catalog_*` 段 0.662ms 比 MainChat 8 dep 的 0.07ms 慢 10×（与 deps 数量比线性），根因是 `LocalContentCatalog.TryGetLocations` 在 `HCLogger.LogVerbose` 调用前构造 StringBuilder + for 循环拼接所有 dep InternalId——`[Conditional]` 守卫只擦除调用点 + 参数表达式（`sb.ToString()`），但前置 7 行独立语句不擦除，关 `HYPERCONTENT_LOG_VERBOSE` 后仍按 deps 数量 O(n) 浪费时间。修复：整段（含 `if (count > 0)` 判断）用 `#if HYPERCONTENT_LOG_VERBOSE` 包。预期下次采数 catalog 段降到 ~0.05ms。这是 D2 落地后第三处埋点设计失误（前两处分别是 5/21 Provide End 时机污染、PAD BundleIO 缺失），都属于"`[Conditional]` 边界认知偏差"——前置 statements 必须显式 `#if` 或包到方法体内才能完整擦除。**(3) Extract 子段拆分（D2 P1）**：`BundleAssetExtractor.Provide` 在外层 `HC.Extract_<asset>` 内插入 Issue / Wait / Complete 三段——Issue 段是 `bundle.LoadAssetAsync` 同步入队（< 1ms），Wait 段是从 issue 完成到 `request.completed` 回调入口的黑盒大头（worker 反序列化 + GPU upload + 帧延迟，预期占整段 95%+），Complete 段是回调内同步段（< 1ms）。跨 lambda 段 `#if ENABLE_PROFILERLOG` 包；成功 / 失败两路径各自独立 EndSample（不能依赖 lambda 末尾统一收，因为 `handle.Complete` 后有 return）。数据 → 决策映射：Wait 占 95%+ 时框架侧无优化空间，要 Resource 侧瘦身（拆 atlas / 降图压缩 / OPT-A 预热 bundle 后单独评估）。**(4) Instantiate Track 拆分**：把 `_instanceRegistry.Track` 包进外层 `HC.Instantiate_*` 总段（之前只包 `Object.Instantiate` 一行），新增 `HC.Instantiate.UnityInst_<addr>` 子段紧贴 `Object.Instantiate` 调用前后——目的是让"业务视角的实例化总成本"语义完整 + 让 Track 意外开销（dictionary rehash / weakref 注册）变可观测。两个实现重载（parent + worldSpace / position + rotation + parent）一起改，跨 lambda 段 `#if ENABLE_PROFILERLOG` 包。**进一步拆分 UnityInst 内部（反序列化 vs Awake 链）超出框架埋点能力**——`Object.Instantiate` 是黑盒同步 API，必须用 Unity Profiler attach 看 Player Marker 或测试侧控制变量 baseline（空 prefab vs 业务 prefab）差值法分离，归到 D3 测试矩阵范畴。**整体改动 6 文件 / +126 行（注释为主）**。**学到的教训**：(a) `[Conditional]` 边界要反复确认——只擦调用点 + 参数表达式，前置语句不擦；同一类失误已三连发，以后所有"调用前构造日志参数"的场景必须默认整段 `#if` 包；(b) 同开关 vs 独立宏的设计要看耦合关系——元数据日志和性能 sample 强相关（开 ENABLE_PROFILERLOG 跑测试时两者一起看），单开关比新增宏更友好。本批改动是 D3 测试矩阵采数前的数据基础设施，下一步业务侧用 VipTimeBoxItem.prefab 重跑现有 D2 场景验证 4 项预期数据变化（catalog 段降 10×、BundleSize 关联 BundleIO、Extract 三段总和 ≈ 外层 ±2%、Instantiate Track < 1ms），通过后启动 D3 测试矩阵设计与采数。详见 [`CHANGELOG.md` — 2026-05-22 D3 前置条目](./CHANGELOG.md)
- 2026-05-21：D2 hotfix 合并条目（同一天发现的两个 D2 初版设计盲区，分两次落地，文档归并避免碎片化）。**子项 1（Provide End 时机修正）**：SevenDayRewardWindow 真机实测发现 `HC.Load.Stage.Provide_*` (293ms) 比 `HC.Load_*` (192ms) 还大 100+ ms，违反 `Catalog + Schedule + Provide ≈ Load` 等式。根因：D2 初版用 `op.OnCompleted += _ => EndSample(provideSample)` 订阅 Provide End，但订阅时机是 OnCompleted 链最末——晚于业务侧 ContentHandle 上注册的 `_LoadWindowPrefab → InstantiateWindow → UIControllerBase.Init/Prepare/Show` 整段实例化回调（实测 100+ ms），`.NET MulticastDelegate` 按 += 顺序触发导致 Provide stopwatch 被业务回调链时间污染。修复：`AsyncOperationBase` 加 `internal string _provideSampleName`（#if 包），`SetSucceeded` / `SetFailed` 头部主动 EndSample 后再 InvokeCompleted；`ResourceManager.ExecuteImmediate` 把 `op.OnCompleted += ...` 改为 `op._provideSampleName = provideSample`。改动 2 文件 / ~26 行。**子项 2（PAD 路径 BundleIO 埋点补齐）**：MainChatWindow 数据复盘时发现日志里完全没有 `HC.Resolve_*` / `HC.BundleIO_*`，但 105ms Schedule 段里依赖 bundle mmap 必然非 0。根因：D2 初版只在 `BundleFileProvider.Provide` 包了 BundleIO 埋点，但 Android 真机开 `GOOGLE_PLAY_ASSET_DELIVERY` 宏后 PAD provider 替代 BundleFileProvider 作主路径，PAD 内部 `OnPackReady` + `TryLoadFromBundleStore` 两条 `_bundleLoader.LoadFromFileAsync` 都没埋点——bundle IO wall clock 全部隐身叠到 Schedule 段。修复：`PlayAssetDeliveryBundleProvider` 两条路径都补 `HC.BundleIO_<internalId>`（与 BundleFileProvider 同名），EndSample 放在 lambda 入口最早处兼容 fallback 重试。不另加 `HC.Resolve_*`（PAD 路径解析语义已被 `HC.PAD.Pack_*` + 同步几微秒的 `ResolvePackName`/`GetAssetLocation` 覆盖）。改动 1 文件 / ~18 行。**两个子项合并预期**：(1) `Catalog + Schedule + Provide ≈ Load` 等式 ≤ ±5%（Provide ≈ Extract 同量级）；(2) Schedule 段大头被 `HC.BundleIO_*` 拆出明细，bundle IO 占多少 / Pump 拆帧延迟占多少一目了然，后续 A1 / OPT-1 / OPT-3 / OPT 预热 bundle 决策才能精确判读。**学到的教训**：(a) 相同思路（OnCompleted += EndSample）的埋点都有时序污染风险，以后类似设计要先确认这个 EndSample 是不是 OnCompleted 链上最后注册的；(b) 多 provider 共用同一 `ProviderId` 时（PAD 替代 BundleFileProvider 作主路径），埋点必须按 provider 注册图谱列表确认每个主路径都覆盖到，不能假设 BundleFileProvider 一定执行。详见 [`CHANGELOG.md` — 2026-05-21 D2 hotfix 合并条目](./CHANGELOG.md)
