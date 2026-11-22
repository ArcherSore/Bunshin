# 运行环境动作的优先级与闸门

多个 GUI 入口共用一个运行环境。为使用户考虑期间其他动作能继续，需要询问时先确认，再取闸门复核当前状态。开始类动作尝试取得闸门，其余动作排队，遇到进行中或已排队的更高优先级动作时让位。

| 优先级（高到低） | 动作 |
| --- | --- |
| 1 | 用户退出程序、安装更新 |
| 2 | 命令行 `-q` 退出 |
| 3 | 用户结束桌面分身 |
| 4 | 强制定时顶替 |
| 5 | 定时收尾 |
| 6 | 手动开始、命令行自动执行、普通定时 |

## 依据

用户参考 MXU 停止超时后取消退出、MFAAvalonia 在锁外确认等退出交互调研，确定了以上顺序和取闸门方式。[父票 #154（含 BetterGI 调研）](https://github.com/ArcherSore/Bunshin/issues/154) · [决定 #159](https://github.com/ArcherSore/Bunshin/issues/159) · [ADR 0011](0011-busy-environment-first-come-no-queue.md)

## 2026-11-22 更新

结束运行环境未确认导致 `-q` 退出取消时，放弃该退出请求。此后由 `EndUnconfirmed` 拦住开始和定时。用户再次结束桌面分身并确认成功后，GUI 保留，后续定时照常启动。参见 [ADR 0013](0013-quit-after-run-tracks-own-run.md)。
