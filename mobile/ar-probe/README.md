# AR 真机探测

这是用于确认手机 AR 能力的独立 Unity 测试工程，只包含相机画面、水平面检测、点击放置一个 10 cm 方块和状态显示。没有 Go 后端、React 页面或问题上报功能。

## 版本与构建

- Unity：2022.3.62f3c1（本机已安装的 LTS）。
- AR Foundation / Google ARCore XR Plugin：均固定为 5.2.2。
- Android：ARM64、IL2CPP、OpenGLES3；最低 API 26，目标 API 35。
- ARCore 和 Depth 设为 Optional，用运行时检查判断侧载设备的真实支持情况。
- 使用 Unity 默认调试签名；仅供本地测试，不用于商店发布。

在此目录运行 PowerShell，将路径替换成自己的 Unity 编辑器路径：

```powershell
.\build.ps1 -UnityEditor '<Unity 安装目录>\Editor\Unity.exe'
```

入口 `Assets/Editor/ProbeBuild.cs` 会生成场景、材质和 XR 设置，然后构建 `Builds/AR-Device-Probe.apk`。日志位于 `Logs/build.log`；APK、缓存和日志不提交到 Git。

本机无法访问 Google Maven 下载地址，因此工程的 `settingsTemplate.gradle` 添加阿里云 Google Maven 镜像；Maven Central 和 Google 原地址仍保留。此配置只作用于本工程，不修改系统代理或全局 Gradle 配置。

## 手机测试步骤

1. 把生成的 APK 复制到手机并安装，打开 **AR Device Probe**。
2. 允许相机权限。如提示安装或更新 AR 服务，按系统提示完成。
3. 在光线充足处缓慢移动手机，对准有纹理的桌面或地面。
4. 观察 `Session` 达到 **SessionTracking**，`Horizontal planes` 大于 0，画面出现绿色平面。
5. 在绿色区域点击。应出现橙色方块，`Placements` 增加，`Position (m)` 显示会话坐标。
6. 缓慢走动观察：方块应保持在桌面附近，而不是跟随屏幕移动。
7. 点击顶部状态面板和 `CLEAR MARKER`：`Placements` 不应增加。清除按钮应移除方块。`EXIT APP` 用于退出应用。

通过条件是步骤 4–7 的实际结果。服务安装成功、APK 构建成功、摄像头出现画面都不能代替这项验收。

如果失败，请提供页面上的 `Session`、`Tracking reason`、平面数量和错误提示文字。`Unsupported` 表示当前运行环境未通过支持检查，也可能来自临时检查错误；检查服务和网络后退出应用重开，再重新检查。长期 `SessionInitializing` 或平面数量为 0 时，先确认相机权限、环境光和表面纹理。

应用内使用英文诊断字段，以避免设备缺少中文字体影响读数。坐标以米为单位，原点属于当前 AR 会话；重启应用后不会恢复历史标记。

## 核心代码说明

- `ArProbe.Awake` 创建 ARSession、XR Origin、相机跟踪组件、ARPlaneManager 和 ARRaycastManager。
- `StartAr` 检查相机权限及 AR 服务状态，必要时请求安装，再启用 ARSession。
- `Update` 只处理开始触摸事件，先通过 UI raycast 排除界面操作，再用 `PlaneWithinPolygon` 命中正在跟踪的向上水平面。
- 用 `ARAnchorManager.AttachAnchor` 在命中的平面上创建锚点，方块作为其子物体，中心在锚点上方 5 cm；每次放置复用方块并释放旧锚点。这样地图定位修正时标记可随锚点更新。
- `ProbeBuild.Android` 固定构建配置，保存场景引用和 ARCore loader，构建失败会返回非成功结果。

## 验证边界

本地 Unity Android 构建成功，APK 大小 17,974,686 字节。使用 Android SDK 的 `apksigner verify` 验证签名，`aapt dump badging` 验证包名、入口、ARM64 架构和 API 范围。

运行以下命令可检查 ZIP 完整性、ARM64 ELF 和全部原生库的 16 KB 对齐：

```powershell
python .\verify_apk.py
```

本次 7 个原生库全部通过检查，完整结果保存在 `verification.json`。APK SHA-256：

```text
df9ad35481629a68cfd5dbcf6e5bbf48b093e498ea1633d58d7a88b2681d74e8
```

这些检查仅验证构建产物。真机结果需由手机实际运行确认；当前没有平面检测或业务流程验收结果。

## 官方依据

- [ARCore 5.2 配置与 Required 侧载注意事项](https://docs.unity3d.com/Packages/com.unity.xr.arcore@5.2/manual/project-configuration-arcore.html)
- [ARCore 更新记录：5.2.2 的 16 KB 页支持](https://docs.unity3d.com/Packages/com.unity.xr.arcore@5.2/changelog/CHANGELOG.html)
- [AR Foundation 会话检查](https://developers.google.com/ar/develop/unity-arf/enable-arcore)

16 KB 支持需要最终 APK 内所有原生库及打包对齐满足条件，不能仅依据某个包或编辑器版本推断整包已通过。
