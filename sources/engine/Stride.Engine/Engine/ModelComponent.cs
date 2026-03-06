// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Collections;
using Stride.Core.Mathematics;
using Stride.Engine.Design;
using Stride.Engine.Processors;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Updater;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Engine
{
    /// <summary>
    /// Add a <see cref="Model"/> to an <see cref="Entity"/>, that will be used during rendering.
    /// </summary>
    [DataContract("ModelComponent")]
    [Display("Model", Expand = ExpandRule.Once)]
    // TODO GRAPHICS REFACTOR
    [DefaultEntityComponentProcessor(typeof(ModelTransformProcessor))]
    [DefaultEntityComponentProcessor(typeof(BlendShapeAutoProvisionProcessor), ExecutionMode = ExecutionMode.All)]
    [DefaultEntityComponentRenderer(typeof(ModelRenderProcessor))]
    [ComponentOrder(11000)]
    [ComponentCategory("Model")]
    public sealed class ModelComponent : ActivableEntityComponent, IModelInstance
    {
        private readonly List<MeshInfo> meshInfos = new List<MeshInfo>();
        private Model model;
        private SkeletonUpdater skeleton;
        private bool modelViewHierarchyDirty = true;

        /// <summary>
        /// Per-entity state of each individual mesh of a model.
        /// </summary>
        public class MeshInfo
        {
            /// <summary>
            /// The current blend matrices of a skinned meshes, transforming from mesh space to world space, for each bone.
            /// </summary>
            public Matrix[] BlendMatrices;

            /// <summary>
            /// The current blend shape (morph target) weights for each target.
            /// </summary>
            public float[] BlendShapeWeights;

            /// <summary>
            /// The meshes current bounding box in world space.
            /// </summary>
            public BoundingBox BoundingBox;

            /// <summary>
            /// The meshes current sphere box in world space.
            /// </summary>
            public BoundingSphere BoundingSphere;

            // --- CPU blend shape deformation fields ---

            /// <summary>
            /// Per-entity cloned MeshDraw whose VB[0] points to a dynamic vertex buffer.
            /// Null if this mesh has no blend shapes.
            /// </summary>
            [DataMemberIgnore]
            public MeshDraw ClonedMeshDraw;

            /// <summary>
            /// CPU-side staging buffer containing the deformed vertex data (same layout as VB[0]).
            /// </summary>
            [DataMemberIgnore]
            public byte[] DeformedVertexData;

            /// <summary>
            /// Previous frame's blend shape weights for dirty detection.
            /// </summary>
            [DataMemberIgnore]
            public float[] PreviousWeights;

            /// <summary>
            /// Whether blend shape weights changed since the last deformation.
            /// </summary>
            [DataMemberIgnore]
            public bool BlendShapeDirty;

            /// <summary>
            /// Whether the initial base vertex data has been copied into the deformed buffer.
            /// </summary>
            [DataMemberIgnore]
            public bool BlendShapeInitialized;

            // --- GPU blend shape deformation fields ---

            /// <summary>
            /// GPU StructuredBuffer containing base positions (bind pose). Created once at init.
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuBasePositions;

            /// <summary>
            /// GPU StructuredBuffer containing base normals (bind pose). Created once at init.
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuBaseNormals;

            /// <summary>
            /// GPU StructuredBuffer containing base tangents (bind pose). May be null.
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuBaseTangents;

            /// <summary>
            /// GPU StructuredBuffer containing packed per-target position deltas.
            /// Layout: [target0_vert0..target0_vertN, target1_vert0..target1_vertN, ...]
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuDeltaPositions;

            /// <summary>
            /// GPU StructuredBuffer containing packed per-target normal deltas.
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuDeltaNormals;

            /// <summary>
            /// GPU StructuredBuffer containing packed per-target tangent deltas. May be null.
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuDeltaTangents;

            /// <summary>
            /// GPU StructuredBuffer for active target indices (compacted each dirty frame).
            /// Maps [0..activeCount) to original target indices in the delta arrays.
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuActiveIndicesBuffer;

            /// <summary>
            /// GPU StructuredBuffer for active target weights (compacted each dirty frame).
            /// Contains only weights with |w| > epsilon.
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuActiveWeightsBuffer;

            /// <summary>
            /// Whether GPU blend shape buffers have been initialized for this mesh.
            /// </summary>
            [DataMemberIgnore]
            public bool GpuBlendShapeInitialized;

            // --- Sparse blendshape GPU buffers (built from MeshBlendShapeDefinition.CookedData) ---

            /// <summary>Per-vertex start offsets into the flat contribution lists (StructuredBuffer&lt;uint&gt;).</summary>
            [DataMemberIgnore]
            public Buffer GpuSparseVertexOffset;

            /// <summary>Per-vertex contribution counts (StructuredBuffer&lt;uint&gt;).</summary>
            [DataMemberIgnore]
            public Buffer GpuSparseVertexCount;

            /// <summary>Target index for each flat contribution entry (StructuredBuffer&lt;uint&gt;).</summary>
            [DataMemberIgnore]
            public Buffer GpuSparseShapeIndices;

            /// <summary>Position delta for each flat contribution entry (StructuredBuffer&lt;float3&gt;).</summary>
            [DataMemberIgnore]
            public Buffer GpuSparsePosDeltas;

            /// <summary>Normal delta for each flat contribution entry (StructuredBuffer&lt;float3&gt;).</summary>
            [DataMemberIgnore]
            public Buffer GpuSparseNrmDeltas;

            /// <summary>
            /// Tangent delta for each flat contribution entry (StructuredBuffer&lt;float3&gt;).
            /// Null when the mesh has no tangent blendshape targets.
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuSparseTanDeltas;

            /// <summary>
            /// Full per-target weight array uploaded each dirty frame (StructuredBuffer&lt;float&gt;).
            /// Indexed by original target index so the sparse shader can look up any shape's weight.
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuAllWeightsBuffer;

            /// <summary>Whether the sparse GPU buffers above have been initialised.</summary>
            [DataMemberIgnore]
            public bool GpuSparseInitialized;

            // --- Fused blend shape + skinning fields (Phase 2) ---

            /// <summary>
            /// GPU StructuredBuffer containing object-space bone matrices.
            /// Updated every frame: ObjectSpaceBone[i] = BlendMatrices[i] * MeshWorldInverse.
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuBoneMatricesBuffer;

            /// <summary>
            /// GPU StructuredBuffer containing per-vertex bone weights (float4).
            /// Extracted from the original vertex buffer at init time.
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuVertexBoneWeights;

            /// <summary>
            /// GPU StructuredBuffer containing per-vertex bone indices (uint4).
            /// Extracted from the original vertex buffer at init time.
            /// Note: original VB stores as ushort4 — converted to uint4 at upload.
            /// </summary>
            [DataMemberIgnore]
            public Buffer GpuVertexBoneIndices;

            /// <summary>
            /// CPU-side array for pre-multiplied object-space bone matrices.
            /// Avoids per-frame heap allocation.
            /// </summary>
            [DataMemberIgnore]
            public Matrix[] ObjectSpaceBoneMatrices;

            /// <summary>
            /// Whether fused compute skinning (blend shapes + skeletal skinning in one dispatch)
            /// is active for this mesh. When true, VS skinning is suppressed.
            /// </summary>
            [DataMemberIgnore]
            public bool UseFusedSkinning;

            /// <summary>
            /// When true, <see cref="BlendMatrices"/> are not recomputed from the skeleton each frame.
            /// Set by external skinning systems (e.g. Strideonite) that supply pre-computed
            /// InvBindPose × BoneWorldMatrix values directly.
            /// </summary>
            [DataMemberIgnore]
            public bool UseExternalBlendMatrices;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ModelComponent"/> class.
        /// </summary>
        public ModelComponent() : this(null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ModelComponent"/> class.
        /// </summary>
        /// <param name="model">The model.</param>
        public ModelComponent(Model model)
        {
            Model = model;
            IsShadowCaster = true;
        }

        /// <summary>
        /// Gets or sets the model.
        /// </summary>
        /// <value>
        /// The model.
        /// </value>
        /// <userdoc>The reference to the model asset to attach to this entity</userdoc>
        [DataMemberCustomSerializer]
        [DataMember(10)]
        public Model Model
        {
            get
            {
                return model;
            }
            set
            {
                if (model != value)
                    modelViewHierarchyDirty = true;
                model = value;
            }
        }

        /// <summary>
        /// Gets the materials; non-null ones will override materials from <see cref="Stride.Rendering.Model.Materials"/> (same slots should be used).
        /// </summary>
        /// <value>
        /// The materials overriding <see cref="Stride.Rendering.Model.Materials"/> ones.
        /// </value>
        /// <userdoc>The list of materials to use with the model. This list overrides the default materials of the model.</userdoc>
        [DataMember(40)]
        [Category]
        [MemberCollection(ReadOnly = true)]
        public IndexingDictionary<Material> Materials { get; } = new IndexingDictionary<Material>();

        [DataMemberIgnore, DataMemberUpdatable]
        [DataMember]
        public SkeletonUpdater Skeleton
        {
            get
            {
                CheckSkeleton();
                return skeleton;
            }
        }

        /// <summary>
        /// Gets the current per-entity state for each mesh in the associated model.
        /// </summary>
        [DataMemberIgnore]
        public IReadOnlyList<MeshInfo> MeshInfos => meshInfos;

        private void CheckSkeleton()
        {
            if (modelViewHierarchyDirty || meshInfos.Count != model.Meshes.Count)
            {
                ModelUpdated();
                modelViewHierarchyDirty = false;
            }
        }

        /// <summary>
        /// Gets or sets a boolean indicating if this model component is casting shadows.
        /// </summary>
        /// <value>A boolean indicating if this model component is casting shadows.</value>
        /// <userdoc>Generate a shadow (when shadow maps are enabled)</userdoc>
        [DataMember(30)]
        [DefaultValue(true)]
        [Display("Cast shadows")]
        public bool IsShadowCaster { get; set; }

        /// <summary>
        /// The render group for this component.
        /// </summary>
        [DataMember(20)]
        [DefaultValue(RenderGroup.Group0)]
        [Display("Render group")]
        public RenderGroup RenderGroup { get; set; }

        /// <summary>
        /// Gets the bounding box in world space.
        /// </summary>
        /// <value>The bounding box.</value>
        [DataMemberIgnore]
        public BoundingBox BoundingBox;

        /// <summary>
        /// Gets the bounding sphere in world space.
        /// </summary>
        /// <value>The bounding sphere.</value>
        [DataMemberIgnore]
        public BoundingSphere BoundingSphere;

        /// <summary>
        /// Gets the material at the specified index. If the material is not overriden by this component, it will try to get it from <see cref="Stride.Rendering.Model.Materials"/>
        /// </summary>
        /// <param name="index">The index of the material</param>
        /// <returns>The material at the specified index or null if not found</returns>
        public Material GetMaterial(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), @"index cannot be < 0");

            Material material;
            if (Materials.TryGetValue(index, out material))
            {
                return material;
            }
            // TODO: if Model is null, shouldn't we always return null?
            if (Model != null && index < Model.Materials.Count)
            {
                material = Model.Materials[index].Material;
            }
            return material;
        }

        /// <summary>
        /// Gets the number of materials (computed from <see cref="Stride.Rendering.Model.Materials"/>)
        /// </summary>
        /// <returns></returns>
        public int GetMaterialCount()
        {
            if (Model != null)
            {
                return Model.Materials.Count;
            }
            return 0;
        }

        private void ModelUpdated()
        {
            if (model != null)
            {
                // Create mesh-per-entity state
                meshInfos.Clear();
                foreach (var mesh in model.Meshes)
                {
                    var meshData = new MeshInfo();
                    meshInfos.Add(meshData);

                    if (mesh.Skinning != null)
                        meshData.BlendMatrices = new Matrix[mesh.Skinning.Bones.Length];

                    if (mesh.BlendShapes?.Targets != null)
                    {
                        var targetCount = mesh.BlendShapes.Targets.Length;
                        meshData.BlendShapeWeights = new float[targetCount];
                        meshData.PreviousWeights = new float[targetCount];
                        meshData.BlendShapeDirty = true;
                        meshData.BlendShapeInitialized = false;

                        // Clone MeshDraw so this entity gets its own VB[0] for CPU deformation
                        if (mesh.Draw != null)
                        {
                            meshData.ClonedMeshDraw = mesh.Draw.Clone();

                            // Allocate CPU staging buffer matching VB[0] size
                            var baseVB = mesh.Draw.VertexBuffers[0];
                            var bufferSize = baseVB.Stride * baseVB.Count;
                            meshData.DeformedVertexData = new byte[bufferSize];
                        }
                    }
                }

                if (skeleton != null)
                {
                    // Reuse previous ModelViewHierarchy
                    skeleton.Initialize(model.Skeleton);
                }
                else
                {
                    skeleton = new SkeletonUpdater(model.Skeleton);
                }
            }
        }

        /// <summary>
        /// Updates the skeleton, skinning and bounding box with the associated transform component.
        /// </summary>
        /// <param name="transformComponent">The transform component.</param>
        internal void Update(TransformComponent transformComponent)
        {
            if (!Enabled || model == null)
                return;

            ref Matrix worldMatrix = ref transformComponent.WorldMatrix;

            // Check if scaling is negative
            var up = Vector3.Cross(worldMatrix.Right, worldMatrix.Forward);
            bool isScalingNegative = Vector3.Dot(worldMatrix.Up, up) < 0.0f;

            // Make sure skeleton is up to date
            CheckSkeleton();
            if (skeleton != null)
            {
                // Update model view hierarchy node matrices
                skeleton.NodeTransformations[0].LocalMatrix = worldMatrix;
                skeleton.NodeTransformations[0].IsScalingNegative = isScalingNegative;
                skeleton.UpdateMatrices();
            }

            // Update the bounding sphere / bounding box in world space
            BoundingSphere = BoundingSphere.Empty;
            BoundingBox = BoundingBox.Empty;
            bool modelHasBoundingBox = false;

            for (int meshIndex = 0; meshIndex < Model.Meshes.Count; meshIndex++)
            {
                var mesh = Model.Meshes[meshIndex];
                var meshInfo = meshInfos[meshIndex];
                meshInfo.BoundingSphere = BoundingSphere.Empty;
                meshInfo.BoundingBox = BoundingBox.Empty;

                if (mesh.Skinning != null && skeleton != null && !meshInfo.UseExternalBlendMatrices)
                {
                    bool meshHasBoundingBox = false;
                    var bones = mesh.Skinning.Bones;

                    // For skinned meshes, bounding box is union of the bounding boxes of the unskinned mesh, transformed by each affecting bone.
                    for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                    {
                        var nodeIndex = bones[boneIndex].NodeIndex;
                        Matrix.Multiply(ref bones[boneIndex].LinkToMeshMatrix, ref skeleton.NodeTransformations[nodeIndex].WorldMatrix, out meshInfo.BlendMatrices[boneIndex]);

                        BoundingBox skinnedBoundingBox;
                        BoundingBox.Transform(ref mesh.BoundingBox, ref meshInfo.BlendMatrices[boneIndex], out skinnedBoundingBox);
                        BoundingSphere skinnedBoundingSphere;
                        BoundingSphere.Transform(ref mesh.BoundingSphere, ref meshInfo.BlendMatrices[boneIndex], out skinnedBoundingSphere);

                        if (meshHasBoundingBox)
                        {
                            BoundingBox.Merge(ref meshInfo.BoundingBox, ref skinnedBoundingBox, out meshInfo.BoundingBox);
                            BoundingSphere.Merge(ref meshInfo.BoundingSphere, ref skinnedBoundingSphere, out meshInfo.BoundingSphere);
                        }
                        else
                        {
                            meshHasBoundingBox = true;
                            meshInfo.BoundingSphere = skinnedBoundingSphere;
                            meshInfo.BoundingBox = skinnedBoundingBox;
                        }
                    }
                }
                else
                {
                    // If there is a skeleton, use the corresponding node's transform. Otherwise, fall back to the model transform.
                    var transform = skeleton != null ? skeleton.NodeTransformations[mesh.NodeIndex].WorldMatrix : worldMatrix;
                    BoundingBox.Transform(ref mesh.BoundingBox, ref transform, out meshInfo.BoundingBox);
                    BoundingSphere.Transform(ref mesh.BoundingSphere, ref transform, out meshInfo.BoundingSphere);
                }

                if (modelHasBoundingBox)
                {
                    BoundingBox.Merge(ref BoundingBox, ref meshInfo.BoundingBox, out BoundingBox);
                    BoundingSphere.Merge(ref BoundingSphere, ref meshInfo.BoundingSphere, out BoundingSphere);
                }
                else
                {
                    BoundingBox = meshInfo.BoundingBox;
                    BoundingSphere = meshInfo.BoundingSphere;
                    modelHasBoundingBox = true;
                }
            }
        }
    }
}
