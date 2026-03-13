However, **you must be very careful with AI agents here.** Because Bepu v2 is fundamentally different from traditional Object-Oriented physics engines (like Unity's PhysX or Bepu v1), AI will frequently hallucinate and try to give you `class RigidBody` or Bepu v1 code. Bepu v2 uses `structs`, `Handles`, `BufferPools`, and `System.Numerics`. 

Here is the updated, highly-specific master plan to feed your AI agent. **Give the AI the "Strict Rules" section first.**

---

### ⚠️ Prerequisite Prompt: The Strict Rules for the AI
**Feed this to the AI before anything else to set the correct context:**
> "I am building a C# custom voxel engine using OpenTK and **BepuPhysics v2** (NOT v1). 
> **Strict Rules for BepuPhysics v2:**
> 1. Bepu v2 is Data-Oriented. Do not invent `RigidBody` classes. You must use `Simulation`, `BodyHandle`, `StaticHandle`, `BodyReference`, and `BufferPool`.
> 2. OpenTK uses `OpenTK.Mathematics.Vector3`, but Bepu v2 natively uses `System.Numerics.Vector3`. Write explicit conversion extensions between the two to avoid compilation errors.
> 3. Avoid Garbage Collection (GC) allocations in the physics update loop. Use structs, `ref` parameters, and arrays/pools where necessary.
> 4. The voxel grid has 16 horizontal layers per standard block (vertical scale is 1/16th of horizontal scale). The engine features caves, so we are working in true 3D."

---

### Phase 1: The Voxel Query Interface (C# Setup)
**Prompt for the AI Agent:**
> "Create a `VoxelPhysicsQuery` interface in C#. 
> 1. Implement a method `bool IsSolid(System.Numerics.Vector3 worldPosition)` that converts a continuous world coordinate into my 16-layer block coordinate system and returns true if it hits solid voxel data.
> 2. Ensure the math correctly handles negative coordinates. 
> 3. Provide a static helper class `MathConversions` to easily cast `OpenTK.Mathematics.Vector3` to `System.Numerics.Vector3` and vice versa."

### Phase 2: The "Moving Window" Collision Generator (Bepu v2 Statics)
**Prompt for the AI Agent:**
> "Using the `VoxelPhysicsQuery`, create a `VoxelCollisionWindow` class for BepuPhysics v2. 
> 1. It will take the vehicle's `System.Numerics.Vector3` center position and scan a 3D radius (e.g., 3 blocks).
> 2. For every solid 1/16th layer block found, it needs to represent a physics Box.
> 3. **Crucial Bepu v2 optimization:** Do NOT constantly `Simulation.Statics.Add` and `Remove` every frame, as this is slow. Instead, maintain a fixed-size Object Pool of `StaticHandle`s. 
> 4. Allocate a generic 1/16th block `Box` shape in `Simulation.Shapes` once. 
> 5. Update the `Simulation.Statics.GetStaticReference(handle).Pose` to move the pooled statics to the solid voxel positions around the car. Move unused statics far underground (e.g., Y = -9999) to 'hide' them."

### Phase 3: The 3D DDA Raycaster (Pure C# Voxel Math)
**Prompt for the AI Agent:**
> "Implement a 3D DDA (Digital Differential Analyzer) Raycast algorithm purely in C# for the wheels.
> 1. Signature: `bool Raycast(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float maxDistance, out System.Numerics.Vector3 hitPoint, out System.Numerics.Vector3 normal)`.
> 2. The ray must step through the 3D voxel grid utilizing `VoxelPhysicsQuery.IsSolid`. 
> 3. Because my blocks have 16 layers, the vertical step increments must be 1/16th of the horizontal step increments. 
> 4. Do not use Bepu's internal raycaster for this; query the voxel data directly to avoid snagging on the AABBs generated in Phase 2."

### Phase 4: The Bepu v2 Vehicle Chassis (The Rigid Body)
**Prompt for the AI Agent:**
> "Create a `VehicleChassis` class managed by Bepu v2. 
> 1. Create a dynamic body using `BodyDescription.CreateDynamic(...)`. The shape should be a `Box` (e.g., 2m x 1m x 4m) added to `Simulation.Shapes`.
> 2. Calculate the inertia tensor using `shape.ComputeInertia(mass)`.
> 3. Store the resulting `BodyHandle`. Provide a getter property that returns the `BodyReference` using `Simulation.Bodies.GetBodyReference(handle)` so we can easily read its `Pose` and `Velocity` in the update loop.
> 4. Hook up the `VoxelCollisionWindow` (from Phase 2) to update its pooled statics around the `BodyReference.Pose.Position` every frame."

### Phase 5: Bepu v2 Raycast Suspension (The Hover Springs)
**Prompt for the AI Agent:**
> "Implement the `RaycastSuspension` system. 
> 1. Define 4 wheel attachment points as local `System.Numerics.Vector3` offsets from the chassis center.
> 2. Every physics tick, get the `BodyReference`. Convert the local attachment points to world space using the body's `Pose`.
> 3. Cast a ray straight down (relative to the body's orientation) using the Custom 3D DDA Raycast.
> 4. If it hits the ground within `SuspensionLength`, calculate the spring force: 
>    `Force = (RestLength - HitDistance) * SpringStiffness - (VerticalVelocityAtWheel * Damping)`.
> 5. Apply this force upward using Bepu v2's `BodyReference.ApplyImpulse(impulse, offsetFromCenterOfMass)`. Remember that Bepu uses Impulses (Force * dt), so multiply the calculated force by `dt` before applying."

### Phase 6: Controls & OpenTK Integration
**Prompt for the AI Agent:**
> "Add OpenTK keyboard input integration to drive the Bepu v2 `VehicleChassis`.
> 1. Check OpenTK's `KeyboardState` for W/A/S/D.
> 2. **Acceleration:** If the DDA raycasts detect the wheels are on the ground, apply a forward impulse using `BodyReference.ApplyImpulse` at the wheel offsets along the local forward vector.
> 3. **Steering:** Rotate the local forward vector of the front wheels before applying drive forces, or apply a gentle `ApplyAngularImpulse` to the `BodyReference` to turn the chassis.
> 4. **Friction/Grip:** Calculate the lateral (sideways) velocity at each wheel offset. Apply an opposing impulse to cancel out sliding, ensuring the car grips the voxel surface instead of sliding like it's on ice."

---

### Pro-Tip for BepuPhysics v2
In Bepu v2, updating collision poses (like you will do in Phase 2) happens via `StaticReference`. 
If the AI struggles with Phase 2, remind it: 
> *"To move a static box, get its reference via `Simulation.Statics.GetStaticReference(handle)` and update its `Pose` property directly. Because it is a struct reference, Bepu handles the broad-phase update automatically."*