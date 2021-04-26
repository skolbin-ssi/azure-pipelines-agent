﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Sdk;
using Agent.Plugins;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;

namespace Agent.Plugins.BuildArtifacts
{
    public abstract class BuildArtifactTaskPluginBaseV1 : IAgentTaskPlugin
    {
        public abstract Guid Id { get; }
        protected IAppTraceSource tracer;
        public string Stage => "main";

        public Task RunAsync(AgentTaskPluginExecutionContext context, CancellationToken token)
        {
            this.tracer = context.CreateArtifactsTracer();

            return this.ProcessCommandInternalAsync(context, token);
        }

        protected abstract Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context,
            CancellationToken token);

        // Properties set by tasks
        protected static class TaskProperties
        {
            public static readonly string BuildType = "buildType";
            public static readonly string Project = "project";
            public static readonly string Definition = "definition";
            public static readonly string SpecificBuildWithTriggering = "specificBuildWithTriggering";
            public static readonly string BuildVersionToDownload = "buildVersionToDownload";
            public static readonly string BranchName = "branchName";
            public static readonly string Tags = "tags";
            public static readonly string AllowPartiallySucceededBuilds = "allowPartiallySucceededBuilds";
            public static readonly string AllowFailedBuilds = "allowFailedBuilds";
            public static readonly string AllowCanceledBuilds = "allowCanceledBuilds";
            public static readonly string ArtifactName = "artifactName";
            public static readonly string ItemPattern = "itemPattern";
            public static readonly string DownloadType = "downloadType";
            public static readonly string DownloadPath = "downloadPath";
            public static readonly string BuildId = "buildId";
            public static readonly string RetryDownloadCount = "retryDownloadCount";
            public static readonly string ParallelizationLimit = "parallelizationLimit";
            public static readonly string CheckDownloadedFiles = "checkDownloadedFiles";
        }
    }

    // Can be invoked from a build run or a release run should a build be set as the artifact. 
    public class DownloadBuildArtifactTaskV1_0_0 : BuildArtifactTaskPluginBaseV1
    {
        // Same as https://github.com/Microsoft/vsts-tasks/blob/master/Tasks/DownloadBuildArtifactV1/task.json
        public override Guid Id => BuildArtifactPluginConstants.DownloadBuildArtifactTaskId;
        static readonly string buildTypeCurrent = "current";
        static readonly string buildTypeSpecific = "specific";
        static readonly string buildVersionToDownloadLatest = "latest";
        static readonly string buildVersionToDownloadSpecific = "specific";
        static readonly string buildVersionToDownloadLatestFromBranch = "latestFromBranch";

        protected override async Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context,
            CancellationToken token)
        {
            ArgUtil.NotNull(context, nameof(context));
            string artifactName = context.GetInput(TaskProperties.ArtifactName, required: false);
            string branchName = context.GetInput(TaskProperties.BranchName, required: false);
            string definition = context.GetInput(TaskProperties.Definition, required: false);
            string buildType = context.GetInput(TaskProperties.BuildType, required: true);
            string specificBuildWithTriggering = context.GetInput(TaskProperties.SpecificBuildWithTriggering, required: false);
            string buildVersionToDownload = context.GetInput(TaskProperties.BuildVersionToDownload, required: false);
            string targetPath = context.GetInput(TaskProperties.DownloadPath, required: true);
            string environmentBuildId = context.Variables.GetValueOrDefault(BuildVariables.BuildId)?.Value ?? string.Empty; // BuildID provided by environment.
            string itemPattern = context.GetInput(TaskProperties.ItemPattern, required: false);
            string projectName = context.GetInput(TaskProperties.Project, required: false);
            string tags = context.GetInput(TaskProperties.Tags, required: false);
            string allowPartiallySucceededBuilds = context.GetInput(TaskProperties.AllowPartiallySucceededBuilds, required: false);
            string allowFailedBuilds = context.GetInput(TaskProperties.AllowFailedBuilds, required: false);
            string allowCanceledBuilds = context.GetInput(TaskProperties.AllowCanceledBuilds, required: false);
            string userSpecifiedBuildId = context.GetInput(TaskProperties.BuildId, required: false);
            string defaultWorkingDirectory = context.Variables.GetValueOrDefault("system.defaultworkingdirectory").Value;
            string downloadType = context.GetInput(TaskProperties.DownloadType, required: true);
            // advanced
            string retryDownloadCount = context.GetInput(TaskProperties.RetryDownloadCount, required: false);
            string parallelizationLimit = context.GetInput(TaskProperties.ParallelizationLimit, required: false);
            string checkDownloadedFiles = context.GetInput(TaskProperties.CheckDownloadedFiles, required: false);

            targetPath = Path.IsPathFullyQualified(targetPath) ? targetPath : Path.GetFullPath(Path.Combine(defaultWorkingDirectory, targetPath));

            string[] minimatchPatterns = itemPattern.Split(
                new[] { "\n" },
                StringSplitOptions.RemoveEmptyEntries
            );

            string[] tagsInput = tags.Split(
                new[] { "," },
                StringSplitOptions.None
            );

            if (!bool.TryParse(allowPartiallySucceededBuilds, out var allowPartiallySucceededBuildsBool))
            {
                allowPartiallySucceededBuildsBool = false;
            }
            if (!bool.TryParse(allowFailedBuilds, out var allowFailedBuildsBool))
            {
                allowFailedBuildsBool = false;
            }
            if (!bool.TryParse(allowCanceledBuilds, out var allowCanceledBuildsBool))
            {
                allowCanceledBuildsBool = false;
            }
            var resultFilter = GetResultFilter(allowPartiallySucceededBuildsBool, allowFailedBuildsBool, allowCanceledBuildsBool);

            PipelineArtifactServer server = new PipelineArtifactServer(tracer);
            ArtifactDownloadParameters downloadParameters;

            if (buildType == buildTypeCurrent)
            {
                // TODO: use a constant for project id, which is currently defined in Microsoft.VisualStudio.Services.Agent.Constants.Variables.System.TeamProjectId (Ting)
                string projectIdStr = context.Variables.GetValueOrDefault("system.teamProjectId")?.Value;
                if (String.IsNullOrEmpty(projectIdStr))
                {
                    throw new ArgumentNullException(StringUtil.Loc("CannotBeNullOrEmpty"), "Project ID");
                }
                
                Guid projectId = Guid.Parse(projectIdStr);
                ArgUtil.NotEmpty(projectId, nameof(projectId));

                int pipelineId = 0;
                if (int.TryParse(environmentBuildId, out pipelineId) && pipelineId != 0)
                {
                    OutputBuildInfo(context, pipelineId);
                }
                else
                {
                    string hostType = context.Variables.GetValueOrDefault("system.hosttype")?.Value;
                    if (string.Equals(hostType, "Release", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(hostType, "DeploymentGroup", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(StringUtil.Loc("BuildIdIsNotAvailable", hostType ?? string.Empty, hostType ?? string.Empty));
                    }
                    else if (!string.Equals(hostType, "Build", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(StringUtil.Loc("CannotDownloadFromCurrentEnvironment", hostType ?? string.Empty));
                    }
                    else
                    {
                        // This should not happen since the build id comes from build environment. But a user may override that so we must be careful.
                        throw new ArgumentException(StringUtil.Loc("BuildIdIsNotValid", environmentBuildId));
                    }
                }

                downloadParameters = new ArtifactDownloadParameters
                {
                    ProjectRetrievalOptions = BuildArtifactRetrievalOptions.RetrieveByProjectId,
                    ProjectId = projectId,
                    PipelineId = pipelineId,
                    ArtifactName = artifactName,
                    TargetDirectory = targetPath,
                    MinimatchFilters = minimatchPatterns,
                    MinimatchFilterWithArtifactName = true,
                    ParallelizationLimit = int.TryParse(parallelizationLimit, out var parallelLimit) ? parallelLimit : 8,
                    RetryDownloadCount = int.TryParse(retryDownloadCount, out var retryCount) ? retryCount : 4,
                    CheckDownloadedFiles = bool.TryParse(checkDownloadedFiles, out var checkDownloads) && checkDownloads
                };
            }
            else if (buildType == buildTypeSpecific)
            {
                if (String.IsNullOrEmpty(projectName))
                {
                    throw new ArgumentNullException(StringUtil.Loc("CannotBeNullOrEmpty"), "Project Name");
                }
                Guid projectId; 
                bool isProjGuid = Guid.TryParse(projectName, out projectId);
                if (!isProjGuid) 
                {
                    projectId = await GetProjectIdAsync(context, projectName);
                }
                // Set the default pipelineId to 0, which is an invalid build id and it has to be reassigned to a valid build id.
                int pipelineId = 0;

                if (bool.TryParse(specificBuildWithTriggering, out var specificBuildWithTriggeringBool) && specificBuildWithTriggeringBool)
                {
                    string hostType = context.Variables.GetValueOrDefault("system.hostType")?.Value;
                    string triggeringPipeline = null;
                    if (!string.IsNullOrWhiteSpace(hostType) && !hostType.Equals("build", StringComparison.OrdinalIgnoreCase)) // RM env.
                    {
                        var releaseAlias = context.Variables.GetValueOrDefault("release.triggeringartifact.alias")?.Value;
                        var definitionIdTriggered = context.Variables.GetValueOrDefault("release.artifacts." + releaseAlias ?? string.Empty + ".definitionId")?.Value;
                        if (!string.IsNullOrWhiteSpace(definitionIdTriggered) && definitionIdTriggered.Equals(definition, StringComparison.OrdinalIgnoreCase))
                        {
                            triggeringPipeline = context.Variables.GetValueOrDefault("release.artifacts." + releaseAlias ?? string.Empty + ".buildId")?.Value;
                        }
                        var triggeredProjectIdStr = context.Variables.GetValueOrDefault("release.artifacts." + releaseAlias + ".projectId")?.Value;
                        if (!string.IsNullOrWhiteSpace(triggeredProjectIdStr) && Guid.TryParse(triggeredProjectIdStr, out var triggeredProjectId))
                        {
                            projectId = triggeredProjectId;
                        }
                    }
                    else
                    {
                        var definitionIdTriggered = context.Variables.GetValueOrDefault("build.triggeredBy.definitionId")?.Value;
                        if (!string.IsNullOrWhiteSpace(definitionIdTriggered) && definitionIdTriggered.Equals(definition, StringComparison.OrdinalIgnoreCase))
                        {
                            triggeringPipeline = context.Variables.GetValueOrDefault("build.triggeredBy.buildId")?.Value;
                        }
                        var triggeredProjectIdStr = context.Variables.GetValueOrDefault("build.triggeredBy.projectId")?.Value;
                        if (!string.IsNullOrWhiteSpace(triggeredProjectIdStr) && Guid.TryParse(triggeredProjectIdStr, out var triggeredProjectId))
                        {
                            projectId = triggeredProjectId;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(triggeringPipeline))
                    {
                        pipelineId = int.Parse(triggeringPipeline);
                    }
                }

                if (pipelineId == 0)
                {
                    if (buildVersionToDownload == buildVersionToDownloadLatest)
                    {
                        pipelineId = await this.GetPipelineIdAsync(context, definition, buildVersionToDownload, projectId.ToString(), tagsInput, resultFilter, null, cancellationToken: token);
                    }
                    else if (buildVersionToDownload == buildVersionToDownloadSpecific)
                    {
                        bool isPipelineIdNum = Int32.TryParse(userSpecifiedBuildId, out pipelineId);
                        if (!isPipelineIdNum)
                        {
                            throw new ArgumentException(StringUtil.Loc("RunIDNotValid", userSpecifiedBuildId));
                        }
                    }
                    else if (buildVersionToDownload == buildVersionToDownloadLatestFromBranch)
                    {
                        pipelineId = await this.GetPipelineIdAsync(context, definition, buildVersionToDownload, projectId.ToString(), tagsInput, resultFilter, branchName, cancellationToken: token);
                    }
                    else
                    {
                        throw new InvalidOperationException("Unreachable code!");
                    }
                }

                OutputBuildInfo(context, pipelineId);

                downloadParameters = new ArtifactDownloadParameters
                {
                    ProjectRetrievalOptions = BuildArtifactRetrievalOptions.RetrieveByProjectName,
                    ProjectName = projectName,
                    ProjectId = projectId,
                    PipelineId = pipelineId,
                    ArtifactName = artifactName,
                    TargetDirectory = targetPath,
                    MinimatchFilters = minimatchPatterns,
                    MinimatchFilterWithArtifactName = true,
                    ParallelizationLimit = int.TryParse(parallelizationLimit, out var parallelLimit) ? parallelLimit : 8,
                    RetryDownloadCount = int.TryParse(retryDownloadCount, out var retryCount) ? retryCount : 4,
                    CheckDownloadedFiles = bool.TryParse(checkDownloadedFiles, out var checkDownloads) && checkDownloads
                };
            }
            else
            {
                throw new InvalidOperationException($"Build type '{buildType}' is not recognized.");
            }

            string fullPath = this.CreateDirectoryIfDoesntExist(targetPath);

            var downloadOption = downloadType == "single" ? DownloadOptions.SingleDownload : DownloadOptions.MultiDownload;

            // Build artifacts always includes the artifact in the path name
            downloadParameters.IncludeArtifactNameInPath = true;

            context.Output(StringUtil.Loc("DownloadArtifactTo", targetPath));
            await server.DownloadAsyncV2(context, downloadParameters, downloadOption, token);
            context.Output(StringUtil.Loc("DownloadArtifactFinished"));
        }

        private string CreateDirectoryIfDoesntExist(string targetPath)
        {
            string fullPath = Path.GetFullPath(targetPath);
            bool dirExists = Directory.Exists(fullPath);
            if (!dirExists)
            {
                Directory.CreateDirectory(fullPath);
            }
            return fullPath;
        }

        private async Task<int> GetPipelineIdAsync(AgentTaskPluginExecutionContext context, string pipelineDefinition, string buildVersionToDownload, string project, string[] tagFilters, BuildResult resultFilter = BuildResult.Succeeded, string branchName = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if(String.IsNullOrWhiteSpace(pipelineDefinition)) 
            {
                throw new InvalidOperationException(StringUtil.Loc("CannotBeNullOrEmpty", "Pipeline Definition"));
            }

            VssConnection connection = context.VssConnection;
            BuildHttpClient buildHttpClient = connection.GetClient<BuildHttpClient>();

            var isDefinitionNum = Int32.TryParse(pipelineDefinition, out int definition);
            if (!isDefinitionNum)
            {
                var definitionRef = (await buildHttpClient.GetDefinitionsAsync(new System.Guid(project), pipelineDefinition, cancellationToken: cancellationToken)).FirstOrDefault();
                if (definitionRef == null)
                {
                    throw new ArgumentException(StringUtil.Loc("PipelineDoesNotExist", pipelineDefinition));
                }
                else
                {
                    definition = definitionRef.Id;
                }
            }
            var definitions = new List<int>() { definition };

            List<Build> list;
            if (buildVersionToDownload == buildVersionToDownloadLatest)
            {
                list = await buildHttpClient.GetBuildsAsync(project, definitions, tagFilters: tagFilters, queryOrder: BuildQueryOrder.FinishTimeDescending, resultFilter: resultFilter);
            }
            else if (buildVersionToDownload == buildVersionToDownloadLatestFromBranch)
            {
                list = await buildHttpClient.GetBuildsAsync(project, definitions, branchName: branchName, tagFilters: tagFilters, queryOrder: BuildQueryOrder.FinishTimeDescending, resultFilter: resultFilter);
            }
            else
            {
                throw new InvalidOperationException("Unreachable code!");
            }

            if (list.Count > 0)
            {
                return list.First().Id;
            }
            else
            {
                throw new ArgumentException(StringUtil.Loc("BuildsDoesNotExist"));
            }
        }

        private BuildResult GetResultFilter(bool allowPartiallySucceededBuilds, bool allowFailedBuilds, bool allowCanceledBuilds)
        {
            var result = BuildResult.Succeeded;

            if (allowPartiallySucceededBuilds)
            {
                result |= BuildResult.PartiallySucceeded;
            }

            if (allowFailedBuilds)
            {
                result |= BuildResult.Failed;
            }

            if (allowCanceledBuilds)
            {
                result |= BuildResult.Canceled;
            }

            return result;
        }
      
        private async Task<Guid> GetProjectIdAsync(AgentTaskPluginExecutionContext context, string projectName)
        {
            VssConnection connection = context.VssConnection;
            var projectClient = connection.GetClient<ProjectHttpClient>();

            TeamProject proj = null;

            try
            {
                proj = await projectClient.GetProject(projectName);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Get project failed " + projectName + " , exception: " + ex);
            }

            return proj.Id;
        }

        private void OutputBuildInfo(AgentTaskPluginExecutionContext context, int? pipelineId){
            context.Output(StringUtil.Loc("DownloadingFromBuild", pipelineId));
            // populate output variable 'BuildNumber' with buildId
            context.SetVariable("BuildNumber", pipelineId.ToString());
        }
    }
}