# 命令行指定的配置无效时不执行

`--autostart -i` 给出的名称或 ID 找不到、或匹配到多份配置时，本次不执行任何配置，并弹出托盘提示。MXU 在显式名称未匹配时回退到设置里选的配置，Bunshin 有意不跟随：无人值守时跑错配置，比不跑更难察觉。`-i` 后面没有值时等同于未指定，执行设置里选的配置。

## 依据

2026-11-20 对照 MXU 的[开机目标选择](https://github.com/MaaXYZ/MXU/blob/8d2c857d449535af399c13af99176329c8f77020/src/App.tsx#L1004-L1089)，由用户定案；托盘提示在 [#141](https://github.com/ArcherSore/Bunshin/issues/141) 补上。
