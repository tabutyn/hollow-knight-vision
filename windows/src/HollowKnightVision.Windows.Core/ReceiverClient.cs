using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HollowKnightVision.Windows.Core;

public sealed class ReceiverClient : IAsyncDisposable
{
    private readonly string host;
    private readonly int port;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private TcpClient? client;
    private StreamReader? reader;
    private StreamWriter? writer;
    private long nextSequence = -1;
    private long nextPointerSequence = -1;

    public ReceiverClient(
        Guid? sessionId = null,
        string host = "127.0.0.1",
        int port = ReceiverProtocol.Port)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        SessionId = sessionId ?? Guid.NewGuid();
        this.host = host;
        this.port = port;
    }

    public Guid SessionId { get; }
    public bool IsConnected => client?.Connected == true;

    public async Task ConnectAsync(
        bool renderFrameMarker = false,
        CancellationToken cancellationToken = default)
    {
        if (client is not null) throw new InvalidOperationException("Receiver client already connected.");
        var pending = new TcpClient { NoDelay = true };
        try
        {
            await pending.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            var stream = pending.GetStream();
            client = pending;
            reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                NewLine = "\n"
            };
            await SendLineAsync(
                ReceiverWireCodec.EncodeLine(
                    ReceiverCapabilityRequest.Create(SessionId, renderFrameMarker)),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pending.Dispose();
            client = null;
            reader = null;
            writer = null;
            throw;
        }
    }

    public Task SendStateAsync(
        bool enabled,
        InputButtons heldButtons,
        CancellationToken cancellationToken = default)
    {
        var sequence = checked((ulong)Interlocked.Increment(ref nextSequence));
        return SendLineAsync(
            ReceiverWireCodec.EncodeLine(
                ReceiverInputSnapshot.Create(SessionId, sequence, enabled, heldButtons)),
            cancellationToken);
    }

    public Task SendCheckpointCommandAsync(
        ReceiverCheckpointCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.SessionId != SessionId)
        {
            throw new ArgumentException("Checkpoint command session does not match client.",
                nameof(command));
        }
        return SendLineAsync(ReceiverWireCodec.EncodeLine(command), cancellationToken);
    }

    public Task SendPlayerOpsCommandAsync(
        ReceiverPlayerOpsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.SessionId != SessionId)
        {
            throw new ArgumentException("Player operation session does not match client.",
                nameof(command));
        }
        return SendLineAsync(ReceiverWireCodec.EncodeLine(command), cancellationToken);
    }

    public Task SendPointerAsync(
        string kind,
        double normalizedX,
        double normalizedY,
        int clickCount = 1,
        CancellationToken cancellationToken = default)
    {
        var sequence = checked((ulong)Interlocked.Increment(ref nextPointerSequence));
        var command = ReceiverPointerCommand.Create(
            SessionId, sequence, kind, normalizedX, normalizedY, clickCount);
        return SendLineAsync(ReceiverWireCodec.EncodeLine(command), cancellationToken);
    }

    public async Task PulseAsync(
        InputButtons button,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (button == InputButtons.None) throw new ArgumentOutOfRangeException(nameof(button));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        await SendStateAsync(true, button, cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Always neutralize a started pulse, including cancellation paths.
            await SendStateAsync(true, InputButtons.None, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<string?> ReceiveLineAsync(CancellationToken cancellationToken = default)
    {
        var activeReader = reader ?? throw new InvalidOperationException("Receiver client is not connected.");
        var line = await activeReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is not null && line.Length > ReceiverProtocol.MaximumLineLength)
        {
            throw new InvalidDataException("Receiver message exceeds the protocol line limit.");
        }
        return line;
    }

    public async ValueTask DisposeAsync()
    {
        reader?.Dispose();
        if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
        client?.Dispose();
        writeGate.Dispose();
    }

    private async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        var activeWriter = writer ?? throw new InvalidOperationException("Receiver client is not connected.");
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await activeWriter.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await activeWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }
}
