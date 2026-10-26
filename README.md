# 园区巡检 AR 原型

Unity 在小米 13 Pro 上选点并填写问题，通过 Go 保存，React 查看详情及修改状态。#2 提供最小闭环，#3 增加可靠重试、竖直面、处理状态和深度选点，#4 增加手机历史查询与内容编辑。术语见 `CONTEXT.md`，规格见 [Issue #1](https://github.com/Nuru-Banmian/AR-work/issues/1)，本次任务见 [Issue #4](https://github.com/Nuru-Banmian/AR-work/issues/4)。

## 实际版本

- Unity 2022.3.62f3c1 LTS；AR Foundation / ARCore XR Plugin 5.2.2。
- Go 1.27.3（标准库，无第三方后端依赖）。
- Node.js 22.22.3；npm 10.9.8；React / React DOM 19.3.1；TypeScript 6.0.3；Vite 8.3.5。前端完整版本由 `web/package-lock.json` 固定，使用 `npm ci` 复现。
- 真机：小米 13 Pro，Android 16，HyperOS OS3.0.312.0.WMBCNXM。相机权限及 Google Play Services for AR 必须可用。

## 启动 Go

安装 Go 后，在新 PowerShell 窗口执行：

```powershell
cd backend
$env:ARWORK_ADDR = ':8080'
$env:ARWORK_DATA = 'data/issues.json'
$env:ARWORK_ORIGINS = 'http://localhost:5173,http://127.0.0.1:5173'
go run .
```

`ARWORK_DATA` 相对于服务工作目录，也可使用绝对路径；重启必须指向同一文件。只运行一个服务进程读写一份文件，不手动同时修改文件。存储采用临时文件、同步、替换后再更新内存；问题与幂等键结果映射保存在同一个快照，写入失败返回错误。兼容 #2 的旧数组文件，首次成功修改后保存为包含 issues / requests 的快照；降级到旧服务前需恢复备份。损坏数据文件会阻止启动，不会自动清空。

手机与电脑在同一局域网，使用 `ipconfig` 找到手机能访问的电脑网卡 IPv4 地址。启动示例使用 `http://192.168.1.8:8080`；#4 隔离验收使用 `http://192.168.1.8:18085`，详见验收记录。其他电脑应替换为自己的地址。手机不能使用 `localhost` 访问电脑。Windows 防火墙需允许该 Go 进程在所用网络接收入站 TCP 8080。不要关闭防火墙；端口占用时改用其他空闲端口，并同步修改两端地址。此次电脑的 IPv4 localhost:8080 被其他程序占用，验收直接访问局域网地址。

## 启动 React

```powershell
cd web
npm ci
npm run dev -- --host 127.0.0.1
```

打开 `http://127.0.0.1:5173`，填入同一 Go 地址，点击“连接并刷新”。网页保存该地址，提供加载、空列表、错误、重试以及 ID、标题、描述、优先级、状态、坐标和时间。若改用其他网页源或端口，将其完整 origin 加入 `ARWORK_ORIGINS` 后重启 Go。浏览器刷新页面和按钮均从后端读取；当前未实现自动刷新。

## 构建与运行 Unity

保留 `mobile/ar-probe` 工程和独立探测包入口。业务包复用同一 AR 跟踪代码，通过不同构建入口启用问题表单：

```powershell
cd mobile/ar-probe
.\build.ps1 -UnityEditor '<Unity安装目录>\Editor\Unity.exe' -Inspection
```

生成 `Builds/AR-Inspection.apk`（调试签名，包名 `com.arwork.inspection`）；不带 `-Inspection` 则生成独立 `AR-Device-Probe.apk`。构建入口生成对应场景及材质。业务包开启 Android 局域网明文 HTTP，供可信局域网调试。Unity 2022.3 的 Android Build Support、SDK、NDK、OpenJDK 必须安装；构建目标 ARM64 / IL2CPP / OpenGLES3，最低 API 26、目标 API 35。

1. 安装业务包，允许相机权限；扫描光线充足且有纹理的桌面或墙面，等待 `SessionTracking`。默认 `DEPTH AUTO`，显示 `Depth: ready` 表示已有深度纹理；点击底部开关可切为 `PLANE ONLY` 对照。深度优先选点，无可用深度命中时回退水平/竖直平面；`Hit` 显示实际来源。深度候选限定 0.5–5 米，纯白墙仍可能不准确。详见 [AR 检测方案](docs/ar-detection-options.md)。
2. 在 `Server URL` 输入同一 Go 地址，点击 `TEST CONNECTION`；成功、失败均有提示。地址经校验后保存到手机，重启应用后保留。
3. 点击现场表面放置方块，可继续点选调整位置。深度命中不要求绿色平面已生成；平面模式需点绿色区域。界面按钮、输入框和面板不会放置方块。没有有效命中时保留旧方块，不使用固定距离模拟落点。
4. 点击 `EDIT REPORT` 锁定位置，输入必填标题、可选描述，用 `Priority` 按钮切换 low / medium / high。`BACK TO POINT` 保留内容并允许重新选点。
5. 点击 `SUBMIT ISSUE`；保存期间禁用编辑和重复提交。首次提交固定服务器、请求键和内容；失败后保留草稿并锁定编辑，再点提交用原请求重试。未确认的请求保存在设备上，应用重启后可继续重放，但不恢复旧会话方块。成功显示服务器 ID、标题、优先级和 open 状态；方块保留在当前会话。点击 `NEW ISSUE` 才生成下一次上报的新请求键。
6. 网页点击刷新，核对同一 ID；使用“处理状态”自由切换待处理、处理中、已解决，保存失败仍显示原状态。Go 重启后再次刷新，记录及状态仍存在。
7. 手机点击 `ALL ISSUES` 获取全部问题，点击一行重新查询详情；两页的 `REFRESH` 均查询后端。详情显示 ID、当前状态、时间和历史坐标。重启应用后仍可用保存的地址查询，不按历史坐标恢复 AR 标记。
8. 详情中编辑标题、描述和优先级，点击 `SAVE CONTENT`。保存期间禁用重复操作，成功显示最新状态；网页刷新同一 ID 可见修改。手机仅提交内容字段，不覆盖网页修改的处理状态或位置；相同内容字段采用最后一次成功保存为准。
9. 校验、连接、404 或 500 失败提示原因并保留草稿，可修正或重试；详情刷新更新状态并保留改动过的内容。`BACK TO LIST (KEEP DRAFT)` 和 `BACK TO AR` 保留当前应用内草稿，再进入同一服务器和 ID 可继续编辑；未修改内容时刷新加载最新内容。历史内容草稿不跨应用进程重启保存，退出前应成功保存。新建待确认请求仍按原规则跨重启保留。

表单和诊断使用英文，以减少设备字体差异。位置是当前 AR 会话的米制坐标，不是园区绝对位置；重启应用不会按历史坐标恢复标记。未提交的草稿只在运行中的应用保留；已经发起但尚未确认的请求会跨应用重启保留，直到收到成功结果。若出现幂等冲突或待确认请求损坏，保留应用数据并检查后端，不能清空后直接重建以免重复上报。

## API

| 方法和路径 | 成功结果 |
| --- | --- |
| `GET /health` | `200 {"data":{"status":"ok"}}` |
| `POST /api/issues` | `201 {"data": issue}` |
| `GET /api/issues` | `200 {"data": issue[]}`，创建时间倒序、ID 稳定排序 |
| `GET /api/issues/{id}` | `200 {"data": issue}`，未知 ID 返回 404 |
| `PATCH /api/issues/{id}/content` | `200 {"data": issue}`；必传 title、description、priority，只修改内容及 updatedAt |
| `PATCH /api/issues/{id}/status` | `200 {"data": issue}`，如 `{"status":"resolved"}`；status 仅 open / in_progress / resolved |

创建请求（零坐标有效）：

```json
{"title":"水管漏水","description":"入口附近","priority":"high","position":{"x":0,"y":0,"z":0}}
```

服务生成随机 ID、UTC createdAt / updatedAt 和 open 状态。标题去除首尾空白后必填；priority 仅 low / medium / high；position.x/y/z 必须提供且为有限数值。无效参数返回 `400 {"error":{"code":"invalid_argument","message":"可读错误"}}`；保存失败返回 500；未知接口返回 404。客户端不能设置 ID、状态或时间。单请求最大 64 KiB。

新建请求携带 `Idempotency-Key`（非空白，最多 200 字节）时，相同键与规范化内容重放返回 200 和首次创建的原始回执，不再创建问题；同键不同内容返回 409。规范化包含标题去除首尾空白、JSON 属性顺序及数值编码归一。问题状态后来改变不影响原始创建回执；当前值通过查询获取。键和结果持久化，并发请求也只创建一条。为兼容 #2，无键调用仍可创建，但没有去重保障；业务手机始终提供键。浏览器 CORS 允许 PATCH 和 Idempotency-Key。

## 验证与边界

内容修改示例：`{"title":"复查漏水","description":"已补充现场情况","priority":"low"}`。三个字段均必传且不接受 null，描述允许空字符串；标题和优先级沿用创建校验。不接受 status、position、ID 或时间。无效内容返回 400、未知 ID 返回 404、持久化失败返回 500；失败不改变原记录。内容和状态修改在同一锁下读取当前记录并原子保存各自字段，原始创建幂等回执保留。

```powershell
cd backend
go test ./... -count=1
go vet ./...
cd ../web
npm run build
npm run lint
cd ../mobile/ar-probe
python verify_apk.py Builds/AR-Inspection.apk
```

API 测试使用真实 HTTP 服务及临时文件，覆盖创建、参数校验、CORS、幂等并发/规范化/冲突、保存后响应丢失、重启重放、状态切换、内容独立修改及保存失败不修改内存。浏览器和真机证据见 [#2 验收](docs/issue-2-acceptance.md)、[#3 验收](docs/issue-3-acceptance.md)、[#4 验收](docs/issue-4-acceptance.md)。构建成功和 APK 校验不等于真机功能通过。

后续任务尚未实现：手机重新标记、SSE、状态颜色、交付视频。不包含登录权限或公网部署。Depth 的接入和用户同机反馈不构成厘米级精度、白墙通用可靠性或长期跟踪保证；构建和真机验证分别记录。
