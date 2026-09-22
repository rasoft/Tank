using UnityEngine;

// Drives the minimal validation scene (manual Appendix A) deterministically from
// simulation time, so validation cases exercise exactly one variable each.
public class ValidationDirector : MonoBehaviour
{
    public enum Case
    {
        Static,       // A: camera and objects static (jitter/MV-zero test)
        ObjectMotion, // B: one cube moves horizontally
        CameraOrbit   // C: camera yaws in place, scene static (manual 23.2 Case C)
    }

    public Case mode = Case.Static;
    public Transform movingCube;
    public Transform cameraTransform;

    Quaternion _cameraBaseRotation;

    void Awake()
    {
        if (movingCube != null)
        {
            foreach (var r in movingCube.GetComponentsInChildren<Renderer>())
                r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
        }
        if (cameraTransform != null) _cameraBaseRotation = cameraTransform.rotation;
    }

    void Update()
    {
        float t = Time.time;
        switch (mode)
        {
            case Case.ObjectMotion:
                if (movingCube != null)
                    movingCube.position = new Vector3(Mathf.Sin(2f * Mathf.PI * t / 2f) * 5f, 0.75f, 1.5f);
                break;
            case Case.CameraOrbit:
                if (cameraTransform != null)
                    cameraTransform.rotation = Quaternion.Euler(0f, 8f * Mathf.Sin(2f * Mathf.PI * t / 5f), 0f) * _cameraBaseRotation;
                break;
        }
    }
}
