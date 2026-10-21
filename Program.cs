using Glacier.Showcase.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient<Glacier.Showcase.Services.GeminiEmbeddingClient>()
    .AddTypedClient<Glacier.Showcase.Services.GeminiEmbeddingClient>((httpClient, sp) =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var apiKey = config["GEMINI_API_KEY"] ?? config["GeminiApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        return new Glacier.Showcase.Services.GeminiEmbeddingClient(httpClient, apiKey);
    });
builder.Services.AddSingleton<Glacier.Showcase.Services.AirbnbDataService>();
builder.Services.AddSingleton<Glacier.Showcase.Services.NeighborhoodAssessmentService>();
builder.Services.AddSingleton<Glacier.Showcase.Services.PdfReportService>();
builder.Services.AddSingleton<Glacier.Showcase.Services.GpuAccelerationBenchmarkService>();
builder.Services.AddSingleton<Glacier.Showcase.Services.MissionControlService>();
builder.Services.AddScoped<Glacier.Showcase.Services.AgentOrchestratorState>();
builder.Services.AddScoped<Glacier.Showcase.Services.AgentService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
