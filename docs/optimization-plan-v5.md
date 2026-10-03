# v5.0 优化方案：项目工程清理引擎（重新设计）

> 日期：2026-09-26 · 依据：开源社区专项调研（kondo/npkill/bunkill/pyclean/czkawka 源码级分析，26 次查询）+ 自我根因分析
> 结论先行：**清理能力不行的根因不是规则不够多，而是刻意跳过了占用最大的一类东西——项目工程工件（node_modules / venv / target / build / obj）**。v5.0 以「项目工程清理引擎」为核心重设计，这是 npkill 9.4k 星验证过的第一大开发者痛点。

## 1. 根因分析：为什么现在的清理能力不够

诚实的自我审视。当前工具（v4.3，87 条生效规则）覆盖了 agent/CLI/IDE/系统缓存，但存在一个结构性盲区：

1. **SkipDirNames 黑名单恰好排除了磁盘上最大的东西**。自动发现阶段跳过 `node_modules`、`venv`、`.venv`、`build`、`target`、`obj`、`dist`、`out`、`packages`——当初的理由是怕误删项目内容，但这正是几十 GB 的所在。一个有 30 个历史项目的机器，仅 `node_modules` 就能占 20-60 GB。
2. **没有「项目」概念**。工具理解"缓存目录"，不理解"这是一个 Node 项目、它的 node_modules 可以用 npm install 再生"。而开发者真正想清理的往往是：三个月没动的旧项目里的构建产物。
3. **npkill 9,455 星 / kondo 2,427 星**证明了项目工件清理是开发者磁盘痛点的第一名——我们的工具在这个维度上是空白。

## 2. 社区调研结论（实现细节级）

| 项目 | 星数 | 核心做法 | 可借鉴点 |
|---|---|---|---|
| [kondo](https://github.com/tbillington/kondo) | 2,427 | **标记文件识别项目**（22 种类型）：根层第一层文件名匹配 `package.json`/`Cargo.toml`/`pyproject.toml`/`.csproj`…，命中即停止下钻（项目不嵌套）；每种类型一张**产物目录白名单**（Cargo→`target`；Node→`node_modules`/`.angular`；Python→`__pycache__`/`.mypy_cache`/`.pytest_cache`/`.tox`…；.NET→`bin`/`obj`；Gradle→`build`/`.gradle`；Unity→`Library`/`Temp`…），只删 `root/<产物>` 精确路径 | 项目检测模型 + 产物注册表（源码级核实，直接可抄的映射表） |
| [npkill](https://github.com/voidcosmos/npkill) | 9,455 | **8 worker × 100 并发**扫描 + 已知无价值目录整树剪枝 + size/mtime 懒计算；**profiles 机制**按生态列"可安全删除目录"并注明**再生成命令**；危险路径分析（home 根/系统目录/UNC） | 并行扫描架构 + 「再生成命令」提示 + 危险路径守卫 |
| [bunkill](https://github.com/codingstark-dev/bunkill) | 37 | 深度上限（10/quick 5）、`package.json` mtime <30 天判**活跃项目**、系统路径黑名单 | 活跃度判定 |
| [pyclean](https://pypi.org/project/pyclean/) | 92 | 产物不只是目录——散文件 debris（`*.egg-info`、`coverage.xml`、`*.pyc`）；**目录变空才删目录**；dry-run | 非目录型产物 + 空目录回收 |
| [czkawka](https://github.com/qarmin/czkawka) | 33,864 | 通用垃圾/重复文件（非项目语义） | 定位差异参考 |

**社区共性**（全部如此，无一例外）：「可再生」= 硬编码白名单；新鲜度只用于展示排序（**没有一家自动按年龄删**）；逐项人工确认是安全底线。

**社区空白**（新设计的差异化机会）：① 无 Windows 专项保护（OneDrive/重解析点/长路径）；② 产物注册表不统一、无再生成命令标准化；③ 无 dry-run 审计报告流；④ 无「30 天未动才标记」的陈旧度策略与扫描集成；⑤ store 类缓存与项目产物混为一谈。

## 3. v5.0 设计：项目工程清理引擎

### 3.1 第三扫描阶段「项目工程扫描」

在现有两阶段（已知规则 + 自动发现）之后增加第三阶段：

- **项目检测**：从用户目录（及选定的开发盘）遍历，按 kondo 式标记文件在**目录级**识别项目（首版 8 类：Node `package.json`、Python `pyproject.toml`/`setup.py`、Rust `Cargo.toml`、Go `go.mod`、Maven `pom.xml`、Gradle `build.gradle(.kts)`、.NET `.csproj`/`.sln`、Dart `pubspec.yaml`）；命中即登记项目并**不再向项目内部下钻**（防嵌套、提性能）
- **产物枚举**：按类型查产物注册表（3.2），对每个存在的 `root/<产物目录>` 计算体积
- **陈旧度**：产物目录 LastWriteTime 与项目根最近修改时间 → `陈旧（>90 天）/ 活跃`；**陈旧且 ≥500MB 自动勾选**，活跃项目默认不勾（遵循社区底线：新鲜度用于分级，不自动删）

### 3.2 产物注册表（数据文件 `projects.registry.json`，与 agent 目录同构）

```json
{
  "projectTypes": [
    {
      "id": "node",
      "marker": "package.json",
      "artifacts": [
        { "path": "node_modules", "regenerate": "npm install", "note": "依赖重装" },
        { "path": ".next", "regenerate": "npm run build" },
        { "path": ".nuxt" }, { "path": ".svelte-kit" }, { "path": ".turbo" },
        { "path": ".parcel-cache" }, { "path": "coverage" }, { "path": ".eslintcache", "file": true }
      ]
    },
    {
      "id": "python",
      "marker": "pyproject.toml",
      "markers": ["setup.py", "requirements.txt"],
      "artifacts": [
        { "path": ".venv", "regenerate": "python -m venv .venv && pip install -r requirements.txt" },
        { "path": "venv" }, { "path": "__pycache__" }, { "path": ".pytest_cache" },
        { "path": ".mypy_cache" }, { "path": ".ruff_cache" }, { "path": ".tox" }, { "path": "*.egg-info", "glob": true }
      ]
    },
    { "id": "rust", "marker": "Cargo.toml", "artifacts": [{ "path": "target", "regenerate": "cargo build" }] },
    { "id": "dotnet", "marker": ".csproj", "markersSuffix": [".sln"], "artifacts": [{ "path": "bin" }, { "path": "obj" }] },
    { "id": "maven", "marker": "pom.xml", "artifacts": [{ "path": "target" }] },
    { "id": "gradle", "marker": "build.gradle", "markersSuffix": [".gradle.kts"], "artifacts": [{ "path": "build" }, { "path": ".gradle" }] },
    { "id": "go", "marker": "go.mod", "artifacts": [] },
    { "id": "dart", "marker": "pubspec.yaml", "artifacts": [{ "path": "build" }, { "path": ".dart_tool" }] },
    { "id": "php", "marker": "composer.json", "artifacts": [{ "path": "vendor", "regenerate": "composer install" }] }
  ]
}
```

`regenerate` 命令直接进 Desc（npkill profiles 的好点子）——用户删之前就知道怎么恢复。

### 3.3 安全模型（复用既有机制 + 项目特有规则）

1. 只删 `项目根/<注册表产物>` **精确路径**；项目根的源文件、配置、`.git`、锁文件永不触碰
2. 重解析点跳过（OneDrive/符号链接安全）；长路径由 .NET 原生支持
3. 产物条目 `Safe` 级 + Desc 注明再生成命令；**活跃项目产物 Warn**（可能正在开发中）
4. 预览模式天然覆盖（干跑统计）；审计日志记录每个被删的工件目录与项目路径
5. home 根直下的目录、系统目录走既有危险路径守卫（npkill isDangerous 的思路我们已有）

### 3.4 性能

- 项目检测阶段的剪枝：进入项目根后不再深扫（kondo 证明足够）；跳过 SkipDirNames 中纯缓存/依赖内部
- 体积懒计算沿用现有 robocopy 路径；大目录并行化留待后续（当前阶段一足够）

## 4. 实施计划

| 阶段 | 内容 | 工作量 |
|---|---|---|
| v5.0-a | `projects.registry.json` + ProjectDetector 扫描器（检测/枚举/陈旧度/条目生成）+ UI 呈现 | 1 天 |
| v5.0-b | 散文件 debris 支持（glob 型产物）+ 空目录回收 | 0.5 天 |
| v5.0-c | 扫描范围扩展到其他开发盘（可选目录）+ 并行扫描 | 0.5 天 |

## 5. 与既有能力的协同

- 产物条目走规则引擎同一管道：守卫、预览、审计、大小降序全部继承
- 未收录探测器与项目检测互补：dot-home 归 agent 目录，项目根归工程清理，覆盖面合成完整闭环
- 增长分析持续回答「谁在吃盘」，项目清理回答「能立刻拿回多少」
