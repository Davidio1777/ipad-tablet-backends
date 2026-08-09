using System;
using System.Collections.Generic;
using System.IO;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Components;
using OpenTabletDriver.Plugin.DependencyInjection;
using OpenTabletDriver.Plugin.Devices;

namespace IPadTablet.OpenTabletDriver;

[PluginName("iPad Pencil Device Hub")]
public sealed class IPadPencilTool : ITool
{
    [Resolved]
    public IDriver Driver { private get; set; } = null!;

    private static readonly object Sync = new();
    private static ICompositeDeviceHub? installedCompositeHub;
    private static IPadPencilHub? installedPencilHub;

    public bool Initialize()
    {
        var property = Driver.GetType().GetProperty("CompositeDeviceHub");
        var compositeHub = property?.GetValue(Driver) as ICompositeDeviceHub;
        if (compositeHub is null)
            return false;

        lock (Sync)
        {
            if (ReferenceEquals(installedCompositeHub, compositeHub) && installedPencilHub is not null)
                return true;

            // OTD 0.6.7's RootHub reports removals as additions. Register one
            // process-lifetime hub and reuse it across settings reloads to avoid
            // its SetSettings -> Dispose -> Detect recursion.
            var pencilHub = new IPadPencilHub();
            compositeHub.ConnectDeviceHub(pencilHub);
            pencilHub.Activate();
            installedCompositeHub = compositeHub;
            installedPencilHub = pencilHub;
        }

        Driver.Detect();
        return true;
    }

    public void Dispose() { }
}

internal sealed class IPadPencilHub : IDeviceHub
{
    private readonly IPadPencilEndpoint endpoint = new();

    public event EventHandler<DevicesChangedEventArgs>? DevicesChanged
    {
        add { }
        remove { }
    }

    public IEnumerable<IDeviceEndpoint> GetDevices()
    {
        // Keep the endpoint registered if OTD starts before the backend. Its
        // CanOpen property gates matching until /dev/ipad-pencil appears; a
        // later `otd detect` can then attach without reloading the plugin.
        yield return endpoint;
    }

    public void Activate() => endpoint.Active = true;
}

internal sealed class IPadPencilEndpoint : IDeviceEndpoint
{
    internal const string Path = "/dev/ipad-pencil";

    public int ProductID => 0xA1D0;
    internal bool Active { get; set; }

    public int VendorID => Active ? 0x1209 : 0;
    public int InputReportLength => 10;
    public int OutputReportLength => 0;
    public int FeatureReportLength => 0;
    public string Manufacturer => "Apple";
    public string ProductName => "iPad Pro (Apple Pencil)";
    public string FriendlyName => "Apple iPad Pro (Apple Pencil)";
    public string SerialNumber => "ipad-pencil-network";
    public string DevicePath => Path;
    public IDictionary<string, string> DeviceAttributes { get; } =
        new Dictionary<string, string>();

    public bool CanOpen
    {
        get
        {
            try
            {
                using var stream = OpenPath();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public IDeviceEndpointStream Open() => new IPadPencilStream(OpenPath());

    public string GetDeviceString(byte index) => index switch
    {
        1 => Manufacturer,
        2 => ProductName,
        3 => SerialNumber,
        _ => string.Empty,
    };

    private static FileStream OpenPath() => new(
        Path,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.ReadWrite,
        InputBufferSize,
        FileOptions.None
    );

    private const int InputBufferSize = 10;
}

internal sealed class IPadPencilStream(FileStream stream) : IDeviceEndpointStream
{
    private readonly FileStream stream = stream;

    public byte[] Read()
    {
        var report = new byte[10];
        var length = stream.Read(report, 0, report.Length);
        if (length == 0)
            throw new EndOfStreamException("The iPad Pencil HID endpoint was closed.");
        if (length != report.Length)
            Array.Resize(ref report, length);
        return report;
    }

    public void Write(byte[] buffer) =>
        throw new NotSupportedException("The iPad Pencil endpoint is input-only.");

    public void GetFeature(byte[] buffer) => Array.Clear(buffer);

    public void SetFeature(byte[] buffer) =>
        throw new NotSupportedException("The iPad Pencil endpoint has no feature reports.");

    public void Dispose() => stream.Dispose();
}
