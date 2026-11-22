# `-q` 只跟踪本次命令启动的那一次 Run

`-q` 以 RunKey 认定本次命令触发的 Run，它结束后（成功、失败或被停止）退出；任务根本没开始时不退出，被强制定时顶替时放弃跟踪。MXU 和 MFAAvalonia 监听目标实例的“运行中”标志由真变假。Bunshin 的 Worker 在 GUI 断开后仍可继续运行（ADR-0001），只看这个标志会把其他 Run 或重连后的状态误当成本次结束。

## 依据

2026-11-20 对照 MXU 的[退出订阅](https://github.com/MaaXYZ/MXU/blob/8d2c857d449535af399c13af99176329c8f77020/src/App.tsx#L1050-L1066)和 MFAAvalonia 的[退出监听](https://github.com/MaaXYZ/MFAAvalonia/blob/61b3b84bd999013e2c8a29b4fec3ed1e08f191d8/MFAAvalonia/Views/Windows/RootView.axaml.cs#L991-L1040)，由用户定案（[#95](https://github.com/ArcherSore/Bunshin/issues/95)、[#122](https://github.com/ArcherSore/Bunshin/issues/122)）。
