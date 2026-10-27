# Issue tracker: GitHub

任务和规格保存在 Nuru-Banmian/AR-work 的 GitHub Issues。
所有操作使用 gh CLI，在仓库目录外执行时指定
`--repo Nuru-Banmian/AR-work`。

## 常用操作

- 创建：`gh issue create --title "..." --body-file <file>`
- 读取：`gh issue view <number> --comments`
- 列表：`gh issue list --state open`，按需过滤标签
- 评论：`gh issue comment <number> --body-file <file>`
- 添加标签：`gh issue edit <number> --add-label "..."`
- 移除标签：`gh issue edit <number> --remove-label "..."`
- 关闭：`gh issue close <number> --comment "..."`

多行正文写入 UTF-8 临时文件，再通过 `--body-file` 传入。
分诊时同时读取正文、评论和标签；标签映射见 `triage-labels.md`。

## 技能指令映射

“发布到 issue tracker”表示创建 GitHub Issue。
“获取相关 ticket”表示读取对应 Issue 及其评论。

## Pull requests as a triage surface

PRs as a request surface: no.

## Wayfinding

- Map：使用带 `wayfinder:map` 标签的 Issue，包含 Notes、Decisions-so-far 和 Fog。
- 子任务：优先关联为 GitHub sub-issue；不可用时，在 Map 正文使用任务列表，并在子任务注明 `Part of #<map>`。
- 类型标签：`wayfinder:research`、`wayfinder:prototype`、`wayfinder:grilling` 或 `wayfinder:task`。
- 阻塞关系：优先使用 GitHub 原生 Issue dependencies；不可用时在正文顶部记录 `Blocked by: #<n>`。
- 选择任务：按 Map 顺序选择未关闭、无未关闭阻塞项、且未分配负责人的子任务。
- 领取：开始工作前将任务分配给当前开发者。
- 完成：发表评论记录答案，关闭子任务，并向 Map 的 Decisions-so-far 添加摘要和链接。
