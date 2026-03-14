#version 450 core

in vec3  vWorldPos;
in vec2  vTexCoord;
in float vSunLight;
in float vBlockLight;
in float vAo;
in float vViewDist;
in vec4  vShadowCoord;

out vec4 FragColor;

uniform sampler2D uShadowMap;
uniform float uTime;
uniform float uFogStart;
uniform float uFogEnd;
uniform vec3  uCameraPos;

// ---------------------------------------------------------------------------
// Color palette
// ---------------------------------------------------------------------------
const vec3  DeepColor    = vec3(0.05, 0.18, 0.38);  // deep blue
const vec3  ShallowColor = vec3(0.10, 0.40, 0.55);  // teal
const vec3  SurfaceColor = vec3(0.15, 0.55, 0.65);  // bright surface
const vec3  FoamColor    = vec3(0.85, 0.92, 0.95);  // white foam
const vec3  SunColor     = vec3(0.95, 0.98, 1.00);
const vec3  BlockColor   = vec3(1.00, 0.68, 0.26);
const vec3  FogColor     = vec3(0.58, 0.72, 0.86);
const float Ambient      = 0.042;
const float WaterAlpha   = 0.65;

// ---------------------------------------------------------------------------
// Noise helpers - hash-based, no textures needed
// ---------------------------------------------------------------------------

// Simple 2D hash -> [0,1]
float hash21(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

// Value noise with smooth interpolation
float valueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f); // smoothstep

    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));

    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// Fractal Brownian Motion - 4 octaves
float fbm(vec2 p)
{
    float v = 0.0;
    float a = 0.5;
    vec2 shift = vec2(100.0);
    mat2 rot = mat2(cos(0.5), sin(0.5), -sin(0.5), cos(0.5));
    for (int i = 0; i < 4; i++)
    {
        v += a * valueNoise(p);
        p = rot * p * 2.0 + shift;
        a *= 0.5;
    }
    return v;
}

// ---------------------------------------------------------------------------
// Foam - driven by per-vertex shore proximity in vTexCoord.x
// ---------------------------------------------------------------------------

float shoreFoam(vec2 worldXZ, float shoreProximity, float time)
{
    if (shoreProximity < 0.01) return 0.0;

    // Animated bubbly foam pattern in world coords
    vec2 uv1 = worldXZ * 2.0 + vec2(time * 0.18, time * 0.1);
    vec2 uv2 = worldXZ * 3.2 + vec2(-time * 0.14, time * 0.22);
    float n1 = fbm(uv1);
    float n2 = fbm(uv2);
    float pattern = smoothstep(0.15, 0.55, n1 * 0.6 + n2 * 0.4);

    // Foam strength fades with distance from shore (interpolated vertex value)
    return shoreProximity * pattern;
}

// ---------------------------------------------------------------------------
// Caustics-like pattern on the surface
// ---------------------------------------------------------------------------

float caustics(vec2 p, float time)
{
    vec2 uv = p * 2.0;
    float c = 0.0;
    for (int i = 0; i < 3; i++)
    {
        float fi = float(i);
        vec2 animUv = uv + vec2(time * (0.1 + fi * 0.05), time * (0.08 - fi * 0.03));
        float n = valueNoise(animUv * (3.0 + fi));
        c += n;
    }
    c /= 3.0;
    // Sharpen into bright spots
    c = pow(c, 1.5) * 0.6;
    return c;
}

// ---------------------------------------------------------------------------
// Shadow (same Poisson PCF as main shader)
// ---------------------------------------------------------------------------

float percToLinear(float v) { return v * v; }

float computeShadow()
{
    const vec2 disk[16] = vec2[](
        vec2(-0.9420, -0.3991), vec2( 0.9456, -0.7689),
        vec2(-0.0942, -0.9294), vec2( 0.3450,  0.2939),
        vec2(-0.9159,  0.4577), vec2(-0.8154, -0.8791),
        vec2(-0.3828,  0.2768), vec2( 0.9748,  0.7565),
        vec2( 0.4432, -0.9751), vec2( 0.5374, -0.4737),
        vec2(-0.2650, -0.4189), vec2( 0.7920,  0.1909),
        vec2(-0.2419,  0.9971), vec2(-0.8141,  0.9144),
        vec2( 0.1998,  0.7864), vec2( 0.1438, -0.1410)
    );

    vec3 sc = vShadowCoord.xyz / vShadowCoord.w;
    sc = sc * 0.5 + 0.5;

    if (sc.x < 0.0 || sc.x > 1.0 || sc.y < 0.0 || sc.y > 1.0 || sc.z < 0.0 || sc.z > 1.0)
        return 1.0;

    float slopeScale = max(abs(dFdx(sc.z)), abs(dFdy(sc.z)));
    float bias = clamp(0.0003 + slopeScale * 3.0, 0.0003, 0.008);
    const float spread = 2.0;
    vec2 texel = 1.0 / vec2(textureSize(uShadowMap, 0));

    float ign = fract(52.9829189 * fract(0.06711056 * gl_FragCoord.x + 0.00583715 * gl_FragCoord.y));
    float angle = ign * 6.2831853;
    float cosA = cos(angle), sinA = sin(angle);
    mat2 rot = mat2(cosA, sinA, -sinA, cosA);

    float shadow = 0.0;
    for (int i = 0; i < 16; i++)
    {
        vec2 offset = rot * disk[i] * texel * spread;
        float stored = texture(uShadowMap, clamp(sc.xy + offset, vec2(0.0), vec2(1.0))).r;
        shadow += (sc.z - bias > stored) ? 1.0 : 0.0;
    }
    shadow /= 16.0;

    return 1.0 - shadow * 0.7;
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

void main()
{
    vec2 worldXZ = vWorldPos.xz;

    // -- Procedural water color --
    float t = uTime;

    // Flowing ripple pattern
    float ripple1 = fbm(worldXZ * 0.8 + vec2(t * 0.12, t * 0.06));
    float ripple2 = fbm(worldXZ * 1.2 + vec2(-t * 0.08, t * 0.14));
    float ripple = mix(ripple1, ripple2, 0.5);

    // Mix between deep and shallow based on noise
    vec3 waterColor = mix(DeepColor, ShallowColor, ripple);

    // Add surface highlights
    float highlight = caustics(worldXZ, t);
    waterColor = mix(waterColor, SurfaceColor, highlight * 0.4);

    // -- Foam (only near shore, driven by vertex shore proximity) --
    float shoreProx = vTexCoord.x; // encoded by mesh builder: 0 = open water, 1 = shore
    float totalFoam = shoreFoam(worldXZ, shoreProx, t);
    waterColor = mix(waterColor, FoamColor, totalFoam);

    // -- Lighting (matches main shader) --
    float sun   = percToLinear(vSunLight) * vAo;
    float block = percToLinear(vBlockLight);

    float shadowFactor = computeShadow();
    sun *= shadowFactor;

    vec3 lightColor = SunColor * sun + BlockColor * block;
    lightColor = max(lightColor, vec3(Ambient));

    vec3 color = waterColor * lightColor;

    // -- Specular highlight --
    vec3 viewDir = normalize(uCameraPos - vWorldPos);
    vec3 lightDir = normalize(vec3(0.55, 1.8, 0.35));
    vec3 normal = normalize(vec3(
        fbm(worldXZ * 2.0 + vec2(t * 0.2, 0.0)) - 0.5,
        1.0,
        fbm(worldXZ * 2.0 + vec2(0.0, t * 0.2)) - 0.5
    ));
    vec3 halfDir = normalize(lightDir + viewDir);
    float spec = pow(max(dot(normal, halfDir), 0.0), 64.0);
    color += vec3(1.0) * spec * sun * 0.5;

    // -- Atmospheric fog --
    float fogT    = clamp((vViewDist - uFogStart) / (uFogEnd - uFogStart), 0.0, 1.0);
    float fogFrac = fogT * fogT;
    color = mix(color, FogColor, fogFrac);

    // Gamma encode
    color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));

    // Alpha: slightly more opaque where foam is present
    float alpha = mix(WaterAlpha, 0.85, totalFoam);

    FragColor = vec4(color, alpha);
}
