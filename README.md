<div align="center">

# 🧹 CacheCleaner

**AI 时代开发者的磁盘治理工具 —— Agent · CLI · IDE · 内置 AI · 模型仓库，一个 exe 全覆盖**

[![Release](https://img.shields.io/github/v/release/JiangSuRan/CacheCleaner?color=blue&label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC)](https://github.com/JiangSuRan/CacheCleaner/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-blueviolet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4)](https://github.com/JiangSuRan/CacheCleaner/releases)
[![Rules](https://img.shields.io/badge/%E7%94%9F%E6%95%88%E8%A7%84%E5%88%99-87%E6%9D%A1-teal)](#-核心能力)
[![License](https://img.shields.io/badge/License-MIT-green)](#-许可证)

*自包含单文件 · 免安装 · 零依赖 · 数据安全优先*

</div>

---

## ✨ 为什么需要它

AI agent 与 CLI 的普及，让开发者磁盘出现了一代全新的膨胀源：**会话转录**只进不出、**模型权重**动辄数十 GB、**自更新**堆积旧版本、**内容寻址存储**设计上只增不减、再加上每个工具都在家目录圈一块 dot-home。社区已经大量报障（`~/.claude` 涨到 3.6 GB 的 issue、Cursor "清缓存"误删 300 GB 的事故），而传统清理工具不理解这些语义。

CacheCleaner 用一套**带安全语义的规则引擎**统一治理：87 条生效规则覆盖 Agent · CLI · IDE · 内置 AI · 模型仓库六类位置，配三级风险、进程守卫、预览模式与全程审计——并把收录做成可持续维护的**Agent 目录**。

## 🎯 核心能力

| 能力 | 说明 |
|---|---|
| 🔍 **两阶段智能扫描** | 秒级扫描 70+ 内置规则位置，随后自动遍历用户目录发现未知应用的缓存（支持 `.cache` / `.tmp` / `.logs` 点目录），并按「目录是否存在」自动显隐 |
| 🛡️ **三级安全防护** | 安全 / 注意 / 危险三级风险标注；路径安全校验 + 系统级精确白名单；`guardProcesses` 进程守卫——浏览器、微信运行中自动跳过对应缓存项并记入日志 |
| 👁️ **预览模式** | 只统计将释放的量，不删除任何文件、不执行任何命令、不改动服务状态——先看账单，再动手 |
| 📊 **效果可观测** | 清理汇总以**磁盘可用空间差**为准（拒绝纸面数字）；逐项释放量与失败明细（被占用/权限不足的具体路径）落盘审计日志 |
| 📈 **增长分析** | 按「最后写入时间」找出最近 3/7/14 天新写入的大文件排行，直接回答「**谁在吃我的盘**」；命中可迁盘的大缓存时给出**迁盘顾问**命令 |
| 🧩 **JSON 规则引擎** | 71 条默认规则内嵌 + 16 条 Agent 目录编译，放一份 `rules.user.json` 即可增改，**无需重新编译**（对标 Winapp2.ini 的思路） |
| 🤖 **Agent 收录目录** | 18 个主流 agent 的收录清单（Claude Code / Codex / Gemini CLI / Qwen / OpenCode / Amazon Q / Goose / Crush / 通义灵码 / MarsCode），带探测签名、neverTouch 白名单、置信分级与核实日期 |
| 🗺️ **未收录探测器** | 家目录下未被覆盖的大体积工具目录以「只报告不清理」列出——**已知全自动，未知看得见** |
| 🖥️ **系统级瘦身** | 回收站、系统还原点（缩减上限保留最新）、WinSxS 组件存储（DISM）、Windows 升级残留（cleanmgr /autoclean）、Windows 更新缓存（自动停启服务） |
| 🐳 **Docker/WSL 再生性清理** | `docker system prune` 按 df 差值计释放；vhdx 虚拟磁盘 `diskpart compact` 离线压缩——只回收空白，不碰数据 |
| 🏢 **IDE 与内置 AI** | JetBrains 全家桶系统缓存（多产品多版本）、VS Code C++ ipch 缓存、Copilot Chat 会话数据（只读报告）、Windsurf/ZCode 等桌面客户端 |
| 🌍 **通用性** | 系统盘符运行时推导（Windows 不在 C 盘同样可用）；中英文系统输出与小数逗号 locale 兼容；Conda 安装位置动态发现 |

## 🧭 设计原则

1. **语义不明，绝不碰** —— 未收录的工具目录只报告占用，确认语义前不删除
2. **凭证与配置是红线** —— 每个收录条目都带 neverTouch 白名单（auth/credentials/config/skills 强制跳过）
3. **会话是用户数据** —— 一律「注意」级 + 超龄（默认 30 天）才清理，对话记录不代删
4. **官方命令优先** —— 内容寻址存储只走官方 prune（`pnpm store prune` / `go clean -modcache`），拒绝裸删
5. **预览先行** —— 干跑模式零副作用，命令式清理与系统服务状态同样受保护
6. **全程可审计** —— 每一次扫描与清理：逐项释放量、失败路径、磁盘净增，全部落盘可复盘

## 🚀 快速开始

1. 前往 [**Releases 页面**](https://github.com/JiangSuRan/CacheCleaner/releases) 下载最新版 `CacheCleaner.exe`
2. 双击运行（启动时弹出 UAC 提权确认——清理系统目录与重启删除登记需要管理员权限）
3. 点击 **「🔍 扫描缓存」**，按大小降序查看所有可清理项
4. 不放心？勾选 **「预览模式（不删除）」** 再点清理，先看将释放多少
5. 点击 **「🧹 清理选中」**——完成后弹窗展示磁盘可用空间的真实增量

> 被占用的文件会自动登记为**重启删除**，并在汇总中如实告知；被跳过的项目附失败原因。

## 📖 进阶用法

### 自定义清理规则

在 exe 同目录（便携场景）或 `%LOCALAPPDATA%\CacheCleaner\`（安装场景）放一份 `rules.user.json`，即可以声明式方式新增/覆盖规则，**无需重新编译**：

```json
[
  {
    "name": "MyApp 缓存",
    "base": "localAppData",
    "path": "MyApp\\Cache",
    "desc": "MyApp 播放缓存，清理后自动重建",
    "risk": "safe",
    "guardProcesses": ["MyApp"]
  },
  {
    "name": "某服务日志",
    "base": "windows",
    "path": "Logs\\MyService",
    "desc": "仅清理 30 天前的 *.log",
    "risk": "warn",
    "clean": "files",
    "filesPatterns": ["*.log"],
    "minAgeDays": 30,
    "recursive": true
  },
  {
    "name": "Go 模块缓存",
    "base": "userProfile",
    "path": "go\\pkg\\mod",
    "desc": "官方命令清理，按目标目录前后差值计释放",
    "risk": "safe",
    "clean": "command",
    "command": "go clean -modcache",
    "commandTimeoutMs": 600000
  }
]
```

字段速查：`base`（`localAppData` / `appData` / `userProfile` / `windows` / `programData` / `driveRoot`）· `risk`（`safe` / `warn` / `danger`）· `kind`（`path` / `file` / `special`）· `clean`（`directory` / `files` / `command` / `reportOnly`）· `filesPatterns` · `minAgeDays` · `recursive` · `skipSize` · `guardProcesses` · `command` + `commandTimeoutMs`（`clean: "command"` 时的官方清理命令与超时）。

### 增长基线监控

仓库附带只读的增长基线脚本，定期运行即可看到各关键位置的增量排行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File cleanup\growth-baseline.ps1
```

### 审计日志

每次扫描与清理自动记录到 `%LOCALAPPDATA%\CacheCleaner\logs\clean-日期.log`：条目大小、逐项释放量、被占用/权限不足的具体文件路径、磁盘可用空间前后差——「清理效果不理想」从此有据可查。

## 🤖 Agent 收录目录

收录清单见 [docs/agent-catalog.md](docs/agent-catalog.md)，收录标准：

- **有官方文档或源码级证据**才纳入可清理条目；语义未核验的 agent 只进「未收录报告」
- 每个条目声明 `detect` 探测签名（特征文件）、`neverTouch` 白名单、`confidence` 置信分级与 `verified` 核实日期
- 布局漂移的防护：目录不存在自动隐藏；用户可用 `rules.user.json` 即时修正

欢迎提 issue 贡献新 agent 的布局证据（附官方文档或源码链接），按流程核实后收录。

## 🆕 版本演进

| 版本 | 主题 | 亮点 |
|---|---|---|
| v3.4 | 修复 | 命令式清理死代码复活（还原点/内存转储首次生效）、释放量真实化、DISM 超时保护 |
| v3.5 | 覆盖面 | VSIX 扩展缓存、CBS 日志、pnpm、GPU 着色器、UWP 缓存族、Firefox/Brave/Vivaldi、AI 工具旧版本 |
| v3.6 | 可观测 | 审计日志、可用空间差口径、📈 增长分析、遗留追踪会话检测 |
| v3.7 | 通用性 | 系统盘符运行时推导、Conda 动态发现、多语言/locale 兼容 |
| v3.8 | 架构 | JSON 规则引擎 + 逐规则进程守卫 |
| v3.9 | 体验 | 预览模式、设置持久化、Docker/WSL 再生性清理 |
| v4.0 | Agent 时代 | Agent dot-home 规则包、官方 prune 命令集成、模型仓库报告、迁盘顾问 |
| v4.1 | Agent 目录 | 10 个 agent 收录清单（签名探测/置信分级/neverTouch）+ 未收录探测器 |
| v4.2 | IDE 与内置 AI | JetBrains 系统缓存扫描器、C++ ipch/Copilot Chat 规则、Windsurf/ZCode 收录 |
| v4.3 | 目录扩充 | DeepSeek Harness/Zed/Cline/Copilot CLI/Amp/Droid/CodeBuddy/pi 收录（全部源码级核实） |

## 🛠️ 技术栈与构建

- C# / .NET 10 WinForms，零外部依赖，自包含单文件发布
- Inno Setup 打包安装程序（可选）

```bash
# 编译
dotnet build -c Release

# 发布自包含单文件
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish

# 生成安装包（需 Inno Setup）
ISCC setup.iss
```

## 📁 项目结构

```
├── CacheScanner.cs      # 扫描/清理核心（规则引擎消费端、进程守卫、命令式清理框架）
├── CleaningRules.cs     # JSON 规则加载器（内嵌默认 + 侧车覆盖 + 目录编译）
├── AgentCatalog.cs      # Agent 目录加载与编译器
├── rules.default.json   # 内嵌声明式规则（71 条）
├── agents.catalog.json  # Agent 收录目录（10 个 agent / 16 条编译条目）
├── CleanLog.cs          # 清理审计日志
├── GrowthDialog.cs      # 增长分析页（含迁盘顾问）
├── AppSettings.cs       # 设置持久化
├── MainForm.cs          # 主界面
├── LICENSE              # MIT
├── cleanup/             # 增长基线监控脚本等辅助工具
└── docs/                # 设计演进记录 + Agent 时代调研 + 目录分析
```

## 📸 截图

| 主界面 | 扫描完成（按大小降序 · 三级风险标注） |
|---|---|
| ![主界面](docs/screenshots/main.png) | ![扫描结果](docs/screenshots/scan.png) |

## 📄 许可证

MIT
