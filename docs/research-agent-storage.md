# 调研报告：Agent/CLI 时代的个人开发者磁盘膨胀问题与清理策略

> 调研日期：2026-09-26 · 方法：本机只读实测 + Web 检索 + GitHub 生态扫描（11 组查询）
> 结论先行：AI agent 与 CLI 工具链正在成为个人开发者磁盘的新型大头，社区已大量报障但**清理工具赛道极度分散且无事实标准**；解法是「清 / 挪 / 限 / 防」四字策略，CacheCleaner 已具备其中「清与防」的完整框架，补一个 Agent 规则包即可占住这个生态位。

## 1. 问题定义：为什么"agent 时代"磁盘不够用了

传统开发缓存（npm/pip/IDE）增长缓慢且有成熟清理习惯。Agent 时代新增了四类增长源，共同特点是**隐蔽、多层、自更新**：

| 类别 | 典型位置 | 增长机制 |
|---|---|---|
| ① Agent/CLI 自身 | `~/.codex`、`~/.claude`、`~/.lingma`、`~/.trae-cn`、`~/.qwen`… | 会话转录/项目缓存/遥测只进不出；运行时自更新留下旧版本目录；每个新 AI 工具都在家目录建一个 dot-home |
| ② 模型与数据仓库 | `~/.ollama/models`、`~/.cache/huggingface`、`~/.cache/torch` | 模型权重以 GB 计，下载即囤积；KV/会话缓存可达每会话数十 GB（Hacker News 上 Claude Code 团队讨论确认） |
| ③ 包管理器存储 | npm/pnpm/yarn/uv/cargo/go/gradle/maven/conda 的 cache 与 store | 内容寻址存储（pnpm store、cargo registry）设计上只增不减 |
| ④ 项目与虚拟化层 | `node_modules`、`venv`、`target/`、Docker 镜像、WSL vhdx | 每个项目数百 MB；vhdx/镜像只增不减 |

社区证据（2026 年集中爆发）：
- GitHub issue **[BUG] No disk space management: ~/.claude grows**：用户报告 `~/.claude` 涨到 3.6 GB，请求官方提供 `claude clean`；
- Milvus 博客《How Claude Code Manages Local Storage》：确认 `~/.claude` 中的 sessions/transcripts/caches 持续累积；
- 多篇 Ollama/HuggingFace 指南教用户手工 `ollama rm` / `huggingface-cli delete-cache`。

## 2. 本机实证（Windows 11 开发机，2026-09-26 只读实测）

29 个存在项合计 **13.7 GB**，全部属于上述四类（不含 IDE/浏览器等传统大头）：

| 分层 | 存在项 | 合计 | 备注 |
|---|---|---|---|
| Agent dot-home | `.codex` 2003、`.lingma` 800、`.claude` 741、`.pi` 602、`.trae-cn` 400、`.marscode` 364、`.codegeex` 331、`.copilot` 28 | **4.67 GB** | 8 个 AI 工具各自为政 |
| AI 应用运行时 | Doubao 1218、OpenAI 549、OpenCodex 475、oak 232、ms-playwright-go 186、cloud-code 145、xljsci 145、agy 144、copilot-sdk 143 | **2.94 GB** | 自更新目录堆积是主因 |
| 包管理器 | `.local` 1194（pipx/uv 工具）、uv tools 354、npm-cache 158 | **1.71 GB** | |
| IDE 扩展 | `.vscode` 3272、`.positron` 537 | **3.81 GB** | 扩展只装不删 |
| 模型仓库 | （本机未装 Ollama/HF） | 0 | 装机后通常 +10~50 GB |

**纵向对照**：09-25 基线中 `.cache` 1790 MB、`.nuget` 307 MB、pnpm-cache 213 MB、npm-cache 403 MB，本次实测分别降至 0/0/0/158 MB——说明「定位→清理」闭环确实有效，问题在工具而非不可解。

## 3. 官方清理命令对照表（优先用官方 prune，而非裸删）

| 生态 | 官方清理命令 | 语义 |
|---|---|---|
| npm | `npm cache clean --force` | 清下载缓存，不影响项目 |
| pnpm | `pnpm store prune` | 只删无引用包，**活跃项目安全** |
| yarn | `yarn cache clean` | 清全局缓存 |
| uv | `uv cache clean` | 清缓存 |
| pip | `pip cache purge` | 清 wheel 缓存 |
| conda | `conda clean --all` | 清 tarball/索引/未用包，**不动环境** |
| cargo | `cargo clean` / `cargo cache -a`（cargo-cache 工具） | 删 target / 自动清注册表缓存 |
| Go | `go clean -cache` / `-modcache` / `-testcache` | modcache 重下即可，可释放 5-30 GB |
| Gradle | 停守护进程后删 `~/.gradle/caches` | 下次构建重新下载 |
| Docker | `docker system prune -a --volumes` | 删未使用镜像/容器/卷，**运行中不受影响** |
| Ollama | `ollama list` → `ollama rm <model>` | 按模型删除，勿手工删 blobs |
| HuggingFace | `huggingface-cli delete-cache`（TUI） | 按模型/数据集选择删除，可重下 |

**要点**：内容寻址类存储（pnpm store、cargo registry、conda pkgs）必须走官方 prune 才能保证引用安全；裸删 `node_modules`/构建缓存则是安全的（可再生）。

## 4. 开源工具格局（GitHub 实测扫描）

| 赛道 | 代表项目 | 星数 | 状态 |
|---|---|---|---|
| node_modules 清理 | **voidcosmos/npkill** | **9,453** | 活跃，赛道霸主，证明此类需求真实且巨大 |
| 通用系统清理 | BleachBit / winapp2.ini 生态 | 老牌 | 面向系统缓存，**不理解开发者语义** |
| Docker 清理 | chadoe/docker-cleanup-volumes | 1,436 | 多已废弃——内置 prune 赢了 |
| Mac 开发者清理 | spark-clean、macOS-dev-cache-cleaner | ~49 each | 已覆盖 Ollama/JetBrains/Homebrew |
| **AI/Agent 专用** | zclean(67)、Jharu(77)、claude-code-cleaner(38)、AICacheCleaner(5, Windows)、codex-clean、Kempt、cc-cleaner | **全部 ≤100** | **11+ 个活跃项目同场竞争，无一成标准** |

**关键判断**：AI 专用清理是 2025-2026 年新出现的细分赛道，项目全部仓促上马、两极分化（多目标清理器 vs 单目标专清）、**Windows 端最弱**（唯一专注 Windows 的 AICacheCleaner 仅 5 星）。竞争者宣称的差异化卖点恰恰是安全语义——「清会话缓存但绝不碰对话/配置/MCP」。这是 CacheCleaner 的直接机会：它已有三级风险、进程守卫、预览模式、审计日志——安全语义正是它的既有强项。

## 5. 解决方案框架：清 / 挪 / 限 / 防

- **清（Clean）**：有官方 prune 的走官方命令（保证引用安全）；纯再生的（node_modules、构建缓存、旧版本目录）直接删。删除前给预览。
- **挪（Relocate）**：大缓存官方支持迁盘——`npm config set cache D:\dev-cache\npm`、`PIP_CACHE_DIR`/`UV_CACHE_DIR`/`HF_HOME`/`CARGO_HOME`/`GOPATH` 环境变量、`wsl --export/--import` 迁移 vhdx、Docker Desktop 磁盘镜像位置设置。**对小容量系统盘这是比反复清理更根本的解**。
- **限（Bound）**：定期跑增长基线（CacheCleaner 的 `growth-baseline.ps1` 已实现），对增长超过阈值的类别设置周期性 prune（如每月 `conda clean`、`pnpm store prune`）。
- **防（Prevent）**：规则化。新工具出现时以 JSON 规则纳入（CacheCleaner 已支持侧车热增改），而不是等下一次"磁盘又满了"。

## 6. CacheCleaner 对策路线（把调研变成产品）

现状盘点：已覆盖 OpenAI/OpenCodex 旧版本、Doubao 热更新、npm/pnpm/uv/conda/nuget、`~/.cache` 兜底、增长分析、遗留追踪检测——**约四成 agent 时代位置已有规则**。

建议的 v4.0「Agent 规则包」路线（按收益排序）：

1. **规则引擎加 `cleanCommand` 字段**：让规则能调用官方 prune 命令（`pnpm store prune`、`conda clean --all`、`go clean -modcache`）并捕获执行结果——区别于裸删，这是对标竞品安全语义的关键一步。
2. **Agent dot-home 规则包**：`.claude`（仅 projects 会话缓存 + 超龄清理，绝不碰 config/memory/credentials）、`.lingma`、`.trae-cn`、`.marscode`、`.codegeex` 等按「缓存子目录白名单」纳入——语义与微信 `msg` 目录同款：会话是用户数据，缓存可再生。
3. **模型仓库项**：Ollama（`~/.ollama` 只报告 + 提示 `ollama rm`）、HF 缓存（报告 + 提示 `huggingface-cli delete-cache`），标 Warn——模型是"花了流量换来的"，只提醒不代删。
4. **迁盘顾问**：增长分析对超阈值缓存给出官方迁盘命令建议（只展示不执行），填补「挪」这一没人做的空位。
5. **会话保留策略**：`minAgeDays` 已支持，对 agent 会话类规则默认 30 天，兼顾"保留近期上下文"与"回收历史占用"。

## 7. 来源

- GitHub issue：~/.claude 无磁盘管理（[github.com](https://github.com)）；Claude Code 本地存储机制（[milvus.io](https://milvus.io)）；Claude Code 团队 HN 讨论（[news.ycombinator.com](https://news.ycombinator.com)）
- 官方文档：[pnpm store prune](https://pnpm.io)、[conda clean](https://docs.conda.io)、[Cargo Book](https://doc.rust-lang.org/cargo/)、[pkg.go.dev](https://pkg.go.dev)、[HuggingFace caching](https://huggingface.co)
- 工具仓库：[voidcosmos/npkill](https://github.com/voidcosmos/npkill)、[npmjs.com](https://www.npmjs.com)、zclean / Jharu / claude-code-cleaner / AICacheCleaner 等（GitHub 2026-09 搜索实测）
- 本机实测：`_tmp/agent_storage.ps1`（只读，29 个位置）
