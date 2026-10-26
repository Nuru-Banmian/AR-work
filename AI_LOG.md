# AI 使用记录

范围：AR-work 初始化、工程技能配置及后续需求讨论，按发送顺序记录用户向当前 AI 助手发送的指令；从 P-009 起也记录助手实际发送给子代理的指令，并在目标中注明发送方。技能正文作为单独一条保留；环境、界面上下文、系统指令、工具输出和 IDE 自动补全不作为主动 Prompt。

归属说明：P-001 至 P-006 对应的初始化阶段未调用其他 AI 或子代理。后续阶段的实际委派另行记录。日志不能用于声称用户进行了未发生的独立审核，也不包含不可见历史。

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

## P-007

- 目标：用户请求：提供三端原型需求，并先检查手机 AR 兼容性

### Prompt 原文

> [$ask-matt]([REDACTED: local user path]/ask-matt/SKILL.md) 以下是我这次项目的大致需求
>
> ## 1. 项目背景
>
> 园区巡检人员发现墙面破损、设备异常或安全隐患时，希望用手机在现场放置一个 AR 标记并上报问题。管理人员随后在电脑网页中查看这些问题并更新处理状态。
>
> 请完成一个小型原型，打通以下流程：
> ```Plaintext
> Unity AR 放置标记并上报
>   → Go 后端保存
>   → React 页面查看问题
>   → React 修改处理状态
> ```
>
> ## 2. 固定技术栈
>
> | 部分  | 技术要求                       | 作用             |
> | --- | -------------------------- | -------------- |
> | 移动端 | Unity LTS、C#、AR Foundation | 检测平面、放置标记、上报问题 |
> | 管理端 | React、TypeScript           | 查看问题、修改状态      |
> | 后端  | Go                         | 提供 API 并持久化数据  |
>
> - Unity 可选择 Android + ARCore 或 iOS + ARKit；
> - Go 框架不限；
> - 数据可保存到 SQLite 或本地 JSON 文件；
> - 可以使用 AI 辅助开发，但不要求产品本身接入大模型。
>
> ## 3. 必做功能
>
> ### 3.1 Unity AR 移动端
>
> 1. 在支持 AR 的真机上运行；
> 2. 检测真实环境中的水平平面；
> 3. 点击检测到的平面，在命中位置放置一个简单标记；
> 4. 填写并提交问题：
>    1. 标题：必填；
>    2. 描述：可选；
>    3. 优先级：`low`、`medium`、`high`；
>    4. 位置：标记的 `x/y/z` 坐标；
> 5. 提交成功或失败时，给用户明确提示；
> 6. 点击按钮或输入框时，不应误在 AR 场景中放置标记。
>
> 标记可以直接使用立方体、球体等 Unity 基础对象，不考察美术效果。
>
> ### 3.2 React 管理端
>
> 1. 从 Go 后端获取问题列表；
> 2. 至少显示标题、优先级和状态；
> 3. 能将状态修改为：
>    1. `open`；
>    2. `in_progress`；
>    3. `resolved`；
> 4. 后端不可用时，页面应显示错误提示，不能直接白屏。
>
> ### 3.3 Go 后端
>
> Go 后端需要为 Unity 和 React 提供以下能力：
>
> 1. 提供简单的服务运行状态检查；
> 2. 接收并保存 Unity 上报的问题；
> 3. 返回已经保存的问题列表；
> 4. 根据问题 ID 修改处理状态；
> 5. 为新问题生成唯一 ID，默认状态为 `open`；
> 6. 标题为空或枚举值错误时，返回明确的参数错误；
> 7. 数据在 Go 服务重启后仍然存在；
> 8. Unity 和 React 使用同一个 Go 后端和同一份数据；
> 9. React 在浏览器中能够正常访问后端。
>
> 接口路径和响应格式可以自行设计，请在 README 中简要说明。
>
> ## 4. 基础数据结构
>
> 三端字段名称应保持一致：
> ```JSON
> {
>   "id": "由后端生成",
>   "title": "入口墙面破损",
>   "description": "左侧墙体存在裂缝",
>   "priority": "high",
>   "status": "open",
>   "position": {
>     "x": 0.42,
>     "y": 0.03,
>     "z": 1.26
>   },
>   "createdAt": "由后端生成",
>   "updatedAt": "由后端生成"
> }
> ```
>
> 不要求应用重启后，把历史标记恢复到完全相同的真实物理位置。
>
> 目前我不确认我的手机是否支持ar，请你指导我检查

### 结果

- 采用
- 本轮先处理用户明确提出的手机兼容性检查；读取 ask-matt 和仓库配置，向用户索取型号及系统版本，查阅 Google 官方 ARCore 设备名单和运行时检查文档。三端原型尚未实现，真机尚未验收。

## P-008

- 目标：用户提供 ask-matt 路由技能全文

### Prompt 原文

> <skill>
> <name>ask-matt</name>
> <path>[REDACTED: local user path]/ask-matt/SKILL.md</path>
> ---
> name: ask-matt
> description: Ask which skill or flow fits your situation. A router over the skills in this repo.
> disable-model-invocation: true
> ---
>
> # Ask Matt
>
> You don't remember every skill, so ask.
>
> A **flow** is a path through the skills. Most paths run along one **main flow**, and two **on-ramps** merge onto it. Everything else is standalone, or a vocabulary layer that runs underneath.
>
> ## The main flow: idea → ship
>
> The route most work travels. You have an idea and want it built.
>
> 1. **`/grill-with-docs`** sharpens the idea by interview. Start here whenever you are **working in a working directory**: it's stateful, retaining what it learns in `CONTEXT.md` and ADRs. (No working directory? Use `/grill-me` instead, covered under Standalone. Both run the same `/grilling` primitive; `grill-with-docs` is the one that leaves a paper trail, which makes it the better of the two whenever a repo is there to leave it in.)
> 2. **Branch: can you settle every question in conversation?** If a question needs a runnable answer (state, business logic, a UI you have to see), detour through a prototype, bridged by **`/handoff`** in both directions (a prototype lives in its own directory, which is exactly what `/handoff` is for; see Phase boundaries):
>    - **`/handoff`** out, then open a fresh session against that file,
>    - **`/prototype`** to answer the question with throwaway code,
>    - **`/handoff`** back what you learned, and reference it from the original idea thread.
> 3. **Branch: is this a multi-session build?**
>    - **Yes** → **`/to-spec`** (turn the thread into a spec), then **`/to-tickets`** to split it into tracer-bullet tickets, each declaring its **blocking edges**. On a local tracker that's one file per ticket under `.scratch/<feature>/issues/`, worked blockers-first by hand; on a real tracker the edges become native blocking links, so any ticket whose blockers are done can be grabbed: kick off **`/implement`** per ticket, **`/clear`ing context between each one**. Each ticket is self-contained, so the last one's context is disposable.
>    - **No** → **`/implement`** right here, in the same context window.
>
>    Either way, **`/implement`** builds each issue by driving **`/tdd`** internally (one red-green slice at a time), then closes out by running **`/code-review`**, a two-axis review (Standards + Spec) of the diff, before committing. Reach for **`/tdd`** on its own when you just want to build a concrete behaviour test-first without a full spec, and **`/code-review`** on its own whenever you want to review a branch or PR against a fixed point.
>
> ### Context hygiene
>
> Keep steps 1–3 in **one unbroken context window** (don't compact or clear until after `/to-tickets`) so the grilling, spec, and tickets all build on the same thinking. Each `/implement` then starts fresh, working from the ticket.
>
> The limit on this is the **[smart zone](https://www.aihero.dev/ai-coding-dictionary/smart-zone)**: the window (~150k tokens on state-of-the-art models) within which the model still reasons sharply. If a session approaches it before `/to-tickets`, don't push on degraded; `/compact` at the nearest phase boundary and carry on (see Phase boundaries).
>
> ## On-ramps
>
> A starting situation that generates work, then merges onto the main flow.
>
> - **Bugs and requests piling up** → **`/triage`**. It moves issues through triage roles and produces agent-ready issues, which **`/implement`** later picks up.
>
>   Triage is only for issues **you didn't create**: bug reports, incoming feature requests, anything that arrives raw. Tickets that `/to-tickets` produced are already agent-ready, so **don't triage them**.
>
> - **Something's broken** → **`/diagnosing-bugs`**. For the hard ones: the bug that resists a first glance, the intermittent flake, the regression that crept in between two known-good states. It refuses to theorise until it has a **tight feedback loop** (one command that already goes red on *this* bug), then fixes with a regression test. Its post-mortem hands off to **`/improve-codebase-architecture`** when the real finding is that there's no good seam to lock the bug down.
>
> - **A huge, foggy effort: a greenfield project or a huge feature build, too big for one session** → **`/wayfinder`**, the most cognitively demanding flow here. When the way from here to the destination isn't visible yet, it charts a **shared map** of **decision tickets** on the issue tracker and resolves them one at a time, producing **decisions, not deliverables**, until the fog is pushed back and the way is clear. Where **`/grill-with-docs`** sharpens an idea you can hold in one session, wayfinder is for the idea you can't, and it's slower and denser, so save it for exactly that, never a well-scoped feature.
>
>   When the map clears, **it hands off, it doesn't build**: merge onto the main flow at **`/to-spec`**, which collapses the map's linked decisions into a buildable plan, then `/to-tickets` and `/implement` as usual. Looping the map straight into `/implement` skips that collapse and throws the linked detail away, so go straight to `/implement` only when the effort turned out genuinely small.
>
> ## Codebase health
>
> Not feature work, just upkeep.
>
> - **`/improve-codebase-architecture`** runs whenever you have a spare moment to keep the codebase good for agents to operate in. It surfaces **deepening opportunities**; picking one _generates an idea_ you can take into the main flow at `/grill-with-docs`. It's the survey that finds the candidates; **`/codebase-design`** (below) is the bench you design the chosen one on.
>
> ## Vocabulary underneath
>
> Two model-invoked references that run *beneath* the other skills, each the single source of truth for its vocabulary. Reach for them directly when the **words**, not the process, are the problem; or let the skills above pull them in.
>
> - **`/domain-modeling`**: sharpen the project's *domain* language: challenge a fuzzy term, resolve an overloaded word ("account" doing three jobs), record a hard-to-reverse decision as an ADR. It's the active discipline `/grill-with-docs` drives to keep `CONTEXT.md` a clean glossary.
> - **`/codebase-design`** is the deep-module vocabulary (module, interface, depth, seam, adapter, leverage, locality) for designing a module's *shape*: a lot of behaviour behind a small interface at a clean seam. `/tdd` and `/improve-codebase-architecture` both speak it.
>
> ## Phase boundaries
>
> A **phase** is a chunk of work inside a session: the grilling, the implementation, the QA. At the **boundary** between two of them you have five options, and picking between them is the fuzziest decision in this whole map:
>
> - **Continue**: stay put. Costs nothing, loses nothing.
> - **`/clear`**: empty the window, when nothing here matters to what's next.
> - **`/handoff`** writes a portable markdown file. Narrow: only for a **new harness**, a **new directory**, a **colleague**, or forking a side task **mid-phase**. What it buys is portability.
> - **Subagent**: send a tightly-scoped task to its own window and get a report back.
> - **`/compact`** compresses this context and seeds a fresh session with it. The **default**, at the bottom of the tree rather than the first reach.
>
> Read [PHASE-BOUNDARIES.md](PHASE-BOUNDARIES.md) for the ordered tree: the five questions, the reasoning behind each branch, and why the primary-source cost makes **Continue** the one to rule out first. Make the decision **at** a boundary; mid-phase, continue or split the rest into subagents.
>
> ## Standalone
>
> Off the main flow entirely.
>
> - **`/grill-me`**: the same relentless interview as `/grill-with-docs`, but **stateless**: it saves nothing locally and builds no `CONTEXT.md`. Reach for it when you are **not working in a working directory** (sharpening a plan, a design, a piece of writing, anything with no repo under it). If you are in a working directory, use `/grill-with-docs` instead: it runs the same interview and leaves a paper trail, so it is strictly the better one.
> - **`/grilling`** is the interview primitive itself: rounds, the frontier, facts are the agent's job and decisions are yours. `/grill-me` and `/grill-with-docs` are the two named ways in, and `/triage`, `/wayfinder` and `/improve-codebase-architecture` all run it internally. Reach for it directly only when you want the interview with no wrapper around it.
> - **`/resolving-merge-conflicts`** works an in-progress merge or rebase conflict hunk by hunk, resolving by **intent** traced to each side's primary source rather than by picking lines, then finishes the operation. It never runs `--abort`. Standalone and off every flow: reach for it when you are already mid-conflict.
> - **`/prototype`** is a small, throwaway program that answers one design question: does this state model feel right, or what should this UI look like. Throwaway is a constraint on how the code is written, not a promise to destroy it: the answer folds into the real code, and the prototype itself is kept as a **primary source** on a `prototype/<name>` branch out of main, pointed at from the implementation issue. It's the detour in step 2 of the main flow, but reach for it any time a design question is hard to settle on paper.
> - **`/research`**: delegate reading legwork to a **background agent**: it investigates a question against **primary sources**, then leaves a cited Markdown file in the repo. Keep working while it reads. The file it produces is something to take *into* the main flow at `/grill-with-docs`, since research feeds the thinking rather than replacing it.
> - **`/to-questionnaire`** comes in when the thing blocking you isn't in your head or the codebase but in **someone else's**, and it writes them a questionnaire to fill in. It's the inverse of `/grill-me`: instead of interviewing you about the subject, it interviews you about the **send** (who it's going to, what you need back) and aims the questions at the gap. What comes back is material for `/grill-with-docs` or `/to-spec`.
> - **`/wizard`** is for the steps only a **human** can take: provisioning infrastructure, setting up credentials or CI secrets, clicking through an unfamiliar third-party dashboard, running a one-off migration or cutover. It generates an interactive bash script that opens each URL, captures each value, and writes it into `.env` and GitHub secrets, so the procedure stops being something you re-explain to an agent every time. Model-invoked, so the agent reaches for it the moment it hits a wall only you can pass. If the agent could just do it itself, it should; this is for where a human is genuinely in the loop.
> - **`/wait-what`** is the corrective for a message that didn't land. Use it mid-conversation, inside any other skill, and the agent re-pitches what it just said with the context you were missing, in plain English, using the `CONTEXT.md` vocabulary. It works after the fact; `/grill-with-docs` is the upfront cure, because a shared language agreed early is what stops the jargon arriving at all.
> - **`/teach`**: learn a concept over multiple sessions, using the current directory as a stateful workspace.
> - **`/writing-for-agents`** is the reference for writing documents agents consume: skills, AGENTS.md, pointed-at docs.
>
> ## Precondition
>
> **`/setup-matt-pocock-skills`**: run before your first engineering flow to configure the issue tracker, triage labels, and doc layout the other skills assume. Custom issue trackers also work.
> </skill>

### 结果

- 采用
- 结合已有工作目录，将后续需求澄清对应到 grill-with-docs，再视构建规模进入 to-spec、to-tickets 和 implement。本轮先解决设备可行性，尚未执行这些后续流程。

## P-009

- 目标：助手向子代理委派 Android 官方兼容性核查

### Prompt 原文

> 请只读核查 Android 手机是否支持本项目所需 ARCore 的官方检查流程。项目用 Unity AR Foundation 做水平平面检测、点选平面放置标记；用户尚未提供手机型号。使用 Google 官方来源，说明如何核对准确型号和系统版本、Google Play Services for AR 的作用、中国大陆设备差异、为什么安装成功不足以证明平面检测正常。返回简短中文建议和直接来源链接。不要修改文件，不要再派生子代理，不要发起用户提问。

### 结果

- 采用
- 子代理查阅 Google 官方资料；主代理独立打开同一设备页核对 Google Play 与 China 两个区域条目，并核对官方运行时支持检查要求。

## P-010

- 目标：用户补充手机型号

### Prompt 原文

> 我的手机是小米13pro

### 结果

- 采用
- 将核查对象限定为 Xiaomi 13 Pro；官方 Google Play 区域名单列出该型号，但 China 区域没有单独列出，故保留地区系统及真机运行验证项。

## P-011

- 目标：助手向子代理追加准确型号与核查重点

### Prompt 原文

> 用户补充：手机是小米13pro。请重点核对 Xiaomi 13 Pro 在官方 ARCore 列表中的条目、最低系统和中国大陆机型/安装渠道说明。系统版本及地区版本仍未知。

### 结果

- 采用
- 对照官方列表确认 Xiaomi 13 Pro 在 Google Play 区域的记录及中国区缺项；没有把 Google Play 版本支持推断为国行当前系统必定可用，也没有把安装 AR 服务视为平面检测验收。

本轮核查来源：

- [Google ARCore 支持设备](https://developers.google.com/ar/devices)：区分 Google Play 与 China 区域。
- [Enable AR in your Android app](https://developers.google.com/ar/develop/java/enable-arcore)：支持检测、服务安装更新与相机权限要求。

截至 P-011，尚未获得用户手机的 Android/HyperOS 版本和 AR 服务状态，未运行真机平面检测。

## P-012

- 目标：用户补充手机 HyperOS 完整版本，继续检查 AR 运行条件

### Prompt 原文

> hyperos 3.0.312.0.WMBCNXM.C11

### 结果

- 采用
- 将该版本作为用户报告的信息记录，未独立读取手机核实；不依据版本字符串推断确切 Android 版本或 ARCore 运行结果。继续索取设置中显示的 Android 版本和 AR 服务安装状态，真机平面检测仍待执行。
