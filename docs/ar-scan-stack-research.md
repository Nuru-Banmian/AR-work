# 设备局部与物品表面扫描：技术栈调研

日期：2026-10-27。目标平台：本仓库的 Unity 2022.3、AR Foundation / ARCore XR Plugin 5.2.2、Android 小米 13 Pro。目标是取得可旋转查看、可选点标记的真实三维表面，而不是只在画面上叠加绿色平面或展示好看的新视角图像。本文是基于官方文档、作者论文和项目源码的工程调研；没有把列出的替代产品全部安装或实测，也没有宣称覆盖所有 AR 产品。

## 结论

用户已明确要求 **纯手机、离线、实时**，因此交付主方案不依赖服务器、云 API 或 PC 后处理。应保留 ARCore 的位姿跟踪和 Unity 的交互层，替换“逐帧生成三角形后追加”的重建核心，改为 **手机本地、有界、多帧、置信度加权的 TSDF 体积融合，再提取受观测支持的网格**。使用 Full Depth 提供覆盖，用 Raw Depth/置信度做可信证据与一致性筛选；RGB 关键帧用于本地顶点颜色及问题重放。TSDF 的依据是对同一空间体素积累带权距离，而非不断增加互不一致的面片；Open3D 的官方重建文档明确说明其对深度噪声的融合作用。[KinectFusion 论文](https://www.microsoft.com/en-us/research/publication/kinectfusion-real-time-dense-surface-mapping-tracking/)、[Open3D TSDF](https://www.open3d.org/docs/latest/tutorial/t_reconstruction_system/integration.html)

这是针对当前设备、代码和可验证性的选择，不是“TSDF 必然扫描得好”的保证。手机的输入深度、反光/透明表面、物体运动及相机位姿仍决定上限。仅把体素缩小不会凭空增加手柄按钮、细缝等真实深度细节。后文的云和摄影测量路线用于技术比较；用户没有授权把它们作为当前主流程。[ARCore 深度原理与距离范围](https://developers.google.com/ar/develop/depth)、[COLMAP 重建流程](https://colmap.github.io/tutorial)

本轮父任务通过真机日志读到：GPU 视频纹理 1920×1080、CPU 图像配置 640×480 / 30 Hz、自动对焦状态 True、配置字符串含 `no depth sensor`。这些是当前手机当前会话的观测，来自忽略目录中的 `.local/surface-device/final-run.log` 及本轮设备记录；不是对所有小米 13 Pro 固件、相机配置或物理硬件的普遍断言，也不能把 GPU 纹理尺寸当作深度分辨率。

## 1. 采集、跟踪和输入深度

| 技术 | 真实能力 | 本项目适用性与限制 |
| --- | --- | --- |
| AR Foundation + ARCore | 提供会话、相机位姿、内参、CPU 图像、深度和锚点；命中检测用于找到点击位置 | 继续使用。平面检测或 Depth hit 本身不会产生完整物品模型。[ARCore hit-test](https://developers.google.com/ar/develop/unity-arf/hit-test/developer-guide) |
| ARCore Raw Depth + confidence | 一部分像素的深度更可靠；其余像素可能没有值，有匹配的逐像素置信度 | 作为可信几何输入和密集深度的交叉检查，不把无效像素当作 0 米表面。不能直接把每帧稀疏相邻像素连接后宣称闭合重建。[Raw Depth](https://developers.google.com/ar/develop/java/depth/raw-depth) |
| ARCore Full / smoothed Depth | 为全图提供估计，经过平滑和插值，覆盖更密，但可能降低局部精度 | 可用于更连贯的预览和低权重融合；物体边缘、孔洞及深度跳变要拒绝跨边连接。不能把插值出来的值当成独立高置信度观测。[Raw 与 Full 的区别](https://developers.google.com/ar/develop/java/depth/raw-depth) |
| 设备 ToF / LiDAR / RGB-D | 在支持的设备上补充硬件距离信息 | ARCore 支持深度不等于手机装有 ToF。ARCore 可用运动估深，并在硬件可用时融合传感器。官方表示约 0.5–5 米通常最准确；低纹理仍可能不准确。不能从支持列表推导毫米级物品扫描能力。[Depth](https://developers.google.com/ar/develop/depth) |
| RGB 关键帧 | 保存细节、颜色和纹理证据，可供 SfM/MVS 重建及纹理映射 | 应采集清晰、稳定曝光、有重叠视角的图像。CPU 图像分辨率、GPU 预览纹理和系统相机照片不是同一指标。配置需从当前会话读取，不能只看手机相机标称像素。[ARCore 相机配置](https://developers.google.com/ar/develop/unity-arf/camera-configs) |

原始图像与显示方向必须分开处理。Google Depth Lab 的 `ScreenRotation` 和 `LocalToWorldMatrix` 对屏幕方向做了旋转补偿；直接把横向原生深度坐标当作竖屏相机坐标，会导致错位。[官方示例源码](https://github.com/googlesamples/arcore-depth-lab/blob/master/Assets/ARRealismDemos/Common/Scripts/DepthSource.cs)

Raw Depth 还包含原始估计和按当前位姿重投影的帧，不能只凭“收到纹理”计算新增信息量。要使用时间戳识别重复信息，并保存采用的相机位姿；也不能把“原始估计时间戳较老”一律当作未按当前位姿配准，导致重投影帧全部被拒绝。Full Depth、RGB 与位姿的实际时序必须独立记录和检查。[ARCore Frame 文档](https://developers.google.com/ar/reference/java/com/google/ar/core/Frame)

## 2. 多帧融合与网格生成

| 路线 | 算法与输出 | 工程判断 |
| --- | --- | --- |
| KinectFusion / TSDF | 将距离观测融入一个统一的隐式表面，原论文还以 ICP 进行跟踪 | 核心思想适合本项目，但论文使用 Kinect 深度传感器和 GPU；不能把其精度或帧率照搬到手机运动估深输入。[论文](https://www.microsoft.com/en-us/research/publication/kinectfusion-real-time-dense-surface-mapping-tracking/) |
| Open3D | 提供 TSDF、RGB-D 融合和 Marching Cubes 网格提取，亦可离线处理和作为数值参照 | 适合本地重放、服务端及参考实现。手机 Unity 端直接嵌入仍需 Android 交叉编译/原生桥接及性能验证；其桌面 CUDA 性能不是手机性能证据。[融合教程](https://www.open3d.org/docs/latest/tutorial/t_reconstruction_system/integration.html)、[网格提取](https://open3d.org/html/tutorial/pipelines/rgbd_integration.html)。项目采用 MIT 许可。[LICENSE](https://github.com/isl-org/Open3D/blob/main/LICENSE) |
| Voxblox | CPU 为主的 TSDF / ESDF 体积库，支持稀疏结构、序列化和多种权重方式 | 可参考稀疏存储与融合实现，但 ROS 集成和规划需求不是本项目所需，不应把整个 ROS 栈搬进手机。[源码与 BSD-3-Clause 许可](https://github.com/ethz-asl/voxblox) |
| InfiniTAM | 深度体积融合、跟踪、可扩展场景和相关移动端研究 | 可作为算法参考，不能直接认定可无条件用于商业软件。官方许可证限非商业用途，商业使用另行谈授权。[项目](https://github.com/victorprad/InfiniTAM)、[官方许可证](https://robots.ox.ac.uk/~victor/infinitam/license.html) |
| 手写有界 TSDF + Marching Cubes / tetrahedra | 对单件物品限定内存、计算量与坐标范围，不依赖完整 SLAM 框架 | 是当前 Unity 工程可逐步验证的实现路径。必须有合成平面、球面、重复噪声和实际深度重放测试；仅实现等值面提取不等于质量达标。 |

如果借用第三方代码或查表数据，需要保留各自版权与许可，不能因为算法论文公开就假定任意具体实现可复制。采用 TSDF 不要求采用某个受限实现；也不应为省接入成本继续用无法融合噪声的面片追加器。

## 3. 视觉细节与离线重建

| 技术栈 | 输出与价值 | 平台、成本和边界 |
| --- | --- | --- |
| COLMAP SfM + MVS | 从重叠照片估计相机与稀疏结构，再做密集深度、融合和网格；当前官方教程还列出纹理化流程 | 更适合需要 RGB 细节的物品重建。需要拍摄质量、纹理和多视角覆盖；运算与部署应在 PC / 服务端单独验收，不承诺手机实时。COLMAP 是新 BSD 许可，依赖仍需分别检查。[教程](https://colmap.github.io/tutorial)、[许可](https://github.com/colmap/colmap/blob/main/doc/license.rst) |
| OpenMVS | MVS、表面重建、细化和纹理流程 | 可与 SfM 输出衔接；本轮核实的仓库标示 AGPL-3.0，若纳入发行或服务需按选定版本检查许可。没有把许可证名简化成“免费商用随便嵌入”。[官方仓库](https://github.com/cdcseacave/openMVS) |
| AliceVision / Meshroom | 可视化、节点式摄影测量管线，支持相机恢复、深度、网格和纹理工作流 | 适合作为 PC 上的对照与离线处理候选，非 Unity 手机内置扫描控件。具体版本的深度处理后端、GPU 与驱动要求必须实机验证，不能笼统宣称所有步骤可在当前手机运行。[官方站点](https://alicevision.org/)、[源码](https://github.com/alicevision/meshroom)、[MPL-2.0 许可说明](https://github.com/alicevision/meshroom/blob/develop/COPYING.md) |
| Apple Object Capture | Apple 的引导采集、照片与 LiDAR 辅助物品重建 | 是较完整的采集体验参照；属于 Apple 平台，不能直接在小米 Android 上调用。设备支持须运行时检查，不能仅由系统版本判断。[Apple WWDC23](https://developer.apple.com/videos/play/wwdc2023/10191/)、[isSupported](https://developer.apple.com/documentation/realitykit/objectcapturesession/issupported) |
| ARKit scene reconstruction | 在支持设备上取得环境多边形网格，LiDAR 参与环境估计 | 能力偏环境理解，不自动等价于高细节单物体摄影测量。也不是本机的可替换 Android provider。[ARKit 官方说明](https://developer.apple.com/documentation/arkit/arworldtrackingconfiguration/scenereconstruction) |

RGB 离线重建是质量升级路线，不能只加一个“导出照片”按钮就声称完成：需要采集引导、模糊/曝光筛选、掩码、真实任务处理、失败恢复、模型导入、尺度/坐标对齐和结果检查。照片上传到外部付费服务会改变数据流和费用，不能在研究后默默启用。

## 4. 商用应用和 SDK：可比较，不默认可嵌入

Scaniverse 提供 Android App，当前官方资料区分新体验的云处理与 Classic 的单次本地处理；NSDK 支持 Unity、Android、iOS，但要求 Scaniverse business account。其扫描、资产和定位服务可作为对照或经过评估的集成方案，不等于能免费把成熟扫描核心直接放进本仓库。当前文档的设备建议也不能代替小米 13 Pro 的具体支持与效果实测。[Scaniverse 快速开始](https://nianticspatial.com/docs/scaniverse/quickstart/)、[NSDK 下载与要求](https://nianticspatial.com/docs/nsdk/downloads/)

Polycam 当前确有 Reconstruction API：获批的 Enterprise 集成可上传 `session.zip` 后执行云重建，使用预付 credits 并有 workspace 限流；这是可用的商业后端路线，不是免费离线 Android SDK。本轮没有账户、授权或效果实测，不能报告为已经接入。[官方 Reconstruction API](https://poly.cam/docs/api/reconstruction)、[产品说明](https://poly.cam/reconstruction-api)

## 5. NeRF、3D Gaussian Splatting 与几何扫描的区别

NeRF 的原始目标是新视角合成，用位置、方向预测密度与视角相关颜色；3DGS 则以优化后的三维高斯表示场景，重点同样是高质量新视角渲染。它们可以让照片式的旋转查看更好看，但“看起来真实”不能自动证明得到贴合、可测量、可碰撞的三角网格。[NeRF 作者论文](https://arxiv.org/abs/2003.08934)、[3DGS 作者项目](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/)

若选择这类路线，需要专门处理训练、移动端渲染、尺度、几何提取及标记附着；不能把透明高斯当作可靠碰撞面。原始 3DGS 参考代码本身采用专用许可，不能因其论文公开就推导为任意商用授权。[参考实现许可证](https://github.com/graphdeco-inria/gaussian-splatting/blob/main/LICENSE.md)

因此，本项目应以几何融合保证选点与表面结构，再将摄影测量或神经渲染作为明确区分的细节/外观升级，避免为追求漂亮预览牺牲问题标记的真实位置。

## 6. 当前可实施架构与初始参数

下面是本项目的工程起点，全部需要用真实输入调参，不是官方推荐值或精度承诺：

1. **采集状态机**：空闲 → 框选/确定目标 → 扫描 → 暂停/检查 → 补扫或完成 → 本地保存/查看。跟踪丢失停止融合，恢复后主动继续；清除释放模型、体积和采样状态，保留独立的问题记录。
2. **目标约束**：按用户最新要求缩小可见扫描框，按每帧实际投影裁剪输入；另用约 0.4–0.6 米的有界空间作为默认物品体积。框内桌面仍可能被包含，屏幕框不等于语义分割。若加目标掩码，必须验证掩码时序与 RGB/深度配准。
3. **可信输入**：深度、置信度、相机内参、屏幕方向和位姿同一坐标约定；过滤无效值和明显深度断层。保留真实时间戳，不用上一帧 RGB 冒充同步数据。用户最新选择是保留 0.5 米最低距离，本项目先接受约 0.5–3 米输入，不放宽到 0.1 米；距离不足就引导后退。该软件筛选范围不是精度承诺，官方通常最准确的范围仍约为 0.5–5 米。
4. **融合**：默认约 4–6 毫米体素，截断距离约 3–5 个体素；置信度、视角、残差决定权重，并设累计权重上限。0.5 米、5 毫米步长约为 100³ 个体素；只存 float TSDF 和 weight 就约 8 MB，颜色、计数和网格缓冲另算。Full Depth 为手机预览提供密集输入；Raw 置信度可先试 0.5 门槛，对应的可靠深度可做高权重证据。Full 与高置信 Raw 明显不一致时拒绝该点；没有 Raw 支持的 Full 值低权重参与，并要求多帧一致，不能用 Full 的全图覆盖伪装全图高可信。
5. **支持门槛**：未观测体素保持未知；网格仅从至少数次一致观测支持的零交叉提取。可先试至少 3 个有效观测，不把未知体素初始化成已知自由空间并生成虚假边界。相机近似静止时减少重复融合，重复帧不能当作独立证据。
6. **提取与预览**：融合和网格提取分开调度，避免每来一帧就全体积重建；可先以 2–5 Hz 融合、约 1 Hz 或暂停时提取作为性能起点，再按手机实测调整。主线程负责 Unity 资源更新，重计算用受控后台任务或 Jobs，不无限排队旧帧。
7. **结果处理**：移除孤立小组件与不可信飞点，适度法线/网格平滑；不可把填洞当作实际扫描成功。保留观察到的孔洞和背面缺口，不把设备按钮磨平后叫作“效果更好”。
8. **完整结果**：本地保存可重新打开的网格、版本化元数据、采集诊断与缩略图；本地导出通用格式并提供失败提示。RGB 顶点色应先保证颜色与深度配准和时间一致，不把稀疏顶点色称为照片级纹理贴图；离线要求不妨碍手机自身后台优化，但不得自动上传数据。

若选用手机 C# 的有界实现，可参考 Open3D 的数值定义而不引入其完整依赖；若性能或质量不达标，在用户当前约束下应比较手机原生 C++/GPU 融合并公开限制，不能悄悄把任务改成上传云端或用 PC 处理，也不能降低成功标准。

## 7. 必须形成的验证证据

| 验证层 | 要回答的问题 | 通过依据 |
| --- | --- | --- |
| 合成几何 | 平面、斜面、球面/圆柱能否在不同位姿下融合为同一表面？ | 已知几何误差、法线与零交叉；重复加入带噪帧后不形成成倍面片或漂移层 |
| 坐标与 ROI | 竖屏/横屏、深度分辨率缩放、相机内参、时间同步是否正确？ | 同一标定目标的深度与屏幕投影一致，框外样本不进入融合；方向测试必须先能使旧错误实现失败 |
| 真实数据重放 | 固定输入重建是否稳定？ | 记录一次原始采集，可离线反复得到相同结果，并与 Open3D 或第二实现比较 |
| 真机物品 | 有纹理的盒子、弧面外壳、手柄是否保留可辨认结构？ | 保存相同路线的 RGB 视频、网格预览和失败处，不用截图面片数量替代贴合度 |
| 限制场景 | 黑色低纹理、反光按钮、细缝、透明件是否被误报为成功？ | 明确未覆盖/低可信状态；不存在的表面不填补为精确结果 |
| 生命周期 | 扫描后返回首页、重新新增、反复清除、补扫和预览是否正常？ | 相机重新出帧、模型无残留、状态可重新开始、已保存问题数据保持完整 |
| 性能和持久化 | 长扫是否发热、卡顿、内存增长？结果能否重开/导出？ | 实测帧时/峰值内存/处理时长，重启读取与导出文件检查；构建成功不能代替这些结果 |

对“效果要好”的最终判断应来自上述实物的重复结果。若只有 ARCore 低分辨率运动深度而目标是近距离、黑色、反光且带细小结构的手柄，即使 TSDF 更平滑，也不能保证恢复照片级几何；需要摄影测量、受控光照/表面处理或硬件深度方案的实测比较。
