# 性能验证

只读扫描和预览不删除用户文件、不写 AppSettings、不调用系统维护清理。`live` 与实际软件一样会运行只读探测命令（如 conda info、docker info、卷影存储查询）。本机对照应在相同 Windows 用户和权限下运行。

```powershell
dotnet build CacheCleaner.csproj -c Release
dotnet build tests/Performance/Performance.csproj -c Release
# 使用新目录，避免覆盖已有数据
./tests/Performance/New-Fixture.ps1 -Root _tmp/perf-fixture
./tests/Performance/Build-Rust.ps1
# 独立性能构建的实窗只读扫描，不覆盖正式截图
pwsh -NoProfile -STA -File tests/Regression/published-smoke.ps1 -Executable publish-performance/CacheCleaner.exe -NoScreenshots
# baseline.dll 为原版本在独立目录构建的 CacheCleaner.dll
./tests/Performance/Run-Comparison.ps1 `
  -BaselineAssembly baseline.dll `
  -CandidateAssembly bin/Release/net10.0-windows/CacheCleaner.dll `
  -Fixture _tmp/perf-fixture -OutputDirectory _tmp/perf-results -Live -Rounds 3
```

`Build-Rust.ps1` 优先使用 PATH 中的 Cargo，也支持 `.toolchains/cargo` 和 `.toolchains/rustup` 的项目内安装。本次使用 Rust 1.99.0 MSVC；已有 C++ Build Tools，无第三方 Rust crate，构建和测试均可离线运行。工具链不会打包进清理程序。

基准程序集可从提交 `988e77e` 在独立 worktree 构建；不要切换或覆盖正在编辑的工作区。测量程序通过反射加载指定程序集，不将生产代码链接到测试进程。固定目录包含中文和空格路径、24,576 个文件、49,922,048 字节；先预热 Measure/SumFiles，每种操作测量 7 次，三轮交替 before/after 顺序。Preview/Projects 的首个样本可能含方法或注册表初始化开销。Rust 原型测量同一固定目录，每轮预热后测量 7 次，计时不包含启动进程。

`ScanDiagnostics` 默认关闭。只有测量程序调用 Begin 后才记录阶段、大小统计调用和命令耗时。本次基准版本也临时加入了相同计时，以控制观测开销；直接使用原始提交可复测总耗时，阶段数据仅在带计时类的构建中提供。`SizeMs`、`CommandMs` 是各阶段内部耗时，不能与阶段总耗时再相加。

`Summarize.ps1` 用于本次固定数据规格，校验 C#/Rust 大小及文件数、扫描目标身份摘要，输出中位数与范围。`live` 的目录内容会随后台应用变化，因此身份摘要不含实时大小；这不能替代固定目录的结果一致性检查。分配字节是累计托管分配，峰值工作集包含运行时和测试进程，均不是磁盘读取量。

Rust 原型仅实现 Windows 目录计量：跳过目录 reparse point、报告统计是否完整，不包含规则、进程守卫、取消、删除和 UI。请勿将原型作为正式清理引擎使用，也不要把单项计量速度外推到整机清理。

本轮结果见 [性能评估](../../docs/performance/2026-10-15/README.md)。
