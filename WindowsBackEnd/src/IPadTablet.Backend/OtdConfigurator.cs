using System.Diagnostics;

namespace IPadTablet.Backend;

internal sealed class OtdConfigurator(BackendOptions options)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool configured;

    public async Task<bool> EnsureAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!options.OtdAutoConfig || options.InputMode != "otd" || configured && !force)
            return configured;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (configured && !force) return true;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (await RunAsync(cancellationToken, "detect")
                    && await RunAsync(cancellationToken, "setoutputmode", options.OtdTablet, options.OtdOutputMode))
                {
                    await RunAsync(cancellationToken, "savedefaultsettings");
                    configured = true;
                    Console.WriteLine($"OTD configured: {options.OtdTablet} -> {options.OtdOutputMode}");
                    return true;
                }
                await Task.Delay(1000, cancellationToken);
            }
            Console.Error.WriteLine("OTD auto-config failed; start the daemon and enable the iPad plugin.");
            return false;
        }
        finally { gate.Release(); }
    }

    private async Task<bool> RunAsync(CancellationToken token, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(options.OtdCli)
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) return false;
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            return process.ExitCode == 0;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception
                                      or TimeoutException)
        {
            Console.Error.WriteLine($"OTD {string.Join(' ', arguments)}: {error.Message}");
            return false;
        }
    }
}
