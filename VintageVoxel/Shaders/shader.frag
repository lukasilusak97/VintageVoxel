#version 450 core

// Interpolated from vertex shader.
in vec2  vTexCoord;
in float vSunLight;    // sky-light level [0,1]
in float vBlockLight;  // emitter-light level [0,1]
in float vAo;          // ambient occlusion [0.4,1.0]
in float vViewDist;    // eye-space depth for fog

out vec4 FragColor;

uniform sampler2D uTexture;
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
    // AO modulates sun only; torches punch through AO.
    float sun   = percToLinear(vSunLight   * vAo);
    float block = percToLinear(vBlockLight);

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
