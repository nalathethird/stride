// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Engine;
using Stride.Rendering;

namespace Stride.Engine.Processors
{
    /// <summary>
    /// Watches <see cref="ModelComponent"/> entities and automatically adds a
    /// <see cref="BlendShapeComponent"/> whenever the assigned model contains
    /// blend shape (morph target) data. This removes the need for users to
    /// manually add the component in the editor.
    /// </summary>
    public class BlendShapeAutoProvisionProcessor : EntityProcessor<ModelComponent, BlendShapeAutoProvisionProcessor.ProvisionState>
    {
        public BlendShapeAutoProvisionProcessor()
        {
            // Run early so the BlendShapeComponent is available before BlendShapeProcessor executes.
            Order = -200;
        }

        protected override ProvisionState GenerateComponentData(Entity entity, ModelComponent component)
        {
            return new ProvisionState();
        }

        protected override bool IsAssociatedDataValid(Entity entity, ModelComponent component, ProvisionState associatedData)
        {
            return true;
        }

        protected override void OnEntityComponentAdding(Entity entity, ModelComponent component, ProvisionState data)
        {
            TryProvision(entity, component, data);
        }

        public override void Draw(RenderContext context)
        {
            foreach (var kv in ComponentDatas)
            {
                var modelComponent = kv.Key;
                var data = kv.Value;

                // Detect model changes (hot-swap or late load)
                if (modelComponent.Model != data.LastModel)
                {
                    TryProvision(modelComponent.Entity, modelComponent, data);
                }
            }
        }

        private static void TryProvision(Entity entity, ModelComponent modelComponent, ProvisionState data)
        {
            data.LastModel = modelComponent.Model;

            var hasTargets = HasBlendShapeTargets(modelComponent.Model);
            var existing = entity.Get<BlendShapeComponent>();

            if (hasTargets)
            {
                // Model has blend shapes — add component if missing
                if (existing == null)
                {
                    entity.Add(new BlendShapeComponent());
                }
            }
            else
            {
                // Model has no blend shapes — remove auto-provisioned component if present
                if (existing != null && data.WasAutoProvisioned)
                {
                    entity.Remove<BlendShapeComponent>();
                    data.WasAutoProvisioned = false;
                }
            }

            if (hasTargets && existing == null)
            {
                data.WasAutoProvisioned = true;
            }
        }

        /// <summary>
        /// Returns true if any mesh in the model has at least one blend shape target defined.
        /// </summary>
        internal static bool HasBlendShapeTargets(Model model)
        {
            if (model?.Meshes == null)
                return false;

            foreach (var mesh in model.Meshes)
            {
                if (mesh.BlendShapes?.Targets != null && mesh.BlendShapes.Targets.Length > 0)
                    return true;
            }

            return false;
        }

        public class ProvisionState
        {
            public Model LastModel;
            public bool WasAutoProvisioned;
        }
    }
}
