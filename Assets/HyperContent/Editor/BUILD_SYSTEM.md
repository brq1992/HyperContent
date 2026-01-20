# HyperContent 构建系统文档

## 概述

HyperContent构建系统负责将Unity工程资源转换为可被runtime消费的bundles和catalog。作为Owner1，本系统严格遵守Owner0定义的catalog schema（schemaVersion=1）。

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

### 3. 依赖分析

- 自动分析资源之间的依赖关系
- 根据依赖关系确定bundle切分策略
- 支持将依赖资源包含到同一个bundle中（可选）

### 4. Bundle构建

- 使用Unity的AssetBundle构建系统
- 支持多种压缩方式：None, Lz4, Lz4HC
- 自动生成bundle文件到指定输出目录

### 5. Catalog生成

- 严格遵守schemaVersion=1格式
- 自动生成catalog.json文件
- 包含完整的asset到bundle映射和bundle元信息

### 6. 构建校验

构建系统会进行以下校验：

- **重复key检查**: 检测是否有多个资源使用相同的asset key
- **非法key检查**: 验证asset key格式是否符合规范
- **缺失资源检查**: 确保所有引用的资源文件存在
- **Bundle大小报告**: 生成详细的bundle大小统计

### 7. Build Report（可选）

生成详细的构建报告，包括：

- 总bundle数量和总大小
- 最大/最小bundle信息
- 平均bundle大小
- 重复依赖分析（哪些bundle共享依赖）
- 资源聚合信息（每个bundle包含哪些资源）

## 使用方法

### 方法1：使用构建窗口（推荐）

1. 打开构建窗口：`HyperContent > Build Window`
2. 配置构建参数：
   - Catalog Name: catalog名称
   - Output Directory: 输出目录（默认：Assets/StreamingAssets）
   - Build Target: 构建目标平台
   - Compression: 压缩方式
   - Include Dependencies: 是否包含依赖资源
   - Force Rebuild: 是否强制重建所有bundle
   - Generate Report: 是否生成构建报告
3. 点击"Build"按钮开始构建
4. 查看控制台输出和构建报告

### 方法2：使用菜单

- `HyperContent > Build`: 使用默认配置构建
- `HyperContent > Build (Force Rebuild)`: 强制重建所有bundle
- `HyperContent > Validate`: 仅验证配置，不进行构建

## 工作流程

构建系统按以下步骤执行：

1. **收集资源**: 扫描项目中的标记资源和内容目录
2. **分析依赖**: 分析资源之间的依赖关系
3. **分配Bundle**: 根据标记和分组规则分配资源到bundle
4. **验证配置**: 检查重复key、非法key、缺失资源等
5. **构建Bundle**: 使用Unity AssetBundle系统构建bundle文件
6. **生成Catalog**: 生成符合schemaVersion=1的catalog.json
7. **最终验证**: 验证构建结果
8. **生成报告**: 生成构建报告（如果启用）

## 输出结构

构建完成后，输出目录结构如下：

```
OutputDirectory/
├── {catalog_name}.catalog.json    # Catalog文件
├── bundle1.bundle                  # Bundle文件
├── bundle1.bundle.manifest         # Bundle清单
├── bundle2.bundle
├── bundle2.bundle.manifest
└── ...
```

## 命名规则

- **Asset Key**: 最大长度256字符，支持路径分隔符 `/`，不能包含反斜杠 `\`
- **Bundle Name**: 最大长度128字符，不能包含路径分隔符
- **Catalog Name**: 最大长度128字符

## 错误处理

构建过程中发现的错误会记录在BuildContext中：

- **Errors**: 阻止构建继续的错误（如重复key、非法key等）
- **Warnings**: 不影响构建的警告（如找不到关联资源等）

所有错误和警告都会在控制台输出，构建窗口也会显示摘要。

## 与Owner0的接口

### Catalog Schema

构建系统严格遵守Owner0定义的catalog schema（schemaVersion=1）：

```json
{
  "version": 1,
  "name": "catalog_name",
  "timestamp": 1234567890,
  "assetToBundle": [
    {"key": "asset_key", "bundle": "bundle_name"}
  ],
  "bundles": [
    {
      "name": "bundle_name",
      "size": 1024,
      "hash": "sha256_hash",
      "version": 1,
      "location": "StreamingAssets",
      "remoteUrl": "",
      "localPath": "bundle_name.bundle",
      "dependencies": [],
      "assetKeys": ["asset_key"]
    }
  ]
}
```

### 接口变更控制

- 任何对catalog schema的修改必须先提案给Owner0
- 收到Owner0的confirm指令后才能合并
- 当前版本严格使用schemaVersion=1，不支持任何扩展字段

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

## 未来扩展

以下功能可能在后续版本中添加（需要Owner0确认）：

- 支持自定义bundle命名策略
- 支持资源标签过滤
- 支持增量构建
- 支持远程bundle配置
- 支持加密bundle
