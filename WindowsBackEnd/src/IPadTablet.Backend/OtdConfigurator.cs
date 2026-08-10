using System.Diagnostics;
using System.Security.Cryptography;

namespace IPadTablet.Backend;

internal sealed class OtdConfigurator
{
    internal const string ToolPath = "IPadTablet.OpenTabletDriver.IPadPencilTool";
    private const string ToolName = "iPad Pencil Windows Device Hub";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly BackendOptions options;
    private readonly Func<bool> isInputConnected;
    private bool configured;

    public OtdConfigurator(BackendOptions options, Func<bool>? isInputConnected = null)
    {
        this.options = options;
        this.isInputConnected = isInputConnected ?? (() => false);
    }

    public async Task<bool> EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (!options.OtdAutoConfig || options.InputMode != "otd" || configured)
            return configured;
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Multiple transports may request setup while the first attempt is
            // still running. Coalesce them instead of re-enabling all OTD tools,
            // which disposes the active device hub and drops the Pencil pipe.
            if (configured) return true;
            var integration = FindIntegrationDirectory();
            var data = GetDataDirectory();
            var pluginChanged = IntegrationPluginChanged(integration, data);
            if (pluginChanged) await StopDaemonAsync(cancellationToken);
            InstallIntegration(integration, data);

            if (!await IsDaemonReadyAsync(cancellationToken))
            {
                if (!StartDaemon())
                {
                    Console.Error.WriteLine("OTD setup failed: OpenTabletDriver.Daemon.exe was not found beside the console client.");
                    return false;
                }
                for (var attempt = 0; attempt < 15 && !await IsDaemonReadyAsync(cancellationToken); attempt++)
                    await Task.Delay(500, cancellationToken);
            }
            if (!await IsDaemonReadyAsync(cancellationToken))
            {
                Console.Error.WriteLine("OTD setup failed: daemon did not become ready.");
                return false;
            }

            var tools = await RunAsync(cancellationToken, "gettools");
            if (!tools.Success || !tools.Message.Contains(ToolName, StringComparison.OrdinalIgnoreCase))
            {
                var enabled = await RunAsync(cancellationToken, "enabletools", ToolPath);
                if (!enabled.Success)
                {
                    Console.Error.WriteLine($"OTD setup failed while enabling {ToolPath}: {enabled.Message}");
                    return false;
                }
            }

            // Enabling the tool performs its one required initial detection.
            // If the tool was already enabled before this backend created the
            // pipe, request exactly one detection instead of one per retry.
            for (var attempt = 0; attempt < 8 && !isInputConnected(); attempt++)
                await Task.Delay(250, cancellationToken);
            if (!isInputConnected())
            {
                var detect = await RunAsync(cancellationToken, "detect");
                if (!detect.Success)
                    Console.WriteLine($"OTD waiting for virtual iPad tablet: {detect.Message}");
            }

            CommandResult output = new(false, "tablet profile is not ready");
            for (var attempt = 0; attempt < 12; attempt++)
            {
                output = await RunAsync(cancellationToken, "getoutputmode", options.OtdTablet);
                if (output.Success && isInputConnected()) break;
                await Task.Delay(250, cancellationToken);
            }
            if (output.Success && isInputConnected())
            {
                // A freshly detected profile defaults to Absolute Mode. Avoid
                // SetOutputMode when it is already correct: SetSettings also
                // reinitializes every enabled tool.
                var expectedAbsolute = options.OtdOutputMode.EndsWith(".AbsoluteMode", StringComparison.Ordinal);
                var changed = false;
                if (!expectedAbsolute || !output.Message.Contains("Absolute Mode", StringComparison.OrdinalIgnoreCase))
                {
                    var setOutput = await RunAsync(cancellationToken,
                        "setoutputmode", options.OtdTablet, options.OtdOutputMode);
                    if (!setOutput.Success)
                    {
                        Console.Error.WriteLine($"OTD setup failed while setting output mode: {setOutput.Message}");
                        return false;
                    }
                    changed = true;
                }
                if (changed) await RunAsync(cancellationToken, "savedefaultsettings");
                var settingsFile = Path.Combine(data, "settings.json");
                if (File.Exists(settingsFile))
                {
                    // This is the CLI equivalent of the UX "Apply" button.
                    // It reconstructs the output pipeline while the stable
                    // plugin hub keeps the Pencil pipe connected.
                    var save = await RunAsync(cancellationToken, "savedefaultsettings");
                    if (!save.Success)
                    {
                        Console.Error.WriteLine($"OTD setup failed while saving active settings: {save.Message}");
                        return false;
                    }
                    var apply = await RunAsync(cancellationToken, "loadsettings", settingsFile);
                    if (!apply.Success)
                    {
                        Console.Error.WriteLine($"OTD setup failed while applying settings: {apply.Message}");
                        return false;
                    }
                }
                configured = true;
                Console.WriteLine($"OTD ready and applied: {options.OtdTablet} -> {options.OtdOutputMode}; input pipe stable");
                return true;
            }
            Console.Error.WriteLine("OTD setup failed: the iPad plugin loaded, but no virtual tablet was detected.");
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"OTD setup failed: {error.Message}");
            return false;
        }
        finally { gate.Release(); }
    }

    public async Task MaintainAsync(CancellationToken cancellationToken)
    {
        if (!options.OtdAutoConfig || options.InputMode != "otd") return;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!configured || !isInputConnected())
            {
                configured = false;
                await EnsureAsync(cancellationToken);
            }
            try { await Task.Delay(3000, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private string FindIntegrationDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "otd"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "otd"))
        };
        return candidates.FirstOrDefault(path =>
                   File.Exists(Path.Combine(path, "IPadPencilWindowsHub.dll")) &&
                   File.Exists(Path.Combine(path, "Apple-iPad-Pro-Windows.json")))
               ?? throw new DirectoryNotFoundException(
                   "Bundled OTD integration was not found beside the backend folder.");
    }

    private string GetDataDirectory()
    {
        var programDirectory = Path.GetDirectoryName(Path.GetFullPath(options.OtdCli)) ?? string.Empty;
        var portable = Path.Combine(programDirectory, "userdata");
        return Directory.Exists(portable)
            ? portable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenTabletDriver");
    }

    private static void InstallIntegration(string source, string data)
    {
        var pluginRoot = Directory.CreateDirectory(Path.Combine(data, "Plugins")).FullName;
        var plugin = Directory.CreateDirectory(Path.Combine(pluginRoot, "IPadPencilWindowsHub")).FullName;
        var configurations = Directory.CreateDirectory(Path.Combine(data, "Configurations")).FullName;
        File.Copy(Path.Combine(source, "IPadPencilWindowsHub.dll"),
            Path.Combine(plugin, "IPadPencilWindowsHub.dll"), true);
        File.Copy(Path.Combine(source, "Apple-iPad-Pro-Windows.json"),
            Path.Combine(configurations, "Apple-iPad-Pro-Windows.json"), true);

        // v0.0.4 placed the assembly directly in Plugins, which OTD explicitly ignores.
        var legacy = Path.Combine(pluginRoot, "IPadPencilWindowsHub.dll");
        if (File.Exists(legacy)) File.Delete(legacy);
        Console.WriteLine($"OTD integration installed in {data}");
    }

    private static bool IntegrationPluginChanged(string source, string data)
    {
        var incoming = Path.Combine(source, "IPadPencilWindowsHub.dll");
        var installed = Path.Combine(data, "Plugins", "IPadPencilWindowsHub", "IPadPencilWindowsHub.dll");
        if (!File.Exists(installed)) return true;
        using var incomingStream = File.OpenRead(incoming);
        using var installedStream = File.OpenRead(installed);
        return !SHA256.HashData(incomingStream).SequenceEqual(SHA256.HashData(installedStream));
    }

    private bool StopDaemon()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.OtdCli)) ?? string.Empty;
        var expected = Path.GetFullPath(Path.Combine(directory, "OpenTabletDriver.Daemon.exe"));
        var stopped = false;
        foreach (var process in Process.GetProcessesByName("OpenTabletDriver.Daemon"))
        {
            try
            {
                if (!string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill(true);
                process.WaitForExit(5000);
                stopped = true;
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
        if (stopped) Console.WriteLine("OTD daemon restarted to load the updated iPad input plugin.");
        return stopped;
    }

    private async Task StopDaemonAsync(CancellationToken cancellationToken)
    {
        if (!StopDaemon()) return;
        for (var attempt = 0; attempt < 20 && await IsDaemonReadyAsync(cancellationToken); attempt++)
            await Task.Delay(100, cancellationToken);
    }

    private bool StartDaemon()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.OtdCli)) ?? string.Empty;
        var executable = Path.Combine(directory, "OpenTabletDriver.Daemon.exe");
        if (!File.Exists(executable)) return false;
        Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory
        });
        Console.WriteLine($"OTD daemon started: {executable}");
        return true;
    }

    private async Task<bool> IsDaemonReadyAsync(CancellationToken token) =>
        (await RunAsync(token, "getallsettings")).Success;

    private async Task<CommandResult> RunAsync(CancellationToken token, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(options.OtdCli)
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.OtdCli)) ?? AppContext.BaseDirectory
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) return new(false, "process did not start");
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(8), token);
            var message = string.Join(' ', new[] { await stdout, await stderr }
                .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
            var failedText = message.Contains("Daemon not running", StringComparison.OrdinalIgnoreCase)
                             || message.Contains("Cannot find profile", StringComparison.OrdinalIgnoreCase)
                             || message.Contains("No profile exists", StringComparison.OrdinalIgnoreCase)
                             || message.Contains("Invalid output mode", StringComparison.OrdinalIgnoreCase)
                             || message.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase);
            return new(process.ExitCode == 0 && !failedText, message);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception
                                      or TimeoutException)
        {
            return new(false, error.Message);
        }
    }

    private sealed record CommandResult(bool Success, string Message);
}
