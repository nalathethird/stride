# Blend Shapes API Reference

## BlendShapeComponent

**Namespace:** `Stride.Engine`  
**Assembly:** Stride.Engine  
**File:** `sources/engine/Stride.Engine/Engine/BlendShapeComponent.cs`

Per-entity component that manages blend shape weights and deformation settings.
Automatically added by `BlendShapeAutoProvisionProcessor` when a `ModelComponent`
references a model with blend shape data.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Weights` | `Dictionary<string, float>` | empty | Weight for each target by name. Editable in Game Studio. Typical range 0.0–1.0. |
| `UseGpuDeformation` | `bool` | `true` | Use GPU compute shaders. Set false for CPU fallback on platforms without compute. |
| `UseFusedSkinning` | `bool` | `true` | Fuse blend shape deformation and skeletal skinning into one GPU compute pass. |
| `TargetNames` | `string[]` | (read-only) | Ordered target names, populated at initialization. Index matches `WeightValues`. |
| `WeightValues` | `float[]` | (read-only) | Flat weight array by target index. Written by the animation system. |

### Methods

#### `SetWeight(string targetName, float weight)`

Sets the weight of a single blend shape target.

```csharp
blendShapes.SetWeight("Smile", 0.75f);
```

Incrementing an internal revision counter causes the processor to sync dictionary → array
before the next deformation pass.

#### `GetWeight(string targetName)`

Returns the current weight for a target, or `0.0f` if not found.

```csharp
float w = blendShapes.GetWeight("BlinkLeft");
```

#### `InitializeFromModel(ModelComponent modelComponent)`

Populates `Weights`, `TargetNames`, and `WeightValues` from the model's blend shape
definitions. Called automatically; rarely needed in user code.

### Animation Binding

The animation system targets:
```
[BlendShapeComponent.Key].WeightValues[{targetIndex}]
```
where `targetIndex` is the position in `TargetNames`. FBX animations with morph target
keyframes automatically generate these curves on import.

---

## BlendShapeDriverComponent

**Namespace:** `Stride.Engine`  
**File:** `sources/engine/Stride.Engine/Engine/BlendShapeDriverComponent.cs`

Advanced component for *computed* morph targets — blend shape weights derived from
combinations of other weights. Used for corrective shapes.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Combinations` | `List<BlendShapeCombination>` | Rules for computing output weights |
| `CalculateTension` | `bool` | Write mesh tension to a TEXCOORD channel for material use |
| `TensionTexCoordIndex` | `int` | TEXCOORD index to receive tension data |

### BlendShapeCombination

```
weight[Target] = weight[SourceA] × weight[SourceB] × Multiplier
```

| Property | Type | Description |
|----------|------|-------------|
| `TargetName` | `string` | Output target name |
| `SourceA` | `string` | First source target |
| `SourceB` | `string` | Second source target |
| `Multiplier` | `float` | Scale for the computed weight |

---

## BlendShapeProcessor

**Namespace:** `Stride.Engine.Processors`  
**File:** `sources/engine/Stride.Engine/Engine/Processors/BlendShapeProcessor.cs`

Entity processor (Order = -50) that syncs `BlendShapeComponent` weights into per-mesh
`BlendShapeWeights` arrays and dispatches GPU or CPU deformation each frame.

### Deformation Path Selection

| Path | Condition | Notes |
|------|-----------|-------|
| Fused GPU | GPU on + fused on + skinned mesh | Single compute pass, dispatches every frame |
| Sparse GPU | GPU on + cooked data present | CSR layout, dispatches on weight change |
| Dense GPU | GPU on + no cooked data | Legacy layout, dispatches on weight change |
| CPU | GPU off or unsupported | Multi-threaded SIMD via `Dispatcher.ForBatched()` |

---

## BlendShapeAutoProvisionProcessor

**Namespace:** `Stride.Engine.Processors`  
**File:** `sources/engine/Stride.Engine/Engine/Processors/BlendShapeAutoProvisionProcessor.cs`

Entity processor (Order = -200) registered on `ModelComponent`. Automatically adds a
`BlendShapeComponent` to any entity whose model contains blend shape target data.
Also detects model hot-swaps at runtime.

---

## MeshBlendShapeDefinition

**Namespace:** `Stride.Rendering`  
**File:** `sources/engine/Stride.Rendering/Rendering/MeshBlendShapeDefinition.cs`

Holds all blend shape data for a single mesh. Serialized as part of the model asset.

### BlendShapeTarget

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Target name (e.g. `"Smile"`) |
| `DeltaPositions` | `Vector3[]` | Per-vertex position offsets |
| `DeltaNormals` | `Vector3[]` | Per-vertex normal offsets |
| `DeltaTangents` | `Vector3[]` | Per-vertex tangent offsets |
| `HasDeltaPositions` | `bool` | Whether position deltas were imported |
| `HasDeltaNormals` | `bool` | Whether normal deltas were imported |
| `HasDeltaTangents` | `bool` | Whether tangent deltas were imported |

### BlendShapeCookedData

Compressed Sparse Row representation built by `MeshBlendShapeDefinition.Cook()` at import
time. Used by the sparse GPU compute path.

| Property | Type | Description |
|----------|------|-------------|
| `VertexContribOffset` | `uint[]` | Per-vertex start index into contribution arrays |
| `VertexContribCount` | `uint[]` | Per-vertex number of target contributions |
| `ContribShapeIndices` | `uint[]` | Flat list of contributing target indices |
| `ContribPosDeltas` | `Vector3[]` | Flat list of position deltas |
| `ContribNrmDeltas` | `Vector3[]` | Flat list of normal deltas |
| `ContribTanDeltas` | `Vector3[]` | Flat list of tangent deltas (if present) |
| `TotalContribCount` | `int` | Total non-zero entries across all vertices |

---

## ModelAsset Import Property

**File:** `sources/engine/Stride.Assets.Models/ModelAsset.cs`

```csharp
[Display("Import Blend Shapes", "Import Settings")]
public bool ImportBlendShapes { get; set; } = true;
```

When unchecked, blend shape data is discarded during asset compilation to reduce memory.

---

## Compute Shaders

| Shader | Path | Purpose |
|--------|------|---------|
| `BlendShapeDeform.sdsl` | `Rendering/BlendShapes/` | Dense blend shape deformation |
| `BlendShapeDeformSparse.sdsl` | `Rendering/BlendShapes/` | Sparse CSR blend shape deformation |
| `BlendShapeSkinningFused.sdsl` | `Rendering/BlendShapes/` | Fused blend shapes + skeletal skinning |
