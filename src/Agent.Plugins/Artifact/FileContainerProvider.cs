// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using Agent.Sdk.Blob;
using Agent.Plugins.PipelineArtifact.Telemetry;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Agent.Blob;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.BlobStore.Common;
using Microsoft.VisualStudio.Services.Content.Common;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;
using Microsoft.VisualStudio.Services.FileContainer;
using Microsoft.VisualStudio.Services.FileContainer.Client;
using Microsoft.VisualStudio.Services.WebApi;
using Minimatch;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Agent.Plugins
{
    internal class FileContainerProvider : IArtifactProvider
    {
        private readonly VssConnection connection;
        private readonly FileContainerHttpClient containerClient;
        private readonly IAppTraceSource tracer;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA2000:Dispose objects before losing scope", MessageId = "connection2")]
        public FileContainerProvider(VssConnection connection, IAppTraceSource tracer)
        {
            BuildHttpClient buildHttpClient = connection.GetClient<BuildHttpClient>();
            var connection2 = new VssConnection(buildHttpClient.BaseAddress, connection.Credentials);
            containerClient = connection2.GetClient<FileContainerHttpClient>();
            this.tracer = tracer;
            this.connection = connection;
        }

        public async Task DownloadSingleArtifactAsync(ArtifactDownloadParameters downloadParameters, BuildArtifact buildArtifact, CancellationToken cancellationToken, AgentTaskPluginExecutionContext context)
        {
            await this.DownloadFileContainerAsync(downloadParameters, buildArtifact, downloadParameters.TargetDirectory, context, cancellationToken);
        }

        public async Task DownloadMultipleArtifactsAsync(ArtifactDownloadParameters downloadParameters, IEnumerable<BuildArtifact> buildArtifacts, CancellationToken cancellationToken, AgentTaskPluginExecutionContext context)
        {
            foreach (var buildArtifact in buildArtifacts)
            {
                var dirPath = Path.Combine(downloadParameters.TargetDirectory, buildArtifact.Name);
                await DownloadFileContainerAsync(downloadParameters, buildArtifact, dirPath, context, cancellationToken, isSingleArtifactDownload: false);
            }
        }

        private (long, string) ParseContainerId(string resourceData)
        {
            // Example of resourceData: "#/7029766/artifacttool-alpine-x64-Debug"
            string[] segments = resourceData.Split('/');
            long containerId;

            if (segments.Length < 3)
            {
                throw new ArgumentException($"Resource data value '{resourceData}' is invalid.");
            }

            if (segments.Length >= 3 && segments[0] == "#" && long.TryParse(segments[1], out containerId))
            {
                var artifactName = String.Join('/', segments, 2, segments.Length - 2);
                return (
                        containerId,
                        artifactName
                        );
            }
            else
            {
                var message = $"Resource data value '{resourceData}' is invalid.";
                throw new ArgumentException(message, nameof(resourceData));
            }
        }

        private async Task DownloadFileContainerAsync(ArtifactDownloadParameters downloadParameters, BuildArtifact artifact, string rootPath, AgentTaskPluginExecutionContext context, CancellationToken cancellationToken, bool isSingleArtifactDownload = true)
        {
            var containerIdAndRoot = ParseContainerId(artifact.Resource.Data);
            var projectId = downloadParameters.ProjectId;
            var minimatchPatterns = downloadParameters.MinimatchFilters;

            var items = await containerClient.QueryContainerItemsAsync(containerIdAndRoot.Item1, projectId, isShallow: false, includeBlobMetadata: true, containerIdAndRoot.Item2);

            tracer.Info($"Start downloading FCS artifact- {artifact.Name}");
            IEnumerable<Func<string, bool>> minimatcherFuncs = MinimatchHelper.GetMinimatchFuncs(minimatchPatterns, tracer);

            if (minimatcherFuncs != null && minimatcherFuncs.Count() != 0)
            {
                items = this.GetFilteredItems(items, minimatcherFuncs);
            }

            if (!isSingleArtifactDownload && items.Any())
            {
                Directory.CreateDirectory(rootPath);
            }

            var folderItems = items.Where(i => i.ItemType == ContainerItemType.Folder);
            Parallel.ForEach(folderItems, (folder) =>
            {
                var targetPath = ResolveTargetPath(rootPath, folder, containerIdAndRoot.Item2, downloadParameters.IncludeArtifactNameInPath);
                Directory.CreateDirectory(targetPath);
            });

            var fileItems = items.Where(i => i.ItemType == ContainerItemType.File);

            var downloadBlock = NonSwallowingActionBlock.Create<FileContainerItem>(
                async item =>
                {
                    var targetPath = ResolveTargetPath(rootPath, item, containerIdAndRoot.Item2, downloadParameters.IncludeArtifactNameInPath);
                    var directory = Path.GetDirectoryName(targetPath);
                    Directory.CreateDirectory(directory);
                    await AsyncHttpRetryHelper.InvokeVoidAsync(
                        async () =>
                        {
                            tracer.Info($"Downloading: {targetPath}");
                            if (item.BlobMetadata != null)
                            {
                                await this.DownloadFileFromBlobAsync(context, containerIdAndRoot, targetPath, projectId, item, cancellationToken);
                            }
                            else
                            {
                                using (var sourceStream = await this.DownloadFileAsync(containerIdAndRoot, projectId, containerClient, item, cancellationToken))
                                using (var targetStream = new FileStream(targetPath, FileMode.Create))
                                {
                                    await sourceStream.CopyToAsync(targetStream);
                                }
                            }
                        },
                        maxRetries: downloadParameters.RetryDownloadCount,
                        cancellationToken: cancellationToken,
                        tracer: tracer,
                        continueOnCapturedContext: false,
                        canRetryDelegate: exception => exception is IOException,
                        context: null
                        );
                },
                new ExecutionDataflowBlockOptions()
                {
                    BoundedCapacity = 5000,
                    MaxDegreeOfParallelism = downloadParameters.ParallelizationLimit,
                    CancellationToken = cancellationToken,
                });

            await downloadBlock.SendAllAndCompleteSingleBlockNetworkAsync(fileItems, cancellationToken);

            // check files (will throw an exception if a file is corrupt)
            if (downloadParameters.CheckDownloadedFiles)
            {
                CheckDownloads(items, rootPath, containerIdAndRoot.Item2, downloadParameters.IncludeArtifactNameInPath);
            }
        }

        private void CheckDownloads(IEnumerable<FileContainerItem> items, string rootPath, string artifactName, bool includeArtifactName)
        {
            tracer.Info(StringUtil.Loc("BeginArtifactItemsIntegrityCheck"));
            var corruptedItems = new List<FileContainerItem>();
            foreach (var item in items.Where(x => x.ItemType == ContainerItemType.File))
            {
                var targetPath = ResolveTargetPath(rootPath, item, artifactName, includeArtifactName);
                var fileInfo = new FileInfo(targetPath);
                if (fileInfo.Length != item.FileLength)
                {
                    corruptedItems.Add(item);
                }
            }

            if (corruptedItems.Count > 0)
            {
                tracer.Warn(StringUtil.Loc("CorruptedArtifactItemsList"));
                corruptedItems.ForEach(item => tracer.Warn(item.ItemLocation));

                throw new Exception(StringUtil.Loc("IntegrityCheckNotPassed"));
            }
            tracer.Info(StringUtil.Loc("IntegrityCheckPassed"));
        }

        private async Task<Stream> DownloadFileAsync(
            (long, string) containerIdAndRoot,
            Guid scopeIdentifier,
            FileContainerHttpClient containerClient,
            FileContainerItem item,
            CancellationToken cancellationToken)
        {
            Stream responseStream = await AsyncHttpRetryHelper.InvokeAsync(
                async () =>
                {
                    Stream internalResponseStream = await containerClient.DownloadFileAsync(containerIdAndRoot.Item1, item.Path, cancellationToken, scopeIdentifier);
                    return internalResponseStream;
                },
                maxRetries: 5,
                cancellationToken: cancellationToken,
                tracer: this.tracer,
                continueOnCapturedContext: false
                );

            return responseStream;
        }

        private async Task DownloadFileFromBlobAsync(
            AgentTaskPluginExecutionContext context,
            (long, string) containerIdAndRoot,
            string destinationPath,
            Guid scopeIdentifier,
            FileContainerItem item,
            CancellationToken cancellationToken)
        {
            var (dedupClient, clientTelemetry) = await DedupManifestArtifactClientFactory.Instance.CreateDedupClientAsync(
                false, (str) => this.tracer.Info(str), this.connection, cancellationToken);

            using (clientTelemetry)
            {
                var dedupIdentifier = DedupIdentifier.Deserialize(item.BlobMetadata.ArtifactHash);
                PipelineArtifactActionRecord downloadRecord = clientTelemetry.CreateRecord<PipelineArtifactActionRecord>((level, uri, type) =>
                    new PipelineArtifactActionRecord(level, uri, type, nameof(DownloadMultipleArtifactsAsync), context));
                await clientTelemetry.MeasureActionAsync(
                    record: downloadRecord,
                    actionAsync: async () =>
                    {
                        await AsyncHttpRetryHelper.InvokeVoidAsync(
                            async () =>
                            {
                                using (var targetFileStream = new FileStream(destinationPath, FileMode.Create))
                                {
                                    if (item.BlobMetadata.CompressionType == BlobCompressionType.GZip)
                                    {
                                        using (GZipStream uncompressStream = new GZipStream(targetFileStream, CompressionMode.Decompress))
                                        {
                                            await dedupClient.DownloadToStreamAsync(dedupIdentifier, uncompressStream, null, EdgeCache.Allowed, (size) => {}, (size) => {}, cancellationToken);
                                        }
                                    }
                                    else
                                    {
                                        await dedupClient.DownloadToStreamAsync(dedupIdentifier, targetFileStream, null, EdgeCache.Allowed, (size) => {}, (size) => {}, cancellationToken);
                                    }
                                }
                            },
                            maxRetries: 3,
                            tracer: tracer,
                            canRetryDelegate: e => true,
                            context: nameof(DownloadSingleArtifactAsync),
                            cancellationToken: cancellationToken,
                            continueOnCapturedContext: false);
                    });
                // Send results to CustomerIntelligence
                context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineArtifact, record: downloadRecord);
            }
        }

        private string ResolveTargetPath(string rootPath, FileContainerItem item, string artifactName, bool includeArtifactName)
        {
            if (includeArtifactName)
            {
                return Path.Combine(rootPath, item.Path);
            }
            //Example of item.Path&artifactName: item.Path = "drop3", "drop3/HelloWorld.exe"; artifactName = "drop3"
            string tempArtifactName;
            if (item.Path.Length == artifactName.Length)
            {
                tempArtifactName = artifactName;
            }
            else if (item.Path.Length > artifactName.Length)
            {
                tempArtifactName = artifactName + "/";
            }
            else
            {
                throw new ArgumentException($"Item path {item.Path} cannot be smaller than artifact {artifactName}");
            }

            var itemPathWithoutDirectoryPrefix = item.Path.Replace(tempArtifactName, String.Empty);
            var absolutePath = Path.Combine(rootPath, itemPathWithoutDirectoryPrefix);
            return absolutePath;
        }

        private List<FileContainerItem> GetFilteredItems(List<FileContainerItem> items, IEnumerable<Func<string, bool>> minimatchFuncs)
        {
            List<FileContainerItem> filteredItems = new List<FileContainerItem>();
            foreach (FileContainerItem item in items)
            {
                if (minimatchFuncs.Any(match => match(item.Path)))
                {
                    filteredItems.Add(item);
                }
            }
            var excludedItems = items.Except(filteredItems);
            foreach (FileContainerItem item in excludedItems)
            {
                tracer.Info($"Item excluded: {item.Path}");
            }
            return filteredItems;
        }
    }
}
