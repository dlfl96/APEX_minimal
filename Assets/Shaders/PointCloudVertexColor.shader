Shader "Custom/PointCloudVertexColor"
{
    // Minimaler URP-Unlit-Shader mit Vertex-Farben. Wird fuer die Punktwolken-
    // Oberflaeche (MeshTopology.Triangles) verwendet - kein Geometry-Shader, keine
    // Beleuchtung -> guenstigste Last, ideal fuers 8-View-Rendering auf Intel-iGPU.
    // Nutzt TransformObjectToHClip -> die GameObject-Transform (Position/Skalierung)
    // wirkt, damit die Wolke an/um die Display-Fokusebene platziert werden kann.
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Cull Off   // Flaeche aus allen Blickwinkeln sichtbar (Multiview-Display)
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes {
                float4 positionOS : POSITION;
                half4  color      : COLOR;
            };
            struct Varyings {
                float4 positionHCS : SV_POSITION;
                half4  color       : COLOR;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Kamerafarben sind sRGB. Unity rendert in Linear und wandelt am Ende
                // linear->sRGB fuers Display. Ohne Umrechnung wuerden die Farben blass
                // wirken -> hier sRGB->Linear, damit die Anzeige die Originalfarbe zeigt.
                half3 c = pow(saturate(IN.color.rgb), 2.2);
                return half4(c, 1.0);
            }
            ENDHLSL
        }
    }
}
