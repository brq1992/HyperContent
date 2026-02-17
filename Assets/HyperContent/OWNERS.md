# HyperContent 项目 Owner 职责划分

本文档定义了HyperContent项目中4个Owner的职责范围和权限边界，用于指导开发工作和Agent配置。

## Owner 概览

| Owner | 职责范围 | 核心使命 |
|-------|---------|---------|
| **Owner0** | 核心接口、规范、架构 | 定义和维护系统架构、接口规范、数据结构 |
| **Owner1** | Build Pipeline | 实现资源构建和Catalog生成工具 |
| **Owner2** | 运行时资源管理 | 实现资源加载、依赖解析、生命周期管理 |
| **Owner3** | 内容更新与传输 | 实现下载、缓存、更新流程、环境配置 |

---

## Owner0: 核心接口与规范

### 职责范围

**核心使命**: 定义和维护系统架构、接口规范、数据结构，确保系统的一致性和可扩展性。

### 负责的文件和模块

#### 核心接口定义 (`Runtime/Core/`)
- `IContentCatalog.cs` - Catalog管理接口
- `IBundleStore.cs` - Bundle存储接口
- `IBundleTransport.cs` - Bundle传输接口
- `IBundleLoader.cs` - Bundle加载接口
- `IResourceProvider.cs` - 资源提供接口（对外API）
- `IAssetLoader.cs` - 按 GUID/Name 加载接口（`Load<T>(string key)`，见 RESOURCE_LOADING_SYSTEM_SPEC）
- `EnvironmentProfile.cs` - 环境配置管理

#### 数据结构 (`Runtime/Data/`, `Shared/`)
- `ContentLocation.cs` - 内容位置枚举
- `BundleInfo.cs` - Bundle信息
- `Handle.cs` - 异步操作句柄
- `FetchResult.cs` - 获取结果
- `AssetHandle.cs` - 资产句柄
- `InstanceHandle.cs` - 实例句柄
- `Constants.cs` - 错误码、日志字段、命名规则

#### 规范文档
- `SPECIFICATION.md` - 完整规范文档（通用规范：命名、错误码、RefCount、目录结构）
- `ARCHITECTURE.md` - 架构设计文档
- `Runtime/Catalog/CatalogSchemaV2.cs` - Catalog Schema 定义（schemaVersion=2，详见 RESOURCE_LOADING_SYSTEM_SPEC）

### 接口变更控制

**重要**: 以下内容的任何修改必须经过Owner0 Code Review:

1. **Catalog Schema** (`CatalogSchemaV2.cs`，见 RESOURCE_LOADING_SYSTEM_SPEC)
   - 字段增减、类型变更、格式变更

2. **核心接口** (`Runtime/Core/*.cs`)
   - 方法签名、方法增减、接口继承关系

3. **运行期数据结构** (`Runtime/Data/*.cs`, `Shared/*.cs`)
   - 类/结构体字段、枚举值、序列化格式

4. **错误码** (`Shared/Constants.cs` - ErrorCode)
   - 错误码值、错误码范围分配

5. **日志字段** (`Shared/Constants.cs` - LogFields)
   - 字段名称、字段语义

6. **RefCount规则** (文档和实现)
   - 引用计数策略、卸载时机

### 开发流程

1. **Code Review**: 审查所有接口和Schema变更
2. **规范维护**: 更新规范文档，确保一致性
3. **架构演进**: 根据需求演进架构，但保持向后兼容

### 与其他Owner的协作

- **Owner1/2/3**: 如需修改接口，必须先提交给Owner0 Review
- **Owner1/2/3**: 必须严格按照已定义的Schema、错误码、日志字段实现

---

## Owner1: Build Pipeline

### 职责范围

**核心使命**: 实现资源构建和Catalog生成工具，将Unity工程资源转换为可被runtime消费的bundles和catalog。

### 负责的文件和模块

#### 构建系统 (`Editor/Build/`)
- `HyperContentBuilder.cs` - 构建器主类
- `AssetCollector.cs` - 资源收集器
- `DependencyAnalyzer.cs` - 依赖分析器
- `BundleBuilder.cs` - Bundle构建器
- `CatalogGenerator.cs` - Catalog生成器
- `BuildValidator.cs` - 构建验证器
- `BuildContext.cs` - 构建上下文
- `BuildReport.cs` - 构建报告
- `BuildReportGenerator.cs` - 报告生成器

#### Editor工具 (`Editor/`)
- `HyperContentAsset.cs` - 资源标记ScriptableObject
- `HyperContentBuildMenu.cs` - 构建菜单
- `HyperContentBuildWindow.cs` - 构建窗口

#### 文档
- `Editor/BUILD_SYSTEM.md` - 构建系统文档
- `Editor/QUICK_START.md` - 快速开始指南

### 功能特性

1. **资源标记**: ScriptableObject标记和目录约定两种方式
2. **依赖分析**: 自动分析资源之间的依赖关系
3. **Bundle构建**: 使用Unity AssetBundle构建系统
4. **Catalog生成**: 严格遵守schemaVersion=1格式
5. **构建校验**: 重复key检查、非法key检查、缺失资源检查
6. **构建报告**: 详细的bundle大小统计和依赖分析

### 与Owner0的接口

- **严格遵守**: Owner0定义的catalog schema（schemaVersion=1）
- **接口变更**: 任何对catalog schema的修改必须先提案给Owner0
- **确认机制**: 收到Owner0的confirm指令后才能合并

### 开发流程

1. **实现接口**: 实现构建和生成功能
2. **遵循规范**: 严格按照Owner0定义的Schema格式
3. **接口变更**: 如需修改Schema，先提交给Owner0 Review
4. **测试**: 确保生成的Catalog符合规范

---

## Owner2: 运行时资源管理

### 职责范围

**核心使命**: 实现资源加载、依赖解析、生命周期管理，提供稳定的资源访问API。

### 负责的文件和模块

#### 资源管理 (`Runtime/Resource/`)
- `ResourceProvider.cs` - 资源提供者（对外API实现）
- `DependencyResolver.cs` - 依赖解析器

#### 运行时管理器
- `HyperContentManager.cs` - 系统入口点和生命周期管理

#### 示例代码
- `Runtime/Examples/HyperContentTest.cs` - 测试示例

### 功能特性

1. **资源加载**: 提供LoadAsset等对外API
2. **依赖解析**: 自动解析和加载资源依赖
3. **生命周期管理**: 管理资源的加载、卸载、引用计数
4. **错误处理**: 统一的错误处理和报告

### 与Owner0的接口

- **实现接口**: 实现 `IResourceProvider` 接口
- **遵循规范**: 严格按照已定义的错误码、日志字段
- **接口变更**: 如需修改接口，先提交给Owner0 Review

### 与Owner3的协作

- 使用Owner3提供的 `BundleProvider` 获取bundle数据
- 使用Owner3提供的 `ContentUpdateManager` 进行内容更新

---

## Owner3: 内容更新与传输

### 职责范围

**核心使命**: 实现内容更新、缓存和传输相关的核心功能，让内容能"线上更新、可回退、可缓存、可诊断"。

### 负责的文件和模块

#### Bundle传输 (`Runtime/Bundle/`)
- `HttpBundleTransport.cs` - HTTP/HTTPS下载器
- `BundleProvider.cs` - Bundle提供者（组合接口）

#### Bundle存储 (`Runtime/Bundle/`)
- `LocalBundleStore.cs` - 本地Bundle存储（增强版，包含缓存管理）
- `UnityBundleLoader.cs` - Unity AssetBundle加载器

#### Catalog管理 (`Runtime/Catalog/`)
- `RemoteContentCatalog.cs` - 远程Catalog实现
- `ContentUpdateManager.cs` - 内容更新管理器

#### 环境配置 (`Runtime/Core/`)
- `EnvironmentProfile.cs` - 环境配置管理（Owner3负责实现部分）

#### 文档
- `OWNER3_IMPLEMENTATION.md` - Owner3实现文档

### 功能特性

1. **Downloader**: HTTP/HTTPS下载，超时/重试/并发控制
2. **Cache**: 原子写入、防损坏、hash校验、LRU清理策略
3. **Catalog拉取与缓存**: Remote Catalog覆盖策略
4. **更新流程**: Catalog diff → 下载缺失bundles
5. **CDN/环境profile**: dev/staging/prod的baseUrl配置
6. **BundleProvider**: store/transport/loader的组合

### 与Owner0的接口

- **实现接口**: 实现 `IBundleStore`, `IBundleTransport`, `IBundleLoader` 等接口
- **遵循规范**: 严格按照已定义的Schema、错误码、日志字段
- **接口变更**: 如需修改接口，先提交给Owner0 Review

### 与Owner2的协作

- 为Owner2提供 `BundleProvider` 接口，用于获取bundle数据
- 提供 `ContentUpdateManager` 用于内容更新

---

## Owner 协作原则

### 1. 接口变更流程

```
Owner1/2/3 需要修改接口
    ↓
提交给 Owner0 Review
    ↓
Owner0 确认/拒绝/建议修改
    ↓
Owner0 更新规范文档
    ↓
Owner1/2/3 实现变更
```

### 2. 文件修改权限

- **Owner0**: 可以修改所有文件，但主要负责核心接口和规范
- **Owner1**: 主要负责 `Editor/` 目录下的构建相关文件
- **Owner2**: 主要负责 `Runtime/Resource/` 和 `HyperContentManager.cs`
- **Owner3**: 主要负责 `Runtime/Bundle/`, `Runtime/Catalog/` 相关文件

### 3. 跨Owner协作

- 修改其他Owner负责的文件前，应先沟通
- 接口变更必须经过Owner0 Review
- 遵循统一的错误码、日志格式、命名规范

---

## Agent 配置指南

当配置新的Agent时，请参考以下方式设置Owner身份：

### 方式1: 在Agent配置中指定Owner

在Agent的配置文件中添加：

```yaml
owner: Owner0  # 或 Owner1, Owner2, Owner3
responsibility: "核心接口与规范"  # 对应的职责描述
```

### 方式2: 在系统提示中说明

在Agent的系统提示中包含：

```
你是HyperContent项目的Owner0，负责核心接口定义和规范维护。
请参考 Assets/HyperContent/OWNERS.md 了解你的职责范围。
```

### 方式3: 使用Cursor Rules

在 `.cursor/rules/` 目录下创建对应的规则文件，例如：
- `.cursor/rules/owner0.md` - Owner0的规则
- `.cursor/rules/owner1.md` - Owner1的规则
- `.cursor/rules/owner2.md` - Owner2的规则
- `.cursor/rules/owner3.md` - Owner3的规则

---

## 快速参考

### Owner0 快速检查清单
- [ ] 修改的是核心接口吗？需要Review
- [ ] 修改的是数据结构吗？需要Review
- [ ] 修改的是错误码吗？需要Review
- [ ] 更新了规范文档吗？

### Owner1 快速检查清单
- [ ] 生成的Catalog符合schemaVersion=1吗？
- [ ] 需要修改Schema吗？先提交Owner0 Review
- [ ] 构建报告完整吗？

### Owner2 快速检查清单
- [ ] 实现了IResourceProvider接口吗？
- [ ] 使用了统一的错误码吗？
- [ ] 资源生命周期管理正确吗？

### Owner3 快速检查清单
- [ ] 实现了对应的接口吗？
- [ ] 缓存策略正确吗？
- [ ] 更新流程完整吗？
- [ ] 环境配置支持了吗？

---

## 更新日志

- 2024-XX-XX: 初始版本，定义4个Owner的职责范围
