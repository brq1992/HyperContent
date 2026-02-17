# HyperContent

类似 Addressables 的资源管理系统，支持 Catalog（GUID/Name 加载）与构建期校验。

## 文档索引

| 文档 | 说明 |
|------|------|
| [SPECIFICATION.md](SPECIFICATION.md) | 规范：命名、错误码、RefCount、目录结构（Catalog Schema 见 RESOURCE_LOADING_SYSTEM_SPEC） |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 架构：模块划分、数据流、扩展点 |
| [OWNERS.md](OWNERS.md) | Owner 职责划分与接口变更控制 |
| [Editor/QUICK_START.md](Editor/QUICK_START.md) | 构建系统快速开始（标记资源、构建、验证） |
| [Editor/BUILD_SYSTEM.md](Editor/BUILD_SYSTEM.md) | 构建系统详细说明 |
| [RESOURCE_LOADING_SYSTEM_SPEC.md](RESOURCE_LOADING_SYSTEM_SPEC.md) | Catalog Schema、IAssetLoader、GUID/Name 加载与运行时查找 |

## 架构概览

核心接口定义在 `Runtime/Core/`：

- **IContentCatalog** - Catalog 管理
- **IBundleStore** - Bundle 存储
- **IBundleTransport** - Bundle 传输（下载/上传）
- **IBundleLoader** - Bundle 加载（Unity AssetBundle）
- **IResourceProvider** - 资源提供（对外 API）
- **IAssetLoader** - 按 GUID/Name 加载（`Load<T>(string key)`，见 RESOURCE_LOADING_SYSTEM_SPEC）

Catalog 与 Schema：见 [RESOURCE_LOADING_SYSTEM_SPEC.md](RESOURCE_LOADING_SYSTEM_SPEC.md) 与 `Runtime/Catalog/CatalogSchemaV2.cs`（schemaVersion=2）。

## 快速开始

1. **构建**：按 [Editor/QUICK_START.md](Editor/QUICK_START.md) 标记资源并构建，得到 `.catalog.json` 与 `.bundle`。
2. **运行期**：Catalog 放 `StreamingAssets/`，Bundle 同目录或按 Catalog 配置；初始化与加载示例见 [SPECIFICATION.md §6](SPECIFICATION.md)。

## 开发规范

- 接口与 Schema 变更须经 Owner0 Review，详见 [OWNERS.md](OWNERS.md)。
- 命名、错误码、日志字段见 [SPECIFICATION.md](SPECIFICATION.md) 与 `Shared/Constants.cs`。
