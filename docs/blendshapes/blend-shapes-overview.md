# Blend Shapes (Morph Targets)

Blend shapes, also called *morph targets* or *shape keys*, are a vertex-level deformation
technique used to animate meshes without skeleton bones — most commonly for facial
expressions, lip sync, corrective shapes, and character customization.

## What Are Morph Targets?

Each morph target stores a set of *delta* values: per-vertex offsets from a base mesh
for positions, normals, and tangents. A **weight** (0.0–1.0) controls how much of each
target's deformation is applied at runtime.

$$
\text{position}[v] = \text{base}[v] + \sum_{t} w_t \cdot \Delta p_{t,v}
$$

Weight 0 = no deformation. Weight 1 = full deformation. Values above 1.0 are allowed for
exaggerated / overdrive effects.

### Terminology

All three of these mean the same thing:

| Term | Used by |
|------|---------|
| Blend shapes | Stride, Maya, ARKit |
| Shape keys | Blender |
| Morph targets | 3ds Max, glTF, Unreal |

---

## Creating Morph Targets in 3D Tools

### Blender
1. Select your mesh in Object Mode.
2. In the Properties panel → **Object Data Properties** → **Shape Keys**, add a *Basis* key.
3. Add additional keys and sculpt/edit vertices for each expression.
4. Export: **File → Export → FBX** → enable *Shape Keys* under Geometry.

### Maya
1. Duplicate the base mesh for each expression and sculpt the variation.
2. Select all duplicates plus the base mesh.
3. **Deform → Blend Shape** to create the blend shape deformer.
4. Export as FBX with blend shapes enabled in the FBX export options.

### 3ds Max
1. Select the base mesh.
2. Add a **Morph** modifier from the Modify panel.
3. Load target meshes (one per expression) into the morph channels.
4. Export as FBX.

**Tip:** Name your targets descriptively (e.g. `SmileLeft`, `BrowRaiseRight`, `BlinkLeft`).
These names carry through import and are used to set weights by name at runtime.

---

## Importing in Stride

When you import an FBX into Stride Game Studio:

1. The importer detects blend shapes automatically via the Assimp library.
2. In the model asset **Import Settings**, **Import Blend Shapes** is enabled by default.
   Uncheck it to discard blend shape data and reduce asset size.
3. At import time the data is:
   - Extracted per-target (delta positions, normals, tangents)
   - Transformed into Stride's coordinate space
   - **Cooked into a sparse CSR format** for efficient GPU access

---

## Runtime Usage

### Automatic Setup

When you add a model to an entity in Game Studio, if the model has blend shapes Stride
automatically adds a `BlendShapeComponent` to the entity — no manual setup required.

### Controlling Weights from C#

```csharp
public class FacialController : SyncScript
{
    private BlendShapeComponent blendShapes;

    public override void Start()
    {
        blendShapes = Entity.Get<BlendShapeComponent>();
    }

    public override void Update()
    {
        // Set a named target's weight
        blendShapes.SetWeight("SmileLeft", 0.8f);

        // Read a weight
        float blink = blendShapes.GetWeight("BlinkLeft");

        // Iterate all targets
        foreach (var kvp in blendShapes.Weights)
            Log.Info($"{kvp.Key} = {kvp.Value:F2}");
    }
}
```

### Controlling Weights in the Editor

Select an entity with a `BlendShapeComponent`. In the property panel you will see a
**Weights** dictionary with a slider for each target — useful for previewing expressions
without running the game.

### Animation-Driven Weights

FBX animations that include morph target keyframes are imported as animation curves.
Just play the animation clip normally:

```csharp
Entity.Get<AnimationComponent>().Play("TalkingAnimation");
// Morph target curves are applied automatically each frame
```

The animation system binds to `[BlendShapeComponent.Key].WeightValues[index]` on the
component, where `index` matches the target's position in `TargetNames`.

---

## BlendShapeComponent Settings

| Property | Default | Description |
|----------|---------|-------------|
| **Weights** | (per target, 0) | Per-target weight dictionary, editable in editor |
| **GPU Deformation** | true | Use compute shaders (recommended). Disable for CPU fallback. |
| **Fused Skinning** | true | Fuse blend shapes + skeletal skinning in one GPU pass. Best for facial animation on rigged characters. |

---

## Sample

The **BlendShapeDemo** sample (under `samples/Graphics/BlendShapeDemo/`) shows a
complete working setup: a model with morph targets and a `BlendShapeWeightScript`
that oscillates all weights at runtime.

See also the [API Reference](blend-shapes-api-reference.md) and
[Performance Guide](blend-shapes-performance.md).
