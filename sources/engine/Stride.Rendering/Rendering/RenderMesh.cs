// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;
using Stride.Rendering.Materials;

namespace Stride.Rendering
{
    /// <summary>
    /// Used by <see cref="MeshRenderFeature"/> to render a <see cref="Rendering.Mesh"/>.
    /// </summary>
    public class RenderMesh : RenderObject
    {
        public MeshDraw ActiveMeshDraw;

        /// <summary>
        /// Per-entity override for <see cref="ActiveMeshDraw"/>.
        /// When set (e.g. for CPU blend shape deformation), <see cref="MeshRenderFeature"/> will use
        /// this instead of <see cref="Mesh"/>.<see cref="Rendering.Mesh.Draw"/>.
        /// </summary>
        public MeshDraw OverrideMeshDraw;

        public RenderModel RenderModel;

        /// <summary>
        /// Underlying mesh, can be accessed only during <see cref="RenderFeature.Extract"/> phase.
        /// </summary>
        public Mesh Mesh;

        // Material
        // TODO: Extract with MaterialRenderFeature
        public MaterialPass MaterialPass;

        // TODO GRAPHICS REFACTOR store that in RenderData (StaticObjectNode?)
        internal MaterialRenderFeature.MaterialInfo MaterialInfo;

        public bool IsShadowCaster;

        public bool IsScalingNegative;

        public bool IsPreviousScalingNegative;

        public Matrix World = Matrix.Identity;

        public Matrix[] BlendMatrices;

        /// <summary>
        /// When true, vertex shader skinning is suppressed for this mesh.
        /// Set by the blend shape processor when fused compute skinning is active —
        /// the compute shader has already applied skeletal skinning, so the VS
        /// should use the standard non-skinned transform path (World * Position).
        /// </summary>
        public bool SkipVsSkinning;

        public int InstanceCount;
    }
}
