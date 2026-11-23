# 主题框架：按模式分配色字典，运行时切换

界面颜色按模式（跟随系统、浅色、深色）放在独立的配色字典里，键集相同；视图和样式只用 `DynamicResource` 引用按用途命名的 `Brush.*` 等键，原始色阶只在配色字典内部使用。由 `ThemeService` 在运行时替换字典，并同步 WPF-UI（不用 Mica，强调色取自 `Brush.Primary`）和所有窗口。第一期只提供浅色，不加设置项，外观除 WPF-UI 控件的强调色统一为项目主色外保持不变。之后要按 Claude Desktop 的风格改色阶，按用途命名让改色基本只动配色字典。

## 考虑过的方案

- **保留 `StaticResource`，切换主题时重启程序**：改动最小，但参考产品都是即时切换，而且以后再改引用方式要把同样几百行重改一遍。
- **模式 × 强调色两个维度**（MXU、MFAAvalonia、WPF-UI 的做法）：暂不需要多个强调色；`Brush.Primary` 放在模式字典里，以后需要时再拆出强调色字典。
- **现在就做深色配色**：色阶马上要重做，现在设计的深色值会作废。切换机制用测试配色字典的自测验证。
- **高对比配色、Mica/Acrylic 背景**：用户少，暂不做，作为已接受的限制；框架以后加高对比不需要改结构。

## 依据

用户拍板。调研了 WPF-UI 4.3.0 的 `ApplicationThemeManager`/`ApplicationAccentColorManager` 源码、WPF Fluent 的 `ThemeManager`、BetterGI、MFAAvalonia 和 MXU 的主题实现，以及 Fluent 2、Primer 的 token 分层。
