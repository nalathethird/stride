# Blend Shape Demo Sample

This sample demonstrates how to use **blend shapes (morph targets)** in Stride at runtime.

## What It Shows

- How to import an FBX model with morph targets using the built-in FBX pipeline
- `BlendShapeComponent` being automatically added to the entity — no manual setup needed
- Controlling blend shape weights from a C# `SyncScript` at runtime
- Oscillating all morph targets with phase-offset sine waves for a live deformation effect

## Setup

1. **Provide a model**: Place an FBX file with morph targets in the `Resources/` folder.
   - In **Blender**: these are called *Shape Keys*. Export via File → Export → FBX → enable *Shape Keys*.
   - In **Maya**: Deform → Blend Shape. Export via FBX with blend shapes enabled.
   - In **3ds Max**: Modify → Morph modifier. Export via FBX.

2. **Import in Game Studio**: Drag the FBX into your Assets folder. In the model asset properties,
   confirm **Import Blend Shapes** (under *Import Settings*) is checked.

3. **Add to scene**: Drop the model onto a new entity. Game Studio will automatically add a
   `BlendShapeComponent` to the entity as soon as the model is assigned.

4. **Attach the script**: Add `BlendShapeWeightScript` to the same entity.
   - Leave `TargetName` empty to animate all targets with a ripple effect.
   - Set `TargetName` to a specific name (e.g. `"Smile"`) to animate only that target.
   - Adjust `Speed` to control the oscillation rate.

5. **Run**: Press Play — the model deforms in a continuous loop.

## Script Reference

`BlendShapeWeightScript.cs` is the core of this sample:

```csharp
// Animate a single named target
float weight = (float)(Math.Sin(time) * 0.5 + 0.5);
blendShapes.SetWeight("Smile", weight);

// Or animate all targets with phase offsets (ripple)
for (int i = 0; i < targets.Length; i++)
{
    float phase = i * MathUtil.TwoPi / targets.Length;
    float weight = (float)(Math.Sin(time + phase) * 0.5 + 0.5);
    blendShapes.SetWeight(targets[i], weight);
}
```

## Project Structure

```
BlendShapeDemo/
├── BlendShapeDemo.sdtpl              # Sample template descriptor
├── README.md                         # This file
├── BlendShapeDemo.Game/
│   ├── BlendShapeDemo.Game.csproj
│   ├── BlendShapeDemo.Game.sdpkg
│   └── BlendShapeWeightScript.cs     # Runtime blend shape weight controller
├── BlendShapeDemo.Windows/
│   ├── BlendShapeDemo.Windows.csproj
│   └── BlendShapeDemoApp.cs          # Windows entry point
├── Assets/                           # Stride asset files (.sdm3d, .sdscene, etc.)
└── Resources/                        # Place your source FBX files here
```

## Tips

- **GPU Deformation** (default on): blend shapes are computed entirely on the GPU using compute
  shaders. Disable on `BlendShapeComponent` to fall back to multi-threaded CPU deformation.
- **Fused Skinning** (default on): for skinned/rigged models, blend shapes and skeletal skinning
  are fused into one GPU compute pass — best performance for facial animation.
- **Animation-driven weights**: morph target keyframes exported from FBX are imported as animation
  curves. Just play the animation clip normally — the weights are applied automatically.
