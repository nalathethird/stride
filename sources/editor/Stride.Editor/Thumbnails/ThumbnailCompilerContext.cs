// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using System.Threading.Tasks;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Core.Storage;
using Stride.Editor.Resources;
using Stride.Graphics;

namespace Stride.Editor.Thumbnails
{
    /// <summary>Compiler context for building asset thumbnails.</summary>
    public class ThumbnailCompilerContext : AssetCompilerContext
    {
        public ThumbnailCompilerContext()
        {
            ThumbnailResolution = 128 * Int2.One;
            CompilationContext = typeof(ThumbnailCompilationContext);
        }

        /// <summary>Desired resolution for thumbnails.</summary>
        public Int2 ThumbnailResolution { get; private set; }

        /// <summary><c>true</c> if <see cref="ThumbnailBuilt"/> has subscribers; avoids unnecessary stream operations.</summary>
        public bool ShouldNotifyThumbnailBuilt => ThumbnailBuilt != null;

        /// <summary>Fallback thumbnail data shown when a build fails.</summary>
        public static Task<byte[]> BuildFailedThumbnail = Task.Run(() => HandleBrokenThumbnail());

        /// <summary>Raised when a thumbnail build finishes.</summary>
        public event EventHandler<ThumbnailBuiltEventArgs> ThumbnailBuilt;

        internal void NotifyThumbnailBuilt(AssetItem assetItem, ThumbnailBuildResult result, bool changed, Stream thumbnailStream, ObjectId thumbnailHash)
        {
            var handler = ThumbnailBuilt;
            if (handler != null)
            {
                // create the thumbnail build event arguments
                var thumbnailBuiltArgs = new ThumbnailBuiltEventArgs
                {
                    AssetId = assetItem.Id,
                    Url = assetItem.Location,
                    Result = result,
                    ThumbnailChanged = changed
                };

                // Open the image data stream if the build succeeded
                if (thumbnailBuiltArgs.Result == ThumbnailBuildResult.Succeeded)
                {
                    thumbnailBuiltArgs.ThumbnailStream = thumbnailStream;
                    thumbnailBuiltArgs.ThumbnailId = thumbnailHash;
                }
                else if (BuildFailedThumbnail != null)
                {
                    thumbnailBuiltArgs.ThumbnailStream = new MemoryStream(BuildFailedThumbnail.Result);
                }
                handler(assetItem, thumbnailBuiltArgs);
            }
        }

        private static byte[] HandleBrokenThumbnail()
        {
            // Load broken asset thumbnail
            var assetBrokenThumbnail = Image.Load(DefaultThumbnails.AssetBrokenThumbnail);

            // Apply thumbnail status in corner
            ThumbnailBuildHelper.ApplyThumbnailStatus(assetBrokenThumbnail, LogMessageType.Error);
            var memoryStream = new MemoryStream();
            assetBrokenThumbnail.Save(memoryStream, ImageFileType.Png);

            return memoryStream.ToArray();
        }
    }
}
