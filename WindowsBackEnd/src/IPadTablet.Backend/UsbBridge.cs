using System.Buffers.Binary;
using System.Diagnostics;
using System.ComponentModel;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace IPadTablet.Backend;

internal sealed class UsbBridge : IAsyncDisposable
{
    private sealed record UsbMuxProbe(bool ToolAvailable, string? Udid, string? Error);
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
    private readonly SemaphoreSlim outboundSignal = new(0, 1);
    private Task? runTask;
    private long framesSent;
    private long inputFramesReceived;
    private long droppedInputFrames;
    private long lastWaitingLog;

    public bool Connected { get; private set; }
    public long FramesSent => Interlocked.Read(ref framesSent);
    public long InputFramesReceived => Interlocked.Read(ref inputFramesReceived);
    public long DroppedInputFrames => Interlocked.Read(ref droppedInputFrames);

    public UsbBridge(BackendOptions options, BackendState state)
    {
        this.options = options;
        this.state = state;
    }

    public void Start() => runTask = RunAsync(shutdown.Token);
    public void Offer(byte[] frame)
    {
        if (frames.Writer.TryWrite(frame)) SignalOutbound();
    }

    public void PublishMetadata()
    {
        if (metadata.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(state.Metadata))) SignalOutbound();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? proxy = null;
            var logTasks = new List<Task>();
            try
            {
                var probe = await QueryUsbMuxAsync(cancellationToken);
                if (probe.ToolAvailable && probe.Error is not null)
                {
                    Console.Error.WriteLine($"USB: Apple usbmux query failed: {probe.Error}");
                    await Task.Delay(2000, cancellationToken);
                    continue;
                }
                if (probe.ToolAvailable && probe.Udid is null)
                {
                    Console.WriteLine("USB: Apple usbmux service is available, but no USB iPad is attached.");
                    await Task.Delay(2000, cancellationToken);
                    continue;
                }
                if (probe.Udid is not null)
                    Console.WriteLine($"USB: Apple iPad detected via usbmux ({probe.Udid}).");
                else
                    Console.WriteLine("USB: idevice_id is unavailable; starting iproxy without device preflight.");

                proxy = StartIproxy(probe.Udid);
                logTasks.Add(RelayOutputAsync(proxy.StandardOutput, options.UsbPort, false, cancellationToken));
                logTasks.Add(RelayOutputAsync(proxy.StandardError, options.UsbPort, true, cancellationToken));
                Console.WriteLine($"USB proxy ready: 127.0.0.1:{options.UsbPort} -> iPad:{options.UsbPort}");

                var lastDeviceCheck = Environment.TickCount64;
                while (!proxy.HasExited && !cancellationToken.IsCancellationRequested)
                {
                    var handshakeSucceeded = false;
                    Exception? lastError = null;
                    try
                    {
                        using var client = new TcpClient { NoDelay = true };
                        client.SendBufferSize = 256 * 1024;
                        client.ReceiveBufferSize = 256 * 1024;
                        await client.ConnectAsync("127.0.0.1", options.UsbPort, cancellationToken);
                        await SessionAsync(client.GetStream(), options.UsbPort, () =>
                        {
                            handshakeSucceeded = true;
                        }, cancellationToken);
                    }
                    catch (Exception error) when (error is SocketException or IOException or TimeoutException)
                    {
                        lastError = error;
                    }
                    if (!handshakeSucceeded)
                    {
                        var now = Environment.TickCount64;
                        if (probe.ToolAvailable && now - lastDeviceCheck >= 5_000)
                        {
                            lastDeviceCheck = now;
                            var refreshed = await QueryUsbMuxAsync(cancellationToken);
                            if (refreshed.Error is not null || refreshed.Udid is null)
                            {
                                Console.WriteLine(refreshed.Error is not null
                                    ? $"USB: Apple usbmux query failed: {refreshed.Error}"
                                    : "USB: iPad was disconnected from USB.");
                                break;
                            }
                        }
                        if (now - Interlocked.Read(ref lastWaitingLog) >= 5_000)
                        {
                            Interlocked.Exchange(ref lastWaitingLog, now);
                            Console.WriteLine($"USB: iPad is enumerated; waiting for the iPad Tablet app service " +
                                $"on port {options.UsbPort}. Open the app and enable USB. " +
                                $"({lastError?.Message ?? "iproxy stopped"})");
                        }
                        await Task.Delay(1000, cancellationToken);
                    }
                    else if (!cancellationToken.IsCancellationRequested)
                    {
                        if (lastError is not null)
                            Console.Error.WriteLine($"USB disconnected: {lastError.Message}; retrying.");
                        await Task.Delay(250, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Win32Exception error)
            {
                Console.Error.WriteLine($"USB could not start iproxy '{options.Iproxy}': {error.Message}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"USB proxy error: {error.Message}");
            }
            finally
            {
                Connected = false;
                if (proxy is not null)
                {
                    if (!proxy.HasExited) proxy.Kill(true);
                    await proxy.WaitForExitAsync(CancellationToken.None);
                    proxy.Dispose();
                }
                await Task.WhenAll(logTasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            if (!cancellationToken.IsCancellationRequested) await Task.Delay(1000, cancellationToken);
        }
    }

    internal ProcessStartInfo BuildIproxyStartInfo(string? udid = null)
    {
        var port = options.UsbPort;
        var info = new ProcessStartInfo(options.Iproxy)
        {
            UseShellExecute = false, RedirectStandardError = true,
            RedirectStandardOutput = true, CreateNoWindow = true
        };
        // Current upstream iproxy uses LOCAL:DEVICE. Keep compatibility with
        // the older bundled Windows build when its legacy DLL is selected.
        var directory = Path.GetDirectoryName(options.Iproxy) ?? string.Empty;
        if (File.Exists(Path.Combine(directory, "libusbmuxd-2.0.dll")))
        {
            if (!string.IsNullOrWhiteSpace(udid))
            {
                info.ArgumentList.Add("-u");
                info.ArgumentList.Add(udid);
            }
            info.ArgumentList.Add($"{port}:{port}");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(udid))
            {
                info.ArgumentList.Add("-u");
                info.ArgumentList.Add(udid);
            }
            info.ArgumentList.Add("-l");
            info.ArgumentList.Add(port.ToString());
            info.ArgumentList.Add(port.ToString());
        }
        return info;
    }

    private Process StartIproxy(string? udid)
    {
        var info = BuildIproxyStartInfo(udid);
        return Process.Start(info) ?? throw new InvalidOperationException("iproxy could not be started.");
    }

    private async Task<UsbMuxProbe> QueryUsbMuxAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(options.Iproxy) ?? string.Empty;
        var ideviceId = Path.Combine(directory, "idevice_id.exe");
        if (!File.Exists(ideviceId)) return new(false, null, null);

        var info = new ProcessStartInfo(ideviceId)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-l");
        try
        {
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("idevice_id could not be started.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var stdout = await output;
            var stderr = (await errors).Trim();
            if (process.ExitCode != 0)
                return new(true, null, string.IsNullOrWhiteSpace(stderr)
                    ? $"idevice_id exited with code {process.ExitCode}" : stderr);
            var udid = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return new(true, udid, null);
        }
        catch (TimeoutException)
        {
            return new(true, null, "idevice_id timed out while querying Apple Mobile Device Support");
        }
        catch (Win32Exception error)
        {
            return new(true, null, error.Message);
        }
    }

    private async Task SessionAsync(
        NetworkStream stream, int devicePort, Action handshakeCompleted,
        CancellationToken cancellationToken)
    {
        var hello = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.SerializeToUtf8Bytes(state.Metadata))!;
        hello["transport"] = JsonSerializer.SerializeToElement("usb");
        hello["protocol"] = JsonSerializer.SerializeToElement(1);
        await WriteFrameAsync(stream, HelloFrame, JsonSerializer.SerializeToUtf8Bytes(hello), cancellationToken);
        var (type, _) = await ReadFrameAsync(stream, cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if (type != ReadyFrame) throw new IOException("Invalid USB handshake.");
        Connected = true;
        handshakeCompleted();
        Console.WriteLine($"USB connected: iPad protocol handshake completed on device port {devicePort}");
        PublishMetadata();
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var send = SendAsync(stream, sessionCancellation.Token);
        var receive = ReceiveAsync(stream, sessionCancellation.Token);
        var completed = await Task.WhenAny(send, receive);
        try { await completed; }
        finally
        {
            sessionCancellation.Cancel();
            await Task.WhenAll(send, receive).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Connected = false;
            await state.ReleaseInputAsync(cancellationToken);
        }
    }

    private async Task SendAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await outboundSignal.WaitAsync(cancellationToken);
            while (metadata.Reader.TryRead(out var info))
                await WriteFrameAsync(stream, StreamInfoFrame, info, cancellationToken);
            if (frames.Reader.TryRead(out var frame))
            {
                await WriteFrameAsync(stream, VideoFrame, frame, cancellationToken);
                Interlocked.Increment(ref framesSent);
            }
        }
    }

    private void SignalOutbound()
    {
        try { outboundSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task ReceiveAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var (type, payload) = await ReadFrameAsync(stream, cancellationToken);
            if (type == PencilFrame)
            {
                try
                {
                    using var document = JsonDocument.Parse(payload);
                    Interlocked.Increment(ref inputFramesReceived);
                    await state.HandleInputAsync(document.RootElement, cancellationToken);
                }
                catch (JsonException)
                {
                    Interlocked.Increment(ref droppedInputFrames);
                }
            }
            else if (type != PingFrame)
            {
                Interlocked.Increment(ref droppedInputFrames);
                Console.Error.WriteLine($"USB ignored unknown control frame {type}.");
            }
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
        if (length > 16 * 1024 * 1024) throw new IOException("USB frame is too large.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        return (header[0], payload);
    }

    private static async Task RelayOutputAsync(
        StreamReader reader, int port, bool errorOutput, CancellationToken token)
    {
        string? previous = null;
        var previousAt = 0L;
        while (!token.IsCancellationRequested && await reader.ReadLineAsync(token) is { } line)
        {
            // Current iproxy prints every accepted local probe and every socket
            // request on stdout. The backend already reports meaningful state.
            if (!errorOutput) continue;
            var now = Environment.TickCount64;
            if (line == previous && now - previousAt < 5_000) continue;
            previous = line;
            previousAt = now;
            if (errorOutput) Console.Error.WriteLine($"[iproxy:{port}] {line}");
            else Console.WriteLine($"[iproxy:{port}] {line}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel(); frames.Writer.TryComplete(); metadata.Writer.TryComplete();
        if (runTask is not null) await runTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        outboundSignal.Dispose();
        shutdown.Dispose();
    }
}
