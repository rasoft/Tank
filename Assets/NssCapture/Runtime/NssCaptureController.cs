using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// NSS/NFRU training data capture controller (manual section 26 module map).
// Per simulation frame (fixed 60 Hz via Time.captureFramerate) it renders the same
// simulation state twice - a jittered low-res camera and an unjittered GT camera -
// copies engine motion vectors and depth into private targets, and streams
// everything through AsyncGPUReadback into EXR files on a writer thread.
[DefaultExecutionOrder(1000)]
public class NssCaptureController : MonoBehaviour
{
    const int ChannelLr = 0, ChannelGt = 1, ChannelMv = 2, ChannelDepth = 3;

    public bool IsRunning { get; private set; }
    public bool SessionDone { get; private set; }
    public string OutputRoot { get; private set; }
    public string SessionSummary { get; private set; }

    NssCaptureConfig _cfg;
    long _maxFrames;
    long _frameId;
    int _sequenceId;
    int _frameInSequence;
    string _caseName = "gameplay";

    Camera _lrCam, _gtCam;
    RenderTexture _lrRT, _gtRT, _mvRT, _depthRT;
    Material _mvMat, _depthMat;
    NssDatasetWriter _writer;
    NssCaptureAnalyzer _analyzer;

    Vector2 _jitter = Vector2.zero;
    Vector2 _jitterPrev = Vector2.zero;
    Vector3 _lastCamPos;
    Quaternion _lastCamRot = Quaternion.identity;
    bool _hasLastCam;

    int _prevCaptureFramerate;
    float _prevFixedDelta;
    long _framesCaptured, _framesDropped;
    float _drainStart;
    bool _draining;
    bool _mvGlobalWarned, _depthGlobalWarned;

    class PendingFrame
    {
        public int expected;
        public int received;
        public bool failed;
        public NssFrameMetadata meta;
        public byte[] lr, gt, mv, depth;
        public Vector2 jitter;
        public int frameInSequence;
    }

    readonly Dictionary<long, PendingFrame> _pending = new Dictionary<long, PendingFrame>();

    public void BeginSession(NssCaptureConfig cfg, long maxFrames)
    {
        if (IsRunning) return;
        if (QualitySettings.activeColorSpace != ColorSpace.Linear)
            Debug.LogWarning("[NssCapture] Project is in Gamma color space; captured color will be gamma-encoded. This is recorded truthfully in metadata.json / dataset_spec.json (manual section 16). Switch to Linear only after retuning the art for it.");

        _cfg = cfg;
        _maxFrames = maxFrames;
        _caseName = "gameplay";
        var director = FindFirstObjectByType<ValidationDirector>();
        if (director != null) _caseName = director.mode.ToString();

        OutputRoot = Path.GetFullPath(Path.Combine(Application.dataPath, cfg.outputRoot));
        Directory.CreateDirectory(OutputRoot);

        float aspect = (float)cfg.lrWidth / cfg.lrHeight;
        if (Mathf.Abs(aspect - (float)cfg.gtWidth / cfg.gtHeight) > 0.001f)
            Debug.LogWarning("[NssCapture] LR and GT resolutions have different aspect ratios; GT upsampling checks are skipped.");

        _lrRT = MakeRT(cfg.lrWidth, cfg.lrHeight, GraphicsFormat.R16G16B16A16_SFloat, true);
        _gtRT = cfg.captureGt ? MakeRT(cfg.gtWidth, cfg.gtHeight, GraphicsFormat.R16G16B16A16_SFloat, true) : null;
        _mvRT = cfg.captureMotionVectors ? MakeRT(cfg.lrWidth, cfg.lrHeight, GraphicsFormat.R16G16_SFloat, false) : null;
        _depthRT = cfg.captureDepth ? MakeRT(cfg.lrWidth, cfg.lrHeight, GraphicsFormat.R32_SFloat, false) : null;

        Shader mvShader = Shader.Find("Hidden/Nss/CopyMotion");
        Shader depthShader = Shader.Find("Hidden/Nss/DepthLinear");
        if (mvShader == null || depthShader == null)
        {
            Debug.LogError("[NssCapture] Nss shaders missing (CopyMotion/DepthLinear); session aborted.");
            return;
        }
        _mvMat = new Material(mvShader);
        _depthMat = new Material(depthShader);

        _lrCam = CreateCamera("NssCapture_LR", _lrRT);
        _gtCam = cfg.captureGt ? CreateCamera("NssCapture_GT", _gtRT) : null;

        _prevCaptureFramerate = Time.captureFramerate;
        _prevFixedDelta = Time.fixedDeltaTime;
        Time.captureFramerate = cfg.simulationFps;
        Time.fixedDeltaTime = cfg.SimulationDeltaTime;

        _frameId = 0;
        _frameInSequence = 0;
        _sequenceId = FindNextSequenceIndex(OutputRoot);
        _framesCaptured = 0;
        _framesDropped = 0;
        _pending.Clear();
        _analyzer = new NssCaptureAnalyzer();
        _writer = new NssDatasetWriter(OutputRoot);
        _hasLastCam = false;

        ApplyObjectMotionModes();
        DontDestroyOnLoad(gameObject);
        IsRunning = true;
        SessionDone = false;
        Debug.Log($"[NssCapture] Session started: case={_caseName}, output={OutputRoot}, sequence={_sequenceId}, simFps={cfg.simulationFps}, maxFrames={maxFrames}");
    }

    void LateUpdate()
    {
        if (!IsRunning)
        {
            if (_draining) DrainStep();
            return;
        }

        try
        {
            CaptureFrame();
        }
        catch (Exception e)
        {
            _framesDropped++;
            Debug.LogException(e);
        }

        if (_maxFrames > 0 && _framesCaptured >= _maxFrames)
            EndSession();
    }

    void CaptureFrame()
    {
        Camera main = Camera.main;
        if (main == null)
        {
            _framesDropped++;
            return;
        }

        // ---- jitter for this frame (LR pixels, engine-compatible Halton sequence)
        Vector2 newJitter = _cfg.enableJitter
            ? NssJitter.Get((int)_frameId, _cfg.haltonBaseX, _cfg.haltonBaseY, _cfg.jitterScale)
            : Vector2.zero;
        _jitterPrev = _jitter;
        _jitter = newJitter;

        // ---- sequence / camera-cut bookkeeping (manual 19)
        bool sequenceStart = _frameInSequence == 0;
        bool positionCut = DetectPositionCut(main);
        if (_frameInSequence >= _cfg.framesPerSequenceCap)
        {
            _sequenceId++;
            _frameInSequence = 0;
            sequenceStart = true;
        }
        else if (positionCut && !sequenceStart && _cfg.autoSplitOnCameraCut)
        {
            _sequenceId++;
            _frameInSequence = 0;
            sequenceStart = true;
        }
        bool cameraCut = sequenceStart || positionCut;

        // ---- camera sync + projections rebuilt at the capture aspect
        Matrix4x4 projUnjittered = BuildProjection(main, (float)_cfg.lrWidth / _cfg.lrHeight);
        Matrix4x4 projJittered = NssJitter.Apply(projUnjittered, _jitter, _cfg.lrWidth, _cfg.lrHeight);

        SyncCamera(_lrCam, main);
        _lrCam.projectionMatrix = projJittered;
        _lrCam.Render();

        if (_cfg.captureMotionVectors) CopyMotionVectors(cameraCut);
        if (_cfg.captureDepth) CopyDepth(main);

        if (_gtCam != null)
        {
            SyncCamera(_gtCam, main);
            _gtCam.projectionMatrix = projUnjittered;
            _gtCam.Render();
        }

        // ---- metadata
        var meta = new NssFrameMetadata
        {
            frame_id = _frameId,
            sequence_id = _sequenceId,
            frame_in_sequence = _frameInSequence,
            simulation_timestamp = _frameId * _cfg.SimulationDeltaTime,
            lr_resolution = new[] { _cfg.lrWidth, _cfg.lrHeight },
            gt_resolution = new[] { _cfg.gtWidth, _cfg.gtHeight },
            jitter = new[] { _jitter.x, _jitter.y },
            jitter_unit = "lr_pixel",
            camera = new NssCameraMetadata
            {
                near = main.nearClipPlane,
                far = main.farClipPlane,
                fov_y = main.fieldOfView,
                orthographic_size = main.orthographicSize,
                projection_type = main.orthographic ? "orthographic" : "perspective",
                camera_cut = cameraCut,
                view_matrix = NssCameraMetadata.FromMatrix(main.worldToCameraMatrix),
                projection_jittered = NssCameraMetadata.FromMatrix(projJittered),
                projection_unjittered = NssCameraMetadata.FromMatrix(projUnjittered),
                view_projection_unjittered = NssCameraMetadata.FromMatrix(projUnjittered * main.worldToCameraMatrix)
            },
            render = new NssRenderMetadata
            {
                color_space = QualitySettings.activeColorSpace.ToString(),
                hdr = true,
                exposure = 1f,
                tone_mapping_applied = false,
                ui_composited = false
            }
        };

        int channels = 1 + (_cfg.captureGt ? 1 : 0) + (_cfg.captureMotionVectors ? 1 : 0) + (_cfg.captureDepth ? 1 : 0);
        var pf = new PendingFrame { expected = channels, meta = meta, jitter = _jitter, frameInSequence = _frameInSequence };
        _pending[_frameId] = pf;

        AsyncGPUReadback.Request(_lrRT, 0, r => OnReadback(r, pf, ChannelLr));
        if (_gtRT != null) AsyncGPUReadback.Request(_gtRT, 0, r => OnReadback(r, pf, ChannelGt));
        if (_mvRT != null) AsyncGPUReadback.Request(_mvRT, 0, r => OnReadback(r, pf, ChannelMv));
        if (_depthRT != null) AsyncGPUReadback.Request(_depthRT, 0, r => OnReadback(r, pf, ChannelDepth));

        _framesCaptured++;
        _frameId++;
        _frameInSequence++;

        if (_cfg.autoSetObjectMotionVectors && (_frameId % _cfg.objectMotionRescanEveryFrames) == 0)
            ApplyObjectMotionModes();
    }

    bool DetectPositionCut(Camera main)
    {
        Vector3 pos = main.transform.position;
        Quaternion rot = main.transform.rotation;
        bool cut = false;
        if (_hasLastCam)
        {
            float move = Vector3.Distance(pos, _lastCamPos);
            float turn = Quaternion.Angle(rot, _lastCamRot);
            cut = move > _cfg.cameraCutPositionThreshold || turn > _cfg.cameraCutRotationThresholdDeg;
        }
        _lastCamPos = pos;
        _lastCamRot = rot;
        _hasLastCam = true;
        return cut;
    }

    // Rebuilds the projection at the capture RT aspect so the dataset framing is
    // independent of the current game view aspect.
    Matrix4x4 BuildProjection(Camera main, float aspect)
    {
        if (main.orthographic)
        {
            float half = main.orthographicSize;
            return Matrix4x4.Ortho(-half * aspect, half * aspect, -half, half, main.nearClipPlane, main.farClipPlane);
        }
        return Matrix4x4.Perspective(main.fieldOfView, aspect, main.nearClipPlane, main.farClipPlane);
    }

    void SyncCamera(Camera cam, Camera main)
    {
        cam.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
        cam.orthographic = main.orthographic;
        cam.orthographicSize = main.orthographicSize;
        cam.fieldOfView = main.fieldOfView;
        cam.nearClipPlane = main.nearClipPlane;
        cam.farClipPlane = main.farClipPlane;
        cam.cullingMask = main.cullingMask;
        cam.clearFlags = main.clearFlags;
        cam.backgroundColor = main.backgroundColor;
    }

    void CopyMotionVectors(bool cameraCut)
    {
        var mvTex = Shader.GetGlobalTexture("_MotionVectorTexture") as RenderTexture;
        if (mvTex == null)
        {
            if (!_mvGlobalWarned)
            {
                _mvGlobalWarned = true;
                Debug.LogError("[NssCapture] _MotionVectorTexture is null. Is NssMvTriggerFeature installed on the URP renderer?");
            }
            RenderTexture.active = _mvRT;
            GL.Clear(false, true, Color.clear);
            return;
        }

        // The engine computes MV from non-jittered references while we jitter via
        // camera.projectionMatrix, so the raw texture contains a constant pollution
        // term (j_curr - j_prev) in UV space. Measured on this platform the GL-level
        // and shader-level Y flips cancel, so the stored value is already in
        // top-left-origin UV: pollution = (djx/w, +djy/h) exactly (verified case A).
        Vector2 delta = Vector2.zero;
        if (_cfg.removeJitterFromMotionVectors)
        {
            float jdx = (_jitter.x - _jitterPrev.x) / _cfg.lrWidth;
            float jdy = (_jitter.y - _jitterPrev.y) / _cfg.lrHeight;
            delta = new Vector2(jdx, jdy);
        }
        _mvMat.SetVector("_JitterDelta", delta);
        _mvMat.SetTexture("_MainTex", mvTex); // explicit bind; Graphics.Blit's implicit bind needs the Properties block
        Graphics.Blit(mvTex, _mvRT, _mvMat);

        if (cameraCut) // history is invalid: clear (manual 13 invalid handling)
        {
            RenderTexture.active = _mvRT;
            GL.Clear(false, true, Color.clear);
        }
    }

    void CopyDepth(Camera main)
    {
        var depthTex = Shader.GetGlobalTexture("_CameraDepthTexture") as RenderTexture;
        if (depthTex == null)
        {
            if (!_depthGlobalWarned)
            {
                _depthGlobalWarned = true;
                Debug.LogError("[NssCapture] _CameraDepthTexture is null. Is NssMvTriggerFeature installed on the URP renderer?");
            }
            RenderTexture.active = _depthRT;
            GL.Clear(false, true, Color.clear);
            return;
        }
        _depthMat.SetFloat("_Ortho", main.orthographic ? 1f : 0f);
        _depthMat.SetFloat("_ReverseZ", SystemInfo.usesReversedZBuffer ? 1f : 0f);
        _depthMat.SetFloat("_Near", main.nearClipPlane);
        _depthMat.SetFloat("_Far", main.farClipPlane);
        _depthMat.SetTexture("_MainTex", depthTex);
        Graphics.Blit(depthTex, _depthRT, _depthMat);
    }

    void OnReadback(AsyncGPUReadbackRequest req, PendingFrame pf, int channel)
    {
        if (req.hasError)
        {
            pf.failed = true;
        }
        else
        {
            byte[] data = req.GetData<byte>().ToArray();
            switch (channel)
            {
                case ChannelLr: pf.lr = data; break;
                case ChannelGt: pf.gt = data; break;
                case ChannelMv: pf.mv = data; break;
                case ChannelDepth: pf.depth = data; break;
            }
        }
        pf.received++;
        if (pf.received >= pf.expected)
        {
            _pending.Remove(pf.meta.frame_id);
            if (pf.failed)
            {
                _framesDropped++;
                Debug.LogWarning($"[NssCapture] frame {pf.meta.frame_id} dropped (readback error).");
            }
            else
            {
                FinalizeFrame(pf);
            }
        }
    }

    void FinalizeFrame(PendingFrame pf)
    {
        var meta = pf.meta;

        byte[] lrExr = EncodeExr(pf.lr, _cfg.lrWidth, _cfg.lrHeight, TextureFormat.RGBAHalf);
        byte[] gtExr = pf.gt != null ? EncodeExr(pf.gt, _cfg.gtWidth, _cfg.gtHeight, TextureFormat.RGBAHalf) : null;
        byte[] mvExr = pf.mv != null ? EncodeExr(pf.mv, _cfg.lrWidth, _cfg.lrHeight, TextureFormat.RGHalf) : null;
        byte[] depthExr = pf.depth != null ? EncodeExr(pf.depth, _cfg.lrWidth, _cfg.lrHeight, TextureFormat.RFloat) : null;

        if (_cfg.enableNanScan)
        {
            int nan = 0, inf = 0;
            if (lrExr != null)
            {
                NssHalfUtils.CountHalfAbnormal(pf.lr, _cfg.lrWidth * _cfg.lrHeight * 4, out int n1, out int i1);
                nan += n1; inf += i1;
            }
            if (mvExr != null)
            {
                NssHalfUtils.CountHalfAbnormal(pf.mv, _cfg.lrWidth * _cfg.lrHeight * 2, out int n2, out int i2);
                nan += n2; inf += i2;
            }
            if (depthExr != null)
            {
                NssHalfUtils.CountFloatAbnormal(pf.depth, _cfg.lrWidth * _cfg.lrHeight, out int n3, out int i3);
                nan += n3; inf += i3;
            }
            meta.qa.nan_count = nan;
            meta.qa.inf_count = inf;
        }

        if (_cfg.runQualityGate)
        {
            var qa = _analyzer.Analyze(_cfg, pf.frameInSequence, meta.camera.camera_cut,
                pf.mv, pf.depth, pf.lr, pf.gt, pf.jitter);
            qa.nan_count = meta.qa.nan_count;
            qa.inf_count = meta.qa.inf_count;
            meta.qa = qa;
        }

        var payload = new NssFramePayload
        {
            SequenceId = meta.sequence_id,
            FrameInSequence = meta.frame_in_sequence,
            FrameId = meta.frame_id,
            SimTimestamp = meta.simulation_timestamp,
            CameraCut = meta.camera.camera_cut,
            MetadataJson = JsonUtility.ToJson(meta, true),
            LrColorExr = lrExr,
            GtColorExr = gtExr,
            MotionExr = mvExr,
            DepthExr = depthExr
        };
        if (_analyzer != null)
        {
            payload.VizMotionPng = _analyzer.VizMotionPng; _analyzer.VizMotionPng = null;
            payload.VizDepthPng = _analyzer.VizDepthPng; _analyzer.VizDepthPng = null;
            payload.VizLrPng = _analyzer.VizLrPng; _analyzer.VizLrPng = null;
            payload.VizDiffPng = _analyzer.VizDiffPng; _analyzer.VizDiffPng = null;
        }
        _writer.Enqueue(payload);
    }

    static byte[] EncodeExr(byte[] raw, int w, int h, TextureFormat format)
    {
        var tex = new Texture2D(w, h, format, false, true);
        tex.LoadRawTextureData(raw);
        tex.Apply(false, false);
        byte[] exr = tex.EncodeToEXR();
        UnityEngine.Object.Destroy(tex);
        return exr;
    }

    static RenderTexture MakeRT(int w, int h, GraphicsFormat format, bool depthBuffer)
    {
        var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.Default);
        desc.graphicsFormat = format;
        desc.depthBufferBits = depthBuffer ? 24 : 0;
        desc.msaaSamples = 1;
        desc.mipCount = 1;
        var rt = new RenderTexture(desc);
        rt.filterMode = FilterMode.Point;
        rt.wrapMode = TextureWrapMode.Clamp;
        rt.Create();
        return rt;
    }

    static Camera CreateCamera(string name, RenderTexture target)
    {
        var go = new GameObject(name);
        var cam = go.AddComponent<Camera>();
        cam.enabled = false; // manual rendering only
        cam.targetTexture = target;
        cam.allowMSAA = false;
        cam.allowHDR = true;
        cam.allowDynamicResolution = false;
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        var data = cam.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = false;
        data.antialiasing = AntialiasingMode.None;
        return cam;
    }

    void ApplyObjectMotionModes()
    {
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            bool dynamic = r is SkinnedMeshRenderer || r.GetComponentInParent<Rigidbody>() != null;
            if (dynamic && r.motionVectorGenerationMode != MotionVectorGenerationMode.Object)
                r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
        }
    }

    static int FindNextSequenceIndex(string root)
    {
        int next = 1;
        if (!Directory.Exists(root)) return next;
        foreach (var dir in Directory.GetDirectories(root, "sequence_*"))
        {
            string name = Path.GetFileName(dir);
            if (int.TryParse(name.Substring("sequence_".Length), out int v) && v >= next)
                next = v + 1;
        }
        return next;
    }

    // ------------------------------------------------------------------ stop

    public void EndSession()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _draining = true;
        _drainStart = Time.realtimeSinceStartup;
        if (_pending.Count == 0) DrainStep();
    }

    void DrainStep()
    {
        if (!_draining) return;
        if (_pending.Count > 0 && Time.realtimeSinceStartup - _drainStart < 10f)
            return; // wait for in-flight readbacks across frames
        if (_pending.Count > 0)
        {
            _framesDropped += _pending.Count;
            Debug.LogWarning($"[NssCapture] {_pending.Count} frames lost at stop (readbacks still in flight).");
            _pending.Clear();
        }
        _draining = false;
        FinalizeSession();
    }

    void FinalizeSession()
    {
        Time.captureFramerate = _prevCaptureFramerate;
        Time.fixedDeltaTime = _prevFixedDelta;

        _writer.EndAndWait(120_000);

        var issues = _writer.GetIssues();
        var sequences = _writer.GetSequenceSummaries();

        var perSeq = new List<string>();
        foreach (var s in sequences)
            perSeq.Add($"sequence_{s.sequence_id:D4}: frames={s.frame_count}, ids={s.first_frame_id}..{s.last_frame_id}, cuts={s.camera_cut_frames.Length}, nan={s.nan_count}");

        List<string> caseResults = null;
        string calibration = "off";
        double resampleAvg = -1;
        if (_cfg.runQualityGate && _analyzer != null)
        {
            caseResults = _analyzer.BuildCaseResults(_caseName);
            calibration = _analyzer.Calibrated
                ? $"signX={_analyzer.SignX:+0;-0}, signY={_analyzer.SignY:+0;-0}"
                : "uncalibrated";
            resampleAvg = _analyzer.ResampleFrames > 0 ? _analyzer.SumResampleDiff / _analyzer.ResampleFrames : -1;
        }

        WriteSpec();
        var report = new NssReportJson
        {
            session_started = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            frames_captured = _framesCaptured,
            frames_written = _writer.FramesWritten,
            frames_dropped = _framesDropped,
            sequence_count = sequences.Count,
            jitter_calibration = calibration,
            gt_lr_resample_mean_abs_diff = (float)resampleAvg,
            issues = issues.ToArray(),
            case_results = caseResults != null ? caseResults.ToArray() : new string[0],
            per_sequence = perSeq.ToArray()
        };
        string reportPath = Path.Combine(OutputRoot, $"capture_report_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));

        SessionSummary =
            $"[NssCapture] Session done: case={_caseName}, captured={_framesCaptured}, written={_writer.FramesWritten}, dropped={_framesDropped}, sequences={sequences.Count}\n" +
            string.Join("\n", caseResults != null ? caseResults : perSeq) +
            (issues.Count > 0 ? "\nIssues:\n" + string.Join("\n", issues) : "") +
            $"\nReport: {reportPath}";

        Debug.Log(SessionSummary);

        if (_lrCam != null) Destroy(_lrCam.gameObject);
        if (_gtCam != null) Destroy(_gtCam.gameObject);
        _lrCam = null; _gtCam = null;
        if (_lrRT != null) { _lrRT.Release(); Destroy(_lrRT); }
        if (_gtRT != null) { _gtRT.Release(); Destroy(_gtRT); }
        if (_mvRT != null) { _mvRT.Release(); Destroy(_mvRT); }
        if (_depthRT != null) { _depthRT.Release(); Destroy(_depthRT); }
        _lrRT = _gtRT = _mvRT = _depthRT = null;

        SessionDone = true;
    }

    void WriteSpec()
    {
        var spec = new NssDatasetSpecJson
        {
            engine = Application.unityVersion,
            render_pipeline = "UniversalRenderPipeline 14.2.0-t1 (Tuanjie fork, MotionVectorRenderPass)",
            lr_resolution = new[] { "lr_color", "960x540" },
            lr_resolution_px = new[] { _cfg.lrWidth, _cfg.lrHeight },
            gt_resolution_px = new[] { _cfg.gtWidth, _cfg.gtHeight },
            simulation_fps = _cfg.simulationFps,
            timestamp_rule = "simulation_timestamp = frame_id / simulation_fps; frame_id is globally monotonic per session",
            motion_vector = new NssMotionVectorSpec
            {
                resolution = $"{_cfg.lrWidth}x{_cfg.lrHeight}",
                direction = "previous_to_current",
                unit = "uv (value = currUV - prevUV; multiply by LR resolution for pixels)",
                origin = "top_left (texture space)",
                y_axis = "down (texture space)",
                camera_motion = true,
                dynamic_object_motion = true,
                dynamic_object_note = "per-object motion for renderers with MotionVectorGenerationMode.Object (auto-set on rigidbodies/skinned meshes); other dynamic content (particles, unflagged objects) is approximated by camera-depth reprojection",
                jitter_removed = _cfg.removeJitterFromMotionVectors,
                jitter_removed_note = "constant clip-space jitter delta (j_curr - j_prev) in top-left UV space subtracted from every sample at capture time; GL and shader Y flips cancel on this platform (verified via static-case MV test)",
                invalid_value = new[] { 0f, 0f },
                invalid_note = "motion is zeroed for the first frame of each sequence and every frame flagged camera_cut (history invalid)",
                source = "engine MotionVectorRenderPass (R16G16_SFloat), triggered via NssMvTriggerFeature"
            },
            depth = new NssDepthSpec
            {
                type = "linear_eye_depth",
                unit = "meter",
                range_note = "valid range is [near, far] from per-frame metadata; sky pixels land at ~far",
                reverse_z_raw_buffer = SystemInfo.usesReversedZBuffer,
                projection_type_note = "both perspective and orthographic supported; see per-frame metadata projection_type/near/far",
                formula = "persp: eye=1/(z*(1/near-1/far)+1/far) [reverse-z] ; ortho: eye=lerp(far, near, z) [reverse-z]"
            },
            jitter = new NssJitterSpec
            {
                unit = "lr_pixel",
                applied_to_lr = _cfg.enableJitter,
                sequence = $"Halton({_cfg.haltonBaseX},{_cfg.haltonBaseY}) - 0.5, scale {_cfg.jitterScale}, index (frame&1023)+1 (engine TAA convention)",
                application = "clip-space translation multiplied in front of the CPU projection matrix (jittered LR, unjittered GT)",
                texture_space_x = _analyzer != null && _analyzer.Calibrated ? $"x{(_analyzer.SignX > 0 ? "+" : "-")}1 calibrated" : "uncalibrated",
                texture_space_y = _analyzer != null && _analyzer.Calibrated ? $"y{(_analyzer.SignY > 0 ? "+" : "-")}1 calibrated" : "uncalibrated"
            },
            render = new NssRenderSpec
            {
                color_space = QualitySettings.activeColorSpace.ToString(),
                hdr = true,
                color_format = "RGB16F EXR (ZIP lossless)",
                depth_format = "R32F EXR",
                motion_format = "RG16F EXR",
                exposure = 1f,
                tone_mapping_applied = false,
                ui_composited = false
            },
            matrix_convention = "Unity CPU matrices, column-major, column-vector convention (clip = P * V * world); projections are pre-GL.GetGPUProjectionMatrix (no platform flip)"
        };
        File.WriteAllText(Path.Combine(OutputRoot, "dataset_spec.json"), JsonUtility.ToJson(spec, true));
    }

    void OnDestroy()
    {
        if (IsRunning || _draining)
        {
            IsRunning = false;
            _draining = false;
            _pending.Clear();
            _writer?.EndAndWait(120_000);
            Debug.LogWarning("[NssCapture] Session interrupted; dataset flushed best-effort.");
        }
    }
}
