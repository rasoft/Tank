using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Builds the minimal validation scene (manual Appendix A): checkerboard ground for
// high-frequency detail, a fence, static cubes, one scripted moving cube, and a
// camera pivot the director can orbit. Everything is deterministic (no physics).
public static class NssValidationSceneBuilder
{
    public const string Dir = "Assets/NssCapture/Validation";
    public const string ScenePath = Dir + "/CaptureValidation.unity";
    const string CheckerPath = Dir + "/CheckerTex.asset";

    // Invoked from NssCaptureMenu (single menu entry point).
    public static void Build()
    {
        Directory.CreateDirectory(Dir);

        // ---- checkerboard texture (32px cells, 512x512, tiled ~128 screen cells on the ground)
        if (File.Exists(CheckerPath)) AssetDatabase.DeleteAsset(CheckerPath);
        var tex = new Texture2D(512, 512, TextureFormat.RGBA32, true);
        var pixels = new Color[512 * 512];
        for (int y = 0; y < 512; y++)
        {
            for (int x = 0; x < 512; x++)
            {
                bool a = ((x / 32) + (y / 32)) % 2 == 0;
                float v = a ? 0.72f : 0.28f;
                pixels[y * 512 + x] = new Color(v, v, v, 1f);
            }
        }
        tex.SetPixels(pixels);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 4;
        tex.Apply(true);
        AssetDatabase.CreateAsset(tex, CheckerPath);

        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        var checkerMat = MakeLitMat(lit, Dir + "/CheckerMat.mat", tex, new Color(1, 1, 1));
        var redMat = MakeLitMat(lit, Dir + "/RedMat.mat", null, new Color(0.85f, 0.12f, 0.10f));
        var blueMat = MakeLitMat(lit, Dir + "/BlueMat.mat", null, new Color(0.15f, 0.30f, 0.85f));
        var greenMat = MakeLitMat(lit, Dir + "/GreenMat.mat", null, new Color(0.15f, 0.75f, 0.25f));
        var grayMat = MakeLitMat(lit, Dir + "/GrayMat.mat", null, new Color(0.60f, 0.60f, 0.60f));
        var fenceMat = MakeLitMat(lit, Dir + "/FenceMat.mat", null, new Color(0.10f, 0.10f, 0.12f));

        // ---- scene
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.shadows = LightShadows.Soft;
        light.intensity = 1.15f;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(6f, 1f, 6f);
        ground.GetComponent<Renderer>().sharedMaterial = checkerMat;

        SpawnCube("CubeBlue", new Vector3(-6f, 0.5f, -4f), 1f, blueMat, Quaternion.Euler(0f, 25f, 0f));
        SpawnCube("CubeGreen", new Vector3(5.5f, 0.75f, -9f), 1.5f, greenMat, Quaternion.identity);
        SpawnCube("CubeGray", new Vector3(0f, 0.5f, 4.5f), 1f, grayMat, Quaternion.Euler(0f, -15f, 0f));

        // High-frequency fence: thin vertical bars (manual 20.3)
        for (int i = 0; i < 25; i++)
        {
            var bar = SpawnCube($"FenceBar_{i:D2}", new Vector3(-6f + i * 0.5f, 0.6f, -2f), 1f, fenceMat, Quaternion.identity);
            bar.transform.localScale = new Vector3(0.07f, 1.2f, 0.07f);
        }

        var moving = SpawnCube("MovingCube", new Vector3(0f, 0.75f, 1.5f), 1.5f, redMat, Quaternion.identity);

        // ---- camera rig: pivot at world origin so Case C can orbit deterministically
        var pivot = new GameObject("CameraPivot");
        pivot.transform.position = Vector3.zero;
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        camGo.transform.SetParent(pivot.transform, false);
        camGo.transform.localPosition = new Vector3(0f, 3.2f, -9f);
        var cam = camGo.AddComponent<Camera>();
        Vector3 lookTarget = new Vector3(0f, 0.8f, 0f);
        cam.transform.rotation = Quaternion.LookRotation(lookTarget - cam.transform.position, Vector3.up);
        cam.fieldOfView = 60f;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 1000f;

        var dirGo = new GameObject("ValidationDirector");
        var director = dirGo.AddComponent<ValidationDirector>();
        director.movingCube = moving.transform;
        director.cameraTransform = camGo.transform;

        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[NssCapture] Validation scene built: {ScenePath}");
    }

    static GameObject SpawnCube(string name, Vector3 pos, float size, Material mat, Quaternion rot)
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.position = pos;
        cube.transform.localScale = Vector3.one * size;
        cube.transform.rotation = rot;
        cube.GetComponent<Renderer>().sharedMaterial = mat;
        return cube;
    }

    static Material MakeLitMat(Shader shader, string path, Texture map, Color color)
    {
        if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
        var mat = new Material(shader);
        mat.SetColor("_BaseColor", color);
        if (map != null) mat.SetTexture("_BaseMap", map);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}
