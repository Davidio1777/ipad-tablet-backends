using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace IPadTablet.Backend.Gui;

public partial class MainWindow : Window
{
    private Process? _backend;

    public MainWindow()
    {
        InitializeComponent();
        TokenBox.Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Closing += (_, _) => StopBackend();
    }

    private void GenerateToken_Click(object sender, RoutedEventArgs e) =>
        TokenBox.Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

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

    private static string Selected(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

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
