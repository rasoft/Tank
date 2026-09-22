using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Empty renderer feature whose only job is to declare Motion + Depth as pass inputs.
// The Tuanjie URP fork reacts to ScriptableRenderPassInput.Motion by enqueuing its own
// MotionVectorRenderPass (same path TAA/DLSS use), producing _MotionVectorTexture
// (R16G16_SFloat, previous->current, UV space, computed from non-jittered matrices)
// and forcing _CameraDepthTexture allocation.
public class NssMvTriggerFeature : ScriptableRendererFeature
{
    class TriggerPass : ScriptableRenderPass
    {
        public TriggerPass()
        {
            // Late event so the engine picks the copy-depth based motion vector path
            // instead of forcing a depth prepass.
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            ConfigureInput(ScriptableRenderPassInput.Motion | ScriptableRenderPassInput.Depth);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
        }
    }

    TriggerPass _pass;

    public override void Create()
    {
        _pass = new TriggerPass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_pass);
    }
}
