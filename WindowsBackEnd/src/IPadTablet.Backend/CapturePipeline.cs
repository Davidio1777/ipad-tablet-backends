using System.Diagnostics;

namespace IPadTablet.Backend;

internal sealed class CapturePipeline : IAsyncDisposable
{
    private readonly BackendOptions options;
    private readonly Func<byte[], ValueTask> onFrame;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource? runCancellation;
    private Task? runTask;
    private string selectedEncoder = "unknown";
    private string selectedCapture = "unknown";
    private bool hdrColorCorrection;

    public string Encoder => selectedEncoder;
    public string Backend => selectedCapture;

    public CapturePipeline(BackendOptions options, Func<byte[], ValueTask> onFrame)
    {
        this.options = options;
        this.onFrame = onFrame;
    }

    public async Task StartAsync(CaptureProfile profile, CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (runTask is not null) return;
            selectedEncoder = await ResolveEncoderAsync(cancellationToken);
            selectedCapture = options.CaptureBackend is "auto" or "dda" ? "ddagrab" : "gdigrab";
            hdrColorCorrection = selectedCapture == "ddagrab" &&
                                 await DetectHdrDesktopAsync(cancellationToken);
            if (hdrColorCorrection)
                Console.WriteLine("HDR desktop detected: applying calibrated scRGB-to-SDR correction.");
            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runTask = RunAsync(profile, runCancellation.Token);
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (runTask is null) return;
            runCancellation!.Cancel();
            await runTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            runCancellation.Dispose();
            runCancellation = null;
            runTask = null;
        }
        finally { lifecycle.Release(); }
    }

    private async Task RunAsync(CaptureProfile profile, CancellationToken cancellationToken)
    {
        var captureBackend = selectedCapture;
        var ddaWasHealthy = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var process = new Process { StartInfo = BuildStartInfo(profile, captureBackend) };
            process.Start();
            selectedCapture = captureBackend;
            Console.WriteLine($"Capture ({captureBackend}): {options.Ffmpeg} {process.StartInfo.Arguments}");
            var stderr = RelayErrorsAsync(process.StandardError, cancellationToken);
            var frameCount = 0L;
            try
            {
                var reader = new AnnexBAccessUnitReader(process.StandardOutput.BaseStream);
                var intervalFrames = 0L;
                var reportAt = Stopwatch.GetTimestamp();
                await foreach (var frame in reader.ReadAsync(cancellationToken))
                {
                    frameCount++;
                    if (captureBackend == "ddagrab") ddaWasHealthy = true;
                    intervalFrames++;
                    if (frameCount == 1)
                        Console.WriteLine($"Capture ready: first H.264 frame ({frame.Length:N0} bytes)");
                    else if (Stopwatch.GetElapsedTime(reportAt) >= TimeSpan.FromSeconds(5))
                    {
                        var elapsed = Stopwatch.GetElapsedTime(reportAt).TotalSeconds;
                        Console.WriteLine($"Capture healthy: {intervalFrames / elapsed:F1} FPS, " +
                                          $"{frameCount:N0} frames encoded");
                        intervalFrames = 0;
                        reportAt = Stopwatch.GetTimestamp();
                    }
                    await onFrame(frame);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
                await stderr.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            if (!cancellationToken.IsCancellationRequested)
            {
                if (captureBackend == "ddagrab" && ddaWasHealthy)
                {
                    Console.Error.WriteLine(
                        "Desktop Duplication capture was interrupted; recreating it in 500 ms.");
                    await Task.Delay(500, cancellationToken);
                    continue;
                }
                if (captureBackend == "ddagrab" && options.CaptureBackend == "auto")
                {
                    captureBackend = "gdigrab";
                    selectedCapture = captureBackend;
                    Console.Error.WriteLine("Desktop Duplication capture ended; falling back to GDI capture.");
                    continue;
                }
                Console.Error.WriteLine("Capture wurde beendet; neuer Versuch in 2 Sekunden.");
                await Task.Delay(2000, cancellationToken);
            }
        }
    }

    internal ProcessStartInfo BuildStartInfo(CaptureProfile profile, string? captureBackend = null,
        bool? hdrColorCorrectionOverride = null)
    {
        captureBackend ??= options.CaptureBackend is "auto" or "dda" ? "ddagrab" : "gdigrab";
        var encoder = selectedEncoder == "unknown" ? options.Encoder : selectedEncoder;
        var applyHdrColorCorrection = hdrColorCorrectionOverride ?? hdrColorCorrection;
        // GDI capture tops out around one desktop refresh on this Windows path.
        // Never advertise synthetic 120 FPS by duplicating every GDI frame.
        var captureFps = Math.Min(profile.Fps, 60);
        var outputFps = captureBackend == "ddagrab" ? profile.Fps : captureFps;
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostdin"
        };
        if (captureBackend == "ddagrab")
        {
            args.AddRange(["-f", "lavfi", "-i",
                $"ddagrab=output_idx={options.DisplayIndex}:draw_mouse=1:framerate={profile.Fps}:dup_frames=0"]);
            // scale_d3d11 cannot create an NV12 texture on current AMD RDNA4
            // drivers. Downloading the DDA frame before scaling lets AMF upload
            // it reliably. The fps filter creates fresh monotonic timestamps;
            // ddagrab's own duplicated frames can otherwise carry equal DTS.
            var colorCorrection = applyHdrColorCorrection ? ",eq=gamma=1.48:contrast=1.03" : "";
            var filter = $"hwdownload,format=bgra,scale={profile.Width}:{profile.Height}:" +
                         $"flags=fast_bilinear,format=nv12{colorCorrection}," +
                         $"fps=fps={profile.Fps}:start_time=0:round=near";
            args.AddRange(["-vf", filter]);
        }
        else
        {
            args.AddRange(["-f", "gdigrab", "-draw_mouse", "1", "-framerate", captureFps.ToString(),
                "-offset_x", options.SourceX.ToString(), "-offset_y", options.SourceY.ToString(),
                "-video_size", $"{options.SourceWidth}x{options.SourceHeight}", "-i", "desktop",
                "-vf", $"scale={profile.Width}:{profile.Height}:flags=fast_bilinear,format=nv12"]);
        }
        args.AddRange(["-an", "-color_range", "tv", "-colorspace", "bt709",
            "-color_primaries", "bt709", "-color_trc", "bt709",
            "-c:v", encoder, "-g", outputFps.ToString(), "-bf", "0"]);
        var bitrate = profile.Bitrate.ToString();
        switch (encoder)
        {
            case "h264_amf":
                args.AddRange(["-usage", "ultralowlatency", "-quality", "speed", "-rc",
                    profile.RateControl == "cbr" ? "cbr" : "vbr_peak", "-b:v", bitrate,
                    "-maxrate", bitrate, "-bufsize", Math.Max(profile.Bitrate / profile.Fps * 2, 100_000).ToString(),
                    "-frame_skipping", "false", "-preanalysis", "false", "-async_depth", "2", "-aud", "false"]);
                break;
            case "h264_nvenc":
                args.AddRange(["-preset", "p1", "-tune", "ull", "-rc",
                    profile.RateControl == "cbr" ? "cbr" : "vbr", "-b:v", bitrate,
                    "-maxrate", bitrate, "-bufsize", Math.Max(profile.Bitrate / profile.Fps * 2, 100_000).ToString(),
                    "-zerolatency", "1"]);
                break;
            case "h264_qsv":
                args.AddRange(["-preset", "veryfast", "-low_delay_brc", "1", "-b:v", bitrate,
                    "-maxrate", bitrate, "-bufsize", Math.Max(profile.Bitrate / profile.Fps * 2, 100_000).ToString()]);
                break;
            default:
                args.AddRange(["-preset", "ultrafast", "-tune", "zerolatency", "-b:v", bitrate,
                    "-maxrate", bitrate, "-bufsize", Math.Max(profile.Bitrate / profile.Fps * 2, 100_000).ToString(),
                    "-x264-params", "aud=1:repeat-headers=1:bframes=0:scenecut=0:sync-lookahead=0"]);
                break;
        }
        // Hardware encoders can emit their own AUD while the old pipeline inserted
        // another one. Empty access units then alternated with real frames. Strip all
        // encoder AUDs, repeat SPS/PPS at keyframes, and insert exactly one canonical AUD.
        args.AddRange(["-bsf:v",
            "filter_units=remove_types=9,dump_extra=freq=keyframe," +
            "h264_metadata=aud=insert:video_full_range_flag=0:colour_primaries=1:" +
            "transfer_characteristics=1:matrix_coefficients=1"]);
        if (captureBackend == "ddagrab")
            args.AddRange(["-fps_mode", "passthrough"]);
        else
            // gdigrab reports a 1 MHz time base and occasionally repeats or
            // regresses timestamps. Normalize at its real capture cadence.
            args.AddRange(["-r", captureFps.ToString(), "-fps_mode", "cfr"]);
        args.AddRange(["-f", "h264", "pipe:1"]);
        return new ProcessStartInfo(options.Ffmpeg, JoinArguments(args))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    private async Task<string> ResolveEncoderAsync(CancellationToken cancellationToken)
    {
        if (options.Encoder != "auto") return options.Encoder;
        foreach (var encoder in new[] { "h264_amf", "h264_nvenc", "h264_qsv", "libx264" })
        {
            if (!await ProbeEncoderAsync(encoder, cancellationToken)) continue;
            Console.WriteLine($"Encoder probe selected: {encoder}");
            return encoder;
        }
        throw new InvalidOperationException("FFmpeg could not initialize a supported H.264 encoder.");
    }

    private async Task<bool> DetectHdrDesktopAsync(CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(options.Ffmpeg)
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "verbose", "-nostdin", "-f", "lavfi", "-i",
            $"ddagrab=output_idx={options.DisplayIndex}:draw_mouse=0:framerate=1:" +
            "output_fmt=16bit:allow_fallback=0:dup_frames=0",
            "-frames:v", "1", "-f", "null", "-"
        }) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("FFmpeg HDR probe could not be started.");
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                var output = await errors;
                return process.ExitCode == 0 &&
                       output.Contains("Probed 16 bit float RGB frame format", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (TimeoutException)
        {
            Console.WriteLine("HDR desktop probe timed out; continuing without HDR correction.");
            return false;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"HDR desktop probe unavailable: {error.Message}");
            return false;
        }
    }

    private async Task<bool> ProbeEncoderAsync(string encoder, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(options.Ffmpeg)
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i",
            "color=size=128x128:rate=1", "-frames:v", "1", "-vf", "format=nv12",
            "-c:v", encoder, "-f", "null", "-"
        }) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("FFmpeg could not be started.");
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                if (process.ExitCode == 0) return true;
                Console.WriteLine($"Encoder probe skipped {encoder}: {(await errors).Trim()}");
                return false;
            }
            finally
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"Encoder probe timed out: {encoder}");
            return false;
        }
    }

    private static async Task RelayErrorsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && await reader.ReadLineAsync(cancellationToken) is { } line)
            Console.Error.WriteLine($"[capture] {line}");
    }

    private static string JoinArguments(IEnumerable<string> args) => string.Join(' ', args.Select(arg =>
        arg.Any(char.IsWhiteSpace) || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg));

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lifecycle.Dispose();
    }
}

internal sealed class AnnexBAccessUnitReader(Stream stream)
{
    private readonly List<byte> buffer = new(256 * 1024);

    public async IAsyncEnumerable<byte[]> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            while (FindSecondAud(buffer) is var boundary && boundary > 0)
            {
                var frame = buffer.GetRange(0, boundary).ToArray();
                buffer.RemoveRange(0, boundary);
                if (ContainsVclNal(frame)) yield return frame;
            }
        }
        if (buffer.Count > 0)
        {
            var frame = buffer.ToArray();
            if (ContainsVclNal(frame)) yield return frame;
        }
    }

    private static int FindSecondAud(List<byte> data)
    {
        var foundFirst = false;
        for (var i = 0; i + 4 < data.Count; i++)
        {
            var startLength = data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 ? 3 :
                i + 4 < data.Count && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1 ? 4 : 0;
            if (startLength == 0 || (data[i + startLength] & 0x1f) != 9) continue;
            if (foundFirst) return i;
            foundFirst = true;
            i += startLength;
        }
        return -1;
    }

    internal static bool ContainsVclNal(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 4 < data.Length; i++)
        {
            var startLength = data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 ? 3 :
                data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1 ? 4 : 0;
            if (startLength == 0) continue;
            var type = data[i + startLength] & 0x1f;
            if (type is >= 1 and <= 5) return true;
            i += startLength;
        }
        return false;
    }
}
