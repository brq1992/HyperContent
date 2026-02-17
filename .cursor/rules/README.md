# Cursor Rules for HyperContent Project

本目录包含HyperContent项目中各个Owner的规则文件，用于配置Agent的身份和职责。

## 如何使用

### 方式1: 在Agent配置中引用规则

在创建或配置Agent时，指定对应的规则文件：

- **Owner0 Agent**: 引用 `.cursor/rules/owner0.md`
- **Owner1 Agent**: 引用 `.cursor/rules/owner1.md`
- **Owner2 Agent**: 引用 `.cursor/rules/owner2.md`
- **Owner3 Agent**: 引用 `.cursor/rules/owner3.md`

### 方式2: 在系统提示中包含

在Agent的系统提示中添加：

```
你是HyperContent项目的Owner0，负责核心接口定义和规范维护。
请参考 .cursor/rules/owner0.md 和 Assets/HyperContent/OWNERS.md 了解你的职责范围。
```

### 方式3: 使用Cursor的规则系统

Cursor会自动读取 `.cursor/rules/` 目录下的规则文件。你可以在Cursor设置中配置默认使用的规则。

## 规则文件说明

- `owner0.md` - Owner0的规则：核心接口与规范
- `owner1.md` - Owner1的规则：Build Pipeline
- `owner2.md` - Owner2的规则：运行时资源管理
- `owner3.md` - Owner3的规则：内容更新与传输

## 完整文档

详细的Owner职责划分请参考：
- `Assets/HyperContent/OWNERS.md` - 完整的Owner职责划分文档

## 更新规则

当Owner职责发生变化时，请同时更新：
1. `Assets/HyperContent/OWNERS.md` - 主文档
2. `.cursor/rules/owner*.md` - 对应的规则文件
