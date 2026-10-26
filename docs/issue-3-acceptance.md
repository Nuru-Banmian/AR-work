# Issue #3 验收记录

日期：2026-10-26（Asia/Shanghai）。对应 #3 可靠上报、竖直面及网页处理状态；包含本轮用户要求优先处理的 AR 选点改进。#4 及后续手机历史编辑、重新标记、SSE 和交付视频仍不在本次范围。

## 自动化与构建

- 沿用用户确认的验证边界：真实 Go HTTP API 与临时持久化数据、真实 React 页面、Unity 小米真机。审查基线为 `5b0c25e88405a157bbb4c74dd6cc94ad162fcc73`。
- TDD：幂等重放初次返回新问题和 201、同键并发产生重复，测试先失败；实现后通过。状态测试初次得到 404，新增详情/状态 API 后通过。审查发现有符号零边界后，先将重放用例改为 -0.0，确认得到 409，再统一正负零，回到 200 与原回执。
- 丢响应 HTTP 用例使用真实转发服务：后端完成持久化后，代理读取并丢弃响应、直接关闭连接；客户端确实收到传输失败。随后关闭并重新创建 API，原键重试得到原问题，变更内容返回 409。
- 最终 `go test ./... -count=1 -v`：10 项顶层测试全部通过，含既有 9 个非法请求子用例；`go vet ./...` 通过。新增覆盖同键 16 并发、重启后重放、旧数组迁移保留、JSON 顺序/数值/标题规范化、任意状态切换、未知 ID、非法状态、失败不更新问题或占用键。
- 前端 `npm run build`（严格 TypeScript + Vite）和 `npm run lint`（Oxlint）通过。浏览器验收使用真实服务，没有替换 API 响应。
- 最终 Android ARM64 / IL2CPP 构建成功，18,006,630 字节，SHA-256 `08591031f2291cb637af10cb0f111592208dd1cba353bf10bee7abf4a1104b34`。ZIP CRC、7 个 ARM64 原生库的 16 KB 对齐、apksigner 签名验证通过。该 APK 已覆盖安装到小米 13 Pro；以下手机测试使用此版本。

## 真机 AR 与表单

1. 最终包实际显示 SessionTracking、Depth: ready；用户扫描后截图显示 Horizontal=4、Vertical=3、Placements=6、Hit=Depth，命中距离约 0.71 米，现场墙面附近有方块。后续截图显示多个水平及竖直面、可重复调整标记。
2. 默认 Depth 优先，未命中则回退平面；底部可切换 PLANE ONLY。此前同机对照包中用户明确反馈“深度模式明显更贴合”。最终版补上平面可见性修复；不将该定性反馈当作误差测量。方案、官方来源和限制见 [AR 检测方案](ar-detection-options.md)。
3. 主代理通过 ADB 操作真实手机：进入表单、输入标题 `issue3-retry`，点击 AR 区域，再返回选点；截图标题保留、显示 Draft kept，Placements 仍为 18。重新进入表单锁定后提交。输入框、按钮及锁定期间的画面点击没有增加标记次数。
4. 失败截图保留标题、当前标记和 Placements=18；提示原草稿和请求保留，编辑锁定，SUBMIT 可重试。成功后出现 NEW ISSUE，点击后标题清空并回到待选点状态。
5. 应用重新启动后不恢复旧会话方块；本次只恢复待确认请求，没有将旧坐标重放成新的现场标记。

## 真机保存成功但响应丢失

此处用本地临时故障代理和 USB `adb reverse` 精确制造响应丢失。代理将原手机请求转发到真实 Go `:18083`、使用单独文件 `.local/issue-3-acceptance.json`。第一次 POST 已得到后端 201，代理关闭连接不给手机发送响应，不伪造成功数据。

- 标题：`issue3-retry`；请求键 `ea3f9537fa1b4fc7a9a3cc8d3bfc2cdc`。
- 第一次后端实际保存为 ID `ef19c9f274c8b1f55e385d7a7e1f2309`，手机显示 `Received no data in response`，保留草稿、标记及原请求。
- 重启 Go 并读取同一文件，再强制停止及启动手机应用。截图显示 `Unconfirmed report restored`、原标题、锁定编辑；Placements=0、Marker=none，符合新会话约束。
- 直接点击 SUBMIT，代理记录第二次请求的键和请求正文与第一次逐字相同；后端返回 200、同一原始 ID，手机显示 Saved ID 和 open。真实列表仅有一条该 ID 的记录。
- 此流程同时验证手机待确认请求跨进程保留和后端映射持久化。USB 故障代理已停止并移除反向端口，手机最终改为实际局域网地址 `http://192.168.1.8:18083`，连接测试成功，不依赖故障代理继续使用。

## 浏览器与状态持久化

- 起始空列表显示明确空状态。真实创建后刷新，显示标题、描述、优先级、ID、坐标与时间。
- `Wall crack` 记录通过页面依次切到 resolved、open、in_progress，均提示保存成功；停止本次服务后尝试 resolved，页面显示保存失败且选中值仍为 in_progress。
- 手机创建的上述 `ef19...2309` 在网页显示同一 ID 和 `issue3-retry`；通过页面改为 resolved。停止 Go 后刷新出现 Failed to fetch 和重试按钮；重启同一文件后点击重试，同一 ID 及 resolved 状态仍在。
- 使用浏览器网络节流增加真实请求的往返延迟，截图显示“正在加载问题…”及禁用的“加载中…”刷新按钮，随后真实列表恢复；没有 mock 或拦截替换响应。

## Standards

独立子代理先审 AR 方案，再审完整实现：0 项文档规范硬违规；提出的平面显示重复控制与合并平面重复启停问题已修复并复核。最终无待处理发现。

## Spec

独立子代理提出 1 项正负零规范化边界偏差，已通过先失败的 HTTP 用例修复并复核；最终 0 项未解决发现、0 项范围扩张。

审查快照：AR `cd1ba0c57a563ec2b162c9de51106c6469a24e03`，完整实现 `95846913aa476bca0c0a744099b00718507d2655`；之后对工作区的小修做定向复核。快照由临时索引生成，没有移动分支或纳入用户原有修改。

## 证据与限制

本地忽略目录保留 `.local/issue3-current.png`、`issue3-cancel-draft.png`、`issue3-response-lost.png`、`issue3-pending-restored.png`、`issue3-retry-success.png`、`issue3-final-lan.png`、`issue3-proxy-events.jsonl`；浏览器截图在 `output/playwright/issue3-*.png`，含 loading、list-error、status、phone-record-status、final-restart。原始现场图片、APK、运行数据和临时代理不提交。

本次验证不证明厘米级精度、纯白墙通用可靠性、长期漂移、跨会话定位、负载或生产可用性。存储仍是单进程 JSON 快照，不能多进程同时写入。升级会保留旧数组数据，但旧服务无法读取新快照，回退应使用升级前备份。
