# Issue #2 验收记录

日期：2026-10-26（Asia/Shanghai）。本记录只覆盖 #2；#3–#7 的功能不计入完成范围。

## 自动化与构建

- Go：真实 HTTP 公共接口，测试数据在临时目录；创建、零坐标、标题规范化、默认 open / ID / 时间、参数错误、CORS、并发保存、写入失败不出现成功记录、重启加载后的列表。
- 初次 `go test -run TestCreateListAndRestart -v` 因尚无实现而编译失败（undefined newAPI / Issue）；实现后同一测试通过。其余相关行为测试逐项运行通过。
- React：严格 TypeScript 检查、Vite 构建、Oxlint 通过。
- Unity：2022.3.62f3c1、AR Foundation / ARCore 5.2.2；Android ARM64 / IL2CPP 业务构建成功。APK、签名和 16 KB 对齐只作为构建产物检查。
- 代码审查后运行 `go test ./... -count=1 -v`：5 项测试全部通过（含 9 个非法请求子用例）；`go vet ./...`、`npm run build`、`npm run lint` 均通过。
- 最终 APK：18,001,498 字节，SHA-256 `cffecc490e831bc4b051f962a20ec47b643d0bc945fee09ef034f46641a84dec`；apksigner 签名验证、ZIP CRC、7 个 ARM64 原生库的 16 KB 检查通过。

## 真实浏览器与手机

浏览器使用 Playwright CLI 打开真实 React 页面，连接真实 Go 服务；没有拦截接口或使用 mock。手机为 USB 连接的小米 13 Pro（2210132C / nuwa），读取到 Android 16、OS3.0.312.0.WMBCNXM；未把模拟器记录当作真机。

1. 先安装既有探测包，用户反馈“能正常运行”；截图记录 Placements=8。随后安装业务包，截图显示 SessionTracking、Horizontal planes=2、Placements=1。
2. 默认错误地址 .100 及不可达无线地址 .13 均明确报错，标题和描述 `1` 保留。手机 UI 输入及按钮操作后 Placements 仍为 1，没有额外放置。电脑有线地址 `192.168.1.8:8080` 经手机 TCP 检查可达，用户在修改地址后反馈“ok了”。
3. 真机提交成功，Unity 日志 `ARWORK_SAVED_ID=570f03e593e9fc4300a41074bac0d89f`；真实 GET 列表返回同一 ID、title=`1`、description=`1`、priority=`medium`、status=`open`。坐标约为 (-0.2260, -0.3102, 0.5086)，服务生成 UTC 时间 `2026-10-26T08:12:25.3866886Z`。
4. 浏览器将地址改为同一有线地址后刷新，显示上述 ID、标题、优先级、状态及详情。首次空列表也有明确提示。
5. 停止此次 Go 进程后，点击网页刷新：显示“无法加载问题：Failed to fetch”及重试按钮，无白屏；手机测试连接提示 Request timeout。
6. Go 使用原文件 `.local/acceptance-issues.json` 重启后，HTTP 列表仍返回同一记录；网页点击重试恢复显示。
7. 主代理通过 ADB 在真实手机现有水平面上选点（Placements 从 2 到 3），进入表单并输入标题 `tage-draft-2`。停止此次 Go 进程后提交，截图明确显示 `Save failed: Request timeout. Draft kept.`，标题仍在，Placements 仍为 3。恢复同一 Go 数据文件后直接点击提交，保存为 `64c5618b812300ba14141d6d0bbd2ebe`，HTTP 与网页均显示相同 ID 和原标题。此处是真机触摸与输入，不是模拟坐标请求。
8. 最终 APK 覆盖安装成功并重新启动：截图显示保存的 `http://192.168.1.8:8080` 仍在，`SessionTracking`，点击 TEST CONNECTION 显示 `Connection successful.`；新会话未恢复历史方块，符合会话坐标约束。最终构建的变化是连接检查严格解析健康响应、按审查建议重命名无参通知方法；完整上报及停机保留检查在此前业务构建执行。

证据文件保留在本地（不提交含现场画面的手机截图、APK 和运行数据）：`output/playwright/phone-issue.png`、`service-stopped.png`、`restarted.png`；`.local/ar-inspection.png`、`.local/ar-stopped.png`。原始手机截图可能包含现场个人内容，仓库仅记录必要的脱敏读数。

停机草稿及最终更新检查截图：`.local/ar-outage-draft.png`、`.local/ar-retry-saved.png`、`.local/ar-final.png`。

## 代码审查

审查起点由用户确认：`f5666c6`。两个独立只读子代理审查待提交快照：

- Standards：0 项硬性违规，1 项低优先级命名建议（`SetPosition` 忽略参数）。已采用，改为 `MarkPositionSelected()`；重新构建成功。
- Spec：0 项代码偏差；当时提出的地址保留及停机草稿两项验收证据由主代理继续补齐。

本次检查不证明长期定位稳定性、负载性能、生产可用性或后续任务功能。
