# Owner3 实现文档

## 概述

作为Owner3，负责实现内容更新、缓存和传输相关的核心功能，使命是让内容能"线上更新、可回退、可缓存、可诊断"。

## 已实现功能

### 1. Downloader（超时/重试/并发）

**文件**: `Runtime/Bundle/HttpBundleTransport.cs`

**功能特性**:
- ✅ HTTP/HTTPS下载支持
- ✅ 超时控制（可配置，默认30秒）
- ✅ 自动重试机制（可配置，默认3次）
- ✅ 并发下载控制（可配置，默认4个并发）
- ✅ 下载队列管理
- ✅ 进度回调支持
- ✅ 错误处理和分类（网络错误、超时、HTTP错误等）
- ✅ 指数退避重试策略

**主要方法**:
- `Initialize(baseUrl, timeoutSeconds)`: 初始化传输器
- `SetMaxRetries(maxRetries)`: 设置最大重试次数
- `SetMaxConcurrentDownloads(maxConcurrent)`: 设置最大并发数
- `DownloadAsync(url, onProgress, onComplete)`: 异步下载
- `Download(url, out data)`: 同步下载
- `CancelDownload(url)`: 取消下载
- `IsDownloading(url)`: 检查是否正在下载

### 2. Cache（原子写入、防损坏、hash校验、清理策略prune）

**文件**: `Runtime/Bundle/LocalBundleStore.cs` (增强版)

**新增功能**:
- ✅ **原子写入**: 使用临时文件+重命名机制，确保写入的原子性
- ✅ **防损坏**: 
  - 写入后验证文件完整性
  - 启动时验证所有缓存文件
  - 自动清理损坏的文件
- ✅ **Hash校验**: 
  - 保存时验证hash
  - 加载时验证hash
  - 支持手动验证
- ✅ **清理策略prune**: 
  - LRU（最近最少使用）策略
  - 可配置最大缓存大小（默认1GB）
  - 自动清理超过限制的缓存
  - 缓存元数据持久化

**主要增强**:
- `SetMaxCacheSize(maxSizeBytes)`: 设置最大缓存大小
- `PruneCache()`: 手动触发清理
- 缓存元数据管理（访问时间、创建时间、大小、hash）
- 启动时自动验证和清理损坏文件

### 3. Catalog拉取与缓存（remote覆盖策略）

**文件**: `Runtime/Catalog/RemoteContentCatalog.cs`

**功能特性**:
- ✅ 从远程URL拉取Catalog
- ✅ 本地缓存Catalog（1小时有效期）
- ✅ Remote Catalog覆盖Local Catalog（按 Owner0 规范）
- ✅ 自动回退机制（远程失败时使用缓存或本地Catalog）
- ✅ 原子写入缓存文件
- ✅ Catalog验证

**覆盖策略**:
- Remote Catalog优先：所有查询先检查Remote Catalog
- Local Catalog作为回退：Remote Catalog不存在时使用Local Catalog
- 合并结果：GetAllAssetKeys和GetAllBundleNames合并两个Catalog的结果

### 4. 更新流程（catalog diff → 下载缺失bundles）

**文件**: `Runtime/Catalog/ContentUpdateManager.cs`

**功能特性**:
- ✅ Catalog差异比较
- ✅ 识别需要下载的bundles（缺失、版本更新、hash变化）
- ✅ 批量下载缺失bundles
- ✅ 同步和异步更新支持
- ✅ 进度报告
- ✅ 错误处理和报告

**主要方法**:
- `CheckForUpdates()`: 检查更新，返回需要下载的bundles信息
- `UpdateContent()`: 同步更新（阻塞）
- `UpdateContentAsync(onComplete, onProgress)`: 异步更新

**UpdateResult包含**:
- 需要下载的bundles数量
- 总大小
- 下载成功/失败数量
- 失败bundle列表

### 5. CDN/环境profile（dev/staging/prod的baseUrl）

**文件**: `Runtime/Core/EnvironmentProfile.cs`

**功能特性**:
- ✅ 环境类型定义（Development/Staging/Production）
- ✅ 每个环境的独立配置：
  - BaseUrl（CDN基础URL）
  - CatalogUrl（Catalog URL）
  - TimeoutSeconds（超时时间）
  - MaxRetries（最大重试次数）
  - MaxConcurrentDownloads（最大并发数）
- ✅ 默认配置文件（可自定义）
- ✅ 运行时切换环境
- ✅ 单例管理器

**主要方法**:
- `SetEnvironment(type)`: 设置当前环境
- `GetCurrentProfile()`: 获取当前环境配置
- `GetProfile(type)`: 获取指定环境配置
- `RegisterProfile(profile)`: 注册自定义配置
- `GetBaseUrl()`: 获取当前环境的BaseUrl
- `GetCatalogUrl()`: 获取当前环境的CatalogUrl

### 6. BundleProvider（store/transport/loader的组合）

**文件**: `Runtime/Bundle/BundleProvider.cs`

**功能特性**:
- ✅ 统一的Bundle访问接口
- ✅ 自动处理不同Location（Local/Remote/StreamingAssets）
- ✅ 自动缓存远程bundles
- ✅ Hash验证
- ✅ 同步和异步支持
- ✅ 与Unity AssetBundle集成

**主要方法**:
- `GetBundle(bundleName)`: 同步获取bundle数据
- `GetBundleAsync(bundleName, onComplete, onProgress)`: 异步获取bundle数据
- `LoadAssetBundle(bundleName, onComplete, onError)`: 加载为Unity AssetBundle

**工作流程**:
1. 从Catalog获取BundleInfo
2. 根据Location选择策略：
   - Local/StreamingAssets: 直接从本地加载
   - Remote: 检查缓存 → 下载（如需要）→ 保存到缓存 → 返回数据
3. 验证Hash
4. 返回Bundle数据或AssetBundle实例

## 与Owner2的接口

BundleProvider提供了稳定的接口，Owner2可以通过以下方式使用：

```csharp
// 初始化
var bundleProvider = new BundleProvider(store, transport, loader, catalog);

// 获取bundle数据
var result = bundleProvider.GetBundle("bundle_name");
if (result.Success)
{
    // 使用result.Data或result.LocalPath
}

// 异步获取
bundleProvider.GetBundleAsync("bundle_name", (result) => {
    // 处理结果
});

// 直接加载为AssetBundle
bundleProvider.LoadAssetBundle("bundle_name", (assetBundle) => {
    // 使用AssetBundle
});
```

## 使用示例

### 初始化系统

```csharp
// 设置环境
EnvironmentProfileManager.Instance.SetEnvironment(EnvironmentProfile.EnvironmentType.Production);

// 初始化组件
var store = new LocalBundleStore();
store.Initialize(null);
store.SetMaxCacheSize(1024L * 1024 * 1024); // 1GB

var transport = new HttpBundleTransport();
var profile = EnvironmentProfileManager.Instance.GetCurrentProfile();
transport.Initialize(profile.BaseUrl, profile.TimeoutSeconds);
transport.SetMaxRetries(profile.MaxRetries);
transport.SetMaxConcurrentDownloads(profile.MaxConcurrentDownloads);

var catalog = new RemoteContentCatalog();
catalog.Initialize(profile.CatalogUrl);

var loader = new UnityBundleLoader();

// 创建BundleProvider
var bundleProvider = new BundleProvider(store, transport, loader, catalog);
```

### 更新内容

```csharp
var localCatalog = new LocalContentCatalog();
localCatalog.Initialize("local_catalog.catalog.json");

var remoteCatalog = new RemoteContentCatalog();
remoteCatalog.Initialize("https://cdn.example.com/catalog.catalog.json");

var updateManager = new ContentUpdateManager(localCatalog, remoteCatalog, store, transport);

// 检查更新
var checkResult = updateManager.CheckForUpdates();
Debug.Log($"需要下载 {checkResult.BundlesToDownload} 个bundles，总大小: {checkResult.TotalSizeBytes / 1024 / 1024}MB");

// 执行更新
updateManager.UpdateContentAsync(
    (result) => {
        Debug.Log($"更新完成: 成功 {result.BundlesDownloaded}, 失败 {result.BundlesFailed}");
    },
    (progress) => {
        Debug.Log($"更新进度: {progress * 100}%");
    }
);
```

## 技术要点

1. **原子写入**: 使用临时文件+重命名，确保写入过程中断不会损坏现有文件
2. **Hash验证**: SHA256校验，确保文件完整性
3. **LRU清理**: 基于最后访问时间的清理策略，自动管理缓存大小
4. **错误恢复**: 多层回退机制，确保系统稳定性
5. **并发控制**: 下载队列管理，避免资源竞争
6. **远程覆盖**: Remote Catalog 始终优先，符合 Owner0 规范

## 注意事项

1. HttpBundleTransport使用UnityWebRequest，需要在主线程调用
2. RemoteContentCatalog的远程拉取目前是同步的，可以改为异步
3. FetchResult接口不包含数据，需要修改接口或使用同步下载作为fallback
4. 缓存元数据使用JSON序列化，大量数据时可能需要优化

## 文件清单

- `Runtime/Bundle/HttpBundleTransport.cs` - HTTP下载器
- `Runtime/Bundle/LocalBundleStore.cs` - 增强的缓存实现（已修改）
- `Runtime/Catalog/RemoteContentCatalog.cs` - 远程Catalog实现
- `Runtime/Catalog/ContentUpdateManager.cs` - 更新流程管理器
- `Runtime/Core/EnvironmentProfile.cs` - 环境配置管理
- `Runtime/Bundle/BundleProvider.cs` - Bundle提供者（组合接口）
