// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.Assets.Editor.Components.Status;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.BuildEngine;
using Stride.Core.Collections;
using Stride.Core.Diagnostics;
using Stride.Assets;
using Stride.Editor.Build;
using Stride.Editor.Resources;
using Stride.Graphics;
using Stride.Shaders.Compiler;

namespace Stride.Editor.Thumbnails
{ 
    public class GameStudioThumbnailService : IThumbnailService
    {
        private readonly object hashLock = new object();
        private readonly Dictionary<AssetItem, PriorityQueueNode<AssetBuildUnit>> thumbnailQueueHash = new Dictionary<AssetItem, PriorityQueueNode<AssetBuildUnit>>();

        // Note: KVP.Value is usually null, and is only set if a new request for the same thumbnail has been done and we need to start it when the current one finish (avoid running it twice at the same time)
        private readonly Dictionary<AssetId, ThumbnailContinuation> thumbnailsInProgressAndContinuation = new Dictionary<AssetId, ThumbnailContinuation>();
        private readonly SessionViewModel session;
        private readonly GameStudioBuilderService assetBuilderService;
        private readonly ThumbnailListCompiler thumbnailCompiler;
        private readonly AssetCompilerRegistry compilerRegistry;
        private readonly List<ThumbnailPriorityItem> assetsToIncreasePriority = new List<ThumbnailPriorityItem>();
        private readonly ThumbnailGenerator generator;
        private bool thumbnailThreadShouldTerminate;
        private RenderingMode renderingMode;
        private ColorSpace colorSpace;
        private GraphicsProfile graphicsProfile;
        private int firstPriority, lastPriority;

        private int currentJobToken = -1;

        private GameSettingsAsset currentGameSettings;

        private readonly GameSettingsProviderService gameSettingsProviderService;

        public GameStudioThumbnailService(SessionViewModel session, GameSettingsProviderService settingsProvider, GameStudioBuilderService assetBuilderService)
        {
            this.session = session;
            this.assetBuilderService = assetBuilderService;

            generator = new ThumbnailGenerator((EffectCompilerBase)assetBuilderService.EffectCompiler);
            compilerRegistry = new AssetCompilerRegistry { DefaultCompiler = new CustomAssetThumbnailCompiler() };
            thumbnailCompiler = new ThumbnailListCompiler(generator, ThumbnailBuilt, compilerRegistry);

            gameSettingsProviderService = settingsProvider;
            gameSettingsProviderService.GameSettingsChanged += GameSettingsChanged;
            UpdateGameSettings(settingsProvider.CurrentGameSettings);
            StartPushNotificationsTask();
        }

        public void Dispose()
        {
            // Terminate thumbnail control thread
            lock (hashLock)
            {
                foreach (var item in thumbnailQueueHash)
                {
                    assetBuilderService.RemoveBuildUnit(item.Value);
                }
                thumbnailQueueHash.Clear();
            }
            thumbnailThreadShouldTerminate = true;
            generator.Dispose();
            gameSettingsProviderService.GameSettingsChanged -= GameSettingsChanged;
        }

        private void GameSettingsChanged(object sender, GameSettingsChangedEventArgs e)
        {
            UpdateGameSettings(e.GameSettings);
        }

        private void UpdateGameSettings(GameSettingsAsset gameSettings)
        {
            currentGameSettings = AssetCloner.Clone(gameSettings, AssetClonerFlags.RemoveUnloadableObjects);

            bool shouldRefreshAllThumbnails = false;
            if (renderingMode != gameSettings.GetOrCreate<EditorSettings>().RenderingMode)
            {
                renderingMode = gameSettings.GetOrCreate<EditorSettings>().RenderingMode;
                shouldRefreshAllThumbnails = true;
            }
            if (colorSpace != gameSettings.GetOrCreate<RenderingSettings>().ColorSpace)
            {
                colorSpace = gameSettings.GetOrCreate<RenderingSettings>().ColorSpace;
                shouldRefreshAllThumbnails = true;
            }
            if (graphicsProfile != gameSettings.GetOrCreate<RenderingSettings>().DefaultGraphicsProfile)
            {
                graphicsProfile = gameSettings.GetOrCreate<RenderingSettings>().DefaultGraphicsProfile;
                shouldRefreshAllThumbnails = true;
            }
            if (shouldRefreshAllThumbnails)
            {
                var allAssets = session.AllAssets.Select(x => x.AssetItem).ToList();
                Task.Run(() => AddThumbnailAssetItems(allAssets, QueuePosition.First));
            }
        }

        public static byte[] HandleBrokenThumbnail()
        {
            // Load broken asset thumbnail
            var assetBrokenThumbnail = Image.Load(DefaultThumbnails.AssetBrokenThumbnail);

            // Apply thumbnail status in corner
            ThumbnailBuildHelper.ApplyThumbnailStatus(assetBrokenThumbnail, LogMessageType.Error);
            var memoryStream = new MemoryStream();
            assetBrokenThumbnail.Save(memoryStream, ImageFileType.Png);

            return memoryStream.ToArray();
        }

        public event EventHandler<ThumbnailCompletedArgs> ThumbnailCompleted;

        /// <inheritdoc/>
        public bool HasStaticThumbnail(Type assetType)
        {
            var compiler = (IThumbnailCompiler)compilerRegistry.GetCompiler(assetType, typeof(ThumbnailCompilationContext));
            return compiler?.IsStatic ?? true;
        }

        public ListBuildStep Compile(AssetItem asset, GameSettingsAsset gameSettings)
        {
            // Mark thumbnail as being compiled
            lock (hashLock)
            {
                thumbnailQueueHash.Remove(asset);
                if (thumbnailsInProgressAndContinuation.ContainsKey(asset.Id))
                {
                    // Race condition: the builder dequeued this unit before Compile() was called, and
                    // AddThumbnailAssetItems() created a replacement unit in that window (between the
                    // dequeue under queueLock and this Compile() call under hashLock). Running both
                    // concurrently would corrupt the continuation chain. Demote to a continuation so
                    // the newer request is honoured once the current in-progress build finishes.
                    if (thumbnailsInProgressAndContinuation[asset.Id] == null)
                        thumbnailsInProgressAndContinuation[asset.Id] = new ThumbnailContinuation(asset, QueuePosition.First);
                    return null;
                }
                thumbnailsInProgressAndContinuation[asset.Id] = null;
            }

            try
            {
                return thumbnailCompiler.Compile(asset, gameSettings, HasStaticThumbnail(asset.Asset.GetType()));
            }
            catch
            {
                // If compilation setup fails unexpectedly, remove the in-progress marker so the asset
                // is not permanently stuck (ThumbnailBuilt will never fire in this path).
                lock (hashLock)
                    thumbnailsInProgressAndContinuation.Remove(asset.Id);
                throw;
            }
        }

        public void AddThumbnailAssetItems(IEnumerable<AssetItem> assetItems, QueuePosition position)
        {
            if (!thumbnailThreadShouldTerminate)
            {
                assetBuilderService.WaitForShaders();

                lock (hashLock)
                {
                    foreach (var asset in assetItems)
                    {
                        if (thumbnailsInProgressAndContinuation.ContainsKey(asset.Id))
                        {
                            // Thumbnail is already being generated, set this one as continuation
                            thumbnailsInProgressAndContinuation[asset.Id] = new ThumbnailContinuation(asset, position);
                        }
                        else if (position == QueuePosition.First)
                        {
                            PriorityQueueNode<AssetBuildUnit> node;
                            if (thumbnailQueueHash.TryGetValue(asset, out node))
                            {
                                assetBuilderService.RemoveBuildUnit(node);
                                thumbnailQueueHash.Remove(asset);
                            }

                            node = assetBuilderService.PushBuildUnit(new ThumbnailAssetBuildUnit(asset, currentGameSettings, this, firstPriority--));
                            thumbnailQueueHash.Add(asset, node);
                        }
                        else
                        {
                            if (!thumbnailQueueHash.ContainsKey(asset))
                            {
                                var node = assetBuilderService.PushBuildUnit(new ThumbnailAssetBuildUnit(asset, currentGameSettings, this, lastPriority++));
                                thumbnailQueueHash.Add(asset, node);
                            }
                        }
                    }
                }
            }
        }

        public void IncreaseThumbnailPriority(IEnumerable<AssetItem> assetItems)
        {
            if (!thumbnailThreadShouldTerminate)
            {
                lock (hashLock)
                {
                    // Batch assets whose priority needs to be updated
                    foreach (var assetItem in assetItems)
                    {
                        PriorityQueueNode<AssetBuildUnit> node;
                        if (thumbnailQueueHash.TryGetValue(assetItem, out node))
                        {
                            var compiler = (IThumbnailCompiler)compilerRegistry.GetCompiler(assetItem.Asset.GetType(), typeof(ThumbnailCompilationContext));
                            var priority = compiler.Priority;
                            assetsToIncreasePriority.Add(new ThumbnailPriorityItem(assetItem, node, priority));
                        }
                    }

                    // Sort by thumbnail priority
                    assetsToIncreasePriority.Sort(ThumbnailPriorityComparer.Default);

                    // Readd at beginning of the queue (reverse so that firstPriority-- matches assetsToIncreasePriority order
                    for (int index = assetsToIncreasePriority.Count - 1; index >= 0; index--)
                    {
                        var thumbnailPriorityItem = assetsToIncreasePriority[index];
                        var node = thumbnailPriorityItem.Node;
                        var asset = thumbnailPriorityItem.Asset;

                        assetBuilderService.RemoveBuildUnit(node);
                        thumbnailQueueHash.Remove(asset);
                        node = assetBuilderService.PushBuildUnit(new ThumbnailAssetBuildUnit(asset, currentGameSettings, this, firstPriority--));
                        thumbnailQueueHash.Add(asset, node);
                    }

                    assetsToIncreasePriority.Clear();
                }
            }
        }

        private void ThumbnailBuilt(object sender, ThumbnailBuiltEventArgs e)
        {
            ThumbnailData thumbnailData = null;
            if (e.ThumbnailStream != null)
            {
                var stream = new MemoryStream();
                e.ThumbnailStream.CopyTo(stream);
                thumbnailData = new BitmapThumbnailData(e.ThumbnailId, stream);
            }

            if (e.Result != ThumbnailBuildResult.Cancelled)
            {
                ThumbnailCompleted?.Invoke(this, new ThumbnailCompletedArgs(e.AssetId, thumbnailData));
            }

            lock (hashLock)
            {
                ThumbnailContinuation thumbnailContinuation;
                thumbnailsInProgressAndContinuation.TryGetValue(e.AssetId, out thumbnailContinuation);
                thumbnailsInProgressAndContinuation.Remove(e.AssetId);

                // Check if same asset has been requested again while it was compiling
                if (thumbnailContinuation != null)
                {
                    var priority = thumbnailContinuation.Position == QueuePosition.First ? firstPriority-- : lastPriority++;
                    var node = assetBuilderService.PushBuildUnit(new ThumbnailAssetBuildUnit(thumbnailContinuation.UpdatedAssetToRecompile, currentGameSettings, this, priority));
                    thumbnailQueueHash.Add(thumbnailContinuation.UpdatedAssetToRecompile, node);
                }
            }
        }

        private void StartPushNotificationsTask()
        {
            Task.Run(async () =>
            {
                while (!thumbnailThreadShouldTerminate)
                {
                    await Task.Delay(500);
                    int queueCount;
                    lock (hashLock)
                    {
                        queueCount = thumbnailQueueHash.Count;
                    }
                    if (currentJobToken >= 0)
                    {
                        if (queueCount > 0)
                        {
                            EditorViewModel.Instance.Status.NotifyBackgroundJobProgress(currentJobToken, queueCount, true);
                        }
                        else
                        {
                            EditorViewModel.Instance.Status.NotifyBackgroundJobFinished(currentJobToken);
                            currentJobToken = -1;
                        }
                    }
                    else if (queueCount > 0)
                    {
                        currentJobToken = EditorViewModel.Instance.Status.NotifyBackgroundJobStarted("Building thumbnails… ({0} in queue)", JobPriority.Background);
                    }
                }
            });
        }

        struct ThumbnailPriorityItem
        {
            public readonly AssetItem Asset;
            public readonly PriorityQueueNode<AssetBuildUnit> Node;
            public readonly int Priority;

            public ThumbnailPriorityItem(AssetItem asset, PriorityQueueNode<AssetBuildUnit> node, int priority) : this()
            {
                Asset = asset;
                Node = node;
                Priority = priority;
            }
        }

        class ThumbnailPriorityComparer : Comparer<ThumbnailPriorityItem>
        {
            public new static readonly ThumbnailPriorityComparer Default = new ThumbnailPriorityComparer();

            public override int Compare(ThumbnailPriorityItem x, ThumbnailPriorityItem y)
            {
                return x.Priority - y.Priority;
            }
        }

        class ThumbnailContinuation
        {
            public readonly AssetItem UpdatedAssetToRecompile;
            public readonly QueuePosition Position;

            public ThumbnailContinuation(AssetItem updatedAssetToRecompile, QueuePosition position)
            {
                UpdatedAssetToRecompile = updatedAssetToRecompile;
                Position = position;
            }
        }
    }
}
