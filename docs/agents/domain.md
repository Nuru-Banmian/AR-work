# Domain docs

采用 single-context 布局：

- 根目录 `CONTEXT.md`：领域术语和模型。
- `docs/adr/`：架构决策记录。

## 探索前读取

读取根目录 `CONTEXT.md`，以及与当前工作相关的 ADR。
文件不存在时直接继续；由 `domain-modeling` 技能在术语或决策明确后按需创建。

## 使用领域术语

Issue、设计、代码和测试中的领域概念使用 `CONTEXT.md` 定义的名称。
缺少术语时，先检查是否已有对应概念，再将实际缺口交给 `domain-modeling` 处理。

## 决策冲突

方案与现有 ADR 冲突时，明确指出对应 ADR 和重新讨论的理由。
