// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Graphics;

namespace Stride.Rendering
{
    // Need to add support for fields in auto data converter
    [DataContract]
    public class MeshDraw
    {
        public PrimitiveType PrimitiveType;

        public int DrawCount;

        public int StartLocation;

        public VertexBufferBinding[] VertexBuffers;

        public IndexBufferBinding IndexBuffer;

        /// <summary>
        /// Creates a shallow clone of this <see cref="MeshDraw"/>, copying the VertexBuffers array
        /// so the clone can be given its own vertex buffer bindings without affecting the original.
        /// </summary>
        public MeshDraw Clone()
        {
            var clone = (MeshDraw)MemberwiseClone();
            if (VertexBuffers != null)
                clone.VertexBuffers = (VertexBufferBinding[])VertexBuffers.Clone();
            return clone;
        }
    }
}
