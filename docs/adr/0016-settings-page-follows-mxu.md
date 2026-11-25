# 设置页采用 MXU 的目录式视觉

设置页按 MXU 的目录式布局：左侧是分区目录；右侧条目标题用中等字重，说明用浅色次要文字且保持简短，按钮是中性填充，只保留一处实心高亮，条目带前置图标。

## 考虑过的方案

- **与主窗口导航保持一致的版本**：结构更整齐、改动更小，曾被推荐。在相同内容上渲染对比后，用户认为 MXU 风格明显更清爽。设置页的取舍以渲染效果为准，不以结构整齐为准；比较方案时要用同样的内容渲染。

## 依据

用户在原型对比后拍板。参考源码：MXU 的 [`src/components/SettingsPage.tsx`](https://github.com/MaaXYZ/MXU/blob/main/src/components/SettingsPage.tsx) 和 `src/components/settings/`，文案在 `src/i18n/locales/zh-CN.ts`；MFAAvalonia 的 [`MFAAvalonia/Views/Pages/SettingsView.axaml`](https://github.com/MaaXYZ/MFAAvalonia/blob/master/MFAAvalonia/Views/Pages/SettingsView.axaml)。

## 更新（2026-11-24）

界面按 Claude 设计分析稿改版后，设置页改为一页长滚动、左侧分类随滚动高亮，“外观”并入“通用”分区，“关于”并入最后一节；MXU 目录式的清爽风格仍是取舍依据，但具体版式以设计稿为准（见 ADR 0020）。
