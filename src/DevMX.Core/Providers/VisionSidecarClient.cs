using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DevMX.Core.Providers;

/// <summary>
/// Tool-free OpenAI-compatible vision client for the image OCR sidecar: one image per
/// request, plain chat completion with no tools/tool_choice (document-vision servers
/// like vLLM reject those without a tool-call parser). Mirrors DevMind's VisionNoteClient.
/// </summary>
public sealed class VisionSidecarClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string? _apiKey;

    public VisionSidecarClient(string baseUrl, string model, string? apiKey = null, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
        _http = handler is not null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromMinutes(3);
    }

    /// <summary>Send one image and return the model's transcription/description text.</summary>
    public async Task<string> DescribeImageAsync(
        string mediaType, string base64Data, string prompt, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 2048,
            ["temperature"] = 0,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = prompt },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = $"data:{mediaType};base64,{base64Data}"
                            }
                        }
                    }
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Add("Authorization", "Bearer " + _apiKey);

        var response = await _http.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"vision sidecar request failed with status {response.StatusCode}: {Truncate(responseText)}");

        var content = JsonNode.Parse(responseText)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("vision sidecar returned an empty response");
        return content.Trim();
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;

    public void Dispose() => _http.Dispose();
}
