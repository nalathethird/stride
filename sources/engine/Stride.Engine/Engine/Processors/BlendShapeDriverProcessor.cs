// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Collections;
using Stride.Core.Mathematics;
using Stride.Rendering;

namespace Stride.Engine.Processors
{
    /// <summary>
    /// Processes <see cref="BlendShapeDriverComponent"/> to automatically calculate combination shapes
    /// and pose-space deformations before the <see cref="BlendShapeProcessor"/> dispatches the values to the GPU or CPU backends.
    /// Runs at Order = -60 (before BlendShapeProcessor which runs at -50).
    /// </summary>
    public class BlendShapeDriverProcessor : EntityProcessor<BlendShapeDriverComponent>
    {
        public BlendShapeDriverProcessor()
            : base(typeof(BlendShapeComponent), typeof(ModelComponent)) // Ensure it only works on entities that have BlendShapes and Models
        {
            Order = -60;
        }

        public override void Draw(RenderContext context)
        {
            foreach (var driverState in ComponentDatas)
            {
                var driver = driverState.Key;
                var entity = driver.Entity;
                var blendShapeComponent = entity.Get<BlendShapeComponent>();

                if (blendShapeComponent?.Weights == null)
                    continue;

                bool modified = false;

                // --- 1. Evaluate Pose-Space Deformations ---
                var modelComponent = entity.Get<ModelComponent>();
                var skeleton = modelComponent?.Skeleton;

                if (skeleton != null && driver.PoseSpaceDeformations != null && driver.PoseSpaceDeformations.Count > 0)
                {
                    foreach (var psd in driver.PoseSpaceDeformations)
                    {
                        if (string.IsNullOrEmpty(psd.TargetShape) || string.IsNullOrEmpty(psd.NodeName))
                            continue;

                        // Find the target node in the skeleton
                        int nodeIndex = -1;
                        for (int i = 0; i < skeleton.Nodes.Length; i++)
                        {
                            if (skeleton.Nodes[i].Name == psd.NodeName)
                            {
                                nodeIndex = i;
                                break;
                            }
                        }

                        if (nodeIndex >= 0 && nodeIndex < skeleton.NodeTransformations.Length)
                        {
                            var transform = skeleton.NodeTransformations[nodeIndex].LocalMatrix;
                            transform.Decompose(out _, out Quaternion localRotation, out _);

                            // Extract the signed angle around the specified axis using
                            // the cross product to determine rotation direction.
                            Vector3 transformedAxis = Vector3.Transform(psd.RotationAxis, localRotation);
                            float dot = MathUtil.Clamp(Vector3.Dot(psd.RotationAxis, transformedAxis), -1f, 1f);
                            float angleRad = (float)Math.Acos(dot);

                            // Determine sign via cross product projected onto the rotation axis
                            Vector3 cross;
                            Vector3.Cross(ref psd.RotationAxis, ref transformedAxis, out cross);
                            float sign = Vector3.Dot(cross, psd.RotationAxis) >= 0 ? 1f : -1f;
                            float angleDeg = MathUtil.RadiansToDegrees(angleRad) * sign;

                            // Map angle to weight
                            float range = psd.MaxAngle - psd.MinAngle;
                            float t = range <= float.Epsilon ? 0 : MathUtil.Clamp((angleDeg - psd.MinAngle) / range, 0f, 1f);
                            float finalWeight = MathUtil.Lerp(psd.MinWeight, psd.MaxWeight, t);

                            if (blendShapeComponent.Weights.ContainsKey(psd.TargetShape))
                            {
                                blendShapeComponent.Weights[psd.TargetShape] = finalWeight;
                                modified = true;
                            }
                        }
                    }
                }

                // --- 2. Evaluate Combination Shapes ---
                if (driver.Combinations != null && driver.Combinations.Count > 0)
                {
                    foreach (var combo in driver.Combinations)
                    {
                        if (string.IsNullOrEmpty(combo.TargetShape) || 
                            string.IsNullOrEmpty(combo.SourceShapeA) || 
                            string.IsNullOrEmpty(combo.SourceShapeB))
                            continue;

                        // Read source weights
                        blendShapeComponent.Weights.TryGetValue(combo.SourceShapeA, out var weightA);
                        blendShapeComponent.Weights.TryGetValue(combo.SourceShapeB, out var weightB);

                        // Multiplicative combination is the industry standard for intersecting shapes
                        // e.g., Smile (0.8) * Squint (0.8) = Combo (0.64).
                        float result = (weightA * weightB) * combo.Multiplier;

                        // Only apply if the target actually exists in the dictionary
                        if (blendShapeComponent.Weights.ContainsKey(combo.TargetShape))
                        {
                            blendShapeComponent.Weights[combo.TargetShape] = result;
                            modified = true;
                        }
                    }
                }

                // Bump revision so BlendShapeProcessor syncs dict → array this frame
                if (modified)
                {
                    blendShapeComponent.DictionaryRevision++;
                }
            }
        }
    }
}