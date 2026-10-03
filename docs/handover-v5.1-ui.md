# v5.1 UI 一体化改版 · 交接文档

> 交接日期：2026-10-03 · 当前状态：**v5.1 与 v5.1.1 均已提交推送并发布 GitHub Release（附自包含 exe），本轮交接闭环**

## 本次接手修复与验证

### 5.1.1 清理流程修复（已随仓库推送并发布 Release v5.1.1）

- 用户日志：2026-10-03 12:47:48 开始清理 104 项，已处理多项后无最终汇总。代码存在命令管道未读取、ReadToEnd 先于超时判断、取消被异常捕获吞掉的问题；尚不能将缺少汇总直接判定为某个系统命令死锁。
- 新增 `CommandRunner` 并行读取 stdout/stderr，限制输出缓存，采用有期限轮询；长任务持续显示已用时间。取消/超时停止等待，系统维护进程保留后台执行，并明确提示 PID，避免强杀 DISM 等维护任务。
- 新增 `FileCleaner` 按子目录逐个处理，访问失败记录后继续其他目录，跳过链接，不递归强删残留目录；预览完全不删除空目录。取消时保留已完成删除的计量。
- 数据行保存原始 CacheItem 身份与初始大小，清理后核实残留；不再按显示名称（含自动发现前缀）重建缺少大小的对象。只读报告不能勾选，全选排除只读项。新增“处理结果”列与失败/跳过/待重启提示；完成后统计剩余占用。
- 进度按已完成项目更新，单项耗时时使用不确定进度；处理期间锁定表格防止排序/编辑干扰行索引；在成功、取消和异常结束时均写汇总日志。
- 回归测试保留在 `tests/Regression`，覆盖双管道大量输出、超时、取消、预览不删目录、真实删除、最近文件保留、失败后继续、残留大小和部分取消计量，以及只读条目的 UI 行为。实际删除只针对测试创建的目录；没有运行本机系统维护清理。
- 本地 `publish/CacheCleaner.exe` 已生成 5.1.1.0（自包含），编译零警告/零错误；15 项后端回归与 5 项实窗状态检查通过。最终 exe 实际扫描完成（本机本轮 85 项），新结果列及全选/取消选择检查通过，截图已更新。已推送并发布 v5.1.1 Release（附自包含 exe）。


- 用完整控件初始化标志阻止构造期布局，修复历史 `LayoutUI` 空引用崩溃；补建缺失的 `lblCount`。
- 将按钮改为完全自绘 `Control`，避免原生 `Button` 在点击/状态变化后覆盖自绘外观；保留按钮名称、PushButton 无障碍角色与默认操作、Tab/Space/Enter 操作。旧截图脚本不能再假设每个按钮具有 UIA InvokePattern，应检查实际支持的模式或使用实际鼠标操作。
- 扫描结果标题条采用 Dock Top，表格 Dock Fill，按停靠顺序处理；标题、副标题、空状态和统计区均有独立位置。预览开关移到头部，状态详情超宽省略，大小列加宽，列标题去掉深色边框。
- 原素材是圆角图标，不适合直接 cover 放大；现在采样内部插画、按窗高等比靠右，并用渐变遮罩衔接页面底色。空闲时显示角色，结果行保持不透明以保证阅读。
- 修复风险枚举未转成中文标签、hover 定时器重复注册及资源释放问题；空列表禁用清理/选择按钮，清理期间取消按钮改为“取消清理”。
- 验证：Release 编译 0 错误/0 警告；实窗截图检查；示例条目全选/取消选择、状态详情防遮挡、忙碌进度条、趋势窗口启动。最终自包含 `publish/CacheCleaner.exe` 启动成功，实际扫描完成（本机 104 项），鼠标操作全选/取消选择通过。未执行实际清理，未覆盖用户设置。当前实测显示缩放为 100%，其他 DPI 尚未进行实机验证。
- `docs/screenshots/main.png` 与 `scan.png` 更新为最终发布程序的实际截图。

以下各节保留原始交接背景，历史“已知问题”以本节验证结果为准。
> 当前仓库状态：v5.0 已发布（[github.com/JiangSuRan/CacheCleaner/releases/tag/v5.0](https://github.com/JiangSuRan/CacheCleaner/releases/tag/v5.0)）；工作区有**未提交的 UI 改版代码**（Theme.cs 重写 + MainForm.cs 多轮修补），构建报错未清零，截图自检未通过。接手者请先读本文档第 4 节的「已知问题清单」再动手。

## 1. 这轮要解决什么（用户验收标准）

用户对 v5.0 界面不满意，给出完整设计规范（见对话记录，要点）：

1. **无边框窗口**：去掉系统标题栏与黑边，自绘标题栏（图标 + `C盘缓存清理` + 最小化/关闭，40px，可拖动），DWM 圆角
2. **背景铺满全窗**：cover + center-right 构图，白纱 0.38-0.50（最终定 0.44），角色不能压数据可读性
3. **按钮层级**：Primary（实心蓝+阴影）/ Secondary（浅蓝底蓝字）/ Ghost（透明 hover 浅蓝灰）三档；**动态主按钮**——扫描前主按钮是「扫描缓存」，扫描完成后切给「清理选中」；全部 36px 高、8px 圆角、线性图标（Segoe MDL2）、hover 120-200ms 过渡动画
4. **双层头部**：第一层 58px 产品名（C盘缓存清理 + Cache Cleaner 副标题），第二层 46px 操作栏
5. **列表面板**：圆角 10-12 浅色主面板、行高 38、表头 34 背景 #F2F6FB、分隔线 #E8EEF6、hover #F4F8FF、选中 #EAF2FF、状态列小型圆角标签（浅底深字）
6. **状态区**：左「● 状态标题 + 灰色详情」，右「可释放 X GB / 已选择 X B / 预览开关」；状态机 Idle/Scanning/ScanCompleted/Cleaning/CleanCompleted/Cancelled/Error 驱动白纱强度与空状态
7. **进度条**：5-6px 细圆角、主色蓝（ProgressLite，替代系统绿条），支持确定/跑马灯两态
8. **文案产品化**：「趋势分析」「取消选择」「取消扫描」；版本号从标题移除

## 2. 已完成且可用的部分（构建零错误的版本曾出现）

- **Theme.cs**（当前工作区版本已可用）：色彩系统（#F6F9FE 底 / #6EA8FE 主蓝 / 语义三色）、字体系统（Microsoft YaHei UI 五级）、间距常量、`EnsureBackground`（cover + center-right + 白纱 0.44，尺寸变化重建）、`DrawSliceUnder/DrawSlice`（切片绘制，面板透出背景的核心机制）、`SoftPanel`（可调白纱内容面板）、`RoundedContainer`（12px 圆角白面板）、`GradientButton`（基于 Button，三档样式 + 动态主按钮 + hover lerp 动画 + AccessibleName）、`ProgressLite`（细圆角进度条两态）、`UiState` 枚举与 `VeilAlpha()` 分级
- **MainForm.cs**（当前工作区版本，**有构建错误**，见下）：无边框窗口 + 自绘标题栏（CaptionButton 最小化/关闭 + HTCAPTION 拖动）+ DWM 圆角、双层头部、绝对布局 `LayoutUI()`、按钮 FitButton 自适应、状态机 `SetState`、项目工件/自动发现/审计日志等业务逻辑全部保留未动

## 3. 与 v5.0 的行为差异（接手者必读）

- 窗口从 `FixedSingle`+系统标题栏 → `None`+自绘标题栏；窗体**不可缩放**（无最大化按钮），拖动用 `ReleaseCapture + WM_NCLBUTTONDOWN` 手法
- 按钮从系统 Button 换色 → 全自绘 `GradientButton`（基类 Button，保留 InvokePattern 供 UIA）
- 状态栏文案从「扫描完成：xxx」→「● 扫描完成 / 发现 N 个缓存项 / 可释放 X」结构化状态区
- 「取消」常驻显示但空闲时禁用；「取消扫描」在扫描期间可用
- 版本号显示：窗体标题改为 `Cache Cleaner`（无版本号）；MainForm 标题字符串在 `SetupForm()` 中

## 4. 已知问题清单（按优先级，接手者从这里开始）

1. **[阻断] MainForm.cs 构建错误未清零**。多轮补丁式修改后文件处于不一致状态（最近一轮：`lblSection`/`lblFound` 的「Dock 表头条」重构做了一半）。错误集中在：
   - `SetupControls()` 里浮动标签块与新的 `sectionStrip` 代码块并存冲突（`listPanel.Controls.Add(dgv)` 可能重复、`lblFound` 定位代码在 `LayoutUI` 中残留）
   - 建议处理：**不要继续打补丁**。以第 5 节的目标结构为准，把 `SetupControls()` 的「主内容区」段重写一遍（约 60 行），删掉 `LayoutUI()` 中所有对 `lblFound/lblSection` 的绝对定位行
2. **[阻断] 按钮文字重叠**（用户看到的截图）：按钮上图标+文字画在错误偏移处。根因未定，两个候选：① `FitButton` 宽度计算与 `GradientButton.OnPaint` 中 `TextRenderer.MeasureText` 用了不同字体度量；② PrintWindow 截图伪影（UIA 转储证明运行时位置无重叠——**先跑一次真机目视确认再改代码**，UIA 定位Dump 脚本见第 6 节）
3. **[高] 黑三角**（用户反馈「边框不要有黑三角」）：历史原因是 `Region` 多边形裁剪。当前代码已改为 DWM `DWMWCP_ROUND`（`OnHandleCreated`），**Region 裁剪代码已删**，此项应已解决——验证方式：截图四角无黑角即为通过。若仍有，检查 `RoundedContainer` 的 Region 裁剪（它内部也有 `OnResize` 里设置 Region，理论上面板级 Region 不产生黑角，因为父层是白色面板）
4. **[中] shot.ps1 截图脚本适配**：`_tmp/shot.ps1` 在轮次间会被 Stop hook 清空（每次要重写，注意 **UTF-8 BOM**，否则中文按钮名匹配失败）；按钮 UIA Name 现在是纯 `Text`（`AccessibleName = Text`），「取消扫描」等待循环的名称匹配要同步；`empty rect` 报错可忽略（重试一次即可）
5. **[低] 状态区右侧拥挤**：预览开关/已选择/可释放三项在 46px 高度内偏挤，可考虑预览开关移到操作栏右端

## 5. 目标结构（MainForm.SetupControls 主内容区段的正确形态）

```csharp
// ---- 主内容区 ----
contentPanel = new SoftPanel { VeilAlpha = _state.VeilAlpha() };

lblEmpty = new Label { ... Dock 未设，Absolute 居中 ... };
contentPanel.Controls.Add(lblEmpty);

var listPanel = new RoundedContainer { Dock = DockStyle.Fill, Padding = new Padding(1) };
contentPanel.Controls.Add(listPanel);
Controls.Add(contentPanel);          // 注意：contentPanel 先加入，listPanel Fill 在其内

// 表头条（Dock Top，先于 dgv 加入——WinForms Dock 逆序：先加的在最下/后绘）
var sectionStrip = new SoftPanel { Dock = DockStyle.Top, Height = 36, VeilAlpha = 0 };
lblSection = new Label { Text = "扫描结果", AutoSize = true, BackColor = Color.Transparent };
lblFound   = new Label { AutoSize = true, Anchor = Top|Right, BackColor = Color.Transparent };
sectionStrip.Controls.Add(lblSection);
sectionStrip.Controls.Add(lblFound);
sectionStrip.Resize += (_,_) => lblFound.Location = new Point(sectionStrip.Width - lblFound.Width - Theme.SpaceM, 9);
listPanel.Controls.Add(sectionStrip);
listPanel.Controls.Add(dgv);         // dgv 只在这里 Add 一次
```

`LayoutUI()` 中**不得**再出现 `lblFound.Location` / `lblSection.Location`（由 sectionStrip 的 Resize 驱动）；`BtnScan_Click` 完成分支中的 `lblFound.Location = ...` 行也要删除。

## 6. 可复用的工具与验证方法

- **截图脚本**（重建 `_tmp/shot.ps1`，务必 UTF-8 BOM）：逻辑 = 启动 publish exe → UIA 按 ProcessId 找窗口 → `PrintWindow`（flag=2）抓 main.png → Invoke「扫描缓存」→ 轮询「取消扫描」按钮消失 → 抓 scan.png → 关闭并还原 settings.json。完整可用版本在本轮对话历史中出现过 4 次，任取一份
- **UIA 布局转储**（验证按钮是否真重叠）：启动 exe 后 `FindAll Descendants ControlType=Button` 逐个打印 `BoundingRectangle`。本轮结论：运行时坐标无重叠，截图重叠是 PrintWindow 伪影
- **启动崩溃诊断**：`Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2}` 看 .NET Runtime 的 NullReferenceException 堆栈（本轮 LayoutUI 在构造期被 OnResize 触发过 NRE，已加 null 守卫修复）
- **publish 报 `UnauthorizedAccessException`**：说明有残留 exe 进程，`taskkill //F //IM CacheCleaner.exe`

## 7. 建议的收尾路径（半天内可完成）

1. 按 §5 重写主内容区段，清零构建错误（预计 30 分钟）
2. 真机运行目视确认按钮文字无重叠（若截图重叠而真机正常，判定为 PrintWindow 伪影，不修）
3. 全功能回归：扫描 → 全选/取消选择 → 预览清理 → 实际清理 → 取消扫描 → 趋势分析（预计 15 分钟）
4. 重新截图替换 `docs/screenshots/`，README 版本演进表加 v5.1 行，csproj/setup.iss 标题同步 **5.1**
5. 提交推送（`git push` 直连失败就加 `-c http.proxy=http://127.0.0.1:7890`，代理不在了再试直连）+ `gh release create v5.1`

## 8. 项目整体背景（防止上下文丢失）

- 项目：CacheCleaner，C# .NET 10 WinForms，Windows 磁盘治理工具；已发布 v3.4→v5.0 共 11 个版本，全部有 GitHub Release（附自包含单文件 exe）
- 三大资产：`rules.default.json`（71 条规则）+ `agents.catalog.json`（18 个 agent/31 条编译条目）+ `projects.registry.json`（8 类项目工件注册表）；三份设计文档在 `docs/`
- 业务核心：`CacheScanner.cs`（三阶段扫描：已知规则 → 自动发现 → 项目工件；CleanItem 分发）——**本轮 UI 改版完全没动这个文件**，业务零风险
- 环境注意：PS 5.1 跑含中文脚本必须 UTF-8 BOM；git push 可能需本地代理 7890（FlClash）；`_tmp/` 每轮会话结束被清空
