using IPadTablet.Shared;

namespace IPadTablet.Backend;

internal sealed record CaptureProfile(
    int Width, int Height, int Fps, int Bitrate, string RateControl, bool GamingMode);

internal sealed class BackendOptions
{
    public string Host { get; private set; } = "0.0.0.0";
    public string Token { get; private set; } = Environment.GetEnvironmentVariable("IPAD_TABLET_TOKEN") ?? "";
    public string Ffmpeg { get; private set; } = "ffmpeg.exe";
    public string Encoder { get; private set; } = "auto";
    public bool NoUdp { get; private set; }
    public int UdpVideoPort { get; private set; } = 8766;
    public int UdpInputPort { get; private set; } = 8767;
    public bool Usb { get; private set; }
    public string Iproxy { get; private set; } = "iproxy.exe";
    public int UsbPort { get; private set; } = 18765;
    public int SourceX { get; private set; }
    public int SourceY { get; private set; }
    public int SourceWidth { get; private set; } = 2560;
    public int SourceHeight { get; private set; } = 1440;
    public string InputMode { get; private set; } = "otd";
    public bool OtdAutoConfig { get; private set; } = true;
    public string OtdCli { get; private set; } = "otd.exe";
    public string OtdTablet { get; private set; } = "Apple iPad Pro (Apple Pencil)";
    public string OtdOutputMode { get; private set; } = "OpenTabletDriver.Desktop.Output.AbsoluteMode";
    public CaptureProfile BaseProfile { get; private set; } =
        new(2560, 1440, 60, 16_000_000, "cbr", false);

    public static BackendOptions Parse(string[] args)
    {
        var options = new BackendOptions();
        int IntValue(ref int index, string name)
        {
            if (++index >= args.Length || !int.TryParse(args[index], out var value))
                throw new ArgumentException($"{name} expects an integer.");
            return value;
        }
        string Value(ref int index, string name)
        {
            if (++index >= args.Length) throw new ArgumentException($"{name} expects a value.");
            return args[index];
        }

        var width = options.BaseProfile.Width;
        var height = options.BaseProfile.Height;
        var fps = options.BaseProfile.Fps;
        var bitrate = options.BaseProfile.Bitrate;
        var rateControl = options.BaseProfile.RateControl;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "serve": break;
                case "--host": options.Host = Value(ref i, args[i]); break;
                case "--token": options.Token = Value(ref i, args[i]); break;
                case "--ffmpeg": options.Ffmpeg = Value(ref i, args[i]); break;
                case "--encoder": options.Encoder = Value(ref i, args[i]).ToLowerInvariant(); break;
                case "--no-udp": options.NoUdp = true; break;
                case "--udp-video-port": options.UdpVideoPort = IntValue(ref i, args[i]); break;
                case "--udp-input-port": options.UdpInputPort = IntValue(ref i, args[i]); break;
                case "--usb": options.Usb = true; break;
                case "--iproxy": options.Iproxy = Value(ref i, args[i]); break;
                case "--usb-port": options.UsbPort = IntValue(ref i, args[i]); break;
                case "--source-x": options.SourceX = IntValue(ref i, args[i]); break;
                case "--source-y": options.SourceY = IntValue(ref i, args[i]); break;
                case "--source-width": options.SourceWidth = IntValue(ref i, args[i]); break;
                case "--source-height": options.SourceHeight = IntValue(ref i, args[i]); break;
                case "--width": width = IntValue(ref i, args[i]); break;
                case "--height": height = IntValue(ref i, args[i]); break;
                case "--fps": fps = IntValue(ref i, args[i]); break;
                case "--bitrate": bitrate = IntValue(ref i, args[i]); break;
                case "--rate-control": rateControl = Value(ref i, args[i]).ToLowerInvariant(); break;
                case "--input-mode": options.InputMode = Value(ref i, args[i]).ToLowerInvariant(); break;
                case "--no-otd-auto-config": options.OtdAutoConfig = false; break;
                case "--otd-cli": options.OtdCli = Value(ref i, args[i]); break;
                case "--otd-tablet": options.OtdTablet = Value(ref i, args[i]); break;
                case "--otd-output-mode": options.OtdOutputMode = Value(ref i, args[i]); break;
                case "--help": case "-h": PrintHelp(); Environment.Exit(0); break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        if (!options.NoUdp && System.Text.Encoding.UTF8.GetByteCount(options.Token) < 16)
            throw new ArgumentException("Encrypted UDP requires a token containing at least 16 UTF-8 bytes.");
        if (options.NoUdp && !options.Usb)
            throw new ArgumentException("At least encrypted UDP or USB must be enabled.");
        if (options.InputMode is not ("otd" or "none"))
            throw new ArgumentException("--input-mode must be otd or none.");
        if (rateControl is not ("cbr" or "vbr"))
            throw new ArgumentException("--rate-control must be cbr or vbr.");
        options.BaseProfile = new(width & ~1, height & ~1, Math.Clamp(fps, 30, 120),
            Math.Clamp(bitrate, 1_000_000, 50_000_000), rateControl, false);
        options.Ffmpeg = WindowsExecutableLocator.Find(options.Ffmpeg, WindowsTool.Ffmpeg)
            ?? options.Ffmpeg;
        options.OtdCli = WindowsExecutableLocator.Find(
            options.OtdCli, WindowsTool.OpenTabletDriverConsole) ?? options.OtdCli;
        if (options.Usb)
            options.Iproxy = WindowsExecutableLocator.Find(options.Iproxy, WindowsTool.Iproxy)
                ?? options.Iproxy;
        return options;
    }

    private static void PrintHelp() => Console.WriteLine("""
        Windows 11 iPad Tablet Backend
        ipad-tablet-backend serve [options]

          --host 0.0.0.0 --token LONG_RANDOM_TOKEN
          --encoder auto|h264_amf|h264_nvenc|h264_qsv|libx264
          --source-x 0 --source-y 0 --source-width 2560 --source-height 1440
          --width 2560 --height 1440 --fps 60 --bitrate 16000000
          --rate-control cbr|vbr --input-mode otd|none
          --udp-video-port 8766 --udp-input-port 8767 [--no-udp]
          --usb [--iproxy C:\Path\iproxy.exe --usb-port 18765]
          --otd-cli otd.exe [--no-otd-auto-config]
        """);
}
