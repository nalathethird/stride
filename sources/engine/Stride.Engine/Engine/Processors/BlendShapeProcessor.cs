// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Engine.Processors
{
    /// <summary>
    /// Processor that syncs <see cref="BlendShapeComponent"/> weights into <see cref="ModelComponent.MeshInfo.BlendShapeWeights"/>
    /// each frame and dispatches GPU or CPU deformation when weights change.
    /// </summary>
    public class BlendShapeProcessor : EntityProcessor<BlendShapeComponent, BlendShapeProcessor.BlendShapeData>, IDisposable
    {
        private BlendShapeGpuDeformer gpuDeformer;
        private bool useGpuDeformation;

        public BlendShapeProcessor()
            : base(typeof(ModelComponent))
        {
            // Run after ModelTransformProcessor (-100) but before rendering
            Order = -50;
        }

        protected override BlendShapeData GenerateComponentData(Entity entity, BlendShapeComponent component)
        {
            return new BlendShapeData
            {
                BlendShapeComponent = component,
                ModelComponent = entity.Get<ModelComponent>(),
            };
        }

        protected override bool IsAssociatedDataValid(Entity entity, BlendShapeComponent component, BlendShapeData associatedData)
        {
            return component == associatedData.BlendShapeComponent
                && entity.Get<ModelComponent>() == associatedData.ModelComponent;
        }

        protected override void OnEntityComponentAdding(Entity entity, BlendShapeComponent component, BlendShapeData data)
        {
            // Auto-initialize weights from the model if not already populated
            if (data.ModelComponent != null)
            {
                component.InitializeFromModel(data.ModelComponent);
            }
        }

        protected override void OnEntityComponentRemoved(Entity entity, BlendShapeComponent component, BlendShapeData data)
        {
            // Dispose GPU buffers for this entity's meshes
            if (data.ModelComponent?.MeshInfos != null)
            {
                foreach (var meshInfo in data.ModelComponent.MeshInfos)
                {
                    if (meshInfo.GpuBlendShapeInitialized)
                        BlendShapeGpuDeformer.DisposeGpuBuffers(meshInfo);
                }
            }
        }

        public override void Draw(RenderContext context)
        {
            var graphicsDevice = Services.GetService<IGraphicsDeviceService>()?.GraphicsDevice;
            var graphicsContext = context.GraphicsContext;

            // Lazy-initialize GPU deformer
            if (gpuDeformer == null && graphicsDevice != null && graphicsContext != null)
            {
                useGpuDeformation = BlendShapeGpuDeformer.IsSupported(graphicsDevice);
                if (useGpuDeformation)
                    gpuDeformer = new BlendShapeGpuDeformer(graphicsDevice, graphicsContext, Services);
            }

            foreach (var kv in ComponentDatas)
            {
                var data = kv.Value;
                var blendShapeComponent = data.BlendShapeComponent;
                var modelComponent = data.ModelComponent;

                if (!blendShapeComponent.Enabled || modelComponent?.Model == null)
                    continue;

                // Sync animation array → dictionary (animation writes to WeightValues[])
                var targetNames = blendShapeComponent.TargetNames;
                var weightValues = blendShapeComponent.WeightValues;
                if (targetNames != null && weightValues != null)
                {
                    for (int i = 0; i < targetNames.Length && i < weightValues.Length; i++)
                    {
                        blendShapeComponent.Weights[targetNames[i]] = weightValues[i];
                    }
                }

                // Ensure MeshInfos are up-to-date
                var meshInfos = modelComponent.MeshInfos;
                if (meshInfos == null)
                    continue;

                for (int meshIndex = 0; meshIndex < modelComponent.Model.Meshes.Count && meshIndex < meshInfos.Count; meshIndex++)
                {
                    var mesh = modelComponent.Model.Meshes[meshIndex];
                    var meshInfo = meshInfos[meshIndex];

                    if (mesh.BlendShapes?.Targets == null || meshInfo.BlendShapeWeights == null)
                        continue;

                    // Sync component weights → meshInfo weights
                    var targets = mesh.BlendShapes.Targets;
                    for (int targetIdx = 0; targetIdx < targets.Length && targetIdx < meshInfo.BlendShapeWeights.Length; targetIdx++)
                    {
                        var targetName = targets[targetIdx]?.Name;
                        if (targetName != null && blendShapeComponent.Weights.TryGetValue(targetName, out var weight))
                        {
                            meshInfo.BlendShapeWeights[targetIdx] = weight;
                        }
                    }

                    // Dirty detection: check if weights changed since last frame
                    bool isDirty = meshInfo.BlendShapeDirty;
                    if (!isDirty && meshInfo.PreviousWeights != null)
                    {
                        for (int i = 0; i < meshInfo.BlendShapeWeights.Length && i < meshInfo.PreviousWeights.Length; i++)
                        {
                            if (Math.Abs(meshInfo.BlendShapeWeights[i] - meshInfo.PreviousWeights[i]) > BlendShapeGpuDeformer.WeightEpsilon)
                            {
                                isDirty = true;
                                break;
                            }
                        }
                    }

                    // Dispatch deformation
                    bool useFused = blendShapeComponent.UseFusedSkinning && mesh.Skinning != null;

                    if (useGpuDeformation && blendShapeComponent.UseGpuDeformation && gpuDeformer != null)
                    {
                        DeformGpu(graphicsContext, meshInfo, mesh, isDirty, useFused, modelComponent);
                    }
                    else if (isDirty)
                    {
                        BlendShapeDeformer.Deform(mesh.BlendShapes, meshInfo.BlendShapeWeights, meshInfo.DeformedVertexData);
                    }

                    // Save current weights for next frame's dirty check
                    if (meshInfo.PreviousWeights != null)
                        Array.Copy(meshInfo.BlendShapeWeights, meshInfo.PreviousWeights, meshInfo.BlendShapeWeights.Length);
                    meshInfo.BlendShapeDirty = false;
                }
            }
        }

        private void DeformGpu(GraphicsContext graphicsContext, ModelComponent.MeshInfo meshInfo,
            Mesh mesh, bool isDirty, bool useFused, ModelComponent modelComponent)
        {
            // Initialize GPU buffers on first use
            if (!meshInfo.GpuBlendShapeInitialized)
            {
                // Prefer sparse CSR path when cooked data is available
                if (mesh.BlendShapes?.CookedData != null)
                    gpuDeformer.InitializeGpuBuffersSparse(meshInfo, mesh.BlendShapes, mesh);
                else
                    gpuDeformer.InitializeGpuBuffers(meshInfo, mesh.BlendShapes, mesh);
            }

            if (!meshInfo.GpuBlendShapeInitialized)
                return;

            // Sparse path: weight check is inside the shader, dispatch every dirty frame
            if (meshInfo.UseSparseGpuPath)
            {
                if (isDirty)
                    gpuDeformer.DispatchSparse(graphicsContext, meshInfo, mesh.BlendShapes, meshInfo.BlendShapeWeights);
                return;
            }

            if (useFused)
            {
                // Initialize fused skinning buffers once
                if (!meshInfo.UseFusedSkinning)
                    gpuDeformer.InitializeFusedSkinningBuffers(meshInfo, mesh);

                if (meshInfo.UseFusedSkinning)
                {
                    // Fused path dispatches every frame (bone matrices change each frame)
                    var skeleton = modelComponent.Skeleton;
                    if (skeleton != null)
                    {
                        var meshWorld = skeleton.NodeTransformations[mesh.NodeIndex].WorldMatrix;
                        gpuDeformer.DispatchFused(graphicsContext, meshInfo, mesh.BlendShapes,
                            meshInfo.BlendShapeWeights, ref meshWorld);
                    }
                }
            }
            else if (isDirty)
            {
                // Blend-only path: dispatch only when weights changed
                gpuDeformer.Dispatch(graphicsContext, meshInfo, mesh.BlendShapes, meshInfo.BlendShapeWeights);
            }
        }

        public void Dispose()
        {
            gpuDeformer?.Dispose();
            gpuDeformer = null;
        }

        public class BlendShapeData
        {
            public BlendShapeComponent BlendShapeComponent;
            public ModelComponent ModelComponent;
        }
    }
}
