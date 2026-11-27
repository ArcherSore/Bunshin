# 代理分工与交付流程

多票、跨模块或需要审查的工作按这里分工。单个会话就能完成的小改动，直接实施并验证，不走审查流程。

## 分工

主控代理负责编排：拆票、写交给实现者和审查者的简报、跑完整套件、读日志、提交、开 PR、维护 Issue。合入 main 必须等用户确认。实际编码和审查交给下面的执行者。

主控默认用 Claude Sonnet 5.5，拆难票和分析根因时换 Opus 5.5。默认把任务交给 Antigravity CLI（`agy`），Claude 订阅留给主控、并发和 UI，Codex 额度最紧（Plus 且多人共用），首选只用于并发和协议票。

| 工作 | 执行者 | 备用 |
| --- | --- | --- |
| 日常实现、窄修、按审查意见修复、只改测试、文档 | `agy` `gemini-4-hydrogen-high`；多文件改动用 `gemini-4-neon-high` | Claude 子代理（Sonnet） |
| 复杂实现：新模块、跨模块改动 | `agy` `gemini-4-neon-xhigh` | Claude 子代理（Opus） |
| 复杂实现：并发、协议 | Claude 子代理（Opus） | `agy` `claude-opus-5-5-high` |
| UI 与前端 | Claude 子代理（Opus 或 Sonnet） | `agy` `gemini-4-neon-high` |
| 设计评审（开工前，只读）：跨模块票 | `agy` `claude-opus-5-5-high` | `agy` `gemini-4-neon-xhigh` |
| 设计评审：并发、协议票 | Codex `gpt-6.2-astra`，high | `agy` `claude-opus-5-5-high` |
| 审查（首次和后续轮次）：Gemini 写的代码 | `agy` `claude-sonnet-5-5-high`；首次审查跨模块票用 `claude-opus-5-5-high` | Claude 子代理（Sonnet） |
| 审查（首次和后续轮次）：Claude 写的代码 | `agy` `gemini-4-neon-high` | Codex `gpt-6.2-sol`，medium |
| 合入前 P1 复核（只用于并发、协议票） | Codex `gpt-6.2-astra`，high | `agy` `claude-opus-5-5-high` |

- 审查者尽量用与实现者不同厂商的模型。
- 备用在首选额度用完或质量不够时使用。`agy` 里的 Claude 模型与 Gemini 模型分开计额度，也独立于 Claude 订阅。

### 派发

- 执行者在 `.worktrees/<名称>` 工作树中运行。提交由主控完成。提交说明和 PR 正文都不写任何 AI 署名。
- 简报只给当次任务需要的材料：问题清单、相关文件路径和 [CODING_STANDARDS.md](../../CODING_STANDARDS.md)。不要让执行者通读设计稿和历史审查记录。
- 报告按固定的简短格式写，每个问题一行：级别、现象、位置。详细内容留在文件里。
- Codex 只做评审和审查，用 `--sandbox read-only`。每次显式指定模型和推理档，不依赖本机默认值。xhigh 只用于跨模块的设计或并发问题；不用 max、ultra 和 fast 模式。

### agy

- 实现类任务用 full access：`agy --model <模型> --dangerously-skip-permissions --output-format json --print-timeout <时限> -p "<简报>"`。只读任务不加 `--dangerously-skip-permissions`；它此时不能执行命令，主控先把要审查的 diff 写到工作树的 `artifacts/` 下，在简报中给出路径。
- full access 会自动批准所有工具调用，agy 以及它运行的构建脚本和测试代码拥有当前用户的完整权限。工作树只隔离改动，不限制访问范围，所以只用于可信分支，并在简报中写明边界：只在本工作树内修改，不提交、不推送、不切换或改写分支，不注销会话、不结束其他进程、不改系统设置。
- 派发前记下各分支和远端的位置；结束后核对主目录和工作树的 `git status`、分支和远端。这只能发现 Git 层面的越界。
- 退出码 0 和 `status: SUCCESS` 不代表完成：还要确认 `response` 非空、没有 `denied_actions`，再看 diff 和自检日志。
- 用默认 agent，不用自定义 agent 和 Orchestrator。

## 一张票的流程

1. **合入门槛**：开工前写进票里。默认门槛是：没有 P1；P2 只算本票范围内的。观察项不进入修复循环，基线已有的问题转交对应 Issue。
2. **设计评审**：跨模块的票先做设计评审，由用户确认范围后再实现。
3. **实现**：实现者只跑自己改到的自检，`verify.ps1 -Suite <对应套件> -Only <名称>`，跑一次。真实 Worker 连接检查（`VerifyRealWorker*`）不能带 `-Only`，用 `-Suite Worker` 整套运行。
4. **完整验证**：主控跑一次 `-Suite` 完整验证。标记为 `RequiresFocus` 的用例如果因焦点断言失败，`verify.ps1` 会自动单独重跑一次，以重跑结果为准；其他失败照常算失败。
5. **首次审查**：可以读全仓，以对照基线为准，按 CODING_STANDARDS.md 的审查输出约定报告问题。
6. **后续轮次**：审查者仍可读全仓，但只回答两件事：上一轮的问题是否已修复；这次修复是否在任何地方引入了新问题。不对整票重新做开放式审查。
7. **合入前**：并发、协议票由 Codex 对整票做一次轻量复核，只报 P1。
8. **提 PR，等用户过目**：主控推送分支并开 PR，PR 正文按 `pr` skill 的模板写。有依赖关系的多张票开成 stack PR：每个 PR 以前一个分支为 base，并在正文里写明合入顺序。开完 PR 就停下汇报，由用户审阅。用户确认后，主控按顺序合入：先合最底层的 PR，再把下一个 PR 的 base 改到 main，然后继续合。

## 不收敛时停下

- 只统计修复引入的新问题。问题总数在下降、而且没有修复引入的新问题，就算收敛。
- 审查三轮仍不收敛，停止打补丁，先分析根因，然后**交给用户决定**：重新设计、缩小范围，还是窄修后合入。需要改动已定范围时，同样交给用户决定。
