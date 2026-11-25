using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public enum BotTrialCommandKind { Status, Stop, MarkReturn, Return, RestoreStart }

public sealed record BotTrialCommand(BotTrialCommandKind Kind, string? FileName = null)
{
    public static BotTrialCommand? Parse(string text) => text switch
    {
        "status" => new(BotTrialCommandKind.Status),
        "stop" => new(BotTrialCommandKind.Stop),
        "mark-return" => new(BotTrialCommandKind.MarkReturn),
        "return" => new(BotTrialCommandKind.Return),
        _ => SavedStart(text)
    };

    private static BotTrialCommand? SavedStart(string text)
    {
        const string prefix = "restore-start:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var file = text[prefix.Length..];
        return file.Length <= 255 && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !file.Contains(':') && !file.Any(char.IsControl)
            && RecordedInputPathStore.IsSafeFileName(file) ? new(BotTrialCommandKind.RestoreStart, file) : null;
    }
}

public sealed record BotTrialReply(bool Accepted, string? Issue = null, JsonElement? Status = null);

/// <summary>Local command transport for a single opted-in Windows desktop in the current logon session.</summary>
public sealed class BotTrialControl : IAsyncDisposable
{
    private const int MaxCommandBytes = 1024;
    private const int MaxReplyBytes = 16384;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;

    public BotTrialControl(Func<BotTrialCommand, CancellationToken, Task<BotTrialReply>> dispatch) : this(dispatch, LocalPipe()) { }

    internal BotTrialControl(Func<BotTrialCommand, CancellationToken, Task<BotTrialReply>> dispatch, string pipeName)
    {
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        worker = Task.Run(() => AcceptCommandsAsync(pipe, dispatch, lifetime.Token));
    }

    public static Task<BotTrialReply> SendAsync(string command, CancellationToken cancellationToken = default) =>
        SendAsync(command, LocalPipe(), cancellationToken);

    internal static async Task<BotTrialReply> SendAsync(string command, string pipeName, CancellationToken cancellationToken)
    {
        if (BotTrialCommand.Parse(command) is null || Utf8.GetByteCount(command) >= MaxCommandBytes)
            throw new ArgumentException("Invalid bot trial command.", nameof(command));
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(36));
        await WriteLineAsync(client, command, deadline.Token).ConfigureAwait(false);
        var reply = await ReadLineAsync(client, MaxReplyBytes, deadline.Token).ConfigureAwait(false);
        await client.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BotTrialReply>(reply, Json)
            ?? throw new InvalidDataException("Vision returned no trial response.");
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { await worker.ConfigureAwait(false); }
        finally { lifetime.Dispose(); }
    }

    private static async Task AcceptCommandsAsync(NamedPipeServerStream pipe,
        Func<BotTrialCommand, CancellationToken, Task<BotTrialReply>> dispatch, CancellationToken token)
    {
        using (pipe)
        {
            while (!token.IsCancellationRequested)
            {
                var connected = false;
                try
                {
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    connected = true;
                    using var intake = CancellationTokenSource.CreateLinkedTokenSource(token);
                    intake.CancelAfter(TimeSpan.FromSeconds(2));
                    var request = await ReadLineAsync(pipe, MaxCommandBytes, intake.Token).ConfigureAwait(false);
                    using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    operation.CancelAfter(TimeSpan.FromSeconds(33));
                    var reply = BotTrialCommand.Parse(request) is { } command
                        ? await dispatch(command, operation.Token).ConfigureAwait(false)
                        : new BotTrialReply(false, "Invalid bot trial command.");
                    await WriteLineAsync(pipe, JsonSerializer.Serialize(reply, Json), operation.Token).ConfigureAwait(false);
                    // Windows discards unread bytes on Disconnect. Give the client a bounded chance to read the reply.
                    using var receipt = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                    receipt.CancelAfter(TimeSpan.FromSeconds(2));
                    await pipe.ReadAsync(new byte[1], receipt.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException
                    or DecoderFallbackException) { }
                finally
                {
                    if (connected) pipe.Disconnect();
                }
            }
        }
    }

    private static string LocalPipe()
    {
        using var process = Process.GetCurrentProcess();
        var account = FormattableString.Invariant($"{Environment.UserDomainName}/{Environment.UserName}/{process.SessionId}");
        var key = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(account)))[..20];
        return "HollowKnightVision.BotTrial." + key;
    }

    private static async Task<string> ReadLineAsync(Stream stream, int limit, CancellationToken token)
    {
        var buffer = new byte[limit];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (count == 0) break;
            var end = Array.IndexOf(buffer, (byte)'\n', offset, count);
            if (end >= 0) return Utf8.GetString(buffer, 0, end).TrimEnd('\r');
            offset += count;
        }
        throw new InvalidDataException("The trial message was incomplete or exceeded its size limit.");
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken token)
    {
        await stream.WriteAsync(Utf8.GetBytes(line + "\n"), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
