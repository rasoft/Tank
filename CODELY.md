

## Codely Structured Memories

### User

### Feedback
- [2026-09-22 09:40:15] Tank 项目必须保持 Gamma 色彩空间：2026-09-18 切 Linear 后画面出现方块/渲染破坏（美术与烘焙按 Gamma 制作），用户要求回滚，已恢复。采集管线会如实把实际色彩空间写入 metadata.json/dataset_spec.json，Gamma 下可直接采；除非用户先为 Linear 重调美术，否则不要再切。

### Project
- [2026-09-22 09:40:15] NSS/NFRU 训练数据采集系统在 Assets/NssCapture/（依据 NSS_NFRU训练数据采集说明手册_V1.2.md 实现）：同状态 LR 960×540 + GT 1920×1080 双渲染、引擎 MotionVectorRenderPass 触发式 MV、线性深度、Halton 抖动、异步读回 EXR、后台写入线程与质量门禁分析器；入口菜单 Tools/NSS Capture；数据输出到项目根 NSS_Dataset/（已 gitignore）。验证门禁 A/B/C 与 Tanks 300 帧冒烟均已 PASS（2026-09-18，commit 42df138）。
- [2026-09-22 09:40:15] Tuanjie 1.10.3（Unity 2022.3 分支）非直觉 API：URP 14.2.0-t1 分支内置 MotionVectorRenderPass，任何声明 ScriptableRenderPassInput.Motion 的 RendererFeature 即触发 _MotionVectorTexture（R16G16_SFloat、previous→current、UV、左上原点；GL 与 shader 的 Y 翻转互相抵消，jitter 修正公式=(Δjx/w, Δjy/h) 无需再翻转）；AsyncGPUReadbackRequest 用 hasError（不是 error）；PlayModeStateChange.EnteredPlayMode（不是 EnteredPlay）；EncodeToEXR 是 ImageConversion 扩展方法；Graphics.Blit 自定义材质的 shader 必须有 Properties 块声明 _MainTex 并显式 SetTexture，否则采样到未初始化显存（表现为整屏均匀垃圾值）；ScriptableRendererData 的 m_RendererFeatureMap 是 List&lt;long&gt;（localId）。

### Reference

