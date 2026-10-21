using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using Glacier.Inference.Gpu;
using Glacier.Inference.Hardware;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

public class GlacierStudioM3ChallengerTests
{
    // =========================================================================
    // 1. GlacierDocTreePreviewService Line Navigation & AST Outline
    // =========================================================================

    [Fact]
    public void DocTree_MultiLineDocument_AccuratelyMaps1BasedLineNumbers()
    {
        var service = new GlacierDocTreePreviewService();
        string markdown = @"# Document Title
Line 2 text
Line 3 text

## Section 1: Overview
Line 6 text
Line 7 text

### SubSection 1.1: Deep Dive
Line 10 text

#### SubSubSection 1.1.1: Micro Specs
Line 13 text

##### SubSubSub 1.1.1.1: Nano
Line 16 text

###### Level 6 Deepest
Line 19 text

## Section 2: Summary
Line 22 text";

        service.SetDocument("MultiLine.md", markdown, "MultiLine.md");
        var doc = service.ActiveDocument;

        Assert.NotNull(doc);
        Assert.Equal(7, doc.Outline.Count);

        // Header 1 at Line 1
        Assert.Equal("Document Title", doc.Outline[0].Title);
        Assert.Equal(1, doc.Outline[0].Level);
        Assert.Equal(1, doc.Outline[0].LineNumber);

        // Header 2 at Line 5
        Assert.Equal("Section 1: Overview", doc.Outline[1].Title);
        Assert.Equal(2, doc.Outline[1].Level);
        Assert.Equal(5, doc.Outline[1].LineNumber);

        // Header 3 at Line 9
        Assert.Equal("SubSection 1.1: Deep Dive", doc.Outline[2].Title);
        Assert.Equal(3, doc.Outline[2].Level);
        Assert.Equal(9, doc.Outline[2].LineNumber);

        // Header 4 at Line 12
        Assert.Equal("SubSubSection 1.1.1: Micro Specs", doc.Outline[3].Title);
        Assert.Equal(4, doc.Outline[3].Level);
        Assert.Equal(12, doc.Outline[3].LineNumber);

        // Header 5 at Line 15
        Assert.Equal("SubSubSub 1.1.1.1: Nano", doc.Outline[4].Title);
        Assert.Equal(5, doc.Outline[4].Level);
        Assert.Equal(15, doc.Outline[4].LineNumber);

        // Header 6 at Line 18
        Assert.Equal("Level 6 Deepest", doc.Outline[5].Title);
        Assert.Equal(6, doc.Outline[5].Level);
        Assert.Equal(18, doc.Outline[5].LineNumber);

        // Header 2 at Line 21
        Assert.Equal("Section 2: Summary", doc.Outline[6].Title);
        Assert.Equal(2, doc.Outline[6].Level);
        Assert.Equal(21, doc.Outline[6].LineNumber);
    }

    [Fact]
    public void DocTree_FrontmatterDocument_CorrectlyTracksPostFrontmatterLines()
    {
        var service = new GlacierDocTreePreviewService();
        string markdown = @"---
title: Production Specification
version: 1.0.4
author: Glacier Architecture Team
tags:
  - net10
  - high-performance
---

# Real Main Title
Line 11 body content

## Architecture Components
Line 14 body content";

        service.SetDocument("FrontmatterDoc.md", markdown, "FrontmatterDoc.md");
        var doc = service.ActiveDocument;

        Assert.NotNull(doc);
        Assert.Equal(2, doc.Outline.Count);

        Assert.Equal("Real Main Title", doc.Outline[0].Title);
        Assert.Equal(1, doc.Outline[0].Level);
        Assert.Equal(10, doc.Outline[0].LineNumber);

        Assert.Equal("Architecture Components", doc.Outline[1].Title);
        Assert.Equal(2, doc.Outline[1].Level);
        Assert.Equal(13, doc.Outline[1].LineNumber);
    }

    [Fact]
    public void DocTree_HeadersWithCodeFences_DoesNotTreatCodeHashesAsHeaders()
    {
        var service = new GlacierDocTreePreviewService();
        string markdown = @"# C# Source Header
Line 2 description

```csharp
#define DEBUG
#region Helper
public class Program
{
    // # Not a markdown heading
    public static void Main() {}
}
#endregion
```

## Post-Code Section
Line 17 text

```python
# Python comment that starts with hash
# Another comment
def test():
    pass
```

### Final Conclusion
Line 28 text";

        service.SetDocument("CodeFenceDoc.md", markdown, "CodeFenceDoc.md");
        var doc = service.ActiveDocument;

        Assert.NotNull(doc);
        Assert.Equal(3, doc.Outline.Count);

        // Header 1 at line 1
        Assert.Equal("C# Source Header", doc.Outline[0].Title);
        Assert.Equal(1, doc.Outline[0].Level);
        Assert.Equal(1, doc.Outline[0].LineNumber);

        // Header 2 at line 15
        Assert.Equal("Post-Code Section", doc.Outline[1].Title);
        Assert.Equal(2, doc.Outline[1].Level);
        Assert.Equal(15, doc.Outline[1].LineNumber);

        // Header 3 at line 25
        Assert.Equal("Final Conclusion", doc.Outline[2].Title);
        Assert.Equal(3, doc.Outline[2].Level);
        Assert.Equal(25, doc.Outline[2].LineNumber);
    }

    [Fact]
    public void DocTree_DuplicateHeadingTitles_SequentiallyDispatchesLineNumbers()
    {
        var service = new GlacierDocTreePreviewService();
        string markdown = @"# Module Alpha
Alpha details

## Overview
Alpha overview text (line 5)

# Module Beta
Beta details

## Overview
Beta overview text (line 12)

# Module Gamma
Gamma details

## Overview
Gamma overview text (line 19)";

        service.SetDocument("Duplicates.md", markdown, "Duplicates.md");
        var doc = service.ActiveDocument;

        Assert.NotNull(doc);
        Assert.Equal(6, doc.Outline.Count);

        Assert.Equal("Module Alpha", doc.Outline[0].Title);
        Assert.Equal(1, doc.Outline[0].LineNumber);

        Assert.Equal("Overview", doc.Outline[1].Title);
        Assert.Equal(4, doc.Outline[1].LineNumber);

        Assert.Equal("Module Beta", doc.Outline[2].Title);
        Assert.Equal(7, doc.Outline[2].LineNumber);

        Assert.Equal("Overview", doc.Outline[3].Title);
        Assert.Equal(10, doc.Outline[3].LineNumber);

        Assert.Equal("Module Gamma", doc.Outline[4].Title);
        Assert.Equal(13, doc.Outline[4].LineNumber);

        Assert.Equal("Overview", doc.Outline[5].Title);
        Assert.Equal(16, doc.Outline[5].LineNumber);
    }

    [Fact]
    public void DocTree_FormattedHtml_InjectsHeadingAnchorAttributes()
    {
        var service = new GlacierDocTreePreviewService();
        string markdown = @"# Root Title
Body text

## Sub Heading
Sub text";

        service.SetDocument("Anchors.md", markdown, "Anchors.md");
        var doc = service.ActiveDocument;

        Assert.NotNull(doc);
        Assert.Contains("id=\"heading-line-1\"", doc.FormattedHtml);
        Assert.Contains("data-line=\"1\"", doc.FormattedHtml);
        Assert.Contains("gs-nav-heading", doc.FormattedHtml);

        Assert.Contains("id=\"heading-line-4\"", doc.FormattedHtml);
        Assert.Contains("data-line=\"4\"", doc.FormattedHtml);
    }

    // =========================================================================
    // 2. GlacierAnsiParser Stress Testing
    // =========================================================================

    [Theory]
    [InlineData("\u001b[30mBlack\u001b[0m", "#1e293b", "Black")]
    [InlineData("\u001b[31mRed\u001b[0m", "#ef4444", "Red")]
    [InlineData("\u001b[32mGreen\u001b[0m", "#22c55e", "Green")]
    [InlineData("\u001b[33mYellow\u001b[0m", "#eab308", "Yellow")]
    [InlineData("\u001b[34mBlue\u001b[0m", "#3b82f6", "Blue")]
    [InlineData("\u001b[35mMagenta\u001b[0m", "#d946ef", "Magenta")]
    [InlineData("\u001b[36mCyan\u001b[0m", "#06b6d4", "Cyan")]
    [InlineData("\u001b[37mWhite\u001b[0m", "#f8fafc", "White")]
    public void AnsiParser_StandardForegroundColors30To37_RenderCorrectHexColors(
        string input, string expectedColor, string expectedText)
    {
        string html = GlacierAnsiParser.ToHtml(input);
        Assert.Contains($"color:{expectedColor}", html);
        Assert.Contains(expectedText, html);
    }

    [Theory]
    [InlineData("\u001b[90mHiBlack\u001b[0m", "#94a3b8", "HiBlack")]
    [InlineData("\u001b[91mHiRed\u001b[0m", "#f87171", "HiRed")]
    [InlineData("\u001b[92mHiGreen\u001b[0m", "#4ade80", "HiGreen")]
    [InlineData("\u001b[93mHiYellow\u001b[0m", "#fde047", "HiYellow")]
    [InlineData("\u001b[94mHiBlue\u001b[0m", "#60a5fa", "HiBlue")]
    [InlineData("\u001b[95mHiMagenta\u001b[0m", "#f472b6", "HiMagenta")]
    [InlineData("\u001b[96mHiCyan\u001b[0m", "#22d3ee", "HiCyan")]
    [InlineData("\u001b[97mHiWhite\u001b[0m", "#ffffff", "HiWhite")]
    public void AnsiParser_HighIntensityColors90To97_RenderCorrectHexColors(
        string input, string expectedColor, string expectedText)
    {
        string html = GlacierAnsiParser.ToHtml(input);
        Assert.Contains($"color:{expectedColor}", html);
        Assert.Contains(expectedText, html);
    }

    [Fact]
    public void AnsiParser_MultipleParameters_CombinesStylesCorrectly()
    {
        // Bold Green: \u001b[1;32m
        string boldGreen = GlacierAnsiParser.ToHtml("\u001b[1;32mBold Green Text\u001b[0m");
        Assert.Contains("color:#22c55e", boldGreen);
        Assert.Contains("font-weight:bold", boldGreen);
        Assert.Contains("Bold Green Text", boldGreen);

        // Italic Underline Red: \u001b[3;4;31m
        string italicUnderlineRed = GlacierAnsiParser.ToHtml("\u001b[3;4;31mItalic Underline Red\u001b[0m");
        Assert.Contains("color:#ef4444", italicUnderlineRed);
        Assert.Contains("font-style:italic", italicUnderlineRed);
        Assert.Contains("text-decoration:underline", italicUnderlineRed);
        Assert.Contains("Italic Underline Red", italicUnderlineRed);

        // Bold Yellow on Blue background: \u001b[1;33;44m
        string boldYellowBlueBg = GlacierAnsiParser.ToHtml("\u001b[1;33;44mWarning\u001b[0m");
        Assert.Contains("color:#eab308", boldYellowBlueBg);
        Assert.Contains("background-color:#1e3a8a", boldYellowBlueBg);
        Assert.Contains("font-weight:bold", boldYellowBlueBg);
        Assert.Contains("Warning", boldYellowBlueBg);
    }

    [Fact]
    public void AnsiParser_ResetAndClearingSequences_ResetsStateCleanly()
    {
        string input = "\u001b[1;32mGreenBold\u001b[0mPlain\u001b[31mRed\u001b[22mStillRedNotBold\u001b[0m";
        string html = GlacierAnsiParser.ToHtml(input);

        Assert.Contains("GreenBold", html);
        Assert.Contains("Plain", html);
        Assert.Contains("Red", html);
        Assert.Contains("StillRedNotBold", html);
    }

    [Fact]
    public void AnsiParser_BrokenEscapeSequences_DoesNotThrowAndHtmlEncodesSafely()
    {
        // Unclosed escape sequences
        string broken1 = "Prefix \u001b[1;32 Text without m suffix";
        string html1 = GlacierAnsiParser.ToHtml(broken1);
        Assert.NotNull(html1);
        Assert.Contains("Text without m suffix", html1);

        // Broken escape with other letters
        string broken2 = "Error \u001b[31X invalid command";
        string html2 = GlacierAnsiParser.ToHtml(broken2);
        Assert.NotNull(html2);
        Assert.Contains("invalid command", html2);

        // Bare escape character
        string broken3 = "Bare escape: \u001b and test";
        string html3 = GlacierAnsiParser.ToHtml(broken3);
        Assert.NotNull(html3);
        Assert.Contains("Bare escape:", html3);
    }

    [Fact]
    public void AnsiParser_256ColorAnd24BitRgb_SurvivesWithoutExceptions()
    {
        // 256-color sequence: \u001b[38;5;196m (bright red in 256-color table)
        string seq256 = "\u001b[38;5;196m256-Color String\u001b[0m";
        string html256 = GlacierAnsiParser.ToHtml(seq256);
        Assert.NotNull(html256);
        Assert.Contains("256-Color String", html256);

        // 24-bit TrueColor RGB: \u001b[38;2;255;128;64m
        string rgbSeq = "\u001b[38;2;255;128;64mTrueColor String\u001b[0m";
        string htmlRgb = GlacierAnsiParser.ToHtml(rgbSeq);
        Assert.NotNull(htmlRgb);
        Assert.Contains("TrueColor String", htmlRgb);
    }

    [Fact]
    public void AnsiParser_HtmlEncodingHygiene_PreventsXssInjection()
    {
        string raw = "\u001b[31m<script>alert('xss')</script> & 'quotes'\u001b[0m";
        string html = GlacierAnsiParser.ToHtml(raw);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
    }

    // =========================================================================
    // 3. GlacierConsoleService CWD Persistence & Shell Resolution
    // =========================================================================

    [Fact]
    public void ConsoleService_ResolveShellExecutable_ReturnsValidShell()
    {
        string shell = GlacierConsoleService.ResolveShellExecutable();
        Assert.False(string.IsNullOrWhiteSpace(shell));
        Assert.True(shell.EndsWith("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
                    shell.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConsoleService_CwdPersistence_TracksConsecutiveCdCommands()
    {
        var service = new GlacierConsoleService();
        string initialDir = service.CurrentWorkingDirectory;
        Assert.True(Directory.Exists(initialDir));

        // 1. cd ..
        var (code1, out1) = await service.ExecuteCommandAsync("cd ..");
        Assert.Equal(0, code1);
        string parentDir = Path.GetFullPath(Path.Combine(initialDir, ".."));
        Assert.Equal(parentDir, service.CurrentWorkingDirectory);
        Assert.Contains("Directory changed to:", out1);

        // 2. cd back into initial directory
        string childName = Path.GetFileName(initialDir);
        var (code2, out2) = await service.ExecuteCommandAsync($"cd \"{childName}\"");
        Assert.Equal(0, code2);
        Assert.Equal(initialDir, service.CurrentWorkingDirectory);

        // 3. cd into nonexistent path -> should fail with code 1 and NOT alter CurrentWorkingDirectory
        var (code3, out3) = await service.ExecuteCommandAsync("cd nonexistent_subdir_404_xyz");
        Assert.Equal(1, code3);
        Assert.Contains("Cannot find path", out3);
        Assert.Equal(initialDir, service.CurrentWorkingDirectory);

        // 4. cd . -> stays same
        var (code4, _) = await service.ExecuteCommandAsync("cd .");
        Assert.Equal(0, code4);
        Assert.Equal(initialDir, service.CurrentWorkingDirectory);

        // 5. Set-Location ..
        var (code5, _) = await service.ExecuteCommandAsync("Set-Location ..");
        Assert.Equal(0, code5);
        Assert.Equal(parentDir, service.CurrentWorkingDirectory);

        // 6. cd.. (without space)
        var (code6, _) = await service.ExecuteCommandAsync("cd..");
        Assert.Equal(0, code6);
        string grandParentDir = Path.GetFullPath(Path.Combine(parentDir, ".."));
        Assert.Equal(grandParentDir, service.CurrentWorkingDirectory);
    }

    [Fact]
    public async Task ConsoleService_ExecuteChildProcess_HonorsCurrentWorkingDirectory()
    {
        var service = new GlacierConsoleService();
        string initialDir = service.CurrentWorkingDirectory;

        // Run command printing working directory from PowerShell
        var (exitCode, output) = await service.ExecuteCommandAsync("[System.IO.Directory]::GetCurrentDirectory()");
        Assert.Equal(0, exitCode);
        Assert.Contains(initialDir, output.Trim());
    }

    // =========================================================================
    // 4. DeviceManager.GetDevices() Hardware Telemetry Integration
    // =========================================================================

    [Fact]
    public void DeviceManager_GetDevices_EnumeratesPhysicalHardwareCleanlyWithoutExceptions()
    {
        // Must execute cleanly on physical hardware
        var devices = DeviceManager.GetDevices();

        Assert.NotNull(devices);
        Assert.NotEmpty(devices);

        foreach (var dev in devices)
        {
            Assert.False(string.IsNullOrWhiteSpace(dev.Id), "Device ID must not be empty.");
            Assert.False(string.IsNullOrWhiteSpace(dev.Name), "Device Name must not be empty.");
            Assert.True(dev.Index >= 0, "Device Index must be >= 0.");
            Assert.True(dev.DedicatedVramGb >= 0, "Dedicated VRAM must be >= 0.");
            Assert.True(dev.SharedVramGb >= 0, "Shared VRAM must be >= 0.");
            Assert.NotNull(dev.SupportedEngines);
            Assert.NotEmpty(dev.SupportedEngines);
        }

        var optimal = DeviceManager.GetOptimalDevice();
        Assert.NotNull(optimal);
        Assert.Contains(optimal, devices);
    }

    [Fact]
    public void DeviceManager_RazorTelemetryComputations_ExecuteWithoutExceptions()
    {
        // Emulate telemetry bindings from GlacierStudio.razor
        var devices = DeviceManager.GetDevices();
        var optimal = DeviceManager.GetOptimalDevice();

        double totalRamGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        Assert.True(totalRamGb > 0, "Total RAM must be greater than 0 GB.");

        int cpuThreads = Environment.ProcessorCount;
        Assert.True(cpuThreads > 0);

        var arch = RuntimeInformation.ProcessArchitecture;
        Assert.NotEqual(Architecture.Arm, arch); // Windows x64 host

        bool hasVector512 = Vector512.IsHardwareAccelerated;
        bool hasVector256 = Vector256.IsHardwareAccelerated;
        Assert.True(hasVector256, "Modern x64 CPU must support at least Vector256.");

        // Verify optimal device exists in list
        Assert.True(optimal.Id.Length > 0);
        Assert.True(optimal.DedicatedVramGb >= 0);
    }

    // =========================================================================
    // 5. Additional Adversarial Stress Tests
    // =========================================================================

    [Fact]
    public void DocTree_FrontmatterWithComment_ObservesAstBehavior()
    {
        var service = new GlacierDocTreePreviewService();
        string markdown = @"---
# YAML comment inside frontmatter
title: Test Frontmatter
---

# Genuine Heading
Body";

        service.SetDocument("YamlComment.md", markdown, "YamlComment.md");
        var doc = service.ActiveDocument;

        Assert.NotNull(doc);
        // Note: SpanMarkdownParser sees '# YAML comment' on line 2 as a header if not frontmatter-filtered.
        // We verify that the genuine heading on line 6 is present and correctly mapped.
        var genuineHeading = doc.Outline.Find(o => o.Title == "Genuine Heading");
        Assert.NotNull(genuineHeading);
        Assert.Equal(6, genuineHeading.LineNumber);
    }

    [Fact]
    public void DocTree_NoHeadingsDocument_ReturnsEmptyOutlineAndValidHtml()
    {
        var service = new GlacierDocTreePreviewService();
        string markdown = @"Just a plain text document without any markdown headers.
Line 2 of plain text.
Line 3 of plain text.";

        service.SetDocument("Plain.md", markdown, "Plain.md");
        var doc = service.ActiveDocument;

        Assert.NotNull(doc);
        Assert.Empty(doc.Outline);
        Assert.Contains("Just a plain text document", doc.FormattedHtml);
        Assert.DoesNotContain("gs-nav-heading", doc.FormattedHtml);
    }

    [Fact]
    public void DocTree_LoadFile_NonExistentFileHandlesGracefully()
    {
        var service = new GlacierDocTreePreviewService();
        string initialTitle = service.ActiveDocument.Title;
        service.LoadFile(@"C:\NonExistent_Invalid_Path_999\Missing.md");

        var doc = service.ActiveDocument;
        Assert.NotNull(doc);
        Assert.Equal(initialTitle, doc.Title);
    }

    [Fact]
    public void AnsiParser_EmptyAndNullInputs_ReturnsEmptyString()
    {
        Assert.Equal("", GlacierAnsiParser.ToHtml(null));
        Assert.Equal("", GlacierAnsiParser.ToHtml(""));
        Assert.Equal("   ", GlacierAnsiParser.ToHtml("   "));
    }

    [Fact]
    public void AnsiParser_ConsecutiveSgrCodesWithoutText_CombinesStylesCorrectly()
    {
        string input = "\u001b[1m\u001b[31m\u001b[4mTriple Styled\u001b[0m";
        string html = GlacierAnsiParser.ToHtml(input);

        Assert.Contains("font-weight:bold", html);
        Assert.Contains("color:#ef4444", html);
        Assert.Contains("text-decoration:underline", html);
        Assert.Contains("Triple Styled", html);
    }

    [Fact]
    public async Task ConsoleService_Cancellation_AbortsGracefully()
    {
        var service = new GlacierConsoleService();
        using var cts = new System.Threading.CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Command that would take a long time without cancellation
        var (exitCode, output) = await service.ExecuteCommandAsync("Start-Sleep -Seconds 10", "TEST", cts.Token);

        Assert.Equal(-1, exitCode);
        Assert.Contains("cancelled", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConsoleService_LogRingBuffer_CapsAt1000Logs()
    {
        var service = new GlacierConsoleService();
        for (int i = 0; i < 1100; i++)
        {
            service.AddLog("TEST", $"Log entry {i}");
        }

        Assert.True(service.Logs.Count <= 1000);
        Assert.True(service.Logs.Count >= 900);
    }

    [Fact]
    public void DeviceManager_ResolveDevice_ResolvesAutoCpuAndOptimal()
    {
        var autoDev = DeviceManager.ResolveDevice("auto");
        Assert.NotNull(autoDev);

        var optimalDev = DeviceManager.GetOptimalDevice();
        Assert.Equal(optimalDev.Id, autoDev.Id);

        var cpuDev = DeviceManager.ResolveDevice("cpu");
        Assert.NotNull(cpuDev);
        Assert.Equal(GpuVendor.Cpu, cpuDev.Vendor);
    }
}

