Shader "SwaKaar3DIY/Capture/CleanCameraCopy"
{
    Properties { _MainTex("ARCore External Camera", 2D) = "black" {} }
    SubShader
    {
        Pass
        {
            Cull Off
            ZTest Always
            ZWrite Off
            Blend Off
            GLSLPROGRAM
            #pragma only_renderers gles3
            #include "UnityCG.glslinc"
            #extension GL_OES_EGL_image_external_essl3 : require
            // Output UV -> source UV: (u, v) -> (u, 1-v). One V reflection, full coverage.
            // No display matrix, aspect-fill crop or device rotation. Device verification pending.
            #ifdef VERTEX
            varying vec2 cameraUV;
            void main()
            {
                gl_Position = gl_ModelViewProjectionMatrix * gl_Vertex;
                cameraUV = vec2(gl_MultiTexCoord0.x, 1.0 - gl_MultiTexCoord0.y);
            }
            #endif
            #ifdef FRAGMENT
            varying vec2 cameraUV;
            uniform samplerExternalOES _MainTex;
            void main()
            {
                // Preserve provider RGB code values in a non-sRGB UNorm target. In particular,
                // do not apply ARCoreBackground's linear-project gamma-to-linear conversion.
                gl_FragColor = vec4(texture(_MainTex, cameraUV).rgb, 1.0);
            }
            #endif
            ENDGLSL
        }
    }
    Fallback Off
}
