# AR 检测改进方案

日期：2026-10-26。范围：小米 13 Pro、Unity 2022.3.62f3c1、AR Foundation / ARCore XR Plugin 5.2.2。本文是方案与验收建议，不代表改进后精度已在真机通过。

## 推荐

保留 ARCore 的相机跟踪和锚点，增加 **Depth 命中优先、已检测平面回退**，并提供 `Plane` / `Depth + Plane` 开关做同机对照。先验证实际选点改善，再决定默认模式。Google 官方设备表列出 Xiaomi 13 Pro 支持 Depth API；运行时仍须检查当前会话的支持状态和实际数据。[设备支持](https://developers.google.com/ar/devices/)

现有代码在本轮研究开始时只查询 `PlaneWithinPolygon`，相机未添加 `AROcclusionManager`。因此之前的体验不能用于评价 Depth 模式。官方要求手动启用深度；仅将构建配置中的 Depth 设置成 Optional 并不会自动启用它。[Unity 启用深度](https://developers.google.com/ar/develop/unity-arf/depth/developer-guide)

## “不准确”需要分开看

- **识别不到墙面**：ARCore 用特征点估计平面，纯白墙缺乏纹理，可能检测不到。Depth 命中可覆盖部分低纹理和非平面区域，不必等一个完整墙面平面生成。[平面与跟踪原理](https://developers.google.com/ar/develop/fundamentals)、[深度命中](https://developers.google.com/ar/develop/hit-test)
- **点下去的位置离真实表面有偏差**：深度用于估计点击射线的距离和表面朝向，是本次对照直接验证的能力。[命中类型](https://developers.google.com/ar/develop/hit-test)
- **放好后走动，标记漂移**：相机位姿、环境理解及锚点会随会话更新。增加深度命中没有替换这些跟踪环节，不能据此承诺消除漂移。[锚点与跟踪对象](https://developers.google.com/ar/develop/fundamentals)

Depth API 会综合运动产生的多帧图像，并在存在支持的硬件传感器时融合其数据；支持 Depth API 不等于这台手机具备 ToF 测距。官方建议距离约 0.5–5 米、适当移动设备，并明确少纹理白墙仍可能产生不精确深度。因此这是一项可验证的改进方向，不是白墙精度保证。[深度原理与限制](https://developers.google.com/ar/develop/depth)

## 方案比较

| 方案 | 对本项目的价值 | 成本和边界 | 当前选择 |
| --- | --- | --- | --- |
| ARCore Depth + 平面回退 | 复用现有跟踪、UI 和锚点；低纹理区域可获得更多表面命中 | 必须启用并确认真实深度数据；无命中时明确提示扫描 | 优先做真机对照 |
| 已知图像标记，如 ARCore Augmented Images / Vuforia Image Targets | 在指定位置放印刷图像，提供已知视觉参考 | 需要预置目标和现场标识，改变“任意现场选点”的使用条件；换 SDK 本身不证明更准确 | 只有允许布置标识时再评估 |
| 单目 AI 深度，如 Depth Anything V2 | 可对单张图估计场景深度 | 官方通用模型输出相对深度，另有米制微调模型；还需相机标定、位姿融合、时序稳定及移动端性能验证 | 不作为这次直接替换方案 |

图像目标通过已知目标数据库匹配自然特征，仍受反光和照明影响：[Vuforia Image Targets](https://developer.vuforia.com/library/vuforia-engine/images-and-objects/image-targets/image-targets/)。ARCore 已提供图像跟踪，采用标识不必先换引擎：[ARCore Augmented Images](https://developers.google.com/ar/develop/augmented-images)。AI 模型能力与模型类型参照作者仓库；表中接入成本是基于本项目需求的工程判断：[Depth Anything V2](https://github.com/DepthAnything/Depth-Anything-V2)。本次未安装或实测这两个替代方案，不对其精度作排名。

## 与当前插件版本的接法

已读取本地 5.2.2 包源码和包内文档，以下不是套用新版 API：

1. 在 AR Camera 增加 `AROcclusionManager`，设置 `requestedEnvironmentDepthMode`，读取 `currentEnvironmentDepthMode` 区分“请求启用”和“实际启用”。
2. 会话启动后持续检查 `descriptor.environmentDepthImageSupported`。刚启动尚未支持或尚未出帧时，不永久关闭深度。实际深度纹理/图像可用后才显示就绪。
3. 通过 `ARRaycastManager.Raycast(..., TrackableType.Depth)` 获取深度命中；缺失时再查询 `PlaneWithinPolygon`。界面记录本次采用的命中类型，避免把平面回退误报为深度成功。
4. 深度命中不保证关联 `ARPlane`，不能假设其 trackable ID 可传给 `GetPlane` 并继续 `AttachAnchor`。应按命中位姿创建锚点；平面路径仍可附着平面。
5. 两种模式均检查 AR 跟踪状态并排除 UI 点击。保留原待提交标记，只有新选点和锚点确认后再替换；不使用臆测距离的 Instant Placement 充当精确落点。

版本依据：`mobile/ar-probe/Library/PackageCache/com.unity.xr.arcore@5.2.2/Runtime/ARCoreRaycastSubsystem.cs` 注册支持 `TrackableType.Depth`；`ARCoreOcclusionSubsystem.cs` 提供深度能力和模式；同包 `Documentation~/features/occlusion.md` 说明须在会话启动后判断支持。AR Foundation 同版本 `Runtime/ARFoundation/AROcclusionManager.cs` 提供上述属性。缓存不纳入 Git，可由固定的 `Packages/packages-lock.json` 恢复。官方接入说明见 [Unity 深度指南](https://developers.google.com/ar/develop/unity-arf/depth/developer-guide)。

## 真机对照与通过条件

用相同手机、场景、光照和距离，分别测 `Plane` 与 `Depth + Plane`；每种至少覆盖有纹理墙、纯白墙、桌面/地面。每组重新开始会话，保持扫描路径接近，记录：

- 首次可命中的等待时间、有效点击数/尝试数，以及每次实际命中来源、距离、跟踪状态。
- 选点是否贴合指定真实位置。可用细小十字与尺做现场参考，十字周边的纹理会改变识别条件，应另保留未贴参考物的纯白墙测试。
- 标记后绕行、离开再返回原视角时的偏移；记录视频并用现场尺估计可观察误差。不要只凭截图或绿面覆盖面积宣称厘米级精度。
- 深度未就绪时回退提示、跟踪丢失时禁止新建标记、表单锁定、输入框/按钮防误触。

只有实际使用 Depth 的样本表现更好，才能写“这组场景选点改善”。如果纯白墙仍不可靠，应明确限制或改用现场图像参考；不悄悄扩大平面边界、不用固定距离假点伪造成功，也不将本次结果外推为跨会话定位或长期精度保证。

## 本轮落地与已取得的证据

- 业务包已实现 `DEPTH AUTO` / `PLANE ONLY` 切换；自动模式先查询真实 Depth 命中，缺失时保留平面回退。开关用于对照，不修改已放置的锚点。
- 深度候选距离限定为 0.5–5 米；该范围是接入时的保守选择，不是精度保证。独立深度锚点须取得有效 ID 且处于 Tracking 才替换旧标记。无有效命中时保留旧标记。
- 平面覆盖透明度降低；仅显示正在跟踪且未被合并的平面，会话丢失跟踪时隐藏。它改善观察条件，不提高底层测距精度。
- USB 真机为小米 13 Pro；读取到 Google Play Services for AR `1.57.262640093`。安装本轮深度对照包后，截图显示 `SessionTracking`、`Depth: ready`，确认当前会话已产生深度纹理。
- 真机截图 `.local/ar-depth-hit.png` 显示 `Horizontal: 7`、`Vertical: 1`、`Placements: 18`、`Hit: Depth`，当前命中距离约 0.97 米。Unity 日志记录多次 `ARWORK_PLACED source=Depth`，样本距离约 0.87–0.99 米；也有 `Floor plane` 回退记录。用户同时在实际操作，因此次数不作为固定自动化用例。
- 这些证据仅确认深度路径、锚点创建和回退确实运行；尚无带尺测量、固定位置的重复对照或漂移视频，不能报告误差数值或宣称整体更准确。原始现场截图仅保留在忽略目录，不提交。
- 用户切换同机模式对照后明确反馈“深度模式明显更贴合”，据此保留业务包默认 `DEPTH AUTO`；该反馈是定性试用结果，没有量化误差或可控重复试验。
- Standards 与 Spec 两个独立子代理已审查。Spec 未发现 AR 代码偏差；Standards 提出的平面可见性重复控制及合并后反复启用问题均已修复并复查。最终修正版已重新构建、安装并确认 Depth: ready 和竖直面；完整包版本与真机证据见 [#3 验收](issue-3-acceptance.md)。
