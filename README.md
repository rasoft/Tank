# Tank — 坦克大战 & NSS/NFRU 训练数据采集工程

本仓库基于 Unity 官方经典教学项目 **Tanks!（坦克大战）**（团结引擎版），并在其之上构建了一套面向 **NSS（神经超分）/ NFRU（神经插帧）** 研究的**运行时训练数据采集系统**（`Assets/NssCapture/`）。

- 引擎：**团结引擎（Tuanjie）1.10.3**（Unity 2022.3 分支）
- 渲染管线：**URP 14.2.0-t1**（含团结引擎运动矢量扩展）
- 色彩空间：**Gamma**（美术按 Gamma 制作，请勿直接切 Linear，详见下文注意事项）
- 主场景：`Assets/_Complete-Game.unity`（完整版游戏），`Assets/_Complete-GameSettings.lighting` 为其烘焙设置

---

## 1. 游戏说明（Tanks!）

双人对战坦克游戏：控制坦克移动、转向并蓄力发射炮弹击毁对方，多回合制。

| 操作 | 玩家 1 | 玩家 2 |
|------|--------|--------|
| 移动 | W / S | ↑ / ↓ |
| 转向 | A / D | ← / → |
| 开火（长按蓄力） | Space | Enter/Return |

也支持手柄（joystick 1/2）。游戏脚本位于 `Assets/Scripts/`（`Camera`/`Managers`/`Shell`/`Tank`/`UI`），完整实现版本在 `Assets/_Completed-Assets/Scripts/`。

## 2. 目录结构

```
Assets/
├── _Complete-Game.unity        # 完整游戏场景
├── _Completed-Assets/          # 教程完成版脚本/预制体/动画等
├── Scripts/                    # 游戏运行脚本（相机/管理器/炮弹/坦克/UI）
├── NssCapture/                 # ★ NSS/NFRU 训练数据采集系统（本项目新增）
│   ├── Runtime/               #   采集控制器、抖动、MV触发Feature、写入线程、分析器、验证导演、Shader
│   ├── Editor/                #   菜单、Play模式自动化、验证场景构建器
│   ├── Validation/            #   极简验证场景（CaptureValidation.unity）与棋盘格材质
│   └── NssCaptureConfig.asset #   采集配置（分辨率/帧率/输出路径等）
├── Models / Prefabs / Materials / Sprites / AudioClips / Fonts ...
NSS_Dataset/                   # 采集输出目录（gitignored，含 EXR 数据与报告）
CODELY.md                      # Codely CLI 项目记忆
```

## 3. NSS/NFRU 训练数据采集系统

依据《NSS/NFRU 训练数据采集说明手册》实现，为神经超分（960×540→1920×1080）与神经插帧（30→60 FPS）研究提供"渲染信息超集"数据集。

### 3.1 每帧采集内容

| 数据 | 规格 | 说明 |
|------|------|------|
| `lr_color.exr` | 960×540 RGB16F | 真实低分辨率渲染（非降采样），带 Halton(2,3) 亚像素抖动 |
| `gt_color.exr` | 1920×1080 RGB16F | 同一仿真状态的高清真值，**无抖动** |
| `depth_linear.exr` | 960×540 R32F | 线性眼空间深度（米），透视/正交、Reverse-Z 均已处理 |
| `motion.exr` | 960×540 RG16F | 引擎级运动矢量（相机+逐物体），已扣除抖动分量 |
| `metadata.json` | 每帧 | 帧号、时间戳、抖动、视图/投影矩阵（抖动前/后）、near/far、camera_cut、QA 统计 |

关键设计：

- **固定 60 Hz 仿真时间轴**（`Time.captureFramerate` + 物理对齐），保证时间连续性与 30/60 帧率可离线构造；
- **同一仿真状态双渲染**：LR 与 GT 相机在同一帧内先后渲染，粒子/动画/Shader 时间完全一致；
- **运动矢量**来自团结引擎 URP 内置 `MotionVectorRenderPass`（通过 `NssMvTriggerFeature` 声明 `ScriptableRenderPassInput.Motion` 触发），方向 `previous→current`、UV 单位、左上原点、RG16F；抖动污染以常量精确扣除；
- **异步链路**：`AsyncGPUReadback` → 主线程 EXR 编码 → 后台写入线程落盘，帧号全程绑定；
- **质量门禁**：静态 MV≈0 校验、GT→LR 抖动重采样对比（含抖动符号自动校准）、运动占比/方向一致性检查、NaN/Inf 扫描、帧连续性检查，产出 `capture_report_*.json`。

### 3.2 使用方法

所有入口在编辑器菜单 **`Tools/NSS Capture/`**：

| 菜单 | 用途 |
|------|------|
| `Validation / Run Case A/B/C` | 在极简验证场景自动采集 240 帧并给出门禁 PASS/FAIL（建议改动后先跑） |
| `Session / Capture Current Scene (300 frames)` | 对当前场景定长自动采集 |
| `Session / Start Capture` + `Stop & Flush` | 手动开始/结束（可边玩边采） |
| `Open Dataset Folder` / `Select Config` | 打开输出目录 / 编辑采集配置 |

输出结构（对应手册 §17）：

```
NSS_Dataset/
├── dataset_spec.json                      # 数据集级约定（MV/深度/抖动/矩阵/色彩空间）
├── sequence_NNNN/
│   ├── sequence.json                       # 序列统计（帧范围/cut/NaN）
│   ├── frame_NNNNNN/{lr_color,gt_color,depth_linear,motion}.exr + metadata.json
│   └── viz/                                # 质量门禁可视化 PNG（MV/深度/LR/差异图）
└── capture_report_YYYYMMDD_HHMMSS.json    # 会话报告
```

### 3.3 质量门禁基线（当前状态）

- Case A（静态）：抖动修正后 MV 均值 **0.0001 px**；GT↔LR 重采样差 **0.0048** — PASS
- Case B（物体运动）：运动区域占比 **1.14%**，峰值 12.4 px 与物理速度吻合 — PASS
- Case C（相机原地旋转）：均值 1.10 px，方向一致性 **100%** — PASS
- Tanks 实战冒烟：300/300 帧，0 丢失，正交相机路径正常

## 4. 注意事项

1. **色彩空间保持 Gamma**：直接切 Linear 会使美术/烘焙失效（画面异常），采集系统会如实将实际色彩空间写入 `metadata.json`/`dataset_spec.json`；若未来需要线性 HDR 数据，请先为 Linear 重调美术再切换。
2. `Packages/manifest.json` 中 `cn.tuanjie.codely.bridge` 指向本地 `.codely.packages/`（Codely CLI 工具状态，gitignored）——属预期现象，请勿提交该改动。
3. `NSS_Dataset/` 已加入 `.gitignore`，切勿提交大规模训练数据。
4. 采集在 Play 模式下进行，仿真按 60 Hz 确定性推进，游戏观感呈"慢动作"属正常现象。

## 5. 致谢

- 游戏本体基于 Unity 官方教学项目 Tanks!（第三方素材声明见 `Assets/Tanks!_Third-PartyNotice.txt`）。
- 采集系统设计依据《NSS_NFRU 训练数据采集说明手册 V1.2》。
