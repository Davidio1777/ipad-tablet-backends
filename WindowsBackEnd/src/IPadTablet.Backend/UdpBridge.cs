using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace IPadTablet.Backend;

internal sealed class UdpBridge : IAsyncDisposable
{
    private sealed record Client(IPEndPoint Endpoint, DateTime LastSeen);
    private readonly BackendOptions options;
    private readonly BackendState state;
    private readonly UdpClient video;
    private readonly UdpClient input;
    private readonly ConcurrentDictionary<string, Client> clients = new();
    private readonly Channel<byte[]> frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false
    });
    private readonly CancellationTokenSource shutdown = new();
    private Task? videoReceiveTask, inputReceiveTask, sendTask;
    private uint frameId, metadataId;
    private long framesSent;

    public int ConnectedClients => clients.Count;
    public long FramesSent => Interlocked.Read(ref framesSent);

    public UdpBridge(BackendOptions options, BackendState state)
    {
        this.options = options;
        this.state = state;
        video = new UdpClient(new IPEndPoint(IPAddress.Any, options.UdpVideoPort));
        input = new UdpClient(new IPEndPoint(IPAddress.Any, options.UdpInputPort));
    }

    public void Start()
    {
        videoReceiveTask = ReceiveHelloAsync(shutdown.Token);
        inputReceiveTask = ReceiveInputAsync(shutdown.Token);
        sendTask = SendFramesAsync(shutdown.Token);
        Console.WriteLine($"UDP: Video {options.UdpVideoPort}, Pencil {options.UdpInputPort}");
    }

    public void Offer(byte[] frame) => frames.Writer.TryWrite(frame);

    public void PublishMetadata()
    {
        foreach (var client in ActiveClients()) _ = SendMetadataAsync(client.Endpoint);
    }

    private async Task ReceiveHelloAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var packet = await video.ReceiveAsync(cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(packet.Buffer);
                var root = document.RootElement;
                if (GetString(root, "type") != "hello" || GetString(root, "token") != options.Token) continue;
                var session = GetString(root, "session");
                if (string.IsNullOrWhiteSpace(session) || session.Length > 128) continue;
                clients[session] = new Client(packet.RemoteEndPoint, DateTime.UtcNow);
                await SendMetadataAsync(packet.RemoteEndPoint);
            }
            catch (JsonException) { }
        }
    }

    private async Task ReceiveInputAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var packet = await input.ReceiveAsync(cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(packet.Buffer);
                var root = document.RootElement;
                var session = GetString(root, "session");
                if (GetString(root, "type") != "input" || GetString(root, "token") != options.Token
                    || !clients.TryGetValue(session, out var client)
                    || !Equals(client.Endpoint.Address, packet.RemoteEndPoint.Address)
                    || !root.TryGetProperty("payload", out var payload)) continue;
                clients[session] = client with { LastSeen = DateTime.UtcNow };
                await state.HandleInputAsync(payload, cancellationToken);
            }
            catch (JsonException) { }
        }
    }

    private async Task SendFramesAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken))
        {
            var id = unchecked(++frameId);
            var packets = EncodePackets(1, id, frame).ToArray();
            foreach (var client in ActiveClients())
            {
                foreach (var packet in packets) await video.SendAsync(packet, client.Endpoint, cancellationToken);
                Interlocked.Increment(ref framesSent);
            }
        }
    }

    private async Task SendMetadataAsync(IPEndPoint endpoint)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(state.Metadata);
        foreach (var packet in EncodePackets(2, unchecked(++metadataId), payload))
            await video.SendAsync(packet, endpoint);
    }

    private IEnumerable<Client> ActiveClients()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-5);
        foreach (var pair in clients)
        {
            if (pair.Value.LastSeen >= cutoff) yield return pair.Value;
            else clients.TryRemove(pair.Key, out _);
        }
    }

    private static IEnumerable<byte[]> EncodePackets(byte type, uint id, byte[] payload)
    {
        const int mtu = 1200, header = 14, chunkSize = mtu - header;
        var count = Math.Max(1, (payload.Length + chunkSize - 1) / chunkSize);
        for (var index = 0; index < count; index++)
        {
            var length = Math.Min(chunkSize, payload.Length - index * chunkSize);
            var packet = new byte[header + Math.Max(0, length)];
            "IPUD"u8.CopyTo(packet);
            packet[4] = 1; packet[5] = type;
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6), id);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), (ushort)index);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12), (ushort)count);
            if (length > 0) payload.AsSpan(index * chunkSize, length).CopyTo(packet.AsSpan(header));
            yield return packet;
        }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        video.Dispose(); input.Dispose(); frames.Writer.TryComplete();
        var tasks = new[] { videoReceiveTask, inputReceiveTask, sendTask }.Where(t => t is not null).Cast<Task>();
        await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        shutdown.Dispose();
    }
}
