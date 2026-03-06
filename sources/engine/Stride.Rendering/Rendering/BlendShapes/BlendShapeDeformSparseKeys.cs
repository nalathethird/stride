// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Rendering.BlendShapes
{
    /// <summary>
    /// Parameter keys for the BlendShapeDeformSparse compute shader.
    /// Maps C# buffer/value parameters to shader resource declarations in BlendShapeDeformSparse.sdsl.
    /// Uses vertex-major Compressed Sparse Row (CSR) layout for 15-30x memory reduction.
    /// </summary>
    public static class BlendShapeDeformSparseKeys
    {
        // --- Base mesh data (uploaded once at init) ---
        public static readonly ObjectParameterKey<Buffer> BasePositions = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> BaseNormals = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> BaseTangents = ParameterKeys.NewObject<Buffer>();

        // --- Sparse CSR contribution data (uploaded once at init from CookedData) ---
        public static readonly ObjectParameterKey<Buffer> VertexContribOffset = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> VertexContribCount = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> ContribShapeIndices = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> ContribPosDeltas = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> ContribNrmDeltas = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> ContribTanDeltas = ParameterKeys.NewObject<Buffer>();

        // --- All target weights (indexed by shape index from CSR data) ---
        public static readonly ObjectParameterKey<Buffer> AllWeights = ParameterKeys.NewObject<Buffer>();

        // --- Output vertex buffer (UAV, written by compute, read by draw) ---
        public static readonly ObjectParameterKey<Buffer> OutputVertexBuffer = ParameterKeys.NewObject<Buffer>();

        // --- Uniforms ---
        public static readonly ValueParameterKey<int> VertexCount = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> VertexStride = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> PositionOffset = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> NormalOffset = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> TangentOffset = ParameterKeys.NewValue<int>();
    }
}
