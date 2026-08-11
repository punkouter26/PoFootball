// Procedural football field. Everything a viewer reads as "a stadium pitch" —
// mow stripes, yard lines, hash marks, sidelines, end zones and trodden-in wear —
// is generated in the fragment shader from field coordinates.
//
// WHY A SHADER AND NOT SPRITES. SCN_GAME drew the field as ~24 stretched copies of
// a 1x1 white pixel: nineteen yard lines, two goal lines, two end zones and a turf
// quad. That is 24 transforms to keep in step with Systems_FieldModel, 24 things a
// scene edit can nudge out of alignment, and 24 draw calls for a surface that never
// changes. Here it is one quad, one draw call, and the line positions are derived
// from the same yard constant the simulation uses, so they cannot disagree with
// where the referee thinks the sticks are.
//
// WHY LIT AND NOT UNLIT. The turf is the surface every 2D shadow lands on. An
// unlit field would take the stadium light rig and the players' ShadowCaster2D
// output and simply ignore both. This declares the same three passes as URP's
// Sprite-Lit-Default (Universal2D, NormalsRendering, UniversalForward) and hands
// its procedural albedo to CombinedShapeLightShared, so Light2D treats it exactly
// like any other lit sprite.
//
// COORDINATES. All work happens in yards measured from the centre of the field,
// matching Systems_FieldModel: +Y is the direction the offense attacks, Y = 0 is
// the 50, X = 0 is the middle of the field. Systems_FieldRenderer feeds the half
// extents in so the shader never has to guess the quad's scale.
Shader "PoFootball/Turf"
{
    Properties
    {
        [Header(Field metrics in yards)]
        _HalfLengthYards("Half length (goal line to 50)", Float) = 50
        _HalfWidthYards("Half width", Float) = 26.65
        _EndZoneYards("End zone depth", Float) = 10

        [Header(Grass)]
        _GrassDark("Grass dark", Color) = (0.055, 0.192, 0.086, 1)
        _GrassLight("Grass light", Color) = (0.086, 0.267, 0.118, 1)
        _StripeYards("Mow stripe width", Float) = 5
        _BladeStrength("Blade noise", Range(0, 1)) = 0.35

        [Header(Markings)]
        _LineColor("Line colour", Color) = (0.902, 0.941, 0.910, 1)
        _LineWidthYards("Line width", Float) = 0.14
        _HashWidthYards("Hash mark length", Float) = 0.7
        // NFL hashes are 70'9" from each sideline on a 160' field, which puts
        // them 18'6" apart — 3.08 yards either side of the middle.
        _HashInsetYards("Hash inset from centre", Float) = 3.08

        [Header(End zones)]
        _HomeEndZone("Home end zone", Color) = (0.129, 0.325, 0.545, 1)
        _AwayEndZone("Away end zone", Color) = (0.478, 0.176, 0.153, 1)

        [Header(Wear)]
        _WearColor("Trodden turf", Color) = (0.208, 0.204, 0.145, 1)
        _WearAmount("Wear amount", Range(0, 1)) = 0.45
        _WearCenterY("Wear centre (yards from 50)", Float) = 0
        _WearSpreadYards("Wear spread", Float) = 9

        // Required so a SpriteRenderer can drive this shader. Sprite-Lit-Default
        // declares the same set; leaving any of them out makes the material fail
        // to bind when Unity falls back to the legacy sprite path.
        _MainTex("Diffuse", 2D) = "white" {}
        _MaskTex("Mask", 2D) = "white" {}
        _NormalMap("Normal Map", 2D) = "bump" {}
        [HideInInspector] _Color("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _AlphaTex("External Alpha", 2D) = "white" {}
        [HideInInspector] _EnableExternalAlpha("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        // The whole material lives in one cbuffer, repeated verbatim in every
        // pass. The SRP batcher rejects a material whose UnityPerMaterial layout
        // differs between passes, and the failure is silent — it just stops
        // batching. Keeping this block identical everywhere is load-bearing.
        HLSLINCLUDE
        #define POFOOTBALL_TURF_CBUFFER      \
            half4 _Color;                    \
            half4 _GrassDark;                \
            half4 _GrassLight;               \
            half4 _LineColor;                \
            half4 _HomeEndZone;              \
            half4 _AwayEndZone;              \
            half4 _WearColor;                \
            float _HalfLengthYards;          \
            float _HalfWidthYards;           \
            float _EndZoneYards;             \
            float _StripeYards;              \
            float _BladeStrength;            \
            float _LineWidthYards;           \
            float _HashWidthYards;           \
            float _HashInsetYards;           \
            float _WearAmount;               \
            float _WearCenterY;              \
            float _WearSpreadYards;
        ENDHLSL

        Pass
        {
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"

            #pragma vertex TurfVertex
            #pragma fragment TurfFragment

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/ShapeLightShared.hlsl"

            #pragma multi_compile_instancing
            #pragma multi_compile _ DEBUG_DISPLAY
            #pragma multi_compile _ SKINNED_SPRITE

            struct Attributes
            {
                COMMON_2D_INPUTS
                half4 color : COLOR;
                UNITY_SKINNED_VERTEX_INPUTS
            };

            struct Varyings
            {
                COMMON_2D_LIT_OUTPUTS
                half4 color : COLOR;
            };

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Lit2DCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                POFOOTBALL_TURF_CBUFFER
            CBUFFER_END

            #include "PoFootball_TurfPattern.hlsl"

            Varyings TurfVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);

                Varyings o = CommonLitVertex(input);
                o.color = input.color * _Color * unity_SpriteColor;
                return o;
            }

            half4 TurfFragment(Varyings input) : SV_Target
            {
                half4 albedo = TurfAlbedo(input.uv) * input.color;

                SurfaceData2D surfaceData;
                InputData2D inputData;

                // Flat mask and a straight-up normal: the pattern is paint on a
                // level plane, so any bump here would only fight the stadium rig.
                InitializeSurfaceData(albedo.rgb, albedo.a, half4(1, 1, 1, 1),
                    half3(0, 0, 1), surfaceData);
                InitializeInputData(input.uv, input.lightingUV, inputData);

                return CombinedShapeLightShared(surfaceData, inputData);
            }
            ENDHLSL
        }

        Pass
        {
            Tags { "LightMode" = "NormalsRendering" }

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"

            #pragma vertex NormalsRenderingVertex
            #pragma fragment NormalsRenderingFragment

            #pragma multi_compile_instancing
            #pragma multi_compile _ SKINNED_SPRITE

            struct Attributes
            {
                COMMON_2D_NORMALS_INPUTS
                float4 color : COLOR;
                UNITY_SKINNED_VERTEX_INPUTS
            };

            struct Varyings
            {
                COMMON_2D_NORMALS_OUTPUTS
                half4 color : COLOR;
            };

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Normals2DCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                POFOOTBALL_TURF_CBUFFER
            CBUFFER_END

            Varyings NormalsRenderingVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);

                Varyings o = CommonNormalsVertex(input);
                o.color = input.color * _Color * unity_SpriteColor;
                return o;
            }

            half4 NormalsRenderingFragment(Varyings input) : SV_Target
            {
                SetUpSpriteInstanceProperties();
                return CommonNormalsFragment(input, input.color);
            }
            ENDHLSL
        }

        // Fallback for when no 2D light rig is present — training and the profiler
        // path both land here. Same pattern, no lighting resolve.
        Pass
        {
            Tags { "LightMode" = "UniversalForward" "Queue" = "Transparent" "RenderType" = "Transparent" }

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"

            #pragma vertex UnlitVertex
            #pragma fragment UnlitFragment

            struct Attributes
            {
                COMMON_2D_INPUTS
                half4 color : COLOR;
                UNITY_SKINNED_VERTEX_INPUTS
            };

            struct Varyings
            {
                COMMON_2D_OUTPUTS
                half4 color : COLOR;
            };

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/2DCommon.hlsl"

            #pragma multi_compile_instancing
            #pragma multi_compile _ DEBUG_DISPLAY SKINNED_SPRITE

            CBUFFER_START(UnityPerMaterial)
                POFOOTBALL_TURF_CBUFFER
            CBUFFER_END

            #include "PoFootball_TurfPattern.hlsl"

            Varyings UnlitVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);

                Varyings o = CommonUnlitVertex(input);
                o.color = input.color * _Color * unity_SpriteColor;
                return o;
            }

            half4 UnlitFragment(Varyings input) : SV_Target
            {
                return TurfAlbedo(input.uv) * input.color;
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/2D/Sprite-Lit-Default"
}
