// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using Stride.Core.Assets.Editor.Quantum.NodePresenters;
using Stride.Core.Assets.Quantum;
using Stride.Core.Extensions;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Core.Collections;
using Stride.Core.Presentation.ViewModels;
using Stride.Core.Serialization;
using Stride.Assets.Models;
using Stride.Engine;

namespace Stride.Assets.Presentation.ViewModel
{
    public class BlendShapeComponentViewModel : DispatcherViewModel
    {
        private readonly EntityViewModel entity;
        private IMemberNode modelContent;

        public BlendShapeComponentViewModel(IViewModelServiceProvider serviceProvider, EntityViewModel entity)
            : base(serviceProvider)
        {
            this.entity = entity;
        }

        public void Initialize()
        {
            var assetNode = entity.Editor.NodeContainer.GetOrCreateNode(entity.AssetSideEntity);
            var componentNode = assetNode[nameof(Entity.Components)].Target;
            componentNode.ItemChanged += ComponentListChanged;
            RegisterModelChanged();
        }

        public override void Destroy()
        {
            var assetNode = entity.Editor.NodeContainer.GetOrCreateNode(entity.AssetSideEntity);
            var componentNode = assetNode[nameof(Entity.Components)].Target;
            componentNode.ItemChanged -= ComponentListChanged;
            UnregisterModelChanged();

            base.Destroy();
        }

        internal void UpdateNodePresenter(INodePresenter node)
        {
            if (node.Value is BlendShapeComponent && node.Parent?.Value is EntityComponentCollection)
            {
                // Add dependency on the ModelComponent.Model so the blend shape weights section refreshes
                // when the model changes (different models may have different blend shape targets).
                var modelComponentNode = FindSiblingComponentNode<ModelComponent>(node);
                if (modelComponentNode != null)
                {
                    var modelNode = modelComponentNode[nameof(ModelComponent.Model)];
                    if (modelNode != null)
                    {
                        var weights = node[nameof(BlendShapeComponent.Weights)];
                        weights?.AddDependency(modelNode, false);
                    }
                }
            }
        }

        private static INodePresenter FindSiblingComponentNode<T>(INodePresenter componentNode) where T : EntityComponent
        {
            if (componentNode.Parent == null)
                return null;

            foreach (var sibling in componentNode.Parent.Children)
            {
                if (sibling.Value is T)
                    return sibling;
            }
            return null;
        }

        private void ComponentListChanged(object sender, ItemChangeEventArgs e)
        {
            if (e.ChangeType == ContentChangeType.CollectionAdd)
            {
                if (e.NewValue is ModelComponent)
                {
                    RegisterModelChanged();
                }
            }
            if (e.ChangeType == ContentChangeType.CollectionRemove)
            {
                if (e.OldValue is ModelComponent)
                {
                    UnregisterModelChanged();
                }
            }
        }

        private void RegisterModelChanged()
        {
            UnregisterModelChanged();
            var modelComponent = entity.AssetSideEntity.Get<ModelComponent>();
            if (modelComponent != null)
            {
                var modelNode = entity.Editor.NodeContainer.GetNode(modelComponent);
                modelContent = modelNode[nameof(ModelComponent.Model)];
                modelContent.ValueChanged += ModelChanged;
            }
        }

        private void UnregisterModelChanged()
        {
            if (modelContent != null)
            {
                modelContent.ValueChanged -= ModelChanged;
                modelContent = null;
            }
        }

        private void ModelChanged(object sender, MemberNodeChangeEventArgs e)
        {
            if (e.NewValue != e.OldValue && !entity.Editor.UndoRedoService.UndoRedoInProgress)
            {
                // When the model changes, clear existing blend shape weights since the new model
                // may have a different set of blend shape targets.
                var blendShapeComponent = entity.AssetSideEntity.Get<BlendShapeComponent>();
                if (blendShapeComponent != null)
                {
                    var weightsNode = (IObjectNode)entity.Editor.NodeContainer.GetNode(blendShapeComponent)[nameof(BlendShapeComponent.Weights)].Target;
                    if (weightsNode != null)
                    {
                        var indices = weightsNode.Indices.ToList();
                        foreach (var index in indices)
                        {
                            var item = weightsNode.Retrieve(index);
                            weightsNode.Remove(item, index);
                        }
                    }
                }
            }
        }
    }
}
