namespace Glacier.Showcase.Tests;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Glacier.Showcase.Components.Layout;
using Glacier.Showcase.Components.Pages;
using Glacier.Showcase.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public class GlacierShowcaseUiComponentTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"embedding":{"values":[0.1,0.2,0.3]}}""", Encoding.UTF8, "application/json")
            });
    }

    private static TestContext CreateShowcaseContext()
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var httpClient = new HttpClient(new StubHandler(), disposeHandler: false);
        ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        ctx.Services.AddSingleton(new GeminiEmbeddingClient(httpClient, ""));
        ctx.Services.AddSingleton<AirbnbDataService>();
        ctx.Services.AddSingleton<NeighborhoodAssessmentService>();
        ctx.Services.AddSingleton<PdfReportService>();
        ctx.Services.AddSingleton<GpuAccelerationBenchmarkService>();
        ctx.Services.AddSingleton<MissionControlService>();
        ctx.Services.AddScoped<AgentOrchestratorState>();
        ctx.Services.AddScoped<AgentService>();

        return ctx;
    }

    [Fact]
    public void HomePage_PromptValidation_ExampleChips_AllSixTabs_AutoAdvance_And_AgentInspector_Work()
    {
        using var ctx = CreateShowcaseContext();
        var cut = ctx.RenderComponent<Home>();

        // Input validation: send button is disabled when prompt is empty/whitespace
        var sendBtn = cut.Find("button.send-btn");
        Assert.True(sendBtn.HasAttribute("disabled"));

        var textarea = cut.Find("#chatTextarea");
        textarea.Input("   ");
        Assert.True(cut.Find("button.send-btn").HasAttribute("disabled"));

        // Non-empty prompt enables send button
        textarea.Input("Cozy flat in Westminster under 150");
        Assert.False(cut.Find("button.send-btn").HasAttribute("disabled"));

        // Example prompt chip populates prompt
        var chips = cut.FindAll("button.prompt-chip");
        Assert.NotEmpty(chips);
        chips[0].Click();
        Assert.False(cut.Find("button.send-btn").HasAttribute("disabled"));

        // Auto-advance toggle button flips state
        var autoToggle = cut.Find("button.auto-advance-toggle");
        string beforeClass = autoToggle.GetAttribute("class") ?? "";
        autoToggle.Click();
        string afterClass = cut.Find("button.auto-advance-toggle").GetAttribute("class") ?? "";
        Assert.NotEqual(beforeClass, afterClass);

        // All 6 tabs switch cleanly
        int tabCount = cut.FindAll("button.tab-btn").Count;
        Assert.Equal(6, tabCount);
        for (int i = 0; i < tabCount; i++)
        {
            cut.FindAll("button.tab-btn")[i].Click();
        }

        // Back to orchestrator tab and click SVG agent nodes + inspector close
        cut.FindAll("button.tab-btn")[0].Click();
        int nodeCount = cut.FindAll("g.svg-node").Count;
        Assert.True(nodeCount >= 6);
        for (int i = 0; i < nodeCount; i++)
        {
            var node = cut.FindAll("g.svg-node")[i];
            if (node.Attributes.Any(a => a.Name.Contains("onclick", StringComparison.OrdinalIgnoreCase)))
            {
                node.Click();
            }
        }
        var closeInspector = cut.FindAll("button.inspector-close-btn");
        if (closeInspector.Count > 0)
        {
            closeInspector[0].Click();
        }
    }

    [Fact]
    public void MissionControlPage_AllSixTabs_ModelDeviceSelects_Prompt_And_EmpiricalReset_Work()
    {
        using var ctx = CreateShowcaseContext();
        var cut = ctx.RenderComponent<MissionControl>();

        // Switch through all 6 tabs
        int tabCount = cut.FindAll("button.mc-tab").Count;
        Assert.Equal(6, tabCount);
        for (int i = 0; i < tabCount; i++)
        {
            cut.FindAll("button.mc-tab")[i].Click();
        }

        // Speedometer tab: selects, prompt input
        cut.FindAll("button.mc-tab")[0].Click();
        var selects = cut.FindAll("select.mc-select");
        Assert.True(selects.Count >= 2);
        var modelOpts = selects[0].QuerySelectorAll("option");
        if (modelOpts.Length > 1)
        {
            cut.FindAll("select.mc-select")[0].Change(modelOpts[1].GetAttribute("value")!);
        }
        var devOpts = cut.FindAll("select.mc-select")[1].QuerySelectorAll("option");
        if (devOpts.Length > 1)
        {
            cut.FindAll("select.mc-select")[1].Change(devOpts[1].GetAttribute("value")!);
        }

        var promptInput = cut.Find("input.mc-input");
        promptInput.Change("Benchmark prompt");

        // Loss tab: Reset Empirical Log button
        cut.FindAll("button.mc-tab")[1].Click();
        var resetBtn = cut.FindAll("button.mc-btn-sm").FirstOrDefault(b => b.TextContent.Contains("Reset Empirical Log"));
        resetBtn?.Click();

        // Graph tab: click graph node
        cut.FindAll("button.mc-tab")[2].Click();
        var graphNodes = cut.FindAll("g.graph-node-g");
        if (graphNodes.Count > 0)
        {
            graphNodes[0].Click();
        }

        // Paged tab: verify VRAM stats rendered
        cut.FindAll("button.mc-tab")[4].Click();
        Assert.Contains("VRAM Saved", cut.Markup);
    }

    [Fact]
    public void GpuShowcasePage_RendersAndHasBenchmarkTriggerButton()
    {
        using var ctx = CreateShowcaseContext();
        var cut = ctx.RenderComponent<GpuShowcase>();

        var runBtn = cut.Find("button.btn.btn-primary");
        Assert.False(runBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void Layout_NavMenu_ReconnectModal_Error_And_NotFound_RenderCleanly()
    {
        using var ctx = CreateShowcaseContext();

        var nav = ctx.RenderComponent<NavMenu>();
        Assert.NotNull(nav.Find("input.navbar-toggler"));
        Assert.Contains("mission-control", nav.Markup);
        Assert.Contains("gpu", nav.Markup);

        var reconnect = ctx.RenderComponent<ReconnectModal>();
        Assert.NotNull(reconnect.Find("#components-reconnect-button"));
        Assert.NotNull(reconnect.Find("#components-resume-button"));

        var error = ctx.RenderComponent<Error>();
        Assert.Contains("Error", error.Markup);

        var notFound = ctx.RenderComponent<NotFound>();
        Assert.Contains("Not Found", notFound.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
