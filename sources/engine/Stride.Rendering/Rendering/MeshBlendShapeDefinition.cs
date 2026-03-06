// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Mathematics;

namespace Stride.Rendering
{
    /// <summary>
    /// Describes a single blend shape (morph target) for a <see cref="Mesh"/>.
    /// Each target stores per-vertex deltas that are applied at a given weight.
    /// </summary>
    [DataContract]
    public class BlendShapeTarget
    {
        public string Name;

        public bool HasDeltaPositions;
        public bool HasDeltaNormals;
        public bool HasDeltaTangents;

        /// <summary>Per-vertex position deltas. Serialized with the asset for the compute deformation path.</summary>
        public Vector3[] DeltaPositions;

        /// <summary>Per-vertex normal deltas.</summary>
        public Vector3[] DeltaNormals;

        /// <summary>Per-vertex tangent deltas.</summary>
        public Vector3[] DeltaTangents;

        /// <summary>Number of vertices with non-zero deltas (populated by <see cref="MeshBlendShapeDefinition.BuildSparseMetadata"/>).</summary>
        [DataMemberIgnore]
        public int AffectedVertexCount;

        /// <summary>Indices of vertices with non-zero deltas.</summary>
        [DataMemberIgnore]
        public int[] AffectedVertexIndices;

        /// <summary>Ratio of affected vertices to total vertices (0..1).</summary>
        [DataMemberIgnore]
        public float Sparsity;
    }

    /// <summary>
    /// Pre-cooked sparse CSR (Compressed Sparse Row) representation of blend shape deltas.
    /// Built at import time by <see cref="MeshBlendShapeDefinition.Cook()"/> and serialized with the asset.
    /// The GPU sparse deformation shader reads this layout directly.
    /// </summary>
    [DataContract]
    public class BlendShapeCookedData
    {
        /// <summary>Per-vertex offset into the contribution arrays. Length = VertexCount.</summary>
        public uint[] VertexContribOffset;

        /// <summary>Per-vertex count of contributing targets. Length = VertexCount.</summary>
        public uint[] VertexContribCount;

        /// <summary>Flat array of target indices for each contribution.</summary>
        public uint[] ContribShapeIndices;

        /// <summary>Flat array of position deltas for each contribution.</summary>
        public Vector3[] ContribPosDeltas;

        /// <summary>Flat array of normal deltas for each contribution.</summary>
        public Vector3[] ContribNrmDeltas;

        /// <summary>Flat array of tangent deltas for each contribution.</summary>
        public Vector3[] ContribTanDeltas;

        /// <summary>Minimum position delta bounds (for future int16 quantization).</summary>
        public Vector3 PosBoundsMin;

        /// <summary>Maximum position delta bounds (for future int16 quantization).</summary>
        public Vector3 PosBoundsMax;

        /// <summary>Whether any target has tangent contributions.</summary>
        public bool HasTangentContribs;

        /// <summary>Total number of (vertex, target) contributions across all vertices.</summary>
        public int TotalContribCount;
    }

    /// <summary>
    /// Describes blend shapes (morph targets) for a <see cref="Mesh"/>,
    /// through a collection of <see cref="BlendShapeTarget"/>.
    /// Delta vertex data is packed into additional vertex buffer bindings on the <see cref="MeshDraw"/>.
    /// </summary>
    [DataContract]
    public class MeshBlendShapeDefinition
    {
        public BlendShapeTarget[] Targets;

        /// <summary>Total vertex count of the mesh.</summary>
        public int VertexCount;

        /// <summary>Base mesh positions (undeformed).</summary>
        public Vector3[] BasePositions;

        /// <summary>Base mesh normals (undeformed).</summary>
        public Vector3[] BaseNormals;

        /// <summary>Base mesh tangents (undeformed, null if mesh has no tangents).</summary>
        public Vector3[] BaseTangents;

        /// <summary>Vertex stride (bytes) of the output vertex buffer.</summary>
        public int VertexStride;

        /// <summary>Byte offset of Position within a vertex.</summary>
        public int PositionOffset;

        /// <summary>Byte offset of Normal within a vertex.</summary>
        public int NormalOffset;

        /// <summary>Byte offset of Tangent within a vertex (-1 if no tangent).</summary>
        public int TangentOffset = -1;

        /// <summary>
        /// Pre-cooked sparse CSR data built by <see cref="Cook()"/>. Serialized with the asset.
        /// Null until Cook() is called during import.
        /// </summary>
        public BlendShapeCookedData CookedData;

        /// <summary>
        /// Dead-zone threshold. Deltas with all components below this magnitude are culled during cooking.
        /// </summary>
        private const float DeadZoneEpsilon = 1e-7f;

        /// <summary>
        /// Converts dense per-target delta arrays into a vertex-major Compressed Sparse Row layout.
        /// This dramatically reduces GPU memory for typical facial rigs (15-30x) and enables
        /// dead-zone culling of imperceptible deltas.
        /// Must be called after all coordinate transforms are finalized (post-import).
        /// </summary>
        public void Cook()
        {
            if (Targets == null || Targets.Length == 0 || VertexCount <= 0)
                return;

            BuildSparseMetadata();

            var targetCount = Targets.Length;

            // Pass 1: count total contributions across all vertices
            int totalContribs = 0;
            var perVertexCount = new uint[VertexCount];
            var perVertexOffset = new uint[VertexCount];

            for (int v = 0; v < VertexCount; v++)
            {
                int count = 0;
                for (int t = 0; t < targetCount; t++)
                {
                    var target = Targets[t];
                    if (target == null)
                        continue;

                    bool hasContrib = false;

                    if (target.HasDeltaPositions && target.DeltaPositions != null && v < target.DeltaPositions.Length)
                    {
                        var d = target.DeltaPositions[v];
                        if (Math.Abs(d.X) > DeadZoneEpsilon || Math.Abs(d.Y) > DeadZoneEpsilon || Math.Abs(d.Z) > DeadZoneEpsilon)
                            hasContrib = true;
                    }

                    if (!hasContrib && target.HasDeltaNormals && target.DeltaNormals != null && v < target.DeltaNormals.Length)
                    {
                        var d = target.DeltaNormals[v];
                        if (Math.Abs(d.X) > DeadZoneEpsilon || Math.Abs(d.Y) > DeadZoneEpsilon || Math.Abs(d.Z) > DeadZoneEpsilon)
                            hasContrib = true;
                    }

                    if (!hasContrib && target.HasDeltaTangents && target.DeltaTangents != null && v < target.DeltaTangents.Length)
                    {
                        var d = target.DeltaTangents[v];
                        if (Math.Abs(d.X) > DeadZoneEpsilon || Math.Abs(d.Y) > DeadZoneEpsilon || Math.Abs(d.Z) > DeadZoneEpsilon)
                            hasContrib = true;
                    }

                    if (hasContrib)
                        count++;
                }

                perVertexCount[v] = (uint)count;
                totalContribs += count;
            }

            // Build prefix-sum offsets
            uint runningOffset = 0;
            for (int v = 0; v < VertexCount; v++)
            {
                perVertexOffset[v] = runningOffset;
                runningOffset += perVertexCount[v];
            }

            // Allocate contribution arrays (minimum 1 element to avoid zero-length GPU buffers)
            int allocSize = Math.Max(1, totalContribs);
            var contribShapeIndices = new uint[allocSize];
            var contribPosDeltas = new Vector3[allocSize];
            var contribNrmDeltas = new Vector3[allocSize];
            var contribTanDeltas = new Vector3[allocSize];

            bool hasTangentContribs = false;
            var boundsMin = new Vector3(float.MaxValue);
            var boundsMax = new Vector3(float.MinValue);

            // Pass 2: fill contribution arrays
            var writePos = new uint[VertexCount];
            Array.Copy(perVertexOffset, writePos, VertexCount);

            for (int v = 0; v < VertexCount; v++)
            {
                for (int t = 0; t < targetCount; t++)
                {
                    var target = Targets[t];
                    if (target == null)
                        continue;

                    var posD = Vector3.Zero;
                    var nrmD = Vector3.Zero;
                    var tanD = Vector3.Zero;
                    bool hasContrib = false;

                    if (target.HasDeltaPositions && target.DeltaPositions != null && v < target.DeltaPositions.Length)
                    {
                        posD = target.DeltaPositions[v];
                        if (Math.Abs(posD.X) > DeadZoneEpsilon || Math.Abs(posD.Y) > DeadZoneEpsilon || Math.Abs(posD.Z) > DeadZoneEpsilon)
                            hasContrib = true;
                    }

                    if (target.HasDeltaNormals && target.DeltaNormals != null && v < target.DeltaNormals.Length)
                    {
                        nrmD = target.DeltaNormals[v];
                        if (!hasContrib && (Math.Abs(nrmD.X) > DeadZoneEpsilon || Math.Abs(nrmD.Y) > DeadZoneEpsilon || Math.Abs(nrmD.Z) > DeadZoneEpsilon))
                            hasContrib = true;
                    }

                    if (target.HasDeltaTangents && target.DeltaTangents != null && v < target.DeltaTangents.Length)
                    {
                        tanD = target.DeltaTangents[v];
                        if (!hasContrib && (Math.Abs(tanD.X) > DeadZoneEpsilon || Math.Abs(tanD.Y) > DeadZoneEpsilon || Math.Abs(tanD.Z) > DeadZoneEpsilon))
                            hasContrib = true;
                        if (hasContrib && (Math.Abs(tanD.X) > DeadZoneEpsilon || Math.Abs(tanD.Y) > DeadZoneEpsilon || Math.Abs(tanD.Z) > DeadZoneEpsilon))
                            hasTangentContribs = true;
                    }

                    if (!hasContrib)
                        continue;

                    var idx = writePos[v]++;
                    contribShapeIndices[idx] = (uint)t;
                    contribPosDeltas[idx] = posD;
                    contribNrmDeltas[idx] = nrmD;
                    contribTanDeltas[idx] = tanD;

                    // Track bounds for future quantization
                    boundsMin = Vector3.Min(boundsMin, posD);
                    boundsMax = Vector3.Max(boundsMax, posD);
                }
            }

            if (totalContribs == 0)
            {
                boundsMin = Vector3.Zero;
                boundsMax = Vector3.Zero;
            }

            CookedData = new BlendShapeCookedData
            {
                VertexContribOffset = perVertexOffset,
                VertexContribCount = perVertexCount,
                ContribShapeIndices = contribShapeIndices,
                ContribPosDeltas = contribPosDeltas,
                ContribNrmDeltas = contribNrmDeltas,
                ContribTanDeltas = contribTanDeltas,
                PosBoundsMin = boundsMin,
                PosBoundsMax = boundsMax,
                HasTangentContribs = hasTangentContribs,
                TotalContribCount = totalContribs,
            };
        }

        /// <summary>
        /// Populates sparse metadata on each target: AffectedVertexCount, AffectedVertexIndices, Sparsity.
        /// </summary>
        public void BuildSparseMetadata()
        {
            if (Targets == null || VertexCount <= 0)
                return;

            for (int t = 0; t < Targets.Length; t++)
            {
                var target = Targets[t];
                if (target == null)
                    continue;

                int affectedCount = 0;
                var indices = new int[VertexCount]; // worst case

                int count = 0;
                if (target.HasDeltaPositions && target.DeltaPositions != null)
                    count = Math.Min(VertexCount, target.DeltaPositions.Length);
                else if (target.HasDeltaNormals && target.DeltaNormals != null)
                    count = Math.Min(VertexCount, target.DeltaNormals.Length);
                else if (target.HasDeltaTangents && target.DeltaTangents != null)
                    count = Math.Min(VertexCount, target.DeltaTangents.Length);

                for (int v = 0; v < count; v++)
                {
                    bool nonZero = false;

                    if (target.HasDeltaPositions && target.DeltaPositions != null && v < target.DeltaPositions.Length)
                    {
                        var d = target.DeltaPositions[v];
                        if (Math.Abs(d.X) > DeadZoneEpsilon || Math.Abs(d.Y) > DeadZoneEpsilon || Math.Abs(d.Z) > DeadZoneEpsilon)
                            nonZero = true;
                    }

                    if (!nonZero && target.HasDeltaNormals && target.DeltaNormals != null && v < target.DeltaNormals.Length)
                    {
                        var d = target.DeltaNormals[v];
                        if (Math.Abs(d.X) > DeadZoneEpsilon || Math.Abs(d.Y) > DeadZoneEpsilon || Math.Abs(d.Z) > DeadZoneEpsilon)
                            nonZero = true;
                    }

                    if (!nonZero && target.HasDeltaTangents && target.DeltaTangents != null && v < target.DeltaTangents.Length)
                    {
                        var d = target.DeltaTangents[v];
                        if (Math.Abs(d.X) > DeadZoneEpsilon || Math.Abs(d.Y) > DeadZoneEpsilon || Math.Abs(d.Z) > DeadZoneEpsilon)
                            nonZero = true;
                    }

                    if (nonZero)
                        indices[affectedCount++] = v;
                }

                target.AffectedVertexCount = affectedCount;
                target.AffectedVertexIndices = new int[affectedCount];
                Array.Copy(indices, target.AffectedVertexIndices, affectedCount);
                target.Sparsity = VertexCount > 0 ? (float)affectedCount / VertexCount : 0f;
            }
        }

        /// <summary>
        /// Applies a coordinate space transformation matrix to all base positions/normals/tangents
        /// and per-target deltas. Used during import when coordinate systems change.
        /// </summary>
        public void ApplyTransform(ref Matrix transformationMatrix)
        {
            // Extract rotation/scale for normal/tangent transforms (no translation)
            var normalMatrix = transformationMatrix;
            normalMatrix.TranslationVector = Vector3.Zero;

            if (BasePositions != null)
            {
                for (int i = 0; i < BasePositions.Length; i++)
                    Vector3.TransformCoordinate(ref BasePositions[i], ref transformationMatrix, out BasePositions[i]);
            }

            if (BaseNormals != null)
            {
                for (int i = 0; i < BaseNormals.Length; i++)
                    Vector3.TransformNormal(ref BaseNormals[i], ref normalMatrix, out BaseNormals[i]);
            }

            if (BaseTangents != null)
            {
                for (int i = 0; i < BaseTangents.Length; i++)
                    Vector3.TransformNormal(ref BaseTangents[i], ref normalMatrix, out BaseTangents[i]);
            }

            if (Targets == null)
                return;

            foreach (var target in Targets)
            {
                if (target == null)
                    continue;

                if (target.DeltaPositions != null)
                {
                    for (int i = 0; i < target.DeltaPositions.Length; i++)
                        Vector3.TransformNormal(ref target.DeltaPositions[i], ref transformationMatrix, out target.DeltaPositions[i]);
                }

                if (target.DeltaNormals != null)
                {
                    for (int i = 0; i < target.DeltaNormals.Length; i++)
                        Vector3.TransformNormal(ref target.DeltaNormals[i], ref normalMatrix, out target.DeltaNormals[i]);
                }

                if (target.DeltaTangents != null)
                {
                    for (int i = 0; i < target.DeltaTangents.Length; i++)
                        Vector3.TransformNormal(ref target.DeltaTangents[i], ref normalMatrix, out target.DeltaTangents[i]);
                }
            }
        }
    }
}
