# AI 使用记录

范围：本次 AR-work 初始化与工程技能配置对话，按发送顺序记录用户向当前 AI 助手发送的指令。技能正文作为单独一条保留；环境、界面上下文、系统指令、工具输出和 IDE 自动补全不作为主动 Prompt。

归属说明：本次助手未向其他 AI 或子代理发送问题或指令。因此，不存在可记录的“助手向另一个 AI 发出的 Prompt”。以下是用户到助手的真实对话记录，不能据此声称用户进行了未发生的独立审核，也不包含不可见历史。

脱敏：本地用户路径已用占位内容替换；未复制登录检查输出、账号名称或凭据。

## P-001

- 目标：启动仓库工程技能配置

### Prompt 原文

> [$setup-matt-pocock-skills]([REDACTED: local user path]/setup-matt-pocock-skills/SKILL.md)

### 结果

- 采用
- 读取指定技能并检查目录、Git 状态、现有配置和 triage 安装情况；当时目录为空且不是 Git 仓库。路径已脱敏。

## P-002

- 目标：提供配置技能的完整流程和约束

### Prompt 原文

> <skill>
> <name>setup-matt-pocock-skills</name>
> <path>[REDACTED: local user path]/setup-matt-pocock-skills/SKILL.md</path>
> ---
> name: setup-matt-pocock-skills
> description: "Configure this repo for the engineering skills: set up its issue tracker, triage label vocabulary, and domain doc layout. Run once before first use of the other engineering skills."
> disable-model-invocation: true
> ---
>
> # Setup Matt Pocock's Skills
>
> Scaffold the per-repo configuration that the engineering skills assume:
>
> - **Issue tracker**: where issues live (GitHub by default; local markdown is also supported out of the box)
> - **Triage labels**: the strings used for the five canonical triage roles
> - **Domain docs**: where `CONTEXT.md` and ADRs live, and the consumer rules for reading them
>
> This is a prompt-driven skill, not a deterministic script. Explore, present what you found, confirm with the user, then write.
>
> ## Process
>
> ### 1. Explore
>
> Look at the current repo to understand its starting state. Read whatever exists; don't assume:
>
> - `git remote -v` and `.git/config`: is this a GitHub repo? Which one?
> - `AGENTS.md` and `CLAUDE.md` at the repo root: does either exist? Is there already an `## Agent skills` section in either?
> - `CONTEXT.md` and `CONTEXT-MAP.md` at the repo root
> - `docs/adr/` and any `src/*/docs/adr/` directories
> - `docs/agents/`: does this skill's prior output already exist?
> - `.scratch/`: a sign that a local-markdown issue tracker convention is already in use
> - Is the `triage` skill installed? (a `triage` skill folder alongside this one, or `triage` in your available skills.) This decides whether Section B runs at all.
> - Monorepo signals: a `pnpm-workspace.yaml`, a `workspaces` field in `package.json`, or a populated `packages/*` with its own `src/`. These are present only in a genuinely large multi-package repo; their absence means single-context, which is almost every repo.
>
> ### 2. Present findings and ask
>
> Summarise what's present and what's missing. Then take the sections in order. One section, one answer, then the next.
>
> Lead each section with the recommended answer so the user can accept it in a word. Give a one-line explainer only when the choice genuinely branches; skip the section entirely when exploration already settled it (Section B when `triage` isn't installed, Section C when there's no monorepo).
>
> **Section A: Issue tracker.**
>
> > Explainer: The "issue tracker" is where issues live for this repo. Skills like `to-tickets`, `triage`, and `to-spec` read from and write to it. They need to know whether to call `gh issue create`, write a markdown file under `.scratch/`, or follow some other workflow you describe. Pick the place you actually track work for this repo.
>
> Default posture: these skills were designed for GitHub. If a `git remote` points at GitHub, propose that. If a `git remote` points at GitLab (`gitlab.com` or a self-hosted host), propose GitLab. Otherwise (or if the user prefers), offer:
>
> - **GitHub**: issues live in the repo's GitHub Issues (uses the `gh` CLI)
> - **GitLab**: issues live in the repo's GitLab Issues (uses the [`glab`](https://gitlab.com/gitlab-org/cli) CLI)
> - **Local markdown**: issues live as files under `.scratch/<feature>/` in this repo (good for solo projects or repos without a remote)
> - **Other** (Jira, Linear, etc.): ask the user to describe the workflow in one paragraph; the skill will record it as freeform prose
>
> Record the choice in `docs/agents/issue-tracker.md`. The GitHub and GitLab templates carry a "PRs as a request surface" flag, defaulted **off**. Leave it off and don't raise it: a user who wants external PRs in the triage queue can flip the flag in the file later.
>
> **Section B: Triage label vocabulary.** Skip this section entirely if the `triage` skill isn't installed (exploration told you), since an uninstalled skill needs no labels.
>
> If it is installed, ask exactly one question:
>
> > Do you want to keep the default triage labels? (recommended: **yes**)
>
> The defaults are the five canonical roles, each label string equal to its name: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. On **yes**, write them as-is. Only if the user says no, usually because their tracker already uses other names (e.g. `bug:triage` for `needs-triage`), collect the overrides so `triage` applies existing labels instead of creating duplicates.
>
> **Section C: Domain docs.** Default to **single-context** (one `CONTEXT.md` + `docs/adr/` at the repo root). This fits almost every repo; write it without asking.
>
> Offer **multi-context** (a root `CONTEXT-MAP.md` pointing to per-context `CONTEXT.md` files) only when exploration found monorepo signals. Then confirm which layout they want.
>
> ### 3. Confirm and edit
>
> Show the user a draft of:
>
> - The `## Agent skills` block to add to whichever of `CLAUDE.md` / `AGENTS.md` is being edited (see step 4 for selection rules)
> - The contents of `docs/agents/issue-tracker.md`, `docs/agents/domain.md`, and `docs/agents/triage-labels.md` (the last only when `triage` is installed)
>
> Let them edit before writing.
>
> ### 4. Write
>
> **Pick the file to edit:**
>
> - If `CLAUDE.md` exists, edit it.
> - Else if `AGENTS.md` exists, edit it.
> - If neither exists, ask the user which one to create; don't pick for them.
>
> Never create `AGENTS.md` when `CLAUDE.md` already exists (or vice versa); always edit the one that's already there.
>
> If an `## Agent skills` block already exists in the chosen file, update its contents in-place rather than appending a duplicate. Don't overwrite user edits to the surrounding sections.
>
> The block:
>
> ```markdown
> ## Agent skills
>
> ### Issue tracker
>
> [one-line summary of where issues are tracked]. See `docs/agents/issue-tracker.md`.
>
> ### Triage labels
>
> [one-line summary of the label vocabulary]. See `docs/agents/triage-labels.md`.
>
> ### Domain docs
>
> [one-line summary of layout: "single-context" or "multi-context"]. See `docs/agents/domain.md`.
> ```
>
> Include the `### Triage labels` sub-block, and write `docs/agents/triage-labels.md`, only when `triage` is installed and Section B ran. When it isn't, both are omitted.
>
> Then write the docs files using the seed templates in this skill folder as a starting point:
>
> - [issue-tracker-github.md](./issue-tracker-github.md): GitHub issue tracker
> - [issue-tracker-gitlab.md](./issue-tracker-gitlab.md): GitLab issue tracker
> - [issue-tracker-local.md](./issue-tracker-local.md): local-markdown issue tracker
> - [triage-labels.md](./triage-labels.md): label mapping (only if `triage` is installed)
> - [domain.md](./domain.md): domain doc consumer rules + layout
>
> For "other" issue trackers, write `docs/agents/issue-tracker.md` from scratch using the user's description.
>
> ### 5. Done
>
> Tell the user the setup is complete and which engineering skills will now read from these files. Mention they can edit `docs/agents/*.md` directly later; re-running this skill is only necessary if they want to switch issue trackers or restart from scratch.
> </skill>

### 结果

- 采用
- 按该流程依次确认任务平台、默认标签和入口文件，并先展示草稿再写入。正文依据本次用户提供的技能内容及读取的同一 SKILL.md 留存；本地路径已脱敏。

## P-003

- 目标：选择 GitHub Issues 并创建远程仓库

### Prompt 原文

> 使用github仓库，github尚未创建仓库，帮我创建仓库，名字为AR-work

### 结果

- 采用
- gh repo create 成功；gh repo view 验证仓库为 PRIVATE 且 hasIssuesEnabled=true；git remote -v 验证 origin 已关联。省略账号和完整远程地址。

## P-004

- 目标：确认采用默认五个分诊标签

### Prompt 原文

> 是

### 结果

- 采用
- 对应上一轮默认标签确认问题。检查 triage-labels.md 包含 needs-triage、needs-info、ready-for-agent、ready-for-human、wontfix。仅配置标签映射，未声称远程标签已创建。

## P-005

- 目标：选择 Agent 配置入口并落实配置草稿

### Prompt 原文

> agent.md

### 结果

- 修改后采用
- 助手将输入解释为选择标准入口 AGENTS.md，并在写入前明确说明；没有创建字面名称 agent.md。写入后检查三个配置文件均存在且被入口引用。本次再次读回四份文件核对内容。这是助手的检查和命名调整，不代表用户另行确认了拼写修改。

## P-006

- 目标：保存真实 Prompt、结果和验证过程并提交日志

### Prompt 原文

> 请在项目根目录提交 `AI_LOG.md`，按实际顺序保存本题中所有由你主动发给 AI 的问题或指令，包括追问和纠错。
>
> 每条记录使用以下简化格式即可：
> ```Markdown
> ## P-001
>
> - 目标：解决什么问题
>
> ### Prompt 原文
>
> > 实际发送给 AI 的问题或指令
>
> ### 结果
>
> - 采用 / 修改后采用 / 未采用
> - 简要说明如何验证
> ```
>
> 要求：
>
> - 必须保留 Prompt 原文，不能只写总结；
> - 不需要记录 IDE 的逐字自动补全；
> - 至少记录一次你检查、修改或放弃 AI 结果的过程；
> - 你需要能够解释最终提交的核心代码；
> - 不得提交 API Key、Token、账号、个人隐私或其他敏感信息；如需脱敏，请使用类似 `[REDACTED: API token]` 的占位内容。

### 结果

- 采用
- 按本次可见对话逐条记录原文（敏感路径除外），并保留完整技能正文；提交前验证编号、必要小节、配置引用、默认标签及敏感信息扫描。首次 git diff --cached --check 发现引用空行尾随空格，已移除引用标记后的多余空格，保留原文空行，并重新检查。只暂存 AI_LOG.md。Git 提交是否成功以 Git 实际返回为准。

## 核心实现说明与边界

本次没有业务代码，不能声称实现或测试了业务功能。现有配置的核心行为如下：

- AGENTS.md 是入口，通过条件明确的引用引导工程技能读取三份专用配置。
- docs/agents/issue-tracker.md 将任务和规格映射到 GitHub Issues，规定 gh 操作、多行正文文件传入方式以及任务领取、阻塞、完成约定。
- docs/agents/triage-labels.md 将五个规范角色映射到同名标签；配置文件本身不会在 GitHub 创建标签。
- docs/agents/domain.md 规定单上下文布局，缺失领域文档时继续工作，按需生成；存在 ADR 冲突时明确指出。

检查与调整：采用 Windows 适用的 --body-file 方式替换种子模板中的 heredoc 建议；按单上下文选择精简领域文档；将用户输入 agent.md 解释为标准 AGENTS.md，并检查入口的三个文件引用。验证仅针对配置文件和仓库状态，未运行业务测试。

本次日志提交只包含 AI_LOG.md；此前生成的工程配置仍留在工作区，未包含在该提交内。未执行推送。
