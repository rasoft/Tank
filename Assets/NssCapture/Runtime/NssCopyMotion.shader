Shader "Hidden/Nss/CopyMotion"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
    }
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _JitterDelta; // UV-space constant subtracted from every sample

            float4 frag(v2f_img i) : SV_Target
            {
                float2 mv = tex2D(_MainTex, i.uv).xy - _JitterDelta.xy;
                return float4(mv, 0, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
