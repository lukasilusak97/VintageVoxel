#version 450 core

// Per-vertex attributes supplied by the VBO (stride = 7 floats = 28 bytes).
layout(location = 0) in vec3 aPosition; // offset  0 bytes
layout(location = 1) in vec2 aTexCoord; // offset 12 bytes
layout(location = 2) in float aLight;   // offset 20 bytes - combined light level [0,1]
layout(location = 3) in float aAo;      // offset 24 bytes - ambient occlusion factor [0.4,1.0]

// The MVP (Model-View-Projection) transformation chain:
//   model      - places the object in world space
//   view       - transforms world space into camera space
//   projection - applies perspective (far things look smaller)
uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

// Pass the UV coordinate and lighting to the fragment shader.
// 'out' variables are interpolated across the triangle.
out vec2 vTexCoord;
out float vLight;
out float vAo;

void main()
{
    gl_Position = projection * view * model * vec4(aPosition, 1.0);
    vTexCoord = aTexCoord;
    vLight    = aLight;
    vAo       = aAo;
}
