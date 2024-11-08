// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using Microsoft.VisualStudio.Services.WebApi;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Agent.Sdk.Knob;
using Microsoft.VisualStudio.Services.Agent.Worker.Telemetry;
using Newtonsoft.Json;
using System.Runtime.Versioning;
using Agent.Sdk.Util;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Handlers
{
    public interface IHandler : IAgentService
    {
        List<ServiceEndpoint> Endpoints { get; set; }
        Dictionary<string, string> Environment { get; set; }
        IExecutionContext ExecutionContext { get; set; }
        Variables RuntimeVariables { get; set; }
        IStepHost StepHost { get; set; }
        Dictionary<string, string> Inputs { get; set; }
        List<SecureFile> SecureFiles { get; set; }
        string TaskDirectory { get; set; }
        Pipelines.TaskStepDefinitionReference Task { get; set; }
        Task RunAsync();
        void AfterExecutionContextInitialized();
    }

    public abstract class Handler : AgentService
    {
        // On Windows, the maximum supported size of a environment variable value is 32k.
        // You can set environment variables greater then 32K, but Node won't be able to read them.
        private const int _windowsEnvironmentVariableMaximumSize = 32766;

        protected bool _continueAfterCancelProcessTreeKillAttempt;

        protected IWorkerCommandManager CommandManager { get; private set; }

        public List<ServiceEndpoint> Endpoints { get; set; }
        public Dictionary<string, string> Environment { get; set; }
        public Variables RuntimeVariables { get; set; }
        public IExecutionContext ExecutionContext { get; set; }
        public IStepHost StepHost { get; set; }
        public Dictionary<string, string> Inputs { get; set; }
        public List<SecureFile> SecureFiles { get; set; }
        public string TaskDirectory { get; set; }
        public Pipelines.TaskStepDefinitionReference Task { get; set; }

        public override void Initialize(IHostContext hostContext)
        {
            ArgUtil.NotNull(hostContext, nameof(hostContext));

            base.Initialize(hostContext);
            CommandManager = hostContext.GetService<IWorkerCommandManager>();
        }

        public void AfterExecutionContextInitialized()
        {
            _continueAfterCancelProcessTreeKillAttempt = AgentKnobs.ContinueAfterCancelProcessTreeKillAttempt.GetValue(ExecutionContext).AsBoolean();
            Trace.Info($"Handler.AfterExecutionContextInitialized _continueAfterCancelProcessTreeKillAttempt = {_continueAfterCancelProcessTreeKillAttempt}");
        }

        protected void AddEndpointsToEnvironment()
        {
            Trace.Entering();
            ArgUtil.NotNull(Endpoints, nameof(Endpoints));
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(ExecutionContext.Endpoints, nameof(ExecutionContext.Endpoints));

            List<ServiceEndpoint> endpoints = Endpoints;

            // Add the endpoints to the environment variable dictionary.
            foreach (ServiceEndpoint endpoint in endpoints)
            {
                ArgUtil.NotNull(endpoint, nameof(endpoint));

                string partialKey = null;
                if (endpoint.Id != Guid.Empty)
                {
                    partialKey = endpoint.Id.ToString();
                }
                else if (string.Equals(endpoint.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase))
                {
                    partialKey = WellKnownServiceEndpointNames.SystemVssConnection.ToUpperInvariant();
                }
                else if (endpoint.Data == null ||
                    !endpoint.Data.TryGetValue(EndpointData.RepositoryId, out partialKey) ||
                    string.IsNullOrEmpty(partialKey))
                {
                    continue; // This should never happen.
                }

                AddEnvironmentVariable(
                    key: $"ENDPOINT_URL_{partialKey}",
                    value: endpoint.Url?.ToString());
                AddEnvironmentVariable(
                    key: $"ENDPOINT_AUTH_{partialKey}",
                    // Note, JsonUtility.ToString will not null ref if the auth object is null.
                    value: JsonUtility.ToString(endpoint.Authorization));
                if (endpoint.Authorization != null && endpoint.Authorization.Scheme != null)
                {
                    AddEnvironmentVariable(
                        key: $"ENDPOINT_AUTH_SCHEME_{partialKey}",
                        value: endpoint.Authorization.Scheme);

                    foreach (KeyValuePair<string, string> pair in endpoint.Authorization.Parameters)
                    {
                        AddEnvironmentVariable(
                            key: $"ENDPOINT_AUTH_PARAMETER_{partialKey}_{VarUtil.ConvertToEnvVariableFormat(pair.Key, preserveCase: false)}",
                            value: pair.Value);
                    }
                }
                if (endpoint.Id != Guid.Empty)
                {
                    AddEnvironmentVariable(
                        key: $"ENDPOINT_DATA_{partialKey}",
                        // Note, JsonUtility.ToString will not null ref if the data object is null.
                        value: JsonUtility.ToString(endpoint.Data));

                    if (endpoint.Data != null)
                    {
                        foreach (KeyValuePair<string, string> pair in endpoint.Data)
                        {
                            AddEnvironmentVariable(
                                key: $"ENDPOINT_DATA_{partialKey}_{VarUtil.ConvertToEnvVariableFormat(pair.Key, preserveCase: false)}",
                                value: pair.Value);
                        }
                    }
                }
            }
        }

        protected void AddSecureFilesToEnvironment()
        {
            Trace.Entering();
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(SecureFiles, nameof(SecureFiles));

            List<SecureFile> secureFiles = SecureFiles;

            // Add the secure files to the environment variable dictionary.
            foreach (SecureFile secureFile in secureFiles)
            {
                if (secureFile != null && secureFile.Id != Guid.Empty)
                {
                    string partialKey = secureFile.Id.ToString();
                    AddEnvironmentVariable(
                        key: $"SECUREFILE_NAME_{partialKey}",
                        value: secureFile.Name);
                    AddEnvironmentVariable(
                        key: $"SECUREFILE_TICKET_{partialKey}",
                        value: secureFile.Ticket);
                }
            }
        }

        protected void AddInputsToEnvironment()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(Inputs, nameof(Inputs));

            // Add the inputs to the environment variable dictionary.
            foreach (KeyValuePair<string, string> pair in Inputs)
            {
                AddEnvironmentVariable(
                    key: $"INPUT_{VarUtil.ConvertToEnvVariableFormat(pair.Key, preserveCase: false)}",
                    value: pair.Value);
            }
        }

        protected void AddVariablesToEnvironment(bool excludeNames = false, bool excludeSecrets = false)
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(Environment, nameof(Environment));
            ArgUtil.NotNull(RuntimeVariables, nameof(RuntimeVariables));

            // Add the public variables.
            var names = new List<string>();
            foreach (Variable variable in RuntimeVariables.Public)
            {
                // Add "agent.jobstatus" using the unformatted name and formatted name.
                if (string.Equals(variable.Name, Constants.Variables.Agent.JobStatus, StringComparison.OrdinalIgnoreCase))
                {
                    AddEnvironmentVariable(variable.Name, variable.Value);
                }

                // Add the variable using the formatted name.
                string formattedName = VarUtil.ConvertToEnvVariableFormat(variable.Name, variable.PreserveCase);
                AddEnvironmentVariable(formattedName, variable.Value);

                // Store the name.
                names.Add(variable.Name ?? string.Empty);
            }

            // Add the public variable names.
            if (!excludeNames)
            {
                AddEnvironmentVariable("VSTS_PUBLIC_VARIABLES", JsonUtility.ToString(names));
            }

            if (!excludeSecrets)
            {
                // Add the secret variables.
                var secretNames = new List<string>();
                foreach (Variable variable in RuntimeVariables.Private)
                {
                    // Add the variable using the formatted name.
                    string formattedName = VarUtil.ConvertToEnvVariableFormat(variable.Name, variable.PreserveCase);
                    AddEnvironmentVariable($"SECRET_{formattedName}", variable.Value);

                    // Store the name.
                    secretNames.Add(variable.Name ?? string.Empty);
                }

                // Add the secret variable names.
                if (!excludeNames)
                {
                    AddEnvironmentVariable("VSTS_SECRET_VARIABLES", JsonUtility.ToString(secretNames));
                }
            }
        }

        protected void AddEnvironmentVariable(string key, string value)
        {
            ArgUtil.NotNullOrEmpty(key, nameof(key));
            Trace.Verbose($"Setting env '{key}' to '{value}'.");

            Environment[key] = value ?? string.Empty;

            if (PlatformUtil.RunningOnWindows && Environment[key].Length > _windowsEnvironmentVariableMaximumSize)
            {
                ExecutionContext.Warning(StringUtil.Loc("EnvironmentVariableExceedsMaximumLength", key, value.Length, _windowsEnvironmentVariableMaximumSize));
            }
        }

        protected void AddTaskVariablesToEnvironment()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(ExecutionContext.TaskVariables, nameof(ExecutionContext.TaskVariables));

            foreach (Variable variable in ExecutionContext.TaskVariables.Public)
            {
                // Add the variable using the formatted name.
                string formattedKey = VarUtil.ConvertToEnvVariableFormat(variable.Name, variable.PreserveCase);
                AddEnvironmentVariable($"VSTS_TASKVARIABLE_{formattedKey}", variable.Value);
            }

            foreach (Variable variable in ExecutionContext.TaskVariables.Private)
            {
                // Add the variable using the formatted name.
                string formattedKey = VarUtil.ConvertToEnvVariableFormat(variable.Name, variable.PreserveCase);
                AddEnvironmentVariable($"VSTS_TASKVARIABLE_{formattedKey}", variable.Value);
            }
        }

        protected void AddPrependPathToEnvironment()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(ExecutionContext.PrependPath, nameof(ExecutionContext.PrependPath));
            if (ExecutionContext.PrependPath.Count == 0)
            {
                return;
            }

            // Prepend path.
            var containerStepHost = StepHost as ContainerStepHost;
            if (containerStepHost != null)
            {
                List<string> prepend = new List<string>();
                foreach (var path in ExecutionContext.PrependPath)
                {
                    prepend.Add(ExecutionContext.TranslatePathForStepTarget(path));
                }
                containerStepHost.PrependPath = string.Join(Path.PathSeparator.ToString(), prepend.Reverse<string>());
            }
            else
            {
                string prepend = string.Join(Path.PathSeparator.ToString(), ExecutionContext.PrependPath.Reverse<string>());
                string taskEnvPATH;
                Environment.TryGetValue(Constants.PathVariable, out taskEnvPATH);
                string originalPath = RuntimeVariables.Get(Constants.PathVariable) ?? // Prefer a job variable.
                    taskEnvPATH ?? // Then a task-environment variable.
                    System.Environment.GetEnvironmentVariable(Constants.PathVariable) ?? // Then an environment variable.
                    string.Empty;
                string newPath = PathUtil.PrependPath(prepend, originalPath);
                AddEnvironmentVariable(Constants.PathVariable, newPath);
            }
        }

        [SupportedOSPlatform("windows")]
        protected bool PsModulePathContainsPowershellCoreLocations()
        {
            bool checkLocationsKnob = AgentKnobs.CheckPsModulesLocations.GetValue(HostContext).AsBoolean();

            bool isPwshCore = Inputs.TryGetValue("pwsh", out string pwsh) && StringUtil.ConvertToBoolean(pwsh);

            if (!PlatformUtil.RunningOnWindows || !checkLocationsKnob || isPwshCore)
            {
                return false;
            }

            const string PSModulePath = nameof(PSModulePath);

            bool localVariableExists = Environment.TryGetValue(PSModulePath, out string localVariable);
            bool localVariableContainsPwshLocations = PsModulePathUtil.ContainsPowershellCoreLocations(localVariable);

            // Special case when the env variable is set for local process environment
            // for example by vso command in a preceding pipeline step
            if (localVariableExists && !localVariableContainsPwshLocations)
            {
                return false;
            }

            string systemVariable = System.Environment.GetEnvironmentVariable(PSModulePath);

            bool systemVariableContainsPwshLocations = PsModulePathUtil.ContainsPowershellCoreLocations(systemVariable);

            return localVariableContainsPwshLocations || systemVariableContainsPwshLocations;
        }

        [SupportedOSPlatform("windows")]
        protected void RemovePSModulePathFromEnvironment()
        {
            if (PlatformUtil.RunningOnWindows == false
                || AgentKnobs.CleanupPSModules.GetValue(ExecutionContext).AsBoolean() == false)
            {
                return;
            }
            try
            {
                if (WindowsProcessUtil.IsAgentRunningInPowerShellCore())
                {
                    AddEnvironmentVariable("PSModulePath", "");
                    Trace.Info("PSModulePath is removed from environment since agent is running on Windows and in PowerShell.");
                }
            }
            catch (Exception ex)
            {
                Trace.Error(ex.Message);

                var telemetry = new Dictionary<string, string>()
                {
                    ["ParentProcessFinderError"] = StringUtil.Loc("ParentProcessFinderError")
                };
                PublishTelemetry(telemetry);

                ExecutionContext.Error(StringUtil.Loc("ParentProcessFinderError"));
            }
        }

        // This overload is to handle specific types some other way.
        protected void PublishTelemetry<T>(
            Dictionary<string, T> telemetryData,
            string feature = "TaskHandler"
        )
        {
            // JsonConvert.SerializeObject always converts to base object.
            PublishTelemetry((object)telemetryData, feature);
        }

        private void PublishTelemetry(
            object telemetryData,
            string feature = "TaskHandler"
        )
        {
            ArgUtil.NotNull(Task, nameof(Task));

            var cmd = new Command("telemetry", "publish")
            {
                Data = JsonConvert.SerializeObject(telemetryData, Formatting.None)
            };
            cmd.Properties.Add("area", "PipelinesTasks");
            cmd.Properties.Add("feature", feature);

            var publishTelemetryCmd = new TelemetryCommandExtension();
            publishTelemetryCmd.Initialize(HostContext);
            publishTelemetryCmd.ProcessCommand(ExecutionContext, cmd);
        }
    }
}
