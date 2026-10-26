# Issue #4 验收记录

日期：2026-10-26（Asia/Shanghai）。范围为手机查询全部历史问题、详情和内容编辑；重新标记、SSE 和视频交付属于后续任务。

## 自动化与构建

- 用户确认边界为真实 HTTP API、临时持久化数据和手机/网页实际验收；审查起点 `a4d39f45b6cdf284db7ecbce2ed1957dc94e1c4c`。
- TDD 首个内容修改 HTTP 测试先返回 404；实现 `/content` 后通过。覆盖编辑期间管理端修改状态、内容修改不覆盖 ID/状态/位置/createdAt、详情与列表一致、重启持久化、创建原始幂等回执不受编辑影响。
- 错误覆盖空标题、非法优先级、缺失/null 字段、非法 JSON、多对象、夹带状态或位置、超出 64 KiB、未知 ID、真实文件替换失败。验证失败后原记录仍可查询，恢复存储后同一内容可重试。
- 最终 `go test ./... -count=1 -v`：12 项顶层测试和既有 9 个非法创建子用例全部通过；`go vet ./...` 通过。新增两个内容相关顶层测试，没有改写历史测试。
- `npm run build`（严格 TypeScript + Vite）、`npm run lint` 通过。
- Unity Android ARM64 / IL2CPP 构建成功：18,016,902 字节，SHA-256 `903a4d62a495790535ae2b035b6a8889854fd3784c2a9c3737a064ee055a5e9b`。ZIP CRC、7 个原生库 16 KB 对齐和 apksigner v2 签名验证通过。实际覆盖安装并运行于小米 13 Pro，不以构建替代以下真机证据。

## 真机与网页同一 ID

验收使用单独文件 `.local/issue4-acceptance.json`，Go `:18085`，网页 `http://127.0.0.1:5174`；手机通过局域网 `http://192.168.1.8:18085` 访问，不使用 USB 转发或伪造响应。为隔离内容编辑，起始问题通过真实 POST API 创建。

问题 ID：`4565d42dd3b9e43c049c6b4d0e9eabfa`，原标题 `Issue4 phone edit`、描述 `Before phone edit`、priority high、position (0,1,2)。

1. 手机 `ALL ISSUES` 读取全部列表，点击行重新请求详情，显示 open、ID、时间和历史坐标。顶部 SessionTracking、Marker: none、Placements: 0。
2. 网页将状态改为 resolved。手机输入标题 `issue4-updated-on-phone`、priority low，点击 REFRESH，显示 resolved，同时保留标题和优先级草稿。之后输入描述 `checked-on-phone`。
3. 关闭本次 Go 后手机点击 SAVE CONTENT，等待真实超时，显示 `Request timeout` 与 `Draft kept`，三个内容字段不丢失、可重试。
4. 用同一端口启动指向另一份空测试数据的真实 Go，手机在已打开的旧详情保存，得到真实 `404 not_found: Issue not found`，草稿保留；原文件没有删除或清空。
5. 恢复 Go 指向原验收文件，读取成功后将该文件移到备份并在原路径创建空目录，制造真实落盘失败。手机保存得到 `500 storage_error`、原记录未变、草稿保留。移除空目录并恢复备份后继续。
6. 网页在手机仍显示 resolved 时改为 in_progress。手机直接用原草稿保存成功，回执为同一 ID 和 in_progress。网页刷新显示新标题、描述、low 和 in_progress；坐标与创建时间保留。手机日志出现 `ARWORK_CONTENT_SAVED_ID=4565...abfa`。
7. 手机清空标题后保存，明确提示 `Title is required. Draft kept.`，描述与优先级保留；返回列表再打开详情，空标题草稿仍保留。该无效编辑未改动后端成功记录。
8. 强制停止手机应用，并停止、重启 Go 读取原文件。手机重新打开应用，保存的服务器地址仍在，进入列表/详情读到同一 ID、新内容、low、in_progress。网页整页重新加载显示相同记录。手机仍为 Marker: none、Placements: 0，没有按历史位置恢复 AR 标记。

原始截图位于忽略目录 `.local/issue4-*.png`，关键证据为 list、draft-refreshed、offline-draft、not-found、storage-error、save-success、validation、navigation-draft、restarted-detail。网页证据为 `output/playwright/issue4-before.png`、`issue4-phone-content.png`、`issue4-restarted.png`；未提交现场图片、APK 或运行数据。

## Standards

独立子代理审查快照 `53b7ef1a5cec3a380b0cbc8de7420fdfb0bf366b`，确认 diff 非空：0 项文档规范硬违规、0 项需修改的 smell 建议，未发现可确认的重大代码缺陷。

## Spec

另一独立子代理对同一快照核对 #4 和 #1 相关范围：0 项可确认代码偏差、0 项范围扩张。审查时待补的真机和浏览器证据已由以上流程完成。补充文档复核发现 README 将示例 :8080 误称为本次验收地址，已改为明确区分示例端口与 #4 的 :18085；无过强功能声明。

## 限制

历史编辑草稿仅在当前应用进程内按服务器和 ID 保留，未承诺跨应用重启恢复；已保存内容从后端重新读取。内容字段同字段竞争采用最后一次成功保存，无登录/身份筛选。坐标仅为旧 AR 会话数据，不代表跨会话位置。本次不证明重新标记、SSE、负载或生产可用性。
