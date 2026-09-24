# C盘缓存清理工具

一个轻量的 Windows 缓存清理工具，自动扫描 C 盘缓存文件，一键释放磁盘空间。

## 功能特点

- **两阶段智能扫描**
  - 第一阶段：快速扫描 40+ 个已知缓存位置（1-2 秒出结果）
  - 第二阶段：自动遍历 C 盘用户目录，发现未知应用的缓存
- **安全防护**
  - 三级风险标识：安全（绿色）、注意（黄色）、危险（红色）
  - 以管理员权限运行（启动时弹 UAC），可清理系统目录；自动遍历仍限于用户目录
  - 路径安全校验 + 系统级精确白名单，防止误删
  - 清理失败明细可见：被占用 / 权限不足的文件单独计数，不再静默跳过；被占用文件可登记为重启删除
- **覆盖范围广**
  - 开发工具：pip、npm、uv、NuGet、node-gyp、R 编译缓存、CLI 工具缓存根（`~/.cache`）、Codex 临时文件
  - IDE / 编辑器：VS Code、Cursor、Trae CN、Positron、RStudio、Claude Desktop、Copilot、CherryStudio
  - 浏览器：Edge、Chrome
  - 音乐/视频客户端：汽水音乐（LunaCacheV2）
  - 聊天软件：微信4.0 缓存（仅 `cache` 子目录，绝不触碰 `msg` 聊天记录）
  - 系统缓存：Windows 更新下载缓存、Delivery Optimization、系统临时文件、错误报告（WER）、缩略图、预读取、D3D 着色器、崩溃/内存转储、DNS 缓存
  - 系统级空间大头：回收站、系统还原点（缩减卷影上限到 3GB，保留最新还原点）、Windows 组件存储（DISM 清理）
  - Adobe 自动恢复快照：扫描 `Adobe * Settings\*\DataRecovery` 会话镜像（AI 重度使用可达数 GB，清理前会提醒关闭对应程序）
  - 浏览器多 Profile：Edge/Chrome 除 Default 外的 Profile 1/2/3... 缓存（只清纯缓存子目录，不动登录态）
  - 豆包缓存改为白名单子目录清理（不再整目录删除，保留 IndexedDB 登录态和沙箱环境）
  - 其他：Conda、Docker、迅雷、剪映、豆包、bilibili、Obsidian 等
  - 自动发现支持点目录（`.cache` / `.tmp` / `.logs`），可发现未知 CLI 工具的缓存
- **自包含单文件**，无需安装 .NET 运行时

## 下载安装

1. 前往 [Releases 页面](https://github.com/JiangSuRan/CacheCleaner/releases) 下载最新版 `CacheCleaner.exe`（自包含单文件，免安装）
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
