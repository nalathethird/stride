// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.Data;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Engine.Processors
{
    /// <summary>
    /// Processor that syncs <see cref="BlendShapeComponent"/> weights into <see cref="ModelComponent.MeshInfo.BlendShapeWeights"/>
    /// each frame and dispatches GPU compute shader deformation. Falls back to CPU deformation
    /// when the GPU compute path is unavailable.
    /// </summary>
    public class BlendShapeProcessor : EntityProcessor<BlendShapeComponent, BlendShapeProcessor.BlendShapeData>
    {
        private GraphicsDevice graphicsDevice;
        private GraphicsContext graphicsContext;
        private BlendShapeGpuDeformer gpuDeformer;
        private bool useGpuPath;

        public BlendShapeProcessor()
            : base(typeof(ModelComponent))
        {
            // Run after ModelTransformProcessor (-100) but before rendering
            Order = -50;
        }

        protected internal override void OnSystemAdd()
        {
            base.OnSystemAdd();
            graphicsDevice = Services.GetSafeServiceAs<IGraphicsDeviceService>().GraphicsDevice;
            graphicsContext = Services.GetSafeServiceAs<GraphicsContext>();

            // Use GPU path if compute shaders are supported
            useGpuPath = BlendShapeGpuDeformer.IsSupported(graphicsDevice);
            if (useGpuPath)
            {
                gpuDeformer = new BlendShapeGpuDeformer(graphicsDevice, graphicsContext, Services);
            }
        }

        protected internal override void OnSystemRemove()
        {
            gpuDeformer?.Dispose();
            gpuDeformer = null;
            base.OnSystemRemove();
        }

        protected override BlendShapeData GenerateComponentData(Entity entity, BlendShapeComponent component)
        {
            return new BlendShapeData
            {
                BlendShapeComponent = component,
                ModelComponent = entity.Get<ModelComponent>(),
            };
        }

        protected override bool IsAssociatedDataValid(Entity entity, BlendShapeComponent component, BlendShapeData associatedData)
        {
            return component == associatedData.BlendShapeComponent
                && entity.Get<ModelComponent>() == associatedData.ModelComponent;
        }

        protected override void OnEntityComponentAdding(Entity entity, BlendShapeComponent component, BlendShapeData data)
        {
            // Auto-initialize weights from the model if not already populated
            if (data.ModelComponent != null)
            {
                component.InitializeFromModel(data.ModelComponent);
                data.LastDictionaryRevision = component.DictionaryRevision;
            }
        }

        protected override void OnEntityComponentRemoved(Entity entity, BlendShapeComponent component, BlendShapeData data)
        {
            // Dispose per-entity GPU/CPU resources to avoid memory leaks
            if (data.ModelComponent?.MeshInfos == null)
                return;

            foreach (var meshInfo in data.ModelComponent.MeshInfos)
            {
                if (meshInfo.GpuBlendShapeInitialized)
                {
                    BlendShapeGpuDeformer.DisposeGpuBuffers(meshInfo);
                }
                else
                {
                    // CPU path cleanup
                    if (meshInfo.ClonedMeshDraw?.VertexBuffers != null)
                    {
                        foreach (var vb in meshInfo.ClonedMeshDraw.VertexBuffers)
                        {
                            vb.Buffer?.Dispose();
                        }
                    }
                    meshInfo.ClonedMeshDraw = null;
                    // Reset GPU flags that DisposeGpuBuffers would have cleared
                    meshInfo.GpuSparseInitialized = false;
                    meshInfo.UseFusedSkinning = false;
                    meshInfo.GpuBlendShapeInitialized = false;
                }

                meshInfo.DeformedVertexData = null;
                meshInfo.BlendShapeInitialized = false;
            }
        }

        public override void Draw(RenderContext context)
        {
            var commandList = graphicsContext?.CommandList;

            foreach (var kv in ComponentDatas)
            {
                var data = kv.Value;
                var blendShapeComponent = data.BlendShapeComponent;
                var modelComponent = data.ModelComponent;

                if (!blendShapeComponent.Enabled || modelComponent?.Model == null)
                    continue;

                // Check per-component GPU override
                var useGpu = useGpuPath && blendShapeComponent.UseGpuDeformation;

                // Re-initialize if the model was hotswapped
                var targetNames = blendShapeComponent.TargetNames;
                if (targetNames == null || targetNames.Length == 0)
                {
                    blendShapeComponent.InitializeFromModel(modelComponent);
                    targetNames = blendShapeComponent.TargetNames;
                    data.LastDictionaryRevision = blendShapeComponent.DictionaryRevision;
                }

                var weightValues = blendShapeComponent.WeightValues;
                if (targetNames == null || weightValues == null)
                    continue;

                // Two-way sync between dictionary and array:
                // If user changed dictionary (via SetWeight or direct edit), sync dict → array
                // Otherwise, sync array → dict (animation system writes to WeightValues[])
                if (blendShapeComponent.DictionaryRevision != data.LastDictionaryRevision)
                {
                    // Dictionary was changed — sync dict → array
                    for (int i = 0; i < targetNames.Length && i < weightValues.Length; i++)
                    {
                        weightValues[i] = blendShapeComponent.Weights.TryGetValue(targetNames[i], out var w) ? w : 0.0f;
                    }
                    data.LastDictionaryRevision = blendShapeComponent.DictionaryRevision;
                }
                else
                {
                    // Animation/script may have written to WeightValues[] — sync array → dict
                    for (int i = 0; i < targetNames.Length && i < weightValues.Length; i++)
                    {
                        blendShapeComponent.Weights[targetNames[i]] = weightValues[i];
                    }
                }

                // Ensure MeshInfos are up-to-date
                var meshInfos = modelComponent.MeshInfos;
                if (meshInfos == null)
                    continue;

                for (int meshIndex = 0; meshIndex < modelComponent.Model.Meshes.Count && meshIndex < meshInfos.Count; meshIndex++)
                {
                    var mesh = modelComponent.Model.Meshes[meshIndex];
                    var meshInfo = meshInfos[meshIndex];

                    if (mesh.BlendShapes?.Targets == null || meshInfo.BlendShapeWeights == null)
                        continue;

                    // Sync component weights → meshInfo per-mesh weights
                    var targets = mesh.BlendShapes.Targets;
                    for (int targetIdx = 0; targetIdx < targets.Length && targetIdx < meshInfo.BlendShapeWeights.Length; targetIdx++)
                    {
                        var targetName = targets[targetIdx]?.Name;
                        if (targetName != null && blendShapeComponent.Weights.TryGetValue(targetName, out var weight))
                        {
                            meshInfo.BlendShapeWeights[targetIdx] = weight;
                        }
                        else
                        {
                            meshInfo.BlendShapeWeights[targetIdx] = 0.0f;
                        }
                    }
                    // Zero out any remaining slots from a previous model with more targets
                    for (int targetIdx = targets.Length; targetIdx < meshInfo.BlendShapeWeights.Length; targetIdx++)
                    {
                        meshInfo.BlendShapeWeights[targetIdx] = 0.0f;
                    }

                    // Determine if this mesh should use fused blend-shape + skinning
                    bool canUseFused = useGpu && blendShapeComponent.UseFusedSkinning
                        && mesh.Skinning?.Bones != null && mesh.Skinning.Bones.Length > 0;

                    if (canUseFused)
                    {
                        // --- Fused blend shape + skinning path ---
                        // Check if all weights are zero and unchanged — if so, skip fused
                        // compute and let standard VS skinning handle this mesh instead.
                        bool allZero = true;
                        for (int w = 0; w < meshInfo.BlendShapeWeights.Length; w++)
                        {
                            if (meshInfo.BlendShapeWeights[w] != 0f) { allZero = false; break; }
                        }

                        if (allZero && !BlendShapeDeformer.AreWeightsDirty(meshInfo.BlendShapeWeights, meshInfo.PreviousWeights)
                            && meshInfo.GpuBlendShapeInitialized)
                        {
                            // All weights zero and unchanged — fall back to VS skinning
                            meshInfo.BlendShapeDirty = false;
                            continue;
                        }

                        // Dispatches EVERY frame because bone matrices change each frame.
                        // The fused compute shader applies both blend shapes and skeletal
                        // skinning in a single dispatch, eliminating the VS skinning pass.
                        DeformFused(meshInfo, mesh, modelComponent);

                        // Save weights for informational purposes (dirty detection unused in fused mode)
                        BlendShapeDeformer.CopyWeights(meshInfo.BlendShapeWeights, meshInfo.PreviousWeights);
                        meshInfo.BlendShapeDirty = false;
                    }
                    else
                    {
                        // --- Blend-shape-only path (dispatches only when weights change) ---
                        bool isInitialized = useGpu ? meshInfo.GpuBlendShapeInitialized : meshInfo.BlendShapeInitialized;
                        if (meshInfo.PreviousWeights != null &&
                            !BlendShapeDeformer.AreWeightsDirty(meshInfo.BlendShapeWeights, meshInfo.PreviousWeights) &&
                            isInitialized)
                        {
                            continue; // No change since last frame — skip deformation
                        }

                        meshInfo.BlendShapeDirty = true;

                        if (useGpu)
                        {
                            DeformGpu(meshInfo, mesh);
                        }
                        else
                        {
                            DeformCpu(commandList, meshInfo, mesh, targets);
                        }

                        BlendShapeDeformer.CopyWeights(meshInfo.BlendShapeWeights, meshInfo.PreviousWeights);
                        meshInfo.BlendShapeDirty = false;
                    }
                }
            }
        }

        /// <summary>
        /// GPU compute shader deformation path. Dispatches the blend shape compute shader
        /// to deform the vertex buffer entirely on the GPU.
        /// Prefers the sparse CSR path when cooked data is present; falls back to the
        /// legacy dense path for assets imported without cooking.
        /// </summary>
        private void DeformGpu(ModelComponent.MeshInfo meshInfo, Mesh mesh)
        {
            var hasSparse = mesh.BlendShapes?.CookedData != null;

            // Lazy-initialize GPU buffers on first use, choosing the appropriate path.
            if (!meshInfo.GpuBlendShapeInitialized)
            {
                if (hasSparse)
                    gpuDeformer.InitializeGpuBuffersSparse(meshInfo, mesh.BlendShapes, mesh);
                else
                    gpuDeformer.InitializeGpuBuffers(meshInfo, mesh.BlendShapes, mesh);
            }

            // Dispatch — sparse path reads only the vertices that each shape actually affects.
            if (hasSparse && meshInfo.GpuSparseInitialized)
                gpuDeformer.DispatchSparse(graphicsContext, meshInfo, mesh.BlendShapes, meshInfo.BlendShapeWeights);
            else
                gpuDeformer.Dispatch(graphicsContext, meshInfo, mesh.BlendShapes, meshInfo.BlendShapeWeights);
        }

        /// <summary>
        /// Fused blend shape + skinning GPU compute path.
        /// Applies both blend shape deltas and skeletal skinning in a single compute dispatch,
        /// eliminating the VS skinning pass entirely. Dispatches every frame because bone
        /// matrices change with skeletal animation.
        /// </summary>
        private void DeformFused(ModelComponent.MeshInfo meshInfo, Mesh mesh, ModelComponent modelComponent)
        {
            // Lazy-initialize GPU buffers (blend shape + bone data) on first use
            if (!meshInfo.GpuBlendShapeInitialized)
            {
                gpuDeformer.InitializeGpuBuffers(meshInfo, mesh.BlendShapes, mesh);
            }

            if (!meshInfo.UseFusedSkinning)
            {
                gpuDeformer.InitializeFusedSkinningBuffers(meshInfo, mesh);
            }

            // Get the mesh's world matrix from the skeleton
            var skeleton = modelComponent.Skeleton;
            Matrix meshWorld;
            if (skeleton != null && (uint)mesh.NodeIndex < (uint)skeleton.NodeTransformations.Length)
            {
                meshWorld = skeleton.NodeTransformations[mesh.NodeIndex].WorldMatrix;
            }
            else
            {
                meshWorld = modelComponent.Entity.Transform.WorldMatrix;
            }

            // Dispatch the fused compute shader
            gpuDeformer.DispatchFused(graphicsContext, meshInfo, mesh.BlendShapes,
                meshInfo.BlendShapeWeights, ref meshWorld);
        }

        /// <summary>
        /// CPU fallback deformation path. Used when GPU compute shaders are unavailable.
        /// Performs multi-threaded SIMD vertex deformation on the CPU and uploads to a dynamic vertex buffer.
        /// </summary>
        private void DeformCpu(CommandList commandList, ModelComponent.MeshInfo meshInfo, Mesh mesh, BlendShapeTarget[] targets)
        {
            // Cache locally to avoid TOCTOU if another thread nulls the field
            var clonedMeshDraw = meshInfo.ClonedMeshDraw;

            // Ensure we have the CPU staging buffer and cloned MeshDraw
            if (meshInfo.DeformedVertexData == null || clonedMeshDraw == null)
                return;

            // Copy the full base vertex buffer if not initialized yet
            // (we only overwrite pos/nrm/tan offsets — other data like UVs, colors, bone weights must be preserved)
            if (!meshInfo.BlendShapeInitialized && mesh.Draw?.VertexBuffers != null && mesh.Draw.VertexBuffers.Length > 0)
            {
                var baseVB = mesh.Draw.VertexBuffers[0];
                var serializationData = baseVB.Buffer?.GetSerializationData();
                if (serializationData?.Content != null)
                {
                    if (meshInfo.DeformedVertexData.Length < serializationData.Content.Length)
                    {
                        Array.Resize(ref meshInfo.DeformedVertexData, serializationData.Content.Length);
                    }
                    var copyLen = Math.Min(serializationData.Content.Length, meshInfo.DeformedVertexData.Length);
                    Array.Copy(serializationData.Content, meshInfo.DeformedVertexData, copyLen);
                }
                else
                {
                    // Fallback: reconstruct base data from MeshBlendShapeDefinition (UVs etc. will be zero)
                    BlendShapeDeformer.Deform(mesh.BlendShapes, new float[targets.Length], meshInfo.DeformedVertexData);
                }
            }

            // Perform CPU deformation
            BlendShapeDeformer.Deform(mesh.BlendShapes, meshInfo.BlendShapeWeights, meshInfo.DeformedVertexData);
            meshInfo.BlendShapeInitialized = true;

            // Upload deformed data to dynamic GPU vertex buffer
            if (commandList != null && clonedMeshDraw.VertexBuffers != null && clonedMeshDraw.VertexBuffers.Length > 0)
            {
                var dynamicBuffer = clonedMeshDraw.VertexBuffers[0].Buffer;

                // Lazy-create the dynamic vertex buffer on first use
                if (dynamicBuffer == null || dynamicBuffer.SizeInBytes != meshInfo.DeformedVertexData.Length)
                {
                    dynamicBuffer?.Dispose();
                    dynamicBuffer = Buffer.Vertex.New(graphicsDevice, meshInfo.DeformedVertexData.Length, GraphicsResourceUsage.Dynamic);

                    // Replace VB[0] binding with the new dynamic buffer
                    var origBinding = mesh.Draw.VertexBuffers[0];
                    clonedMeshDraw.VertexBuffers[0] = new VertexBufferBinding(
                        dynamicBuffer, origBinding.Declaration, origBinding.Count, origBinding.Stride, origBinding.Offset);
                }

                dynamicBuffer.SetData(commandList, meshInfo.DeformedVertexData);
            }
        }

        public class BlendShapeData
        {
            public BlendShapeComponent BlendShapeComponent;
            public ModelComponent ModelComponent;
            /// <summary>
            /// Tracks the last observed <see cref="BlendShapeComponent.DictionaryRevision"/>
            /// so the processor knows whether the user edited weights via the dictionary API
            /// or whether changes came from the animation system writing to <see cref="BlendShapeComponent.WeightValues"/>.
            /// </summary>
            public long LastDictionaryRevision;
        }
    }
}
