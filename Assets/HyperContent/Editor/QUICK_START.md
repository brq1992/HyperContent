# HyperContent 构建系统快速开始

## 第一步：标记资源

### 方式1：使用ScriptableObject标记（推荐）

1. 选择要标记的资源（如Sprite、Prefab等）
2. 在资源所在目录创建标记文件：
   - 右键点击资源文件
   - 选择 `Create > HyperContent > Asset Marker`
   - 或者手动创建：`AssetName_HyperContent.asset`

3. 配置标记：
   - **assetKey**: 设置运行时加载使用的key（如 "ui/main_menu"）
   - **bundleGroup**: 设置bundle组名（如 "ui_bundle"）
   - **labels**: 可选，添加标签用于过滤
   - **forceSeparateBundle**: 如果需要单独打包，勾选此项

### 方式2：使用目录约定

1. 将资源放在 `Assets/HyperContent/Content/` 目录下
2. 系统会自动收集这些资源
3. asset key会自动从资源路径生成（如 "HyperContent/Content/sprite.png" -> "HyperContent/Content/sprite"）

## 第二步：配置构建

1. 打开构建窗口：`HyperContent > Build Window`
2. 配置参数：
   - **Catalog Name**: 设置catalog名称（如 "main_catalog"）
   - **Output Directory**: 设置输出目录（默认：Assets/StreamingAssets）
   - **Build Target**: 选择目标平台
   - **Compression**: 选择压缩方式（Lz4推荐）
   - **Include Dependencies**: 是否包含依赖资源
   - **Generate Report**: 是否生成构建报告

## 第三步：构建

1. 点击"Build"按钮
2. 等待构建完成
3. 查看控制台输出和构建报告

## 示例

### 示例1：标记一个Sprite资源

1. 资源路径：`Assets/HyperContent/Content/Sprites/hero.png`
2. 创建标记：`Assets/HyperContent/Content/Sprites/hero_HyperContent.asset`
3. 配置标记：
   - assetKey: "hero"
   - bundleGroup: "characters"

### 示例2：标记一个Prefab资源

1. 资源路径：`Assets/HyperContent/Content/Prefabs/Enemy.prefab`
2. 创建标记：`Assets/HyperContent/Content/Prefabs/Enemy_HyperContent.asset`
3. 配置标记：
   - assetKey: "prefabs/enemy"
   - bundleGroup: "prefabs"
   - forceSeparateBundle: false

### 示例3：使用目录约定

1. 将所有UI资源放在 `Assets/HyperContent/Content/UI/` 目录
2. 系统会自动收集并生成key
3. 如需自定义，创建对应的标记文件

## 验证构建结果

构建完成后，检查：

1. **Catalog文件**: `{OutputDirectory}/{CatalogName}.catalog.json`
2. **Bundle文件**: `{OutputDirectory}/*.bundle`
3. **控制台输出**: 查看是否有错误或警告
4. **构建报告**: 如果启用了报告，查看bundle大小和依赖信息

## 常见问题

**Q: 如何为多个资源设置相同的bundle group？**
A: 在标记文件中设置相同的 `bundleGroup` 值，它们会被打包到同一个bundle。

**Q: 如何强制某个资源单独打包？**
A: 在标记文件中勾选 `forceSeparateBundle`。

**Q: 构建后找不到catalog文件？**
A: 检查输出目录路径是否正确，确保有写入权限。

**Q: 如何查看构建报告？**
A: 构建完成后，在Unity控制台查看详细输出，或启用"Generate Report"选项。
