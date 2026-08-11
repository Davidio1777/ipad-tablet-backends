using IPadTablet.Backend;

BackendOptions options;
try { options = BackendOptions.Parse(args); }
catch (ArgumentException error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine("Use --help to list all options.");
    return 2;
}
if (!options.NoUdp) SecureDatagrams.AssertCompatibility();

await using var state = new BackendState(options);
await using var udp = options.NoUdp ? null : new UdpBridge(options, state);
await using var usb = options.Usb ? new UsbBridge(options, state) : null;
state.Attach(udp, usb);
udp?.Start();
usb?.Start();
await state.StartAsync();

Console.WriteLine("RayShine Windows backend is running.");
Console.WriteLine($"Input: {options.InputMode}; encrypted UDP: {!options.NoUdp}; USB: {options.Usb}");
Console.WriteLine("Press Ctrl+C to stop.");

var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopped.TrySetResult();
};
await stopped.Task;
return 0;
