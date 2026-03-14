### Phase 1: Direct JSON Conversion (Do this first)
**Prompt to AI:**
> "I have 7 Minecraft Java `.json` block/item models that I need converted to the Vintage Story `.json` shape format. I do not want a script; I want you to directly convert them and give me the updated JSON code for each.
> 
> Here are the rules for the conversion:
> 1. **Hierarchy:** Wrap the flat MC `elements` array inside a root element, or just leave them as a flat array (VS supports both).
> 2. **Coordinates:** Map MC `from` and `to` directly to VS `from` and `to`.
> 3. **Rotations:** MC defines rotation as an object with `origin[3]`, `axis` (x, y, or z), and `angle`. 
>    - Map MC `origin` to VS `rotationOrigin`.
>    - Map the MC `angle` to VS `rotationX`, `rotationY`, or `rotationZ` based on the specified MC `axis`. 
> 4. **Faces:** Map MC `faces` to VS `faces`. Ensure the `uv` array and `texture` string references are kept.
> 
> Here are the 7 models: 
> `[Paste the contents of your 7 Minecraft JSON files here]`"

### Phase 2: Refactoring Internal Data Structures
**Prompt to AI:**
> "Now we need to update the engine. Review my current engine code for the Minecraft model data structures. We are replacing them with Vintage Story (VS) model data structures.
> 
> Refactor or replace the existing model structs/classes to support the following VS schema:
> - `VSModel`: Root struct containing texture mappings, an array of root `VSElement`s, and an array of `VSAnimation`s.
> - `VSElement`: Must support hierarchy. Needs `name`, `from[3]`, `to[3]`, `rotationOrigin[3]`, `rotationX`, `rotationY`, `rotationZ`, a dictionary of `VSFace`s, and an array of `children` (which are also `VSElement`s).
> - `VSAnimation`: Contains `name` and an array of `VSKeyFrame`s.
> - `VSKeyFrame`: Contains a `frame` float, and a dictionary of `ElementTransforms` (position and rotation offsets for specific element names).
> Remove all old MC-specific struct fields (like `axis`-based rotations)."

### Phase 3: Writing the VS JSON Deserializer
**Prompt to AI:**
> "Update the JSON parsing system to load the new `VSModel` structures instead of the old MC ones.
> 
> Requirements:
> 1. Write a recursive parsing function to handle `VSElement`s, because in VS, elements can have `children` arrays infinitely deep.
> 2. Safely handle optional fields. In VS JSON, if `rotationX/Y/Z` or `rotationOrigin` are missing, default them to `0.0`.
> 3. Ensure texture string resolutions handle the VS syntax (which often maps texture codes directly to the faces)."

### Phase 4: Implementing Hierarchical Transform Math
**Prompt to AI:**
> "Rewrite the matrix transformation logic for the model elements. Since VS elements have parents, we can no longer use flat MC transform logic.
> 
> Create a recursive `CalculateTransform` function for `VSElement` that does the following in order:
> 1. Start with an identity matrix.
> 2. Translate to `rotationOrigin`.
> 3. Apply rotations (`rotationZ`, then `rotationY`, then `rotationX` — convert degrees to radians).
> 4. Translate back (negative `rotationOrigin`).
> 5. Multiply this local matrix by the `parentTransform` matrix passed into the function.
> 6. Recursively call this on all `children`, passing the newly calculated matrix as the new parent transform."

### Phase 5: Updating the Mesh Generator
**Prompt to AI:**
> "Update the mesh generation step (where we create the vertex, normal, and UV buffers).
> 
> 1. Iterate through the root `VSElement`s and their `children` recursively.
> 2. For every `VSFace` defined on an element, generate the two triangles (4 vertices).
> 3. Apply the correct Global Transform Matrix (calculated in the previous step) to the vertex positions and normals.
> 4. Normalize the coordinates by dividing the `from` and `to` positions by 16.0 (since both MC and VS use a 16-unit voxel scale).
> 5. Ensure the UV mappings match the new texture atlas logic."

### Phase 6: Implementing Native Keyframe Animations
**Prompt to AI:**
> "Implement a new animation controller for the VS models.
> 
> 1. Create a function that takes a `VSAnimation`, a current `time/frame`, and an `ElementTree`.
> 2. Find the two adjacent `VSKeyFrame`s based on the current time.
> 3. Use `Lerp` for position offsets and `Slerp` for rotation offsets to interpolate between the keyframes.
> 4. Inject these animated offsets into the `CalculateTransform` logic from Phase 4. The animated translation/rotation must be applied *relative to the rotationOrigin* and *before* the parent matrix is multiplied."

### Phase 7: Engine Clean-Up & Integration
**Prompt to AI:**
> "Now, trace through the rest of the codebase and replace all instantiations of the old Minecraft model loader with our new Vintage Story model loader.
> 
> 1. Fix any broken references in the entity rendering loop.
> 2. Hook up the game engine's `DeltaTime` to the new animation controller so the models animate over time.
> 3. Ensure the asset pipeline is now exclusively loading the 7 new VS JSON files we created earlier.
> 4. Delete any remaining legacy Minecraft model-loading code."