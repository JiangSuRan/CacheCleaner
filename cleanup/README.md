# cleanup

辅助脚本目录（主程序之外的清理/诊断工具）：

- **growth-baseline.ps1** — C 盘增长基线与对比（只读，不删任何文件）。首次运行建立基线，
  之后每次运行输出各关键位置的增量排行；某位置在两次运行间持续增长 >100 MB/天即为泄漏点。
  用法见 [docs/upgrade-plan.md](../docs/upgrade-plan.md) 第 6 节。

