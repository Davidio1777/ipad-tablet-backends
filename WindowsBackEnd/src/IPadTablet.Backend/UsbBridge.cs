using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace IPadTablet.Backend;

internal sealed class UsbBridge : IAsyncDisposable
{
    private const byte HelloFrame = 1, VideoFrame = 2, PencilFrame = 3, PingFrame = 4,
        ReadyFrame = 5, StreamInfoFrame = 6;
    private readonly BackendOptions options;
    private readonly BackendState state;
    private readonly Channel<byte[]> frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false
    });
    private readonly Channel<byte[]> metadata = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false
    });
    private readonly CancellationTokenSource shutdown = new();
    private Task? runTask;
    private long framesSent;

    public bool Connected { get; private set; }
    public long FramesSent => Interlocked.Read(ref framesSent);

    public UsbBridge(BackendOptions options, BackendState state)
    {
        this.options = options;
        this.state = state;
    }

    public void Start() => runTask = RunAsync(shutdown.Token);
    public void Offer(byte[] frame) => frames.Writer.TryWrite(frame);
    public void PublishMetadata() => metadata.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(state.Metadata));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var proxy = StartIproxy();
            _ = RelayErrorsAsync(proxy.StandardError, cancellationToken);
            try
            {
                while (!proxy.HasExited && !cancellationToken.IsCancellationRequested)
                {
                    var connected = false;
                    for (var offset = 0; offset < 10 && !connected; offset++)
                    {
                        try
                        {
                            using var client = new TcpClient { NoDelay = true };
                            await client.ConnectAsync("127.0.0.1", options.UsbPort + offset, cancellationToken);
                            Console.WriteLine($"USB: iPad auf Geräteport {options.UsbPort + offset} verbunden");
                            connected = true;
                            await SessionAsync(client.GetStream(), cancellationToken);
                        }
                        catch (Exception error) when (error is SocketException or IOException)
                        {
                            if (offset == 9) Console.WriteLine($"USB wartet auf iPad: {error.Message}");
                        }
                    }
                    if (!connected) await Task.Delay(1000, cancellationToken);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                Connected = false;
                if (!proxy.HasExited) proxy.Kill(true);
                await proxy.WaitForExitAsync(CancellationToken.None);
            }
            if (!cancellationToken.IsCancellationRequested) await Task.Delay(1000, cancellationToken);
        }
    }

    private Process StartIproxy()
    {
        var mappings = string.Join(' ', Enumerable.Range(0, 10)
            .Select(offset => $"{options.UsbPort + offset}:{options.UsbPort + offset}"));
        var info = new ProcessStartInfo(options.Iproxy, $"-l {mappings}")
        {
            UseShellExecute = false, RedirectStandardError = true,
            RedirectStandardOutput = true, CreateNoWindow = true
        };
        Console.WriteLine($"USB proxy: {options.Iproxy} {info.Arguments}");
        return Process.Start(info) ?? throw new InvalidOperationException("iproxy konnte nicht gestartet werden.");
    }

    private async Task SessionAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var hello = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.SerializeToUtf8Bytes(state.Metadata))!;
        hello["transport"] = JsonSerializer.SerializeToElement("usb");
        hello["protocol"] = JsonSerializer.SerializeToElement(1);
        await WriteFrameAsync(stream, HelloFrame, JsonSerializer.SerializeToUtf8Bytes(hello), cancellationToken);
        var (type, _) = await ReadFrameAsync(stream, cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if (type != ReadyFrame) throw new IOException("Ungültiger USB-Handshake.");
        Connected = true;
        PublishMetadata();
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var send = SendAsync(stream, sessionCancellation.Token);
        var receive = ReceiveAsync(stream, sessionCancellation.Token);
        await Task.WhenAny(send, receive);
        sessionCancellation.Cancel();
        await Task.WhenAll(send, receive).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Connected = false;
        await state.ReleaseInputAsync(cancellationToken);
    }

    private async Task SendAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var videoWait = frames.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var metadataWait = metadata.Reader.WaitToReadAsync(cancellationToken).AsTask();
            await Task.WhenAny(videoWait, metadataWait);
            while (metadata.Reader.TryRead(out var info))
                await WriteFrameAsync(stream, StreamInfoFrame, info, cancellationToken);
            if (frames.Reader.TryRead(out var frame))
            {
                await WriteFrameAsync(stream, VideoFrame, frame, cancellationToken);
                Interlocked.Increment(ref framesSent);
            }
        }
    }

    private async Task ReceiveAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var (type, payload) = await ReadFrameAsync(stream, cancellationToken);
            if (type == PencilFrame)
            {
                using var document = JsonDocument.Parse(payload);
                await state.HandleInputAsync(document.RootElement, cancellationToken);
            }
            else if (type != PingFrame) throw new IOException($"Unbekannter USB-Frame {type}.");
        }
    }

    private static async ValueTask WriteFrameAsync(Stream stream, byte type, byte[] payload, CancellationToken token)
    {
        var header = new byte[5];
        header[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)payload.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    private static async ValueTask<(byte Type, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        if (length > 16 * 1024 * 1024) throw new IOException("USB-Frame zu groß.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        return (header[0], payload);
    }

    private static async Task RelayErrorsAsync(StreamReader reader, CancellationToken token)
    {
        while (!token.IsCancellationRequested && await reader.ReadLineAsync(token) is { } line)
            Console.Error.WriteLine($"[iproxy] {line}");
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel(); frames.Writer.TryComplete(); metadata.Writer.TryComplete();
        if (runTask is not null) await runTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        shutdown.Dispose();
    }
}
