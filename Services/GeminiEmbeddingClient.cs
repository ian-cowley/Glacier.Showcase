using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glacier.Showcase.Services;

public class GeminiEmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public GeminiEmbeddingClient(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
    }

    public async Task<float[]> GetEmbeddingAsync(string text)
    {
        var request = new
        {
            model = "models/gemini-embedding-001",
            content = new
            {
                parts = new[] { new { text = text } }
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"https://generativelanguage.googleapis.com/v1/models/gemini-embedding-001:embedContent?key={_apiKey}",
            request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new Exception($"Gemini API Error (EmbedContent): {response.StatusCode}\nDetails: {errorBody}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var values = json.GetProperty("embedding").GetProperty("values").EnumerateArray();
        
        return values.Select(v => v.GetSingle()).ToArray();
    }
}
