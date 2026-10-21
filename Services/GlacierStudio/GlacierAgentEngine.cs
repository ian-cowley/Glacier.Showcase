using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Glacier.Showcase.Services.GlacierStudio;

public class GlacierAgentEngine
{
    private readonly HttpClient _httpClient;
    private readonly GlacierStudioState _state;
    private readonly GlacierConsoleService _consoleService;
    private readonly GlacierDocTreePreviewService _docPreviewService;
    private readonly GlacierMcpManager _mcpManager;
    private GlacierRuntimeLauncherService? _launcherService;
    private CancellationTokenSource? _currentCts;

    private readonly string _workspaceRoot = GlacierWorkspaceResolver.ResolveWorkspaceRoot();

    public GlacierRuntimeLauncherService? LauncherService
    {
        get => _launcherService;
        set => _launcherService = value;
    }

    public GlacierAgentEngine(
        HttpClient httpClient,
        GlacierStudioState state,
        GlacierConsoleService consoleService,
        GlacierDocTreePreviewService docPreviewService,
        GlacierMcpManager mcpManager,
        GlacierRuntimeLauncherService? launcherService = null)
    {
        _httpClient = httpClient;
        _state = state;
        _consoleService = consoleService;
        _docPreviewService = docPreviewService;
        _mcpManager = mcpManager;
        _launcherService = launcherService;
    }

    public void CancelCurrentExecution()
    {
        _currentCts?.Cancel();
        _state.ExecutionStatus = AgentExecutionStatus.Cancelled;
        _state.StatusText = "Cancelled by user";
        _state.NotifyStateChanged();
    }

    public async Task ProcessUserMessageAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        _currentCts?.Dispose();
        _currentCts = new CancellationTokenSource();
        var ct = _currentCts.Token;

        // 1. Add user message to active session
        var session = _state.ActiveSession;
        session.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Timestamp = DateTime.Now
        });
        session.UpdatedAt = DateTime.Now;

        // Auto-title session if still default
        if (session.Title == "New Conversation" || session.Title == "Initial Chat")
        {
            session.Title = prompt.Length > 28 ? prompt[..25] + "..." : prompt;
        }

        _state.NotifyStateChanged();

        // 2. Check for slash commands
        if (prompt.StartsWith("/"))
        {
            await HandleSlashCommandAsync(prompt, ct);
            return;
        }

        // 3. Run Autonomous Agent Loop
        await RunAgentLoopAsync(prompt, ct);
    }

    private async Task HandleSlashCommandAsync(string prompt, CancellationToken ct)
    {
        var parts = prompt.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant();
        string args = parts.Length > 1 ? parts[1] : "";

        var session = _state.ActiveSession;

        switch (cmd)
        {
            case "/help":
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = @"### ⚡ Glacier Agent Studio Slash Commands
- `/terminal <command>`: Directly run shell command in the interactive console.
- `/inspect <filepath>`: Load and preview any workspace file in the Glacier.DocTree viewer.
- `/mcp`: List all connected MCP servers and available tools.
- `/clear`: Clear message history in the current session.
- `/plan <objective>`: Formulate a multi-step structured engineering execution plan.
- `/goal <objective>`: Run autonomous goal-directed ReAct agent task.",
                    Timestamp = DateTime.Now
                });
                break;

            case "/clear":
                session.Messages.Clear();
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = "Conversation history cleared. Ready for your instructions.",
                    Timestamp = DateTime.Now
                });
                break;

            case "/terminal":
                if (!string.IsNullOrWhiteSpace(args))
                {
                    _state.ActiveRightTab = "console";
                    _state.NotifyStateChanged();
                    var (exitCode, output) = await _consoleService.ExecuteCommandAsync(args, "USER", ct);
                    session.Messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"```powershell\n$ {args}\n# Exit code: {exitCode}\n{output}\n```",
                        Timestamp = DateTime.Now
                    });
                }
                break;

            case "/inspect":
                if (!string.IsNullOrWhiteSpace(args))
                {
                    _docPreviewService.LoadFile(args);
                    _state.ActiveRightTab = "doc";
                    session.Messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = $"Loaded **{args}** into Document Preview pane. Inspected with `Glacier.DocTree` AST.",
                        Timestamp = DateTime.Now
                    });
                }
                break;

            case "/mcp":
                var sb = new StringBuilder("### 🛠️ Connected MCP Servers & Tools\n\n");
                foreach (var s in _mcpManager.Servers)
                {
                    sb.AppendLine($"- **{s.Name}** (`{s.StatusMessage}`):");
                    foreach (var t in s.AvailableTools)
                    {
                        sb.AppendLine($"  - `{t}`");
                    }
                }
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = sb.ToString(),
                    Timestamp = DateTime.Now
                });
                _state.ActiveRightTab = "mcp";
                break;

            case "/plan":
            case "/goal":
                await RunAgentLoopAsync($"Please formulate and execute a step-by-step plan for: {args}", ct);
                break;

            default:
                await RunAgentLoopAsync(prompt, ct);
                break;
        }

        _state.NotifyStateChanged();
    }

    private async Task RunAgentLoopAsync(string initialPrompt, CancellationToken ct)
    {
        _state.ExecutionStatus = AgentExecutionStatus.Thinking;
        _state.StatusText = "Thinking & Planning...";
        _state.NotifyStateChanged();

        var session = _state.ActiveSession;
        var stopwatch = Stopwatch.StartNew();

        int maxTurns = _state.IsAgentMode ? 8 : 1;
        int currentTurn = 0;
        int totalCompletionTokens = 0;
        ChatMessage? currentAssistantMsg = null;

        try
        {
            while (currentTurn < maxTurns && !ct.IsCancellationRequested)
            {
                currentTurn++;
                _state.StatusText = $"Agent Cycle {currentTurn}/{maxTurns}...";
                _state.NotifyStateChanged();

                // Prepare OpenAI-compatible chat messages payload
                var messagesPayload = BuildMessagesPayload(session);
                var toolsPayload = GetToolsSchema();

                // Dual-tier routing resolution:
                // Strategic planning (Turn 1) and Final synthesis turn route to RemoteDeepReasoning (Beast 14B or fallback).
                // Intermediate tool execution turns route to LocalFastTool (Local standalone <= 4.8 GB).
                AgentTurnTier turnTier = (currentTurn == 1 || currentTurn >= maxTurns)
                    ? AgentTurnTier.RemoteDeepReasoning
                    : AgentTurnTier.LocalFastTool;

                string targetEndpoint = _launcherService != null
                    ? _launcherService.ResolveEndpointForTurn(turnTier)
                    : _state.EndpointUrl;

                string targetModel = _launcherService != null
                    ? _launcherService.ResolveModelForTurn(turnTier)
                    : _state.SelectedModel;

                // Create a distinct assistant message for this turn
                currentAssistantMsg = new ChatMessage
                {
                    Role = "assistant",
                    Timestamp = DateTime.Now
                };
                session.Messages.Add(currentAssistantMsg);

                _state.ExecutionStatus = AgentExecutionStatus.StreamingResponse;
                _state.StatusText = $"Streaming Turn {currentTurn} ({targetModel})...";
                _state.NotifyStateChanged();

                LlmStreamResult? streamResult = null;
                try
                {
                    streamResult = await StreamModelEndpointCoreAsync(
                        targetEndpoint,
                        targetModel,
                        _state.ApiKey,
                        messagesPayload,
                        toolsPayload,
                        onContentDelta: delta =>
                        {
                            currentAssistantMsg.Content += delta;
                            _state.NotifyStateChanged();
                        },
                        onReasoningDelta: delta =>
                        {
                            currentAssistantMsg.ReasoningContent = (currentAssistantMsg.ReasoningContent ?? "") + delta;
                            _state.NotifyStateChanged();
                        },
                        ct);

                    if (turnTier == AgentTurnTier.RemoteDeepReasoning && (targetEndpoint == _launcherService?.BeastEndpointUrl || targetEndpoint.Contains("192.168.1.108")))
                    {
                        _launcherService?.RecordRemoteSuccess();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (targetEndpoint == _launcherService?.BeastEndpointUrl || turnTier == AgentTurnTier.RemoteDeepReasoning)
                {
                    // Failover from Beast remote cluster to local standalone!
                    _launcherService?.RecordRemoteFailure(ex.Message);

                    string fallbackEndpoint = $"http://127.0.0.1:{_launcherService?.InferencePort ?? 5055}/v1";
                    string fallbackModel = _launcherService?.ResolveModelForTurn(AgentTurnTier.LocalFastTool) ?? _state.SelectedModel;

                    _consoleService.AddLog("HYBRID",
                        $"[FALLBACK]: Remote Beast cluster error ({ex.Message}). Automatically failing over turn {currentTurn} to local standalone runtime ({fallbackEndpoint}).",
                        isError: true);

                    currentAssistantMsg.Content = $"> ⚠️ *Beast cluster node unavailable ({ex.Message}). Seamlessly falling back to local standalone model (`{fallbackModel}`).*\n\n";
                    _state.NotifyStateChanged();

                    try
                    {
                        streamResult = await StreamModelEndpointCoreAsync(
                            fallbackEndpoint,
                            fallbackModel,
                            _state.ApiKey,
                            messagesPayload,
                            toolsPayload,
                            onContentDelta: delta =>
                            {
                                currentAssistantMsg.Content += delta;
                                _state.NotifyStateChanged();
                            },
                            onReasoningDelta: delta =>
                            {
                                currentAssistantMsg.ReasoningContent = (currentAssistantMsg.ReasoningContent ?? "") + delta;
                                _state.NotifyStateChanged();
                            },
                            ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception fallbackEx)
                    {
                        _consoleService.AddLog("SYSTEM", $"Local fallback failed: {fallbackEx.Message}", isError: true);
                        _state.ExecutionStatus = AgentExecutionStatus.Error;
                        _state.StatusText = $"Endpoint Error: {fallbackEx.Message}";
                        currentAssistantMsg.IsError = true;
                        currentAssistantMsg.Content += $"### ⚠️ Model Endpoint Error\n\nCould not communicate with local fallback endpoint at `{fallbackEndpoint}`: `{fallbackEx.Message}`";
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _consoleService.AddLog("SYSTEM", $"Model API error: {ex.Message}", isError: true);
                    _state.ExecutionStatus = AgentExecutionStatus.Error;
                    _state.StatusText = $"Endpoint Error: {ex.Message}";
                    currentAssistantMsg.IsError = true;
                    currentAssistantMsg.Content = $"### ⚠️ Model Endpoint Error\n\nCould not communicate with model endpoint at `{targetEndpoint}`: `{ex.Message}`\n\nPlease ensure the model server is running and reachable, or switch to a different provider in the left pane.";
                    break;
                }

                if (streamResult == null) break;

                totalCompletionTokens += streamResult.CompletionTokens;
                if (streamResult.TotalTokens > 0)
                {
                    session.TotalTokensUsed += streamResult.TotalTokens;
                    _state.ActiveContextTokens = streamResult.TotalTokens;
                }

                // If tool calls were returned, execute each tool
                if (streamResult.ToolCalls.Count > 0 && _state.IsAgentMode)
                {
                    _state.ExecutionStatus = AgentExecutionStatus.ExecutingTools;

                    foreach (var toolCall in streamResult.ToolCalls)
                    {
                        if (ct.IsCancellationRequested) break;

                        var toolRecord = new AgentToolCallRecord
                        {
                            Id = toolCall.Id,
                            ToolName = toolCall.Name,
                            ArgumentsJson = toolCall.Arguments
                        };
                        currentAssistantMsg.ToolCalls.Add(toolRecord);

                        _state.StatusText = $"Executing {toolCall.Name}...";
                        _state.NotifyStateChanged();

                        var toolSw = Stopwatch.StartNew();
                        string toolResult = await ExecuteToolAsync(toolCall.Name, toolCall.Arguments, ct);
                        toolSw.Stop();

                        toolRecord.Duration = toolSw.Elapsed;
                        toolRecord.ResultOutput = toolResult;

                        // Add tool message to session history for next turn
                        session.Messages.Add(new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            Content = toolResult,
                            Timestamp = DateTime.Now
                        });
                    }

                    if (!ct.IsCancellationRequested)
                    {
                        _state.ExecutionStatus = AgentExecutionStatus.Thinking;
                        _state.NotifyStateChanged();
                    }
                }
                else
                {
                    // No more tool calls, ReAct cycle finishes
                    break;
                }
            }

            stopwatch.Stop();
            _state.LastTurnDuration = stopwatch.Elapsed;
            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

            if (totalCompletionTokens > 0 && elapsedSeconds > 0)
            {
                _state.LastTokensPerSecond = totalCompletionTokens / elapsedSeconds;
            }
            else
            {
                _state.LastTokensPerSecond = 0.0;
            }

            if (ct.IsCancellationRequested || _state.ExecutionStatus == AgentExecutionStatus.Cancelled)
            {
                _state.ExecutionStatus = AgentExecutionStatus.Cancelled;
                _state.StatusText = "Cancelled by user";
                if (currentAssistantMsg != null && !currentAssistantMsg.Content.Contains("Execution was cancelled"))
                {
                    currentAssistantMsg.Content += "\n\n*(Execution was cancelled)*";
                }
            }
            else if (_state.ExecutionStatus != AgentExecutionStatus.Error)
            {
                _state.ExecutionStatus = AgentExecutionStatus.Completed;
                _state.StatusText = _state.LastTokensPerSecond > 0 
                    ? $"Completed in {elapsedSeconds:F2}s ({_state.LastTokensPerSecond:F1} tok/s)"
                    : $"Completed in {elapsedSeconds:F2}s";
            }
        }
        catch (OperationCanceledException)
        {
            _state.ExecutionStatus = AgentExecutionStatus.Cancelled;
            _state.StatusText = "Cancelled by user";
            if (currentAssistantMsg != null)
            {
                currentAssistantMsg.Content += "\n\n*(Execution was cancelled)*";
            }
        }
        catch (Exception ex)
        {
            _state.ExecutionStatus = AgentExecutionStatus.Error;
            _state.StatusText = $"Error: {ex.Message}";
            if (currentAssistantMsg != null)
            {
                currentAssistantMsg.Content += $"\n\n**Agent Execution Error:** `{ex.Message}`";
            }
        }
        finally
        {
            _state.NotifyStateChanged();
        }
    }

    private async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        _consoleService.AddLog("AGENT", $"Tool Call: {toolName}({argumentsJson})");

        try
        {
            var root = ToolArgumentSanitizer.ParseAndSanitize(argumentsJson);

            switch (toolName.ToLowerInvariant())
            {
                case "run_command":
                    string cmd = root.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(cmd) && root.TryGetProperty("raw", out var rawCmdProp))
                    {
                        cmd = rawCmdProp.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(cmd)) return "Error: 'command' argument is required.";
                    
                    var (exitCode, output) = await _consoleService.ExecuteCommandAsync(cmd, "AGENT", ct);
                    return $"Exit Code: {exitCode}\nOutput:\n{output}";

                case "read_file":
                    string path = root.TryGetProperty("path", out var pProp) ? pProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(path) && root.TryGetProperty("raw", out var rawPathProp))
                    {
                        path = rawPathProp.GetString() ?? "";
                    }
                    string fullPath = Path.IsPathRooted(path) ? path : Path.Combine(_workspaceRoot, path);
                    if (!File.Exists(fullPath)) return $"Error: File '{path}' does not exist.";
                    
                    string content = await File.ReadAllTextAsync(fullPath, ct);
                    _docPreviewService.LoadFile(fullPath);
                    return content.Length > 8000 ? content[..8000] + "\n...(truncated)" : content;

                case "write_file":
                    string wPath = root.TryGetProperty("path", out var wpProp) ? wpProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(wPath) && root.TryGetProperty("raw", out var rawWPathProp))
                    {
                        wPath = rawWPathProp.GetString() ?? "";
                    }
                    string wContent = root.TryGetProperty("content", out var wcProp) ? wcProp.GetString() ?? "" : "";
                    string fullWPath = Path.IsPathRooted(wPath) ? wPath : Path.Combine(_workspaceRoot, wPath);
                    
                    string? dir = Path.GetDirectoryName(fullWPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    await File.WriteAllTextAsync(fullWPath, wContent, ct);
                    _docPreviewService.LoadFile(fullWPath);
                    _consoleService.AddLog("AGENT", $"File created/updated: {wPath} ({wContent.Length} chars)");
                    return $"Successfully wrote {wContent.Length} characters to '{wPath}'.";

                case "list_directory":
                    string dirPath = root.TryGetProperty("path", out var dpProp) ? dpProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(dirPath) && root.TryGetProperty("raw", out var rawDirProp))
                    {
                        dirPath = rawDirProp.GetString() ?? "";
                    }
                    string targetDir = string.IsNullOrEmpty(dirPath) ? _workspaceRoot : (Path.IsPathRooted(dirPath) ? dirPath : Path.Combine(_workspaceRoot, dirPath));
                    if (!Directory.Exists(targetDir)) return $"Error: Directory '{dirPath}' not found.";

                    var files = Directory.GetFileSystemEntries(targetDir);
                    var sb = new StringBuilder();
                    foreach (var f in files)
                    {
                        sb.AppendLine(Path.GetFileName(f) + (Directory.Exists(f) ? "/" : ""));
                    }
                    return sb.ToString();

                case "inspect_document":
                    string docFile = root.TryGetProperty("path", out var dfProp) ? dfProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(docFile) && root.TryGetProperty("raw", out var rawDocProp))
                    {
                        docFile = rawDocProp.GetString() ?? "";
                    }
                    _docPreviewService.LoadFile(docFile);
                    _state.ActiveRightTab = "doc";
                    return $"Document '{docFile}' loaded into DocTree Preview. Word Count: {_docPreviewService.ActiveDocument.WordCount}, Outline items: {_docPreviewService.ActiveDocument.Outline.Count}.";

                case "call_mcp_tool":
                    string server = root.TryGetProperty("server", out var sProp) ? sProp.GetString() ?? "" : "SharpTools";
                    string mcpTool = root.TryGetProperty("tool", out var tProp) ? tProp.GetString() ?? "" : "query";
                    string mcpArgs = root.TryGetProperty("arguments", out var aProp) ? aProp.ToString() : "{}";
                    return await _mcpManager.ExecuteMcpToolAsync(server, mcpTool, mcpArgs);

                default:
                    return $"Error: Unknown tool '{toolName}'.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Tool Execution Exception: {ex.Message}";
        }
    }

    private Task<LlmStreamResult> StreamModelEndpointAsync(
        List<JsonObject> messages,
        JsonArray tools,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta,
        CancellationToken ct)
    {
        return StreamModelEndpointCoreAsync(
            _state.EndpointUrl,
            _state.SelectedModel,
            _state.ApiKey,
            messages,
            tools,
            onContentDelta,
            onReasoningDelta,
            ct);
    }

    private async Task<LlmStreamResult> StreamModelEndpointCoreAsync(
        string endpointUrl,
        string modelName,
        string? apiKey,
        List<JsonObject> messages,
        JsonArray tools,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta,
        CancellationToken ct)
    {
        string endpoint = (endpointUrl ?? _state.EndpointUrl).TrimEnd('/');
        string url = endpoint.EndsWith("/chat/completions") ? endpoint : $"{endpoint}/chat/completions";

        var payload = new JsonObject
        {
            ["model"] = modelName ?? _state.SelectedModel,
            ["messages"] = new JsonArray(messages.Select(m => (JsonObject)m.DeepClone()).ToArray()),
            ["temperature"] = _state.Temperature,
            ["repetition_penalty"] = _state.RepetitionPenalty,
            ["max_tokens"] = _state.MaxTokens,
            ["stream"] = true
        };

        if (_state.IsAgentMode && tools.Count > 0)
        {
            payload["tools"] = (JsonArray)tools.DeepClone();
            payload["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        string effectiveKey = !string.IsNullOrEmpty(apiKey) ? apiKey : _state.ApiKey;
        if (!string.IsNullOrEmpty(effectiveKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", effectiveKey);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            string err = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Model API error {(int)response.StatusCode}: {err}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            string responseJson = await response.Content.ReadAsStringAsync(ct);
            return ParseNonStreamingResponse(responseJson, onContentDelta, onReasoningDelta);
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var toolAccumulators = new Dictionary<int, StreamingToolCallAccumulator>();
        var fullContent = new StringBuilder();
        var fullReasoning = new StringBuilder();
        int tokenCount = 0;
        int completionTokens = 0;
        int promptTokens = 0;
        int totalTokens = 0;
        bool inThinkBlock = false;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith(":")) continue;

            string lineTrimmed = line.Trim();
            if (!lineTrimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            string data = lineTrimmed.Substring(5).Trim();
            if (data == "[DONE]") break;
            if (string.IsNullOrEmpty(data)) continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
                {
                    if (usageElem.TryGetProperty("completion_tokens", out var ctProp) && ctProp.TryGetInt32(out int ctVal))
                        completionTokens = ctVal;
                    if (usageElem.TryGetProperty("prompt_tokens", out var ptProp) && ptProp.TryGetInt32(out int ptVal))
                        promptTokens = ptVal;
                    if (usageElem.TryGetProperty("total_tokens", out var ttProp) && ttProp.TryGetInt32(out int ttVal))
                        totalTokens = ttVal;
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var firstChoice = choices[0];
                if (!firstChoice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // 1. Check reasoning_content delta (DeepSeek-R1 / Qwen2.5)
                if (delta.TryGetProperty("reasoning_content", out var rcProp) && rcProp.ValueKind == JsonValueKind.String)
                {
                    string rToken = rcProp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(rToken))
                    {
                        tokenCount++;
                        fullReasoning.Append(rToken);
                        onReasoningDelta?.Invoke(rToken);
                    }
                }

                // 2. Check content delta
                if (delta.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String)
                {
                    string cToken = cProp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(cToken))
                    {
                        tokenCount++;

                        if (cToken.Contains("<think>"))
                        {
                            inThinkBlock = true;
                            int idx = cToken.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                            string before = cToken[..idx];
                            string after = cToken[(idx + 7)..];
                            if (!string.IsNullOrEmpty(before))
                            {
                                fullContent.Append(before);
                                onContentDelta?.Invoke(before);
                            }
                            if (after.Contains("</think>"))
                            {
                                inThinkBlock = false;
                                int endIdx = after.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                                string thinkPart = after[..endIdx];
                                string rest = after[(endIdx + 8)..];
                                if (!string.IsNullOrEmpty(thinkPart))
                                {
                                    fullReasoning.Append(thinkPart);
                                    onReasoningDelta?.Invoke(thinkPart);
                                }
                                if (!string.IsNullOrEmpty(rest))
                                {
                                    fullContent.Append(rest);
                                    onContentDelta?.Invoke(rest);
                                }
                            }
                            else if (!string.IsNullOrEmpty(after))
                            {
                                fullReasoning.Append(after);
                                onReasoningDelta?.Invoke(after);
                            }
                        }
                        else if (inThinkBlock)
                        {
                            if (cToken.Contains("</think>"))
                            {
                                inThinkBlock = false;
                                int endIdx = cToken.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                                string thinkPart = cToken[..endIdx];
                                string rest = cToken[(endIdx + 8)..];
                                if (!string.IsNullOrEmpty(thinkPart))
                                {
                                    fullReasoning.Append(thinkPart);
                                    onReasoningDelta?.Invoke(thinkPart);
                                }
                                if (!string.IsNullOrEmpty(rest))
                                {
                                    fullContent.Append(rest);
                                    onContentDelta?.Invoke(rest);
                                }
                            }
                            else
                            {
                                fullReasoning.Append(cToken);
                                onReasoningDelta?.Invoke(cToken);
                            }
                        }
                        else
                        {
                            fullContent.Append(cToken);
                            onContentDelta?.Invoke(cToken);
                        }
                    }
                }

                // 3. Tool call streaming deltas
                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        int index = tc.TryGetProperty("index", out var idxProp) && idxProp.TryGetInt32(out int idx) ? idx : 0;
                        if (!toolAccumulators.TryGetValue(index, out var acc))
                        {
                            acc = new StreamingToolCallAccumulator { Index = index };
                            toolAccumulators[index] = acc;
                        }

                        if (tc.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                        {
                            string idVal = idProp.GetString() ?? "";
                            if (!string.IsNullOrEmpty(idVal)) acc.Id = idVal;
                        }

                        if (tc.TryGetProperty("function", out var fnProp) && fnProp.ValueKind == JsonValueKind.Object)
                        {
                            if (fnProp.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String)
                            {
                                string nVal = nProp.GetString() ?? "";
                                if (!string.IsNullOrEmpty(nVal)) acc.Name += nVal;
                            }
                            if (fnProp.TryGetProperty("arguments", out var aProp) && aProp.ValueKind == JsonValueKind.String)
                            {
                                string aVal = aProp.GetString() ?? "";
                                if (!string.IsNullOrEmpty(aVal)) acc.ArgumentsBuilder.Append(aVal);
                            }
                        }
                    }
                }
            }
        }

        var finalToolCalls = toolAccumulators.Values
            .OrderBy(a => a.Index)
            .Select(a => new ToolCallItem
            {
                Id = string.IsNullOrEmpty(a.Id) ? $"call_{Guid.NewGuid():N}"[..12] : a.Id,
                Name = a.Name,
                Arguments = a.ArgumentsBuilder.ToString()
            })
            .Where(tc => !string.IsNullOrEmpty(tc.Name))
            .ToList();

        return new LlmStreamResult
        {
            Content = fullContent.ToString(),
            Reasoning = fullReasoning.ToString(),
            ToolCalls = finalToolCalls,
            TotalTokens = totalTokens > 0 ? totalTokens : tokenCount,
            CompletionTokens = completionTokens > 0 ? completionTokens : tokenCount,
            PromptTokens = promptTokens
        };
    }

    private static LlmStreamResult ParseNonStreamingResponse(
        string json,
        Action<string>? onContentDelta,
        Action<string>? onReasoningDelta)
    {
        var result = new LlmStreamResult();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
        {
            if (usageElem.TryGetProperty("completion_tokens", out var ctProp))
                result.CompletionTokens = ctProp.GetInt32();
            if (usageElem.TryGetProperty("prompt_tokens", out var ptProp))
                result.PromptTokens = ptProp.GetInt32();
            if (usageElem.TryGetProperty("total_tokens", out var ttProp))
                result.TotalTokens = ttProp.GetInt32();
        }

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var msg))
            {
                if (msg.TryGetProperty("reasoning_content", out var rProp) && rProp.ValueKind == JsonValueKind.String)
                {
                    result.Reasoning = rProp.GetString() ?? "";
                    if (!string.IsNullOrEmpty(result.Reasoning))
                    {
                        onReasoningDelta?.Invoke(result.Reasoning);
                    }
                }

                if (msg.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                {
                    string raw = contentProp.GetString() ?? "";
                    
                    int thinkStart = raw.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                    int thinkEnd = raw.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                    if (thinkStart >= 0 && thinkEnd > thinkStart)
                    {
                        string thinkPart = raw.Substring(thinkStart + 7, thinkEnd - (thinkStart + 7)).Trim();
                        result.Reasoning = (result.Reasoning + "\n" + thinkPart).Trim();
                        raw = (raw[..thinkStart] + raw[(thinkEnd + 8)..]).Trim();
                        onReasoningDelta?.Invoke(thinkPart);
                    }

                    result.Content = raw;
                    if (!string.IsNullOrEmpty(raw))
                    {
                        onContentDelta?.Invoke(raw);
                    }
                }

                if (msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        string id = tc.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : $"call_{Guid.NewGuid():N}"[..12];
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            string fnName = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            string fnArgs = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";

                            if (!string.IsNullOrEmpty(fnName))
                            {
                                result.ToolCalls.Add(new ToolCallItem
                                {
                                    Id = id,
                                    Name = fnName,
                                    Arguments = fnArgs
                                });
                            }
                        }
                    }
                }
            }
        }

        if (result.CompletionTokens == 0)
        {
            result.CompletionTokens = result.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        return result;
    }

    private List<JsonObject> BuildMessagesPayload(StudioSession session)
    {
        var list = new List<JsonObject>();

        // System prompt instructing the agent
        list.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = $@"You are the Glacier Antigravity Engineering Agent, an advanced pair programmer embedded in the Glacier .NET 10 Ecosystem.
Workspace Directory: {_workspaceRoot}
Capabilities:
- You have autonomous access to tools: run_command, read_file, write_file, list_directory, inspect_document, and call_mcp_tool.
- When running terminal commands, execute clean PowerShell/dotnet commands.
- Follow the ZERO SECRETS POLICY: Never embed credentials or API keys in code files.
- Always provide structured, elegant responses."
        });

        foreach (var msg in session.Messages)
        {
            if (msg.Role == "tool")
            {
                list.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = msg.ToolCallId ?? "call_0",
                    ["content"] = msg.Content
                });
            }
            else if (msg.Role == "assistant")
            {
                var assistantObj = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = string.IsNullOrEmpty(msg.Content) ? null : msg.Content
                };

                // ReAct message sequence fix: Assistant message must include tool_calls before role: tool
                if (msg.ToolCalls.Count > 0)
                {
                    var toolCallsArray = new JsonArray();
                    foreach (var tc in msg.ToolCalls)
                    {
                        toolCallsArray.Add(new JsonObject
                        {
                            ["id"] = string.IsNullOrEmpty(tc.Id) ? $"call_{Guid.NewGuid():N}"[..12] : tc.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = tc.ToolName,
                                ["arguments"] = tc.ArgumentsJson
                            }
                        });
                    }
                    assistantObj["tool_calls"] = toolCallsArray;
                }

                list.Add(assistantObj);
            }
            else
            {
                list.Add(new JsonObject
                {
                    ["role"] = msg.Role,
                    ["content"] = msg.Content
                });
            }
        }

        return list;
    }

    private static JsonArray GetToolsSchema()
    {
        var tools = new JsonArray();

        // 1. run_command
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "run_command",
                ["description"] = "Executes a shell or terminal command in the workspace and streams output to console.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["command"] = new JsonObject { ["type"] = "string", ["description"] = "Command line string to execute." }
                    },
                    ["required"] = new JsonArray { "command" }
                }
            }
        });

        // 2. read_file
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "read_file",
                ["description"] = "Reads file content from workspace and loads it into Document Preview.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative or absolute file path." }
                    },
                    ["required"] = new JsonArray { "path" }
                }
            }
        });

        // 3. write_file
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "write_file",
                ["description"] = "Writes or overwrites file content in workspace.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Target file path." },
                        ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Text content to write." }
                    },
                    ["required"] = new JsonArray { "path", "content" }
                }
            }
        });

        // 4. list_directory
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "list_directory",
                ["description"] = "Lists files and subdirectories in workspace.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Directory path (leave empty for root)." }
                    }
                }
            }
        });

        // 5. inspect_document
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "inspect_document",
                ["description"] = "Parses Markdown or source file using Glacier.DocTree and displays outline in preview pane.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "File path to inspect." }
                    },
                    ["required"] = new JsonArray { "path" }
                }
            }
        });

        // 6. call_mcp_tool
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "call_mcp_tool",
                ["description"] = "Calls an MCP tool on an ecosystem server (e.g. SharpTools, GlacierVectorEngine, GlacierGraphEngine).",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server"] = new JsonObject { ["type"] = "string", ["description"] = "MCP server name." },
                        ["tool"] = new JsonObject { ["type"] = "string", ["description"] = "Tool name." },
                        ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "Tool arguments." }
                    },
                    ["required"] = new JsonArray { "server", "tool" }
                }
            }
        });

        return tools;
    }
}
