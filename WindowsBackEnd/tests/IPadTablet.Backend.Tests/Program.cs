using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Text.Json;
using IPadTablet.Backend;

SecureDatagrams.AssertCompatibility();
await UdpRoundTripAsync();
await VerifyLegacyIproxyArgumentsAsync();
await VerifyCurrentIproxyArgumentsAsync();
await VerifyOtdPipeReportAsync();
VerifyAccessUnitFiltering();
await VerifyCaptureTimingArgumentsAsync();
if (Environment.GetEnvironmentVariable("IPAD_TABLET_BACKEND_EXE") is { Length: > 0 } executable)
    await PublishedExecutableRoundTripAsync(executable);
Console.WriteLine("Windows backend protocol tests passed.");

static async Task UdpRoundTripAsync()
{
    var videoPort = FreeUdpPort();
    var inputPort = FreeUdpPort();
    while (inputPort == videoPort) inputPort = FreeUdpPort();
    const string token = "windows-backend-test-token";
    const string session = "windows-test-session";
    var options = BackendOptions.Parse([
        "serve", "--host", "127.0.0.1", "--token", token,
        "--udp-video-port", videoPort.ToString(), "--udp-input-port", inputPort.ToString(),
        "--input-mode", "none", "--no-otd-auto-config"
    ]);

    await using var state = new BackendState(options);
    await using var bridge = new UdpBridge(options, state);
    state.Attach(bridge, null);
    bridge.Start();

    using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var crypto = new SecureDatagrams(token, "client-to-server", "server-to-client");
    var hello = JsonSerializer.SerializeToUtf8Bytes(new { type = "hello", session });
    await client.SendAsync(crypto.Seal(SecureDatagrams.ControlEnvelope, hello),
        new IPEndPoint(IPAddress.Loopback, videoPort));

    var response = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
    var packet = crypto.Open(SecureDatagrams.VideoEnvelope, response.Buffer)
        ?? throw new InvalidOperationException("UDP metadata response could not be decrypted.");
    if (packet.Length < 14 || !packet.AsSpan(0, 4).SequenceEqual("IPUD"u8)
        || packet[4] != 1 || packet[5] != 2
        || BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10)) != 0
        || BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12)) != 1)
        throw new InvalidOperationException("UDP metadata response has an invalid packet header.");
    using var metadata = JsonDocument.Parse(packet.AsMemory(14));
    if (metadata.RootElement.GetProperty("type").GetString() != "stream-info")
        throw new InvalidOperationException("UDP metadata response has the wrong payload.");

    var input = JsonSerializer.SerializeToUtf8Bytes(new
    {
        type = "input",
        session,
        payload = new { type = "stream-settings", enabled = false, videoEnabled = false }
    });
    using var inputClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    await inputClient.SendAsync(crypto.Seal(SecureDatagrams.ControlEnvelope, input),
        new IPEndPoint(IPAddress.Loopback, inputPort));
    for (var attempt = 0; attempt < 20 && state.VideoEnabled; attempt++)
        await Task.Delay(25);
    if (state.VideoEnabled) throw new InvalidOperationException("UDP input did not reach the backend state.");

    var pencil = JsonSerializer.SerializeToUtf8Bytes(new
    {
        type = "input",
        session,
        payload = new { type = "pencil", sequence = 1, phase = "move", x = 0.5, y = 0.5, pressure = 0.5 }
    });
    await inputClient.SendAsync(crypto.Seal(SecureDatagrams.ControlEnvelope, pencil),
        new IPEndPoint(IPAddress.Loopback, inputPort));
    for (var attempt = 0; attempt < 20 && state.InputSamples == 0; attempt++)
        await Task.Delay(25);
    if (state.InputSamples != 1 || bridge.InputPacketsReceived != 2 || bridge.DroppedInputPackets != 0)
        throw new InvalidOperationException("UDP Pencil telemetry did not count accepted input correctly.");
}

static async Task VerifyLegacyIproxyArgumentsAsync()
{
    var options = BackendOptions.Parse([
        "serve", "--no-udp", "--usb", "--iproxy", "C:\\Tools\\iproxy.exe",
        "--usb-port", "18765", "--input-mode", "none"
    ]);
    await using var state = new BackendState(options);
    await using var bridge = new UsbBridge(options, state);
    var info = bridge.BuildIproxyStartInfo();
    var arguments = info.ArgumentList.ToArray();
    if (!arguments.SequenceEqual(["-l", "18765", "18765"]))
        throw new InvalidOperationException("iproxy arguments are not compatible with Windows builds.");
}

static async Task VerifyCurrentIproxyArgumentsAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), $"ipad-tablet-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllBytes(Path.Combine(directory, "iproxy.exe"), []);
        File.WriteAllBytes(Path.Combine(directory, "libusbmuxd-2.0.dll"), []);
        var options = BackendOptions.Parse([
            "serve", "--no-udp", "--usb", "--iproxy", Path.Combine(directory, "iproxy.exe"),
            "--usb-port", "18765", "--input-mode", "none"
        ]);
        await using var state = new BackendState(options);
        await using var bridge = new UsbBridge(options, state);
        var arguments = bridge.BuildIproxyStartInfo("test-udid").ArgumentList.ToArray();
        if (!arguments.SequenceEqual(["-u", "test-udid", "18765:18765"]))
            throw new InvalidOperationException("iproxy arguments do not match current upstream syntax.");
    }
    finally { Directory.Delete(directory, true); }
}

static async Task VerifyOtdPipeReportAsync()
{
    var pipeName = $"ipad-pencil-test-{Guid.NewGuid():N}";
    await using var sink = new OtdPipePenSink(pipeName);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var server = sink.StartAsync(cancellation.Token);
    await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
    await client.ConnectAsync(cancellation.Token);
    using var message = JsonDocument.Parse("""
        {"type":"pencil","phase":"move","x":0.25,"y":0.75,"pressure":0.5,"altitude":1.5707963267948966,"azimuth":0}
        """);
    var report = new byte[10];
    var read = client.ReadExactlyAsync(report, cancellation.Token).AsTask();
    await sink.ApplyAsync(message.RootElement, cancellation.Token);
    await read;
    var x = BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(2));
    var y = BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(4));
    var pressure = BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(6));
    if (report[0] != 1 || x != 8192 || y != 24575 || pressure != 4096 || report[8] != 0 || report[9] != 0)
        throw new InvalidOperationException("The OTD pipe report does not match XP_PenTabletReport's byte layout.");
    cancellation.Cancel();
    await server.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
}

static void VerifyAccessUnitFiltering()
{
    var emptyAud = new byte[] { 0, 0, 0, 1, 9, 0xf0 };
    var frame = new byte[] { 0, 0, 0, 1, 9, 0xf0, 0, 0, 0, 1, 5, 0x88 };
    if (AnnexBAccessUnitReader.ContainsVclNal(emptyAud))
        throw new InvalidOperationException("An AUD-only unit was treated as a video frame.");
    if (!AnnexBAccessUnitReader.ContainsVclNal(frame))
        throw new InvalidOperationException("An IDR access unit was rejected.");
}

static async Task VerifyCaptureTimingArgumentsAsync()
{
    var options = BackendOptions.Parse([
        "serve", "--host", "127.0.0.1", "--token", "capture-timing-test-token",
        "--ffmpeg", "ffmpeg.exe", "--encoder", "h264_amf", "--fps", "120",
        "--capture", "auto", "--display-index", "2",
        "--input-mode", "none", "--no-otd-auto-config"
    ]);
    await using var pipeline = new CapturePipeline(options, _ => ValueTask.CompletedTask);
    var dda = pipeline.BuildStartInfo(options.BaseProfile, "ddagrab", true).Arguments;
    if (!dda.Contains("ddagrab=output_idx=2:draw_mouse=1:framerate=120:dup_frames=0", StringComparison.Ordinal)
        || !dda.Contains("hwdownload,format=bgra,scale=2560:1440:flags=fast_bilinear,format=nv12,eq=gamma=1.48:contrast=1.03,fps=fps=120:start_time=0:round=near", StringComparison.Ordinal)
        || !dda.Contains("-color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709", StringComparison.Ordinal)
        || !dda.Contains("video_full_range_flag=0:colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1", StringComparison.Ordinal)
        || !dda.Contains("-fps_mode passthrough", StringComparison.Ordinal)
        || dda.Contains("-r 120", StringComparison.Ordinal))
        throw new InvalidOperationException("Desktop Duplication capture is not synchronized correctly.");
    var ddaSdr = pipeline.BuildStartInfo(options.BaseProfile, "ddagrab", false).Arguments;
    if (ddaSdr.Contains("eq=gamma", StringComparison.Ordinal))
        throw new InvalidOperationException("HDR correction leaked into the SDR capture path.");
    var gdi = pipeline.BuildStartInfo(options.BaseProfile, "gdigrab").Arguments;
    if (!gdi.Contains("-framerate 60", StringComparison.Ordinal)
        || !gdi.Contains("-draw_mouse 1", StringComparison.Ordinal)
        || !gdi.Contains("-r 60 -fps_mode cfr", StringComparison.Ordinal)
        || gdi.Contains("-r 120", StringComparison.Ordinal))
        throw new InvalidOperationException("GDI fallback timing is not normalized correctly.");
}

static async Task PublishedExecutableRoundTripAsync(string executable)
{
    var videoPort = FreeUdpPort();
    var inputPort = FreeUdpPort();
    while (inputPort == videoPort) inputPort = FreeUdpPort();
    const string token = "published-backend-test-token";
    var info = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    foreach (var argument in new[]
    {
        "serve", "--host", "127.0.0.1", "--token", token,
        "--udp-video-port", videoPort.ToString(), "--udp-input-port", inputPort.ToString(),
        "--input-mode", "none", "--no-otd-auto-config", "--encoder", "libx264",
        "--ffmpeg", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
    }) info.ArgumentList.Add(argument);

    using var process = Process.Start(info)
        ?? throw new InvalidOperationException("Published backend executable did not start.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    UdpReceiveResult? response = null;
    try
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var crypto = new SecureDatagrams(token, "client-to-server", "server-to-client");
        // Self-contained single-file apps can spend several seconds in first-run
        // extraction and antivirus scanning before managed Main begins.
        for (var attempt = 0; attempt < 60 && response is null; attempt++)
        {
            var hello = JsonSerializer.SerializeToUtf8Bytes(new
                { type = "hello", session = "published-test-session" });
            await client.SendAsync(crypto.Seal(SecureDatagrams.ControlEnvelope, hello),
                new IPEndPoint(IPAddress.Loopback, videoPort));
            using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try { response = await client.ReceiveAsync(receiveTimeout.Token); }
            catch (OperationCanceledException) { }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { }
            if (response is null) await Task.Delay(500);
        }
        if (response is not null
            && crypto.Open(SecureDatagrams.VideoEnvelope, response.Value.Buffer) is null)
            throw new InvalidOperationException("Published backend returned an invalid UDP response.");
    }
    finally
    {
        if (!process.HasExited) process.Kill(true);
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
    }
    if (response is null)
        throw new InvalidOperationException(
            $"Published backend did not answer on its UDP video port. Exit {process.ExitCode}.\n" +
            $"stdout:\n{await stdout}\nstderr:\n{await stderr}");
}

static int FreeUdpPort()
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
}
