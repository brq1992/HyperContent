# HyperContent

类似 Addressables 的资源管理系统，支持 Catalog（GUID/Name 加载）、Operation DAG 异步加载与 Bundle 热更新。

## 文档索引

| 文档 | 说明 |
|------|------|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 架构：分层设计、核心抽象、类职责 |
| [docs/LOAD_RELEASE_FLOW.md](docs/LOAD_RELEASE_FLOW.md) | 运行时数据流：DAG 加载、释放、边界情况 |
| [docs/PROVIDER_FLOW.md](docs/PROVIDER_FLOW.md) | Provider 注册、执行链、PAD、远程加载 |
| [docs/INITIALIZATION_FLOW.md](docs/INITIALIZATION_FLOW.md) | 初始化流程：settings.json、CatalogLocator |
| [docs/CATALOG_SCHEMA.md](docs/CATALOG_SCHEMA.md) | Catalog Schema 设计：JSON 格式、查找算法 |
| [docs/CONVENTIONS.md](docs/CONVENTIONS.md) | 规范：命名、错误码、RefCount 规则 |
| [docs/OWNERS.md](docs/OWNERS.md) | Owner 职责划分与接口变更控制 |
| [docs/DIRECTORY_STRUCTURE.md](docs/DIRECTORY_STRUCTURE.md) | 目录结构说明 |
| [Editor/QUICK_START.md](Editor/QUICK_START.md) | 构建系统快速开始 |
| [Editor/BUILD_SYSTEM.md](Editor/BUILD_SYSTEM.md) | 构建系统详细说明 |

## 架构概览

核心接口定义在 `Runtime/Core/` 和 `Runtime/Catalog/`：

- **ICatalog** — 地址解析：Address -> ResourceLocation 树（`TryGetLocations`）
- **IBundleStore** — Bundle 本地缓存
- **IBundleTransport** — Bundle 远程下载
- **IBundleLoader** — Unity AssetBundle 加载

运行时加载通过 `HyperContent` 静态门面 -> `HyperContentImpl` -> `ICatalog` -> `ResourceManager` DAG 调度器完成。

## 快速开始

1. **构建**：按 [Editor/QUICK_START.md](Editor/QUICK_START.md) 标记资源并构建，得到 `HyperCatalog.bin` 与 `.bundle`。
2. **运行期**：

```csharp
using com.igg.hypercontent;

// 必须先显式初始化（首次 Load/Release 之前），二选一：
//   HyperContent.Initialize(pOnComplete: ok => { /* 初始化完成 */ });
//   await HyperContent.InitializeAsync();
await HyperContent.InitializeAsync();

// 回调方式
var handle = HyperContent.LoadAsync<Texture2D>("UI/Avatar");
handle.Completed += h => { if (h.IsSuccess) image.texture = h.Result; };

// 或 await 方式（ContentHandle<T> 可直接 await）
var tex = await HyperContent.LoadAsync<Texture2D>("UI/Avatar");

// 释放
HyperContent.Release(handle);
```

## 开发规范

- 接口与 Schema 变更须经 Owner0 Review，详见 [docs/OWNERS.md](docs/OWNERS.md)。
- 命名、错误码、日志字段见 [docs/CONVENTIONS.md](docs/CONVENTIONS.md) 与 `Shared/Constants.cs`。
