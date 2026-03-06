# Blend Shapes Performance Guide

## Choosing a Deformation Path

Stride automatically selects the best deformation path based on your settings and hardware.

### GPU vs CPU

| | GPU Compute (default) | CPU Fallback |
|-|-----------------------|--------------|
| **Performance** | Excellent | Good (multi-threaded SIMD) |
| **Memory bandwidth** | GPU-local; no CPU→GPU upload per frame | Uploads modified VB each frame |
| **CPU cost** | Near zero | Uses `Dispatcher.ForBatched()` threads |
| **Platform** | Requires compute shader support | Works everywhere |
| **When to use** | All modern desktop/console hardware | Debugging, or no compute support |

Disable GPU deformation per-entity:
```csharp
entity.Get<BlendShapeComponent>().UseGpuDeformation = false;
```

### Sparse vs Dense GPU

All newly imported models use the **sparse CSR** path. The dense path is a legacy fallback
for assets imported before sparse cooking was introduced.

| | Sparse (default) | Dense (legacy) |
|-|-----------------|----------------|
| **Memory** | ~15–30× smaller | Full vertex×target matrix |
| **Dead-zone culling** | Yes — removes ~15–40% near-zero deltas | No |
| **Access pattern** | Vertex-major CSR | Target-major flat arrays |
| **When** | Cooked data present (all current imports) | Old imported assets |

To upgrade old assets to sparse, simply re-import them in Game Studio.

**Example memory cost** (50 targets, 10 000 vertices, positions only):

| Layout | Calculation | Size |
|--------|------------|------|
| Dense | 50 × 10 000 × 12 B | 6 MB |
| Sparse (10 % density) | 50 000 contributions × 12 B + CSR tables | ~640 KB |

### Fused vs Separate Skinning

For **skinned (rigged) meshes**, fused mode merges blend shape deformation and skeletal
skinning into one compute dispatch, removing the vertex shader skinning pass entirely.

| Mode | When to use | GPU cost |
|------|------------|---------|
| **Fused** (default) | Facial animation — weights change every frame | One dispatch/frame (blend + skin) |
| **Separate** | Static blend shapes on animated body | Dispatch only when weights change; VS handles skinning |

Disable fused mode if weights are static most frames:
```csharp
entity.Get<BlendShapeComponent>().UseFusedSkinning = false;
```

---

## Dirty Detection

In **separate (non-fused) mode**, the processor compares the current weight array against
the previous frame's. If nothing changed, the compute dispatch is **skipped entirely**.

This means characters with static blend shapes (e.g. custom body proportions set once)
have zero per-frame GPU deformation cost after the first frame.

---

## Active Target Compaction

The GPU deformer only processes targets with `|weight| > 0.0001`. A 80-target rig with
only 5 non-zero weights pays the cost of 5 targets, not 80.

---

## Memory Budget Reference

### Per-model (shared, loaded once)

| Data | Sparse (10 % density, 50 targets, 10 K verts) |
|------|-----------------------------------------------|
| Base positions | 120 KB |
| Base normals | 120 KB |
| Sparse CSR contributions | ~640 KB |
| **Total** | **~880 KB** |
| Dense equivalent | ~18 MB |

### Per-entity (instanced)

| Data | Approximate size |
|------|-----------------|
| Output vertex buffer (clone of VB[0]) | Same as original VB |
| All-weights upload buffer | ~200–500 B for typical rigs |
| Object-space bone matrices (fused only) | 56 bones × 64 B ≈ 3.6 KB |

---

## Recommendations

| Recommendation | Reason |
|---------------|--------|
| Keep GPU deformation on | Up to 10× faster than CPU for complex rigs |
| Use fused skinning for facial animation | Saves one full vertex buffer pass per frame |
| Disable fused for set-once blend shapes | Avoids dispatching when bone matrices change each frame |
| Re-import old model assets | Picks up sparse cooking (15–30× memory saving) |
| Limit active targets | Even with compaction, large active target counts increase bandwidth |
| Name targets clearly | Names are preserved through import and used at runtime by `SetWeight()` |
