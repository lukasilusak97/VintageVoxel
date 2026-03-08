#version 450 core

// Per-vertex attributes supplied by the VBO (stride = 5 floats = 20 bytes).
layout(location = 0) in vec3 aPosition; // offset  0 bytes
layout(location = 1) in vec2 aTexCoord; // offset 12 bytes

// The MVP (Model-View-Projection) transformation chain:
//   model      - places the object in world space
//   view       - transforms world space into camera space
//   projection - applies perspective (far things look smaller)
uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

// Pass the UV coordinate to the fragment shader.
// 'out' variables are interpolated across the triangle (smooth UV across the face).
out vec2 vTexCoord;

void main()
{
    gl_Position = projection * view * model * vec4(aPosition, 1.0);
    vTexCoord = aTexCoord;
}
