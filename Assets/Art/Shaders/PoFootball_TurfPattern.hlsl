#ifndef POFOOTBALL_TURF_PATTERN_INCLUDED
#define POFOOTBALL_TURF_PATTERN_INCLUDED

// The field pattern itself, shared by the lit and unlit passes of
// PoFootball/Turf so the two can never drift apart.
//
// Everything is computed in YARDS FROM THE CENTRE OF THE FIELD, matching
// Systems_FieldModel exactly: +Y is the direction the offense attacks, Y = 0 is
// the 50 yard line, X = 0 is the middle of the field between the hashes. The
// caller supplies the half extents through the material, so the quad can be
// scaled without the markings sliding off it.
//
// Every edge is antialiased against fwidth rather than a fixed epsilon. The
// broadcast camera changes orthographic size mid-play (Systems_BroadcastCameraView),
// so a line that looked crisp at one zoom would crawl or disappear at another.

// --- Small helpers --------------------------------------------------------

// 1 inside a band of half-width `halfWidth` around zero, 0 outside, with one
// pixel of gradient at the edge. `gradient` is the screen-space derivative of
// whatever coordinate `distance` was measured in.
float TurfBand(float distance, float halfWidth, float gradient)
{
    float aa = max(gradient, 1e-5);
    return 1.0 - smoothstep(halfWidth - aa, halfWidth + aa, distance);
}

// Cheap value noise. Two octaves is enough to break up flat colour into
// something that reads as cut grass without looking like television static.
float TurfHash(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453123);
}

float TurfValueNoise(float2 p)
{
    float2 cell = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);

    float a = TurfHash(cell);
    float b = TurfHash(cell + float2(1.0, 0.0));
    float c = TurfHash(cell + float2(0.0, 1.0));
    float d = TurfHash(cell + float2(1.0, 1.0));

    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// --- Yard numbers ---------------------------------------------------------
//
// Digits are 3x5 bitmaps packed into an int, decoded per fragment. A glyph
// texture would be the usual answer, but it would mean a second sampler, an
// atlas to keep in sync and an import setting to get wrong — for ten shapes
// that are five rows tall. The blocky result also sits correctly alongside the
// flat geometric players rather than fighting them.
//
// Bit index is row * 3 + column, row 0 at the top, column 0 on the left.
int TurfDigitMask(int digit)
{
    if (digit == 0) { return 31599; } // 111 101 101 101 111
    if (digit == 1) { return 29850; } // 010 110 010 010 111
    if (digit == 2) { return 29671; } // 111 001 111 100 111
    if (digit == 3) { return 31207; } // 111 001 111 001 111
    if (digit == 4) { return 18925; } // 101 101 111 001 001
    return 31183;                     // 5: 111 100 111 001 111
}

// Coverage of one digit inside a local box running 0..3 in x and 0..5 in y.
float TurfDigitCoverage(int digit, float2 cell)
{
    if (cell.x < 0.0 || cell.x >= 3.0 || cell.y < 0.0 || cell.y >= 5.0)
    {
        return 0.0;
    }

    int column = (int)floor(cell.x);
    int row = 4 - (int)floor(cell.y); // cell.y counts up, the bitmap counts down

    int mask = TurfDigitMask(digit);
    int bit = row * 3 + column;

    return (((mask >> bit) & 1) != 0) ? 1.0 : 0.0;
}

// Coverage of the two-digit label belonging to the nearest ten-yard line.
// `local` is in yards relative to the centre of the label.
float TurfNumberCoverage(float2 local, int value, float glyphYards)
{
    // 3 wide + 1 gap + 3 wide = 7 cells across, 5 cells tall.
    float2 cell = float2(local.x / glyphYards + 3.5, local.y / glyphYards + 2.5);

    int tens = value / 10;
    int ones = value - tens * 10;

    float coverage = TurfDigitCoverage(tens, float2(cell.x, cell.y));
    coverage += TurfDigitCoverage(ones, float2(cell.x - 4.0, cell.y));

    return saturate(coverage);
}

// --- The field ------------------------------------------------------------

half4 TurfAlbedo(float2 uv)
{
    // Quad UV to yards from centre. The quad spans both end zones.
    float halfTotal = _HalfLengthYards + _EndZoneYards;
    float2 yards = float2((uv.x - 0.5) * 2.0 * _HalfWidthYards,
                          (uv.y - 0.5) * 2.0 * halfTotal);

    float gradientX = fwidth(yards.x);
    float gradientY = fwidth(yards.y);

    bool inEndZone = abs(yards.y) > _HalfLengthYards;

    // --- Grass base: mow stripes banded across the width of the field ------
    float stripePhase = floor(yards.y / max(_StripeYards, 0.01));
    float stripe = fmod(abs(stripePhase), 2.0);

    half3 grass = lerp(_GrassDark.rgb, _GrassLight.rgb, stripe);

    // Two octaves of blade noise. The first is per-blade speckle, the second a
    // slow patchiness so the pitch is not uniformly perfect.
    float blades = TurfValueNoise(yards * 6.0) * 0.65
                 + TurfValueNoise(yards * 0.7) * 0.35;
    grass *= 1.0 + (blades - 0.5) * _BladeStrength;

    // --- End zone paint ----------------------------------------------------
    if (inEndZone)
    {
        half3 paint = yards.y > 0.0 ? _AwayEndZone.rgb : _HomeEndZone.rgb;

        // Keep the mow stripes faintly visible through the paint — a painted
        // end zone is still grass, and a flat fill reads as a hole in the pitch.
        grass = lerp(paint, grass * 0.55 + paint * 0.65, 0.35);
    }

    // --- Wear: trodden turf around the line of scrimmage and up the middle --
    float scrimmageWear = exp(-pow((yards.y - _WearCenterY) / max(_WearSpreadYards, 0.01), 2.0));

    // Deliberately weak. A wide band up the middle of the field is where every
    // play happens, so it is also where every player is standing — overdo it and
    // the twenty-two shapes the viewer is meant to be reading end up sitting on
    // the darkest part of the pitch.
    float middleWear = exp(-pow(yards.x / 11.0, 2.0)) * 0.28;
    float wear = saturate((scrimmageWear + middleWear) * _WearAmount)
               * saturate(0.35 + blades);

    grass = lerp(grass, _WearColor.rgb, wear * (inEndZone ? 0.35 : 1.0));

    // --- Markings ----------------------------------------------------------
    float halfLine = _LineWidthYards * 0.5;
    float paintCoverage = 0.0;

    // Yard lines every five yards, goal lines included, but never inside an
    // end zone. Distance to the nearest multiple of five.
    float toNearestFive = abs(yards.y - round(yards.y / 5.0) * 5.0);
    float onPlayingField = abs(yards.y) <= _HalfLengthYards + halfLine ? 1.0 : 0.0;
    paintCoverage = max(paintCoverage,
        TurfBand(toNearestFive, halfLine, gradientY) * onPlayingField);

    // Back line of each end zone.
    float toBackLine = abs(abs(yards.y) - halfTotal);
    paintCoverage = max(paintCoverage, TurfBand(toBackLine, halfLine, gradientY));

    // Sidelines, drawn just inside the edge of the quad so the boundary the
    // referee enforces and the boundary the viewer sees are the same line.
    float toSideline = abs(abs(yards.x) - _HalfWidthYards);
    paintCoverage = max(paintCoverage, TurfBand(toSideline, halfLine, gradientX));

    // Hash marks: a one-yard ladder at each hash, between the goal lines only.
    if (!inEndZone)
    {
        float toHashColumn = abs(abs(yards.x) - _HashInsetYards);
        float toYardTick = abs(yards.y - round(yards.y));

        float hash = TurfBand(toHashColumn, _HashWidthYards * 0.5, gradientX)
                   * TurfBand(toYardTick, halfLine, gradientY);

        // Suppress the tick where it would sit on top of a five-yard line —
        // on a real field the line wins and the hash is absorbed into it.
        hash *= step(halfLine * 2.5, toNearestFive);

        paintCoverage = max(paintCoverage, hash);

        // Yard numbers, two columns inset from each sideline.
        float toNearestTen = yards.y - round(yards.y / 10.0) * 10.0;
        int label = 50 - (int)abs(round(yards.y / 10.0) * 10.0);

        if (label >= 10)
        {
            // Yards per bitmap cell. Five cells tall makes the digit 2.25 yd,
            // close to the six-foot numbers a real field is painted with.
            float glyph = 0.45;
            float inset = _HalfWidthYards - 9.0;

            float2 leftLocal = float2(yards.x + inset, toNearestTen);
            float2 rightLocal = float2(yards.x - inset, toNearestTen);

            float numbers = max(TurfNumberCoverage(leftLocal, label, glyph),
                                TurfNumberCoverage(rightLocal, label, glyph));

            paintCoverage = max(paintCoverage, numbers);
        }
    }

    // Paint sits on grass, so it picks up a little of the wear underneath it
    // rather than staying showroom white all game.
    half3 paintColor = _LineColor.rgb * (1.0 - wear * 0.4);
    half3 albedo = lerp(grass, paintColor, paintCoverage);

    return half4(albedo, 1.0);
}

#endif // POFOOTBALL_TURF_PATTERN_INCLUDED
