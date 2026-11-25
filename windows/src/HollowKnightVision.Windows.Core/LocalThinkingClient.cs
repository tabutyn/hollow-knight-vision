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
public sealed record LocalThinkingMessage(string Role, string Content,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Images = null);

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
    private readonly Func<bool, string?>? serverLog;
    private sealed record Inventory(InventoryItem[]? Models);
    private sealed record InventoryItem(string? Name, [property: JsonPropertyName("context_length")] int? ContextLength);
    private sealed record Reply(LocalThinkingMessage? Message, string? Error);
    private sealed record ModelReceipt(bool Done, [property: JsonPropertyName("done_reason")] string? DoneReason);
    private sealed record ModelDetails(string? Modelfile);

    public LocalThinkingClient() : this(LocalHttp, WindowsServerLog) { }

    internal LocalThinkingClient(HttpClient http, Func<bool, string?>? serverLog = null)
    {
        this.http = http;
        this.serverLog = serverLog;
    }

    private static string? WindowsServerLog(bool compatibility) => compatibility
        ? Environment.GetEnvironmentVariable("HKV_OLLAMA_COMPAT_LOG")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollama", "server.log");

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
        object schema, int outputTokens, bool keepResident, CancellationToken cancellationToken)
    {
        var text = await ChatTextAsync(resident, messages, schema, outputTokens, keepResident, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(text, Json)
            ?? throw new InvalidDataException("Local Ollama returned empty JSON.");
    }

    public async Task<string> ChatTextAsync(ResidentThinkingModel resident, IReadOnlyList<LocalThinkingMessage> messages,
        object schema, int outputTokens, bool keepResident, CancellationToken cancellationToken)
    {
        var options = new Dictionary<string, int> { ["temperature"] = 0, ["num_predict"] = outputTokens };
        if (resident.ContextLength is > 0 and var context) options["num_ctx"] = context;
        else if (keepResident) options["num_ctx"] = 4096;
        var url = new Uri(resident.OnCompatibilityService ? Compatible : Primary, "chat");
        var request = new Dictionary<string, object>
        {
            ["model"] = resident.Model.Identifier, ["stream"] = false, ["format"] = schema,
            ["options"] = options, ["messages"] = messages
        };
        if (keepResident) request["keep_alive"] = "30m";
        var data = await PostAsync(url, request, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        var reply = JsonSerializer.Deserialize<Reply>(data, Json);
        if (reply?.Message?.Content is not { } text || string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Local Ollama returned an empty reply.");
        return text;
    }

    public async Task<ResidentThinkingModel> LoadForThinkingAsync(LocalThinkingModel model, bool useCompatibilityService,
        CancellationToken cancellationToken, IProgress<OllamaLoadProgress>? progress = null)
    {
        var compatibility = model == LocalThinkingModel.Llama && useCompatibilityService;
        var server = compatibility ? Compatible : Primary;
        // Switching from Vision's selected model to another uses the same single-resident policy as macOS.
        if (useCompatibilityService)
            await ReleaseOtherModelsAsync(compatibility ? Primary : Compatible, model, cancellationToken).ConfigureAwait(false);
        await ReleaseOtherModelsAsync(server, model, cancellationToken).ConfigureAwait(false);
        var request = new Dictionary<string, object>
        {
            ["model"] = model.Identifier, ["stream"] = false, ["keep_alive"] = "30m",
            ["options"] = new Dictionary<string, int> { ["num_ctx"] = 4096 }
        };
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitor = progress is not null && serverLog?.Invoke(compatibility) is { } filename && Path.IsPathFullyQualified(filename)
            ? MonitorLoadAsync(server, model, filename, progress, monitorCancellation.Token) : Task.CompletedTask;
        try
        {
            var data = await PostAsync(new Uri(server, "generate"), request, TimeSpan.FromMinutes(15), cancellationToken).ConfigureAwait(false);
            var receipt = JsonSerializer.Deserialize<ModelReceipt>(data, Json);
            if (receipt is not { Done: true } || receipt.DoneReason == "unload")
                throw new InvalidDataException("Ollama did not confirm the model load.");
            return new(model, 4096, compatibility);
        }
        finally
        {
            monitorCancellation.Cancel();
            await monitor.ConfigureAwait(false);
        }
    }

    private async Task MonitorLoadAsync(Uri server, LocalThinkingModel model, string path,
        IProgress<OllamaLoadProgress> progress, CancellationToken token)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, useAsync: true);
            file.Seek(0, SeekOrigin.End);
            // Note the end of the file before the load starts. The receipt remains authoritative.
            var data = await PostAsync(new Uri(server, "show"), new { model = model.Identifier }, TimeSpan.FromSeconds(4), token)
                .ConfigureAwait(false);
            var detail = JsonSerializer.Deserialize<ModelDetails>(data, Json);
            if (OllamaLoadLog.ModelDigest(detail?.Modelfile) is not { } digest) return;
            var parser = new OllamaLoadLog(digest);
            using var reader = new StreamReader(file);
            var buffer = new char[8192];
            while (!token.IsCancellationRequested)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0)
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    continue;
                }
                foreach (var update in parser.Consume(new string(buffer, 0, count)))
                {
                    token.ThrowIfCancellationRequested();
                    progress.Report(update);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException
            or JsonException or OperationCanceledException or ArgumentException) { }
    }

    private async Task ReleaseOtherModelsAsync(Uri server, LocalThinkingModel selected, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        Inventory? inventory;
        try
        {
            using var response = await http.GetAsync(new Uri(server, "ps"), timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            inventory = await response.Content.ReadFromJsonAsync<Inventory>(Json, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !token.IsCancellationRequested)
        {
            return;
        }
        foreach (var model in LocalThinkingModel.All.Where(model => model != selected
            && (inventory?.Models?.Any(item => item?.Name == model.Identifier) ?? false)))
            await PostAsync(new Uri(server, "generate"), new { model = model.Identifier, keep_alive = 0 },
                TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
    }

    private async Task<byte[]> PostAsync(Uri url, object request, TimeSpan limit, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
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
        return data;
    }
}
