// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Engine;
using Stride.Rendering;

namespace Stride.Engine.Processors
{
    /// <summary>
    /// Processor that syncs <see cref="BlendShapeComponent"/> weights into <see cref="ModelComponent.MeshInfo.BlendShapeWeights"/>
    /// each frame so the render pipeline picks them up.
    /// </summary>
    public class BlendShapeProcessor : EntityProcessor<BlendShapeComponent, BlendShapeProcessor.BlendShapeData>
    {
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

        public override void Draw(RenderContext context)
        {
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

                    var targets = mesh.BlendShapes.Targets;
                    for (int targetIdx = 0; targetIdx < targets.Length && targetIdx < meshInfo.BlendShapeWeights.Length; targetIdx++)
                    {
                        var targetName = targets[targetIdx]?.Name;
                        if (targetName != null && blendShapeComponent.Weights.TryGetValue(targetName, out var weight))
                        {
                            meshInfo.BlendShapeWeights[targetIdx] = weight;
                        }
                    }
                }
            }
        }

        public class BlendShapeData
        {
            public BlendShapeComponent BlendShapeComponent;
            public ModelComponent ModelComponent;
        }
    }
}
