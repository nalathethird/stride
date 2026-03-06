// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine.Design;

namespace Stride.Engine
{
    /// <summary>
    /// Component that automatically drives blend shape weights based on combinations of other shapes or skeletal pose-space rotations.
    /// This runs before <see cref="BlendShapeProcessor"/> to set the final weights.
    /// </summary>
    [DataContract("BlendShapeDriverComponent")]
    [Display("Blend Shape Driver", Expand = ExpandRule.Once)]
    [DefaultEntityComponentProcessor(typeof(Processors.BlendShapeDriverProcessor))]
    [ComponentCategory("Models")]
    public class BlendShapeDriverComponent : EntityComponent
    {
        [DataMember(10)]
        [Display("Combination Shapes")]
        public List<BlendShapeCombination> Combinations { get; } = new List<BlendShapeCombination>();

        [DataMember(20)]
        [Display("Pose Space Deformations")]
        public List<PoseSpaceDeformation> PoseSpaceDeformations { get; } = new List<PoseSpaceDeformation>();
        
        /// <summary>
        /// If enabled, the GPU Compute Shader will calculate the vertex tension (stretch/compression)
        /// and write it to the specified TexCoord channel stream for materials to use.
        /// </summary>
        [DataMember(30)]
        [Display("Calculate Vertex Tension (GPU)")]
        public bool CalculateTension { get; set; } = false;

        [DataMember(40)]
        [Display("Tension TexCoord Index")]
        public int TensionTexCoordIndex { get; set; } = 1;
    }

    [DataContract("BlendShapeCombination")]
    public class BlendShapeCombination
    {
        [DataMember(10)]
        public string TargetShape { get; set; }

        [DataMember(20)]
        public string SourceShapeA { get; set; }

        [DataMember(30)]
        public string SourceShapeB { get; set; }

        /// <summary>
        /// Target = (SourceA * SourceB) * Multiplier
        /// Use 1.0 for standard intersections, or negative values for subtractive corrections.
        /// </summary>
        [DataMember(40)]
        public float Multiplier { get; set; } = 1.0f;
    }

    [DataContract("PoseSpaceDeformation")]
    public class PoseSpaceDeformation
    {
        [DataMember(10)]
        public string TargetShape { get; set; }

        [DataMember(20)]
        [Display("Bone / Node Name")]
        public string NodeName { get; set; }

        [DataMember(30)]
        [Display("Local Rotation Axis")]
        public Vector3 RotationAxis { get; set; } = Vector3.UnitX;

        [DataMember(40)]
        [Display("Min Angle (Degrees)")]
        public float MinAngle { get; set; } = 0.0f;

        [DataMember(50)]
        [Display("Max Angle (Degrees)")]
        public float MaxAngle { get; set; } = 90.0f;

        [DataMember(60)]
        [Display("Min Weight")]
        public float MinWeight { get; set; } = 0.0f;

        [DataMember(70)]
        [Display("Max Weight")]
        public float MaxWeight { get; set; } = 1.0f;
    }
}