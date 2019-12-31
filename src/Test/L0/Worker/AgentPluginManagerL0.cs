using System;
using Microsoft.VisualStudio.Services.Agent.Worker;
using System.Runtime.CompilerServices;
using Xunit;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Agent.Plugins.PipelineArtifact;
using Agent.Plugins.PipelineCache;

namespace Microsoft.VisualStudio.Services.Agent.Tests.Worker
{
    public sealed class AgentPluginManagerL0
    {

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void SimpleTests()
        {
            using (TestHostContext tc = CreateTestContext())
            {
                Tracing trace = tc.GetTrace();
                var agentPluginManager = new AgentPluginManager();
                agentPluginManager.Initialize(tc);

                Assert.True(agentPluginManager.GetTaskPlugins(Pipelines.PipelineConstants.CheckoutTask.Id).Count == 2, "Checkout Task has 2 Task Plugins");
                Assert.True(agentPluginManager.GetTaskPlugins(PipelineArtifactPluginConstants.DownloadPipelineArtifactTaskId).Count == 7, "Download Pipline Artifact Task has 2 Task Plugins");
                Assert.True(agentPluginManager.GetTaskPlugins(PipelineArtifactPluginConstants.PublishPipelineArtifactTaskId).Count == 3, "Publish Pipeline Artifact Task has 2 Task Plugins");
                Assert.True(agentPluginManager.GetTaskPlugins(PipelineCachePluginConstants.CacheTaskId).Count == 2, "Pipeline Cache Task has 2 Task Plugins");
            }
        }

        private TestHostContext CreateTestContext([CallerMemberName] String testName = "")
        {
            TestHostContext tc = new TestHostContext(this, testName);
            return tc;
        }
    }
}
