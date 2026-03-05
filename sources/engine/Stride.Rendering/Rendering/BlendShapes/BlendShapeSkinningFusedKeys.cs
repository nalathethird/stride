// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Rendering.BlendShapes
{
    /// <summary>
    /// Parameter keys for the BlendShapeSkinningFused compute shader.
    /// Maps C# buffer/value parameters to shader resource declarations in BlendShapeSkinningFused.sdsl.
    /// Extends the blend-shape-only keys with skeletal skinning inputs.
    /// </summary>
    public static class BlendShapeSkinningFusedKeys
    {
        // --- Base mesh data (uploaded once at init) ---
        public static readonly ObjectParameterKey<Buffer> BasePositions = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> BaseNormals = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> BaseTangents = ParameterKeys.NewObject<Buffer>();

        // --- Dense per-target deltas (uploaded once at init) ---
        public static readonly ObjectParameterKey<Buffer> DeltaPositions = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> DeltaNormals = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> DeltaTangents = ParameterKeys.NewObject<Buffer>();

        // --- Active target compaction (updated per dirty frame) ---
        public static readonly ObjectParameterKey<Buffer> ActiveTargetIndices = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> ActiveWeights = ParameterKeys.NewObject<Buffer>();

        // --- Skeletal skinning inputs ---
        /// <summary>
        /// Object-space bone matrices: InverseBindPose * BoneModelSpace.
        /// CPU pre-multiplies BlendMatrices[i] * MeshWorldInverse before upload.
        /// Updated every frame (bone animation changes bone transforms each frame).
        /// </summary>
        public static readonly ObjectParameterKey<Buffer> BoneMatrices = ParameterKeys.NewObject<Buffer>();

        /// <summary>
        /// Per-vertex bone weights (float4: up to 4 bone influences).
        /// Extracted from the original vertex buffer at init time.
        /// </summary>
        public static readonly ObjectParameterKey<Buffer> VertexBoneWeights = ParameterKeys.NewObject<Buffer>();

        /// <summary>
        /// Per-vertex bone indices (uint4: indices into BoneMatrices).
        /// Extracted from the original vertex buffer at init time.
        /// Note: original VB stores as ushort4 — converted to uint4 at upload.
        /// </summary>
        public static readonly ObjectParameterKey<Buffer> VertexBoneIndices = ParameterKeys.NewObject<Buffer>();

        // --- Output vertex buffer (UAV, written by compute, read by draw) ---
        public static readonly ObjectParameterKey<Buffer> OutputVertexBuffer = ParameterKeys.NewObject<Buffer>();

        // --- Uniforms (cbuffer BlendShapeSkinningParams) ---
        public static readonly ValueParameterKey<int> VertexCount = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> ActiveTargetCount = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> VertexStride = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> PositionOffset = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> NormalOffset = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> TangentOffset = ParameterKeys.NewValue<int>();
    }
}
