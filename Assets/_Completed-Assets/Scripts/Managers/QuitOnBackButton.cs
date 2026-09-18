using UnityEngine;

namespace Complete
{
    public class QuitOnBackButton : MonoBehaviour
    {
        // On Android the system back button is delivered as KeyCode.Escape.
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Application.Quit();
            }
        }
    }
}