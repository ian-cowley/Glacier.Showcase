namespace Glacier.Showcase.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glacier.Showcase.Services;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ShowcaseHardeningTests
{
    [Fact]
    public void PdfReportService_GeneratesValidPdf_WithUnguessableName_AndNoLeftoverTempFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_pdf_{Guid.NewGuid():N}");
        try
        {
            var svc = new PdfReportService(tempDir);
            var matches = new List<PropertyMatch>
            {
                new()
                {
                    Id = "p1",
                    Name = "Quiet Westminster Flat",
                    Neighbourhood = "Westminster",
                    Description = "Bright apartment near Victoria.",
                    Price = 165.0,
                    ReviewScore = 4.9,
                    ReviewsCount = 42,
                    VibeMatchScore = 0.91f
                }
            };

            string relativeUrl = svc.GenerateInvestmentProspectus(matches, "Strong yield profile.");
            Assert.Matches(new Regex(@"^/reports/prospectus_[0-9a-f]{32}\.pdf$"), relativeUrl);

            string fileName = Path.GetFileName(relativeUrl);
            string fullPath = Path.Combine(tempDir, fileName);
            Assert.True(File.Exists(fullPath));

            byte[] header = new byte[5];
            using (var fs = File.OpenRead(fullPath))
            {
                int read = fs.Read(header, 0, header.Length);
                Assert.Equal(5, read);
            }
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(header));
            Assert.Empty(Directory.EnumerateFiles(tempDir, "*.tmp"));
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PdfReportService_PrunesExpiredAndExcessReports()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_pdf_prune_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            DateTime fixedNow = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);

            string expiredPath = Path.Combine(tempDir, "prospectus_expired.pdf");
            string olderValidPath = Path.Combine(tempDir, "prospectus_older_valid.pdf");
            string newerValidPath = Path.Combine(tempDir, "prospectus_newer_valid.pdf");

            File.WriteAllText(expiredPath, "%PDF-1.4\n");
            File.WriteAllText(olderValidPath, "%PDF-1.4\n");
            File.WriteAllText(newerValidPath, "%PDF-1.4\n");

            File.SetLastWriteTimeUtc(expiredPath, fixedNow.AddHours(-2));
            File.SetLastWriteTimeUtc(olderValidPath, fixedNow.AddMinutes(-30));
            File.SetLastWriteTimeUtc(newerValidPath, fixedNow.AddMinutes(-10));

            var svc = new PdfReportService(
                tempDir,
                retention: TimeSpan.FromHours(1),
                maxRetainedReports: 2,
                utcNowProvider: () => fixedNow);

            string url = svc.GenerateInvestmentProspectus(
                [new PropertyMatch { Id = "p2", Name = "Camden Loft", Neighbourhood = "Camden", Price = 140 }],
                "Balanced cash-on-cash projection.");

            string newlyGeneratedPath = Path.Combine(tempDir, Path.GetFileName(url));

            Assert.False(File.Exists(expiredPath), "Expired report should be pruned.");
            Assert.False(File.Exists(olderValidPath), "Oldest valid excess report should be pruned to enforce maxRetainedReports.");
            Assert.True(File.Exists(newerValidPath), "Newest existing report within retention should be kept.");
            Assert.True(File.Exists(newlyGeneratedPath), "Newly generated report must be protected from pruning.");

            var remaining = Directory.EnumerateFiles(tempDir, "prospectus_*.pdf").ToList();
            Assert.True(remaining.Count <= 2);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MissionControlService_LoadEmpiricalTelemetry_UsesSourceGeneratedJsonl_AndTracksLiveVsBaseline()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_telemetry_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string livePath = Path.Combine(tempDir, "custom_run.jsonl");
            File.WriteAllLines(livePath,
            [
                """{"step":1,"loss":18.25,"lr":0.0002,"tok_sec":54.5}""",
                """{"step":2,"loss":14.5,"lr":0.00018,"tok_sec":56.0}"""
            ]);

            using var svc = new MissionControlService();
            svc.LoadEmpiricalTelemetry(livePath);

            Assert.True(svc.IsLiveTelemetry);
            Assert.Contains("custom_run.jsonl", svc.TelemetrySource, StringComparison.Ordinal);
            Assert.Equal(2, svc.TrainingHistory.Count);
            Assert.Equal(1, svc.TrainingHistory[0].Step);
            Assert.Equal(18.25f, svc.TrainingHistory[0].Loss, precision: 2);
            Assert.Equal(54.5, svc.TrainingHistory[0].TokensPerSec, precision: 1);
            Assert.Equal(56.0, svc.TrainingHistory[1].TokensPerSec, precision: 1);

            string badPath = Path.Combine(tempDir, "corrupt_run.jsonl");
            File.WriteAllLines(badPath,
            [
                """{"step":1,"loss":12.0,"lr":0.0002,"tok_sec":50.0}""",
                "{not valid json"
            ]);

            svc.LoadEmpiricalTelemetry(badPath);

            Assert.False(svc.IsLiveTelemetry);
            Assert.Contains("Reference Baseline", svc.TelemetrySource, StringComparison.Ordinal);
            Assert.Equal(10, svc.TrainingHistory.Count);
            Assert.Equal(21.8704f, svc.TrainingHistory[0].Loss, precision: 3);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AgentOrchestratorState_PersistRunHistory_WritesAtomicUniqueFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_runs_{Guid.NewGuid():N}");
        try
        {
            var state = new AgentOrchestratorState();
            state.SetExecutionPhase("Finished");
            state.ProspectusPdfPath = "/reports/prospectus_demo.pdf";
            state.FinalMatches.Add(new PropertyMatch
            {
                Id = "101",
                Name = "Islington Townhouse",
                Neighbourhood = "Islington",
                Price = 180.0
            });

            string file1 = state.PersistRunHistory(tempDir);
            string file2 = state.PersistRunHistory(tempDir);

            Assert.False(string.IsNullOrWhiteSpace(file1));
            Assert.False(string.IsNullOrWhiteSpace(file2));
            Assert.NotEqual(file1, file2);

            string fullPath1 = Path.Combine(tempDir, file1);
            string fullPath2 = Path.Combine(tempDir, file2);
            Assert.True(File.Exists(fullPath1));
            Assert.True(File.Exists(fullPath2));
            Assert.Empty(Directory.EnumerateFiles(tempDir, "*.tmp"));

            var parsed = JsonSerializer.Deserialize(File.ReadAllText(fullPath1), ShowcaseIndentedJsonContext.Default.ShowcaseRunReport);
            Assert.NotNull(parsed);
            Assert.Equal(1, parsed.FinalMatchesCount);
            Assert.Equal("/reports/prospectus_demo.pdf", parsed.ProspectusPdfPath);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
