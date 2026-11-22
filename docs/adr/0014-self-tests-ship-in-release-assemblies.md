# 自检保留在发布程序集中

GUI 和 Worker 的自检编译在发布程序集里，发布 CI 在成品目录运行 `dotnet Bunshin.dll --self-test` 和 `dotnet worker/Bunshin.Worker.dll --self-test`，借此检查打包后的布局和依赖加载。把自检整体移到独立测试项目能让发布包更干净，却会失去对成品本身的检查。以后可以分批移出纯逻辑测试，前提是先列明哪些检查必须对打包后的程序集执行，并让 CI 继续覆盖它们。

## 依据

用户 2026-11-20 定案（[#112](https://github.com/ArcherSore/Bunshin/issues/112)）。MXU 同时有[仓库级逻辑测试](https://github.com/MaaXYZ/MXU/blob/8d2c857d449535af399c13af99176329c8f77020/tests/taskStopRestart.test.ts)和[驱动发布程序的 Windows 回归](https://github.com/MaaXYZ/MXU/blob/8d2c857d449535af399c13af99176329c8f77020/tests/windows/README.md)，可作将来拆分的参考。
