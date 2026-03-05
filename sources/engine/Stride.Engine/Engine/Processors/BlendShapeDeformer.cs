// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Runtime.CompilerServices;
using Stride.Core.Mathematics;
using Stride.Core.Threading;
using Stride.Rendering;
using SysVector3 = System.Numerics.Vector3;

namespace Stride.Engine.Processors
{
    /// <summary>
    /// High-performance CPU-side blend shape (morph target) vertex deformation engine.
    /// Applies weighted deltas to base mesh positions, normals, and tangents, writing
    /// the result into an interleaved vertex buffer byte array for GPU upload.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="System.Numerics.Vector3"/> for SIMD auto-vectorization by RyuJIT,
    /// <see cref="Dispatcher.ForBatched"/> for multi-threaded vertex processing, and
    /// sparse target evaluation (skipping targets with zero weight).
    /// </remarks>
    public static class BlendShapeDeformer
    {
        /// <summary>
        /// Performs CPU blend shape deformation, writing deformed vertices into <paramref name="outputBytes"/>.
        /// </summary>
        /// <param name="definition">The blend shape definition containing base mesh data and layout info.</param>
        /// <param name="weights">Current blend shape weights, indexed by target index.</param>
        /// <param name="outputBytes">Output byte array (same layout as VB[0]) to write deformed data into.</param>
        public static void Deform(MeshBlendShapeDefinition definition, float[] weights, byte[] outputBytes)
        {
            if (definition == null || weights == null || outputBytes == null)
                return;

            var targets = definition.Targets;
            if (targets == null || targets.Length == 0)
                return;

            var vertexCount = definition.VertexCount;
            var stride = definition.VertexStride;
            var posOffset = definition.PositionOffset;
            var nrmOffset = definition.NormalOffset;
            var tanOffset = definition.TangentOffset;
            var hasTangent = tanOffset >= 0 && definition.BaseTangents != null;

            var basePositions = definition.BasePositions;
            var baseNormals = definition.BaseNormals;
            var baseTangents = definition.BaseTangents;

            // Pre-filter to only active targets (weight != 0) for sparse evaluation
            // Stack-allocate for small counts, heap for larger
            var activeTargetCount = 0;
            Span<int> activeIndices = targets.Length <= 64
                ? stackalloc int[targets.Length]
                : new int[targets.Length];
            Span<float> activeWeights = targets.Length <= 64
                ? stackalloc float[targets.Length]
                : new float[targets.Length];

            for (int t = 0; t < targets.Length && t < weights.Length; t++)
            {
                if (weights[t] != 0f)
                {
                    activeIndices[activeTargetCount] = t;
                    activeWeights[activeTargetCount] = weights[t];
                    activeTargetCount++;
                }
            }

            if (activeTargetCount == 0)
            {
                // All weights are zero — just write base data
                WriteBaseData(basePositions, baseNormals, baseTangents, hasTangent,
                    vertexCount, stride, posOffset, nrmOffset, tanOffset, outputBytes);
                return;
            }

            // Capture active targets into arrays for safe parallel access (can't pass Span to lambda)
            var activeTargetArray = new BlendShapeTarget[activeTargetCount];
            var activeWeightArray = new float[activeTargetCount];
            for (int i = 0; i < activeTargetCount; i++)
            {
                activeTargetArray[i] = targets[activeIndices[i]];
                activeWeightArray[i] = activeWeights[i];
            }

            // Multi-threaded vertex deformation
            var job = new DeformJob
            {
                BasePositions = basePositions,
                BaseNormals = baseNormals,
                BaseTangents = baseTangents,
                HasTangent = hasTangent,
                ActiveTargets = activeTargetArray,
                ActiveWeights = activeWeightArray,
                ActiveTargetCount = activeTargetCount,
                OutputBytes = outputBytes,
                Stride = stride,
                PosOffset = posOffset,
                NrmOffset = nrmOffset,
                TanOffset = tanOffset,
                VertexCount = vertexCount,
            };

            // Use batched dispatcher for parallelism — threshold at 256 verts to avoid overhead on tiny meshes
            if (vertexCount > 256)
            {
                Dispatcher.ForBatched(vertexCount, job);
            }
            else
            {
                job.Process(0, vertexCount);
            }
        }

        /// <summary>
        /// Writes base (undeformed) vertex data — used when all weights are zero.
        /// </summary>
        private static unsafe void WriteBaseData(
            Vector3[] basePositions, Vector3[] baseNormals, Vector3[] baseTangents, bool hasTangent,
            int vertexCount, int stride, int posOffset, int nrmOffset, int tanOffset, byte[] outputBytes)
        {
            fixed (byte* outPtr = outputBytes)
            {
                for (int v = 0; v < vertexCount; v++)
                {
                    var vertexBase = outPtr + v * stride;
                    *(Vector3*)(vertexBase + posOffset) = basePositions[v];
                    *(Vector3*)(vertexBase + nrmOffset) = baseNormals[v];
                    if (hasTangent && baseTangents != null)
                        *(Vector3*)(vertexBase + tanOffset) = baseTangents[v];
                }
            }
        }

        /// <summary>
        /// Checks if weights have changed since the previous frame.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool AreWeightsDirty(float[] current, float[] previous)
        {
            if (current == null || previous == null || current.Length != previous.Length)
                return true;

            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] != previous[i])
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Copies current weights into the previous weights array for next-frame dirty detection.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyWeights(float[] source, float[] destination)
        {
            if (source == null || destination == null)
                return;
            Array.Copy(source, destination, Math.Min(source.Length, destination.Length));
        }

        /// <summary>
        /// Zero-alloc batch job for multi-threaded vertex deformation.
        /// Implements <see cref="Dispatcher.IBatchJob"/> for use with <see cref="Dispatcher.ForBatched"/>.
        /// </summary>
        private struct DeformJob : Dispatcher.IBatchJob
        {
            public Vector3[] BasePositions;
            public Vector3[] BaseNormals;
            public Vector3[] BaseTangents;
            public bool HasTangent;
            public BlendShapeTarget[] ActiveTargets;
            public float[] ActiveWeights;
            public int ActiveTargetCount;
            public byte[] OutputBytes;
            public int Stride;
            public int PosOffset;
            public int NrmOffset;
            public int TanOffset;
            public int VertexCount;

            public unsafe void Process(int start, int endExclusive)
            {
                fixed (byte* outPtr = OutputBytes)
                {
                    for (int v = start; v < endExclusive; v++)
                    {
                        // Start with base mesh data (use System.Numerics.Vector3 for SIMD)
                        var pos = ToSys(BasePositions[v]);
                        var nrm = ToSys(BaseNormals[v]);
                        SysVector3 tan = default;
                        if (HasTangent && BaseTangents != null)
                            tan = ToSys(BaseTangents[v]);

                        // Accumulate weighted deltas from all active targets
                        for (int t = 0; t < ActiveTargetCount; t++)
                        {
                            var target = ActiveTargets[t];
                            var weight = ActiveWeights[t];

                            if (target.HasDeltaPositions && target.DeltaPositions != null)
                                pos += ToSys(target.DeltaPositions[v]) * weight;

                            if (target.HasDeltaNormals && target.DeltaNormals != null)
                                nrm += ToSys(target.DeltaNormals[v]) * weight;

                            if (HasTangent && target.HasDeltaTangents && target.DeltaTangents != null)
                                tan += ToSys(target.DeltaTangents[v]) * weight;
                        }

                        // Normalize normals and tangents after accumulation
                        nrm = SysVector3.Normalize(nrm);
                        if (HasTangent)
                            tan = SysVector3.Normalize(tan);

                        // Write to output buffer at the correct interleaved offsets
                        var vertexBase = outPtr + v * Stride;
                        *(Vector3*)(vertexBase + PosOffset) = FromSys(pos);
                        *(Vector3*)(vertexBase + NrmOffset) = FromSys(nrm);
                        if (HasTangent)
                            *(Vector3*)(vertexBase + TanOffset) = FromSys(tan);
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static SysVector3 ToSys(Vector3 v) => Unsafe.As<Vector3, SysVector3>(ref v);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector3 FromSys(SysVector3 v) => Unsafe.As<SysVector3, Vector3>(ref v);
        }
    }
}
