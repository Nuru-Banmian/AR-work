# PC 显示连接地址，手机手动输入（#23）

2026-10-27 用户在真机体验后取消自动寻找。当前范围以 [#23](https://github.com/Nuru-Banmian/AR-work/issues/23) 修订正文和 ADR 0002 为准；不再提供自动发现、广播扫描、候选列表或后台重发现。此文件保留原路径以兼容既有文档链接。

## 使用

在仓库根目录执行 `./start.ps1`。API 首选 TCP 18088，网页首选 TCP 5176，各实际尝试起始端口至 +9（不超过 65535）。绑定成功后保持 socket，Vite 使用 strictPort；端口冲突自动继续，其他启动错误明确失败，绝不把占用端口的服务当成本次实例。每次启动使用独立 `.local/start/<run-id>` 输出目录，退出脚本停止自己的子进程。

PC 自动打开网页，网页通过同源 `/api` 与 `/health` 代理连接本次实际 API。网页“手机连接”区与启动窗口显示实际 **API 端口**和各网卡的完整 HTTP 地址，例如 `http://192.168.1.8:18089`。手机在设置输入完整地址，点“连接并保存”；手机和 PC 需处于同一局域网，多张网卡选择与手机同网段的一条。不要输入 5176 等网页端口。网络/网卡变化后 PC 可刷新连接地址。

手机保存已验证的地址与接收端身份，重启继续连接该地址；PC 地址变化时，按 PC 新显示的地址手动重新连接。同身份更新路由后，原冻结请求继续上传；切换另一身份时，旧队列仍绑定原电脑，不自动转发。没有二维码。失败提示检查 IP、API 端口、同一 Wi-Fi 及电脑防火墙；不再依赖 UDP 18087 或任何发现端口。

## 身份与旧队列

- 数据目录 `receiver.json` 保存随机稳定 ID，名称或端口变化不改变 ID；不能删掉身份文件来“修复”连接。它只识别上传目的地，不代表认证。
- `GET /api/receiver` 返回 `data:{id,name,protocol:1,port,addresses:[{interface,address}]}`，port 是实际绑定的 API 端口，addresses 是当前活跃 IPv4 网卡的完整手机连接地址。PC 刷新获取当前接口信息。
- 每次上传前验证 HTTP 身份，携带 `X-ARWork-Receiver`，不匹配返回 409。上传客户端不自动跟随重定向。手机原地址被另一接收端或普通服务占用时保留失败请求，不传出正文。
- 队列 version 1 兼容可选 receiverId，routes/receivers.json 单独保存身份到地址的路由；请求原 server、body、幂等键不因换址重建。旧条目只有原地址成功握手才绑定身份，无法验证则保留。
- 同身份手动换址保留开放详情、文字/采集草稿和当前会话标记。切换不同身份不继承同 ID 的编辑草稿，旧握手也不能覆盖已更新路由。

## 验证边界

先前自动发现的实现与真机截图属于取消前的历史记录，不作为当前交互验收目标。当前回归包含默认端口占用的真实启动、网页地址/端口展示、手机手动输入/保存/重启、连接失败保留配置、同身份手动换址和队列隔离。所有数据使用隔离演示目录和独立验收包，原 App、18088 服务、已有问题与扫描库保持不变。

自动 HTTP/队列用例使用真实临时持久目录与 HTTP；浏览器及手机结果分别记录。手机队列夹具只证明真实 Android 运行时、Wi-Fi HTTP 与持久请求行为，不冒称当次实物扫描；同一 PC 的多接收端夹具不冒称两台物理电脑。

## 2026-10-27 修订范围验收

- 累计基线 a3bae74，执行前已等待 #28 idle 并确认设备/UDP 交接。默认 API 18088 与网页 5176 均被原服务占用，`start.ps1` 实际选择 18089/5177；原服务未停止。PC 浏览器直接打开 5177，手机连接区显示 **18089** 及 `http://192.168.1.8:18089`，两种刷新均可恢复信息，不把网页端口当作 API。日志 `manual-start.stdout.log`，浏览器 Playwright 快照 10:30:14 / 10:30:37 UTC；新服务不监听 UDP。
- 手机 c80a663e 使用独立 `com.arwork.issue23acceptance` 包，同运行时代码，Development 构建仅用于读取持久化证据；原 App 和 #26 独立包未替换。首次安装被系统取消，经用户确认后重试成功。设置界面没有寻找、候选或重扫按钮；手动键入完整 URL 并点“连接并保存”成功（`manual-connected-success.png`），强制停止重启后保存目标不丢。
- 两个正常生产 Go 接收端进程运行在同一 PC 的隔离 A/B 数据目录，不是两台物理电脑。A 的存储保存注入失败后，手机请求保持失败。手动连接 B:18162 成功，B 仍为零记录，A 队列保留。手动输入不可达 18167 时显示 IP/端口/Wi-Fi/防火墙诊断，PlayerPrefs 仍保存 B:18162（`manual-failure-preserves-config.png`）。
- A 保留原 receiver.json，改名并重启到 18165；手机手动输入新地址后原两条请求同步成功。持久文件原 server、key 与 body SHA-256 都保持不变：新版模型请求 `047319bd802890f656088b0c8ca508dbb7e35a5b668b3742eafdfb0132024ac1`，旧版无 receiverId 请求 `3645564e9cee1615c9e412fb69d076e1b8e9f2947a4c852c5897c09001038e3b`。旧请求只经原地址握手后绑定 A；未验证的旧地址 18167 条目仍 pending、无绑定。`manual-queue-results.json` 与对应队列快照保存证明。
- 新增同目标冻结 guard 请求；用受控 HTTP 服务暂占 18165，分别返回 B 身份及普通 HTML，手机均保留失败请求。服务记录了两类 GET /api/receiver，但 **没有 POST**，正文未误投。恢复原 A 后相同 key/body 同步；`manual-guard-wrong.json` / `manual-guard-plain.json` / `manual-guard-restored.json`、`guard-http.jsonl`。手机再次重启后 A 共 3 条，B 仍 0 条。
- 手机队列输入为可辨颜色三角形、合成图片和旧协议的受控持久文件，只证明真实 Android 运行时、同 Wi-Fi HTTP、持久队列和目的地行为，不冒称本轮真实物品扫描。手机 Wi-Fi 地址为 192.168.1.7，PC LAN 为 192.168.1.8；没有 adb reverse 上传路径。全程网络设置未更改，结束读回 airplane=0 / wifi=1 / mobile_data=0，与开始一致。
- Go 全套测试/vet 通过（13.242s）；前端 build/typecheck/lint 通过；Unity 导航 62 项、ReceiverQueueChecks 通过。/code-review Standards/Spec 均复审无剩余发现，同址刷新卡片问题已修复。日志与私有截图均在忽略目录 `.local/issue23-device-final/`。

- 正式 APK 构建成功，28066192 字节，SHA-256 `9d5397c0e7c18b62bfd79e6cf862efefaaf48c45d001edbf2afb4857a1f26868`；ZIP 完整性、ARM64、7 个原生库 16KiB ELF 对齐通过，metadata 不含 ReceiverDiscovery、含 ReceiverConnection。真机独立 Development 包 SHA-256 `f48753e61c2aeaa94ca0998ba8a7929ec0a666e877883908aec609c34bee1ae3`（33,555,984 字节），同运行时代码。正式 APK 未覆盖安装到原 App；留给后续统一部署。
