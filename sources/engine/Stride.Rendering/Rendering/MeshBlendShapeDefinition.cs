// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;

namespace Stride.Rendering
{
    /// <summary>
    /// Describes a single blend shape (morph target) for a <see cref="Mesh"/>.
    /// Each target stores per-vertex deltas that are applied at a given weight.
    /// </summary>
    [DataContract]
    public class BlendShapeTarget
    {
        /// <summary>
        /// The name of this blend shape target (e.g. "Smile", "BrowRaise").
        /// </summary>
        public string Name;

        /// <summary>
        /// Whether this target contains position deltas.
        /// </summary>
        public bool HasDeltaPositions;

        /// <summary>
        /// Whether this target contains normal deltas.
        /// </summary>
        public bool HasDeltaNormals;

        /// <summary>
        /// Whether this target contains tangent deltas.
        /// </summary>
        public bool HasDeltaTangents;

        /// <summary>
        /// Per-vertex position deltas for this target. Used by the compute shader deformation path.
        /// Not serialized — populated at runtime when the compute path is active.
        /// </summary>
        [DataMemberIgnore]
        public Vector3[] DeltaPositions;

        /// <summary>
        /// Per-vertex normal deltas for this target. Used by the compute shader deformation path.
        /// </summary>
        [DataMemberIgnore]
        public Vector3[] DeltaNormals;

        /// <summary>
        /// Per-vertex tangent deltas for this target. Used by the compute shader deformation path.
        /// </summary>
        [DataMemberIgnore]
        public Vector3[] DeltaTangents;
    }

    /// <summary>
    /// Describes blend shapes (morph targets) for a <see cref="Mesh"/>,
    /// through a collection of <see cref="BlendShapeTarget"/>.
    /// Delta vertex data is packed into additional vertex buffer bindings on the <see cref="MeshDraw"/>.
    /// </summary>
    [DataContract]
    public class MeshBlendShapeDefinition
    {
        /// <summary>
        /// The blend shape targets associated with this mesh.
        /// </summary>
        public BlendShapeTarget[] Targets;

        /// <summary>
        /// Total vertex count of the mesh. Used by the compute shader deformation path.
        /// </summary>
        [DataMemberIgnore]
        public int VertexCount;

        /// <summary>
        /// Vertex stride (bytes) of the output vertex buffer. Used by the compute shader deformation path.
        /// </summary>
        [DataMemberIgnore]
        public int VertexStride;

        /// <summary>
        /// Byte offset of Position within a vertex. Used by the compute shader deformation path.
        /// </summary>
        [DataMemberIgnore]
        public int PositionOffset;

        /// <summary>
        /// Byte offset of Normal within a vertex. Used by the compute shader deformation path.
        /// </summary>
        [DataMemberIgnore]
        public int NormalOffset;

        /// <summary>
        /// Byte offset of Tangent within a vertex (-1 if no tangent). Used by the compute shader deformation path.
        /// </summary>
        [DataMemberIgnore]
        public int TangentOffset = -1;

        /// <summary>
        /// Base mesh positions (undeformed). Used by the compute shader deformation path.
        /// </summary>
        [DataMemberIgnore]
        public Vector3[] BasePositions;

        /// <summary>
        /// Base mesh normals (undeformed). Used by the compute shader deformation path.
        /// </summary>
        [DataMemberIgnore]
        public Vector3[] BaseNormals;

        /// <summary>
        /// Base mesh tangents (undeformed). Used by the compute shader deformation path.
        /// </summary>
        [DataMemberIgnore]
        public Vector3[] BaseTangents;
    }
}
