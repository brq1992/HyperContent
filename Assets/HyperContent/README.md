# HyperContent

类似Addressable的资源管理系统，v1版本。

## 架构概览

HyperContent采用模块化设计，核心接口定义在`Runtime/Core/`目录下：

- **IContentCatalog**: Catalog管理接口
- **IBundleStore**: Bundle存储接口
- **IBundleTransport**: Bundle传输接口（下载/上传）
- **IBundleLoader**: Bundle加载接口（Unity AssetBundle）
- **IResourceProvider**: 资源提供接口（对外API）

## 快速开始

### 1. 创建Catalog文件

在`Assets/StreamingAssets/`目录下创建`test.catalog.json`:

```json
{
  "version": 1,
  "name": "test_catalog",
  "timestamp": 1234567890,
  "assetToBundle": {
    "test_sprite": "test_bundle"
  },
  "bundles": {
    "test_bundle": {
      "name": "test_bundle",
      "size": 1024,
      "hash": "",
      "version": 1,
      "location": "StreamingAssets",
      "remoteUrl": "",
      "localPath": "test_bundle.bundle",
      "dependencies": [],
      "assetKeys": ["test_sprite"]
    }
  }
}
```

### 2. 创建测试Bundle

使用Unity的AssetBundle构建系统创建Bundle，放在`StreamingAssets/`目录。

### 3. 使用代码

```csharp
using HyperContent;
using UnityEngine;

public class TestScript : MonoBehaviour
{
    void Start()
    {
        // 初始化
        if (HyperContentManager.Instance.Initialize("test.catalog.json"))
        {
            // 加载资源
            var sprite = HyperContentManager.ResourceProvider.LoadAsset<Sprite>("test_sprite");
            if (sprite != null)
            {
                Debug.Log("Asset loaded successfully!");
            }
        }
    }
}
```

## 项目结构

详见 [SPECIFICATION.md](SPECIFICATION.md)

## 开发规范

- 所有接口变更必须经过Owner0 Code Review
- 遵循命名规则和错误码规范
- 使用结构化日志字段
