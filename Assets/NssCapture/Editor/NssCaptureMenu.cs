using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class NssCaptureMenu
{
    const string ScenePath = "Assets/NssCapture/Validation/CaptureValidation.unity";
    const int ValidationFrames = 240;

    [MenuItem("Tools/NSS Capture/Setup Project (Linear + MV Feature + Config)")]
    static void Setup()
    {
        foreach (var line in NssCaptureSetup.EnsureSetup())
            Debug.Log("[NssCapture] " + line);
    }

    [MenuItem("Tools/NSS Capture/Select Config")]
    static void SelectConfig()
    {
        Selection.activeObject = NssCaptureSetup.EnsureConfig();
    }

    [MenuItem("Tools/NSS Capture/Open Dataset Folder")]
    static void OpenDataset()
    {
        string root = NssCaptureSetup.GetOutputRoot();
        if (!System.IO.Directory.Exists(root)) System.IO.Directory.CreateDirectory(root);
        EditorUtility.RevealInFinder(root);
    }

    [MenuItem("Tools/NSS Capture/Session/Start Capture (Current Scene)")]
    static void StartCaptureCurrentScene()
    {
        if (EditorApplication.isPlaying)
        {
            var existing = Object.FindFirstObjectByType<NssCaptureController>();
            if (existing != null && existing.IsRunning)
            {
                Debug.LogWarning("[NssCapture] A capture session is already running.");
                return;
            }
            SpawnController(0);
        }
        else
        {
            SessionState.SetBool(NssCaptureAutomation.PendingRun, true);
            SessionState.SetInt(NssCaptureAutomation.PendingMode, NssCaptureAutomation.ModeCaptureCurrent);
            SessionState.SetInt(NssCaptureAutomation.PendingFrames, 0);
            EditorApplication.isPlaying = true;
        }
    }

    [MenuItem("Tools/NSS Capture/Session/Capture Current Scene (300 frames)")]
    static void CaptureCurrentSceneFramed()
    {
        if (EditorApplication.isPlaying)
        {
            var existing = Object.FindFirstObjectByType<NssCaptureController>();
            if (existing != null && existing.IsRunning)
            {
                Debug.LogWarning("[NssCapture] A capture session is already running.");
                return;
            }
            SpawnController(300);
            return;
        }
        SessionState.SetBool(NssCaptureAutomation.PendingRun, true);
        SessionState.SetInt(NssCaptureAutomation.PendingMode, NssCaptureAutomation.ModeCaptureCurrent);
        SessionState.SetInt(NssCaptureAutomation.PendingFrames, 300);
        EditorApplication.isPlaying = true;
    }

    [MenuItem("Tools/NSS Capture/Session/Stop & Flush")]
    static void StopCapture()
    {
        var ctrl = Object.FindFirstObjectByType<NssCaptureController>();
        if (ctrl == null)
        {
            Debug.LogWarning("[NssCapture] No capture controller found.");
            return;
        }
        ctrl.EndSession();
    }

    [MenuItem("Tools/NSS Capture/Validation/Build Validation Scene")]
    static void BuildValidationScene()
    {
        NssValidationSceneBuilder.Build();
    }

    [MenuItem("Tools/NSS Capture/Validation/Run Case A - Static (240 frames)")]
    static void RunCaseA() => RunValidation(ValidationDirector.Case.Static);

    [MenuItem("Tools/NSS Capture/Validation/Run Case B - Object Motion (240 frames)")]
    static void RunCaseB() => RunValidation(ValidationDirector.Case.ObjectMotion);

    [MenuItem("Tools/NSS Capture/Validation/Run Case C - Camera Orbit (240 frames)")]
    static void RunCaseC() => RunValidation(ValidationDirector.Case.CameraOrbit);

    static void RunValidation(ValidationDirector.Case mode)
    {
        NssCaptureSetup.EnsureSetup();

        if (!System.IO.File.Exists(ScenePath))
            NssValidationSceneBuilder.Build();

        var active = EditorSceneManager.GetActiveScene();
        if (active.path != ScenePath)
        {
            if (active.isDirty)
            {
                EditorUtility.DisplayDialog("NSS Capture",
                    "The current scene has unsaved changes. Save or revert it before running validation (validation opens CaptureValidation.unity).",
                    "OK");
                return;
            }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        // The director's case is applied by NssCaptureAutomation after entering play,
        // so the scene asset itself stays untouched (no dirty scene).

        SessionState.SetBool(NssCaptureAutomation.PendingRun, true);
        SessionState.SetInt(NssCaptureAutomation.PendingMode, NssCaptureAutomation.ModeValidation);
        SessionState.SetInt(NssCaptureAutomation.PendingCase, (int)mode);
        SessionState.SetInt(NssCaptureAutomation.PendingFrames, ValidationFrames);
        EditorApplication.isPlaying = true;
        Debug.Log($"[NssCapture] Validation run queued: {mode}, {ValidationFrames} frames.");
    }

    internal static void SpawnController(long maxFrames)
    {
        var go = new GameObject("NssCaptureController");
        var ctrl = go.AddComponent<NssCaptureController>();
        ctrl.BeginSession(NssCaptureSetup.EnsureConfig(), maxFrames);
    }
}
