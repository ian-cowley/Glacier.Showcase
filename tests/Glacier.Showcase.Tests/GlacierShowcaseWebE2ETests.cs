namespace Glacier.Showcase.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

[Trait("Category", "E2E")]
public class GlacierShowcaseWebE2ETests
{
    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task WebApp_Playwright_WalksHomeMissionControlGpuAndNotFound()
    {
        if (Environment.GetEnvironmentVariable("GLACIER_RUN_E2E") != "1")
        {
            return;
        }

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        int port = GetFreePort();
        string baseUrl = $"http://127.0.0.1:{port}";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{Path.Combine(repoRoot, "Glacier.Showcase.csproj")}\" --no-build -c Release --urls {baseUrl}",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);

        try
        {
            using var probe = new HttpClient();
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    var resp = await probe.GetAsync(baseUrl);
                    if (resp.IsSuccessStatusCode) break;
                }
                catch { }
                await Task.Delay(250);
            }

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Channel = "msedge" });
            var page = await browser.NewPageAsync();

            // 1. Home page: send button validation, chips, tabs
            await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.WaitForSelectorAsync("#chatTextarea");
            Assert.True(await page.Locator("button.send-btn").IsDisabledAsync());
            await page.Locator("#chatTextarea").ClickAsync();
            await page.Locator("#chatTextarea").PressSequentiallyAsync("Westminster cottage under 150");
            await Assertions.Expect(page.Locator("button.send-btn")).ToBeEnabledAsync();

            int homeTabs = await page.Locator("button.tab-btn").CountAsync();
            for (int i = 0; i < homeTabs; i++)
            {
                await page.Locator("button.tab-btn").Nth(i).ClickAsync();
            }

            // 2. Mission Control: all 6 tabs
            await page.GotoAsync($"{baseUrl}/mission-control", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.WaitForSelectorAsync("button.mc-tab");
            int mcTabs = await page.Locator("button.mc-tab").CountAsync();
            Assert.Equal(6, mcTabs);
            for (int i = 0; i < mcTabs; i++)
            {
                await page.Locator("button.mc-tab").Nth(i).ClickAsync();
            }

            // 3. GPU Showcase
            await page.GotoAsync($"{baseUrl}/gpu", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.WaitForSelectorAsync("button.btn.btn-primary");

            // 4. Not Found route
            await page.GotoAsync($"{baseUrl}/does-not-exist-404", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            string content = await page.ContentAsync();
            Assert.Contains("Not Found", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (!proc!.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }
}
