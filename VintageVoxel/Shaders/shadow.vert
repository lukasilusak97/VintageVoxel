#version 450 core

// Shadow-map depth pass.
// UV is forwarded to the fragment shader so transparent leaf pixels can be
// discarded, giving accurate per-leaf shadow silhouettes.
// Works with both stride-8 (chunks) and stride-7 (entities) vertex layouts
// since both share the same position/UV attribute locations and offsets.
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aTexCoord;

uniform mat4 lightSpaceMatrix; // combined light projection * view
uniform mat4 model;

out vec2 vTexCoord;

void main()
{
    gl_Position = lightSpaceMatrix * model * vec4(aPosition, 1.0);
    vTexCoord   = aTexCoord;
}
