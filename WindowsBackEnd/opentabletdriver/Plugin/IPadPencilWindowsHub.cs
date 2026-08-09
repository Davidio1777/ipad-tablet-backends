using System.IO.Pipes;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Components;
using OpenTabletDriver.Plugin.DependencyInjection;
using OpenTabletDriver.Plugin.Devices;

namespace IPadTablet.OpenTabletDriver.Windows;

[PluginName("iPad Pencil Windows Device Hub")]
public sealed class IPadPencilTool : ITool
{
    [Resolved] public IDriver Driver { private get; set; } = null!;
    private static readonly object Sync = new();
    private static ICompositeDeviceHub? installedHub;

    public bool Initialize()
    {
        var composite = Driver.GetType().GetProperty("CompositeDeviceHub")?.GetValue(Driver) as ICompositeDeviceHub;
        if (composite is null) return false;
        lock (Sync)
        {
            if (!ReferenceEquals(installedHub, composite))
            {
                composite.ConnectDeviceHub(new IPadPencilHub());
                installedHub = composite;
            }
        }
        Driver.Detect();
        return true;
    }
    public void Dispose() { }
}

internal sealed class IPadPencilHub : IDeviceHub
{
    public event EventHandler<DevicesChangedEventArgs>? DevicesChanged { add { } remove { } }
    public IEnumerable<IDeviceEndpoint> GetDevices() { yield return new IPadPencilEndpoint(); }
}

internal sealed class IPadPencilEndpoint : IDeviceEndpoint
{
    public int VendorID => 0x1209;
    public int ProductID => 0xA1D0;
    public int InputReportLength => 10;
    public int OutputReportLength => 0;
    public int FeatureReportLength => 0;
    public string Manufacturer => "Apple";
    public string ProductName => "iPad Pro (Apple Pencil)";
    public string FriendlyName => ProductName;
    public string SerialNumber => "ipad-pencil-windows";
    public string DevicePath => @"\\.\pipe\ipad-pencil";
    public IDictionary<string, string> DeviceAttributes { get; } = new Dictionary<string, string>();
    public bool CanOpen => true;
    public IDeviceEndpointStream Open()
    {
        var pipe = new NamedPipeClientStream(".", "ipad-pencil", PipeDirection.In, PipeOptions.None);
        pipe.Connect(1500);
        return new IPadPencilStream(pipe);
    }
    public string GetDeviceString(byte index) => index switch
    {
        1 => Manufacturer, 2 => ProductName, 3 => SerialNumber, _ => string.Empty
    };
}

internal sealed class IPadPencilStream(NamedPipeClientStream stream) : IDeviceEndpointStream
{
    public byte[] Read()
    {
        var report = new byte[10];
        var read = 0;
        while (read < report.Length)
        {
            var length = stream.Read(report, read, report.Length - read);
            if (length == 0) throw new EndOfStreamException("iPad Pencil Pipe wurde geschlossen.");
            read += length;
        }
        return report;
    }
    public void Write(byte[] buffer) => throw new NotSupportedException();
    public void GetFeature(byte[] buffer) => Array.Clear(buffer);
    public void SetFeature(byte[] buffer) => throw new NotSupportedException();
    public void Dispose() => stream.Dispose();
}
