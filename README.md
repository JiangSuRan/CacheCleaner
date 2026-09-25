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
  - 开发工具：pip、npm、pnpm（含 store）、uv、NuGet、node-gyp、R 编译缓存、CLI 工具缓存根（`~/.cache`）、Codex 临时文件与自更新旧版本（保留最新）
  - IDE / 编辑器：VS Code、Cursor、Trae CN、Positron、Qoder、RStudio、Claude Desktop、Copilot、CherryStudio——统一扫描 Cache/CachedData/Code Cache/GPUCache/扩展包缓存（VSIX）等子目录；工作区状态单独列为「注意」项
  - 浏览器：Edge、Chrome、Firefox、Brave、Vivaldi
  - 音乐/视频客户端：汽水音乐（LunaCacheV2）
  - 聊天软件：微信4.0 缓存（仅 `cache` 子目录，绝不触碰 `msg` 聊天记录）、微信3.x/4.0 小程序运行时（清理后首次使用需重新下载）、微信4.0 日志与更新包、微信输入法安装包
  - 系统缓存：Windows 更新下载缓存（自动停启 wuauserv/bits）、Delivery Optimization（官方 cmdlet）、系统临时文件、错误报告（WER）、缩略图与图标缓存、预读取、D3D/NVIDIA/AMD/Intel 着色器缓存、UWP 应用缓存族（AC\INetCache/TempState）、CBS 服务日志（仅 30 天前）、崩溃/内存转储、DNS 缓存
  - 系统级空间大头：回收站、系统还原点（缩减卷影上限到 3GB，保留最新还原点）、Windows 组件存储（DISM 清理）、Windows 升级残留（cleanmgr /autoclean，清理后无法回滚旧版本）
  - Adobe 自动恢复快照：扫描 `Adobe * Settings\*\DataRecovery` 会话镜像（AI 重度使用可达数 GB，清理前会提醒关闭对应程序）
  - 浏览器多 Profile：Edge/Chrome/Brave/Vivaldi 除 Default 外的 Profile 1/2/3... 缓存（只清纯缓存子目录，不动登录态）
  - 豆包缓存改为白名单子目录清理（不再整目录删除，保留 IndexedDB 登录态和沙箱环境），含热更新资源 hot_fix
  - 其他：Conda、Docker、迅雷、剪映、豆包、bilibili、Obsidian、飞书（根级缓存）等
  - 只读报告：VS 安装缓存（Package Cache，删除会破坏修复/卸载，仅提示大小）
  - 自动发现支持点目录（`.cache` / `.tmp` / `.logs`），可发现未知 CLI 工具的缓存
- **可观测性**
  - 清理审计日志落盘（`%LOCALAPPDATA%\CacheCleaner\logs`），逐项释放量与失败明细可复盘
  - 清理汇总以 C 盘可用空间差为准，文件长度累计仅作明细
  - 「📈 增长分析」：按最后写入时间找出最近新增的大文件排行，直接定位「谁在吃 C 盘」
  - 检测并一键停止遗留性能追踪会话（WPR 未停止的录制、手动内核追踪）
- **通用性**
  - 系统盘符运行时推导（Windows 不在 C 盘同样可用）；命令解析兼容中英文系统输出
  - 全部条目按目录是否存在自动显隐，不同机器只列出实际存在的项目；Conda 安装位置动态发现
  - 自包含单文件（x64），无需安装 .NET 运行时；需 Windows 10/11 与管理员权限

## 下载安装

1. 前往 [Releases 页面](https://github.com/JiangSuRan/CacheCleaner/releases) 下载最新版 `CacheCleaner.exe`（自包含单文件，免安装）
2. 双击运行安装程序
3. 安装完成后从桌面快捷方式启动

## 使用方法

1. 点击 **「扫描缓存」** — 先快速扫描已知缓存，然后自动发现更多缓存目录
2. 勾选要清理的项目（已知规则的安全项默认已勾选；`[自动发现]` 项需逐项确认后再勾选）
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
