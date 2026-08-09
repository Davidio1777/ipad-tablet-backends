using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
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
            var destination = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTabletDriver");
            var plugins = Directory.CreateDirectory(Path.Combine(destination, "Plugins")).FullName;
            var configurations = Directory.CreateDirectory(Path.Combine(destination, "Configurations")).FullName;
            File.Copy(Path.Combine(source, "IPadPencilWindowsHub.dll"),
                Path.Combine(plugins, "IPadPencilWindowsHub.dll"), true);
            File.Copy(Path.Combine(source, "Apple-iPad-Pro-Windows.json"),
                Path.Combine(configurations, "Apple-iPad-Pro-Windows.json"), true);
            AppendLog($"Installed iPad OTD integration in {destination}");
            MessageBox.Show(this,
                "The iPad OTD plugin and tablet configuration are installed. Restart OpenTabletDriver and click Detect.",
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
        Add(info, "--encoder", Selected(EncoderBox));
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
