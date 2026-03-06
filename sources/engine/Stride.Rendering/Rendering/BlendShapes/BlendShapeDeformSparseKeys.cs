// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Rendering.BlendShapes
{
    /// <summary>
    /// Parameter keys for the BlendShapeDeformSparse compute shader.
    /// Maps C# buffer/value parameters to shader resource declarations in BlendShapeDeformSparse.sdsl.
    /// </summary>
    public static class BlendShapeDeformSparseKeys
    {
        // --- Base mesh data (immutable, uploaded once at init) ---
        public static readonly ObjectParameterKey<Buffer> BasePositions     = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> BaseNormals       = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> BaseTangents      = ParameterKeys.NewObject<Buffer>();

        // --- CSR vertex table (immutable) ---
        public static readonly ObjectParameterKey<Buffer> VertexContribOffset = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> VertexContribCount  = ParameterKeys.NewObject<Buffer>();

        // --- Flat contribution arrays (immutable) ---
        public static readonly ObjectParameterKey<Buffer> ContribShapeIndices = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> ContribPosDeltas    = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> ContribNrmDeltas    = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> ContribTanDeltas    = ParameterKeys.NewObject<Buffer>();

        // --- Runtime weight map (updated each dirty frame) ---
        public static readonly ObjectParameterKey<Buffer> AllWeights          = ParameterKeys.NewObject<Buffer>();

        // --- Output UAV vertex buffer ---
        public static readonly ObjectParameterKey<Buffer> OutputVertexBuffer  = ParameterKeys.NewObject<Buffer>();

        // --- Uniforms (cbuffer BlendShapeSparseParams) ---
        public static readonly ValueParameterKey<int> VertexCount     = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> VertexStride    = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> PositionOffset  = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> NormalOffset    = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<int> TangentOffset   = ParameterKeys.NewValue<int>();
    }
}
