# 清理流程回归

不依赖测试框架，所有真实删除仅针对本次运行在输出目录内创建的随机测试目录；退出时删除该目录。命令测试使用本测试程序的输出/等待模式，不运行系统维护命令。

```powershell
dotnet run --project tests/Regression/Regression.csproj -c Release
# 实窗检查（创建示例行，不保存用户设置、不执行系统清理）
dotnet run --project tests/Regression/Regression.csproj -c Release -- --ui
# 只读实测系统盘占用扫描，输出文件数、目录数及无法读取/跳过项统计
dotnet run --project tests/Regression/Regression.csproj -c Release -- --disk-live
# 最终 exe 启动与只读扫描，更新正式截图；不会执行清理或保存设置
pwsh -NoProfile -STA -File tests/Regression/published-smoke.ps1
```

专项覆盖只读文件与属性恢复、不可读子目录的部分统计、父目录中的 reportOnly/LocalHistory 保护、Agent neverTouch、junction 边界、深层嵌套项目、超过 60 项及 1 MB 产物。权限测试只临时调整本次夹具的 ACL，并在 finally 中恢复；不修改用户目录权限。
