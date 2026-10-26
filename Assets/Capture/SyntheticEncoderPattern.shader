Shader "SwaKaar3DIY/Capture/SyntheticEncoderPattern"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always Blend Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers gles3
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            float _SyntheticEncoderFrame;
            struct V { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            V Vert(uint id : SV_VertexID)
            {
                V o;
                o.uv = float2((id << 1) & 2, id & 2);
                o.position = float4(o.uv * 2 - 1, 0, 1);
                return o;
            }
            // 3x5 decimal digits, row-major from TOP left, bit 14 first.
            int Glyph(int n)
            {
                if(n==0) return 31599; if(n==1) return 11415;
                if(n==2) return 29671; if(n==3) return 29647;
                if(n==4) return 23497; if(n==5) return 31183;
                if(n==6) return 31215; if(n==7) return 29257;
                if(n==8) return 31727; return 31695;
            }
            float4 Frag(V i) : SV_Target
            {
                float2 p = float2(i.uv.x, 1-i.uv.y); // Pattern coordinates: TOP-left.
                int f = (int)_SyntheticEncoderFrame;
                float3 color = float3(0.08,0.08,0.08);
                int bar = min((int)(p.x*6),5);
                if(p.y>0.22 && p.y<0.78) {
                    if(bar==0) color=float3(1,0,0); else if(bar==1) color=float3(0,1,0);
                    else if(bar==2) color=float3(0,0,1); else if(bar==3) color=float3(1,1,0);
                    else if(bar==4) color=float3(0,1,1); else color=float3(1,0,1);
                    if(abs(p.x-frac((float)f/150.0))<0.008) color=1;
                }
                // Four unequal markers: top-left red; top-right green;
                // bottom-left blue; bottom-right yellow. No display rotation.
                if(p.x<0.10 && p.y<0.12) color=float3(1,0,0);
                if(p.x>0.86 && p.y<0.10) color=float3(0,1,0);
                if(p.x<0.08 && p.y>0.84) color=float3(0,0,1);
                if(p.x>0.88 && p.y>0.88) color=float3(1,1,0);
                float2 q=(p-float2(0.38,0.025))/float2(0.08,0.032);
                int digit=(int)floor(q.x/4); int col=(int)floor(fmod(max(q.x,0),4));
                int row=(int)floor(q.y);
                if(q.x>=0 && q.y>=0 && digit<3 && col<3 && row<5) {
                    int value=digit==0 ? f/100 : (digit==1 ? (f/10)%10 : f%10);
                    color=((Glyph(value)>>(14-row*3-col))&1)!=0 ? float3(1,1,1) : float3(0,0,0);
                }
                // Eight binary frame-ID cells along the lower edge, LSB left.
                if(p.y>0.92 && p.y<0.97 && p.x>0.20 && p.x<0.76) {
                    int bit=(int)((p.x-0.20)/0.07);
                    color=((f>>bit)&1)!=0 ? float3(1,1,1) : float3(0,0,0);
                }
                return float4(color,1); // Raw RGB code values -> RGBA8 UNorm, no sRGB transform.
            }
            ENDHLSL
        }
    }
    Fallback Off
}
