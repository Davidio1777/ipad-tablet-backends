namespace IPadTablet.Backend;

internal sealed record CaptureProfile(
    int Width, int Height, int Fps, int Bitrate, string RateControl, bool GamingMode);

internal sealed class BackendOptions
{
    public string Host { get; private set; } = "0.0.0.0";
    public int Port { get; private set; } = 8765;
    public string Token { get; private set; } = "";
    public string Ffmpeg { get; private set; } = "ffmpeg.exe";
    public string Encoder { get; private set; } = "auto";
    public bool Udp { get; private set; }
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
    public CaptureProfile BaseProfile { get; private set; } =
        new(2560, 1440, 60, 16_000_000, "cbr", false);

    public static BackendOptions Parse(string[] args)
    {
        var options = new BackendOptions();
        int IntValue(ref int index, string name)
        {
            if (++index >= args.Length || !int.TryParse(args[index], out var value))
                throw new ArgumentException($"{name} erwartet eine Ganzzahl.");
            return value;
        }
        string Value(ref int index, string name)
        {
            if (++index >= args.Length) throw new ArgumentException($"{name} erwartet einen Wert.");
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
                case "--port": options.Port = IntValue(ref i, args[i]); break;
                case "--token": options.Token = Value(ref i, args[i]); break;
                case "--ffmpeg": options.Ffmpeg = Value(ref i, args[i]); break;
                case "--encoder": options.Encoder = Value(ref i, args[i]).ToLowerInvariant(); break;
                case "--udp": options.Udp = true; break;
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
                case "--help": case "-h": PrintHelp(); Environment.Exit(0); break;
                default: throw new ArgumentException($"Unbekannte Option: {args[i]}");
            }
        }
        if (options.Udp && string.IsNullOrWhiteSpace(options.Token))
            throw new ArgumentException("--udp benötigt einen nicht leeren --token.");
        if (options.InputMode is not ("otd" or "none"))
            throw new ArgumentException("--input-mode muss otd oder none sein.");
        if (rateControl is not ("cbr" or "vbr"))
            throw new ArgumentException("--rate-control muss cbr oder vbr sein.");
        options.BaseProfile = new(width & ~1, height & ~1, Math.Clamp(fps, 30, 120),
            Math.Clamp(bitrate, 1_000_000, 50_000_000), rateControl, false);
        return options;
    }

    private static void PrintHelp() => Console.WriteLine("""
        Windows 11 iPad Tablet Backend
        ipad-tablet-backend serve [Optionen]

          --host 0.0.0.0 --port 8765 --token TOKEN
          --encoder auto|h264_amf|h264_nvenc|h264_qsv|libx264
          --source-x 0 --source-y 0 --source-width 2560 --source-height 1440
          --width 2560 --height 1440 --fps 60 --bitrate 16000000
          --rate-control cbr|vbr --input-mode otd|none
          --udp [--udp-video-port 8766 --udp-input-port 8767]
          --usb [--iproxy C:\Pfad\iproxy.exe --usb-port 18765]
        """);
}
