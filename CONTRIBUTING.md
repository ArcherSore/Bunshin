# 开发指南

## 环境

- .NET 10 SDK：GUI 与 Worker 的日常构建和自检
- Rust 1.98.1 MSVC 与 Visual Studio C++ Build Tools：Update Engine 和完整验证

开发入口：[领域术语](GLOSSARY.md)、[当前架构](docs/ARCHITECTURE.md)、[架构决策](docs/adr/)、
[GUI 行为说明](docs/GUI-BEHAVIOR.md)、[Update Engine 契约](docs/UPDATE-ENGINE.md)。
当前工作与验收条件在 [GitHub Issues](https://github.com/ArcherSore/NarutoAutoGUI/issues)。

## 构建与自检

在仓库根目录运行：

```powershell
.\src\Gui\scripts\verify.ps1            # 增量构建 GUI + GUI 自检
.\src\Gui\scripts\verify.ps1 -Suite All # 完整自动验证
```

`verify.ps1` 只输出摘要，完整日志保存在 `artifacts/validation/`。按改动范围选择 `-Suite`：

| `-Suite` | 适用改动 |
| --- | --- |
| `Gui`（默认） | 一般 GUI、共享布局或不确定影响范围 |
| `Project` | 任务配置、标签条、参数编辑、TaskShelf、引导（含 Support） |
| `Support` | 通知、诊断 ZIP、调试文件清理、设置、导航 |
| `RuntimeControls` | 运行控制栏、侧栏宽度、Run 进度、运行环境接线 |
| `Worker` | Worker、GUI 与 Worker 的连接（含真实 Worker 连接） |
| `Updater` | GUI 与 Engine 的更新适配器 |
| `Engine` | Rust Engine（Cargo tests + Clippy） |
| `All` | 跨组件、Protocol、依赖改动，或开 PR 前的完整验证 |

- `-Only <名称片段>` 只运行匹配的自检；`-NoBuild` 只在刚成功构建且源码未变时使用。
- 改动布局或布局断言时，先在本机缩放下运行相关自检。本机缩放非 100% 时，加 `-Dpi96` 补跑一次作为对照。
  实际验证的缩放以自检日志中的 `ENV` 为准。
- 不在同一输出目录并行构建或自检；DLL 被占用时先确认是哪个进程。
- SKIP 表示本机条件不足、该项未验证。`VerifyRealWorker` 在正式 GUI 运行时 SKIP，不覆盖 Task Scheduler 和 Child Session。
- 自检通过 `dotnet Bunshin.dll --self-test` 运行，不经过 apphost，不请求提权，也不连接 RDP；
  完整 GUI 自检会创建本机 RDP 控件验证输入禁用和窗口模式。

编写自检时，断言应对应用户能感知的缺陷，不固定文案、颜色或私有成员；几何比较用 `PixelTolerance`，等待用 `PumpUntil`。
每个套件先检查固定等待，超出既有基线即失败；标记为依赖前台焦点的失败用例会单独重跑一次，通过时摘要显示 `FOCUS-RERUN PASS <名称>`。
审查时请阅读仓库根目录的 [CODING_STANDARDS.md](CODING_STANDARDS.md)。

### 主题颜色源码检查

`Gui`、`All` 及其他构建 GUI 的套件在构建前运行 `theme-sources`，使用 `-Only` 时也会执行。
使用 `-NoBuild` 时，`theme-sources` 仍会编译检查工具。浅色和深色配色字典
`src/Gui/Themes/Colors/Light.xaml`、`Dark.xaml` 定义颜色键（两者键集必须一致），可以使用色值并静态引用本字典的键。视图和
`DesignSystem.xaml` 样式用 `DynamicResource` 引用配色，字体、尺寸和控件样式仍可静态引用。
C# 从配色资源取色，允许 `Transparent` 和基于原色 RGB 通道的透明度调整。

失败日志给出源码路径、行号、所在元素或成员和违规表达式。迁移现有用法时，从
[`allowlist.tsv`](tools/ThemeSourceCheck/allowlist.tsv) 删除对应行。确需例外时，按“路径、位置、违规表达式、原因”写一行，
列之间用制表符分隔，路径省略诊断中的行号。每行只放行一次匹配；多余的条目也会使检查失败。

## 发布包

```powershell
.\src\Gui\scripts\build.ps1   # 产物位于 artifacts\Bunshin\win-x64
```

发布、打包或 runtime 布局改动还需运行 `validate-package.ps1`。
验证打包后的原生 runtime 图片保存（不启动游戏或 RDP）：

```powershell
.\src\Gui\scripts\test-framework-diagnostics.ps1 -PackageDirectory artifacts\Bunshin\win-x64
```
