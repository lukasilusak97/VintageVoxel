#version 450 core

// Same vertex layout as world chunks (stride 8):
//   pos(3) + uv(2) + sunLight(1) + blockLight(1) + ao(1)
layout(location = 0) in vec3  aPosition;
layout(location = 1) in vec2  aTexCoord;
layout(location = 2) in float aSunLight;
layout(location = 3) in float aBlockLight;
layout(location = 4) in float aAo;

uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;
uniform mat4 lightSpaceMatrix;
uniform float uTime;

out vec3  vWorldPos;
out vec2  vTexCoord;
out float vSunLight;
out float vBlockLight;
out float vAo;
out float vViewDist;
out vec4  vShadowCoord;

void main()
{
    vec4 worldPos = model * vec4(aPosition, 1.0);

    // Gentle vertex wave displacement on the Y axis (top faces only).
    // Use two overlapping sine waves at different frequencies for organic motion.
    float wave1 = sin(worldPos.x * 1.2 + uTime * 1.8) * 0.04;
    float wave2 = sin(worldPos.z * 0.9 + uTime * 1.3) * 0.03;
    float wave3 = sin((worldPos.x + worldPos.z) * 0.7 + uTime * 2.1) * 0.02;
    worldPos.y += wave1 + wave2 + wave3;

    vec4 viewPos = view * worldPos;
    gl_Position  = projection * viewPos;

    vWorldPos    = worldPos.xyz;
    vTexCoord    = aTexCoord;
    vSunLight    = aSunLight;
    vBlockLight  = aBlockLight;
    vAo          = aAo;
    vViewDist    = -viewPos.z;
    vShadowCoord = lightSpaceMatrix * worldPos;
}
