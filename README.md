# C盘缓存清理工具

一个轻量的 Windows 缓存清理工具，自动扫描 C 盘缓存文件，一键释放磁盘空间。

## 功能特点

- **两阶段智能扫描**
  - 第一阶段：快速扫描 35+ 个已知缓存位置（1-2 秒出结果）
  - 第二阶段：自动遍历 C 盘用户目录，发现未知应用的缓存
- **安全防护**
  - 三级风险标识：安全（绿色）、注意（黄色）、危险（红色）
  - 仅扫描用户目录，不碰系统目录
  - 路径安全校验，防止误删
- **覆盖范围广**
  - 开发工具：pip、npm、uv、NuGet、node-gyp、R 编译缓存
  - IDE / 编辑器：VS Code、Cursor、Trae CN、Positron、RStudio、Claude Desktop、Copilot、CherryStudio
  - 浏览器：Edge、Chrome
  - 系统缓存：临时文件、缩略图、预读取、D3D 着色器、崩溃转储
  - 其他：Conda、Docker、迅雷、剪映、豆包、bilibili、Obsidian 等
- **自包含单文件**，无需安装 .NET 运行时

## 下载安装

1. 前往 [Releases 页面](https://github.com/Jack-5732/CacheCleaner/releases) 下载最新版 `CacheCleaner-Setup.exe`
2. 双击运行安装程序
3. 安装完成后从桌面快捷方式启动

## 使用方法

1. 点击 **「扫描缓存」** — 先快速扫描已知缓存，然后自动发现更多缓存目录
2. 勾选要清理的项目（安全级别默认已勾选）
3. 点击 **「清理选中」** — 确认后逐项清理

扫描过程中可随时点击 **「取消扫描」** 中断。

自动发现的缓存项会以 `[自动发现]` 前缀和蓝色背景显示，与已知项区分。

## 截图

*(待补充)*

## 技术栈

- C# .NET 10.0 WinForms
- 零外部依赖
- Inno Setup 打包安装程序

## 开发构建

```bash
# 还原并编译
dotnet build -c Release

# 发布自包含单文件
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish

# 生成安装包（需要 Inno Setup）
ISCC setup.iss
```

## 许可证

MIT
