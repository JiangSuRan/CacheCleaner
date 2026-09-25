# CacheCleaner 升级方案（v3.4 → v4.0）

> 基于 2026-09-24 的代码评估与 2026-09-24/25 的本机只读诊断。
> 结论先行：清理效果不理想 = 3 个执行 bug + 释放量虚高 + 覆盖面缺口；C 盘持续缩小 = 六类增长源叠加，本机 24h 净变化 ≈ 0（非匀速流失）。

## 1. 本机诊断快照（2026-09-25，C 盘可用 72.3 / 198.9 GB）

**确认的增长源（按证据强度）：**

| # | 增长源 | 实测 | 性质 |
|---|--------|------|------|
| 1 | 遗留 WPR 追踪会话（`WPR_initiated_..._20260922_1_EC_0` 仍在运行）+ NT Kernel Logger.etl 100 MB | 实时写入 | 唯一"正在写盘"的会话，需人工终止 |
| 2 | `C:\Windows\Logs\CBS` 服务日志 | 695 MB / 5 天 5 次 | 频率异常，疑似更新反复失败重试 |
| 3 | VS Code `CachedExtensionVSIXs` | 2,207 MB | 每次扩展更新囤积 VSIX，只进不出（本机第一大可清项） |
| 4 | Illustrator 崩溃转储（`Temp\Silent Process Exit\`） | 220 MB / 2 次 | 每崩一次新增约 110 MB，7 天规则下滞留一周 |
| 5 | AI 工具自更新版本堆积（Codex `bin` 双版本、`OpenAI` 640 MB、`OpenCodex` 475 MB、豆包 hot_fix+gecko_cache 337 MB） | ≈1.6 GB 可回收 | 结构性：更新频繁、旧构建不删 |
| 6 | 用户数据自然膨胀：`xwechat_files` 3.16 GB、`BaiduYunKernel` 2.29 GB、`Roaming\Tencent` 2.74 GB、`Roaming\baidu` 2.29 GB | ≈5.4 GB | 只能迁盘/白名单，不能盲删 |

**已排除：** 系统还原点（卷影占用 0，上限已 3 GB）、pagefile（1 GB）、hiberfil（无）、Docker/WSL vhdx（无）、搜索索引（约 180 MB）。WinSxS 20.7 GB 为硬链接重复计数。

**空间去向（真实值）：** Users ≈ 60 GB（AppData 31 GB、OneDrive 10.6 GB、Videos 5.4 GB、十几个 AI/IDE dot-home 合计约 10.5 GB）；Windows ≈ 50 GB（含 WinSxS 虚高约 10 GB，`Installer` 7.1 GB，`DriverStore` 5 GB，`Windows\Temp` 3 GB）；Program Files 合计 37.3 GB；ProgramData 4.3 GB；`C:\nodejs` 2.4 GB。

## 2. 立即行动（用户侧，管理员 PowerShell 执行）

```powershell
# ① 终止遗留的 WPR 录制（09-22 启动的那个会话）
wpr -cancel
# 若上面无效，按会话名停止：
logman stop "WPR_initiated_DiagTrackMiniLogger_OneTrace_User_Logger_20260922_1_EC_0" -ets

# ② 停止手动实例的 NT Kernel Logger（Circular Kernel Context Logger、EventLog-* 等是系统正常会话，不要动）
logman stop "NT Kernel Logger" -ets

# ③ 删除已写下的追踪文件（停止后才可删）
Remove-Item "C:\Windows\System32\LogFiles\WMI\NT Kernel Logger.etl" -Force
Remove-Item "C:\Windows\System32\LogFiles\WMI\NetCore.etl" -Force

# ④ 次日复核：logman query -ets 中不应再有 WPR_initiated_*；监控脚本显示 WMI trace logs 不再增长
```

**CBS 根因排查**（治本）：打开「设置 → Windows 更新 → 历史记录」看是否有反复失败的 KB；如有，先 `DISM /Online /Cleanup-Image /RestoreHealth` + `sfc /scannow` 修复组件存储，CBS 日志爆发会随之回落。删日志只治标（阶段 1 会加规则）。

## 3. 阶段 0 —— v3.4 修复（约半天，全部在 CacheScanner.cs / MainForm.cs）

> ✅ 状态：已实施（2026-09-26）。5 项全部落地，`dotnet build -c Release` 零警告，自包含单文件已发布至 `publish\`；尚未提交 git，待用户复核。GitHub Desktop 旧版本的释放量统计（同类缺陷）也已一并修正，共 4 处。

| # | 问题 | 位置 | 改法 | 验收 |
|---|------|------|------|------|
| 1 | 命令式清理项被 `Directory.Exists` 门卫挡死：「系统还原点」Path 是占位符、「系统内存转储」是文件路径，清理函数均为死代码 | CacheScanner.cs:525 | 把「系统还原点（卷影副本）」分发挪到门卫之前（与 DNS 同层）；门卫改 `Directory.Exists(path) \|\| File.Exists(path)` | 还原点清理后 `vssadmin list shadowstorage` 上限变 3 GB；Memory.dmp 存在时可删 |
| 2 | 释放量在 `file.Delete()` 前累加，失败文件也计入「共释放」 | CacheScanner.cs:654/708/772 | 改为 Delete 成功后 `freed += file.Length`；登记重启删除的文件单独归类，不计入本次释放 | 构造被占用文件时，报告的释放量 ≤ 磁盘可用空间实际增量 |
| 3 | RunCommand 忽略 WaitForExit 超时，DISM >30 分钟时 ExitCode 抛异常误报失败，进程仍在后台跑 | CacheScanner.cs:1566 | DISM 超时放宽到 2 小时；`WaitForExit` 返回 false 时不得读 ExitCode，按「仍在执行」如实报告 | DISM 长任务不再假报失败 |
| 4 | 自动发现白名单含 `session storage`/`blob_storage`（删了丢登录态），且自动发现项默认勾选 | CacheScanner.cs:94、MainForm.AddCacheRow | 白名单移除这两项；自动发现行一律不默认勾选；自动发现的 `log(s)` 匹配改判 Warn | 全新扫描中不存在可勾选的 Session Storage 项 |
| 5 | 列表无排序，大头排在底部，用户感知不到收益 | MainForm（AddCacheRow 前） | 按 SizeBytes 降序插入（命令式无路径项置底） | 扫描完成最大项在第一行 |

## 4. 阶段 1 —— 覆盖面扩展（1-2 天，按本机收益排序）

| 新增规则 | 路径 / 方式 | 风险 | 本机预期 | 备注 |
|---|---|---|---|---|
| VS Code/Cursor VSIX 缓存 | `%APPDATA%\{Code,Cursor}\CachedExtensionVSIXs`，整目录清空 | Safe | **2.2 GB** | 需要时自动重下 |
| CBS 服务日志 | `C:\Windows\Logs\CBS\CbsPersist_*`，删 30 天前 | Warn | 695 MB | 被占用走重启删除 |
| pnpm 缓存 | `%LOCALAPPDATA%\pnpm-cache` 与 `%LOCALAPPDATA%\pnpm\store` | Safe | 213 MB | |
| 豆包热更新资源 | `Doubao\User Data\hot_fix`、`gecko_cache`（gecko_cache 已在白名单，补 hot_fix） | Safe | 337 MB | |
| AI 工具旧版本 | `OpenAI\Codex\bin\<hash>`、`runtimes\*\<hash>`、`OpenCodex`：仿 GitHubDesktop 保留最新版；`@zcodedesktop-updater` 已被 `*-updater` 扫描覆盖 | Safe | ≈500 MB | 哈希目录按时间保留最新 |
| icon 缓存 | Explorer 目录的 `iconcache_*` 并入现有缩略图规则 | Safe | 115 MB | explorer 占用走重启删除 |
| UWP 安全缓存族 | `%LOCALAPPDATA%\Packages\*\AC\INetCache`、`AC\Temp`、`TempState`（通配聚合项） | Safe | 68 MB | 不碰 LocalCache（应用数据） |
| GPU 厂商着色器缓存 | `%LOCALAPPDATA%\NVIDIA\{DXCache,GLCache}`、`AMD\{DxCache,DxcCache,GLCache}`、`Intel\ShaderCache` | Safe | 50 MB | |
| Electron 子目录推广 | 把 `ScanChromiumCacheSubDirs` 推广到 Code/Cursor/Trae CN/Qoder/Positron/CherryStudio/Claude，白名单加 `CachedData`、`CachedExtensionVSIXs` | Safe | ≈100 MB | 现规则只清 Code\Cache（5.9 MB） |
| Windows Update 正确姿势 | 清 `SoftwareDistribution\Download` 前停 `wuauserv/bits`，清后启回；DO 改用官方 `Delete-DeliveryOptimizationCache -Force` | Safe | — | 已核实官方 cmdlet 存在 |
| Windows.old / 升级残留 | 探测 `C:\Windows.old`、`C:\$GetCurrent`、`C:\ESD` 并报告大小；清理集成 `cleanmgr /autoclean` | Warn | 本机无 | 动辄 10-30 GB |
| 更多浏览器 | Firefox `Profiles\*\cache2`；Brave/Vivaldi 复用 Chromium 子目录扫描 | Safe | 本机无 | |
| 飞书缓存 | `Roaming\LarkShell`（1.2 GB）——先核验子目录构成再定白名单 | 待核验 | 1.2 GB | 检测名单已有进程，缺规则 |
| Tencent 白名单 | `Roaming\Tencent\{WeChat,xwechat,WeType,WeGame}` 逐个核验子目录后接入，绝不整删 | 待核验 | 最高 2.7 GB | xwechat 旧目录先确认是否 4.0 迁移残留 |
| workspaceStorage | `{Code,Cursor,Positron}\User\workspaceStorage` | Warn | ≈120 MB | 清了重置工作区状态，不默认勾选 |
| Package Cache | 只读报告大小 + 建议，不提供清理 | 只读 | 860 MB | 删了破坏 VS 修复/卸载 |

验收：本机一次全清可清理总量较 v3.3 提升 ≥ 4 GB；每项失败有明细计数。

## 5. 阶段 2 —— 架构升级（3-5 天）

1. **规则引擎外置 JSON**（对标 [MoscaDotTo/Winapp2](https://github.com/MoscaDotTo/Winapp2)）：每条规则声明 `name/base/pattern/risk/cleanMethod(dir|files|command|keepNewest)/guardProcesses/serviceGuards/minAgeDays/reportOnly`；随发行版带默认规则 + 侧车文件热加载，补规则不改代码。
2. **每规则进程守卫**：把 `DetectRunningTargets` 硬编码表搬进规则，清理前逐项检测提示。
3. **统一命令式框架**：服务停启、官方 cmdlet、`WaitForExitAsync` + 取消令牌。
4. **审计日志落盘**（`%LOCALAPPDATA%\CacheCleaner\logs`）：扫描/清理逐项 before/after、失败路径清单——诊断「效果不理想」的基础设施。
5. **增长分析页**：把 `cleanup/growth-baseline.ps1` 的逻辑内置——枚举最近 N 天新增大文件排行（扫描范围：用户目录、ProgramData、`Windows\Logs`、`LogFiles\WMI`），直接回答「谁在吃 C 盘」。
6. **遗留追踪检测**：解析 `logman query -ets`，发现 `WPR_initiated_*` 或手动 NT Kernel Logger 时给 Warn 提示（本次诊断的 WPR 会话即此类）。
7. **指标口径修正**：以 C 盘可用空间差为准（清理前后 `GetDiskFreeSpaceEx` 对比），file.Length 累计只作明细。
8. **干跑模式 + 设置持久化**（记住勾选、阈值）；Docker/WSL 改为 `docker system prune`（opt-in）+ vhdx 压缩（diskpart compact vdisk），替代现在的整删 Danger 项。

## 6. 增长监控脚本（已随本方案交付）

[cleanup/growth-baseline.ps1](../cleanup/growth-baseline.ps1)：只读快照 24 个关键位置 + C 盘可用空间，存基线 JSON（`%LOCALAPPDATA%\CacheCleaner\growth-baseline.json`）；之后每次运行输出各位置增量排行，持续增长 >100 MB/天的位置就是泄漏点。

```powershell
# 建议每周（或清理前后）运行：
powershell -NoProfile -ExecutionPolicy Bypass -File cleanup\growth-baseline.ps1
# 重新开始记录：加 -Reset
```

首次运行建立基线；管理员终端运行可完整读取 `C:\Windows\...` 位置。
