// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Collections;
using Stride.Engine.Design;
using Stride.Engine.Processors;
using Stride.Rendering;
using Stride.Updater;

namespace Stride.Engine
{
    /// <summary>
    /// Provides per-entity blend shape (morph target) weight control for a <see cref="ModelComponent"/>.
    /// Weights are exposed to the editor and animation system.
    /// </summary>
    [DataContract("BlendShapeComponent")]
    [Display("Blend Shapes", Expand = ExpandRule.Once)]
    [DefaultEntityComponentProcessor(typeof(BlendShapeProcessor), ExecutionMode = ExecutionMode.Runtime | ExecutionMode.Thumbnail | ExecutionMode.Preview)]
    [ComponentOrder(11100)]
    [ComponentCategory("Model")]
    public sealed class BlendShapeComponent : ActivableEntityComponent
    {
        /// <summary>
        /// Per-blend-shape-target weight (0.0 = no deformation, 1.0 = full deformation).
        /// The dictionary key is the blend shape target name.
        /// </summary>
        /// <userdoc>The weight of each blend shape target. 0 means no effect, 1 means full effect.</userdoc>
        [DataMember(10)]
        [DataMemberRange(0.0, 1.0, 0.01, 0.1, 3)]
        [Display("Weights")]
        [MemberCollection(ReadOnly = false)]
        [DataMemberUpdatable]
        public Dictionary<string, float> Weights { get; } = new Dictionary<string, float>();

        /// <summary>
        /// Initializes the weight dictionary from the blend shape definitions on the model.
        /// Called when the component is first attached or the model changes.
        /// </summary>
        /// <param name="modelComponent">The model component to read blend shape targets from.</param>
        public void InitializeFromModel(ModelComponent modelComponent)
        {
            if (modelComponent?.Model == null)
                return;

            var existingTargetNames = new HashSet<string>();
            var orderedNames = new List<string>();

            foreach (var mesh in modelComponent.Model.Meshes)
            {
                if (mesh.BlendShapes?.Targets == null)
                    continue;

                foreach (var target in mesh.BlendShapes.Targets)
                {
                    if (target?.Name == null)
                        continue;

                    if (existingTargetNames.Add(target.Name))
                    {
                        orderedNames.Add(target.Name);
                    }

                    if (!Weights.ContainsKey(target.Name))
                    {
                        Weights[target.Name] = 0.0f;
                    }
                }
            }

            // Remove weights for targets that no longer exist in the model
            var keysToRemove = new List<string>();
            foreach (var key in Weights.Keys)
            {
                if (!existingTargetNames.Contains(key))
                    keysToRemove.Add(key);
            }
            foreach (var key in keysToRemove)
            {
                Weights.Remove(key);
            }

            // Build indexed arrays for animation system
            TargetNames = orderedNames.ToArray();
            WeightValues = new float[TargetNames.Length];
            for (int i = 0; i < TargetNames.Length; i++)
            {
                WeightValues[i] = Weights.TryGetValue(TargetNames[i], out var w) ? w : 0.0f;
            }
        }

        /// <summary>
        /// Ordered array of blend shape target names, set during initialization.
        /// Indices match <see cref="WeightValues"/> for animation system compatibility.
        /// </summary>
        [DataMemberIgnore]
        public string[] TargetNames { get; internal set; } = Array.Empty<string>();

        /// <summary>
        /// Flat weight array indexed by target index, targetable by the animation system.
        /// The animation property path is <c>[BlendShapeComponent.Key].WeightValues[i]</c>.
        /// </summary>
        [DataMemberIgnore]
        public float[] WeightValues { get; internal set; } = Array.Empty<float>();

        /// <summary>
        /// Gets or sets the weight for a specific blend shape target by name.
        /// </summary>
        /// <param name="targetName">The name of the blend shape target.</param>
        /// <returns>The current weight, or 0 if not found.</returns>
        public float GetWeight(string targetName)
        {
            return Weights.TryGetValue(targetName, out var weight) ? weight : 0.0f;
        }

        /// <summary>
        /// Sets the weight for a specific blend shape target by name.
        /// </summary>
        /// <param name="targetName">The name of the blend shape target.</param>
        /// <param name="weight">The weight value (typically 0.0 to 1.0).</param>
        public void SetWeight(string targetName, float weight)
        {
            Weights[targetName] = weight;
        }
    }
}
