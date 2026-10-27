# 园区巡检 AR 原型

Unity 真机采集并上报问题，Go 持久保存，React 查看问题并修改状态。源码： [Unity](mobile/ar-probe)、[React](web)、[Go](backend)。

## 版本

Unity **2022.3.62f3c1**，AR Foundation / ARCore XR Plugin **5.2.2**；Go 验证版本 **1.27.3**（go.mod 最低语言版本 1.23）；Node.js **22.22.3**、npm **10.9.8**。React **19.3.1**、TypeScript **6.0.3**，Vite 等准确依赖以 [package-lock.json](web/package-lock.json) 为准，使用 `npm ci`。真机验证设备：小米 13 Pro / Android 16。

## 启动 Go 和 React

Windows 安装 Go、Node.js 后，在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\start.ps1
```

脚本首次安装前端依赖，构建并启动 Go、启动 React，打开网页。API 从 18088、网页从 5176 起各尝试 10 个端口；以终端和网页显示的实际地址为准。网页通过同源代理访问 Go。Ctrl+C 停止本次服务。

默认数据为 `backend/data/issues.json`；重启保留同一数据及同目录接收端身份文件。演示可指定独立数据，不能覆盖现有数据：

```powershell
.\start.ps1 -DataFile backend/data/demo-issues.json
```

分别启动时，在两个终端执行：

```powershell
# 终端 1，仓库根目录
cd backend
$env:ARWORK_ADDR = ':18088'
$env:ARWORK_DATA = 'data/issues.json'
$env:ARWORK_ORIGINS = 'http://127.0.0.1:5176'
go run .
```

```powershell
# 终端 2，仓库根目录
cd web
npm ci
$env:ARWORK_API_URL = 'http://127.0.0.1:18088'
npm run dev -- --host 127.0.0.1 --port 5176 --strictPort
```

打开 `http://127.0.0.1:5176`。改 API 端口时同步修改代理地址；改网页端口时同步修改 Go 的允许源。单份数据只运行一个 Go 写入进程。

## 启动 Unity 真机应用

用上述 Unity 版本打开 `mobile/ar-probe`，安装 Android Build Support（SDK、NDK、OpenJDK）。构建业务包：

```powershell
cd mobile/ar-probe
.\build.ps1 -UnityEditor '<Unity安装目录>\Editor\Unity.exe' -Inspection
```

安装生成的 `Builds/AR-Inspection.apk`（ARM64 / IL2CPP，最低 Android API 26），允许相机权限，确保 Google Play Services for AR 可用。APK 无须随作业提交。

手机和电脑连接同一局域网，在手机“设置”输入电脑完整 Go 地址，例如 `http://192.168.1.8:18088`，点击“连接并保存”。使用网页“手机连接”显示的实际 LAN 地址和 API 端口，不能填手机的 localhost 或网页端口。多网卡选择与手机同网段的地址，Windows 防火墙放行实际 API 端口；不需要公网部署或自动发现。

首页新增问题 → 扫描真实表面 → 暂停并选点放置圆锥 → 拍摄并确认现场照片 → 填写并提交。React 手动刷新、打开同一编号并修改状态；使用同一数据路径重启 Go 后再刷新核对。详细操作和验收边界见 [集成交付说明](docs/issue-28-delivery.md)。

## 完成情况与已知问题

- 已实现：三端源码、局域网上报、持久化、状态管理、照片及扫描模型双端查看、离线重试、重新标记、SSE、管理页筛选和删除。历史测试及真机证据见 [集成交付说明](docs/issue-28-delivery.md)，不代表本次重新执行全部验收。
- **未完成交付：手机、电脑各一段 3～5 分钟真实演示视频。** 当前没有已登记的可访问视频、实际时长或五项流程时间点，不能判定材料齐全。录制脚本及登记见 [视频交付表](docs/demo-recording.md)。两段应核对同一问题 ID，并展示真机平面识别/放置、手机提交、React 刷新、修改状态、Go 重启后数据保留。
- 已知限制：AR 坐标不能跨会话恢复；未扫描区域允许缺口，不保证模型精度；历史发生系统低内存退出，尚无长期稳定性结论。无登录权限；接收端身份防误投但不提供认证。网页整页刷新不保留未保存草稿。

[AI_LOG.md](AI_LOG.md) 记录 AI 用途、采纳调整与验证；[原交付清单](docs/issue-7-delivery.md) 保留早期证据，当前行为以上方集成说明为准。视频待完成状态优先于历史 Issue 关闭状态。
