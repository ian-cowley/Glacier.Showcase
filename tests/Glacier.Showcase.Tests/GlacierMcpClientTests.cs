using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

/// <summary>
/// Dedicated Unit and Lifecycle Tests for GlacierMcpClient.
/// Verifies process startup guards, disposal safety, JSON-RPC correlation, and error handling.
/// </summary>
public class GlacierMcpClientTests
{
    private static readonly string WorkspaceRoot = GlacierWorkspaceResolver.ResolveWorkspaceRoot();

    [Fact]
    public void McpClient_Constructor_InitializesPropertiesCorrectly()
    {
        var env = new Dictionary<string, string> { ["TEST_VAR"] = "VALUE_1" };
        var client = new GlacierMcpClient(
            "TestServer",
            "dotnet",
            new[] { "run", "--project", "test" },
            env,
            WorkspaceRoot);

        Assert.Equal("TestServer", client.ServerName);
        Assert.False(client.IsRunning);
        Assert.False(client.IsInitialized);
    }

    [Fact]
    public async Task McpClient_StartAsync_NonExistentCommand_ThrowsException()
    {
        using var client = new GlacierMcpClient(
            "InvalidServer",
            "nonexistent_executable_binary_xyz_12345.exe",
            Array.Empty<string>(),
            workingDirectory: WorkspaceRoot);

        await Assert.ThrowsAnyAsync<Exception>(() => client.StartAsync());
    }

    [Fact]
    public void McpClient_DoubleDispose_IsSafeAndDoesNotThrow()
    {
        var client = new GlacierMcpClient(
            "DisposeTest",
            "dotnet",
            Array.Empty<string>(),
            workingDirectory: WorkspaceRoot);

        client.Dispose();
        client.Dispose(); // Must not throw on second dispose

        Assert.False(client.IsRunning);
        Assert.False(client.IsInitialized);
    }

    [Fact]
    public async Task McpClient_DisposeAsync_IsSafeAndDoesNotThrow()
    {
        var client = new GlacierMcpClient(
            "DisposeAsyncTest",
            "dotnet",
            Array.Empty<string>(),
            workingDirectory: WorkspaceRoot);

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.False(client.IsRunning);
    }

    [Fact]
    public async Task McpClient_OperationsAfterDispose_ThrowObjectDisposedException()
    {
        var client = new GlacierMcpClient(
            "DisposedOpTest",
            "dotnet",
            Array.Empty<string>(),
            workingDirectory: WorkspaceRoot);

        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.StartAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.InitializeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ListToolsAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.CallToolAsync("ping", "{}"));
    }

    [Fact]
    public async Task McpClient_CallToolBeforeInitialize_ThrowsInvalidOperationException()
    {
        string graphHostDll = Path.Combine(WorkspaceRoot, "Glacier.Graph", "src", "Glacier.Graph.Host", "bin", "Release", "net10.0", "Glacier.Graph.Host.dll");
        if (!File.Exists(graphHostDll))
        {
            return; // Skip if DLL not found
        }

        string tempDb = Path.Combine(Path.GetTempPath(), $"mcp_init_test_{Guid.NewGuid():N}.bin");
        try
        {
            await using var client = new GlacierMcpClient(
                "GraphInitTest",
                "dotnet",
                new[] { graphHostDll, tempDb },
                workingDirectory: WorkspaceRoot);

            await client.StartAsync();
            Assert.True(client.IsRunning);
            Assert.False(client.IsInitialized);

            // Attempting to call tool before InitializeAsync must throw
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallToolAsync("ping", "{}"));
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }
}
