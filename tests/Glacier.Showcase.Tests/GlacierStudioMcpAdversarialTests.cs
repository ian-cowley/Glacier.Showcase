using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

public class GlacierStudioMcpAdversarialTests
{
    private static readonly string EcosystemRoot = ResolveEcosystemRoot();
    private static readonly string WorkspaceRoot = EcosystemRoot;

    private static string ResolveEcosystemRoot()
    {
        string root = GlacierWorkspaceResolver.ResolveWorkspaceRoot();
        if (Directory.Exists(Path.Combine(root, "Glacier.Graph")))
        {
            return root;
        }
        string parent = Path.GetFullPath(Path.Combine(root, ".."));
        if (Directory.Exists(Path.Combine(parent, "Glacier.Graph")))
        {
            return parent;
        }
        return root;
    }

    private static string GetGraphHostDll()
    {
        return Path.Combine(EcosystemRoot, "Glacier.Graph", "src", "Glacier.Graph.Host", "bin", "Release", "net10.0", "Glacier.Graph.Host.dll");
    }

    private static string GetVectorHostDll()
    {
        return Path.Combine(EcosystemRoot, "Glacier.Vector", "src", "Glacier.Vector.Host", "bin", "Release", "net10.0", "Glacier.Vector.Host.dll");
    }

    private static string GetSharpToolsExe()
    {
        return "C:/Users/spuri/source/repos/SharpToolsMCP/SharpTools.StdioServer/bin/Release/net8.0/SharpTools.StdioServer.exe";
    }

    [Fact]
    public async Task McpClient_GlacierGraphHost_PerformsHandshakeAndListsTools()
    {
        string graphHostDll = GetGraphHostDll();
        Assert.True(File.Exists(graphHostDll), $"Glacier.Graph.Host.dll not found at: {graphHostDll}");

        string tempDb = Path.Combine(Path.GetTempPath(), $"graph_test_{Guid.NewGuid():N}.bin");

        try
        {
            await using var client = new GlacierMcpClient(
                "GlacierGraphTest",
                "dotnet",
                new[] { graphHostDll, tempDb },
                workingDirectory: WorkspaceRoot);

            await client.StartAsync();
            Assert.True(client.IsRunning);

            await client.InitializeAsync();
            Assert.True(client.IsInitialized);

            var tools = await client.ListToolsAsync();
            Assert.NotEmpty(tools);
            Assert.Contains(tools, t => t.Name == "ping");
            Assert.Contains(tools, t => t.Name == "add_node");
            Assert.Contains(tools, t => t.Name == "add_edge");
            Assert.Contains(tools, t => t.Name == "find_shortest_path");
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }

    [Fact]
    public async Task McpClient_GlacierGraphHost_ExecutesToolsSuccessfully()
    {
        string graphHostDll = GetGraphHostDll();
        Assert.True(File.Exists(graphHostDll), $"Glacier.Graph.Host.dll not found at: {graphHostDll}");

        string tempDb = Path.Combine(Path.GetTempPath(), $"graph_test_{Guid.NewGuid():N}.bin");

        try
        {
            await using var client = new GlacierMcpClient(
                "GlacierGraphTest",
                "dotnet",
                new[] { graphHostDll, tempDb },
                workingDirectory: WorkspaceRoot);

            // Execute ping
            string pingResult = await client.CallToolAsync("ping", "{}");
            Assert.NotNull(pingResult);
            Assert.NotEmpty(pingResult);

            // Execute add_node
            string addNodeResult = await client.CallToolAsync("add_node", JsonSerializer.Serialize(new
            {
                id = "node_alpha",
                metadata = "{\"kind\":\"test_entity\"}"
            }));
            Assert.NotNull(addNodeResult);
            Assert.Contains("node_alpha", addNodeResult);
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }

    [Fact]
    public async Task McpClient_GlacierGraphHost_ThrowsRpcExceptionOnUnknownTool()
    {
        string graphHostDll = GetGraphHostDll();
        Assert.True(File.Exists(graphHostDll), $"Glacier.Graph.Host.dll not found at: {graphHostDll}");

        string tempDb = Path.Combine(Path.GetTempPath(), $"graph_test_{Guid.NewGuid():N}.bin");

        try
        {
            await using var client = new GlacierMcpClient(
                "GlacierGraphTest",
                "dotnet",
                new[] { graphHostDll, tempDb },
                workingDirectory: WorkspaceRoot);

            var ex = await Assert.ThrowsAsync<McpRpcException>(() =>
                client.CallToolAsync("non_existent_tool_adversarial", "{}"));

            Assert.True(ex.Code != 0);
            Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }

    [Fact]
    public async Task McpClient_GlacierVectorHost_PerformsHandshakeAndListsTools()
    {
        string vectorHostDll = GetVectorHostDll();
        Assert.True(File.Exists(vectorHostDll), $"Glacier.Vector.Host.dll not found at: {vectorHostDll}");

        await using var client = new GlacierMcpClient(
            "GlacierVectorTest",
            "dotnet",
            new[] { vectorHostDll, "128" },
            workingDirectory: WorkspaceRoot);

        await client.StartAsync();
        Assert.True(client.IsRunning);

        await client.InitializeAsync();
        Assert.True(client.IsInitialized);

        var tools = await client.ListToolsAsync();
        Assert.NotEmpty(tools);
        Assert.Contains(tools, t => t.Name == "ping");
        Assert.Contains(tools, t => t.Name == "add_vector");
        Assert.Contains(tools, t => t.Name == "search_vectors");

        string pingRes = await client.CallToolAsync("ping", "{}");
        Assert.NotEmpty(pingRes);
    }

    [Fact]
    public async Task McpClient_SharpTools_PerformsHandshakeAndListsTools()
    {
        string sharpToolsExe = GetSharpToolsExe();
        if (!File.Exists(sharpToolsExe))
        {
            // Skip if environment doesn't have SharpTools deployed
            return;
        }

        var env = new Dictionary<string, string>
        {
            ["DOTNET_ROOT"] = @"C:\Program Files\dotnet"
        };

        await using var client = new GlacierMcpClient(
            "SharpToolsTest",
            sharpToolsExe,
            args: Array.Empty<string>(),
            env: env,
            workingDirectory: WorkspaceRoot);

        await client.StartAsync();
        Assert.True(client.IsRunning);

        await client.InitializeAsync();
        Assert.True(client.IsInitialized);

        var tools = await client.ListToolsAsync();
        Assert.NotEmpty(tools);
        Assert.Contains(tools, t => t.Name == "SharpTool_ViewDefinition");
    }

    [Fact]
    public async Task McpClient_AbruptProcessTermination_FailsPendingAndNewRequestsWithoutHanging()
    {
        // 1. Test short-lived / crashing child process: cmd.exe exiting immediately
        var sw = Stopwatch.StartNew();
        await using var crashingClient = new GlacierMcpClient(
            "CrashServer",
            "cmd.exe",
            new[] { "/c", "exit 1" },
            workingDirectory: WorkspaceRoot);

        await crashingClient.StartAsync();

        // Calling initialize on an abruptly exited process must fail quickly via IOException or InvalidOperationException without waiting 45s
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => crashingClient.InitializeAsync());
        sw.Stop();

        Assert.True(sw.Elapsed.TotalSeconds < 5.0, $"Abrupt exit took {sw.Elapsed.TotalSeconds}s - expected fast failure");
        Assert.True(ex is IOException or InvalidOperationException, $"Expected IOException or InvalidOperationException, got {ex.GetType().Name}");

        // 2. Test disposed client: CallToolAsync after disposal throws ObjectDisposedException
        string graphHostDll = GetGraphHostDll();
        Assert.True(File.Exists(graphHostDll));
        string tempDb = Path.Combine(Path.GetTempPath(), $"graph_test_{Guid.NewGuid():N}.bin");

        try
        {
            var client = new GlacierMcpClient(
                "GlacierGraphDispose",
                "dotnet",
                new[] { graphHostDll, tempDb },
                workingDirectory: WorkspaceRoot);

            await client.StartAsync();
            await client.InitializeAsync();

            client.Dispose();

            // Calling after disposal must throw ObjectDisposedException
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.CallToolAsync("ping", "{}"));
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }

    [Fact]
    public async Task McpManager_EndToEnd_ExecutesPingAndReportsDiagnostics()
    {
        using var manager = new GlacierMcpManager(WorkspaceRoot);

        // Ping against GlacierGraphEngine
        string result = await manager.ExecuteMcpToolAsync("GlacierGraphEngine", "ping", "{}");
        Assert.NotNull(result);
        Assert.DoesNotContain("\"error\": true", result);

        // Call an unknown tool against GlacierGraphEngine
        string errResult = await manager.ExecuteMcpToolAsync("GlacierGraphEngine", "invalid_tool_test", "{}");
        Assert.NotNull(errResult);
        Assert.Contains("\"error\": true", errResult);
        Assert.Contains("Failed to execute MCP tool", errResult);

        // Call against completely unknown server
        string unknownServerResult = await manager.ExecuteMcpToolAsync("NonExistentMcpServer", "ping", "{}");
        Assert.NotNull(unknownServerResult);
        Assert.Contains("\"error\": true", unknownServerResult);
        Assert.Contains("is not defined in configuration", unknownServerResult);

        // Call a tool explicitly disabled in configuration
        string disabledToolResult = await manager.ExecuteMcpToolAsync("SharpTools", "SharpTool_CreateRoslynDocument", "{}");
        Assert.NotNull(disabledToolResult);
        Assert.Contains("\"error\": true", disabledToolResult);
        Assert.Contains("is disabled on server", disabledToolResult);
    }
}
