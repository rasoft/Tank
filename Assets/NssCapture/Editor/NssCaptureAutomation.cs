using UnityEditor;
using UnityEngine;

// Play-mode automation: consumes the menu's SessionState intent, spawns the capture
// controller right after entering play, and exits play when a framed run completes.
public static class NssCaptureAutomation
{
    public const string PendingRun = "Nss.PendingRun";
    public const string PendingMode = "Nss.PendingMode";
    public const string PendingCase = "Nss.PendingCase";
    public const string PendingFrames = "Nss.PendingFrames";

    public const int ModeValidation = 1;
    public const int ModeCaptureCurrent = 2;

    static long _watchMaxFrames;

    [InitializeOnLoadMethod]
    static void Init()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode) return;
        if (!SessionState.GetBool(PendingRun, false)) return;
        SessionState.SetBool(PendingRun, false); // intent consumed

        int mode = SessionState.GetInt(PendingMode, ModeCaptureCurrent);
        long frames = SessionState.GetInt(PendingFrames, 0);

        if (mode == ModeValidation)
        {
            var director = Object.FindFirstObjectByType<ValidationDirector>();
            if (director != null)
                director.mode = (ValidationDirector.Case)SessionState.GetInt(PendingCase, 0);
        }

        var go = new GameObject("NssCaptureController");
        var ctrl = go.AddComponent<NssCaptureController>();
        ctrl.BeginSession(NssCaptureSetup.EnsureConfig(), frames);

        if (frames > 0)
        {
            _watchMaxFrames = frames;
            EditorApplication.update += WatchFramedSession;
        }
    }

    static void WatchFramedSession()
    {
        var ctrl = Object.FindFirstObjectByType<NssCaptureController>();
        if (ctrl == null || ctrl.SessionDone)
        {
            EditorApplication.update -= WatchFramedSession;
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false; // framed runs end themselves
        }
    }
}
