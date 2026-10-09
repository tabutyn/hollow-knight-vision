using System.Net.Sockets;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

internal sealed record NegotiatedReceiver(
    ReceiverClient Client,
    ReceiverCapabilitiesAcknowledgement Capabilities);

internal static class ReceiverConnection
{
    internal static async Task<ReceiverClient> ConnectWithRetryAsync(
        CancellationToken cancellationToken) =>
        (await ConnectNegotiatedWithRetryAsync(cancellationToken).ConfigureAwait(false)).Client;

    internal static async Task<NegotiatedReceiver> ConnectNegotiatedWithRetryAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receiver = new ReceiverClient();
            try
            {
                await receiver.ConnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                while (true)
                {
                    var line = await receiver.ReceiveLineAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new IOException("Receiver closed during negotiation.");
                    if (ReceiverWireCodec.MessageType(line) != "capabilitiesAck") continue;
                    var acknowledgement = ReceiverWireCodec
                        .DecodeLine<ReceiverCapabilitiesAcknowledgement>(line);
                    if (acknowledgement.SessionId != receiver.SessionId
                        || !acknowledgement.Capabilities.Contains("input-state-v1"))
                    {
                        throw new InvalidDataException("Receiver capability acknowledgement is incompatible.");
                    }
                    return new NegotiatedReceiver(receiver, acknowledgement);
                }
            }
            catch (SocketException) when (!cancellationToken.IsCancellationRequested)
            {
                await receiver.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await receiver.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
