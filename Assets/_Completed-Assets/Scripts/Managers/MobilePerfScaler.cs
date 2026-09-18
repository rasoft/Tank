using UnityEngine;

namespace Complete
{
    public class MobilePerfScaler : MonoBehaviour
    {
        // The project ships with desktop-grade quality settings; on low-end mobile GPUs
        // the biggest cost is fragment fill at native resolution, so render at 70%.
        private void Awake()
        {
            if (Application.isMobilePlatform)
            {
                Screen.SetResolution(Mathf.RoundToInt(Screen.currentResolution.width * 0.7f),
                                     Mathf.RoundToInt(Screen.currentResolution.height * 0.7f),
                                     true);

                // Android defaults to a 30 FPS target; raise it so optimizations can show.
                Application.targetFrameRate = 60;
            }
        }
    }
}