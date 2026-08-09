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

    public string Encoder => selectedEncoder;

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
        while (!cancellationToken.IsCancellationRequested)
        {
            using var process = new Process { StartInfo = BuildStartInfo(profile) };
            process.Start();
            Console.WriteLine($"Capture: {options.Ffmpeg} {process.StartInfo.Arguments}");
            var stderr = RelayErrorsAsync(process.StandardError, cancellationToken);
            try
            {
                var reader = new AnnexBAccessUnitReader(process.StandardOutput.BaseStream);
                await foreach (var frame in reader.ReadAsync(cancellationToken))
                    await onFrame(frame);
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
                Console.Error.WriteLine("Capture wurde beendet; neuer Versuch in 2 Sekunden.");
                await Task.Delay(2000, cancellationToken);
            }
        }
    }

    private ProcessStartInfo BuildStartInfo(CaptureProfile profile)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostdin",
            "-f", "gdigrab", "-draw_mouse", "0", "-framerate", profile.Fps.ToString(),
            "-offset_x", options.SourceX.ToString(), "-offset_y", options.SourceY.ToString(),
            "-video_size", $"{options.SourceWidth}x{options.SourceHeight}", "-i", "desktop",
            "-vf", $"scale={profile.Width}:{profile.Height}:flags=fast_bilinear,format=nv12",
            "-an", "-c:v", selectedEncoder, "-g", profile.Fps.ToString(), "-bf", "0"
        };
        var bitrate = profile.Bitrate.ToString();
        switch (selectedEncoder)
        {
            case "h264_amf":
                args.AddRange(["-usage", "ultralowlatency", "-quality", "speed", "-rc",
                    profile.RateControl == "cbr" ? "cbr" : "vbr_peak", "-b:v", bitrate,
                    "-maxrate", bitrate, "-bufsize", Math.Max(profile.Bitrate / profile.Fps * 2, 100_000).ToString()]);
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
                    "-x264-params", "bframes=0:scenecut=0:sync-lookahead=0"]);
                break;
        }
        args.AddRange(["-bsf:v", "h264_metadata=aud=insert", "-f", "h264", "pipe:1"]);
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
        var info = new ProcessStartInfo(options.Ffmpeg, "-hide_banner -encoders")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg could not be started.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        foreach (var encoder in new[] { "h264_amf", "h264_nvenc", "h264_qsv", "libx264" })
            if (output.Contains(encoder, StringComparison.Ordinal)) return encoder;
        throw new InvalidOperationException("FFmpeg does not contain a supported H.264 encoder.");
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
                yield return frame;
            }
        }
        if (buffer.Count > 0) yield return buffer.ToArray();
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
}
