# Owner0 开发指南

Owner0 负责核心接口、规范与架构。职责范围与接口变更控制见 [OWNERS.md](OWNERS.md) 与 `.cursor/rules/owner0.md`。

## 当前状态（概要）

- **程序集**: HyperContent.Runtime、HyperContent.Editor、HyperContent.Shared；依赖方向 Editor → Runtime → Shared。
- **核心接口**: `Runtime/Core/*.cs`（含 IContentCatalog、IBundleStore、IBundleTransport、IBundleLoader、IResourceProvider、**IAssetLoader** 等）。详见 [ARCHITECTURE.md](ARCHITECTURE.md)。
- **数据结构**: `Runtime/Data/*.cs`、`Shared/*.cs`（Handle、AssetHandle、FetchResult、BundleInfo、ContentLocation、Constants 等）。详见 [SPECIFICATION.md](SPECIFICATION.md)。
- **规范**: Catalog Schema 见 RESOURCE_LOADING_SYSTEM_SPEC 与 CatalogSchemaV2.cs；命名/错误码/日志/RefCount 见 SPECIFICATION 与 Constants.cs。
- **实现**: LocalContentCatalog、LocalContentCatalogV2、ResourceProvider、AssetLoaderV2、HyperContentManager 等；构建侧见 [Editor/BUILD_SYSTEM.md](Editor/BUILD_SYSTEM.md)。

## 接口变更控制

以下内容的任何修改须经 Owner0 Code Review。具体条目与流程见 [OWNERS.md](OWNERS.md) 与 `.cursor/rules/owner0.md`：

- Catalog Schema（CatalogSchemaV2.cs，见 RESOURCE_LOADING_SYSTEM_SPEC）
- 核心接口（Runtime/Core/*.cs）
- 运行期数据结构（Runtime/Data、Shared）
- 错误码、日志字段、RefCount 规则

## 开发流程

### 对于 Owner1/2/3

1. 实现接口时严格遵循已定义的 Schema、错误码、日志字段。
2. 如需修改接口或 Schema，先提交给 Owner0 Review。
3. 测试可参考 HyperContentTest 与构建/运行时文档。

### 对于 Owner0

1. Code Review：审查所有接口与 Schema 变更。
2. 规范维护：更新 SPECIFICATION、ARCHITECTURE、OWNERS 等，保持一致。
3. 架构演进：保持向后兼容，必要时在 OWNERS 与 owner0 规则中补充审定内容。

## 测试 POC

1. **前置**: 创建测试 Bundle 并放入 `StreamingAssets/`；准备 Catalog JSON（v1 参考 SPECIFICATION，v2 由构建生成）。
2. **步骤**: Scene 中添加 HyperContentManager；挂载 HyperContentTest；配置 Catalog 与测试 key；运行查看 Console。

## 注意事项

- 未经过 Owner0 Review 不要修改接口/Schema/错误码/RefCount 规则。
- 遵循 [SPECIFICATION.md](SPECIFICATION.md) 中的命名、错误码、日志与 RefCount。

## 文件清单（Owner0 负责）

- `Runtime/Core/*.cs` - 所有接口定义（含 IAssetLoader.cs）
- `Runtime/Data/*.cs` - 数据结构
- `Shared/*.cs` - 共享定义
- `Runtime/Catalog/CatalogSchemaV2.cs` - Catalog Schema 定义
- `SPECIFICATION.md`、`ARCHITECTURE.md` - 规范与架构

实现文件归属见 [OWNERS.md](OWNERS.md) 各 Owner 章节。
