// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Diagnostics;
using Stride.Core.Threading;
using Stride.Rendering.Materials;

namespace Stride.Rendering
{
    /// <summary>
    /// Computes and uploads blend shape (morph target) weights to the GPU.
    /// </summary>
    public class BlendShapeRenderFeature : SubRenderFeature
    {
        private StaticObjectPropertyKey<RenderEffect> renderEffectKey;
        private StaticObjectPropertyKey<BlendShapeInfo> blendShapeInfoKey;
        private ObjectPropertyKey<float[]> renderModelBlendShapeWeightsKey;

        private ConstantBufferOffsetReference blendShapeWeightsOffset;

        private static readonly ProfilingKey PrepareEffectPermutationsKey = new ProfilingKey("BlendShapeRenderFeature.PrepareEffectPermutations");

        public int MaxBlendShapeCount { get; set; } = 8;

        private struct BlendShapeInfo
        {
            public ParameterCollection Parameters;
            public int PermutationCounter;
            public bool HasBlendShapes;
            public int BlendShapeCount;
            public bool HasTangent;
        }

        protected override void InitializeCore()
        {
            renderModelBlendShapeWeightsKey = RootRenderFeature.RenderData.CreateObjectKey<float[]>();
            blendShapeInfoKey = RootRenderFeature.RenderData.CreateStaticObjectKey<BlendShapeInfo>();
            renderEffectKey = ((RootEffectRenderFeature)RootRenderFeature).RenderEffectKey;

            blendShapeWeightsOffset = ((RootEffectRenderFeature)RootRenderFeature).CreateDrawCBufferOffsetSlot(TransformationBlendShapeKeys.BlendShapeWeightArray.Name);
        }

        public override void PrepareEffectPermutations(RenderDrawContext context)
        {
            using var _ = Profiler.Begin(PrepareEffectPermutationsKey);
            var blendShapeInfos = RootRenderFeature.RenderData.GetData(blendShapeInfoKey);
            var renderEffects = RootRenderFeature.RenderData.GetData(renderEffectKey);
            int effectSlotCount = ((RootEffectRenderFeature)RootRenderFeature).EffectPermutationSlotCount;

            Dispatcher.ForEach(RootRenderFeature.ObjectNodeReferences, objectNodeReference =>
            {
                var objectNode = RootRenderFeature.GetObjectNode(objectNodeReference);
                var renderMesh = (RenderMesh)objectNode.RenderObject;
                var staticObjectNode = renderMesh.StaticObjectNode;

                ref var info = ref blendShapeInfos[staticObjectNode];
                var parameters = renderMesh.Mesh.Parameters;
                if (parameters != info.Parameters || parameters.PermutationCounter != info.PermutationCounter)
                {
                    info.Parameters = parameters;
                    info.PermutationCounter = parameters.PermutationCounter;
                    info.HasBlendShapes = parameters.Get(MaterialKeys.HasBlendShapes);
                    info.BlendShapeCount = parameters.Get(MaterialKeys.BlendShapeCount);
                    info.HasTangent = parameters.Get(MaterialKeys.BlendShapeHasTangent);
                }

                for (int i = 0; i < effectSlotCount; ++i)
                {
                    var staticEffectObjectNode = staticObjectNode * effectSlotCount + i;
                    var renderEffect = renderEffects[staticEffectObjectNode];

                    if (renderEffect == null || !renderEffect.IsUsedDuringThisFrame(RenderSystem))
                        continue;

                    if (renderMesh.Mesh.BlendShapes != null)
                    {
                        renderEffect.EffectValidator.ValidateParameter(MaterialKeys.HasBlendShapes, info.HasBlendShapes);
                        var count = Math.Max(info.BlendShapeCount, renderMesh.Mesh.BlendShapes.Targets.Length);
                        renderEffect.EffectValidator.ValidateParameter(MaterialKeys.BlendShapeCount, count);
                        renderEffect.EffectValidator.ValidateParameter(MaterialKeys.BlendShapeHasTangent, info.HasTangent);
                    }
                }
            });
        }

        public override void Extract()
        {
            var renderModelBlendShapeWeights = RootRenderFeature.RenderData.GetData(renderModelBlendShapeWeightsKey);

            Dispatcher.ForEach(RootRenderFeature.ObjectNodeReferences, objectNodeReference =>
            {
                var objectNode = RootRenderFeature.GetObjectNode(objectNodeReference);
                var renderMesh = (RenderMesh)objectNode.RenderObject;

                renderModelBlendShapeWeights[objectNodeReference] = renderMesh.BlendShapeWeights;
            });
        }

        public override unsafe void Prepare(RenderDrawContext context)
        {
            var renderModelBlendShapeWeightsData = RootRenderFeature.RenderData.GetData(renderModelBlendShapeWeightsKey);

            Dispatcher.ForBatched(RootRenderFeature.RenderNodes.Count, (from, toExclusive) =>
            {
                for (int i = from; i < toExclusive; i++)
                {
                    var renderNode = RootRenderFeature.RenderNodes[i];
                    var perDrawLayout = renderNode.RenderEffect.Reflection?.PerDrawLayout;
                    if (perDrawLayout == null)
                        continue;

                    var weightsOffset = perDrawLayout.GetConstantBufferOffset(blendShapeWeightsOffset);
                    if (weightsOffset == -1)
                        continue;

                    var weights = renderModelBlendShapeWeightsData[renderNode.RenderObject.ObjectNode];
                    if (weights == null)
                        continue;

                    var mappedCB = (byte*)renderNode.Resources.ConstantBuffer.Data + weightsOffset;

                    // Clamp copy size to prevent overwriting adjacent cbuffer memory
                    // when the model has more targets than MaxBlendShapeCount.
                    var copyCount = Math.Min(weights.Length, MaxBlendShapeCount);
                    fixed (float* weightsPtr = weights)
                    {
                        MemoryUtilities.CopyWithAlignmentFallback(mappedCB, weightsPtr, (uint)copyCount * sizeof(float));
                    }
                }
            });
        }
    }
}
