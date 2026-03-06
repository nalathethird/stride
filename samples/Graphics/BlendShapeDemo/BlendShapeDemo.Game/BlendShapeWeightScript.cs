// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace BlendShapeDemo
{
    /// <summary>
    /// Oscillates blend shape (morph target) weights at runtime to demonstrate
    /// how to control morph targets from C# code.
    ///
    /// Attach this script to an entity that has a <see cref="ModelComponent"/> whose
    /// model contains morph targets. A <see cref="BlendShapeComponent"/> is added
    /// automatically when such a model is loaded.
    ///
    /// Each blend shape target weight is driven by a sine wave with a unique phase
    /// offset, creating a ripple effect across all targets. Set <see cref="TargetName"/>
    /// to restrict animation to a single named target.
    /// </summary>
    public class BlendShapeWeightScript : SyncScript
    {
        /// <summary>
        /// Speed multiplier for the sine-wave oscillation.
        /// </summary>
        /// <userdoc>Speed multiplier for the sine-wave oscillation of blend shape weights.</userdoc>
        [DataMember(10)]
        public float Speed { get; set; } = 1.0f;

        /// <summary>
        /// When set, only this target is animated. Leave empty to animate all targets.
        /// </summary>
        /// <userdoc>Name of a single blend shape target to animate. Leave empty to animate all targets.</userdoc>
        [DataMember(20)]
        public string TargetName { get; set; }

        private BlendShapeComponent blendShapes;

        public override void Start()
        {
            blendShapes = Entity.Get<BlendShapeComponent>();

            if (blendShapes == null)
            {
                Log.Warning(
                    "BlendShapeWeightScript on '{0}': no BlendShapeComponent found. " +
                    "Make sure the model has morph targets.",
                    Entity.Name);
            }
        }

        public override void Update()
        {
            if (blendShapes == null || blendShapes.TargetNames == null)
                return;

            float time = (float)Game.UpdateTime.Total.TotalSeconds * Speed;

            if (!string.IsNullOrEmpty(TargetName))
            {
                // Animate a single named target
                float weight = (float)(Math.Sin(time) * 0.5 + 0.5);
                blendShapes.SetWeight(TargetName, weight);
            }
            else
            {
                // Animate all targets with phase offsets for a ripple effect
                string[] targets = blendShapes.TargetNames;
                int count = targets.Length;
                for (int i = 0; i < count; i++)
                {
                    float phase = count > 1 ? i * MathUtil.TwoPi / count : 0f;
                    float weight = (float)(Math.Sin(time + phase) * 0.5 + 0.5);
                    blendShapes.SetWeight(targets[i], weight);
                }
            }
        }
    }
}
