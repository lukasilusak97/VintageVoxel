#version 450 core

// Interpolated from vertex shader.
in vec2  vTexCoord;
in float vSunLight;    // sky-light level [0,1]
in float vBlockLight;  // emitter-light level [0,1]
in float vAo;          // ambient occlusion [0.4,1.0]
in float vViewDist;    // eye-space depth for fog
in vec4  vShadowCoord; // fragment position in light clip-space

out vec4 FragColor;

uniform sampler2D uTexture;
uniform sampler2D uShadowMap;  // depth texture rendered from light's POV
uniform int       uNoTexture;
uniform float     uFogStart;   // distance at which fog begins (blocks)
uniform float     uFogEnd;     // distance at which fog is fully opaque

// ---------------------------------------------------------------------------
// Color temperatures
// ---------------------------------------------------------------------------
// Sky light: slightly cool white - mimics overcast/noon daylight.
const vec3  SunColor    = vec3(0.95, 0.98, 1.00);
// Block light: warm amber - mimics a torch flame.
const vec3  BlockColor  = vec3(1.00, 0.68, 0.26);
// Horizon fog: hazy pale-blue sky.
const vec3  FogColor    = vec3(0.58, 0.72, 0.86);
// Minimum ambient so caves are never pitch-black.
const float Ambient     = 0.042;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

// Perceptual square encoding: approximates gamma 2.0, evens out light falloff.
float percToLinear(float v) { return v * v; }

void main()
{
    // percToLinear converts the perceptual light level to a linear scale.
    // AO is applied as a direct linear multiplier AFTER the conversion so it
    // is not accidentally squared along with the light value, which would make
    // corners unrealistically dark and gray.
    float sun   = percToLinear(vSunLight) * vAo;
    float block = percToLinear(vBlockLight);

    // ---------------------------------------------------------------------------
    // Shadow: soft Poisson-disk PCF with per-fragment IGN rotation.
    // 16 taps + Interleaved Gradient Noise rotation give smooth, low-noise
    // soft shadows without temporal flickering.
    // ---------------------------------------------------------------------------

    // 16-tap Poisson disk in unit-circle space.
    const vec2 disk[16] = vec2[](
        vec2(-0.9420,  -0.3991),
        vec2( 0.9456,  -0.7689),
        vec2(-0.0942,  -0.9294),
        vec2( 0.3450,   0.2939),
        vec2(-0.9159,   0.4577),
        vec2(-0.8154,  -0.8791),
        vec2(-0.3828,   0.2768),
        vec2( 0.9748,   0.7565),
        vec2( 0.4432,  -0.9751),
        vec2( 0.5374,  -0.4737),
        vec2(-0.2650,  -0.4189),
        vec2( 0.7920,   0.1909),
        vec2(-0.2419,   0.9971),
        vec2(-0.8141,   0.9144),
        vec2( 0.1998,   0.7864),
        vec2( 0.1438,  -0.1410)
    );

    {
        // Perspective divide -> NDC, then remap [-1,1] -> [0,1].
        vec3 sc = vShadowCoord.xyz / vShadowCoord.w;
        sc      = sc * 0.5 + 0.5;

        if (sc.x >= 0.0 && sc.x <= 1.0 &&
            sc.y >= 0.0 && sc.y <= 1.0 &&
            sc.z >= 0.0 && sc.z <= 1.0)
        {
            // Slope-scale bias: faces that are nearly parallel to the light
            // direction (e.g. leaf faces, vertical walls) receive a larger bias
            // so they don't self-shadow.  Flat ground facing the sun gets a
            // very small bias, keeping shadow contact tight.
            float slopeScale = max(abs(dFdx(sc.z)), abs(dFdy(sc.z)));
            float bias = clamp(0.0003 + slopeScale * 3.0, 0.0003, 0.008);
            const float spread = 2.0; // blur radius in shadow-map texels
            vec2  texel  = 1.0 / vec2(textureSize(uShadowMap, 0));

            // Interleaved Gradient Noise: better spectral distribution than a
            // sin-hash, produces far less visible grain at 16 taps.
            float ign   = fract(52.9829189 * fract(
                              0.06711056 * gl_FragCoord.x +
                              0.00583715 * gl_FragCoord.y));
            float angle = ign * 6.2831853;
            float cosA  = cos(angle), sinA = sin(angle);
            mat2  rot   = mat2(cosA, sinA, -sinA, cosA);

            float shadow = 0.0;
            for (int i = 0; i < 16; i++)
            {
                vec2 offset = rot * disk[i] * texel * spread;
                float stored = texture(uShadowMap,
                    clamp(sc.xy + offset, vec2(0.0), vec2(1.0))).r;
                shadow += (sc.z - bias > stored) ? 1.0 : 0.0;
            }
            shadow /= 16.0;

            // Keep 30 % of sun in shadowed areas so they are never pitch-black.
            sun *= 1.0 - shadow * 0.7;
        }
    }

    vec3 lightColor = SunColor * sun + BlockColor * block;
    lightColor = max(lightColor, vec3(Ambient));

    // Debug modes
    if (uNoTexture == 2)
    {
        float lum = dot(lightColor, vec3(0.299, 0.587, 0.114));
        FragColor = vec4(vec3(lum), 1.0);
        return;
    }
    if (uNoTexture != 0)
    {
        FragColor = vec4(lightColor, 1.0);
        return;
    }

    vec4 tex = texture(uTexture, vTexCoord);
    if (tex.a < 0.1) discard;

    // Modulate in approximate linear space.
    vec3 color = tex.rgb * lightColor;

    // Atmospheric fog
    float fogT    = clamp((vViewDist - uFogStart) / (uFogEnd - uFogStart), 0.0, 1.0);
    float fogFrac = fogT * fogT;
    color = mix(color, FogColor, fogFrac);

    // Gamma encode (linear to sRGB approximate)
    color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));

    FragColor = vec4(color, tex.a);
}
