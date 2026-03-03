// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;

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
    }
}
