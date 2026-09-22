using System;
using UnityEngine;

// Quality-gate analyzer (manual section 22/23): motion vector statistics,
// GT->LR jittered-resample comparison with automatic jitter sign calibration,
// and debug visualization PNGs for the first analyzed frames.
public class NssCaptureAnalyzer
{
    // Motion vector aggregates (corrected values, in LR pixels)
    public int MvFrames;
    public int MvInvalidFrames;
    public double SumMvMeanPx;
    public double MaxMvPx;
    public double SumActiveFrac;
    // Per-frame direction consistency over actively moving pixels (|MV| > 0.5 px):
    // oscillating pans reverse sign across frames and near the turning points the
    // field is jitter-noise-level, so direction is measured within each frame,
    // active pixels only, then aggregated.
    public double SumActiveSignedX;
    public double SumActiveAbsX;

    // GT/LR resample comparison
    public int ResampleFrames;
    public double SumResampleDiff;
    public double WorstResampleDiff;
    public bool Calibrated;
    public int SignX = 1, SignY = 1;
    public double CalibrationDiff;
    public int CalibrationFrame = -1;

    // Visualization PNGs produced on the designated frame; consumed by the controller.
    public byte[] VizMotionPng, VizDepthPng, VizLrPng, VizDiffPng;

    public NssFrameQa Analyze(NssCaptureConfig cfg, int frameInSequence, bool mvInvalid,
        byte[] mvBytes, byte[] depthBytes, byte[] lrBytes, byte[] gtBytes, Vector2 jitter)
    {
        var qa = new NssFrameQa { motion_vector_invalid = mvInvalid, gt_lr_resample_mean_abs_diff = -1f };

        if (mvBytes != null && !mvInvalid)
        {
            ComputeMvStats(cfg, mvBytes, qa);
            MvFrames++;
            SumMvMeanPx += qa.mv_mean_pixels;
            if (qa.mv_max_pixels > MaxMvPx) MaxMvPx = qa.mv_max_pixels;
            SumActiveFrac += qa.mv_active_fraction;
        }
        else
        {
            MvInvalidFrames++;
            qa.mv_mean_pixels = mvInvalid ? 0f : -1f;
        }

        bool doResample = gtBytes != null && lrBytes != null &&
            (frameInSequence == 8 || frameInSequence == 16 || (frameInSequence > 16 && frameInSequence % 32 == 16));
        if (doResample)
        {
            ResampleCompare(cfg, lrBytes, gtBytes, jitter, frameInSequence, qa,
                buildViz: cfg.writeVisualizationPngs && frameInSequence == 16);
        }

        if (cfg.writeVisualizationPngs && frameInSequence == 16)
        {
            if (mvBytes != null) VizMotionPng = BuildMotionViz(cfg, mvBytes);
            if (depthBytes != null) VizDepthPng = BuildDepthViz(cfg, depthBytes);
            if (lrBytes != null) VizLrPng = BuildColorViz(cfg, lrBytes, false);
        }

        return qa;
    }

    void ComputeMvStats(NssCaptureConfig cfg, byte[] mv, NssFrameQa qa)
    {
        int w = cfg.lrWidth, h = cfg.lrHeight;
        double sumMag = 0, maxMag = 0;
        // Direction consistency over actively moving pixels only: near the turning
        // points of an oscillating pan the field is jitter-noise-level and its
        // direction is meaningless, so those pixels must not dilute the metric.
        double activeSignedX = 0, activeAbsX = 0;
        int active = 0, n = 0;
        for (int y = 0; y < h; y += 2)
        {
            int row = y * w;
            for (int x = 0; x < w; x += 2)
            {
                int o = (row + x) * 4; // R16G16_SFloat = 4 bytes/px
                if (o + 3 >= mv.Length) break;
                float vx = NssHalfUtils.HalfToFloat((ushort)(mv[o] | (mv[o + 1] << 8))) * w;
                float vy = NssHalfUtils.HalfToFloat((ushort)(mv[o + 2] | (mv[o + 3] << 8))) * h;
                double mag = Math.Sqrt(vx * (double)vx + vy * (double)vy);
                sumMag += mag;
                if (mag > maxMag) maxMag = mag;
                if (mag > 0.5)
                {
                    active++;
                    activeSignedX += vx;
                    activeAbsX += Math.Abs(vx);
                }
                n++;
            }
        }
        if (active >= 16) // enough moving pixels for a meaningful direction vote
        {
            SumActiveSignedX += Math.Abs(activeSignedX);
            SumActiveAbsX += activeAbsX;
        }
        qa.mv_mean_pixels = n > 0 ? (float)(sumMag / n) : 0f;
        qa.mv_max_pixels = (float)maxMag;
        qa.mv_active_fraction = n > 0 ? (float)active / n : 0f;
    }

    void ResampleCompare(NssCaptureConfig cfg, byte[] lr, byte[] gt, Vector2 jitter,
        int frameInSequence, NssFrameQa qa, bool buildViz)
    {
        int lw = cfg.lrWidth, lh = cfg.lrHeight, gw = cfg.gtWidth, gh = cfg.gtHeight;
        int scale = gw / lw;
        if (scale * lw != gw || scale * lh != gh)
        {
            // Non-integer scale unsupported by the box resampler; report only.
            return;
        }

        float[] gtF = DecodeRgbaHalf(gt, gw, gh);
        float[] lrF = DecodeRgbaHalf(lr, lw, lh);

        int[] cand = Calibrated ? new[] { SignX } : new[] { 1, -1 };
        int[] candY = Calibrated ? new[] { SignY } : new[] { 1, -1 };
        double best = double.MaxValue; int bestX = SignX, bestY = SignY;
        float[] bestDiffMap = null;

        for (int yi = 0; yi < candY.Length; yi++)
        {
            for (int xi = 0; xi < cand.Length; xi++)
            {
                float offX = cand[xi] * jitter.x * scale;
                float offY = candY[yi] * jitter.y * scale;
                float[] diffMap = buildViz && !Calibrated ? null : (buildViz ? new float[lw * lh] : null);
                double sum = 0; int n = 0;
                int step = buildViz && diffMap != null ? 1 : 2;
                for (int y = 0; y < lh; y += step)
                {
                    for (int x = 0; x < lw; x += step)
                    {
                        double d = SampleBlockDiff(gtF, gw, gh, lrF, lw, scale, x, y, offX, offY);
                        sum += d; n++;
                        if (diffMap != null) diffMap[y * lw + x] = (float)d;
                    }
                }
                double mean = n > 0 ? sum / n : double.MaxValue;
                if (mean < best)
                {
                    best = mean;
                    bestX = cand[xi]; bestY = candY[yi];
                    bestDiffMap = diffMap;
                }
            }
        }

        if (!Calibrated)
        {
            Calibrated = true;
            SignX = bestX; SignY = bestY;
            CalibrationDiff = best;
            CalibrationFrame = frameInSequence;
        }

        qa.gt_lr_resample_mean_abs_diff = (float)best;
        ResampleFrames++;
        SumResampleDiff += best;
        if (best > WorstResampleDiff) WorstResampleDiff = best;

        if (buildViz && bestDiffMap != null) VizDiffPng = BuildDiffViz(lw, lh, bestDiffMap);
    }

    // Averages the scale x scale GT block covering LR pixel (x, y), shifted by the
    // calibrated jitter offset, and compares it against the real LR sample.
    double SampleBlockDiff(float[] gt, int gw, int gh, float[] lr, int lw, int scale,
        int x, int y, float offX, float offY)
    {
        double r = 0, g = 0, b = 0;
        for (int j = 0; j < scale; j++)
        {
            for (int i = 0; i < scale; i++)
            {
                float gx = x * scale + offX + 0.5f + i;
                float gy = y * scale + offY + 0.5f + j;
                BilinRgba(gt, gw, gh, gx, gy);
                r += _scratch[0]; g += _scratch[1]; b += _scratch[2];
            }
        }
        double inv = 1.0 / (scale * scale);
        r *= inv; g *= inv; b *= inv;
        int li = (y * lw + x) * 4;
        return Math.Abs(lr[li] - r) + Math.Abs(lr[li + 1] - g) + Math.Abs(lr[li + 2] - b);
    }

    readonly float[] _scratch = new float[4];

    // Bilinear sample of an RGBA float buffer at a continuous coordinate in pixel-index
    // space (pixel p covers [p, p+1], center at p+0.5). Out of range clamps to the edge.
    void BilinRgba(float[] buf, int w, int h, float x, float y)
    {
        float cx = x - 0.5f, cy = y - 0.5f;
        int x0 = (int)Math.Floor(cx); int y0 = (int)Math.Floor(cy);
        float fx = cx - x0, fy = cy - y0;
        if (x0 < 0) { x0 = 0; fx = 0; }
        if (y0 < 0) { y0 = 0; fy = 0; }
        if (x0 > w - 2) { x0 = w - 2; fx = 1; if (x0 < 0) x0 = 0; }
        if (y0 > h - 2) { y0 = h - 2; fy = 1; if (y0 < 0) y0 = 0; }
        int i00 = (y0 * w + x0) * 4;
        int i10 = i00 + 4;
        int i01 = i00 + w * 4;
        int i11 = i01 + 4;
        for (int c = 0; c < 4; c++)
        {
            float v0 = buf[i00 + c] * (1 - fx) + buf[i10 + c] * fx;
            float v1 = buf[i01 + c] * (1 - fx) + buf[i11 + c] * fx;
            _scratch[c] = v0 * (1 - fy) + v1 * fy;
        }
    }

    static float[] DecodeRgbaHalf(byte[] data, int w, int h)
    {
        int px = w * h;
        var f = new float[px * 4];
        for (int i = 0; i < px; i++)
        {
            int o = i * 8;
            for (int c = 0; c < 4; c++)
                f[i * 4 + c] = NssHalfUtils.HalfToFloat((ushort)(data[o + c * 2] | (data[o + c * 2 + 1] << 8)));
        }
        return f;
    }

    static byte[] EncodePng(Color32[] pixels, int w, int h)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
        tex.SetPixels32(pixels);
        tex.Apply(false, false);
        byte[] png = tex.EncodeToPNG();
        UnityEngine.Object.Destroy(tex);
        return png;
    }

    static byte[] BuildMotionViz(NssCaptureConfig cfg, byte[] mv)
    {
        int w = cfg.lrWidth, h = cfg.lrHeight;
        var px = new Color32[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            float vx = NssHalfUtils.HalfToFloat((ushort)(mv[o] | (mv[o + 1] << 8))) * w;
            float vy = NssHalfUtils.HalfToFloat((ushort)(mv[o + 2] | (mv[o + 3] << 8))) * h;
            px[i] = new Color32((byte)(Mathf.Clamp01(Math.Abs(vx) * 0.25f) * 255f),
                               (byte)(Mathf.Clamp01(Math.Abs(vy) * 0.25f) * 255f), 0, 255);
        }
        return EncodePng(px, w, h);
    }

    static byte[] BuildDepthViz(NssCaptureConfig cfg, byte[] depth)
    {
        int w = cfg.lrWidth, h = cfg.lrHeight;
        var px = new Color32[w * h];
        for (int i = 0; i < w * h; i++)
        {
            float d = BitConverter.ToSingle(depth, i * 4);
            byte v = (byte)(Mathf.Clamp01(1f - d / 60f) * 255f);
            px[i] = new Color32(v, v, v, 255);
        }
        return EncodePng(px, w, h);
    }

    static byte[] BuildColorViz(NssCaptureConfig cfg, byte[] color, bool diff)
    {
        int w = cfg.lrWidth, h = cfg.lrHeight;
        var px = new Color32[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 8;
            float r = NssHalfUtils.HalfToFloat((ushort)(color[o] | (color[o + 1] << 8)));
            float g = NssHalfUtils.HalfToFloat((ushort)(color[o + 2] | (color[o + 3] << 8)));
            float b = NssHalfUtils.HalfToFloat((ushort)(color[o + 4] | (color[o + 5] << 8)));
            if (diff)
            {
                px[i] = new Color32((byte)(Mathf.Clamp01(r * 8f) * 255f),
                                   (byte)(Mathf.Clamp01(g * 8f) * 255f),
                                   (byte)(Mathf.Clamp01(b * 8f) * 255f), 255);
            }
            else
            {
                px[i] = new Color32((byte)(Mathf.Pow(Mathf.Clamp01(r), 1f / 2.2f) * 255f),
                                   (byte)(Mathf.Pow(Mathf.Clamp01(g), 1f / 2.2f) * 255f),
                                   (byte)(Mathf.Pow(Mathf.Clamp01(b), 1f / 2.2f) * 255f), 255);
            }
        }
        return EncodePng(px, w, h);
    }

    static byte[] BuildDiffViz(int w, int h, float[] diff)
    {
        var px = new Color32[w * h];
        for (int i = 0; i < w * h; i++)
        {
            byte v = (byte)(Mathf.Clamp01(diff[i] * 8f) * 255f);
            px[i] = new Color32(v, v, v, 255);
        }
        return EncodePng(px, w, h);
    }

    // Session-level case verdicts (manual 23.2 expectations).
    public System.Collections.Generic.List<string> BuildCaseResults(string caseName)
    {
        var results = new System.Collections.Generic.List<string>();
        double mvMeanAvg = MvFrames > 0 ? SumMvMeanPx / MvFrames : 0;
        double activeAvg = MvFrames > 0 ? SumActiveFrac / MvFrames : 0;
        double resampleAvg = ResampleFrames > 0 ? SumResampleDiff / ResampleFrames : -1;
        double signXRatio = SumActiveAbsX > 1e-6 ? SumActiveSignedX / SumActiveAbsX : 0;

        switch (caseName)
        {
            case "Static":
                results.Add($"[Static] mean |MV| after jitter correction = {mvMeanAvg:F4} px (expect < 0.05): {(mvMeanAvg < 0.05 ? "PASS" : "FAIL")}");
                results.Add($"[Static] GT->LR resample mean abs diff = {resampleAvg:F4} (expect < 0.02): {(resampleAvg >= 0 && resampleAvg < 0.02 ? "PASS" : "FAIL")}");
                break;
            case "ObjectMotion":
                results.Add($"[ObjectMotion] active MV fraction = {activeAvg:P2} (expect 0.2%..40%): {(activeAvg > 0.002 && activeAvg < 0.4 ? "PASS" : "FAIL")}");
                results.Add($"[ObjectMotion] mean |MV| = {mvMeanAvg:F3} px");
                break;
            case "CameraOrbit":
                results.Add($"[CameraOrbit] mean |MV| = {mvMeanAvg:F3} px (expect > 0.5): {(mvMeanAvg > 0.5 ? "PASS" : "FAIL")}");
                results.Add($"[CameraOrbit] X direction consistency = {signXRatio:P1} (expect > 70%): {(signXRatio > 0.7 ? "PASS" : "FAIL")}");
                break;
            default:
                results.Add($"[{caseName}] mean |MV| = {mvMeanAvg:F3} px, active fraction = {activeAvg:P2}, resample diff = {resampleAvg:F4} (report only)");
                break;
        }
        results.Add($"NaN/Inf in motion vectors: MaxMv = {MaxMvPx:F2} px, invalid frames = {MvInvalidFrames}");
        results.Add(Calibrated
            ? $"Jitter texture-space calibration: signX={SignX:+0;-0}, signY={SignY:+0;-0} (frame {CalibrationFrame}, diff {CalibrationDiff:F4})"
            : "Jitter texture-space calibration: not calibrated (no resample frames analyzed)");
        return results;
    }
}
