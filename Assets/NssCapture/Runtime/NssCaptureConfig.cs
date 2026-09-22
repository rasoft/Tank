using UnityEngine;

[CreateAssetMenu(fileName = "NssCaptureConfig", menuName = "NSS Capture/Capture Config")]
public class NssCaptureConfig : ScriptableObject
{
    [Header("Resolutions (must share the same aspect ratio)")]
    public int lrWidth = 960;
    public int lrHeight = 540;
    public int gtWidth = 1920;
    public int gtHeight = 1080;

    [Header("Simulation timeline")]
    [Tooltip("Fixed simulation rate. Time.captureFramerate and Time.fixedDeltaTime are set to this during a session.")]
    public int simulationFps = 60;

    [Header("Jitter (applied to the LR camera only; GT stays unjittered)")]
    public bool enableJitter = true;
    public int haltonBaseX = 2;
    public int haltonBaseY = 3;
    [Tooltip("Jitter magnitude scale, in LR pixels (engine TAA default is 1.0).")]
    public float jitterScale = 1f;
    [Tooltip("Subtract the per-frame jitter delta from captured motion vectors (jitter_removed = true).")]
    public bool removeJitterFromMotionVectors = true;

    [Header("Capture channels")]
    public bool captureMotionVectors = true;
    public bool captureDepth = true;
    public bool captureGt = true;

    [Header("Quality gate")]
    public bool runQualityGate = true;
    public bool writeVisualizationPngs = true;
    public bool enableNanScan = true;

    [Header("Sequences")]
    [Tooltip("Hard cap per sequence; the session continues in a new sequence afterwards.")]
    public int framesPerSequenceCap = 1800;
    public bool autoSplitOnCameraCut = true;
    public float cameraCutPositionThreshold = 2.5f;
    public float cameraCutRotationThresholdDeg = 25f;

    [Header("Motion vectors")]
    [Tooltip("Set MotionVectorGenerationMode.Object on renderers with a Rigidbody or SkinnedMeshRenderer so dynamic objects get per-object motion.")]
    public bool autoSetObjectMotionVectors = true;
    public int objectMotionRescanEveryFrames = 30;

    [Header("Output")]
    [Tooltip("Relative to Application.dataPath. '../NSS_Dataset' puts the dataset at the project root, outside Assets to avoid imports.")]
    public string outputRoot = "../NSS_Dataset";

    public float SimulationDeltaTime => 1f / Mathf.Max(1, simulationFps);
}
