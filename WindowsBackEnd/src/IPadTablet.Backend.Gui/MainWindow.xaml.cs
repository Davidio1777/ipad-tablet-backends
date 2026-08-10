using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using IPadTablet.Shared;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Screen = System.Windows.Forms.Screen;

namespace IPadTablet.Backend.Gui;

public partial class MainWindow : Window
{
    private sealed record DisplayOption(string DeviceName, int X, int Y, int Width, int Height)
    {
        public override string ToString() => $"{DeviceName} — {Width}×{Height} at {X},{Y}";
    }

    private Process? _backend;

    public MainWindow()
    {
        InitializeComponent();
        TokenBox.Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        FfmpegBox.Text = WindowsExecutableLocator.Find("ffmpeg.exe", WindowsTool.Ffmpeg) ?? "ffmpeg.exe";
        OtdCliBox.Text = WindowsExecutableLocator.Find(
            "OpenTabletDriver.Console.exe", WindowsTool.OpenTabletDriverConsole)
            ?? "OpenTabletDriver.Console.exe";
        IproxyBox.Text = WindowsExecutableLocator.Find("iproxy.exe", WindowsTool.Iproxy)
            ?? "iproxy.exe";
        RefreshScreens();
        Closing += (_, _) => StopBackend();
    }

    private void RefreshScreens_Click(object sender, RoutedEventArgs e) => RefreshScreens();

    private void RefreshScreens()
    {
        var previous = (ScreenBox.SelectedItem as DisplayOption)?.DeviceName;
        ScreenBox.Items.Clear();
        foreach (var screen in Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            ScreenBox.Items.Add(new DisplayOption(screen.DeviceName, bounds.X, bounds.Y, bounds.Width, bounds.Height));
        }
        ScreenBox.SelectedItem = ScreenBox.Items.Cast<DisplayOption>()
            .FirstOrDefault(display => display.DeviceName == previous) ?? ScreenBox.Items.Cast<object>().FirstOrDefault();
    }

    private void ScreenBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScreenBox.SelectedItem is not DisplayOption display) return;
        SourceXBox.Text = display.X.ToString();
        SourceYBox.Text = display.Y.ToString();
        SourceWidthBox.Text = display.Width.ToString();
        SourceHeightBox.Text = display.Height.ToString();
    }

    private void GenerateToken_Click(object sender, RoutedEventArgs e) =>
        TokenBox.Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private void InstallOtd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var source = FindOtdDirectory();
            OtdCliBox.Text = RequireExecutable(OtdCliBox.Text,
                WindowsTool.OpenTabletDriverConsole, "Select OpenTabletDriver.Console.exe",
                "OpenTabletDriver console|OpenTabletDriver.Console.exe;otd.exe");
            var otdDirectory = Path.GetDirectoryName(Path.GetFullPath(OtdCliBox.Text))!;
            var portable = Path.Combine(otdDirectory, "userdata");
            var destination = Directory.Exists(portable) ? portable : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTabletDriver");
            var pluginRoot = Directory.CreateDirectory(Path.Combine(destination, "Plugins")).FullName;
            var plugins = Directory.CreateDirectory(Path.Combine(pluginRoot, "IPadPencilWindowsHub")).FullName;
            var configurations = Directory.CreateDirectory(Path.Combine(destination, "Configurations")).FullName;
            File.Copy(Path.Combine(source, "IPadPencilWindowsHub.dll"),
                Path.Combine(plugins, "IPadPencilWindowsHub.dll"), true);
            File.Copy(Path.Combine(source, "Apple-iPad-Pro-Windows.json"),
                Path.Combine(configurations, "Apple-iPad-Pro-Windows.json"), true);
            var legacy = Path.Combine(pluginRoot, "IPadPencilWindowsHub.dll");
            if (File.Exists(legacy)) File.Delete(legacy);
            AppendLog($"Installed iPad OTD integration in {destination}");
            MessageBox.Show(this,
                "The iPad OTD integration was repaired. Start the backend; it will start the OTD daemon, enable the plugin, and detect the tablet automatically.",
                "Installation complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "OTD installation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_backend is { HasExited: false }) return;
        try
        {
            var executable = FindBackend();
            FfmpegBox.Text = RequireExecutable(
                FfmpegBox.Text, WindowsTool.Ffmpeg, "Select ffmpeg.exe", "ffmpeg.exe|ffmpeg.exe");
            if (OtdEnabled.IsChecked == true)
                OtdCliBox.Text = RequireExecutable(OtdCliBox.Text,
                    WindowsTool.OpenTabletDriverConsole, "Select OpenTabletDriver.Console.exe",
                    "OpenTabletDriver console|OpenTabletDriver.Console.exe;otd.exe");
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            AddArguments(info);
            _backend = new Process { StartInfo = info, EnableRaisingEvents = true };
            _backend.OutputDataReceived += (_, args) => AppendLog(args.Data);
            _backend.ErrorDataReceived += (_, args) => AppendLog(args.Data);
            _backend.Exited += (_, _) => Dispatcher.Invoke(() => SetRunning(false));
            if (!_backend.Start()) throw new InvalidOperationException("The backend process did not start.");
            _backend.BeginOutputReadLine();
            _backend.BeginErrorReadLine();
            SetRunning(true);
            AppendLog($"Started {executable}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to start backend", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddArguments(ProcessStartInfo info)
    {
        info.ArgumentList.Add("serve");
        Add(info, "--ffmpeg", FfmpegBox.Text);
        Add(info, "--encoder", Selected(EncoderBox));
        Add(info, "--capture", "auto");
        Add(info, "--display-index", Math.Max(0, ScreenBox.SelectedIndex).ToString());
        Add(info, "--source-x", SourceXBox.Text);
        Add(info, "--source-y", SourceYBox.Text);
        Add(info, "--source-width", SourceWidthBox.Text);
        Add(info, "--source-height", SourceHeightBox.Text);
        Add(info, "--width", WidthBox.Text);
        Add(info, "--height", HeightBox.Text);
        Add(info, "--fps", FpsBox.Text);
        Add(info, "--bitrate", BitrateBox.Text);
        Add(info, "--rate-control", Selected(RateBox));

        if (UdpEnabled.IsChecked == true)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(TokenBox.Password) < 16)
                throw new InvalidOperationException("The encrypted UDP token must contain at least 16 UTF-8 bytes.");
            info.Environment["IPAD_TABLET_TOKEN"] = TokenBox.Password;
            Add(info, "--udp-video-port", VideoPortBox.Text);
            Add(info, "--udp-input-port", InputPortBox.Text);
        }
        else info.ArgumentList.Add("--no-udp");

        if (UsbEnabled.IsChecked == true)
        {
            IproxyBox.Text = RequireExecutable(
                IproxyBox.Text, WindowsTool.Iproxy, "Select iproxy.exe", "iproxy.exe|iproxy.exe");
            info.ArgumentList.Add("--usb");
            Add(info, "--iproxy", IproxyBox.Text);
        }
        if (UdpEnabled.IsChecked != true && UsbEnabled.IsChecked != true)
            throw new InvalidOperationException("Enable encrypted UDP, USB, or both.");

        if (OtdEnabled.IsChecked == true)
        {
            Add(info, "--otd-cli", OtdCliBox.Text);
            Add(info, "--otd-tablet", OtdTabletBox.Text);
        }
        else info.ArgumentList.Add("--no-otd-auto-config");
    }

    private static void Add(ProcessStartInfo info, string option, string value)
    {
        info.ArgumentList.Add(option);
        info.ArgumentList.Add(value.Trim());
    }

    private static string Selected(System.Windows.Controls.ComboBox box) =>
        (box.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "";

    private static string FindBackend()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ipad-tablet-backend.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "backend", "ipad-tablet-backend.exe")),
            Path.Combine(AppContext.BaseDirectory, "IPadTablet.Backend.exe")
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("ipad-tablet-backend.exe was not found. Keep the gui and backend folders together.");
    }

    private static string FindOtdDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "otd"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "otd"))
        };
        return candidates.FirstOrDefault(path =>
            File.Exists(Path.Combine(path, "IPadPencilWindowsHub.dll")) &&
            File.Exists(Path.Combine(path, "Apple-iPad-Pro-Windows.json")))
            ?? throw new DirectoryNotFoundException("The bundled OTD integration folder was not found. Keep gui, backend and otd together.");
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e) =>
        FfmpegBox.Text = BrowseExecutable("Select ffmpeg.exe", "ffmpeg.exe|ffmpeg.exe") ?? FfmpegBox.Text;

    private void BrowseOtd_Click(object sender, RoutedEventArgs e) =>
        OtdCliBox.Text = BrowseExecutable("Select OpenTabletDriver.Console.exe",
            "OpenTabletDriver console|OpenTabletDriver.Console.exe;otd.exe") ?? OtdCliBox.Text;

    private void BrowseIproxy_Click(object sender, RoutedEventArgs e) =>
        IproxyBox.Text = BrowseExecutable("Select iproxy.exe", "iproxy.exe|iproxy.exe") ?? IproxyBox.Text;

    private async void RepairFirewall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(VideoPortBox.Text, out var videoPort) || videoPort is < 1 or > 65535
                || !int.TryParse(InputPortBox.Text, out var inputPort) || inputPort is < 1 or > 65535)
                throw new InvalidOperationException("UDP ports must be numbers between 1 and 65535.");

            const string ruleName = "iPad Tablet Backend UDP (Private)";
            var backend = FindBackend().Replace("'", "''");
            var script = $"$ErrorActionPreference='Stop'; " +
                $"Get-NetFirewallRule -DisplayName '{ruleName}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; " +
                $"New-NetFirewallRule -DisplayName '{ruleName}' -Direction Inbound -Action Allow " +
                $"-Enabled True -Profile Private -Protocol UDP -LocalPort {videoPort},{inputPort} " +
                $"-Program '{backend}' | Out-Null";
            var exitCode = await RunElevatedPowerShellAsync(script);
            if (exitCode != 0) throw new InvalidOperationException($"Firewall repair exited with code {exitCode}.");
            AppendLog($"Repaired private Windows Firewall rule for UDP {videoPort}/{inputPort}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Firewall repair failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task<int> RunElevatedPowerShellAsync(string script)
    {
        var info = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Windows Firewall could not be opened.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private string RequireExecutable(string configured, WindowsTool tool, string title, string filter)
    {
        var detected = WindowsExecutableLocator.Find(configured, tool);
        if (detected is not null)
        {
            AppendLog($"Detected {tool}: {detected}");
            return detected;
        }
        return BrowseExecutable(title, filter)
            ?? throw new FileNotFoundException($"{title.Replace("Select ", "")} was not found. Select its location to continue.");
    }

    private string? BrowseExecutable(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopBackend();

    private void StopBackend()
    {
        if (_backend is not { HasExited: false }) return;
        _backend.Kill(entireProcessTree: true);
        _backend.WaitForExit(3_000);
        SetRunning(false);
        AppendLog("Backend stopped.");
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        StatusText.Text = running ? "Running" : "Stopped";
        StatusText.Foreground = running ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Gray;
    }
}
