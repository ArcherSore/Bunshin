# 运行环境忙时先到先得，不排队、不补跑

所有配置共用一个运行环境。手动开始、开机自动执行和普通定时，谁先取得环境闸门谁执行；后到的请求立即返回，窗口给出反馈，无人值守入口在运行动态里记下跳过原因，环境空闲后不补跑。同一分钟有多个定时，按配置列表顺序由第一份获准的配置占用这次机会。只有开启「强制定时启动」（默认关闭）时，定时才会在 10 秒可取消的倒计时后顶替正在执行的 Run。

## 考虑过的方案

- **开机自动执行固定优先于定时**：MXU 和 MFAAvalonia 都没有约定入口之间的固定优先级，普通启动遇忙即跳过。只有出现“每天登录必须跑指定配置”的需求时，才值得改这条规则。
- **忙时排队、空闲后补跑**：两个参考产品都不这样做；补跑会让用户在意料之外的时间看到任务启动。
- **强制定时直接停止并重启**：MFAAvalonia 的 `ForceScheduledStart` 如此。Bunshin 加了倒计时，让用户能保留手头的任务。

## 依据

2026-11-20 对照 MXU 的[定时遍历与去重](https://github.com/MaaXYZ/MXU/blob/8d2c857d449535af399c13af99176329c8f77020/src/services/scheduleService.ts#L190-L252)和 MFAAvalonia 的[定时冲突分支](https://github.com/MaaXYZ/MFAAvalonia/blob/61b3b84bd999013e2c8a29b4fec3ed1e08f191d8/MFAAvalonia/ViewModels/Other/TimerModel.cs#L228-L301)做了调研，用户据此定案（[#95](https://github.com/ArcherSore/Bunshin/issues/95)）。
