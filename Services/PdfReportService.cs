using System.IO;
using TinyPdf;

namespace Glacier.Showcase.Services;

public class PdfReportService
{
    // ── Brand palette ────────────────────────────────────────────────────────
    private const string ColAccent      = "#0284c7";
    private const string ColAccentDark  = "#075985";
    private const string ColSuccess     = "#059669";
    private const string ColWarning     = "#d97706";
    private const string ColDivider     = "#CBD5E1";
    private const string ColCardBg      = "#F8FAFC";
    private const string ColCardText    = "#1E293B";
    private const string ColMuted       = "#64748B";
    private const string ColWhite       = "#FFFFFF";
    private const double PageW          = 612.0;  // TinyPdf default: Letter
    private const double PageH          = 792.0;
    private const double MarginL        = 50.0;
    private const double MarginR        = 562.0;

    public string GenerateInvestmentProspectus(
        List<PropertyMatch> properties,
        string rationale,
        TransitRoute? route = null,
        NeighborhoodProfile? profile = null,
        List<PropertyUnderwriting>? underwriting = null)
    {
        var builder = TinyPdfCreate.Create();
        int pageNumber = 0;
        string generatedAt = DateTime.Now.ToString("dd MMM yyyy, HH:mm");

        // ── PAGE 1: Cover ────────────────────────────────────────────────────
        pageNumber++;
        builder.Page(ctx =>
        {
            // Full-width header band
            ctx.Rect(0, PageH - 120, PageW, 120, ColAccentDark);
            ctx.Rect(0, PageH - 125, PageW, 5, ColAccent);

            // Logo / title area (white text on dark)
            ctx.Text("GLACIER SUITE", MarginL, PageH - 55, 26.0);
            ctx.Text("Multi-Agent Investment Prospectus", MarginL, PageH - 80, 13.0);

            // Meta block
            double metaY = PageH - 160;
            ctx.Text($"Generated:  {generatedAt}", MarginL, metaY, 9.0);
            metaY -= 14;
            ctx.Text($"Listings analysed:  {properties.Count}", MarginL, metaY, 9.0);
            metaY -= 14;
            if (profile != null)
                ctx.Text($"Primary borough:  {profile.Name}", MarginL, metaY, 9.0);

            // Decorative mid-band
            ctx.Rect(0, PageH - 370, PageW, 2, ColAccent);

            // Powered-by block
            double pwdY = PageH - 400;
            ctx.Text("Powered by the Glacier data library suite:", MarginL, pwdY, 11.0);
            pwdY -= 20;
            ctx.Text("Glacier.Polaris   —   lazy-evaluated columnar query engine (CSV scan, expression tree filtering)", MarginL, pwdY, 9.0);
            pwdY -= 14;
            ctx.Text("Glacier.Vector    —   SIMD cosine-similarity vector index with Gemini embeddings", MarginL, pwdY, 9.0);
            pwdY -= 14;
            ctx.Text("Glacier.Graph     —   CSR-compressed transit graph, BFS shortest-path (256 KB page cache)", MarginL, pwdY, 9.0);
            pwdY -= 14;
            ctx.Text("Glacier.DocTree   —   disk-serialised tree, lazily deserialized neighbourhood profiles", MarginL, pwdY, 9.0);
            pwdY -= 14;
            ctx.Text("Glacier.AgentDevKit (ADK)   —   LLM agent orchestration, telemetry, delegation tools", MarginL, pwdY, 9.0);

            ctx.Rect(0, PageH - 560, PageW, 2, ColDivider);

            // Disclaimer
            ctx.Text("DISCLAIMER", MarginL, PageH - 585, 9.0);
            ctx.Text("This document contains illustrative financial projections only and does not constitute financial advice.", MarginL, PageH - 600, 8.0);
            ctx.Text("All figures are model-generated for demonstration purposes. Seek qualified financial advice before making investment decisions.", MarginL, PageH - 613, 8.0);

            DrawFooter(ctx, pageNumber, generatedAt);
        });

        // ── PAGE 2: Executive Summary + Location Audit ───────────────────────
        pageNumber++;
        builder.Page(ctx =>
        {
            double y = PageH - MarginL;

            DrawSectionHeader(ctx, "Executive Summary & Investment Rationale", ref y);

            // Word-wrapped rationale
            y = WrapText(ctx, rationale, MarginL + 5, y, 9.0, 105, 13.0);
            y -= 8;
            DrawDivider(ctx, ref y);

            // Borough Audit
            if (profile != null || (route?.Path.Count > 0))
            {
                DrawSectionHeader(ctx, "Borough Audit & Transit Connectivity", ref y);

                if (profile != null)
                {
                    DrawLabelValue(ctx, "Borough", profile.Name, y); y -= 14;
                    DrawLabelValue(ctx, "Safety Score", profile.SafetyScore, y); y -= 14;
                    DrawLabelValue(ctx, "Crime Rate", profile.CrimeRate, y); y -= 14;
                    DrawLabelValue(ctx, "Character", profile.Vibe, y); y -= 14;
                    y = WrapText(ctx, $"Key attractions: {profile.Attractions}", MarginL + 5, y, 8.5, 110, 12.0);
                    y -= 8;
                }

                if (route?.Path.Count > 0)
                {
                    string pathStr = string.Join("  →  ", route.Path);
                    DrawLabelValue(ctx, "Transit to City of London", $"{pathStr}  ({route.Hops} hop{(route.Hops == 1 ? "" : "s")})", y);
                    y -= 20;
                }

                DrawDivider(ctx, ref y);
            }

            DrawFooter(ctx, pageNumber, generatedAt);
        });

        // ── PAGE 3+: Property Cards ───────────────────────────────────────────
        pageNumber++;
        builder.Page(ctx =>
        {
            double y = PageH - MarginL;
            DrawSectionHeader(ctx, "Top Matched Property Listings", ref y);

            int cardIndex = 0;
            foreach (var prop in properties.Take(3))
            {
                var uw = underwriting?.FirstOrDefault(u => u.PropertyId == prop.Id);
                double cardH = uw != null ? 170.0 : 130.0;

                if (y - cardH < 80) break;   // Don't overflow into footer

                // Card background
                ctx.Rect(MarginL - 5, y - cardH + 12, MarginR - MarginL + 10, cardH, ColCardBg);
                // Left accent bar
                ctx.Line(MarginL - 5, y + 12, MarginL - 5, y - cardH + 12, ColAccent, 4);

                // Card number badge
                ctx.Rect(MarginL, y - 2, 18, 14, ColAccent);
                ctx.Text($"#{cardIndex + 1}", MarginL + 4, y, 8.0);

                // Property name
                string name = prop.Name.Length > 65 ? prop.Name[..65] + "…" : prop.Name;
                ctx.Text(name, MarginL + 24, y, 11.0);
                y -= 16;

                // Borough + price row
                ctx.Text(prop.Neighbourhood, MarginL + 5, y, 9.0);
                ctx.Text($"£{prop.Price:F0}/night", 400, y, 9.0);
                y -= 14;

                // Ratings row
                ctx.Text($"★  {prop.ReviewScore:F1}  ({prop.ReviewsCount} reviews)", MarginL + 5, y, 8.5);
                ctx.Text($"Vibe match:  {(prop.VibeMatchScore * 100):F0}%", 360, y, 8.5);
                y -= 14;

                // Description
                y = WrapText(ctx, prop.Description, MarginL + 5, y, 8.0, 110, 12.0, maxLines: 2);
                y -= 4;

                // Financial block
                if (uw != null)
                {
                    ctx.Line(MarginL + 5, y + 2, MarginR - 5, y + 2, "#D1FAE5", 1);
                    y -= 12;
                    ctx.Text("Financial Underwriting  (illustrative)", MarginL + 5, y, 8.0);
                    y -= 13;

                    // Two columns of metrics
                    ctx.Text($"Est. Value:   £{uw.EstimatedValue:N0}", MarginL + 5, y, 8.0);
                    ctx.Text($"Occupancy:   {(uw.OccupancyRate * 100):F0}%", 290, y, 8.0);
                    y -= 12;
                    ctx.Text($"Gross Revenue:  £{uw.GrossAnnualRevenue:N0}/yr", MarginL + 5, y, 8.0);
                    ctx.Text($"Net Income:   £{uw.NetAnnualIncome:N0}/yr", 290, y, 8.0);
                    y -= 12;
                    ctx.Text($"Cap Rate (Yield):  {(uw.RentalYield * 100):F1}%", MarginL + 5, y, 8.0);
                    ctx.Text($"Cash-on-Cash:   {(uw.CashOnCashReturn * 100):F1}%", 290, y, 8.0);
                    y -= 8;
                }

                y -= 20;
                cardIndex++;
            }

            DrawFooter(ctx, pageNumber, generatedAt);
        });

        // ── Save ──────────────────────────────────────────────────────────────
        string reportsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "reports");
        Directory.CreateDirectory(reportsDir);

        string fileName = $"prospectus_{DateTime.Now.Ticks}.pdf";
        string filepath = Path.Combine(reportsDir, fileName);

        byte[] pdf = builder.Build();
        File.WriteAllBytes(filepath, pdf);

        return $"/reports/{fileName}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void DrawSectionHeader(TinyPdfCreate.IPageContext ctx, string title, ref double y)
    {
        // Rect: bottom edge at y-5, height 20 → top edge at y+15
        // Text baseline at y sits comfortably inside the band.
        ctx.Rect(MarginL - 5, y - 5, MarginR - MarginL + 10, 20, ColAccent);
        ctx.Text(title.ToUpperInvariant(), MarginL + 3, y, 9.5, new TinyPdfCreate.TextOptions(Color: ColWhite));
        y -= 28;
    }

    private static void DrawDivider(TinyPdfCreate.IPageContext ctx, ref double y)
    {
        ctx.Line(MarginL, y, MarginR, y, ColDivider, 1);
        y -= 16;
    }

    private static void DrawLabelValue(TinyPdfCreate.IPageContext ctx, string label, string value, double y)
    {
        ctx.Text($"{label}:", MarginL + 5, y, 8.5);
        ctx.Text(value, MarginL + 140, y, 8.5);
    }

    private static void DrawFooter(TinyPdfCreate.IPageContext ctx, int pageNum, string generated)
    {
        ctx.Line(MarginL, 50, MarginR, 50, ColDivider, 1);
        ctx.Text("Glacier Suite  —  Multi-Agent Investment Prospectus", MarginL, 36, 7.5);
        ctx.Text($"Page {pageNum}  |  Generated {generated}  |  Illustrative only — not financial advice", 330, 36, 7.0);
    }

    /// <summary>
    /// Primitive word-wrapping for long text. Returns the new Y position after rendering.
    /// </summary>
    private static double WrapText(TinyPdfCreate.IPageContext ctx, string text, double x, double y, double fontSize, int charsPerLine, double lineHeight, int maxLines = 20)
    {
        if (string.IsNullOrWhiteSpace(text)) return y;
        string[] words = text.Split(' ');
        string line = "";
        int rendered = 0;
        foreach (var word in words)
        {
            if (line.Length + word.Length > charsPerLine)
            {
                ctx.Text(line.TrimEnd(), x, y, fontSize);
                y -= lineHeight;
                line = word + " ";
                rendered++;
                if (rendered >= maxLines) { ctx.Text("…", x, y, fontSize); y -= lineHeight; return y; }
            }
            else
            {
                line += word + " ";
            }
        }
        if (!string.IsNullOrWhiteSpace(line))
        {
            ctx.Text(line.TrimEnd(), x, y, fontSize);
            y -= lineHeight;
        }
        return y;
    }
}
