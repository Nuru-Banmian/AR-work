# Issue #7 仓库交付清单

核对日期：2026-10-26（Asia/Shanghai）。规格：[#1](https://github.com/Nuru-Banmian/AR-work/issues/1)，任务：[#7](https://github.com/Nuru-Banmian/AR-work/issues/7)。本次仅整理交付文档，不修改业务代码。审查基准为用户确认的 `fe82a946746933c5848e58b2f5582ccaf9915aab`。

## 源码与复现

| 交付内容 | 仓库位置 | 复现入口 |
| --- | --- | --- |
| Go HTTP API、文件持久化及 SSE | `backend/` | README“启动 Go”；`go run .` |
| React / TypeScript 管理页面 | `web/`，含 package-lock.json | README“启动 React”；`npm ci` 后 `npm run dev` |
| Unity Android 工程、字体及许可证 | `mobile/ar-probe/Assets`、`Packages`、`ProjectSettings` | README“构建与运行 Unity”；build.ps1 的 Inspection 开关 |
| APK 检查工具 | `mobile/ar-probe/verify_apk.py` | 检查本地构建产物；APK 不要求入库 |
| 配置、API 和功能限制 | `README.md` | 手机服务地址在界面配置并保留，无需修改源码 |
| AI 使用与修正记录 | `AI_LOG.md` | 按任务记录用途、采纳、修正及实际验证 |
| 历史验收 | `docs/issue-2-acceptance.md` 至 `issue-6-acceptance.md` | 各任务的自动、构建和真机证据分别记录 |
| 两段演示视频 | [录制说明](demo-recording.md) | 两段均待人工录制，暂无可访问文件或链接 |

三端源码与锁文件均已跟踪。Git 跟踪路径检查未发现运行数据、Unity 缓存、node_modules、dist、APK、可执行文件、.env 或签名密钥；这是一项路径检查，不等于完整的历史密钥扫描。现有 `.gitignore` 排除 `.local/`、`backend/data/`、`output/`、Unity 缓存和构建产物。克隆仓库后需自行安装 README 中的工具并下载锁定依赖，不依赖本机 `.local` 中的工具或验收数据。

持久化实际使用 JSON 文件快照；父规格建议 SQLite，但未强制。单实例持有一份数据文件，不能多进程同时写。版本、局域网地址、防火墙和 HTTP 配置以 README 为准；历史验收地址只是当时环境，其他电脑须替换。

## 本次执行的检查

| 检查 | 结果与边界 |
| --- | --- |
| `go version` | go1.27.3 windows/amd64 |
| `go test ./... -count=1 -v` | 20 项顶层测试、9 项非法创建子用例通过；真实 HTTP 和临时持久化文件，约 12 秒 |
| `go vet ./...` | 通过 |
| `node --version` / `npm --version` | v22.22.3 / 10.9.8 |
| `npm ci` | 初次因已有 Vite 占用原生绑定返回 EPERM；停止本仓库三个 Vite 进程后重跑成功，安装 27 个包，随后恢复三个端口 5173/5174/5175 的开发服务 |
| `npm run build` | 严格 TypeScript 与 Vite 8.3.5 构建通过 |
| `npm run lint` | Oxlint 1.89.0 通过 |
| `npm ls --depth=0` | 锁定安装版本完整；React/React DOM 19.3.1、TypeScript 6.0.3 |
| `python mobile/ar-probe/verify_apk.py mobile/ar-probe/Builds/AR-Inspection.apk` | 既有 APK ZIP CRC 与 7 个 ARM64 库 16 KB 对齐通过；本次没有重新构建或安装 Unity 包 |

本地 APK 为 27,819,636 字节，SHA-256 `2b46695e2b22492d8dbfd0279214b82ca793946759e4cbf4785dc7d626784f0c`，与 #6 最终包记录一致。工具输出的 `deviceRuntime: not tested` 表示该脚本不测试设备运行。本次未重新执行浏览器或真机操作，不把已有记录写成本次现场通过。

## 同一问题与现场证据

| 能力 | 已有证据 | 本次状态 |
| --- | --- | --- |
| 手机真实上报、网页看到同一 ID、Go 重启保留 | [#2](issue-2-acceptance.md)：`570f03e593e9fc4300a41074bac0d89f`；[#3](issue-3-acceptance.md)：`ef19c9f274c8b1f55e385d7a7e1f2309` 包含网页状态及重启 | 历史通过；最终包全流程待现场复验 |
| 手机内容编辑，保护网页状态，重启后保留 | [#4](issue-4-acceptance.md)：`4565d42dd3b9e43c049c6b4d0e9eabfa`，含超时/404/500 草稿保留 | 历史通过；本次未复测 |
| 水平/竖直面重新标记、取消/失败、同会话替换、跨重启不恢复旧标记 | [#5](issue-5-acceptance.md)：`07b5a9556b681d15a966d35695095a9b` | 历史通过；本次未复测 |
| 可选 SSE、草稿保护、重连与颜色 | [#6](issue-6-acceptance.md)：`2cfe7a985b43cbab269c00a496d8eb96`；颜色在网络实现切换前验证，最终包补测实时数据和前后台生命周期 | 已实现，有分阶段真机证据；最终包未重扫创建标记验证全部颜色 |

以上不能拼接为“最终包的同一 ID 已完整通过”。最终现场验收应新建一个问题，记录其 ID，贯穿手机上报、网页手动刷新、网页改状态、手机读取、内容编辑、重新标记、Go 进程重启及两端刷新。全过程使用同一个 `ARWORK_DATA` 文件，并核对 ID、createdAt、内容、位置、最新 status；仅 updatedAt 随保存更新。执行步骤见视频脚本。必须实际扫描和选点，不可用 API 创建代替手机上报。

## 尚未完成与已知限制

- 手机、电脑各一段 3–5 分钟真实视频：待录制和填写可访问交付位置。没有视频文件，不以截图、脚本或模拟视频充数。
- 最终包完整同一 ID 现场复验：待完成；录制时可同时补齐，但录制脚本本身不构成通过证据。
- AR 坐标只属于当前会话；重启后不恢复历史方块。无跨设备绝对定位、厘米级精度或白墙可靠性保证。
- 历史有 LOW_MEMORY 退出，默认 30 FPS、关闭深度；仅有短时验证，长期扫描稳定性未证明。
- APK、公网部署不是强制交付项；无登录授权、多人派单或生产多实例能力。

源码与文档整理可提交；#7 保持未完全验收状态，补齐现场复验和两段视频后才能宣称全部交付完成。
