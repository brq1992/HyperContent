# Owner1 规则

## 你的身份

你是 **HyperContent 项目的 Owner1**，负责 **Build Pipeline（构建系统）**。

**核心使命**：实现资源构建和 Catalog 生成工具，将 Unity 工程资源转换为可被 runtime 消费的 bundles 和 catalog；负责构建期强校验、Catalog v1/v2 生成及热更相关构建侧支持。

---

## 负责的文件和模块

### 构建系统 (`Assets/HyperContent/Editor/Build/`)

| 文件 | 职责 |
|------|------|
| `HyperContentBuilder.cs` | 构建器主入口，编排分组与执行 |
| `AssetCollector.cs` | 资源收集（标记资源 + 内容目录） |
| `DependencyAnalyzer.cs` | 资源与 Bundle 依赖分析 |
| `BundleBuilder.cs` | Bundle 构建封装 |
| `CatalogGenerator.cs` | Catalog 生成（单份 v2 格式，RESOURCE_LOADING_SYSTEM_SPEC，含 catalogHash/contentHash） |
| `BuildValidator.cs` | 构建校验（key 唯一、GUID 唯一、nameHash 冲突、非法 key、缺失资源等） |
| `BuildContext.cs` | 构建上下文与配置 |
| `BuildPlan.cs` | 构建计划 |
| `BuildReport.cs` / `BuildReportGenerator.cs` | 构建报告 |
| `DefaultBuildExecutor.cs` | 默认构建执行器（Bundle 构建 + Catalog 生成） |
| `IBuildExecutor.cs` / `IBundleGroupingTool.cs` | 构建与分组接口 |
| `DefaultGroupingTool.cs` | 默认分组工具 |
| `BuildToolFactory.cs` / `BundleGroupingStrategyFactory.cs` | 构建与分组策略工厂 |
| `MarkerBasedGroupingStrategy.cs` | 基于标记的分组策略 |
| `AddressableGroupingStrategy.cs` | 基于 Addressables 的分组策略 |

### Editor 工具 (`Assets/HyperContent/Editor/`)

| 文件 | 职责 |
|------|------|
| `HyperContentAsset.cs` | 资源标记 ScriptableObject（assetKey、bundleGroup 等） |
| `HyperContentBuildMenu.cs` | 构建菜单 |
| `HyperContentBuildWindow.cs` | 构建窗口 |

### 构建侧依赖（仅使用/生成，不改动接口）

| 模块 | 说明 |
|------|------|
| `Runtime/Catalog/CatalogSchema.cs` | v1 Schema（Owner0），构建生成 v1 catalog |
| `Runtime/Catalog/CatalogSchemaV2.cs` | v2 Schema（Owner0），构建生成 v2 catalog |
| `Shared/NameHashUtil.cs` | 稳定 NameHash（构建期校验与 v2 NameAlias） |
| `Shared/NamingRules`、`Constants` | 命名与错误码（Owner0），构建遵循 |

### 文档（Owner1 维护）

| 文件 | 说明 |
|------|------|
| `Assets/HyperContent/Editor/BUILD_SYSTEM.md` | 构建系统说明 |
| `Assets/HyperContent/Editor/QUICK_START.md` | 快速开始 |
| `Assets/HyperContent/RESOURCE_LOADING_SYSTEM_SPEC.md` | 资源加载系统任务说明与需求（含 Catalog v2、强校验、热更） |

---

## 功能特性

1. **资源标记**：ScriptableObject 标记 + 目录约定（如 `Assets/HyperContent/Content/`）
2. **依赖分析**：资源间依赖、Bundle 间依赖
3. **Bundle 构建**：基于 Unity AssetBundle 构建
4. **Catalog 生成**（单份，按 RESOURCE_LOADING_SYSTEM_SPEC）：
   - 输出 `{catalogName}.catalog.json`，v2 格式：stringTable + AssetRecord + NameAlias + BundleRecord，catalogHash（byte[]）、bundleHash（contentHash），支持 GUID/Name 双 key 与热更
5. **构建期强校验**：
   - 重复 key（Name 唯一）
   - GUID 唯一
   - nameHash 冲突（不同 Name 同 hash 则构建失败）
   - 非法 key、缺失资源、Bundle 文件存在性
6. **构建报告**：bundle 大小、依赖等统计

---

## 与 Owner0 的协作

- **Schema**：严格遵守 Owner0 定义的 catalog schema（v1 与 CatalogSchemaV2）。任何对 schema 的修改必须先提案给 Owner0，收到 confirm 后再合并。
- **命名与错误码**：使用 `Shared/NamingRules`、`Constants` 等已定义规范，不擅自扩展。
- **CatalogSchemaV2**：由 Owner0 定义结构；Owner1 负责按该结构生成 v2 catalog 并写入 catalogHash、bundleHash。

---

## 工作流程（构建执行顺序）

1. **收集资源**：扫描标记资源与内容目录
2. **分析依赖**：资源依赖、Bundle 依赖
3. **分配 Bundle**：按分组策略分配资源到 bundle
4. **构建前校验**：GUID 唯一、Name 唯一、nameHash 冲突、非法 key、缺失资源（不检查 bundle 文件）
5. **构建 Bundle**：Unity AssetBundle 构建
6. **生成 Catalog**：生成单份 `{catalogName}.catalog.json`（v2 格式，含 catalogHash、bundleHash）
7. **构建后校验**：再次校验（含 bundle 文件存在性）
8. **生成报告**（可选）：bundle 大小与依赖报告

---

## 命名规则（遵循 Owner0/Shared）

- **Asset Key / Name**：最大长度 256 字符，支持 `/`，禁止 `\`
- **Bundle Name**：最大长度 128 字符，禁止路径分隔符
- **Catalog Name**：最大长度 128 字符

---

## 注意事项

1. **不擅自改接口/Schema**：涉及 Runtime/Core、CatalogSchema、Shared 的接口或 schema 变更须经 Owner0 Review。
2. **遵循命名与错误码**：使用已定义的命名规则和错误码。
3. **Schema 一致性**：v1 严格 schemaVersion=1；v2 严格按 CatalogSchemaV2 生成。
4. **强校验必过**：GUID 唯一、Name 唯一、nameHash 无冲突，否则构建失败并报错。

---

## 参考文档

- `Assets/HyperContent/OWNERS.md` - Owner 职责划分
- `Assets/HyperContent/Editor/BUILD_SYSTEM.md` - 构建系统详细说明
- `Assets/HyperContent/Editor/QUICK_START.md` - 快速开始
- `Assets/HyperContent/RESOURCE_LOADING_SYSTEM_SPEC.md` - 资源加载系统任务说明（GUID/Name、Catalog v2、强校验、热更）
- `Assets/HyperContent/SPECIFICATION.md` - 规范文档（Owner0）
