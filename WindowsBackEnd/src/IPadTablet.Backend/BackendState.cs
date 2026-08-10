using System.Text.Json;

namespace IPadTablet.Backend;

internal sealed class BackendState : IAsyncDisposable
{
    private readonly BackendOptions options;
    private readonly IPenSink pen;
    private readonly CapturePipeline capture;
    private readonly SemaphoreSlim settingsLock = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly CaptureProfile baseProfile;
    private readonly OtdConfigurator otd;
    private CaptureProfile profile;
    private UdpBridge? udp;
    private UsbBridge? usb;
    private Task? penTask;
    private Task? healthTask;
    private Task? otdTask;
    private long frames;
    private long inputSamples;
    private int streamRevision;

    public bool VideoEnabled { get; private set; } = true;
    public string InputMode => options.InputMode;
    public long Frames => Interlocked.Read(ref frames);
    public long InputSamples => Interlocked.Read(ref inputSamples);
    public long InputEvents => pen.EventsReceived;
    public bool PenConnected => pen.Connected;

    public BackendState(BackendOptions options)
    {
        this.options = options;
        baseProfile = options.BaseProfile;
        profile = baseProfile;
        pen = options.InputMode == "otd" ? new OtdPipePenSink() : new NullPenSink();
        capture = new CapturePipeline(options, PublishFrameAsync);
        otd = new OtdConfigurator(options, () => pen.Connected);
    }

    public void Attach(UdpBridge? udpBridge, UsbBridge? usbBridge)
    {
        udp = udpBridge;
        usb = usbBridge;
    }

    public async Task StartAsync()
    {
        penTask = pen.StartAsync(shutdown.Token);
        healthTask = ReportHealthAsync(shutdown.Token);
        otdTask = otd.MaintainAsync(shutdown.Token);
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
        encoder = capture.Encoder,
        captureBackend = capture.Backend
    };

    public object Health => new
    {
        status = "ok",
        frames = Frames,
        inputMode = InputMode,
        inputEvents = InputEvents,
        inputSamples = InputSamples,
        inputConnected = PenConnected,
        udpEnabled = udp is not null,
        udpClients = udp?.ConnectedClients ?? 0,
        udpFrames = udp?.FramesSent ?? 0,
        usbEnabled = usb is not null,
        usbConnected = usb?.Connected ?? false,
        usbFrames = usb?.FramesSent ?? 0,
        Metadata
    };

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
                if (!sample.TryGetProperty("type", out var sampleType)
                    || sampleType.GetString() != "pencil") continue;
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

    private async Task ReportHealthAsync(CancellationToken cancellationToken)
    {
        var previousSamples = InputSamples;
        var previousReports = InputEvents;
        var previousUdp = 0L;
        var previousUsb = 0L;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var samples = InputSamples;
                var reports = InputEvents;
                var udpPackets = udp?.InputPacketsReceived ?? 0;
                var usbFrames = usb?.InputFramesReceived ?? 0;
                Console.WriteLine(
                    $"Input health: samples +{samples - previousSamples}, OTD reports +{reports - previousReports} " +
                    $"(pipe {(PenConnected ? "connected" : "waiting")}), UDP input +{udpPackets - previousUdp} " +
                    $"(dropped {udp?.DroppedInputPackets ?? 0}), USB input +{usbFrames - previousUsb} " +
                    $"(dropped {usb?.DroppedInputFrames ?? 0}, {(usb?.Connected == true ? "connected" : "waiting")})");
                previousSamples = samples;
                previousReports = reports;
                previousUdp = udpPackets;
                previousUsb = usbFrames;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

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
            Console.WriteLine($"Stream profile: {(video ? gaming ? "gaming" : "quality" : "tablet-only")}, " +
                $"{next.Width}x{next.Height} @ {next.Fps}, {next.Bitrate / 1_000_000} Mbit/s {next.RateControl.ToUpperInvariant()}");
            PublishMetadata();
            if (video) await capture.StartAsync(profile, shutdown.Token);
        }
        finally { settingsLock.Release(); }
    }

    private ValueTask PublishFrameAsync(byte[] frame)
    {
        Interlocked.Increment(ref frames);
        udp?.Offer(frame);
        usb?.Offer(frame);
        return ValueTask.CompletedTask;
    }

    private void PublishMetadata()
    {
        udp?.PublishMetadata();
        usb?.PublishMetadata();
    }

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
        if (healthTask is not null) await healthTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (otdTask is not null) await otdTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        settingsLock.Dispose();
        shutdown.Dispose();
    }
}
