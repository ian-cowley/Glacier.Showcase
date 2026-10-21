using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Glacier.DocTree.Core;

namespace Glacier.Showcase.Services.GlacierStudio;

public enum ModelServerProvider
{
    LocalOllama,
    LocalGlacierInference,
    LocalGlacierDistributed,
    ClusterBeastCoder,
    LocalLMStudio,
    LocalCustom,
    RemoteGemini,
    RemoteOpenAI,
    RemoteCustom
}

public enum AgentExecutionStatus
{
    Idle,
    Thinking,
    ExecutingTool,
    ExecutingTools = ExecutingTool,
    StreamingResponse,
    Completed,
    Error,
    Cancelled
}

public class StudioSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "New Conversation";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string ModelSnapshot { get; set; } = "Ollama: llama3.2";
    public List<ChatMessage> Messages { get; set; } = new();
    public int TotalTokensUsed { get; set; }
}

public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Role { get; set; } = "user"; // user, assistant, system, tool
    public string Content { get; set; } = "";
    public string? ThinkingContent { get; set; }
    public string? ReasoningContent
    {
        get => ThinkingContent;
        set => ThinkingContent = value;
    }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public List<AgentToolCallRecord> ToolCalls { get; set; } = new();
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public bool IsError { get; set; }
}

public class AgentToolCallRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string ToolName { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
    public string? ResultOutput { get; set; }
    public bool IsSuccess { get; set; } = true;
    public TimeSpan Duration { get; set; }
    public bool IsExpanded { get; set; } = true;
}

public class ConsoleLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Source { get; set; } = "SYSTEM"; // USER, AGENT, SYSTEM, MCP
    public string Message { get; set; } = "";
    public bool IsError { get; set; }
    public string? Command { get; set; }

    public string HtmlMessage => GlacierAnsiParser.ToHtml(Message);
}

public class DocumentPreviewModel
{
    public string Title { get; set; } = "Document";
    public string FilePath { get; set; } = "";
    public string RawContent { get; set; } = "";
    public string FormattedHtml { get; set; } = "";
    public DocNode? AstRoot { get; set; }
    public List<DocOutlineItem> Outline { get; set; } = new();
    public int WordCount { get; set; }
    public int EstimatedTokens { get; set; }
    public DateTime LastModified { get; set; } = DateTime.Now;
}

public class DocOutlineItem
{
    public int Level { get; set; }
    public string Title { get; set; } = "";
    public int LineNumber { get; set; }
}

public class McpServerDefinition
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public List<string> DisabledTools { get; set; } = new();
    public bool IsDisabled { get; set; }
    public bool IsActive { get; set; }
    public List<string> AvailableTools { get; set; } = new();
    public string StatusMessage { get; set; } = "Ready";
}

public class McpToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonElement InputSchema { get; set; }
}

public class McpRpcException : Exception
{
    public int Code { get; }

    public McpRpcException(int code, string message) : base($"JSON-RPC Error [{code}]: {message}")
    {
        Code = code;
    }
}

public static class GlacierWorkspaceResolver
{
    private static string? s_resolvedRoot;

    public static string ResolveWorkspaceRoot()
    {
        if (!string.IsNullOrEmpty(s_resolvedRoot) && Directory.Exists(s_resolvedRoot))
        {
            return s_resolvedRoot;
        }

        var envRoot = Environment.GetEnvironmentVariable("GLACIER_WORKSPACE_ROOT");
        if (!string.IsNullOrEmpty(envRoot) && Directory.Exists(envRoot))
        {
            s_resolvedRoot = Path.GetFullPath(envRoot);
            return s_resolvedRoot;
        }

        // Search upwards from CurrentDirectory
        string current = Directory.GetCurrentDirectory();
        var dir = new DirectoryInfo(current);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || 
                File.Exists(Path.Combine(dir.FullName, "Glacier.sln")) ||
                File.Exists(Path.Combine(dir.FullName, "PolarsPlus.sln")))
            {
                s_resolvedRoot = dir.FullName;
                return s_resolvedRoot;
            }
            dir = dir.Parent;
        }

        // Search upwards from AppDomain.CurrentDomain.BaseDirectory
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || 
                File.Exists(Path.Combine(dir.FullName, "Glacier.sln")) ||
                File.Exists(Path.Combine(dir.FullName, "PolarsPlus.sln")))
            {
                s_resolvedRoot = dir.FullName;
                return s_resolvedRoot;
            }
            dir = dir.Parent;
        }

        s_resolvedRoot = Path.GetFullPath(current);
        return s_resolvedRoot;
    }
}

public class ToolParameterSchema
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    [JsonPropertyName("properties")]
    public Dictionary<string, JsonElement>? Properties { get; set; }

    [JsonPropertyName("required")]
    public List<string>? Required { get; set; }
}

public class StreamingToolCallAccumulator
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public StringBuilder ArgumentsBuilder { get; } = new();
}

public class ToolCallItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

public class LlmStreamResult
{
    public string Content { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public List<ToolCallItem> ToolCalls { get; set; } = new();
    public int TotalTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int PromptTokens { get; set; }
}

public static class ToolArgumentSanitizer
{
    public static JsonElement ParseAndSanitize(string rawArgs)
    {
        if (string.IsNullOrWhiteSpace(rawArgs))
        {
            using var emptyDoc = JsonDocument.Parse("{}");
            return emptyDoc.RootElement.Clone();
        }

        string trimmed = rawArgs.Trim();

        // 1. Strip markdown code fences (```json ... ``` or ``` ... ```)
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(7);
        }
        else if (trimmed.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(3);
        }

        if (trimmed.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 3);
        }

        trimmed = trimmed.Trim();

        // 2. Sanitize unescaped Windows paths and backslashes BEFORE JsonDocument.Parse
        // ensuring unescaped backslashes preceding characters like \t, \n, \r (e.g. C:\temp\file.txt, C:\notes.txt)
        // are escaped to \\ before JSON parsing so they are not parsed as whitespace control characters.
        string sanitized = FixWindowsPathsInJson(trimmed);
        try
        {
            using var doc = JsonDocument.Parse(sanitized);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            // Proceed to general unescaped backslash replacement
        }

        // 3. Try general unescaped backslash replacement if still failing
        try
        {
            string generalEscaped = Regex.Replace(sanitized, @"(?<!\\)\\(?![\\""/bfnrtu])", "\\\\");
            using var doc = JsonDocument.Parse(generalEscaped);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            // Fall through to fallback
        }

        // 4. Resilient fallback for raw non-JSON arguments
        var fallbackObj = new JsonObject
        {
            ["raw"] = trimmed,
            ["command"] = trimmed,
            ["path"] = trimmed,
            ["arguments"] = trimmed
        };

        using var fallbackDoc = JsonDocument.Parse(fallbackObj.ToJsonString());
        return fallbackDoc.RootElement.Clone();
    }

    private static string FixWindowsPathsInJson(string json)
    {
        // Replace single backslashes in Windows drive paths: e.g. C:\Users\... -> C:\\Users\\...
        return Regex.Replace(json, @"([a-zA-Z]:)((\\[^""\\]+)+)", match =>
        {
            string drive = match.Groups[1].Value;
            string rest = match.Groups[2].Value;
            string fixedRest = Regex.Replace(rest, @"(?<!\\)\\(?!\\)", "\\\\");
            return drive + fixedRest;
        });
    }
}

public static class GlacierAnsiParser
{
    private static readonly Regex AnsiRegex = new(@"\x1B\[([0-9;]*)m", RegexOptions.Compiled);

    public static string ToHtml(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return "";

        var matches = AnsiRegex.Matches(rawText);
        if (matches.Count == 0)
        {
            return System.Net.WebUtility.HtmlEncode(rawText);
        }

        var sb = new StringBuilder();
        int lastIndex = 0;
        bool inSpan = false;

        string currentFg = "";
        string currentBg = "";
        bool isBold = false;
        bool isItalic = false;
        bool isUnderline = false;

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                string textSegment = rawText.Substring(lastIndex, match.Index - lastIndex);
                sb.Append(System.Net.WebUtility.HtmlEncode(textSegment));
            }

            lastIndex = match.Index + match.Length;
            string codeStr = match.Groups[1].Value;
            string[] codes = string.IsNullOrEmpty(codeStr) ? ["0"] : codeStr.Split(';');

            for (int i = 0; i < codes.Length; i++)
            {
                if (!int.TryParse(codes[i], out int code)) continue;

                switch (code)
                {
                    case 0: // Reset
                        currentFg = "";
                        currentBg = "";
                        isBold = false;
                        isItalic = false;
                        isUnderline = false;
                        break;
                    case 1:
                        isBold = true;
                        break;
                    case 3:
                        isItalic = true;
                        break;
                    case 4:
                        isUnderline = true;
                        break;
                    case 22:
                        isBold = false;
                        break;
                    case 23:
                        isItalic = false;
                        break;
                    case 24:
                        isUnderline = false;
                        break;
                    // Standard foreground colors
                    case 30: currentFg = "#1e293b"; break;
                    case 31: currentFg = "#ef4444"; break;
                    case 32: currentFg = "#22c55e"; break;
                    case 33: currentFg = "#eab308"; break;
                    case 34: currentFg = "#3b82f6"; break;
                    case 35: currentFg = "#d946ef"; break;
                    case 36: currentFg = "#06b6d4"; break;
                    case 37: currentFg = "#f8fafc"; break;
                    case 39: currentFg = ""; break;
                    // High-intensity foreground colors
                    case 90: currentFg = "#94a3b8"; break;
                    case 91: currentFg = "#f87171"; break;
                    case 92: currentFg = "#4ade80"; break;
                    case 93: currentFg = "#fde047"; break;
                    case 94: currentFg = "#60a5fa"; break;
                    case 95: currentFg = "#f472b6"; break;
                    case 96: currentFg = "#22d3ee"; break;
                    case 97: currentFg = "#ffffff"; break;
                    // Standard background colors
                    case 40: currentBg = "#0f172a"; break;
                    case 41: currentBg = "#7f1d1d"; break;
                    case 42: currentBg = "#14532d"; break;
                    case 43: currentBg = "#713f12"; break;
                    case 44: currentBg = "#1e3a8a"; break;
                    case 45: currentBg = "#701a75"; break;
                    case 46: currentBg = "#164e63"; break;
                    case 47: currentBg = "#334155"; break;
                    case 49: currentBg = ""; break;
                }
            }

            if (inSpan)
            {
                sb.Append("</span>");
                inSpan = false;
            }

            var styleParts = new List<string>();
            if (!string.IsNullOrEmpty(currentFg)) styleParts.Add($"color:{currentFg}");
            if (!string.IsNullOrEmpty(currentBg)) styleParts.Add($"background-color:{currentBg}");
            if (isBold) styleParts.Add("font-weight:bold");
            if (isItalic) styleParts.Add("font-style:italic");
            if (isUnderline) styleParts.Add("text-decoration:underline");

            if (styleParts.Count > 0)
            {
                sb.Append($"<span style=\"{string.Join(";", styleParts)}\">");
                inSpan = true;
            }
        }

        if (lastIndex < rawText.Length)
        {
            string tail = rawText.Substring(lastIndex);
            sb.Append(System.Net.WebUtility.HtmlEncode(tail));
        }

        if (inSpan)
        {
            sb.Append("</span>");
        }

        return sb.ToString();
    }
}

public static class GlacierMarkdownRenderer
{
    private static readonly Regex CodeBlockRegex = new(@"```([a-zA-Z0-9_\-\.]*)\r?\n([\s\S]*?)\r?\n?```", RegexOptions.Compiled);
    private static readonly Regex TableRegex = new(@"(?:^|\n)(\|[^\n]+\|\r?\n\|[-:\s|]+\|\r?\n(?:\|[^\n]+\|\r?\n?)+)", RegexOptions.Compiled);
    private static readonly Regex HeaderRegex = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex BlockquoteRegex = new(@"(?:^|\n)((?:>\s*[^\n]+\r?\n?)+)", RegexOptions.Compiled);
    private static readonly Regex UnorderedListRegex = new(@"(?:^|\n)((?:[\*\-]\s+[^\n]+\r?\n?)+)", RegexOptions.Compiled);
    private static readonly Regex OrderedListRegex = new(@"(?:^|\n)((?:\d+\.\s+[^\n]+\r?\n?)+)", RegexOptions.Compiled);

    public static string Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var placeholders = new Dictionary<string, string>();
        int placeholderIndex = 0;

        // 1. Extract and format code blocks
        string processed = CodeBlockRegex.Replace(markdown, match =>
        {
            string lang = match.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(lang)) lang = "text";
            string code = match.Groups[2].Value;

            string escapedCode = System.Net.WebUtility.HtmlEncode(code);
            string key = $"__CODE_BLOCK_{placeholderIndex++}__";

            string card = $"<div class=\"gs-code-block\"><div class=\"gs-code-header\"><span class=\"gs-code-lang\">{System.Net.WebUtility.HtmlEncode(lang)}</span><button class=\"gs-code-copy-btn\" onclick=\"const c = this.closest('.gs-code-block').querySelector('code').innerText; navigator.clipboard.writeText(c); this.innerText='Copied!'; setTimeout(()=>this.innerText='Copy', 1500);\">Copy</button></div><pre><code class=\"language-{System.Net.WebUtility.HtmlEncode(lang)}\">{escapedCode}</code></pre></div>";

            placeholders[key] = card;
            return key;
        });

        // 2. Extract and format tables
        processed = TableRegex.Replace(processed, match =>
        {
            string tableText = match.Groups[1].Value.Trim();
            string tableHtml = FormatMarkdownTable(tableText);
            string key = $"__TABLE_BLOCK_{placeholderIndex++}__";
            placeholders[key] = tableHtml;
            return "\n" + key + "\n";
        });

        // 3. Blockquotes
        processed = BlockquoteRegex.Replace(processed, match =>
        {
            string bqText = match.Groups[1].Value;
            var lines = bqText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var innerSb = new StringBuilder();
            foreach (var line in lines)
            {
                string cleanLine = line.TrimStart();
                if (cleanLine.StartsWith(">")) cleanLine = cleanLine.Substring(1).TrimStart();
                innerSb.Append(FormatInlineElements(cleanLine)).Append("<br/>");
            }
            string key = $"__BQ_BLOCK_{placeholderIndex++}__";
            placeholders[key] = $"<blockquote class=\"gs-md-blockquote\">{innerSb}</blockquote>";
            return "\n" + key + "\n";
        });

        // 4. Unordered Lists
        processed = UnorderedListRegex.Replace(processed, match =>
        {
            string listText = match.Groups[1].Value;
            var lines = listText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var innerSb = new StringBuilder("<ul class=\"gs-md-ul\">");
            foreach (var line in lines)
            {
                string cleanLine = line.TrimStart();
                if (cleanLine.StartsWith("* ") || cleanLine.StartsWith("- "))
                {
                    cleanLine = cleanLine.Substring(2);
                }
                innerSb.Append("<li>").Append(FormatInlineElements(cleanLine)).Append("</li>");
            }
            innerSb.Append("</ul>");
            string key = $"__UL_BLOCK_{placeholderIndex++}__";
            placeholders[key] = innerSb.ToString();
            return "\n" + key + "\n";
        });

        // 5. Ordered Lists
        processed = OrderedListRegex.Replace(processed, match =>
        {
            string listText = match.Groups[1].Value;
            var lines = listText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var innerSb = new StringBuilder("<ol class=\"gs-md-ol\">");
            foreach (var line in lines)
            {
                string cleanLine = line.TrimStart();
                int dotIdx = cleanLine.IndexOf('.');
                if (dotIdx > 0 && dotIdx < 5)
                {
                    cleanLine = cleanLine.Substring(dotIdx + 1).TrimStart();
                }
                innerSb.Append("<li>").Append(FormatInlineElements(cleanLine)).Append("</li>");
            }
            innerSb.Append("</ol>");
            string key = $"__OL_BLOCK_{placeholderIndex++}__";
            placeholders[key] = innerSb.ToString();
            return "\n" + key + "\n";
        });

        // 6. Headers
        processed = HeaderRegex.Replace(processed, match =>
        {
            int level = match.Groups[1].Value.Length;
            string text = match.Groups[2].Value.Trim();
            string inlineFormatted = FormatInlineElements(text);
            return $"<h{level} class=\"gs-md-h{level}\">{inlineFormatted}</h{level}>";
        });

        // 7. Horizontal Rules
        processed = Regex.Replace(processed, @"^(?:---+|\*\*\*+|___+)\s*$", "<hr class=\"gs-md-hr\"/>", RegexOptions.Multiline);

        // 8. Format general paragraphs and inline elements
        var finalSb = new StringBuilder();
        var paragraphLines = processed.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        foreach (var line in paragraphLines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("__CODE_BLOCK_") ||
                trimmed.StartsWith("__TABLE_BLOCK_") ||
                trimmed.StartsWith("__BQ_BLOCK_") ||
                trimmed.StartsWith("__UL_BLOCK_") ||
                trimmed.StartsWith("__OL_BLOCK_") ||
                trimmed.StartsWith("<h") ||
                trimmed.StartsWith("<hr"))
            {
                finalSb.AppendLine(trimmed);
            }
            else if (string.IsNullOrEmpty(trimmed))
            {
                finalSb.AppendLine("<div class=\"gs-md-spacing\"></div>");
            }
            else
            {
                finalSb.Append("<p class=\"gs-md-p\">").Append(FormatInlineElements(line)).AppendLine("</p>");
            }
        }

        string result = finalSb.ToString();

        // 9. Reinsert placeholders
        foreach (var kvp in placeholders)
        {
            result = result.Replace(kvp.Key, kvp.Value);
            result = result.Replace($"<p class=\"gs-md-p\">{kvp.Key}</p>", kvp.Value);
        }

        return result;
    }

    private static string FormatMarkdownTable(string tableText)
    {
        var lines = tableText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return System.Net.WebUtility.HtmlEncode(tableText);

        var headerCols = lines[0].Split('|', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder("<div class=\"gs-table-wrapper\"><table class=\"gs-markdown-table\"><thead><tr>");

        foreach (var col in headerCols)
        {
            sb.Append("<th>").Append(FormatInlineElements(col.Trim())).Append("</th>");
        }
        sb.Append("</tr></thead><tbody>");

        // Skip lines[1] delimiter
        for (int i = 2; i < lines.Length; i++)
        {
            var rowCols = lines[i].Split('|', StringSplitOptions.RemoveEmptyEntries);
            sb.Append("<tr>");
            for (int c = 0; c < headerCols.Length; c++)
            {
                string cellText = c < rowCols.Length ? rowCols[c].Trim() : "";
                sb.Append("<td>").Append(FormatInlineElements(cellText)).Append("</td>");
            }
            sb.Append("</tr>");
        }

        sb.Append("</tbody></table></div>");
        return sb.ToString();
    }

    private static string FormatInlineElements(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // First HTML encode bare text
        string encoded = System.Net.WebUtility.HtmlEncode(text);

        // Bold: **text** or __text__
        encoded = Regex.Replace(encoded, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        encoded = Regex.Replace(encoded, @"__(.+?)__", "<strong>$1</strong>");

        // Strikethrough: ~~text~~
        encoded = Regex.Replace(encoded, @"~~(.+?)~~", "<del>$1</del>");

        // Inline code: `code`
        encoded = Regex.Replace(encoded, @"`([^`]+)`", "<code class=\"gs-inline-code\">$1</code>");

        // Italic: *text* or _text_
        encoded = Regex.Replace(encoded, @"\*(.+?)\*", "<em>$1</em>");
        encoded = Regex.Replace(encoded, @"(?<!\w)_(.+?)_(?!\w)", "<em>$1</em>");

        // Links: [title](url)
        encoded = Regex.Replace(encoded, @"\[([^\]]+)\]\((https?://[^\s\)]+)\)", "<a href=\"$2\" target=\"_blank\" rel=\"noopener noreferrer\" class=\"gs-md-link\">$1</a>");

        return encoded;
    }
}

