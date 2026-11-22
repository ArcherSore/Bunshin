# 运行环境动作的优先级与闸门

多个 GUI 入口可在同一个 Worker 上请求停止、换场或退出。它们共用 RuntimeEnvironment 的闸门，排队时只登记意图，取得闸门后再核验当前 Run、Worker 和 Child Session；持闸门的流程直接调用闸门内操作。

| 优先级（高到低） | 动作 | 取得闸门与冲突处理 |
| --- | --- | --- |
| 1 | 用户退出程序、安装更新 | 等待。已确认退出或开始安装时，其他排队动作让位。结束未确认则取消退出或安装，保留 GUI。 |
| 2 | 命令行 `-q` 退出 | 等待。本次任务在跑或等待退出时，各类定时跳过。手动停止或手动结束分身后，收到该任务的已取消终态仍照常退出。 |
| 3 | 用户结束桌面分身 | 确认后等待。取得闸门后重新检查分身是否仍在，重复点击共用一次请求。 |
| 4 | 强制定时顶替 | 等待。顶替运行中的非 `-q` 任务前有可取消的 10 秒倒计时，换场前复核。分身已被手动结束时，新到点的机会照常准备。 |
| 5 | 定时收尾 | 等待。只注销仍属于本次成功 Run 的空闲分身。 |
| 6 | 手动开始、命令行自动执行、普通定时 | 尝试。先到先得，忙时跳过，本次机会不补跑。 |

“准备并开始”沿用开始类规则。单独准备和手动停止排队等待，手动停止只向 Worker 请求停止。用户结束分身或退出程序的确认框在申请闸门前弹出，用户考虑期间允许其他动作继续；退出开始后忽略旧确认框的回答。Worker 自行完成或退出时，证据直接进入 RunLifecycle。

## 依据

父票记录了对 MXU、MFAAvalonia、BetterGI 退出与确认交互的调研，包括 MXU 在停止超时时取消退出、MFAAvalonia 在锁外询问用户。六级顺序、确认后排队和强制顶替的边界由用户拍板，开始类动作沿用既有的先到先得规则。[父票 #154](https://github.com/ArcherSore/Bunshin/issues/154) · [MXU 停止超时](https://github.com/MaaXYZ/MXU/blob/2f309effe43bbf151ef71e10895d461eaaad9201/src/services/taskStopService.ts#L79-L92) · [MFAAvalonia 退出确认](https://github.com/MaaXYZ/MFAAvalonia/blob/8646e0c34fa1fc52cd32505bc2020bae7da9d76a/MFAAvalonia/Views/Windows/RootView.axaml.cs#L253-L285) · [ADR 0011](0011-busy-environment-first-come-no-queue.md)
