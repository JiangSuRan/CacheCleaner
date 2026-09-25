# 可行性分析：主流 Agent 目录（Catalog）与自动收录机制

> 分析日期：2026-09-26 · 依据：10 个主流 agent 的源码/官方文档核实（GitHub 38 次查询）+ 本机 7 个 agent 实测 + 社区案例
> 结论先行：**可行，且建议做**。数据来源可靠（10 个主流 agent 中 8 个有官方文档或源码级证据）、规则引擎已具备全部所需能力（零新引擎代码）、维护模型可自洽（置信分级 + 存在性隐藏 + 侧车修正）。主要风险是布局漂移与语义误判，均有成熟缓解手段。

## 1. 目标定义

「Agent 目录」= 一份**结构化的主流 agent 收录清单**（JSON 数据文件），每个 agent 声明：

- **身份与探测签名**：home 目录 + 特征文件（如 `.claude/config.json`），用于判定 agent 是否存在于本机
- **存储分类**：每个子目录标注为 `cache`（可再生，Safe）/ `sessions`（用户数据，Warn + 超龄清理）/ `models`（只读报告）/ `config`（绝不触碰）
- **neverTouch 清单**：凭证与配置的白名单
- **来源与置信**：documented / source-verified / inferred / unverified + 核实日期

探测器运行时：签名命中 → 该 agent 的条目编译为现有规则（复用 `filesPatterns`/`minAgeDays`/`recursive`/`cleanCommand` 全部能力）；家目录发现**未收录**的疑似 agent → 报告占用并提示纳入。这样"检测所有 agent" = 已知全自动、未知看得见。

## 2. 主流 Agent 收录草案（v4.1 目录数据）

置信分级：🟢 本机实测 · 🔵 官方文档 · 🟣 源码核实 · 🟡 推断 · ⚪ 未核验

| Agent | 仓库/星数 | Windows 存储位置 | 可清理（class） | 绝不触碰 | 置信 |
|---|---|---|---|---|---|
| Claude Code | anthropics/claude-code 148k | `~/.claude` | `projects/*.jsonl`（会话，30 天超龄）、`shell-snapshots`、`cache` | `CLAUDE.md`、`settings.json`、`.credentials.json`、`plugins/agents/skills/commands`、`history.jsonl` | 🟢+🔵 |
| Codex CLI | openai/codex 126k | `~/.codex` | `log/`、`archived_sessions/`；`sessions/YYYY/MM/DD/`（超龄） | `config.toml`、`auth.json`、`.credentials.json`、`history.jsonl` | 🟢+🔵 |
| Gemini CLI | google-gemini/gemini-cli 107k | `~/.gemini` | `tmp/<hash>/`（注意含手动 `/chat save` 保存点）、`history/<hash>/`（均超龄） | `settings.json`、`oauth_creds.json`、`google_accounts.json`、`mcp-oauth-tokens.json`、`commands/skills/agents/policies/extensions` | 🔵 |
| OpenCode | anomalyco/opencode 210k | `~/.local/share/opencode` | `log/`（自带保留 10 个）；官方卸载命令 `opencode uninstall` | `auth.json`、`project/*/storage`（会话） | 🔵 |
| Qwen Code | QwenLM/qwen-code 28k | `~/.qwen` | 同 Gemini 结构（fork） | 同 Gemini | 🟡 |
| Aider | Aider-AI/aider 49k | 各 git 仓库根 `.aider*` | `.aider.tags.cache.v4`、`.aider.chat.cache.v4`、`.aider.repo.cache.v4`（SQLite 缓存） | `.aider.chat.history.md`（会话）、`.aider.conf.yml` | 🔵 |
| Goose | aaif-goose/goose 55k | `%APPDATA%\Block\goose\` | data 分区中的会话按超龄 | config 分区、`auth.json` | 🟣(Win🟡) |
| Crush | charmbracelet/crush 28k | `~/.config/crush` + `~/.local/share/crush` | data 分区 SQLite 会话超龄 | config 分区、`crush.json` | 🟣 |
| Amazon Q CLI | aws/amazon-q-developer-cli 2k | `~/.aws/amazonq/` | `cli-checkouts/`（影子仓库缓存）、`.cli_bash_history`（超龄） | `mcp.json`、`cli-agents/`、`prompts/`、`profiles/`、`knowledge_bases/` | 🔵 |
| 通义灵码 | 闭源 | `~/.lingma` | `cache/`、`logs/`、`index/`（重建） | `bin/`、`vscode/`（内嵌运行时）、`model/` | 🟢 |
| MarsCode | 闭源 | `~/.marscode` | `.ckg/`（索引重建）、`logs/` | `ai-chat/`（对话数据） | 🟢 |
| Trae CN / Cursor / Windsurf 等 IDE 系 | 闭源 | `%APPDATA%\<app>` | Cache/CachedData/GPUCache 等由现有 Electron 扫描器覆盖 | `User/`（设置与工作区） | 🟢(扫描器) |

**排除说明**：Cursor/Windsurf 的 AI 数据目录布局无公开文档（⚪），仅收录其 VS Code 惯例缓存（已由扫描器覆盖）；`.pi`、`.codegeex` 语义未核验，进「未收录报告」而非清理。

**官方清理命令现状**：仅 Claude Code（`claude project purge`）与 OpenCode（`opencode uninstall`）有官方命令，其余均需工具侧实现——这正是本目录的价值。

## 3. 与现有规则引擎的兼容性：零新引擎能力

目录条目到现有规则字段是 **1:1 编译**，不需要任何新执行机制：

| Catalog 概念 | 引擎现有字段 |
|---|---|
| `class: cache` | `risk: safe` + `clean: directory` |
| `class: sessions` | `risk: warn` + `filesPatterns` + `minAgeDays: 30` + `recursive` |
| `class: models` | `risk: warn` + `clean: reportOnly` |
| `neverTouch` | 探测器生成规则时直接跳过这些路径（双重保险） |
| 探测签名 | 新增探测器（扫描 home dot-dir + 特征文件存在性），未命中则规则隐藏（现有的 Exists 机制天然支持） |

唯一的新增代码：**探测器**（家目录 dot-dir 枚举 + 签名匹配 + 未收录报告），估计 150-200 行 + 一份数据文件。

## 4. 风险与缓解

| 风险 | 实例 | 缓解 |
|---|---|---|
| 布局漂移 | Claude Code 的 `todos/statsig/logs` 已成 legacy，新版本不再写入 | 条目带 `verified` 日期；目录不存在即隐藏；侧车 `rules.user.json` 可即时修正 |
| 语义误判 | **Cursor 论坛案例：用户"清理缓存"被误删 300GB 数据**（2026-04） | 三级风险 + 会话类一律 Warn + 超龄才删 + 预览模式 + neverTouch 双保险 |
| 新 agent 漏收 | 未收录的 dot-dir | 探测器输出「未收录报告」（占用 + 特征文件），提示用户侧车纳入——看得见比清得掉更重要 |
| 手动保存数据误删 | Gemini `tmp/` 中含 `/chat save` 的手动保存点 | 会话类 minAgeDays 30 + Warn + Desc 明示后果 |

## 5. 结论与建议

**可行**，且是当前竞品都没有的能力（zclean/claude-code-cleaner 等均为无签名的硬编码列表，无未收录报告）。建议作为 **v4.1「Agent 目录」**实施，工作量约一天：

1. `agents.catalog.json` 数据文件（12 个 agent，按上表置信分级收录）
2. 探测器（签名识别 + 条目编译 + 未收录报告，约 200 行）
3. 目录文档页（README 链接 + 收录标准：有官方文档或源码级证据才收录，置信 ⚪ 一律只报告不清理）

收录流程即维护流程：新 agent 出现 → 社区提 issue 附布局证据 → 按置信分级收录 → 探测器自动生效。
