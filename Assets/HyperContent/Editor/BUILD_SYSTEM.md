# HyperContent 构建系统文档

## 概述

HyperContent构建系统负责将Unity工程资源转换为可被runtime消费的bundles和catalog。作为Owner1，本系统严格遵守Owner0定义的catalog schema（见 [CATALOG_SCHEMA.md](../docs/CATALOG_SCHEMA.md) 与 `Runtime/Catalog/CatalogSchema.cs`）。

## 架构设计

构建系统采用**两层架构**，将资源分组和打包执行分离，实现模块化和可扩展性：

### 架构分层

```
┌─────────────────────────────────────────────────────────────┐
│                     项目资源 (Unity Assets)                   │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ↓
┌─────────────────────────────────────────────────────────────┐
│           分组工具 (IBundleGroupingTool)                      │
│  - 收集资源 (AssetCollector)                                  │
│  - 分析依赖 (DependencyAnalyzer)                             │
│  - 分配Bundle (Grouping Strategy)                            │
│  - 生成BuildPlan                                             │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ↓
┌─────────────────────────────────────────────────────────────┐
│                    BuildPlan (构建计划)                       │
│  - AssetMarkers: 资源标记映射                                 │
│  - BundleToAssets: Bundle到资源的映射                         │
│  - Dependencies: 依赖关系                                    │
│  - BundleDependencies: Bundle依赖关系                         │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ↓
┌─────────────────────────────────────────────────────────────┐
│         打包执行器 (IBuildExecutor)                          │
│  - 验证BuildPlan                                             │
│  - 构建AssetBundles (SBP ContentPipeline)                   │
│  - 从SBP结果提取精确bundle依赖                                │
│  - 生成 Catalog（HyperCatalog.bin / CatalogGenerator）      │
│  - 生成报告 (BuildReportGenerator)                          │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ↓
┌─────────────────────────────────────────────────────────────┐
│                   最终产物                                    │
│  - Bundle文件 (*.bundle)                                     │
│  - Catalog文件 (HyperCatalog.bin)                             │
│  - 构建报告 (可选)                                           │
└─────────────────────────────────────────────────────────────┘
```

#### 1. 分组工具 (IBundleGroupingTool)

**职责**：将项目资源转换为可打包的BuildPlan

**功能**：
- 收集项目中的标记资源
- 分析资源依赖关系
- 将资源分配到不同的bundle
- 生成BuildPlan（包含完整的资源到bundle映射）

**输入**：BuildConfig（构建配置）
**输出**：BuildPlan（构建计划）

#### 2. 打包执行器 (IBuildExecutor)

**职责**：读取BuildPlan并执行Unity构建，生成最终产物

**功能**：
- 验证BuildPlan
- 调用Unity AssetBundle构建系统
- 生成 catalog（符合 CatalogSchema，见 [CATALOG_SCHEMA.md](../docs/CATALOG_SCHEMA.md)）
- 生成构建报告（可选）

**输入**：BuildPlan + BuildConfig
**输出**：Bundle文件 + Catalog文件 + 构建报告

### 架构优势

1. **模块化**：分组工具和打包执行器相互独立，可单独替换
2. **可扩展**：通过实现接口可轻松添加新的工具
3. **可配置**：在构建窗口中选择不同的工具组合
4. **隔离性**：工具之间互不干扰，便于测试和维护

### 目录结构

```
Assets/HyperContent/Editor/Build/
├── 核心接口
│   ├── IBundleGroupingTool.cs          # 分组工具接口
│   └── IBuildExecutor.cs               # 打包执行器接口
│
├── 数据结构
│   ├── BuildPlan.cs                    # 构建计划（分组工具的输出）
│   ├── BuildContext.cs                 # 构建上下文（内部使用）
│   ├── BuildConfig.cs                  # 构建配置（在BuildContext.cs中）
│   └── BuildReport.cs                 # 构建报告
│
├── 默认实现
│   ├── DefaultGroupingTool.cs          # 默认分组工具
│   └── DefaultBuildExecutor.cs         # 默认打包执行器
│
├── 工具工厂
│   └── BuildToolFactory.cs            # 工具注册和获取工厂
│
├── 核心构建器
│   └── HyperContentBuilder.cs         # 主构建器（协调工具）
│
├── 资源收集与分析
│   ├── AssetCollector.cs              # 资源收集器
│   └── DependencyAnalyzer.cs         # 依赖分析器
│
├── 分组策略（用于DefaultGroupingTool）
│   ├── IBundleGroupingStrategy.cs     # 分组策略接口
│   ├── MarkerBasedGroupingStrategy.cs # 基于标记的分组策略
│   ├── AddressableGroupingStrategy.cs  # 基于Addressable的分组策略
│   └── BundleGroupingStrategyFactory.cs # 策略工厂
│
├── 打包与生成
│   ├── CatalogGenerator.cs             # Catalog生成器
│   └── BuildValidator.cs               # 构建验证器
│
└── 报告生成
    ├── BuildReportGenerator.cs         # 报告生成器
    └── BuildReport.cs                  # 报告数据结构

Assets/Scripts/Editor/AddressableGrouping/
├── HyperContentAddressableGroupingTool.cs  # 从 Addressable 生成 HyperContent BuildPlan
└── AddressableGroupingInitializer.cs      # 注册 id "addressable"
```

### 当前支持的工具

#### 分组工具

- **DefaultGroupingTool** (ID: "default")
  - 使用HyperContentAsset markers或目录约定收集资源
  - 分析资源依赖关系
  - 使用配置的分组策略（MarkerBased或Addressable）分配bundle
  - 生成完整的BuildPlan
  - **位置**: `Assets/HyperContent/Editor/Build/DefaultGroupingTool.cs`

- **HyperContentAddressableGroupingTool** (ID: "addressable")
  - 直接从Addressable Groups读取分组数据
  - 只收集标记为Addressable的资源
  - 使用Addressable的address作为asset key
  - 按 Group 的 **Bundle Mode** 命名 bundle：`Pack Together` → 整组一包；`Pack Separately` → 每组内按根 Entry（子资源随 `ParentEntry` 链）一包；`Pack Together By Label` → 相同 Label 集合的根 Entry 共包；名称一般为 `sanitize(group)__sanitize(suffix)`，超长时带短 hash
  - 生成完整的BuildPlan
  - **位置**: `Assets/Scripts/Editor/AddressableGrouping/HyperContentAddressableGroupingTool.cs`
  - **特点**: 完全基于Addressable系统，无需HyperContentAsset markers（与工程内从规则维护 Addressable Groups 的 `AddressableGroupRulesMenu` 不同）

#### 打包执行器

- **DefaultBuildExecutor** (ID: "default")
  - 验证BuildPlan
  - 使用完整 SBP `ContentPipeline.BuildAssetBundles`（自定义 task 列表）构建 bundle
  - 从 SBP `IBundleBuildResults` 提取精确的 bundle 间依赖并覆写 `BundleDependencies`
  - 生成符合 CatalogSchema 的 catalog（HyperCatalog.bin）
  - 生成构建报告（可选）

## 功能特性

### 1. 资源标记方式

支持两种标记方式：

#### 方式1：ScriptableObject标记（推荐）
- 创建 `HyperContentAsset` ScriptableObject来标记资源
- 菜单：`Assets > Create > HyperContent > Asset Marker`
- 标记文件命名约定：
  - `AssetName_HyperContent.asset`（推荐）
  - `HyperContent_AssetName.asset`

#### 方式2：目录约定
- 将资源放在 `Assets/HyperContent/Content/` 目录下
- 系统会自动收集这些资源并生成默认的asset key
- 可以为资源创建对应的标记文件来指定自定义key和bundle group

### 2. 标记字段说明

`HyperContentAsset` ScriptableObject包含以下字段：

- **assetKey**: 运行时用于加载资源的键（必填）
- **labels**: 可选的标签列表，用于过滤和分组
- **bundleGroup**: Bundle组名，相同组的资源会被打包到同一个bundle
- **forceSeparateBundle**: 强制此资源单独打包到一个bundle

### 3. 分组策略

构建系统支持多种分组策略，用于将资源分配到不同的bundle：

#### 策略1：Marker-Based（基于标记，默认）
- 使用 `HyperContentAsset` marker 的 `bundleGroup` 字段进行分组
- 相同 `bundleGroup` 的资源会被打包到同一个bundle
- 如果设置了 `forceSeparateBundle`，资源会单独打包
- 如果没有指定 `bundleGroup`，使用 asset key 作为 bundle 名称

#### 策略2：Addressable Groups
- **从 Addressable Groups 获取分组数据**：读取资源在 Addressable Groups 窗口中的分组信息
- 资源收集方式不变（仍使用 HyperContentAsset markers 或目录约定）
- 分组依据：根据资源的 GUID 查找它在哪个 Addressable Group，使用该 Group 名称作为 bundle 名称
- 如果资源不在 Addressable Groups 中，会回退到 marker 的 `bundleGroup` 字段
- **设计理念**：Addressable 只提供分组数据，不参与资源收集

**切换分组策略**：
- 在构建窗口中选择 "Grouping Strategy" 选项
- 或在代码中设置 `BuildConfig.groupingStrategy`

### 4. 依赖分析

- 自动分析资源之间的依赖关系
- 根据依赖关系确定bundle切分策略
- 支持将依赖资源包含到同一个bundle中（可选）
- **Bundle间依赖**：构建阶段使用 `AssetDatabase.GetDependencies(path, true)` 预估；实际构建后由 SBP 的对象级依赖分析覆写，与 Unity 实际打包结果完全一致

### 5. Bundle构建

- 使用 SBP（Scriptable Build Pipeline）的完整 `ContentPipeline.BuildAssetBundles` API
- 自定义 task 列表（`CalculateAssetDependencyData` → `GenerateBundlePacking` → `WriteSerializedFiles` → `ArchiveAndCompressBundles` 等），与 Addressables 同级别的 SBP 集成
- 构建后从 `IBundleBuildResults.BundleInfos` 提取精确的 bundle 间依赖（基于 SBP 对象级依赖分析），覆写 `BuildContext.BundleDependencies`
- 支持多种压缩方式：None, Lz4, Lz4HC（通过 `BundleBuildParameters.BundleCompression` 配置）
- 自动生成bundle文件到指定输出目录

### 6. Catalog生成

- 严格遵守 CatalogSchema 格式（见 [CATALOG_SCHEMA.md](../docs/CATALOG_SCHEMA.md)、`Runtime/Catalog/CatalogSchema.cs`）
- 自动生成 catalog 文件（`HyperCatalog.bin`，UTF-8 JSON 内容；见 `HyperContentPaths.LOCAL_CATALOG_FILENAME`）
- 包含完整的asset到bundle映射和bundle元信息

### 7. 构建校验

构建系统会进行以下校验：

- **重复key检查**: 检测是否有多个资源使用相同的asset key
- **非法key检查**: 验证asset key格式是否符合规范
- **缺失资源检查**: 确保所有引用的资源文件存在
- **Bundle大小报告**: 生成详细的bundle大小统计

### 8. Build Report（可选）

生成详细的构建报告，包括：

- 总bundle数量和总大小
- 最大/最小bundle信息
- 平均bundle大小
- 重复依赖分析（哪些bundle共享依赖）
- 资源聚合信息（每个bundle包含哪些资源）

## 使用方法

### 方法1：使用构建窗口（推荐）

1. 打开 HyperContent 窗口：`HyperContent > HyperContent Window`（**Settings** = 完整构建配置；**Overview** = Play Mode、Build、Update Build 等）。
2. 配置构建参数（Settings 页）：
   - **Basic Settings**:
     - Catalog Name: catalog名称
     - Output Directory: 输出目录（见 CONVENTIONS.md §3）
   - **Build Settings**:
     - Build Target: 构建目标平台
     - Compression: 压缩方式（None, Lz4, Lz4HC）
     - Include Dependencies: 是否包含依赖资源
     - Force Rebuild: 是否强制重建所有bundle
     - Generate Report: 是否生成构建报告
   - **Grouping Tool**: 选择分组工具（当前支持"default"）
   - **Build Executor**: 选择打包执行器（当前支持"default"）
   - **Grouping Strategy**: 当使用默认分组工具时，可选择分组策略（Marker-Based 或 Addressable）
   - **Advanced / Experimental**（Overview 等页底部折叠）：可切换 **Strip Unity version from bundle headers**（`BuildConfig.stripUnityVersionFromBundleHeaders`），与 `ProjectSettings/HyperContentBuildConfig.json` 同步保存。
3. 点击"Build"按钮开始构建
4. 查看控制台输出和构建报告

5. **Content Update Build**（折叠区）：展开后可配置增量构建；其中 **Sync Addressable update groups after Update Build** 默认**关闭**。勾选后，仅在 **Run Update Build** 成功结束时，才会根据 B1 `updateMapping` 调用工程内 Addressable 同步逻辑（`AddressableBuilder.RegisterHyperContentPostUpdateAddressableSync`），在编辑器中创建/调整 Addressable Content Update 组。该选项写入 `ProjectSettings/HyperContentBuildConfig.json`。若你在代码里自行设置 `BuildConfig.onAfterUpdateBuildSucceeded`，请勿依赖该开关覆盖行为；同一 Editor 会话中取消勾选并再次运行 Update Build 会清除该委托（由 `HyperContentAddressableSyncFacade.PrepareForUpdateBuild` 处理）。

### 方法2：使用菜单

- `HyperContent > Build`: 使用默认配置构建
- `HyperContent > Build (Force Rebuild)`: 强制重建所有bundle
- `HyperContent > Validate`: 仅验证配置，不进行构建

## 工作流程

构建系统按以下步骤执行（新架构）：

### 整体流程

1. **获取分组工具**: 根据配置的`groupingToolId`获取对应的分组工具
2. **验证分组工具**: 验证分组工具是否可用
3. **生成BuildPlan**: 调用分组工具的`GeneratePlan()`方法
   - 收集资源（扫描标记资源和内容目录）
   - 分析依赖（分析资源之间的依赖关系）
   - 分配Bundle（根据分组策略分配资源到bundle）
   - 构建Bundle依赖关系
4. **获取打包执行器**: 根据配置的`buildExecutorId`获取对应的打包执行器
5. **验证打包执行器**: 验证打包执行器是否可用
6. **执行构建**: 调用打包执行器的`Execute()`方法
   - 验证BuildPlan（检查重复key、非法key、缺失资源等）
   - 构建Bundle（使用Unity AssetBundle系统）
   - 生成 Catalog（符合 CatalogSchema 的 HyperCatalog.bin）
   - 最终验证（验证构建结果）
   - 生成报告（如果启用）

### 详细步骤（DefaultGroupingTool）

**分组阶段**：
1. 收集资源：使用AssetCollector扫描项目中的标记资源和内容目录
2. 分析依赖：使用DependencyAnalyzer分析资源之间的依赖关系
3. 分配Bundle：使用配置的分组策略（MarkerBased或Addressable）分配资源
4. 构建Bundle依赖：根据资源依赖关系构建bundle之间的依赖关系

### 详细步骤（DefaultBuildExecutor）

**打包阶段**：
1. 验证BuildPlan：使用BuildValidator检查配置
2. 准备AssetBundle构建：将BuildPlan转换为 SBP `BundleBuildContent`
3. 构建AssetBundles：调用 `ContentPipeline.BuildAssetBundles`（完整 SBP 管线，自定义 task 列表）
4. 提取SBP构建产物：从 `IBundleBuildResults.BundleInfos` 读取实际 bundle 名称和依赖关系
5. 覆写BundleDependencies：用 SBP 的对象级精确依赖覆写 `BuildContext.BundleDependencies`
6. 生成 Catalog：使用 CatalogGenerator 写入 `HyperCatalog.bin`（此时 BundleDependencies 已是 ground truth）
7. 生成报告：使用BuildReportGenerator生成构建报告（如果启用）

## 输出结构

完整路径定义见 **[CONVENTIONS.md §3 Build Output & Runtime Paths](../docs/CONVENTIONS.md)**（唯一权威源）。

构建完成后输出到两个位置：

```
Assets/StreamingAssets/{Platform}/Bundles/    # Bundle文件
├── bundle1.bundle
├── bundle2.bundle
└── ...

HyperContentBuild/{Platform}/hc/             # Catalog + Settings
├── HyperCatalog.bin
└── settings.json

{remoteCatalogBuildFolder}/Production/{Platform}/ (可选，buildRemoteCatalog=true；与 GetResolvedRemoteCatalogBuildFolder 一致，默认即 ServerData/Production/{Platform}/)
├── HyperCatalog_{buildVersion}.bin
├── HyperCatalog_{buildVersion}.hash
└── Update Build 时：当次增量 update *.bundle 与上述 .bin/.hash **同目录**，整目录上传 CDN 即可
# settings.json 中 remoteCatalog* 字段为上述文件名本身，不含 hc/ 前缀（与 StreamingAssets/hc/ 本地目录无关）
```

## 命名规则

见 **[CONVENTIONS.md §2 Naming Rules](../docs/CONVENTIONS.md)**。

## 错误处理

构建过程中发现的错误会记录在BuildContext中：

- **Errors**: 阻止构建继续的错误（如重复key、非法key等）
- **Warnings**: 不影响构建的警告（如找不到关联资源等）

所有错误和警告都会在控制台输出，构建窗口也会显示摘要。

## 与Owner0的接口

### Catalog Schema

构建系统严格遵守 Owner0 定义的 catalog schema。顶层结构含 `catalogNameIndex`、`catalogHash`、`timestamp`、`stringTable`、`assetRecords`、`nameAliases`、`bundleRecords` 等，详见 [CATALOG_SCHEMA.md](../docs/CATALOG_SCHEMA.md) 与 `Runtime/Catalog/CatalogSchema.cs`。

### 接口变更控制

- 任何对 catalog schema 的修改必须先提案给 Owner0
- 收到 Owner0 确认后才能合并
- **schemaVersion** 必须以代码 `CatalogSchema.CurrentSchemaVersion` 为准（勿在文档中写死与代码不一致的数字；当前仓库为 **1**）；字段与类型以 Owner0 审定为准

## 最佳实践

1. **使用明确的asset key**: 避免使用自动生成的key，为重要资源创建明确的标记
2. **合理分组**: 使用bundleGroup将相关资源分组，减少bundle数量
3. **避免重复依赖**: 注意bundle之间的依赖关系，避免重复打包相同资源
4. **定期验证**: 在构建前使用Validate功能检查配置
5. **查看报告**: 启用构建报告，了解bundle大小和依赖关系

## 故障排除

### 问题：找不到资源
- 检查资源是否在 `Assets/HyperContent/Content/` 目录下
- 检查标记文件是否正确关联到资源文件

### 问题：重复key错误
- 检查是否有多个资源使用相同的asset key
- 确保每个资源的key是唯一的

### 问题：Bundle构建失败
- 检查输出目录是否有写入权限
- 检查资源文件是否损坏
- 查看Unity控制台的详细错误信息

## 扩展新工具

### 扩展新的分组工具

1. 实现 `IBundleGroupingTool` 接口：
   ```csharp
   public class CustomGroupingTool : IBundleGroupingTool
   {
       public string ToolName => "Custom Grouping Tool";
       public string Description => "Description of what this tool does";
       
       public BuildPlan GeneratePlan(BuildConfig config)
       {
           var plan = new BuildPlan();
           
           // 1. 收集资源（自定义逻辑）
           CollectAssets(plan, config);
           
           // 2. 分析依赖（自定义逻辑）
           AnalyzeDependencies(plan);
           
           // 3. 分配Bundle（自定义逻辑）
           AssignBundles(plan, config);
           
           // 4. 构建Bundle依赖
           BuildBundleDependencies(plan);
           
           return plan;
       }
       
       public List<string> Validate(BuildConfig config)
       {
           var errors = new List<string>();
           // 验证逻辑
           return errors;
       }
   }
   ```

2. 注册新工具（两种方式）：
   
   **方式1：使用InitializeOnLoad自动注册**（推荐）：
   ```csharp
   [InitializeOnLoad]
   public static class CustomGroupingInitializer
   {
       static CustomGroupingInitializer()
       {
           BuildToolFactory.RegisterGroupingTool("custom", new CustomGroupingTool());
       }
   }
   ```
   
   **方式2：手动注册**：
   ```csharp
   BuildToolFactory.RegisterGroupingTool("custom", new CustomGroupingTool());
   ```

3. 在构建窗口的下拉菜单中即可选择新工具

### Addressable分组工具使用说明

**HyperContentAddressableGroupingTool** 是 HyperContent 构建用的分组工具（从 Addressable 读入并生成 BuildPlan），特点：

1. **完全基于Addressable**：
   - 只收集标记为Addressable的资源
   - 使用Addressable的address作为asset key
   - 根据Addressable Group名称分配bundle

2. **无需HyperContentAsset markers**：
   - 不需要为资源创建HyperContentAsset标记文件
   - 直接使用Addressable Groups窗口中的配置

3. **使用方式**：
   - 在构建窗口中选择 "Grouping Tool" 为 "addressable"
   - 确保项目已安装并初始化Addressables包
   - 在Addressables Groups窗口中配置好分组

4. **与DefaultGroupingTool的区别**：
   - DefaultGroupingTool：使用HyperContentAsset markers，支持多种分组策略
   - HyperContentAddressableGroupingTool：直接使用Addressable Groups，更简单直接

### 扩展新的打包执行器

1. 实现 `IBuildExecutor` 接口：
   ```csharp
   public class CustomBuildExecutor : IBuildExecutor
   {
       public string ExecutorName => "Custom Build Executor";
       public string Description => "Description of what this executor does";
       
       public BuildResult Execute(BuildPlan plan, BuildConfig config)
       {
           // 1. 验证BuildPlan
           // 2. 构建Bundle（自定义逻辑）
           // 3. 生成 HyperCatalog.bin（CatalogGenerator）
           // 4. 生成报告
           return BuildResult.Success(context);
       }
       
       public List<string> Validate(BuildPlan plan, BuildConfig config)
       {
           var errors = new List<string>();
           // 验证逻辑
           return errors;
       }
   }
   ```

2. 在 `BuildToolFactory` 中注册新执行器：
   ```csharp
   BuildToolFactory.RegisterBuildExecutor("custom", new CustomBuildExecutor());
   ```

3. 在构建窗口的下拉菜单中即可选择新执行器

### 扩展新的分组策略（用于DefaultGroupingTool）

如果需要为DefaultGroupingTool添加新的分组策略：

1. 实现 `IBundleGroupingStrategy` 接口：
   ```csharp
   public class CustomGroupingStrategy : IBundleGroupingStrategy
   {
       public string StrategyName => "Custom Strategy";
       
       public bool AssignBundles(BuildContext context)
       {
           // 从自定义来源获取分组数据（GUID -> Group Name）
           var groupingData = GetGroupingDataFromCustomSource();
           
           // 应用分组数据到已收集的资源
           foreach (var kvp in context.AssetMarkers)
           {
               var assetGuid = kvp.Key;
               var groupName = groupingData.TryGetValue(assetGuid, out var group) 
                   ? group 
                   : GetFallbackGroupName(kvp.Value);
               
               var bundleName = SanitizeBundleName(groupName);
               // 填充 context.AssetToBundle 和 context.BundleToAssets
           }
           
           return true;
       }
       
       public List<string> Validate(BuildContext context)
       {
           return new List<string>();
       }
   }
   ```

2. 在 `BundleGroupingStrategyFactory` 中注册新策略
3. 在 `BundleGroupingStrategyType` 枚举中添加新类型

### 当前支持的分组策略（DefaultGroupingTool）

- **MarkerBasedGroupingStrategy**: 从 HyperContentAsset marker 的 `bundleGroup` 字段获取分组数据
- **AddressableGroupingStrategy**: 从 Addressable Groups 获取分组数据（读取 GUID -> Group Name 映射）

## 架构变更说明

### 重构历史

**v2.0 - 分层架构重构**（当前版本）：
- 引入分组工具（IBundleGroupingTool）和打包执行器（IBuildExecutor）两层架构
- 将资源分组逻辑和打包执行逻辑完全分离
- 支持在构建窗口中选择不同的工具组合
- 保持向后兼容，默认工具使用原有逻辑

**v1.0 - 初始版本**：
- 单层架构，所有逻辑集中在HyperContentBuilder中
- 使用分组策略模式实现不同的分组方式

### 新增文件

**核心接口**：
- `IBundleGroupingTool.cs` - 分组工具接口
- `IBuildExecutor.cs` - 打包执行器接口

**数据结构**：
- `BuildPlan.cs` - 构建计划（分组工具的输出，打包执行器的输入）

**默认实现**：
- `DefaultGroupingTool.cs` - 默认分组工具实现
- `DefaultBuildExecutor.cs` - 默认打包执行器实现

**工具工厂**：
- `BuildToolFactory.cs` - 工具注册和获取工厂

### 修改的文件

**BuildContext.cs**：
- 在`BuildConfig`中添加了`groupingToolId`和`buildExecutorId`字段

**HyperContentBuilder.cs**：
- 重构为使用分组工具和打包执行器的新架构
- 简化了主构建流程，现在主要负责协调工具

**HyperContentBuildWindow.cs**：
- 添加了分组工具和打包执行器的选择UI
- 保留了分组策略选择（当使用默认分组工具时）

### 保留的文件（向后兼容）

以下文件仍然保留，但主要被新架构使用：
- `AssetCollector.cs` - 被DefaultGroupingTool使用
- `DependencyAnalyzer.cs` - 被DefaultGroupingTool使用
- `CatalogGenerator.cs` - 被DefaultBuildExecutor使用
- `BuildValidator.cs` - 被DefaultBuildExecutor使用
- `BuildReportGenerator.cs` - 被DefaultBuildExecutor使用
- 所有分组策略相关文件 - 被DefaultGroupingTool使用

### 迁移指南

对于使用旧版本API的代码：

1. **直接调用HyperContentBuilder.Build()**: 无需修改，会自动使用默认工具
2. **自定义分组逻辑**: 可以：
   - 实现新的IBundleGroupingTool（推荐）
   - 或继续使用IBundleGroupingStrategy（通过DefaultGroupingTool）

## 未来扩展

以下功能可能在后续版本中添加（需要Owner0确认）：

- 支持自定义bundle命名策略
- 支持资源标签过滤
- 支持增量构建
- 支持远程bundle配置
- 支持加密bundle
- 支持更多分组工具（如基于目录结构、基于标签等）
- 支持更多打包执行器（如支持不同的打包格式、支持并行构建等）
