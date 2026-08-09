using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace IPadTablet.Backend;

internal readonly record struct StreamItem(bool IsText, byte[] Data);

internal sealed class BackendState : IAsyncDisposable
{
    private readonly BackendOptions options;
    private readonly IPenSink pen;
    private readonly CapturePipeline capture;
    private readonly SemaphoreSlim settingsLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Channel<StreamItem>> clients = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly CaptureProfile baseProfile;
    private CaptureProfile profile;
    private UdpBridge? udp;
    private UsbBridge? usb;
    private Task? penTask;
    private long frames;
    private long inputSamples;
    private int streamRevision;

    public bool VideoEnabled { get; private set; } = true;
    public string InputMode => options.InputMode;
    public int StreamClients => clients.Count;
    public long Frames => Interlocked.Read(ref frames);
    public long InputSamples => Interlocked.Read(ref inputSamples);
    public long InputEvents => pen.EventsReceived;

    public BackendState(BackendOptions options)
    {
        this.options = options;
        baseProfile = options.BaseProfile;
        profile = baseProfile;
        pen = options.InputMode == "otd" ? new OtdPipePenSink() : new NullPenSink();
        capture = new CapturePipeline(options, PublishFrameAsync);
    }

    public void Attach(UdpBridge? udpBridge, UsbBridge? usbBridge)
    {
        udp = udpBridge;
        usb = usbBridge;
    }

    public async Task StartAsync()
    {
        penTask = pen.StartAsync(shutdown.Token);
        if (VideoEnabled) await capture.StartAsync(profile, shutdown.Token);
    }

    public object Metadata => new
    {
        type = "stream-info",
        width = profile.Width,
        height = profile.Height,
        fps = profile.Fps,
        bitrate = profile.Bitrate,
        rateControl = profile.RateControl,
        gamingMode = profile.GamingMode,
        videoEnabled = VideoEnabled,
        streamRevision,
        encoder = capture.Encoder
    };

    public object Health => new
    {
        status = "ok",
        streamClients = StreamClients,
        frames = Frames,
        inputMode = InputMode,
        inputEvents = InputEvents,
        inputSamples = InputSamples,
        udpEnabled = udp is not null,
        udpClients = udp?.ConnectedClients ?? 0,
        udpFrames = udp?.FramesSent ?? 0,
        usbEnabled = usb is not null,
        usbConnected = usb?.Connected ?? false,
        usbFrames = usb?.FramesSent ?? 0,
        Metadata
    };

    public (Guid Id, ChannelReader<StreamItem> Reader) AddClient()
    {
        var channel = Channel.CreateBounded<StreamItem>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var id = Guid.NewGuid();
        clients[id] = channel;
        channel.Writer.TryWrite(TextItem(Metadata));
        return (id, channel.Reader);
    }

    public void RemoveClient(Guid id)
    {
        if (clients.TryRemove(id, out var channel)) channel.Writer.TryComplete();
    }

    public async Task HandleInputAsync(JsonElement message, CancellationToken cancellationToken = default)
    {
        if (!message.TryGetProperty("type", out var typeElement)) return;
        var type = typeElement.GetString();
        if (type == "stream-settings")
        {
            await ApplyStreamSettingsAsync(message, cancellationToken);
            return;
        }
        if (type == "pencil-batch" && message.TryGetProperty("samples", out var samples)
            && samples.ValueKind == JsonValueKind.Array)
        {
            foreach (var sample in samples.EnumerateArray().Take(512))
            {
                Interlocked.Increment(ref inputSamples);
                await pen.ApplyAsync(sample, cancellationToken);
            }
            return;
        }
        if (type is "pencil" or "button")
        {
            Interlocked.Increment(ref inputSamples);
            await pen.ApplyAsync(message, cancellationToken);
        }
    }

    public ValueTask ReleaseInputAsync(CancellationToken cancellationToken = default) =>
        pen.ReleaseAsync(cancellationToken);

    private async Task ApplyStreamSettingsAsync(JsonElement message, CancellationToken cancellationToken)
    {
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            var gaming = GetBool(message, "enabled");
            var video = !message.TryGetProperty("videoEnabled", out var videoElement)
                || videoElement.ValueKind != JsonValueKind.False;
            var next = gaming
                ? new CaptureProfile(
                    Even(GetInt(message, "width", 1280), 640, 3840),
                    Even(GetInt(message, "height", 720), 360, 2160),
                    Math.Clamp(GetInt(message, "fps", 120), 30, 120),
                    Math.Clamp(GetInt(message, "bitrate", 8_000_000), 1_000_000, 50_000_000),
                    GetString(message, "rateControl", "cbr") is "vbr" ? "vbr" : "cbr", true)
                : baseProfile;
            if (next == profile && video == VideoEnabled) return;

            await capture.StopAsync();
            profile = next;
            VideoEnabled = video;
            streamRevision++;
            Console.WriteLine($"Stream-Profil: {(video ? gaming ? "Gaming" : "Qualität" : "Nur Tablet")}, " +
                $"{next.Width}x{next.Height} @ {next.Fps}, {next.Bitrate / 1_000_000} Mbit/s {next.RateControl.ToUpperInvariant()}");
            PublishMetadata();
            if (video) await capture.StartAsync(profile, shutdown.Token);
        }
        finally { settingsLock.Release(); }
    }

    private ValueTask PublishFrameAsync(byte[] frame)
    {
        Interlocked.Increment(ref frames);
        var item = new StreamItem(false, frame);
        foreach (var channel in clients.Values) channel.Writer.TryWrite(item);
        udp?.Offer(frame);
        usb?.Offer(frame);
        return ValueTask.CompletedTask;
    }

    private void PublishMetadata()
    {
        var item = TextItem(Metadata);
        foreach (var channel in clients.Values) channel.Writer.TryWrite(item);
        udp?.PublishMetadata();
        usb?.PublishMetadata();
    }

    private static StreamItem TextItem(object value) => new(true, JsonSerializer.SerializeToUtf8Bytes(value));
    private static int Even(int value, int min, int max) => Math.Clamp(value, min, max) & ~1;
    private static int GetInt(JsonElement e, string key, int fallback) =>
        e.TryGetProperty(key, out var p) && p.TryGetInt32(out var v) ? v : fallback;
    private static bool GetBool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
    private static string GetString(JsonElement e, string key, string fallback) =>
        e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()!.ToLowerInvariant() : fallback;

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        await capture.DisposeAsync();
        await pen.ReleaseAsync();
        await pen.DisposeAsync();
        if (penTask is not null) await penTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        settingsLock.Dispose();
        shutdown.Dispose();
    }
}
