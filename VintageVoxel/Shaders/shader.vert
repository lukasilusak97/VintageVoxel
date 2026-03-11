#version 450 core

// Per-vertex attributes.  Two layouts are supported:
//
//  stride 8 (world chunks):
//    [0]  vec3  aPosition    - local block-space position
//    [1]  vec2  aTexCoord
//    [2]  float aSunLight    - sky-light level [0,1], pre-attenuated by face direction
//    [3]  float aBlockLight  - emitter-light level [0,1] (torches etc.)
//    [4]  float aAo          - ambient occlusion factor [0.4, 1.0]
//
//  stride 7 (entities / models):
//    same layout, except aBlockLight (location 3) is driven from the constant
//    default value (0.0) because it is not present in the vertex buffer.
//    In that case aSunLight carries the combined directional light.
layout(location = 0) in vec3  aPosition;
layout(location = 1) in vec2  aTexCoord;
layout(location = 2) in float aSunLight;   // sky / directional light [0,1]
layout(location = 3) in float aBlockLight; // emitter light [0,1]
layout(location = 4) in float aAo;         // ambient occlusion [0.4,1]

uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

out vec2  vTexCoord;
out float vSunLight;
out float vBlockLight;
out float vAo;
out float vViewDist;   // eye-space depth for atmospheric fog

void main()
{
    vec4 viewPos  = view * model * vec4(aPosition, 1.0);
    gl_Position   = projection * viewPos;

    vTexCoord   = aTexCoord;
    vSunLight   = aSunLight;
    vBlockLight = aBlockLight;
    vAo         = aAo;
    vViewDist   = -viewPos.z;  // positive depth in view space
}
