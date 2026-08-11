// The twenty-two players. Takes the flat role shape (a circle, a square, a
// hexagon) and gives it an edge, a body and a state, without changing the
// silhouette the collider is honest about.
//
// WHAT IT ADDS, AND WHY EACH ONE EARNS ITS KEYWORD:
//
//   Outline — a dark rim grown from the sprite's own alpha. Twenty-two flat
//   fills on a green pitch have no separation from each other in a pile-up;
//   an outline is what turns a scrum into countable bodies.
//
//   Bevel — the fill darkens toward the edge and lifts in the middle, so a
//   shape reads as an object standing on the field rather than a decal painted
//   on it. This is the 2D stand-in for the "high-detail mesh" idea: geometry
//   stays flat and cheap, shading does the work.
//
//   Carrier glow — an animated rim on whoever has the ball. Agent_FootballPlayer
//   already swaps the fill to white on possession; that swap is legible but
//   static, and in a crowd the eye loses it. A pulsing rim survives clutter.
//
//   Fatigue — desaturation and a slight darkening. This is the only way a
//   viewer ever sees the fatigue term the simulation has been accumulating
//   from applied force since milestone one.
//
// PER-INSTANCE STATE. Carrier and fatigue arrive through a MaterialPropertyBlock
// (see Systems_PlayerAppearanceView), which is the pattern .claude/rules/
// performance.md prescribes over touching renderer.material. It does cost the
// sprite batcher: twenty-two players become twenty-two draw calls instead of
// one. That is the deliberate trade — twenty-two is not a number worth defending
// against, and the alternative is either a cloned material per player (worse) or
// no per-player state at all.
//
// Team colour is NOT here. It arrives as SpriteRenderer.color through
// unity_SpriteColor, which is genuinely free, and Systems_RoleShapeApplier
// remains its only author.
Shader "PoFootball/Player"
{
    Properties
    {
        _MainTex("Diffuse", 2D) = "white" {}

        [Header(Edge)]
        _OutlineColor("Outline colour", Color) = (0.035, 0.055, 0.043, 1)
        _OutlineWidth("Outline width (alpha units)", Range(0, 0.5)) = 0.22

        [Header(Body)]
        _BevelDepth("Bevel depth", Range(0, 1)) = 0.55
        _BevelLift("Centre lift", Range(0, 1)) = 0.3

        [Header(Per instance state)]
        _Carrier("Carrier amount", Range(0, 1)) = 0
        _CarrierColor("Carrier glow", Color) = (1, 0.937, 0.694, 1)
        _Fatigue("Fatigue", Range(0, 1)) = 0
        _FatigueDesaturation("Fatigue desaturation", Range(0, 1)) = 0.65

        // Sprint narrowing, applied in the VERTEX stage across the body's local
        // X. It must be a vertex offset and not a transform scale: the collider
        // is a child of the same transform, so scaling the GameObject would
        // change the 0.5 m contact radius the tackle rule is calibrated on and
        // every trained .onnx would suddenly be running different physics.
        _Lean("Sprint narrowing", Range(0, 0.35)) = 0

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

        // Identical in every pass — see the note in PoFootball/Turf. A mismatched
        // UnityPerMaterial layout silently drops the material out of the batcher.
        HLSLINCLUDE
        #define POFOOTBALL_PLAYER_CBUFFER   \
            half4 _Color;                   \
            half4 _OutlineColor;            \
            half4 _CarrierColor;            \
            float _OutlineWidth;            \
            float _BevelDepth;              \
            float _BevelLift;               \
            float _Carrier;                 \
            float _Fatigue;                 \
            float _FatigueDesaturation;     \
            float _Lean;

        // Narrows the body across its local X. The rigidbody already rotates the
        // player to face its direction of travel, so local X is "across the
        // shoulders" and this reads as a runner turning side-on into a sprint.
        float3 PoFootballLean(float3 positionOS, float lean)
        {
            return float3(positionOS.x * (1.0 - lean), positionOS.yz);
        }
        ENDHLSL

        Pass
        {
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"

            #pragma vertex PlayerVertex
            #pragma fragment PlayerFragment

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
                POFOOTBALL_PLAYER_CBUFFER
            CBUFFER_END

            #include "PoFootball_PlayerBody.hlsl"

            Varyings PlayerVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);
                input.positionOS = PoFootballLean(input.positionOS, _Lean);

                Varyings o = CommonLitVertex(input);
                o.color = input.color * _Color * unity_SpriteColor;
                return o;
            }

            half4 PlayerFragment(Varyings input) : SV_Target
            {
                half4 body = PlayerBody(input.uv, input.color);
                clip(body.a - 0.003);

                SurfaceData2D surfaceData;
                InputData2D inputData;

                InitializeSurfaceData(body.rgb, body.a, half4(1, 1, 1, 1),
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
                POFOOTBALL_PLAYER_CBUFFER
            CBUFFER_END

            Varyings NormalsRenderingVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);
                input.positionOS = PoFootballLean(input.positionOS, _Lean);

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
                POFOOTBALL_PLAYER_CBUFFER
            CBUFFER_END

            #include "PoFootball_PlayerBody.hlsl"

            Varyings UnlitVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);
                input.positionOS = PoFootballLean(input.positionOS, _Lean);

                Varyings o = CommonUnlitVertex(input);
                o.color = input.color * _Color * unity_SpriteColor;
                return o;
            }

            half4 UnlitFragment(Varyings input) : SV_Target
            {
                return PlayerBody(input.uv, input.color);
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/2D/Sprite-Lit-Default"
}
