using System;
using UnityEngine;

// Serializable JSON document types (frame metadata / dataset spec / sequence / report).
// Matrices are stored in Unity's column-major CPU convention; the dataset spec states this.
[Serializable]
public class NssCameraMetadata
{
    public float near;
    public float far;
    public float fov_y;
    public float orthographic_size;
    public string projection_type; // "perspective" | "orthographic"
    public bool camera_cut;

    public float[] view_matrix = new float[16];
    public float[] projection_jittered = new float[16];
    public float[] projection_unjittered = new float[16];
    public float[] view_projection_unjittered = new float[16];

    public static float[] FromMatrix(Matrix4x4 m)
    {
        var a = new float[16];
        for (int i = 0; i < 16; i++) a[i] = m[i];
        return a;
    }
}

[Serializable]
public class NssRenderMetadata
{
    public string color_space;       // project color space of the captured backbuffer
    public bool hdr;                 // source RT is float16
    public float exposure;          // fixed 1.0 (post processing disabled)
    public bool tone_mapping_applied;
    public bool ui_composited;       // false: screen-space overlay UI never renders into capture cameras
}

[Serializable]
public class NssFrameQa
{
    public bool motion_vector_invalid; // first frame of a sequence / after a camera cut
    public int nan_count;
    public int inf_count;
    public float mv_mean_pixels;
    public float mv_max_pixels;
    public float mv_active_fraction; // fraction of samples with |mv| > 0.5 LR px
    public float gt_lr_resample_mean_abs_diff = -1f;
}

[Serializable]
public class NssFrameMetadata
{
    public long frame_id;
    public int sequence_id;
    public int frame_in_sequence;
    public float simulation_timestamp; // frame_id / simulation_fps (authoritative pairing: use frame_id)

    public int[] lr_resolution = new int[2];
    public int[] gt_resolution = new int[2];

    public float[] jitter = new float[2];
    public string jitter_unit = "lr_pixel";

    public NssCameraMetadata camera = new NssCameraMetadata();
    public NssRenderMetadata render = new NssRenderMetadata();
    public NssFrameQa qa = new NssFrameQa();
}

[Serializable]
public class NssSequenceJson
{
    public int sequence_id;
    public long first_frame_id;
    public long last_frame_id;
    public int frame_count;
    public float sim_start;
    public float sim_end;
    public long[] camera_cut_frames = new long[0];
    public int nan_count;
    public int inf_count;
    public string notes = "";
}

[Serializable]
public class NssDatasetSpecJson
{
    public string engine;
    public string render_pipeline;
    public string[] lr_resolution = new string[2];
    public int[] lr_resolution_px = new int[2];
    public int[] gt_resolution_px = new int[2];
    public float simulation_fps;
    public string timestamp_rule;
    public NssMotionVectorSpec motion_vector = new NssMotionVectorSpec();
    public NssDepthSpec depth = new NssDepthSpec();
    public NssJitterSpec jitter = new NssJitterSpec();
    public NssRenderSpec render = new NssRenderSpec();
    public string matrix_convention;
}

[Serializable]
public class NssMotionVectorSpec
{
    public string resolution;
    public string direction;        // previous_to_current
    public string unit;             // uv (value = currUV - prevUV; pixels = value * resolution)
    public string origin;           // top_left (texture space, verified via validation cases)
    public string y_axis;           // down (texture space)
    public bool camera_motion;
    public bool dynamic_object_motion;
    public string dynamic_object_note;
    public bool jitter_removed;
    public string jitter_removed_note;
    public float[] invalid_value = new float[2];
    public string invalid_note;
    public string source;           // engine MotionVectorRenderPass (Tuanjie URP fork)
}

[Serializable]
public class NssDepthSpec
{
    public string type;       // linear_eye_depth
    public string unit;       // meter
    public string range_note; // near..far from per-frame metadata; sky pixels land near 'far'
    public bool reverse_z_raw_buffer; // raw GPU depth before linearization (this build uses Reverse-Z)
    public string projection_type_note;
    public string formula;
}

[Serializable]
public class NssJitterSpec
{
    public string unit;            // lr_pixel
    public bool applied_to_lr;     // LR jittered, GT unjittered
    public string sequence;        // halton(2,3)-0.5, scale 1.0, index (frame&1023)+1
    public string application;     // clip-space translation on CPU projection matrix (pre GPU flip)
    public string texture_space_x; // calibrated effective direction in texture space (from GT/LR resample calibration)
    public string texture_space_y;
}

[Serializable]
public class NssRenderSpec
{
    public string color_space;
    public bool hdr;
    public string color_format;      // RGB16F EXR
    public string depth_format;     // R32F EXR
    public string motion_format;    // RG16F EXR
    public float exposure;
    public bool tone_mapping_applied;
    public bool ui_composited;
}

[Serializable]
public class NssReportJson
{
    public string session_started;
    public long frames_captured;
    public long frames_written;
    public long frames_dropped;
    public int sequence_count;
    public string jitter_calibration;
    public float gt_lr_resample_mean_abs_diff;
    public string[] issues = new string[0];
    public string[] case_results = new string[0];
    public string[] per_sequence = new string[0];
}
