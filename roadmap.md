***

# Project Plan: C# Custom Voxel Engine (Vintage Story Style)

## 0. Project Context (System Instructions)
**Copy and paste this into the AI's "Custom Instructions" or the top of your chat:**
> "You are an expert Graphics Programmer specializing in C#, .NET 8, and OpenTK. We are building a custom Voxel Game Engine from scratch (similar to Vintage Story).
>
> **Technical Stack:**
> - **Language:** C# (.NET 8.0)
> - **Graphics:** OpenTK (Latest Version)
> - **API:** OpenGL 4.5 Core Profile (Do NOT use legacy `glBegin`/`glEnd`)
> - **Math:** OpenTK.Mathematics (Vectors, Matrices, Quaternions)
>
> **Coding Rules:**
> - Keep classes in separate files.
> - Use 'Face Culling' logic immediately (never render hidden block faces).
> - Prioritize performance over simple code.
> - Always explain the 'Why' behind OpenGL calls."

---

## Phase 1: The Foundation (Window & Loop)
**Goal:** Get a blank window running with a proper game loop.

*   **Step 1.1:** Setup .NET 8 Console App and install `OpenTK`.
*   **Step 1.2:** Create the `Game` class inheriting from `GameWindow`.
*   **Step 1.3:** Implement `OnLoad`, `OnRenderFrame`, and `OnUpdateFrame`.
*   **Step 1.4:** Set up the OpenGL Viewport and a Clear Color (Sky Blue).

> **Prompt for AI:** "Start Phase 1. Create a C# Console project structure. Install OpenTK. Create a `Game.cs` class that opens an 800x600 window. Set the background to light blue. Ensure the main program runs this window."

---

## Phase 2: The Graphics Pipeline (The Shader System)
**Goal:** Draw a single 2D quad (square) on the screen to prove rendering works.

*   **Step 2.1:** Create a `Shader` class. It must read vertex/fragment `.glsl` files, compile them, and link the Program.
*   **Step 2.2:** Create `shader.vert` (Vertex Shader) and `shader.frag` (Fragment Shader).
*   **Step 2.3:** Implement VBO (Vertex Buffer Object) and VAO (Vertex Array Object) handling.
*   **Step 2.4:** Draw a simple hard-coded rectangle in the center of the screen.

> **Prompt for AI:** "Start Phase 2. Create the Shader class to load and compile GLSL files. Create a simple shader pair. Then, in `OnLoad`, set up a VBO and VAO to draw a single 2D rectangle (Quad) in the center of the screen."

---

## Phase 3: The 3D World (Camera & Math)
**Goal:** Turn the 2D square into a 3D cube and fly around it.

*   **Step 3.1:** Create a `Camera` class. Implement a View Matrix and Projection Matrix (Perspective).
*   **Step 3.2:** Implement Mouse Input (Looking around) and Keyboard Input (WASD Movement).
*   **Step 3.3:** Update the Shaders to accept Uniform Matrices (`model`, `view`, `projection`).
*   **Step 3.4:** Turn the 2D rectangle into a 3D Cube.

> **Prompt for AI:** "Start Phase 3. Create a Camera class using OpenTK Mathematics. It needs pitch, yaw, and position. Hook up WASD and Mouse inputs to move the camera. Pass the View and Projection matrices to the shader and render a static 3D cube instead of a quad."

---

## Phase 4: Voxel Data Structure
**Goal:** Stop drawing hardcoded cubes. Create a system to manage block data.

*   **Step 4.1:** Create a `Block` struct (holding ID, transparency boolean).
*   **Step 4.2:** Create a `Chunk` class. Define size as `32x32x32`. Create a 3D array of Blocks.
*   **Step 4.3:** Initialize the Chunk with random noise (or just fill the bottom half with "Dirt").

> **Prompt for AI:** "Start Phase 4. Create the logic for `Block` and `Chunk`. The Chunk should contain a 1-dimensional array representing 32x32x32 blocks (flattened array is faster than 3D). Fill the bottom 16 layers with a block ID of 1, and the rest with 0 (Air)."

---

## Phase 5: The "Mesher" (Procedural Geometry)
**Goal:** The most critical part. Convert the Chunk data into a single Mesh efficiently.

*   **Step 5.1:** Create `ChunkMeshBuilder.cs`.
*   **Step 5.2:** **Implementation of Face Culling:** Iterate through every block in the chunk. Check its 6 neighbors (Up, Down, North, South, East, West).
*   **Step 5.3:** **Rule:** Only add vertices to the list IF the neighbor is Air (ID 0).
*   **Step 5.4:** Send this generated list of vertices to the GPU (VBO).

> **Prompt for AI:** "Start Phase 5. This is the most important step. Write a `MeshBuilder` that iterates the Chunk. **Crucial:** Implement Back-Face Culling logic. Only generate the mesh data for a face if the adjacent block is Empty/Air. Render one single chunk."

---

## Phase 6: Texturing (Texture Atlas)
**Goal:** Apply textures to the blocks without loading 1000 images.

*   **Step 6.1:** Create a `Texture` class.
*   **Step 6.2:** Create a "Texture Atlas" (one large image containing Dirt, Stone, Grass side-by-side).
*   **Step 6.3:** Update the `Block` struct to know which UV coordinates (location on the Atlas) it uses.
*   **Step 6.4:** Pass UV data to the shader.

> **Prompt for AI:** "Start Phase 6. Implement a Texture Atlas system. Load one large .png file. Modify the MeshBuilder to add UV coordinates to the vertices based on the Block ID (e.g., Grass uses top-left of the atlas, Dirt uses top-right)."

---

## Phase 7: Infinite World Generation
**Goal:** Generate multiple chunks based on position.

*   **Step 7.1:** Create a `World` class containing a `Dictionary<Vector2, Chunk>`.
*   **Step 7.2:** Implement a Perlin Noise library (Simplex Noise).
*   **Step 7.3:** Generate chunks in a radius around the player.
*   **Step 7.4:** Logic to unload chunks that are far away.

> **Prompt for AI:** "Start Phase 7. Create a World Manager. Use Perlin Noise to generate terrain height. Spawns chunks in a 5x5 grid around the player. Handle the logic to generate new chunks as the player moves."

---

## Phase 8: Interaction (Raycasting)
**Goal:** Break and place blocks.

*   **Step 8.1:** Implement a **DDA (Digital Differential Analyzer)** Raycast. This calculates exactly which block coordinate the camera is looking at.
*   **Step 8.2:** On Left Click -> Set Block ID to 0 (Air) -> Re-run MeshBuilder.
*   **Step 8.3:** On Right Click -> Set Block ID to 1 (Stone) -> Re-run MeshBuilder.

> **Prompt for AI:** "Start Phase 8. Implement a Voxel Raycast (DDA Algorithm). When I click the mouse, detect the specific integer block coordinate I am looking at. Update the chunk data to change that block to Air, and trigger a mesh rebuild."

## Phase 9: UI Dashboard & Debugging (ImGui)
**Goal:** The Developer Console.
*   **9.1:** Integrate `ImGui.NET`.
*   **9.2:** Create `DebugWindow` class.
*   **9.3:** **Metrics:** Show FPS, FrameTime (ms), Player Position (X/Y/Z), Chunks Loaded Count.
*   **9.4:** **Toggles:**
    *   [ ] Wireframe Mode (`GL.PolygonMode`)
    *   [ ] Show Chunk Borders (Draw Red Lines at chunk edges)
    *   [ ] No Textures (White mode for lighting debug)

> **Prompt:** "Start Phase 9. Integrate ImGui.NET. Create a Debug Dashboard. Include an FPS counter, coordinate display, and checkboxes to toggle Wireframe Mode and Chunk Border visualization."

## Phase 10: Physics & Movement
**Goal:** Collision and Gravity.
*   **10.1:** AABB Collision detection (Axis-Aligned Bounding Box).
*   **10.2:** **Sliding:** Prevent sticking to walls.
*   **10.3:** **Creative Fly Mode:** Press 'F' to toggle gravity/collision off.

> **Prompt:** "Start Phase 10. Implement AABB Collision and Gravity. Add a 'Creative Mode' toggle (F key) that disables physics and allows free flight."

## Phase 11: Advanced Lighting (Flood Fill)
**Goal:** Sun rays and Torches.
*   **11.1:** **Ambient Occlusion:** Calculate vertex darkness based on corner neighbors.
*   **11.2:** **Light Propagation:** Implement BFS (Breadth-First Search) for sunlight (Level 15) and Torches (Level 14).
*   **11.3:** Pass Light Levels to the shader to multiply texture color.

> **Prompt:** "Start Phase 11. Implement a Lighting Engine. Use Vertex Colors. First, calculate Ambient Occlusion for corners. Second, implement a Flood Fill algorithm to propagate sunlight and torchlight through air blocks."

## Phase 12: Optimization (Frustum Culling)
**Goal:** Render only what is visible.
*   **12.1:** Extract Camera Frustum Planes.
*   **12.2:** Check Chunk AABB against Frustum.
*   **12.3:** Skip rendering if outside view.

> **Prompt:** "Start Phase 12. Implement View Frustum Culling. Ensure we do not make Draw calls for chunks behind the player."

---

## Phase 13: "Vintage" Mechanics: Chiseling (Micro-Blocks)
**Goal:** The signature feature of Vintage Story.
*   **13.1:** Create a `MicroBlock` class (16x16x16 bits).
*   **13.2:** Define a special Block ID (e.g., ID 999) that acts as a container for MicroBlocks.
*   **13.3:** When rendering ID 999, loop through the mini-bits and generate a custom mesh.

> **Prompt:** "Start Phase 13. Create a 'Chiseled Block' system. If a block is marked as 'Chiseled', it should contain a 16x16x16 boolean array. Update the MeshBuilder to render these mini-voxels efficiently."

## Phase 14: "Vintage" Atmosphere: Temporal Stability
**Goal:** Horror elements.
*   **14.1:** Create a `Stability` float (0.0 to 1.0).
*   **14.2:** **Shader Waving:** Update Vertex Shader. If Stability is low, apply a `sin(time)` offset to vertex positions to make the world "breathe" or warp.
*   **14.3:** **Desaturation:** Update Fragment Shader to turn the screen grey/sepia as stability drops.

> **Prompt:** "Start Phase 14. Implement atmospheric shaders. Pass a 'Stability' uniform to the shaders. When stability is low, warp the vertex positions using Sine waves and desaturate the colors in the fragment shader."

## Phase 15: Persistence (Saving/Loading)
**Goal:** Saving the world.
*   **15.1:** Binary Serialization using `BinaryWriter`.
*   **15.2:** **RLE Compression:** Compress block ID arrays.
*   **15.3:** Save to `%AppData%/MyVoxelGame/Saves/`.

> **Prompt:** "Start Phase 15. Implement a Region-based saving system. Use Run-Length Encoding to compress chunk data and save it to binary files."