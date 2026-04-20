// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using Stride.Core;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Graphics.Data;
using Stride.Rendering;
using Stride.Rendering.BlendShapes;
using Stride.Rendering.ComputeEffect;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Engine.Processors
{
    /// <summary>
    /// GPU-side blend shape (morph target) vertex deformation engine.
    /// Uses a compute shader to apply weighted deltas directly to a GPU vertex buffer,
    /// avoiding CPU-side vertex processing and CPU→GPU data uploads each frame.
    ///
    /// Supports two modes:
    /// 1. Blend-shape-only: dispatches only when weights change (dirty flagging).
    /// 2. Fused blend-shape + skinning: dispatches every frame (bone matrices change each frame).
    ///    The fused mode applies both blend shape deltas and skeletal skinning in a single
    ///    compute dispatch, eliminating the redundant VB read for VS skinning.
    ///
    /// Optimization features:
    /// - Active target compaction: only targets with |weight| > epsilon are processed on GPU
    /// - Weight epsilon threshold: negligible weights are skipped entirely
    /// - Dirty-aware dispatch (blend-only mode): processor skips dispatch when weights haven't changed
    /// - Dense delta layout: coalesced GPU memory access for maximum bandwidth utilization
    /// - Pre-allocated scratch arrays: no per-frame heap allocations
    /// - Object-space bone matrices: compute on CPU, avoids modifying VS transform chain
    /// </summary>
    public class BlendShapeGpuDeformer : IDisposable
    {
        private static readonly Logger Log = GlobalLogger.GetLogger(nameof(BlendShapeGpuDeformer));

        private readonly GraphicsDevice graphicsDevice;
        private readonly IServiceRegistry services;
        private GraphicsContext graphicsContext;
        private ComputeEffectShader computeEffect;
        private ComputeEffectShader fusedComputeEffect;
        private ComputeEffectShader sparseComputeEffect;
        private RenderContext renderContext;
        private RenderDrawContext renderDrawContext;
        private bool isInitialized;

        private const int ThreadGroupSize = 64;

        /// <summary>
        /// Weight threshold below which a blend shape target is considered inactive.
        /// Prevents wasting GPU cycles on imperceptible deformations.
        /// Unity uses ~0.0001, Unreal uses ~0.001. We use 0.0001 for higher fidelity.
        /// </summary>
        public const float WeightEpsilon = 0.0001f;

        // Pre-allocated CPU scratch arrays for building the active target list.
        // Sized to the largest target count seen so far. Avoids per-frame heap allocations.
        private uint[] cpuActiveIndices;
        private float[] cpuActiveWeights;

        public BlendShapeGpuDeformer(GraphicsDevice graphicsDevice, GraphicsContext graphicsContext, IServiceRegistry services)
        {
            this.graphicsDevice = graphicsDevice ?? throw new ArgumentNullException(nameof(graphicsDevice));
            this.graphicsContext = graphicsContext ?? throw new ArgumentNullException(nameof(graphicsContext));
            this.services = services ?? throw new ArgumentNullException(nameof(services));
        }

        /// <summary>
        /// Lazy-initializes the compute shader effect. Must be called from the render thread.
        /// </summary>
        private void EnsureInitialized(GraphicsContext graphicsContext)
        {
            if (isInitialized)
                return;

            renderContext = RenderContext.GetShared(services);
            renderDrawContext = new RenderDrawContext(services, renderContext, graphicsContext);

            computeEffect = new ComputeEffectShader(renderContext)
            {
                ShaderSourceName = "BlendShapeDeform",
                ThreadNumbers = new Int3(ThreadGroupSize, 1, 1),
            };

            fusedComputeEffect = new ComputeEffectShader(renderContext)
            {
                ShaderSourceName = "BlendShapeSkinningFused",
                ThreadNumbers = new Int3(ThreadGroupSize, 1, 1),
            };

            sparseComputeEffect = new ComputeEffectShader(renderContext)
            {
                ShaderSourceName = "BlendShapeDeformSparse",
                ThreadNumbers = new Int3(ThreadGroupSize, 1, 1),
            };

            isInitialized = true;
        }

        /// <summary>
        /// Creates GPU-resident StructuredBuffers for the sparse CSR blendshape path.
        /// Called once per entity per mesh when the model is first used and cooked data is available.
        /// Uploads the vertex-CSR layout, contribution lists, and base attributes.
        /// Falls through to <see cref="InitializeGpuBuffers"/> for the shared output-VB setup.
        /// </summary>
        public void InitializeGpuBuffersSparse(ModelComponent.MeshInfo meshInfo, MeshBlendShapeDefinition definition, Mesh mesh)
        {
            if (meshInfo.GpuSparseInitialized)
                return;

            var cooked = definition?.CookedData;
            if (cooked == null || mesh?.Draw?.VertexBuffers == null)
                return;

            var targetCount = definition.Targets?.Length ?? 1;

            // --- Base mesh buffers (shared with dense path) ---
            meshInfo.GpuBasePositions = Buffer.Structured.New(graphicsDevice, definition.BasePositions);
            meshInfo.GpuBaseNormals   = Buffer.Structured.New(graphicsDevice, definition.BaseNormals);
            if (definition.BaseTangents != null)
                meshInfo.GpuBaseTangents = Buffer.Structured.New(graphicsDevice, definition.BaseTangents);

            // --- Sparse CSR vertex table (immutable) ---
            meshInfo.GpuSparseVertexOffset = Buffer.Structured.New(graphicsDevice, cooked.VertexContribOffset);
            meshInfo.GpuSparseVertexCount  = Buffer.Structured.New(graphicsDevice, cooked.VertexContribCount);

            // --- Flat contribution arrays (immutable) ---
            meshInfo.GpuSparseShapeIndices = Buffer.Structured.New(graphicsDevice, cooked.ContribShapeIndices);
            meshInfo.GpuSparsePosDeltas    = Buffer.Structured.New(graphicsDevice, cooked.ContribPosDeltas);
            meshInfo.GpuSparseNrmDeltas    = Buffer.Structured.New(graphicsDevice, cooked.ContribNrmDeltas);
            if (cooked.HasTangentContribs && cooked.ContribTanDeltas != null)
                meshInfo.GpuSparseTanDeltas = Buffer.Structured.New(graphicsDevice, cooked.ContribTanDeltas);

            // Dynamic weight buffer — full target count, updated every dirty frame.
            meshInfo.GpuAllWeightsBuffer = Buffer.Structured.New<float>(graphicsDevice, Math.Max(1, targetCount));

            // --- Output vertex buffer (UAV + raw, used by draw calls and written by compute) ---
            if (mesh.Draw.VertexBuffers.Length > 0)
            {
                var baseVB     = mesh.Draw.VertexBuffers[0];
                var bufferSize = baseVB.Stride * baseVB.Count;

                var outputBuffer = Buffer.Vertex.New(
                    graphicsDevice,
                    bufferSize,
                    GraphicsResourceUsage.Default,
                    BufferFlags.RawBuffer | BufferFlags.UnorderedAccess);

                meshInfo.ClonedMeshDraw = mesh.Draw.Clone();
                var origBinding = mesh.Draw.VertexBuffers[0];
                meshInfo.ClonedMeshDraw.VertexBuffers[0] = new VertexBufferBinding(
                    outputBuffer, origBinding.Declaration, origBinding.Count, origBinding.Stride, origBinding.Offset);

                CopyOriginalVertexBufferContent(mesh, meshInfo, bufferSize);
            }

            meshInfo.GpuSparseInitialized    = true;
            meshInfo.GpuBlendShapeInitialized = true;
        }

        /// <summary>
        /// Dispatches the sparse CSR blendshape compute shader.
        /// Uploads the full weight array (all targets, ~200–500 B for typical rigs) and
        /// dispatches one thread per vertex. Only vertices with non-zero contributions are
        /// affected; unaffected vertices exit immediately inside the shader.
        /// </summary>
        public void DispatchSparse(GraphicsContext graphicsContext, ModelComponent.MeshInfo meshInfo,
            MeshBlendShapeDefinition definition, float[] weights)
        {
            if (!meshInfo.GpuSparseInitialized || definition?.CookedData == null || weights == null)
                return;

            EnsureInitialized(graphicsContext);

            var commandList  = graphicsContext.CommandList;
            var cooked       = definition.CookedData;
            var vertexCount  = definition.VertexCount;
            var hasTangents  = cooked.HasTangentContribs && meshInfo.GpuSparseTanDeltas != null && meshInfo.GpuBaseTangents != null;

            // Upload the full weights array (indexed by original target index).
            // Small upload: max-128-shape rig = 512 B — negligible bandwidth cost.
            int targetCount = definition.Targets?.Length ?? weights.Length;
            if (weights.Length < targetCount)
            {
                var paddedWeights = new float[targetCount];
                Array.Copy(weights, paddedWeights, weights.Length);
                meshInfo.GpuAllWeightsBuffer.SetData(commandList, new ReadOnlySpan<float>(paddedWeights));
            }
            else
            {
                meshInfo.GpuAllWeightsBuffer.SetData(commandList, new ReadOnlySpan<float>(weights, 0, targetCount));
            }

            var outputBuffer = meshInfo.ClonedMeshDraw.VertexBuffers[0].Buffer;

            var parameters = sparseComputeEffect.Parameters;
            parameters.Set(BlendShapeDeformSparseKeys.BasePositions,      meshInfo.GpuBasePositions);
            parameters.Set(BlendShapeDeformSparseKeys.BaseNormals,        meshInfo.GpuBaseNormals);
            parameters.Set(BlendShapeDeformSparseKeys.BaseTangents,       hasTangents ? meshInfo.GpuBaseTangents : meshInfo.GpuBaseNormals);
            parameters.Set(BlendShapeDeformSparseKeys.VertexContribOffset, meshInfo.GpuSparseVertexOffset);
            parameters.Set(BlendShapeDeformSparseKeys.VertexContribCount,  meshInfo.GpuSparseVertexCount);
            parameters.Set(BlendShapeDeformSparseKeys.ContribShapeIndices, meshInfo.GpuSparseShapeIndices);
            parameters.Set(BlendShapeDeformSparseKeys.ContribPosDeltas,    meshInfo.GpuSparsePosDeltas);
            parameters.Set(BlendShapeDeformSparseKeys.ContribNrmDeltas,    meshInfo.GpuSparseNrmDeltas);
            parameters.Set(BlendShapeDeformSparseKeys.ContribTanDeltas,    hasTangents ? meshInfo.GpuSparseTanDeltas : meshInfo.GpuSparseNrmDeltas);
            parameters.Set(BlendShapeDeformSparseKeys.AllWeights,          meshInfo.GpuAllWeightsBuffer);
            parameters.Set(BlendShapeDeformSparseKeys.OutputVertexBuffer,  outputBuffer);
            parameters.Set(BlendShapeDeformSparseKeys.VertexCount,    vertexCount);
            parameters.Set(BlendShapeDeformSparseKeys.VertexStride,   definition.VertexStride);
            parameters.Set(BlendShapeDeformSparseKeys.PositionOffset, definition.PositionOffset);
            parameters.Set(BlendShapeDeformSparseKeys.NormalOffset,   definition.NormalOffset);
            parameters.Set(BlendShapeDeformSparseKeys.TangentOffset,  hasTangents ? definition.TangentOffset : -1);

            var groupCountX = (vertexCount + ThreadGroupSize - 1) / ThreadGroupSize;
            sparseComputeEffect.ThreadGroupCounts = new Int3(groupCountX, 1, 1);
            sparseComputeEffect.Draw(renderDrawContext);
        }

        /// <summary>
        /// Creates GPU-resident StructuredBuffers for blend shape base data, deltas, and active target compaction.
        /// Called once per entity per mesh when the model is first used.
        /// </summary>
        public void InitializeGpuBuffers(ModelComponent.MeshInfo meshInfo, MeshBlendShapeDefinition definition, Mesh mesh)
        {
            if (meshInfo.GpuBlendShapeInitialized)
                return;

            if (definition?.Targets == null || definition.Targets.Length == 0 || mesh?.Draw?.VertexBuffers == null)
                return;

            var vertexCount = definition.VertexCount;
            var targetCount = definition.Targets.Length;
            var hasTangents = definition.TangentOffset >= 0 && definition.BaseTangents != null;

            // --- Base mesh StructuredBuffers (immutable, uploaded once) ---
            meshInfo.GpuBasePositions = Buffer.Structured.New(graphicsDevice, definition.BasePositions);
            meshInfo.GpuBaseNormals = Buffer.Structured.New(graphicsDevice, definition.BaseNormals);

            if (hasTangents)
            {
                meshInfo.GpuBaseTangents = Buffer.Structured.New(graphicsDevice, definition.BaseTangents);
            }

            // --- Dense per-target deltas (immutable, uploaded once) ---
            // Layout: [target0_vert0..target0_vertN-1, target1_vert0..target1_vertN-1, ...]
            // Dense layout chosen over sparse for coalesced GPU memory access.
            var packedPositionDeltas = new Vector3[targetCount * vertexCount];
            var packedNormalDeltas = new Vector3[targetCount * vertexCount];
            Vector3[] packedTangentDeltas = hasTangents ? new Vector3[targetCount * vertexCount] : null;

            for (int t = 0; t < targetCount; t++)
            {
                var target = definition.Targets[t];
                var offset = t * vertexCount;

                if (target?.DeltaPositions != null && target.HasDeltaPositions)
                    Array.Copy(target.DeltaPositions, 0, packedPositionDeltas, offset, Math.Min(target.DeltaPositions.Length, vertexCount));

                if (target?.DeltaNormals != null && target.HasDeltaNormals)
                    Array.Copy(target.DeltaNormals, 0, packedNormalDeltas, offset, Math.Min(target.DeltaNormals.Length, vertexCount));

                if (hasTangents && target?.DeltaTangents != null && target.HasDeltaTangents)
                    Array.Copy(target.DeltaTangents, 0, packedTangentDeltas, offset, Math.Min(target.DeltaTangents.Length, vertexCount));
            }

            meshInfo.GpuDeltaPositions = Buffer.Structured.New(graphicsDevice, packedPositionDeltas);
            meshInfo.GpuDeltaNormals = Buffer.Structured.New(graphicsDevice, packedNormalDeltas);

            if (hasTangents)
            {
                meshInfo.GpuDeltaTangents = Buffer.Structured.New(graphicsDevice, packedTangentDeltas);
            }

            // --- Active target compaction buffers (dynamic, updated per dirty frame) ---
            // Pre-allocated at max target count. Shader only reads [0..ActiveTargetCount).
            meshInfo.GpuActiveIndicesBuffer = Buffer.Structured.New<uint>(graphicsDevice, targetCount);
            meshInfo.GpuActiveWeightsBuffer = Buffer.Structured.New<float>(graphicsDevice, targetCount);

            // --- Output vertex buffer (VertexBuffer + RawBuffer + UAV) ---
            // Used directly by draw calls as VB[0] AND written to by the compute shader.
            if (mesh.Draw.VertexBuffers.Length > 0)
            {
                var baseVB = mesh.Draw.VertexBuffers[0];
                var bufferSize = baseVB.Stride * baseVB.Count;

                var outputBuffer = Buffer.Vertex.New(
                    graphicsDevice,
                    bufferSize,
                    GraphicsResourceUsage.Default,
                    BufferFlags.RawBuffer | BufferFlags.UnorderedAccess);

                // Clone MeshDraw and replace VB[0] with our UAV-capable output buffer
                meshInfo.ClonedMeshDraw = mesh.Draw.Clone();
                var origBinding = mesh.Draw.VertexBuffers[0];
                meshInfo.ClonedMeshDraw.VertexBuffers[0] = new VertexBufferBinding(
                    outputBuffer, origBinding.Declaration, origBinding.Count, origBinding.Stride, origBinding.Offset);

                // Copy original VB content to preserve UVs, bone weights, colors, etc.
                // The compute shader only overwrites position/normal/tangent byte ranges.
                CopyOriginalVertexBufferContent(mesh, meshInfo, bufferSize);
            }

            meshInfo.GpuBlendShapeInitialized = true;
        }

        /// <summary>
        /// Copies the original vertex buffer content into the output UAV vertex buffer.
        /// Preserves all vertex attributes the compute shader doesn't touch (UVs, bone weights, etc.).
        /// </summary>
        private void CopyOriginalVertexBufferContent(Mesh mesh, ModelComponent.MeshInfo meshInfo, int bufferSize)
        {
            if (meshInfo.ClonedMeshDraw?.VertexBuffers == null || meshInfo.ClonedMeshDraw.VertexBuffers.Length == 0)
                return;

            var sourceVB = mesh.Draw.VertexBuffers[0].Buffer;
            var destVB = meshInfo.ClonedMeshDraw.VertexBuffers[0].Buffer;

            var serializationData = sourceVB?.GetSerializationData();
            if (serializationData?.Content != null)
            {
                destVB.SetData(graphicsContext.CommandList, new ReadOnlySpan<byte>(serializationData.Content));
            }
            else
            {
                // Procedural meshes or runtime-created buffers may lack serialization data.
                // Attempt GPU→GPU copy as fallback; if that also fails, log a warning so
                // the user knows UVs/colors/bone-weights in the output buffer are uninitialized.
                try
                {
                    if (sourceVB != null)
                    {
                        graphicsContext.CommandList.CopyRegion(sourceVB, 0, null, destVB, 0);
                    }
                    else
                    {
                        Log.Warning("BlendShape GPU deformer: source vertex buffer is null. " +
                            "Output buffer may contain uninitialized vertex attributes (UVs, colors, bone weights).");
                    }
                }
                catch
                {
                    Log.Warning("BlendShape GPU deformer: could not copy original vertex buffer content. " +
                        "Output buffer may contain uninitialized vertex attributes (UVs, colors, bone weights). " +
                        "This can happen with procedural meshes that lack serialization data.");
                }
            }
        }

        /// <summary>
        /// Initializes GPU buffers for fused blend shape + skinning.
        /// Extracts per-vertex bone weights and indices from the original vertex buffer
        /// and uploads them as StructuredBuffers for the fused compute shader.
        /// Also pre-allocates the CPU-side bone matrix array and GPU bone matrix buffer.
        /// </summary>
        /// <param name="meshInfo">The per-entity mesh info to initialize.</param>
        /// <param name="mesh">The mesh with vertex buffer and skinning data.</param>
        public void InitializeFusedSkinningBuffers(ModelComponent.MeshInfo meshInfo, Mesh mesh)
        {
            if (meshInfo.UseFusedSkinning)
                return; // Already initialized

            if (mesh?.Draw?.VertexBuffers == null || mesh.Draw.VertexBuffers.Length == 0)
                return;

            if (mesh.Skinning?.Bones == null || mesh.Skinning.Bones.Length == 0)
                return;

            var vbBinding = mesh.Draw.VertexBuffers[0];
            var declaration = vbBinding.Declaration;
            var stride = vbBinding.Stride;
            var vertexCount = vbBinding.Count;

            // Find BLENDWEIGHT and BLENDINDICES elements in the vertex declaration
            int blendWeightOffset = -1;
            int blendIndicesOffset = -1;
            PixelFormat blendIndicesFormat = PixelFormat.None;

            foreach (var element in declaration.VertexElements)
            {
                if (element.SemanticName == "BLENDWEIGHT" && element.SemanticIndex == 0)
                    blendWeightOffset = element.AlignedByteOffset;
                else if (element.SemanticName == "BLENDINDICES" && element.SemanticIndex == 0)
                {
                    blendIndicesOffset = element.AlignedByteOffset;
                    blendIndicesFormat = element.Format;
                }
            }

            if (blendWeightOffset < 0 || blendIndicesOffset < 0)
                return; // No skinning data in VB

            // Read the original vertex buffer data to extract bone weights/indices
            var sourceVB = vbBinding.Buffer;
            var serializationData = sourceVB?.GetSerializationData();
            if (serializationData?.Content == null)
            {
                Log.Warning("BlendShape fused skinning: vertex buffer has no serialization data. " +
                    "Fused skinning will not be initialized. This can happen with procedural meshes.");
                return;
            }

            var vbData = serializationData.Content;
            var boneWeights = new Vector4[vertexCount];
            var boneIndices = new Int4[vertexCount]; // uint4 for shader

            for (int v = 0; v < vertexCount; v++)
            {
                int vertexBase = v * stride;

                // Extract bone weights (float4 = 16 bytes)
                int wOff = vertexBase + blendWeightOffset;
                boneWeights[v] = new Vector4(
                    BitConverter.ToSingle(vbData, wOff),
                    BitConverter.ToSingle(vbData, wOff + 4),
                    BitConverter.ToSingle(vbData, wOff + 8),
                    BitConverter.ToSingle(vbData, wOff + 12));

                // Extract bone indices — format depends on import (ushort4 or uint4)
                int iOff = vertexBase + blendIndicesOffset;
                if (blendIndicesFormat == PixelFormat.R16G16B16A16_UInt)
                {
                    // ushort4 → uint4
                    boneIndices[v] = new Int4(
                        BitConverter.ToUInt16(vbData, iOff),
                        BitConverter.ToUInt16(vbData, iOff + 2),
                        BitConverter.ToUInt16(vbData, iOff + 4),
                        BitConverter.ToUInt16(vbData, iOff + 6));
                }
                else if (blendIndicesFormat == PixelFormat.R8G8B8A8_UInt || blendIndicesFormat == PixelFormat.R8G8B8A8_UNorm)
                {
                    // byte4 → uint4
                    boneIndices[v] = new Int4(
                        vbData[iOff],
                        vbData[iOff + 1],
                        vbData[iOff + 2],
                        vbData[iOff + 3]);
                }
                else if (blendIndicesFormat == PixelFormat.R32G32B32A32_UInt || blendIndicesFormat == PixelFormat.R32G32B32A32_Float)
                {
                    // uint4 (R32G32B32A32_UInt) — 16 bytes
                    boneIndices[v] = new Int4(
                        BitConverter.ToInt32(vbData, iOff),
                        BitConverter.ToInt32(vbData, iOff + 4),
                        BitConverter.ToInt32(vbData, iOff + 8),
                        BitConverter.ToInt32(vbData, iOff + 12));
                }
                else
                {
                    throw new InvalidOperationException($"BlendShape fused skinning extraction does not support bone indices format: {blendIndicesFormat}");
                }
            }

            // Upload to GPU StructuredBuffers (immutable, read by compute shader)
            meshInfo.GpuVertexBoneWeights = Buffer.Structured.New(graphicsDevice, boneWeights);
            meshInfo.GpuVertexBoneIndices = Buffer.Structured.New(graphicsDevice, boneIndices);

            // Pre-allocate bone matrix arrays
            var boneCount = mesh.Skinning.Bones.Length;
            meshInfo.ObjectSpaceBoneMatrices = new Matrix[boneCount];
            meshInfo.GpuBoneMatricesBuffer = Buffer.Structured.New<Matrix>(graphicsDevice, boneCount);

            meshInfo.UseFusedSkinning = true;
        }

        /// <summary>
        /// Dispatches the fused blend shape + skinning compute shader.
        /// Must be called every frame for skinned blend shape meshes (bone matrices change each frame).
        /// </summary>
        /// <param name="graphicsContext">The current graphics context.</param>
        /// <param name="meshInfo">The per-entity mesh info with GPU buffers.</param>
        /// <param name="definition">The blend shape definition for layout info.</param>
        /// <param name="weights">Current blend shape weights (full array, all targets).</param>
        /// <param name="meshWorld">The mesh's world matrix (from nodeTransformations[mesh.NodeIndex].WorldMatrix).</param>
        public void DispatchFused(GraphicsContext graphicsContext, ModelComponent.MeshInfo meshInfo,
            MeshBlendShapeDefinition definition, float[] weights, ref Matrix meshWorld)
        {
            if (!meshInfo.GpuBlendShapeInitialized || !meshInfo.UseFusedSkinning ||
                definition == null || weights == null || meshInfo.BlendMatrices == null)
                return;

            EnsureInitialized(graphicsContext);

            var commandList = graphicsContext.CommandList;
            var vertexCount = definition.VertexCount;
            var targetCount = definition.Targets.Length;
            var hasTangents = definition.TangentOffset >= 0 && definition.BaseTangents != null;

            // --- Compute object-space bone matrices ---
            // ObjectSpaceBone[i] = BlendMatrices[i] * MeshWorldInverse
            // This transforms vertices from bind space to object/model space.
            // The VS then applies the entity's World matrix normally.
            ComputeObjectSpaceBoneMatrices(meshInfo, ref meshWorld);

            // Upload bone matrices to GPU (every frame — bones animate)
            meshInfo.GpuBoneMatricesBuffer.SetData(commandList,
                new ReadOnlySpan<Matrix>(meshInfo.ObjectSpaceBoneMatrices));

            // --- Active target compaction (same as blend-only path) ---
            EnsureScratchArraySize(targetCount);
            Array.Clear(cpuActiveIndices, 0, targetCount);
            Array.Clear(cpuActiveWeights, 0, targetCount);

            int activeCount = 0;
            for (int t = 0; t < targetCount && t < weights.Length; t++)
            {
                if (Math.Abs(weights[t]) > WeightEpsilon)
                {
                    cpuActiveIndices[activeCount] = (uint)t;
                    cpuActiveWeights[activeCount] = weights[t];
                    activeCount++;
                }
            }

            meshInfo.GpuActiveIndicesBuffer.SetData(commandList, new ReadOnlySpan<uint>(cpuActiveIndices, 0, targetCount));
            meshInfo.GpuActiveWeightsBuffer.SetData(commandList, new ReadOnlySpan<float>(cpuActiveWeights, 0, targetCount));

            var outputBuffer = meshInfo.ClonedMeshDraw.VertexBuffers[0].Buffer;

            // --- Set fused compute shader parameters ---
            var parameters = fusedComputeEffect.Parameters;

            // Blend shape data
            parameters.Set(BlendShapeSkinningFusedKeys.BasePositions, meshInfo.GpuBasePositions);
            parameters.Set(BlendShapeSkinningFusedKeys.BaseNormals, meshInfo.GpuBaseNormals);
            parameters.Set(BlendShapeSkinningFusedKeys.BaseTangents, hasTangents ? meshInfo.GpuBaseTangents : meshInfo.GpuBaseNormals);
            parameters.Set(BlendShapeSkinningFusedKeys.DeltaPositions, meshInfo.GpuDeltaPositions);
            parameters.Set(BlendShapeSkinningFusedKeys.DeltaNormals, meshInfo.GpuDeltaNormals);
            parameters.Set(BlendShapeSkinningFusedKeys.DeltaTangents, hasTangents ? meshInfo.GpuDeltaTangents : meshInfo.GpuDeltaNormals);
            parameters.Set(BlendShapeSkinningFusedKeys.ActiveTargetIndices, meshInfo.GpuActiveIndicesBuffer);
            parameters.Set(BlendShapeSkinningFusedKeys.ActiveWeights, meshInfo.GpuActiveWeightsBuffer);

            // Skinning data
            parameters.Set(BlendShapeSkinningFusedKeys.BoneMatrices, meshInfo.GpuBoneMatricesBuffer);
            parameters.Set(BlendShapeSkinningFusedKeys.VertexBoneWeights, meshInfo.GpuVertexBoneWeights);
            parameters.Set(BlendShapeSkinningFusedKeys.VertexBoneIndices, meshInfo.GpuVertexBoneIndices);

            // Output
            parameters.Set(BlendShapeSkinningFusedKeys.OutputVertexBuffer, outputBuffer);

            // Uniforms
            parameters.Set(BlendShapeSkinningFusedKeys.VertexCount, vertexCount);
            parameters.Set(BlendShapeSkinningFusedKeys.ActiveTargetCount, activeCount);
            parameters.Set(BlendShapeSkinningFusedKeys.VertexStride, definition.VertexStride);
            parameters.Set(BlendShapeSkinningFusedKeys.PositionOffset, definition.PositionOffset);
            parameters.Set(BlendShapeSkinningFusedKeys.NormalOffset, definition.NormalOffset);
            parameters.Set(BlendShapeSkinningFusedKeys.TangentOffset, hasTangents ? definition.TangentOffset : -1);

            // --- Dispatch ---
            var groupCountX = (vertexCount + ThreadGroupSize - 1) / ThreadGroupSize;
            fusedComputeEffect.ThreadGroupCounts = new Int3(groupCountX, 1, 1);
            fusedComputeEffect.Draw(renderDrawContext);
        }

        /// <summary>
        /// Computes object-space bone matrices by pre-multiplying world-space BlendMatrices
        /// with the inverse of the mesh's world matrix. This transforms vertices from bind
        /// space to object/model space, allowing the VS to apply the World matrix normally.
        ///
        /// Cost: 1 matrix inverse + N matrix multiplies per entity per frame (N = bone count).
        /// For a typical character with 56 bones, this is ~56 * 64 FLOPs ≈ 3.5K FLOPs — trivial.
        /// </summary>
        private static void ComputeObjectSpaceBoneMatrices(ModelComponent.MeshInfo meshInfo, ref Matrix meshWorld)
        {
            Matrix meshWorldInverse;
            Matrix.Invert(ref meshWorld, out meshWorldInverse);

            var blendMatrices = meshInfo.BlendMatrices;
            var objectSpace = meshInfo.ObjectSpaceBoneMatrices;
            var boneCount = Math.Min(blendMatrices.Length, objectSpace.Length);

            for (int i = 0; i < boneCount; i++)
            {
                Matrix.Multiply(ref blendMatrices[i], ref meshWorldInverse, out objectSpace[i]);
            }
        }

        /// <summary>
        /// Dispatches the compute shader to deform the vertex buffer on the GPU.
        /// Performs active target compaction: only targets with |weight| > epsilon are processed.
        /// </summary>
        /// <param name="graphicsContext">The current graphics context.</param>
        /// <param name="meshInfo">The per-entity mesh info with GPU buffers.</param>
        /// <param name="definition">The blend shape definition for layout info.</param>
        /// <param name="weights">Current blend shape weights (full array, all targets).</param>
        public void Dispatch(GraphicsContext graphicsContext, ModelComponent.MeshInfo meshInfo, MeshBlendShapeDefinition definition, float[] weights)
        {
            if (!meshInfo.GpuBlendShapeInitialized || definition == null || weights == null)
                return;

            EnsureInitialized(graphicsContext);

            var commandList = graphicsContext.CommandList;
            var vertexCount = definition.VertexCount;
            var targetCount = definition.Targets.Length;
            var hasTangents = definition.TangentOffset >= 0 && definition.BaseTangents != null;

            // --- Active target compaction (CPU-side filtering) ---
            // Build compact list of targets with significant weights.
            // This eliminates ALL cost for zero-weight targets on the GPU.
            EnsureScratchArraySize(targetCount);
            Array.Clear(cpuActiveIndices, 0, targetCount);
            Array.Clear(cpuActiveWeights, 0, targetCount);

            int activeCount = 0;
            for (int t = 0; t < targetCount && t < weights.Length; t++)
            {
                if (Math.Abs(weights[t]) > WeightEpsilon)
                {
                    cpuActiveIndices[activeCount] = (uint)t;
                    cpuActiveWeights[activeCount] = weights[t];
                    activeCount++;
                }
            }

            // Upload active target data to pre-allocated GPU buffers
            meshInfo.GpuActiveIndicesBuffer.SetData(commandList, new ReadOnlySpan<uint>(cpuActiveIndices, 0, targetCount));
            meshInfo.GpuActiveWeightsBuffer.SetData(commandList, new ReadOnlySpan<float>(cpuActiveWeights, 0, targetCount));

            // Get the output vertex buffer (VB[0] of the cloned MeshDraw)
            var outputBuffer = meshInfo.ClonedMeshDraw.VertexBuffers[0].Buffer;

            // --- Set compute shader parameters ---
            var parameters = computeEffect.Parameters;

            // Base data (set every dispatch — parameter binding, not data upload)
            parameters.Set(BlendShapeDeformKeys.BasePositions, meshInfo.GpuBasePositions);
            parameters.Set(BlendShapeDeformKeys.BaseNormals, meshInfo.GpuBaseNormals);
            parameters.Set(BlendShapeDeformKeys.BaseTangents, hasTangents ? meshInfo.GpuBaseTangents : meshInfo.GpuBaseNormals);

            // Delta data (dense, immutable)
            parameters.Set(BlendShapeDeformKeys.DeltaPositions, meshInfo.GpuDeltaPositions);
            parameters.Set(BlendShapeDeformKeys.DeltaNormals, meshInfo.GpuDeltaNormals);
            parameters.Set(BlendShapeDeformKeys.DeltaTangents, hasTangents ? meshInfo.GpuDeltaTangents : meshInfo.GpuDeltaNormals);

            // Active target compaction (updated this frame)
            parameters.Set(BlendShapeDeformKeys.ActiveTargetIndices, meshInfo.GpuActiveIndicesBuffer);
            parameters.Set(BlendShapeDeformKeys.ActiveWeights, meshInfo.GpuActiveWeightsBuffer);

            // Output
            parameters.Set(BlendShapeDeformKeys.OutputVertexBuffer, outputBuffer);

            // Uniforms
            parameters.Set(BlendShapeDeformKeys.VertexCount, vertexCount);
            parameters.Set(BlendShapeDeformKeys.ActiveTargetCount, activeCount);
            parameters.Set(BlendShapeDeformKeys.VertexStride, definition.VertexStride);
            parameters.Set(BlendShapeDeformKeys.PositionOffset, definition.PositionOffset);
            parameters.Set(BlendShapeDeformKeys.NormalOffset, definition.NormalOffset);
            parameters.Set(BlendShapeDeformKeys.TangentOffset, hasTangents ? definition.TangentOffset : -1);

            // --- Dispatch ---
            // Even with activeCount == 0, we dispatch to write base data back
            // (needed when transitioning from active → all-zero weights).
            var groupCountX = (vertexCount + ThreadGroupSize - 1) / ThreadGroupSize;
            computeEffect.ThreadGroupCounts = new Int3(groupCountX, 1, 1);
            computeEffect.Draw(renderDrawContext);
        }

        /// <summary>
        /// Ensures the CPU scratch arrays are large enough for the given target count.
        /// Arrays grow but never shrink — amortizes allocation across meshes with varying target counts.
        /// </summary>
        private void EnsureScratchArraySize(int targetCount)
        {
            if (cpuActiveIndices == null || cpuActiveIndices.Length < targetCount)
            {
                cpuActiveIndices = new uint[targetCount];
                cpuActiveWeights = new float[targetCount];
            }
        }

        /// <summary>
        /// Disposes GPU buffers for a specific mesh info. Called when entity is removed.
        /// </summary>
        public static void DisposeGpuBuffers(ModelComponent.MeshInfo meshInfo)
        {
            meshInfo.GpuBasePositions?.Dispose();
            meshInfo.GpuBasePositions = null;

            meshInfo.GpuBaseNormals?.Dispose();
            meshInfo.GpuBaseNormals = null;

            meshInfo.GpuBaseTangents?.Dispose();
            meshInfo.GpuBaseTangents = null;

            meshInfo.GpuDeltaPositions?.Dispose();
            meshInfo.GpuDeltaPositions = null;

            meshInfo.GpuDeltaNormals?.Dispose();
            meshInfo.GpuDeltaNormals = null;

            meshInfo.GpuDeltaTangents?.Dispose();
            meshInfo.GpuDeltaTangents = null;

            meshInfo.GpuActiveIndicesBuffer?.Dispose();
            meshInfo.GpuActiveIndicesBuffer = null;

            meshInfo.GpuActiveWeightsBuffer?.Dispose();
            meshInfo.GpuActiveWeightsBuffer = null;

            // Dispose fused skinning buffers
            meshInfo.GpuBoneMatricesBuffer?.Dispose();
            meshInfo.GpuBoneMatricesBuffer = null;

            meshInfo.GpuVertexBoneWeights?.Dispose();
            meshInfo.GpuVertexBoneWeights = null;

            meshInfo.GpuVertexBoneIndices?.Dispose();
            meshInfo.GpuVertexBoneIndices = null;

            meshInfo.ObjectSpaceBoneMatrices = null;
            meshInfo.UseFusedSkinning = false;

            // Dispose the output vertex buffer in the cloned MeshDraw
            if (meshInfo.ClonedMeshDraw?.VertexBuffers != null)
            {
                foreach (var vb in meshInfo.ClonedMeshDraw.VertexBuffers)
                {
                    vb.Buffer?.Dispose();
                }
            }
            meshInfo.ClonedMeshDraw = null;

            // Sparse buffers
            meshInfo.GpuSparseVertexOffset?.Dispose(); meshInfo.GpuSparseVertexOffset = null;
            meshInfo.GpuSparseVertexCount?.Dispose();  meshInfo.GpuSparseVertexCount  = null;
            meshInfo.GpuSparseShapeIndices?.Dispose(); meshInfo.GpuSparseShapeIndices = null;
            meshInfo.GpuSparsePosDeltas?.Dispose();    meshInfo.GpuSparsePosDeltas    = null;
            meshInfo.GpuSparseNrmDeltas?.Dispose();    meshInfo.GpuSparseNrmDeltas    = null;
            meshInfo.GpuSparseTanDeltas?.Dispose();    meshInfo.GpuSparseTanDeltas    = null;
            meshInfo.GpuAllWeightsBuffer?.Dispose();   meshInfo.GpuAllWeightsBuffer   = null;
            meshInfo.GpuSparseInitialized    = false;

            meshInfo.GpuBlendShapeInitialized = false;
        }

        /// <summary>
        /// Checks whether the GPU compute path is supported on the current device.
        /// </summary>
        public static bool IsSupported(GraphicsDevice device)
        {
            return device?.Features.HasComputeShaders ?? false;
        }

        public void Dispose()
        {
            computeEffect?.Dispose();
            computeEffect = null;

            fusedComputeEffect?.Dispose();
            fusedComputeEffect = null;

            sparseComputeEffect?.Dispose();
            sparseComputeEffect = null;

            renderDrawContext?.Dispose();
            renderDrawContext = null;

            isInitialized = false;
        }
    }
}
