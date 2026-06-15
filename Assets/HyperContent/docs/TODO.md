# HyperContent — Future Work & TODO

Tracked items for future implementation or documentation. Update this file when items are completed or deprioritized.

---

## 移动端加载提速优化路线图（P0 / P1 / P2）

> 目标：让"加载 catalog → 读取 key → 加载依赖 AB → 实例化资源"全链路每一步都更快、更稳。约束：Android 已上线 Google PAD（InstallTime），优化不得破坏 PAD 通路。
>
> 详细分析与改造方案见 `plans/hypercontent_移动端加载提速建议_a56d05c9.plan.md`。

### P0（单点收益高、改动可控）

| ID | 项目 | 状态 |
|----|------|------|
| P0-1 | `ResourceManager.Pump` 增加帧时间预算 + `maxOpsPerFrame`，杜绝瞬时长帧 | 待开 |
| P0-2 | `HttpBundleTransport` 改用 `DownloadHandlerFile` 流式落盘 + 移除 `RemoteBundleProvider` 的 `LoadFromMemory` fallback | 待开 |
| P0-3 | `HttpBundleTransport` 同 URL 二次请求必须回调 `onComplete`，杜绝调用方挂起 | 待开 |
| P0-4 | 业务侧引入 Prefab Pool + `ShaderVariantCollection.WarmUp`，消除首次实例化卡顿 | 待开 |
| P0-5 | `CatalogLocator` / `LocalContentCatalog` 解析与 cache 构建切到后台线程，与登录并行 | 待开 |
| **P0-6** | **Catalog 三轨序列化（Json / Binary HCB1 / BinaryGzip），settings.json 驱动、对称设计、可扩展** | **本次完成** |

### P1（链路深度优化）

| ID | 项目 | 状态 |
|----|------|------|
| P1-1 | HTTP Range / 断点续传 + ETag（与 P0-2 同管线扩展） | 待开 |
| P1-2 | 自适应并发 + `RuntimeSettings` 暴露 `maxConcurrentDownloads` / `maxBytesInFlight` | 待开 |
| P1-3 | facade 增加 `PrewarmAsync` API + 业务侧 idle 帧预热常用 UI/角色 | 待开 |
| P1-4 | `LocalContentCatalog.TryGetLocations` 缓存 `ResourceLocation` 减少 GC alloc | 待开 |
| P1-5 | 同 bundle 多 asset 合并加载，减少 `AssetBundleRequest` 次数 | 待开 |

### P2（长期 / 视情况而定）

| ID | 项目 | 状态 |
|----|------|------|
| P2-1 | PAD 增加 FastFollow / OnDemand 通道，缩小 InstallTime 包体 | 待开 |
| P2-2 | `DependencyAnalyzer` / `AddressableGroupingStrategy` 收敛依赖图，base bundle 合并高频共用资源 | 待开 |
| P2-3 | 关键节点埋点 + 上报 P50/P95 加载耗时与命中率指标 | 待开 |

### P0-6 已完成内容（本次实施）

> 设计原则：**写入端按 `BuildConfig.catalogFormat` 选格式；读取端按 `RuntimeSettings.catalogFormat` 选格式。两边各自直接进入对应分支，零 magic 探测、零 fallback。**

- 新增 `Runtime/Catalog/CatalogSerializationFormat.cs` — 三值枚举（Json / Binary / BinaryGzip），扩展点预留。
- 新增 `Runtime/Catalog/CatalogBinaryReader.cs` — HCB1 反序列化（`Read` + `PeekCatalogHash`），仅处理"未压缩"二进制，与压缩算法解耦。
- 新增 `Editor/Build/CatalogBinaryWriter.cs` — HCB1 序列化（与 Reader 字段顺序严格对称）。
- `Runtime/Core/RuntimeSettings.cs` 增加 `int catalogFormat` 字段，从 `settings.json` 读取。
- `Runtime/Core/HyperContentPaths.cs` 增加 `LoadBytes` 方法（Android StreamingAssets 兼容，与 `LoadText` 对称）。
- `Runtime/Catalog/LocalContentCatalog.cs`：
  - 新增 `Initialize(string source, CatalogSerializationFormat format)` 重载，按 format 分发到 JsonUtility / CatalogBinaryReader（BinaryGzip 走 GZipStream 解压后再交给 Reader）。
  - 旧 `Initialize(string source)` 接口实现保留，默认走 Json（向后兼容 `ICatalog` 接口和直接调用方）。
  - `GetCatalogHashFromJson` 改名为 `GetCatalogHashFromBytes(byte[], format)`，调用方传 format。
- `Runtime/Operations/HyperContent.cs` 的 `InitializeBundleMode` 把 `resolution.settings.catalogFormat` 透传给 `catalog.Initialize`。
- `Editor/Build/BuildContext.cs` 的 `BuildConfig` 增加 `catalogFormat` 字段（默认 Json，与现有发布行为一致）。
- `Editor/Build/CatalogGenerator.cs`：
  - `Serialize(schema, format)` / `Deserialize(bytes, format)` 改签名按 format 分发。
  - 内含私有 `GzipCompress` / `GzipDecompress`（`System.IO.Compression.GZipStream`，BCL 自带，零依赖）。
  - `GenerateCatalog` 内部用 `context.Config.catalogFormat`，hash 两轮序列化都按同一 format。
- `Editor/Build/DefaultBuildExecutor.cs` / `UpdateBuildExecutor.cs` 写 `settings.json` 时填入 `catalogFormat = (int)config.catalogFormat`；UpdateBuildExecutor 的混合 catalog 序列化也按同一 format。
- `Editor/HyperContentBuildWindow.cs` Settings 标签新增 "Catalog Format" 下拉 + 切换后弹窗提示是否立刻 Full Build；Info HelpBox 提示跨格式必须出新 APK。

#### 关键约束（运行时）

- `settings.json` 在 APK 出包时固化，**不通过 hot-update 更新**。
- Hot-update 下发的 catalog 必须与 APK 内置 `settings.catalogFormat` 一致；读取端按该值严格分发，无 fallback；不一致 → `CATALOG_INVALID_FORMAT`。
- 切换 catalog format 后必须 Full Build（构建窗口已强制弹窗提示）。

#### 二进制格式（HCB1）

- `magic = "HCB1"` (4 bytes) + `binaryFormatVersion = 1` (int32) + `schemaVersion` (int32) + 字段流。
- string 编码：`int32 length`（-1 为 null）+ UTF-8 字节；数值统一 little-endian（BinaryReader/Writer 默认）。
- 详见 `CATALOG_SCHEMA.md` § "Serialization Formats"。

#### 实测验证（2026-05-18 · Android）

> 在 `LocalContentCatalog.Initialize` 接入 `IGGProfiler`（关 `ENABLE_PROFILERLOG` 时变量声明 + 调用全部条件编译，零开销）后实测：

| Format | IO | Decompress | Deserialize | BuildLookup | Other | **Total** | 文件 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Json | 60 | — | 138 | 74 | ~340 | **612 ms** | 8740 KB |
| Binary | 27 | — | 36 | 74 | ~344 | **481 ms** | 3918 KB |
| BinaryGzip | 6 | 28 | 32 | 74 | ~350 | **490 ms** | 1470 KB |

- Deserialize 是 Json 的主要瓶颈，二进制 -74%。
- `Binary` 综合最快（本地命中场景）；`BinaryGzip` 文件最小、Total 仅慢 ~9 ms，移动端弱 CPU + 慢 IO + 远端拉取场景结论会反转。
- `BuildLookup` 74 ms 跨格式不变；未被 sample 的 "Other" ~345 ms 跨格式恒定占 Total 70%，是下一波优化真正的大头。
- 完整分析与推荐选型见 `CHANGELOG.md` § 2026-05-18 条目。
- **真机复测待补**（中低端 Android），预期 `BinaryGzip` 优势进一步放大。

---

## Next version — Low-priority background prefetch (runtime plan §5.0)

**Scope:** Expose a **public** API that enqueues remote bundle work at **`BundleDownloadPriority.Low`** so background warmup does not starve **High** (load / blocking batch) or **Normal** (update UI) traffic.

| Item | Detail | Owner |
|------|--------|--------|
| **Facade API** | e.g. `PreloadBundlesAsync(IEnumerable<string> bundleNames, …)` or `WarmupRemoteBundlesAsync` — exact name/ shape Owner0 + Owner2; **must** go through `IBundleDownloadQueue` only. | Owner0 / Owner2 |
| **Implementation** | `BundleDownloadManager` (or dedicated helper) enqueues with **Low**; progress optional; **`CancellationToken`** same semantics as batch APIs ([CONVENTIONS.md](CONVENTIONS.md) §1.6). | Owner3 |
| **Docs** | [LOAD_RELEASE_FLOW.md](LOAD_RELEASE_FLOW.md) §0, [ARCHITECTURE.md](ARCHITECTURE.md) §6.4.1 — replace “Future prefetch” row with real API. | Owner3 |

*Depends on:* stable queue + transport path (current release). *Related mid-term APIs:* `PreloadDependenciesAsync`, `GetDownloadSizeAsync` (below).

---

## API (planned — content update / preload; not Low-specific)

The following public APIs are specified in the architecture but **not yet implemented**. Implement when content-update or preload workflows are needed.

| API | Spec / Purpose | Priority |
|-----|----------------|----------|
| `PreloadDependenciesAsync(IEnumerable<string> addresses)` | Returns `ContentHandle<VoidResult>`. Preload dependency bundles for given addresses without loading assets. | Medium |
| `GetDownloadSizeAsync(string address)` | Returns `ContentHandle<long>`. Get total download size in bytes for an address (including dependencies). | Medium |
| `LoadCatalogAsync(string catalogUrl)` | Returns `ContentHandle<VoidResult>`. Hot-update: load and switch to a new catalog from URL. | Medium |

Reference: [ARCHITECTURE.md](ARCHITECTURE.md) §3.3; implementation lives in `Runtime/Operations/HyperContent.cs` and `HyperContentImpl`.

---

## Documentation (planned, not yet created)

| Document | Purpose | Priority |
|----------|---------|----------|
| `CATALOG_LIFECYCLE.md` | Catalog flow: Build → PlayerBuild → Runtime; diagrams and troubleshooting. Referenced by ARCHITECTURE §3.2. | Medium |

---

## Transport / download (deferred beyond current release)

**HTTP Range breakpoint resume:** **Not** in this release. Tracked here only; no release closure action until a future milestone.

| Item | Description | Owner |
|------|-------------|-------|
| **HTTP Range 断点续传** | `HttpBundleTransport` sends `Range` when a partial file exists; HTTP 206 vs 200; merge in queue or transport. See `HttpBundleTransport` class comment. | Owner3 |

**Current release (completed in tree):** `IBundleDownloadQueue` / `BundleDownloadQueue`; `RemoteBundleProvider` + `BundleDownloadManager` enqueue-only; batch **`CancellationToken`** + `DownloadResult.cancelled` + **`OPERATION_CANCELLED` (5009)**; catalog/hash **not** in bundle queue ([ARCHITECTURE.md](ARCHITECTURE.md) §6.4.2). Global progress: `HyperContent.RegisterDownloadQueueProgressListener` → queue.

---

## Catalog: `bundleTagFlags` / Blocking (build-time)

| Item | Description | Priority |
|------|-------------|--------|
| Richer grouping rules | Extend **which** labels or rules map to `BundleTagFlags` beyond today's `markBundleBlocking` / label `blocking` + `BundleTagFlagsFromPlan` (if product needs more tags). See [CATALOG_SCHEMA.md](CATALOG_SCHEMA.md) §2.6. | Low |

**Done in tree:** `CatalogGenerator.BuildBundleTagFlagsByBundleName` + `UpdateBuildExecutor` mixed catalog emit flags from plan + markers.

---

## Hot Update Implementation

Full task breakdown: **[HOT_UPDATE_TODO.md](HOT_UPDATE_TODO.md)**.

Key milestones:

| Milestone | Description | Status |
|-----------|-------------|--------|
| Schema: `contentLocation` field | `CatalogSchema.BundleRecordEntry.contentLocation` added, `LocalContentCatalog` updated | Done |
| Build Manifest specification | Data structure in [CONTENT_UPDATE_BUILD_FLOW.md](CONTENT_UPDATE_BUILD_FLOW.md) §1 | Done |
| SBP switch (full pipeline) | `DefaultBuildExecutor` + `UpdateBuildExecutor` → `ContentPipeline.BuildAssetBundles` (full SBP) | Done |
| Build Manifest implementation | `BuildManifest.cs`, `BuildManifestManager.cs` | Done (in tree) |
| Change detection | `ContentChangeDetector.cs` | Done (in tree) |
| Update Build executor | `UpdateBuildExecutor.cs`, mixed catalog generation | Done (in tree) |
| Runtime integration | Provider routing by `contentLocation`, `remoteBundleBaseUrl` | Done (E2E verified in project) |
| End-to-end test | Full Build → Update Build → runtime download → load | Done (manual verification; see [HOT_UPDATE_TODO.md](HOT_UPDATE_TODO.md) Phase 5) |

---

## Completed / No Longer Applicable

- **`IBundleDownloadQueue` default implementation + wiring:** `BundleDownloadQueue`, `HyperContentImpl` creates queue over `HttpBundleTransport`; `RemoteBundleProvider` and `BundleDownloadManager` use enqueue-only path; batch **Normal** vs **High** for blocking downloads.
- **Batch `CancellationToken` + merge cancel:** `BundleDownloadEnqueueOptions.CancellationToken`, `BundleDownloadQueue` per-waiter removal / last-waiter `CancelDownload`, facade + manager overloads ([CONVENTIONS.md](CONVENTIONS.md) §1.6).
