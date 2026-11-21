# Claude 开发指南

## 上下文

按任务需要阅读 README.md、GLOSSARY.md、docs/ARCHITECTURE.md 和相关 ADR；涉及 Child Session 时阅读 src/Gui/ChildSession/。
工作跟踪只用 GitHub Issues，约定见 docs/agents/issue-tracker.md 和 docs/agents/triage-labels.md。
修改 GLOSSARY、ADR 或 ARCHITECTURE 前先读 docs/agents/domain.md。

## 约束

- Child Session、RDP ActiveX、Task Scheduler COM、分辨率/缩放、进程 Session 验证、运行环境退出与清理
  在真实 Windows 会话中的行为需要人工回归。修改前先说明必要性。

## 项目约定

- 错误处理只加在确实需要的地方；优先复用现有组件和 WPF / WPF-UI 内置控件。

## 工作方式

- 修改源码、构建脚本或运行配置后，按影响范围运行 `./src/Gui/scripts/verify.ps1 -Suite <范围>`，范围说明见 CONTRIBUTING.md。SKIP 表示未验证，不算通过。
- 多票、跨模块或需要审查的工作，按 docs/agents/workflow.md 分工；单会话能完成的小改动，检查通过后就停下汇报。
- 没要求的测试、文档、重构不顺手加；觉得值得做就在最后提一句。
