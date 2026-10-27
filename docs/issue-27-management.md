# #27 PC 管理验收与 #28 交接

## 实现范围

基线 `f20ed0a`（#26 技术交接），实现仅修改 `web/` 和本文件。

- 全量统计卡与状态联动；标题/描述搜索、状态和优先级组合筛选、结果数；创建/更新时间降序、优先级高到低排序（同值按编号稳定排序）。
- 紧凑快照缩略图表格，选中详情侧栏；1024 宽度隐藏表格时间列，详情仍显示全部时间与位置；800 以下侧栏移到表格上方。
- 默认现场照片、大图和缩略图/照片重试；保留 #24 三维颜色/标记/旋转/平移/缩放/复位/失败重试与按需加载；保留 #26 标题确认删除、锁定、失败保留和成功刷新。
- 草稿、操作锁及操作结果按接收端地址与问题编号保存于当前网页内存；切换记录、关闭侧栏、筛选与 SSE 不丢草稿。保存成功仅清除该次保存的草稿对象；查询确认远端删除后清除其草稿，不能另建问题。浏览器整页刷新仍不承诺持久草稿。
- 旧列表请求通过中止与有效标志隔离；旧模型由原查看器中止与资源释放隔离；旧接收端删除回执不得改写当前接收端列表。
- 关闭侧栏恢复原列表按钮焦点，原按钮被筛选隐藏时回到搜索。原生模态大图/删除对话框支持 Escape、焦点循环与返回；保存中的删除确认不能被 Escape 取消。

## 可复验入口

`web/acceptance/seed.py` 创建三条合成演示记录，仅发往独立端口 **18142**；不得把该端口转发到用户数据。浏览器脚本为 playwright-cli `run-code --filename` 输入，使用独立会话 `issue27` 和网页 **5197**。

演示服务数据目录：`.local/issue27/issues.json`。为避免干扰 #26 的 UDP 18087，验收使用 `.local/issue27/backend` 临时复制版，仅把 `serveDiscovery(receiver)` 的启动替换成随机本地 UDP socket；HTTP 路由/持久化/模型/删除代码均为当前仓库代码，仓库 backend 没有改动。网页 Vite 设置 `ARWORK_API_URL=http://127.0.0.1:18142`。此实例不验证发现协议。

空演示列表上运行 `python web/acceptance/seed.py`，依序执行：

```powershell
npx --yes --package @playwright/cli playwright-cli -s=issue27 open http://127.0.0.1:5197
npx --yes --package @playwright/cli playwright-cli -s=issue27 run-code --filename web/acceptance/issue27-core.txt
npx --yes --package @playwright/cli playwright-cli -s=issue27 run-code --filename web/acceptance/issue27-concurrency.txt
npx --yes --package @playwright/cli playwright-cli -s=issue27 run-code --filename web/acceptance/issue27-stale.txt
npx --yes --package @playwright/cli playwright-cli -s=issue27 run-code --filename web/acceptance/issue27-recovery.txt
```

`core`：全量统计、描述搜索、组合筛选、无匹配、优先级排序、切换/筛选/SSE 草稿、内容保存不覆盖状态、大图 Escape 与焦点、三维接入和新记录默认照片。

`concurrency`：延迟内容保存，切走再返回仍锁定，失败后可见错误/草稿/重试；1024/1366/1920 宽度无页面横向溢出、编辑 Tab 顺序；删除模态焦点循环。

`stale`：旧地址查询挂起时通过正常地址输入/连接操作切换，旧空列表返回不能覆盖新列表。

`recovery`：HTTP 边界注入照片/模型 503、删除 500 和列表 503 后恢复；缩略图/照片/模型重试，真实三维鼠标操作，延迟旧模型隔离；标题确认、失败保留、Escape/焦点、真实成功删除和远端删除清理草稿；旧协议无照片/模型；创建/更新时间排序、真实空态。仅删除已知演示 ID，结束清空该次创建的演示问题。

## 证据与限制

先验证旧页缺少统计筛选的红灯，再实现和验证；键盘焦点循环及切换保存反馈的失败先复现后修复。浏览器错误日志中的 500/503 均来自明确注入的错误场景，不代表未处理脚本异常。

前端 `npm --prefix web run build`（含 TypeScript）和 `lint`；构建已有 Three.js 延迟分块超过 500 kB 的提示，模型继续按需加载。

Standards 初审：0 文档规则违反，2 个重复逻辑建议，另发现保存失败提示跨卸载丢失；Spec 初审：同一 P2，未发现其他缺失或越界。修复包括按记录保存反馈、统一当前记录键、清理 CSS 重复覆盖。最终复审结果见本 issue 的完成评论。

#27 属于 PC 交付；本次未操作手机/Unity，未将合成模型测试称为真实扫描对应验收。#23/#24/#25/#26 的设备及网络未完成项继续由 #28 统一验收。未推送或合并，未关闭父规格。README、AI_LOG 和 Unity 模拟设置无关改动保留。

## 最终检查（2026-10-27）

候选提交 `3cac741`：四段浏览器验收整序列通过，输出在 `.local/issue27/final-{core,concurrency,stale,recovery}.log`；`go test ./...` 通过（12.294s）、`go vet ./...` 通过；前端 build/lint 通过，无 lint 警告。Standards / Spec 对 `f20ed0a...3cac741` 复审均为 0 剩余发现。

截图已实际查看：`output/playwright/issue27-1366.png`（完整统计工具栏与表格）、`output/playwright/issue27-1024-details.png`（窄 PC 侧栏）；1920 宽度及编辑状态由浏览器断言覆盖。验收后独立演示服务 18142、Vite 5197 和浏览器会话 issue27 保留供查看，内有重新创建的三条合成记录；不得与 #26 的 18132、旧 18088 混用。

本任务提交：`a136683` 实现、`41356ea` 行尾规范化、`3cac741` 复审修复与接续文档；后续文档提交仅补充本段最终证据。
