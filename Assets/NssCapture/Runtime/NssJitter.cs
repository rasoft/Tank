using UnityEngine;

// Jitter utilities replicating the engine's TAA jitter convention (URP TemporalUtils):
// Halton(baseX, baseY) - 0.5 gives sub-pixel offsets in LR pixels, applied as a
// clip-space translation on the CPU-side projection matrix.
public static class NssJitter
{
    public static float Halton(int index, int radix)
    {
        float f = 1f, r = 0f;
        while (index > 0)
        {
            f /= radix;
            r += f * (index % radix);
            index /= radix;
        }
        return r;
    }

    // Deterministic per-frame jitter in LR pixels, mirroring TemporalUtils.CalculateJitter
    // (index = (frameIndex & 1023) + 1 avoids the unstable Halton(0)).
    public static Vector2 Get(int frameIndex, int baseX, int baseY, float scale)
    {
        int i = (frameIndex & 1023) + 1;
        return new Vector2(Halton(i, baseX) - 0.5f, Halton(i, baseY) - 0.5f) * scale;
    }

    public static Matrix4x4 Apply(Matrix4x4 projection, Vector2 jitterLrPixels, int width, int height)
    {
        // Clip-space translation: NDC offset = (2 * jx / w, 2 * jy / h), same as the engine's
        // jitterMat = Matrix4x4.Translate(offset) multiplied in front of the projection.
        Vector3 offset = new Vector3(jitterLrPixels.x * 2f / width, jitterLrPixels.y * 2f / height, 0f);
        return Matrix4x4.Translate(offset) * projection;
    }
}
