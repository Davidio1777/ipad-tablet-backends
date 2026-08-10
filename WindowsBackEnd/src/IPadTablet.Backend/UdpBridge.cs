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
    private readonly SecureDatagrams crypto;
    private readonly ConcurrentDictionary<string, Client> clients = new();
    private readonly Channel<byte[]> frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false
    });
    private readonly CancellationTokenSource shutdown = new();
    private Task? videoReceiveTask, inputReceiveTask, sendTask;
    private uint frameId;
    private int metadataId;
    private long framesSent;
    private long inputPacketsReceived;
    private long droppedInputPackets;
    private int hadClients;

    public int ConnectedClients
    {
        get { ExpireClients(); return clients.Count; }
    }
    public long FramesSent => Interlocked.Read(ref framesSent);
    public long InputPacketsReceived => Interlocked.Read(ref inputPacketsReceived);
    public long DroppedInputPackets => Interlocked.Read(ref droppedInputPackets);

    public UdpBridge(BackendOptions options, BackendState state)
    {
        this.options = options;
        this.state = state;
        var address = ResolveAddress(options.Host);
        video = CreateSocket(address, options.UdpVideoPort);
        try { input = CreateSocket(address, options.UdpInputPort); }
        catch
        {
            video.Dispose();
            throw;
        }
        crypto = new SecureDatagrams(
            options.Token, "server-to-client", "client-to-server"
        );
    }

    public void Start()
    {
        videoReceiveTask = ReceiveHelloAsync(shutdown.Token);
        inputReceiveTask = ReceiveInputAsync(shutdown.Token);
        sendTask = SendFramesAsync(shutdown.Token);
        Console.WriteLine($"UDP listening: video {video.Client.LocalEndPoint}, " +
                          $"Pencil/control {input.Client.LocalEndPoint}");
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
            try
            {
                var packet = await video.ReceiveAsync(cancellationToken);
                if (packet.Buffer.Length > 64 * 1024) continue;
                var plaintext = crypto.Open(SecureDatagrams.ControlEnvelope, packet.Buffer);
                if (plaintext is null) continue;
                using var document = JsonDocument.Parse(plaintext);
                var root = document.RootElement;
                if (GetString(root, "type") != "hello") continue;
                var session = GetString(root, "session");
                if (string.IsNullOrWhiteSpace(session) || session.Length > 128) continue;
                var isNew = !clients.TryGetValue(session, out var existing)
                            || !Equals(existing.Endpoint, packet.RemoteEndPoint);
                clients[session] = new Client(packet.RemoteEndPoint, DateTime.UtcNow);
                Interlocked.Exchange(ref hadClients, 1);
                if (isNew)
                    Console.WriteLine($"UDP client ready: {packet.RemoteEndPoint.Address} ({session})");
                await SendMetadataAsync(packet.RemoteEndPoint);
            }
            catch (JsonException) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException error)
            {
                Console.Error.WriteLine($"UDP video receive error: {error.Message}");
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private async Task ReceiveInputAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var packet = await input.ReceiveAsync(cancellationToken);
                if (packet.Buffer.Length > 64 * 1024)
                {
                    Interlocked.Increment(ref droppedInputPackets);
                    continue;
                }
                var plaintext = crypto.Open(SecureDatagrams.ControlEnvelope, packet.Buffer);
                if (plaintext is null)
                {
                    Interlocked.Increment(ref droppedInputPackets);
                    continue;
                }
                using var document = JsonDocument.Parse(plaintext);
                var root = document.RootElement;
                var session = GetString(root, "session");
                if (GetString(root, "type") != "input"
                    || !clients.TryGetValue(session, out var client)
                    || !Equals(client.Endpoint.Address, packet.RemoteEndPoint.Address)
                    || !root.TryGetProperty("payload", out var payload))
                {
                    Interlocked.Increment(ref droppedInputPackets);
                    continue;
                }
                clients[session] = client with { LastSeen = DateTime.UtcNow };
                Interlocked.Increment(ref inputPacketsReceived);
                await state.HandleInputAsync(payload, cancellationToken);
            }
            catch (JsonException) { Interlocked.Increment(ref droppedInputPackets); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException error)
            {
                Console.Error.WriteLine($"UDP input receive error: {error.Message}");
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private async Task SendFramesAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken))
        {
            var id = unchecked(++frameId);
            var packets = EncodePackets(1, id, frame, 1200 - SecureDatagrams.Overhead).ToArray();
            foreach (var client in ActiveClients())
            {
                try
                {
                    foreach (var packet in packets)
                        await video.SendAsync(crypto.Seal(SecureDatagrams.VideoEnvelope, packet),
                            client.Endpoint, cancellationToken);
                    Interlocked.Increment(ref framesSent);
                }
                catch (SocketException error)
                {
                    Console.Error.WriteLine($"UDP video send error for {client.Endpoint}: {error.Message}");
                }
            }
        }
    }

    private async Task SendMetadataAsync(IPEndPoint endpoint)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(state.Metadata);
            var id = unchecked((uint)Interlocked.Increment(ref metadataId));
            foreach (var packet in EncodePackets(2, id, payload, 1200 - SecureDatagrams.Overhead))
                await video.SendAsync(crypto.Seal(SecureDatagrams.VideoEnvelope, packet), endpoint);
        }
        catch (SocketException error)
        {
            Console.Error.WriteLine($"UDP metadata send error for {endpoint}: {error.Message}");
        }
    }

    private IEnumerable<Client> ActiveClients()
    {
        ExpireClients();
        return clients.Values.ToArray();
    }

    private void ExpireClients()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-5);
        foreach (var pair in clients)
            if (pair.Value.LastSeen < cutoff) clients.TryRemove(pair.Key, out _);
        if (clients.IsEmpty && Interlocked.Exchange(ref hadClients, 0) == 1)
            _ = state.ReleaseInputAsync();
    }

    private static UdpClient CreateSocket(IPAddress address, int port)
    {
        var socket = new UdpClient(address.AddressFamily);
        try
        {
            if (address.Equals(IPAddress.IPv6Any)) socket.Client.DualMode = true;
            socket.Client.ExclusiveAddressUse = true;
            socket.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            socket.Client.SendBufferSize = 4 * 1024 * 1024;
            socket.Client.Bind(new IPEndPoint(address, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static IPAddress ResolveAddress(string host)
    {
        if (host is "0.0.0.0" or "*") return IPAddress.Any;
        if (host == "::") return IPAddress.IPv6Any;
        if (IPAddress.TryParse(host, out var address)) return address;
        return Dns.GetHostAddresses(host)
                   .FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
               ?? throw new ArgumentException($"UDP host '{host}' did not resolve to an IPv4 address.");
    }

    private static IEnumerable<byte[]> EncodePackets(byte type, uint id, byte[] payload, int mtu)
    {
        const int header = 14;
        var chunkSize = mtu - header;
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
        crypto.Dispose();
        shutdown.Dispose();
    }
}
