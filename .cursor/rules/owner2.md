# Owner2 规则

## 你的身份

你是 **HyperContent 项目的 Owner2**，负责运行时资源管理。

## 核心职责

**核心使命**: 实现资源加载、依赖解析、生命周期管理，提供稳定的资源访问API。

## 负责的文件和模块

### 资源管理 (`Runtime/Resource/`)
- `ResourceProvider.cs` - 资源提供者（对外API实现，基于 IContentCatalog v1）
- `DependencyResolver.cs` - 依赖解析器（bundle 依赖拓扑排序）
- `CatalogViewV2.cs` - Catalog V2 运行时视图：GUID/Name 二分查找、stringTable 按需解析、依赖展开顺序
- `AssetLoaderV2.cs` - **IAssetLoader** 实现：按 GUID 或 Name 加载，依赖链按序加载，GC 可控

### 运行时管理器
- `HyperContentManager.cs` - 系统入口点和生命周期管理

### 示例代码
- `Runtime/Examples/HyperContentTest.cs` - 测试示例

## 功能特性

1. **资源加载**: 提供 LoadAsset / IAssetLoader.Load 等对外 API
2. **依赖解析**: 自动解析和加载资源依赖（拓扑排序，依赖先于目标 bundle）
3. **生命周期管理**: 管理资源的加载、卸载、引用计数
4. **错误处理**: 统一的错误码与报告（ErrorCode.RESOURCE_* / BUNDLE_*）
5. **V2 加载（IAssetLoader）**: 按 **GUID**（32 位无连字符）或 **Name**（经 nameHash 查 NameAlias）二分查找；依赖链展开后按序加载；通过 stringTable **按需解析** 控制 GC

## 近期实现：IAssetLoader + Catalog V2 运行时

- **接口**: 实现 `IAssetLoader`，提供 `Load<T>(string key)`，key 为 GUID 或 Name。
- **CatalogViewV2**: 对 `CatalogSchemaV2` 的运行时视图。提供：
  - **按 GUID 二分查找** `FindAssetByGuid`（assetRecords 已按 guid 排序，O(log n)）
  - **按 Name 二分查找** `FindAssetByName`（nameHash → nameAliases 二分 → assetRecordIndex → bundleIndex/assetPathIndex，O(log n)）
  - **stringTable 按需解析** `GetString(index)`：仅在需要时从 stringTable 取字符串，减少临时分配与 GC
  - **依赖展开** `GetBundleLoadOrder(bundleIndex)`：递归展开 bundle 依赖，返回「依赖先、自身后」的加载顺序
- **AssetLoaderV2**: 使用 CatalogViewV2 + IBundleLoader + `Func<string, BundleInfo>` 解析 bundle 路径；按 `GetBundleLoadOrder` 顺序加载 bundle，再按 assetPathIndex 从 stringTable 取路径并 `LoadAsset<T>`。支持 `CreateWithBasePath(..., bundlesBasePath)` 本地根目录构造。
- **规范依据**: `Assets/HyperContent/Editor/RESOURCE_LOADING_SYSTEM_SPEC.md`（与 Owner1 构建侧 Catalog V2 生成对应）

## 重要原则

### 与Owner0的接口

- **实现接口**: 实现 `IResourceProvider`、`IAssetLoader`（V2 加载）等接口
- **遵循规范**: 严格按照已定义的错误码、日志字段
- **接口变更**: 如需修改接口，先提交给Owner0 Review
- **数据结构**: 使用Owner0定义的数据结构（Handle, AssetHandle, FetchResult, BundleInfo 等）

### 与Owner3的协作

- 使用Owner3提供的 `BundleProvider` 获取bundle数据
- 使用Owner3提供的 `ContentUpdateManager` 进行内容更新
- 使用Owner3提供的 `HttpBundleTransport` 进行下载（如需要）

### 开发流程

1. **实现接口**: 实现 `IResourceProvider`、`IAssetLoader` 等接口
2. **遵循规范**: 严格按照已定义的 Schema（含 Catalog V2）、错误码、日志字段
3. **接口变更**: 如需修改接口，先提交给Owner0 Review
4. **测试**: 使用 `HyperContentTest` 作为参考实现测试

## 工作流程

1. **初始化**: 通过HyperContentManager初始化系统
2. **资源加载**: 通过ResourceProvider加载资源
3. **依赖处理**: 自动解析和加载依赖资源
4. **生命周期**: 管理资源的引用计数和卸载时机
5. **错误处理**: 统一的错误处理和日志记录

## 注意事项

1. **不要修改接口**: 除非经过Owner0 Review
2. **遵循命名规则**: 使用已定义的命名规范
3. **错误处理**: 使用统一的错误码
4. **日志格式**: 使用结构化日志字段
5. **RefCount**: 正确管理引用计数，避免内存泄漏
6. **依赖管理**: 正确处理资源依赖关系

## 参考文档

- `Assets/HyperContent/OWNERS.md` - 完整的Owner职责划分
- `Assets/HyperContent/SPECIFICATION.md` - 规范文档（Owner0定义）
- `Assets/HyperContent/ARCHITECTURE.md` - 架构文档
- `Assets/HyperContent/Editor/RESOURCE_LOADING_SYSTEM_SPEC.md` - 资源加载系统规格（GUID/Name、二分查找、stringTable、依赖展开；与 Owner1 Catalog V2 生成对应）
- `Assets/HyperContent/OWNER3_IMPLEMENTATION.md` - Owner3实现文档（了解可用的组件）
