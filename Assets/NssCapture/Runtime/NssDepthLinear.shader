Shader "Hidden/Nss/DepthLinear"
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
            float _Ortho;    // 1 = orthographic projection
            float _ReverseZ;  // 1 = raw buffer is reverse-z (near = 1, far = 0)
            float _Near;
            float _Far;

            float4 frag(v2f_img i) : SV_Target
            {
                float d = tex2D(_MainTex, i.uv).r;
                float eye;
                if (_Ortho > 0.5)
                {
                    eye = (_ReverseZ > 0.5) ? lerp(_Far, _Near, d) : lerp(_Near, _Far, d);
                }
                else
                {
                    eye = (_ReverseZ > 0.5)
                        ? 1.0 / (d * (1.0 / _Near - 1.0 / _Far) + 1.0 / _Far)
                        : 1.0 / ((1.0 - d) / _Near + d / _Far);
                }
                return float4(eye, 0, 0, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
