#ifndef POFOOTBALL_PLAYER_BODY_INCLUDED
#define POFOOTBALL_PLAYER_BODY_INCLUDED

// Shared body shading for PoFootball/Player, used by both the lit and unlit
// passes so the two cannot drift.
//
// HOW THE EDGE IS FOUND. The role shapes are solid silhouettes with a hard
// alpha edge and no distance field, so there is nothing to read "how far am I
// from the rim" out of directly. Blurring the alpha over a small ring gives one
// for free: the average is 1.0 deep inside the shape, falls through 0.5 exactly
// at the edge, and reaches 0 outside. Everything below — outline, bevel, rim
// glow — is a threshold on that one number.
//
// THE OUTLINE GROWS INWARD. It is drawn inside the existing silhouette, never
// outside it. Systems_RoleShapeApplier is explicit that a player's sprite is
// exactly as big as its 0.5 m collider so the picture never lies about where
// contact happens; an outline grown outward would quietly add a few centimetres
// of visual body that nothing can be tackled by.

// Ring offsets, unit length. Eight directions is enough for shapes this simple —
// a circle, a square, a hexagon — and keeps this to seventeen taps.
static const float2 POFOOTBALL_RING[8] =
{
    float2( 1.0,  0.0), float2( 0.7071,  0.7071),
    float2( 0.0,  1.0), float2(-0.7071,  0.7071),
    float2(-1.0,  0.0), float2(-0.7071, -0.7071),
    float2( 0.0, -1.0), float2( 0.7071, -0.7071)
};

// Alpha of one tap, treating anything outside the sprite's UV rectangle as
// empty.
//
// THIS IS LOAD-BEARING FOR THE SQUARE. A circle or a hexagon has transparent
// corners, so a blur naturally finds its edge somewhere inside the texture. The
// lineman's square fills its 256 px source right to the border, so every tap
// lands on alpha 1, the clamped wrap mode returns alpha 1 outside as well, and
// the shape reads as having no edge at all — perfectly flat, exactly like the
// four other role shapes it is supposed to be distinguishable from. Counting
// out-of-bounds as empty gives the square the same silhouette every other shape
// gets for free.
float PlayerTapAlpha(float2 uv)
{
    float inside = (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0) ? 1.0 : 0.0;
    return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, saturate(uv)).a * inside;
}

// Average alpha over two concentric rings plus the centre tap.
float PlayerEdgeCoverage(float2 uv, float radius)
{
    float total = PlayerTapAlpha(uv);
    float weight = 1.0;

    [unroll]
    for (int index = 0; index < 8; index++)
    {
        float2 direction = POFOOTBALL_RING[index];

        total += PlayerTapAlpha(uv + direction * radius);
        total += PlayerTapAlpha(uv + direction * radius * 0.55) * 1.5;
        weight += 2.5;
    }

    return total / weight;
}

half3 PlayerDesaturate(half3 color, float amount)
{
    // Rec. 709 luma. A flat average would turn the blue offense and the red
    // defense into two different greys, which is the one thing the tint exists
    // to prevent.
    float luma = dot(color, half3(0.2126, 0.7152, 0.0722));
    return lerp(color, luma.xxx, amount);
}

half4 PlayerBody(float2 uv, half4 tint)
{
    float alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).a;

    // UV-relative rather than texel-relative, so all eight role shapes get a
    // proportionally identical rim regardless of their import resolution.
    //
    // The 0.32 is set against how big a player actually is on screen, not
    // against the source art. A body is a 1 m circle on a 110 m field in a
    // portrait window — roughly thirty pixels across. An outline sized as a
    // sensible fraction of a 256 px sprite lands at half a pixel and simply is
    // not there, which is what the first version of this shader shipped.
    float radius = _OutlineWidth * 0.32;
    float coverage = PlayerEdgeCoverage(uv, radius);

    // 0 at the silhouette edge, 1 well inside it.
    float inner = smoothstep(0.45, 0.92, coverage);

    // --- Fill ---------------------------------------------------------------
    half3 fill = tint.rgb;
    fill = PlayerDesaturate(fill, saturate(_Fatigue) * _FatigueDesaturation);

    // Fatigue also drops the value a little. Desaturation alone reads as a
    // lighting change; darkening is what makes it read as effort.
    fill *= 1.0 - saturate(_Fatigue) * 0.25;

    // Bevel: dark at the rim, lifted through the middle. The lift is applied
    // off-centre in Y so the shading implies a light from above the field,
    // matching the stadium rig's downward bias.
    float shoulder = smoothstep(0.35, 1.0, coverage + (uv.y - 0.5) * 0.35);
    float shade = lerp(1.0 - _BevelDepth, 1.0 + _BevelLift, shoulder);
    fill *= shade;

    // --- Outline ------------------------------------------------------------
    float outline = saturate(1.0 - inner);
    half3 body = lerp(fill, _OutlineColor.rgb, outline * _OutlineColor.a);

    // --- Carrier rim --------------------------------------------------------
    // A band sitting just inside the outline rather than on top of it, so the
    // glow reads as light coming off the body instead of a recoloured edge.
    float carrier = saturate(_Carrier);
    if (carrier > 0.0)
    {
        float band = smoothstep(0.30, 0.62, coverage) * (1.0 - smoothstep(0.62, 0.95, coverage));

        // Two-rate pulse. A single sine is a slow throb that the eye stops
        // tracking; a faster component on top keeps it alive in a crowd.
        float pulse = 0.62
                    + 0.26 * sin(_Time.y * 7.5)
                    + 0.12 * sin(_Time.y * 19.0);

        body += _CarrierColor.rgb * band * pulse * carrier * 1.35;
    }

    return half4(body, alpha * tint.a);
}

#endif // POFOOTBALL_PLAYER_BODY_INCLUDED
