<div align="center">

# 🧹 CacheCleaner

**AI 时代开发者的磁盘治理工具**

**Agent · CLI · IDE · 内置 AI · 模型仓库 · 项目工程 —— 一个 exe 全覆盖**

[![Release](https://img.shields.io/github/v/release/JiangSuRan/CacheCleaner?color=blue&label=%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC)](https://github.com/JiangSuRan/CacheCleaner/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-blueviolet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4)](https://github.com/JiangSuRan/CacheCleaner/releases)
[![License](https://img.shields.io/badge/License-MIT-green)](#-许可证)

[![Rules](https://img.shields.io/badge/%E7%94%9F%E6%95%88%E8%A7%84%E5%88%99-87%E6%9D%A1-teal)](#-核心能力)
[![Agents](https://img.shields.io/badge/Agent%E6%94%B6%E5%BD%95-18%E4%B8%AA-blue)](#-agent-收录目录)
[![Artifacts](https://img.shields.io/badge/%E5%8F%AF%E5%86%8D%E7%94%9F%E4%BA%A7%E7%89%A9-26%E9%A1%B9-orange)](#-核心能力)
[![Tests](https://img.shields.io/badge/%E5%9B%9E%E5%BD%92%E6%B5%8B%E8%AF%95-15%E9%A1%B9%E9%80%9A%E8%BF%87-success)](tests/Regression/)

*自包含单文件 · 免安装 · 零依赖 · 数据安全优先 · 全程可审计*

**简体中文** · [English](#-english)

</div>

---

## ✨ 为什么需要它

AI agent 与 CLI 的普及，让开发者的磁盘出现了一代传统清理工具完全不认识的新膨胀源：**会话转录**只进不出、**模型权重**动辄数十 GB、**自更新**堆积旧版本、**内容寻址存储**设计上只增不减，每个工具还要在家目录圈一块 dot-home。社区已经大量报障——`~/.claude` 涨到 3.6 GB 的 issue、Cursor「清缓存」误删 300 GB 的事故——一个不懂语义的清理器，要么不敢删，要么删错。

CacheCleaner 的答案是一套**带安全语义的规则引擎**：87 条生效规则覆盖六类位置，三级风险标注、进程守卫、预览模式、全程审计层层兜底，并把收录工作沉淀为可持续维护的 **Agent 收录目录**——**已知全自动，未知看得见**。

## 🎯 核心能力

| 能力 | 说明 |
|---|---|
| 🔍 **三阶段智能扫描** | 秒级扫描 70+ 内置规则位置，随后自动遍历用户目录发现未知应用的缓存（支持 `.cache` / `.tmp` / `.logs` 点目录），最后枚举项目工程的可再生产物，并按「目录是否存在」自动显隐 |
| 🛡️ **三级安全防护** | 安全 / 注意 / 危险三级风险标注；路径安全校验 + 系统级精确白名单；`guardProcesses` 进程守卫——浏览器、微信运行中自动跳过对应缓存项并记入日志 |
| 👁️ **预览模式** | 只统计将释放的量，不删除任何文件、不执行任何命令、不改动服务状态——先看账单，再动手 |
| 📊 **效果可观测** | 清理汇总以**磁盘可用空间差**为准（拒绝纸面数字）；逐项释放量与失败明细（被占用/权限不足的具体路径）落盘审计日志 |
| ♻️ **可靠清理管线** | 命令式清理并行读取输出、限时轮询、随时可取消——系统维护进程（DISM 等）保留后台自行结束，绝不误杀；逐项「处理结果」列（成功/失败/跳过/待重启），清理后核实**真实残留大小**，单项失败不中断，任何结束方式都写入审计汇总 |
| 📈 **增长分析** | 按「最后写入时间」找出最近 3/7/14 天新写入的大文件排行，直接回答「**谁在吃我的盘**」；命中可迁盘的大缓存时给出**迁盘顾问**命令 |
| 🧩 **JSON 规则引擎** | 71 条默认规则内嵌 + 16 条 Agent 目录编译，放一份 `rules.user.json` 即可增改，**无需重新编译**（对标 Winapp2.ini 的思路） |
| 🤖 **Agent 收录目录** | 18 个主流 agent 的收录清单（Claude Code / Codex / Gemini CLI / Qwen / OpenCode / Amazon Q / Goose / Crush / 通义灵码 / MarsCode 等），带探测签名、neverTouch 白名单、置信分级与核实日期 |
| 🗺️ **未收录探测器** | 家目录下未被覆盖的大体积工具目录以「只报告不清理」列出——**已知全自动，未知看得见** |
| 🖥️ **系统级瘦身** | 回收站、系统还原点（缩减上限保留最新）、WinSxS 组件存储（DISM）、Windows 升级残留（cleanmgr /autoclean）、Windows 更新缓存（自动停启服务） |
| 🐳 **Docker/WSL 再生性清理** | `docker system prune` 按 df 差值计释放；vhdx 虚拟磁盘 `diskpart compact` 离线压缩——只回收空白，不碰数据 |
| 🏢 **IDE 与内置 AI** | JetBrains 全家桶系统缓存（多产品多版本）、VS Code C++ ipch 缓存、Copilot Chat 会话数据（只读报告）、Windsurf/ZCode 等桌面客户端 |
| 🧱 **项目工程清理** | 标记文件识别项目根（Node/Python/Rust/.NET/Maven/Gradle/Dart/PHP），枚举 node_modules/.venv/target/bin/obj 等可再生产物，按大小排序、90 天陈旧度分级，Desc 注明**再生成命令**——npkill/kondo 验证的第一大痛点 |
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
3. 点击 **「扫描缓存」**，按大小降序查看所有可清理项
4. 不放心？勾选 **「预览模式」** 再点清理，先看将释放多少
5. 点击 **「清理选中」**——完成后展示磁盘可用空间的真实增量，逐项处理结果一目了然

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

## 🏗️ 工程品质

- 🧪 **回归测试套件** — [`tests/Regression`](tests/Regression/) 零框架依赖：覆盖双管道大量输出、超时与取消、预览零删除、真实删除、最近文件保留、失败后继续、残留大小计量、只读条目 UI 行为；真实删除仅作用于测试自建的随机目录，退出即清理
- 📦 **自包含单文件** — 一个 exe 内嵌 .NET 10 运行时与全部规则资产，拷贝即用，无需安装任何依赖
- ✅ **零警告编译** — Release 构建 0 错误 / 0 警告
- ♿ **无障碍友好** — 全自绘按钮保留 PushButton 无障碍角色、屏幕阅读器名称与 Tab/Space/Enter 键盘操作
- 🖼️ **一体化界面** — 无边框窗口、三档按钮层级与动态主按钮、细圆角进度条，100% 缩放下实机验证

## 🛠️ 技术栈与构建

- C# / .NET 10 WinForms，零外部依赖，自包含单文件发布
- Inno Setup 打包安装程序（可选）

```bash
# 编译（目标：零警告）
dotnet build -c Release

# 发布自包含单文件
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish

# 清理流程回归测试（真实删除仅作用于测试自建的随机目录）
dotnet run --project tests/Regression/Regression.csproj -c Release

# 实窗检查（创建示例行，不保存用户设置、不执行清理）
dotnet run --project tests/Regression/Regression.csproj -c Release -- --ui

# 生成安装包（需 Inno Setup）
ISCC setup.iss
```

## 📁 项目结构

```
├── CacheScanner.cs        # 扫描/清理核心（三阶段扫描、规则引擎消费端、进程守卫）
├── CommandRunner.cs       # 命令式清理执行器（并行管道读取、限时轮询、取消/超时）
├── FileCleaner.cs         # 文件删除执行器（逐目录处理、失败继续、跳过链接）
├── CleaningRules.cs       # JSON 规则加载器（内嵌默认 + 侧车覆盖 + 目录编译）
├── AgentCatalog.cs        # Agent 目录加载与编译器
├── ProjectRegistry.cs     # 项目工件注册表加载
├── rules.default.json     # 内嵌声明式规则（71 条）
├── agents.catalog.json    # Agent 收录目录（18 个 agent / 31 条编译条目）
├── projects.registry.json # 项目工件注册表（8 类项目 / 26 项可再生产物）
├── CleanLog.cs            # 清理审计日志
├── GrowthDialog.cs        # 增长分析页（含迁盘顾问）
├── AppSettings.cs         # 设置持久化
├── MainForm.cs            # 主界面
├── Theme.cs               # 界面主题系统（色彩/字体/圆角面板/自绘按钮/进度条）
├── LICENSE                # MIT
├── cleanup/               # 增长基线监控脚本等辅助工具
├── tests/Regression/      # 清理流程回归测试（零框架依赖）
└── docs/                  # 设计演进记录 + Agent 时代调研 + 目录分析
```

---

<div align="center">

## 🌐 English

**Disk governance for the AI era**

**Agents · CLIs · IDEs · Built-in AI · Model repos · Project artifacts — one exe covers them all**

[![Release](https://img.shields.io/github/v/release/JiangSuRan/CacheCleaner?color=blue&label=Latest)](https://github.com/JiangSuRan/CacheCleaner/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-blueviolet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4)](https://github.com/JiangSuRan/CacheCleaner/releases)
[![License](https://img.shields.io/badge/License-MIT-green)](#-license--许可证)

[![Rules](https://img.shields.io/badge/Rules-87-teal)](#-core-capabilities)
[![Agents](https://img.shields.io/badge/Agents-18-blue)](#-agent-catalog)
[![Artifacts](https://img.shields.io/badge/Reproducible_artifacts-26-orange)](#-core-capabilities)
[![Tests](https://img.shields.io/badge/Regression_tests-15_passing-success)](tests/Regression/)

*Self-contained single file · Portable · Zero dependencies · Safety first · Fully auditable*

[简体中文](#-cachecleaner) · **English**

</div>

---

## ✨ Why

The rise of AI agents and CLIs has created a generation of disk bloat that traditional cleaners simply don't understand: **session transcripts** that only grow, **model weights** weighing tens of GB, **self-updates** piling up old versions, **content-addressable stores** designed to never shrink — and every tool claiming its own dot-home in your user profile. The community has reported it at scale: issues of `~/.claude` reaching 3.6 GB, and the Cursor "clean cache" incident that wiped 300 GB. A cleaner that doesn't understand semantics either deletes nothing — or deletes the wrong things.

CacheCleaner's answer is a **semantics-aware rule engine**: 87 effective rules across six location classes, backed by three-tier risk labels, process guards, preview mode and end-to-end auditing — with curation maintained as a sustainable **agent catalog**. Known locations are handled automatically; unknown ones stay visible.

## 🎯 Core Capabilities

| Capability | Description |
|---|---|
| 🔍 **Three-phase smart scan** | Scans 70+ built-in rule locations in seconds, then walks the user profile to discover caches of unknown apps (supports `.cache` / `.tmp` / `.logs` dot-directories), and finally enumerates reproducible project artifacts — items hide themselves when a path doesn't exist |
| 🛡️ **Three-tier safety** | Safe / caution / danger risk labels; path validation + system-level exact whitelists; per-rule `guardProcesses` — browser and WeChat caches are skipped while those apps run, and every skip is logged |
| 👁️ **Preview mode** | Only measures what would be freed — deletes no files, runs no commands, touches no service state. See the bill before you pay it |
| 📊 **Real observability** | Cleanup summaries are based on the **free-space delta** (no paper numbers); per-item freed amounts and failure details (the exact occupied or unauthorized paths) land in audit logs |
| ♻️ **Reliable cleanup pipeline** | Command-based cleanups read output in parallel with bounded polling and stay cancellable — system maintenance processes (DISM etc.) keep running in the background, never killed; a per-item **Result** column (success / failed / skipped / reboot pending) with **real leftover sizes** verified after cleaning; one item failing never stops the run; every ending writes an audit summary |
| 📈 **Growth analysis** | Ranks large files written in the last 3/7/14 days by last-write time — directly answers "**what's eating my disk**"; offers a **migration advisor** command for big relocatable caches |
| 🧩 **JSON rule engine** | 71 built-in rules + 16 compiled from the agent catalog; drop a `rules.user.json` beside the exe to add or override rules — **no recompiling** (in the spirit of Winapp2.ini) |
| 🤖 **Agent catalog** | Curated coverage for 18 mainstream agents (Claude Code / Codex / Gemini CLI / Qwen / OpenCode / Amazon Q / Goose / Crush / Tongyi Lingma / MarsCode and more), each with detection signatures, neverTouch whitelists, confidence tiers and verified dates |
| 🗺️ **Unknown detector** | Large tool directories in the user profile that aren't covered are listed read-only — **known is automatic, unknown stays visible** |
| 🖥️ **System-level slimming** | Recycle Bin, system restore points (capped, newest kept), WinSxS component store (DISM), Windows upgrade leftovers (cleanmgr /autoclean), Windows Update cache (service stop/start automated) |
| 🐳 **Docker/WSL regenerative cleanup** | `docker system prune` measured by df delta; vhdx virtual disks compacted offline via `diskpart` — reclaims blank space, never touches data |
| 🏢 **IDEs & built-in AI** | JetBrains system caches (multi-product, multi-version), VS Code C++ ipch cache, Copilot Chat session data (read-only report), Windsurf/ZCode desktop clients |
| 🧱 **Project artifact cleanup** | Marker files identify project roots (Node/Python/Rust/.NET/Maven/Gradle/Dart/PHP); enumerates node_modules/.venv/target/bin/obj and friends, sorted by size with 90-day staleness tiers and **regeneration commands** — the #1 pain point validated by npkill/kondo |
| 🌍 **Portability** | System drive resolved at runtime (works even when Windows isn't on C:); Chinese and English system output plus decimal-comma locales; Conda installation auto-discovery |

## 🧭 Design Principles

1. **Unclear semantics? Don't touch it** — uncurated tool directories are reported only; nothing is deleted before its meaning is verified
2. **Credentials and configuration are a red line** — every catalog entry carries a neverTouch whitelist (auth / credentials / config / skills are always skipped)
3. **Sessions are user data** — always "caution" tier *and* past the age limit (30 days by default) before cleanup; conversations are never deleted silently
4. **Official commands first** — content-addressable stores only go through official prune (`pnpm store prune` / `go clean -modcache`); no raw deletion
5. **Preview first** — dry-run has zero side effects; command-based cleanups and service state are equally protected
6. **Everything audited** — every scan and cleanup: per-item freed amounts, failed paths, net disk delta — all on disk for review

## 🚀 Quick Start

1. Grab the latest `CacheCleaner.exe` from the [**Releases page**](https://github.com/JiangSuRan/CacheCleaner/releases)
2. Double-click to run (a UAC elevation prompt appears at startup — cleaning system directories and reboot-delete registration require administrator rights)
3. Click **Scan** and review everything, sorted by size
4. Unsure? Tick **Preview mode** before cleaning to see exactly what would be freed
5. Click **Clean selected** — a popup reports the real free-space gain, with per-item results

> Files that are in use are automatically registered for deletion at next reboot and honestly reported in the summary; skipped items come with their failure reasons.

## 📖 Advanced Usage

### Custom Rules

Drop a `rules.user.json` next to the exe (portable) or in `%LOCALAPPDATA%\CacheCleaner\` (installed) to add or override rules declaratively — **no recompiling**:

```json
[
  {
    "name": "MyApp cache",
    "base": "localAppData",
    "path": "MyApp\\Cache",
    "desc": "MyApp playback cache; rebuilt automatically after cleaning",
    "risk": "safe",
    "guardProcesses": ["MyApp"]
  },
  {
    "name": "Some service logs",
    "base": "windows",
    "path": "Logs\\MyService",
    "desc": "Only *.log older than 30 days",
    "risk": "warn",
    "clean": "files",
    "filesPatterns": ["*.log"],
    "minAgeDays": 30,
    "recursive": true
  },
  {
    "name": "Go module cache",
    "base": "userProfile",
    "path": "go\\pkg\\mod",
    "desc": "Official command cleanup; freed amount measured by directory delta",
    "risk": "safe",
    "clean": "command",
    "command": "go clean -modcache",
    "commandTimeoutMs": 600000
  }
]
```

Field reference: `base` (`localAppData` / `appData` / `userProfile` / `windows` / `programData` / `driveRoot`) · `risk` (`safe` / `warn` / `danger`) · `kind` (`path` / `file` / `special`) · `clean` (`directory` / `files` / `command` / `reportOnly`) · `filesPatterns` · `minAgeDays` · `recursive` · `skipSize` · `guardProcesses` · `command` + `commandTimeoutMs` (the official cleanup command and its timeout when `clean: "command"`).

### Growth Baseline Monitoring

A read-only growth-baseline script ships with the repo; run it periodically for an increment ranking of key locations:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File cleanup\growth-baseline.ps1
```

### Audit Logs

Every scan and cleanup is logged to `%LOCALAPPDATA%\CacheCleaner\logs\clean-date.log`: item sizes, per-item freed amounts, the exact occupied/unauthorized paths, and the before/after free-space delta — "the cleanup didn't help" is now a question with evidence.

## 🤖 Agent Catalog

The full list lives in [docs/agent-catalog.md](docs/agent-catalog.md). Curation standards:

- An entry becomes cleanable only with **official documentation or source-level evidence**; agents with unverified semantics land in the "uncatalogued report" instead
- Every entry declares a `detect` signature (marker files), a `neverTouch` whitelist, a `confidence` tier and a `verified` date
- Layout-drift protection: entries hide automatically when their directories are absent; users can hot-fix with `rules.user.json`

Issues contributing evidence for new agents (with official docs or source links) are welcome — verified entries get catalogued.

## 🏗️ Engineering Quality

- 🧪 **Regression suite** — [`tests/Regression`](tests/Regression/) with zero framework dependencies: parallel pipes under heavy output, timeout, cancellation, preview deleting nothing, real deletion, recent-file retention, continue-on-failure, leftover-size accounting and read-only UI behavior; real deletion only targets random directories the suite creates itself
- 📦 **Self-contained single file** — one exe embeds the .NET 10 runtime and all rule assets; copy and run
- ✅ **Zero-warning builds** — Release builds with 0 errors / 0 warnings
- ♿ **Accessibility** — fully custom-drawn buttons keep the PushButton role, screen-reader names and Tab/Space/Enter operation
- 🖼️ **Unified UI** — borderless window, three-tier button hierarchy with a dynamic primary button, thin rounded progress bar; verified on a real machine at 100% scaling

## 🛠️ Tech Stack & Build

- C# / .NET 10 WinForms, zero external dependencies, self-contained single-file publish
- Optional Inno Setup installer

```bash
# Build (target: zero warnings)
dotnet build -c Release

# Publish self-contained single file
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish

# Cleanup regression tests (real deletion only targets the suite's own random directories)
dotnet run --project tests/Regression/Regression.csproj -c Release

# Real-window checks (creates sample rows; saves no user settings, runs no cleanup)
dotnet run --project tests/Regression/Regression.csproj -c Release -- --ui

# Build installer (requires Inno Setup)
ISCC setup.iss
```

## 📸 Screenshots · 截图

| Main window 主界面 | Scan results 扫描完成（sorted by size · risk labels · per-item results） |
|---|---|
| ![Main](docs/screenshots/main.png) | ![Scan](docs/screenshots/scan.png) |

## 📄 License · 许可证

MIT
