using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record LocalThinkingModel(string Identifier, string Title)
{
    public static LocalThinkingModel Gemma { get; } = new("gemma3:4b", "Gemma 3 · 4B");
    public static LocalThinkingModel Qwen { get; } = new("qwen2.5vl:7b", "Qwen2.5-VL · 7B");
    public static LocalThinkingModel Llama { get; } = new("llama3.2-vision:11b", "Llama 3.2 Vision · 11B");
    public static IReadOnlyList<LocalThinkingModel> All { get; } = [Gemma, Qwen, Llama];
}

public sealed record ResidentThinkingModel(LocalThinkingModel Model, int? ContextLength, bool OnCompatibilityService);
public sealed record LocalThinkingMessage(string Role, string Content);

public sealed class LocalThinkingClient
{
    private static readonly Uri Primary = new("http://127.0.0.1:11434/api/");
    private static readonly Uri Compatible = new("http://127.0.0.1:11435/api/");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient LocalHttp = Configure(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false
    });

    private readonly HttpClient http;
    private sealed record Inventory(InventoryItem[]? Models);
    private sealed record InventoryItem(string? Name, [property: JsonPropertyName("context_length")] int? ContextLength);
    private sealed record Reply(LocalThinkingMessage? Message, string? Error);

    public LocalThinkingClient() => http = LocalHttp;
    internal LocalThinkingClient(HttpClient http) => this.http = http;

    private static HttpClient Configure(HttpMessageHandler handler) =>
        new(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1024 * 1024 };

    public async Task<ResidentThinkingModel?> FindResidentAsync(LocalThinkingModel preferred,
        bool useCompatibilityService, CancellationToken cancellationToken)
    {
        bool[] services = !useCompatibilityService ? [false] : preferred == LocalThinkingModel.Llama ? [true, false] : [false, true];
        ResidentThinkingModel? fallback = null;
        var reachable = false;
        foreach (var compatibility in services)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var url = new Uri(compatibility ? Compatible : Primary, "ps");
                using var response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var inventory = await response.Content.ReadFromJsonAsync<Inventory>(Json, timeout.Token).ConfigureAwait(false);
                if (inventory?.Models is not { } models) continue;
                reachable = true;
                foreach (var item in models)
                {
                    if (item is null) continue;
                    var model = LocalThinkingModel.All.FirstOrDefault(candidate => candidate.Identifier == item.Name);
                    if (model is null) continue;
                    var resident = new ResidentThinkingModel(model, item.ContextLength, compatibility);
                    if (model == preferred) return resident;
                    fallback ??= resident;
                }
            }
            catch (Exception error) when (error is HttpRequestException or JsonException
                || error is OperationCanceledException && !cancellationToken.IsCancellationRequested) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!reachable) throw new HttpRequestException("Could not read the local Ollama model inventory.");
        return fallback;
    }

    public async Task<T> ChatAsync<T>(ResidentThinkingModel resident, IReadOnlyList<LocalThinkingMessage> messages,
        object schema, int outputTokens, bool forQuestNotes, CancellationToken cancellationToken)
    {
        var options = new Dictionary<string, int> { ["temperature"] = 0, ["num_predict"] = outputTokens };
        if (resident.ContextLength is > 0 and var context) options["num_ctx"] = context;
        else if (forQuestNotes) options["num_ctx"] = 4096;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var url = new Uri(resident.OnCompatibilityService ? Compatible : Primary, "chat");
        var request = new Dictionary<string, object>
        {
            ["model"] = resident.Model.Identifier, ["stream"] = false, ["format"] = schema,
            ["options"] = options, ["messages"] = messages
        };
        if (forQuestNotes) request["keep_alive"] = "30m";
        using var response = await http.PostAsJsonAsync(url, request, Json, timeout.Token).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? detail = null;
            try { detail = JsonSerializer.Deserialize<Reply>(data, Json)?.Error; }
            catch (JsonException) { }
            throw new HttpRequestException(detail ?? $"Local Ollama returned HTTP {(int)response.StatusCode}.",
                null, response.StatusCode);
        }
        var reply = JsonSerializer.Deserialize<Reply>(data, Json);
        if (reply?.Message?.Content is not { } text || string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Local Ollama returned an empty reply.");
        return JsonSerializer.Deserialize<T>(text, Json)
            ?? throw new InvalidDataException("Local Ollama returned empty JSON.");
    }
}
