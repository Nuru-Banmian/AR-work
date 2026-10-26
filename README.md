# 园区巡检 AR 原型

Issue #2 最小闭环：Unity 在小米 13 Pro 上扫描水平面、选点并填写问题，通过 Go 保存，React 刷新显示相同问题。术语见 `CONTEXT.md`，规格见 [Issue #1](https://github.com/Nuru-Banmian/AR-work/issues/1)，本次任务见 [Issue #2](https://github.com/Nuru-Banmian/AR-work/issues/2)。

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

`ARWORK_DATA` 相对于服务工作目录，也可使用绝对路径；重启必须指向同一文件。只运行一个服务进程读写一份文件，不手动同时修改文件。存储采用临时文件、同步、替换后再更新内存；写入失败返回错误。损坏数据文件会阻止启动，不会自动清空。

手机与电脑在同一局域网，使用 `ipconfig` 找到手机能访问的电脑网卡 IPv4 地址。此次验收使用 `http://192.168.1.8:8080`；其他电脑应替换为自己的地址。手机不能使用 `localhost` 访问电脑。Windows 防火墙需允许该 Go 进程在所用网络接收入站 TCP 8080。不要关闭防火墙；端口占用时改用其他空闲端口，并同步修改两端地址。此次电脑的 IPv4 localhost:8080 被其他程序占用，验收直接访问局域网地址。

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

1. 安装业务包，允许相机权限；扫描光线充足且有纹理的桌面，等待 `SessionTracking` 和绿色水平面。
2. 在 `Server URL` 输入同一 Go 地址，点击 `TEST CONNECTION`；成功、失败均有提示。地址经校验后保存到手机，重启应用后保留。
3. 点绿色区域放置方块，可继续点选调整位置。界面按钮、输入框和面板不会放置方块。
4. 点击 `EDIT REPORT` 锁定位置，输入必填标题、可选描述，用 `Priority` 按钮切换 low / medium / high。`BACK TO POINT` 保留内容并允许重新选点。
5. 点击 `SUBMIT ISSUE`；保存期间禁用编辑和重复提交。失败保留表单与位置，成功显示服务器 ID、标题、优先级和 open 状态；方块保留在当前会话。点击 `NEW ISSUE` 开始下一条。
6. 网页点击刷新，核对同一 ID；Go 重启后再次刷新，记录仍存在。

表单和诊断使用英文，以减少设备字体差异。位置是当前 AR 会话的米制坐标，不是园区绝对位置；重启应用不会按历史坐标恢复标记。草稿只保存在运行中的应用，失败不会清空，但退出应用后不恢复草稿。

## API

| 方法和路径 | 成功结果 |
| --- | --- |
| `GET /health` | `200 {"data":{"status":"ok"}}` |
| `POST /api/issues` | `201 {"data": issue}` |
| `GET /api/issues` | `200 {"data": issue[]}`，创建时间倒序、ID 稳定排序 |

创建请求（零坐标有效）：

```json
{"title":"水管漏水","description":"入口附近","priority":"high","position":{"x":0,"y":0,"z":0}}
```

服务生成随机 ID、UTC createdAt / updatedAt 和 open 状态。标题去除首尾空白后必填；priority 仅 low / medium / high；position.x/y/z 必须提供且为有限数值。无效参数返回 `400 {"error":{"code":"invalid_argument","message":"可读错误"}}`；保存失败返回 500；未知接口返回 404。客户端不能设置 ID、状态或时间。单请求最大 64 KiB。

## 验证与边界

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

API 测试使用真实 HTTP 服务及临时文件，覆盖创建、参数校验、CORS、并发保存、失败不入列表、重新加载持久化数据。浏览器和真机证据见 `docs/issue-2-acceptance.md`。构建成功和 APK 校验不等于真机功能通过。

后续任务尚未实现：幂等键、竖直面、网页状态修改、手机历史列表/编辑/重新标记、SSE、状态颜色、交付视频。当前请求超时可能已被服务器保存；结果不确定时先刷新网页确认，避免手动重试产生重复问题（幂等处理属于 #3）。不包含登录权限或公网部署。
