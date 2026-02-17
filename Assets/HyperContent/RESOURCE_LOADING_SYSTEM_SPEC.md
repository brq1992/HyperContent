# Resource Loading System – Task Specification

本文档记录「构建资源加载系统（支持 GUID 和 Name，优化性能和 GC）」的完整任务说明与需求，便于后续核对与实现。

**Owner**: Owner1 (Build Pipeline) – 构建期校验、Catalog 生成、增量热更相关构建支持。  
**接口与运行时**: IAssetLoader、运行时查找策略、Catalog 解析由 Owner0/Owner2 负责。

---

## 1. 任务目标

设计并实现一套资源加载系统，支持通过 **GUID** 或 **资源名（Name）** 作为唯一 key 进行资源加载。系统需满足：

- **高效、快速**：运行时查找尽量快，GC 尽量小。
- **唯一性保障**：构建期强校验，保证所有资源在 GUID 和 Name 上均唯一。
- **增量热更支持**：支持增量更新，资源移动/修改后仍能正确加载。

---

## 2. 资源加载系统接口设计（Owner0/Owner2）

### 2.1 API 结构

- **统一接口** `IAssetLoader`，提供 `Load<T>(string key)`，支持传入 **GUID** 或 **Name** 类型的 key。
- **运行时 key 区分**：
  - **GUID**：32 字符（无连字符的 GUID 格式）。
  - **Name**：按「名称」查找；使用 `nameHash` 的字符串，计算后查 Name 到 GUID 的映射。
- **查找逻辑**：
  - 若 key 为 **GUID**：用 GUID 查资源。
  - 若 key 为 **Name**：用 Name 算 `nameHash`，在 Name 表中查得 GUID，再按 GUID 查资源。

---

## 3. Catalog 结构设计

Catalog 文件结构（构建期由 Owner1 生成；Schema 定义需 Owner0 确认）。

### 3.1 顶层结构

| 字段 | 说明 |
|------|------|
| `schemaVersion` | 格式版本（本设计为 2） |
| `catalogName` | Catalog 名称 |
| `catalogHash` | 整包 catalog 哈希，用于热更判断是否需更新 catalog |
| `timestamp` | 构建时间戳 |
| `stringTable[]` | 字符串表，所有字符串通过索引引用 |
| `assetRecords[]` | 资源主表 |
| `nameAliases[]` | Name → GUID 映射（按 nameHash 排序，便于二分查找） |
| `bundleRecords[]` | Bundle 元信息表 |
| `guidSortedIndex[]` | 按 GUID 排序的 AssetRecord 索引，用于 GUID 二分查找 |

### 3.2 字符串表

- **stringTable[]**：存储所有字符串（如 bundleName、assetPath、资源 Name 等）。
- 其他表通过 **索引** 引用字符串，减少重复与 GC。

### 3.3 资源记录（AssetRecord）

| 字段 | 类型 | 说明 |
|------|------|------|
| `guid` | string | 资源 GUID（32 字符） |
| `bundleIndex` | int | 所在 Bundle 在 `bundleRecords` 中的索引 |
| `assetPathIndex` | int | 资源路径在 `stringTable` 中的索引 |

### 3.4 名称别名表（NameAlias）

| 字段 | 类型 | 说明 |
|------|------|------|
| `nameHash` | string | 资源名称的哈希字符串（稳定、可排序） |
| `guidIndex` | int | 对应资源在 `assetRecords` 中的索引（或通过 guid 可反查） |

- 构建期按 `nameHash` 排序，运行时二分查找。

### 3.5 Bundle 记录（BundleRecord）

| 字段 | 类型 | 说明 |
|------|------|------|
| `bundleNameIndex` | int | Bundle 名称在 `stringTable` 中的索引 |
| `bundleHash` | string | Bundle 文件哈希（contentHash，用于热更） |
| `size` | long | Bundle 大小 |
| `dependencies` | int[] | 依赖的 Bundle 索引列表 |
| `assetCount` | int | 该 Bundle 内资源数量 |

### 3.6 catalogHash 与 contentHash

- **catalogHash**：对整份 Catalog JSON（不含 catalogHash 字段）做 SHA256，用于热更时判断是否需要更新 catalog。
- **contentHash**：即 Bundle 的 `bundleHash`，对单个 bundle 文件做 SHA256，用于热更时判断是否需要下载该 bundle。

### 3.7 按需解析

- 加载时按需解析字符串，避免一次性加载全部字符串，降低内存与 GC。

---

## 4. 运行时查找策略（Owner2）

### 4.1 按 GUID 查找

1. 输入 GUID。
2. 通过 `guidSortedIndex[]` 或按 GUID 排序的 `assetRecords` 二分查找，得到 `AssetRecord`。
3. 得到 `bundleIndex`、`assetPathIndex`。
4. 加载对应 Bundle，用 `LoadAsset(assetPath)` 加载资源。

### 4.2 按 Name 查找

1. 输入 Name 字符串。
2. 计算 `nameHash(Name)`。
3. 在 `nameAliases[]` 中二分查找 `nameHash`，得到 `guidIndex`。
4. 用该 GUID 再走「按 GUID 查找」流程。

### 4.3 依赖链展开

- 加载资源前，先按依赖关系拓扑排序，再按顺序加载依赖的 Bundle，最后加载目标 Bundle。

### 4.4 性能要求

- `guidIndex[]` / 按 GUID 的查找：**O(log n)** 二分查找。
- `nameAliases[]`：**O(log n)** 二分查找。
- 尽量使用 stringTable **索引**，避免重复解析字符串，减少 GC。

---

## 5. 构建期强校验（Owner1）

### 5.1 唯一性校验

- **GUID 唯一性**：每个资源的 GUID 在 Catalog 内唯一。
- **Name 唯一性**：每个资源的 Name（如 assetKey 用作 Name）在 Catalog 内唯一。
- **Name 哈希冲突检查**：同一 `nameHash` 若对应多个不同资源，构建失败并报错（含冲突资源信息）。

### 5.2 资源可打包校验

- 确保所有参与构建的资源可被打包并可被加载，类型符合预期。

### 5.3 依赖关系校验

- 校验资源间、Bundle 间依赖正确，无循环依赖或非法引用。

---

## 6. 热更支持（增量打包）

### 6.1 资源移动

- 资源目录或归属 Bundle 变化时，构建增量包并更新对应 `contentHash`（Bundle 哈希）。

### 6.2 资源修改

- 资源内容或依赖变化时，更新受影响 Bundle 的 `contentHash`，必要时更新依赖的 Bundle 列表。

### 6.3 增量更新逻辑（Owner3 实现）

- **Catalog 是否更新**：用 `catalogHash` 判断。远端拉取 catalog JSON 后解析得到 `catalogHash`，与本地缓存 catalog 的 `catalogHash` 比较；若相同则使用缓存、不覆盖；若不同或无缓存则保存新 catalog 并作为当前远程 catalog。
- **Bundle 是否下载**：用 `contentHash`（即 v2 的 `bundleHash`）判断。对每个远端 bundle：若本地不存在则需下载；若存在则调用 `IBundleStore.VerifyHash(bundleName, remoteContentHash)`，仅当 hash 一致视为已最新，否则需重新下载。
- **本地缓存**：使用 `bundleName + hash` 命名文件，便于判断是否已下载、是否失效。

---

## 7. 高效的数据存储与解析

### 7.1 表结构

- **assetRecords[]**：GUID、bundleIndex、assetPathIndex。
- **nameAliases[]**：nameHash、对应 GUID 或 guidIndex。
- **bundleRecords[]**：bundle 基本信息及依赖。
- **stringTable[]**：所有字符串集中存储，其余表用索引引用。

### 7.2 按需解析

- 运行时按需从 stringTable 解析字符串，避免一次性加载全部，减少内存与 GC。

---

## 8. 任务总结（核对用）

| # | 项 | 负责 | 状态 |
|---|----|------|------|
| 1 | 设计并实现 IAssetLoader，支持 GUID/Name 加载 | Owner0/Owner2 | 待实现 |
| 2 | 实现 Catalog 结构（AssetRecord、NameAlias、BundleRecord、stringTable） | Owner0 Schema + Owner1 生成 | 已实现（CatalogSchemaV2 + CatalogGenerator.GenerateCatalogV2） |
| 3 | 运行时二分查找（guid 表、nameAlias 表） | Owner2 | 待实现 |
| 4 | 构建期强校验（GUID/Name 唯一、nameHash 冲突检查） | Owner1 | 已实现（BuildValidator：ValidateGuidUniqueness、ValidateNameHashCollision） |
| 5 | 增量热更（contentHash、catalogHash、增量打包） | Owner1 构建 + Owner3 更新流程 | 已实现：构建侧 catalogHash/bundleHash；Owner3 用 catalogHash 判断 catalog 是否更新、contentHash 判断 bundle 是否下载 |
| 6 | 性能与 GC：二分查找、按需解析、stringTable 索引 | Owner2/Owner3 | 待实现 |

---

## 9. 参考

- `Assets/HyperContent/OWNERS.md` – Owner 职责划分  
- `Assets/HyperContent/Editor/BUILD_SYSTEM.md` – 构建系统说明  
- `.cursor/rules/owner1.md` – Owner1 规则  

**Schema 变更**：本 Catalog 结构为 schemaVersion=2，正式字段与类型以 Owner0 审定为准。

**已实现文件（Owner1）**：
- `Assets/HyperContent/Runtime/Catalog/CatalogSchemaV2.cs`：v2 数据结构（Owner0 可审定）。
- `Assets/HyperContent/Shared/NameHashUtil.cs`：稳定 NameHash（SHA256 64 位 hex），供构建与运行时区分 Name 查找。
- `Assets/HyperContent/Editor/Build/BuildValidator.cs`：GUID 唯一性、Name 唯一性（原有）、nameHash 冲突检查。
- `Assets/HyperContent/Editor/Build/CatalogGenerator.cs`：生成单份 catalog，输出 `{catalogName}.catalog.json`（v2 格式），含 catalogHash、bundleHash（contentHash）。
