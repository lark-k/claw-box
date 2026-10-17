using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace ClawBox.Desktop.Services;

public sealed record NearbyBoard(ulong Address, string Name, short Signal)
{
    public override string ToString() => $"{Name}   ·   信号{(Signal > -65 ? "良好" : "较弱，请靠近")}";
}
public sealed record WirelessNetwork(string Ssid, string Bytes, int Strength, bool Secured, bool Saved = false)
{
    public override string ToString() => $"{Ssid}   ·   信号 {Strength}%{(Saved ? "   已保存" : "")}{(Secured ? "" : "   开放网络")}";
    public bool IsCurrent(WirelessStatus current)
        => current.Connected && (current.SsidBytes is not null ? current.SsidBytes == Bytes : current.Ssid == Ssid);
}
public sealed record WirelessStatus(bool Connected, string[] Addresses, string? Ssid, string? SsidBytes = null);

public interface IWirelessLink : IDisposable
{
    Task<JsonElement> Exchange(JsonElement message, CancellationToken cancellation);
    Task<JsonElement> Info(CancellationToken cancellation);
}

public sealed class BluetoothNetwork : IWirelessLink
{
    public static readonly Guid ServiceId = new("4e170001-8f64-4a12-9d85-1c3a25dc4d01");
    private static readonly Guid InfoId = new("4e170002-8f64-4a12-9d85-1c3a25dc4d01");
    private static readonly Guid RequestId = new("4e170003-8f64-4a12-9d85-1c3a25dc4d01");
    private static readonly Guid ResponseId = new("4e170004-8f64-4a12-9d85-1c3a25dc4d01");
    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattSession? gattSession;
    private GattCharacteristic? info, request, response;
    private bool disposed;
    public static async Task<NearbyBoard[]> Discover(CancellationToken cancellation)
    {
        BluetoothAdapter? adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(cancellation);
        if (adapter is null || !adapter.IsLowEnergySupported || (await adapter.GetRadioAsync().AsTask(cancellation)).State != RadioState.On)
            throw new WirelessException("bluetooth_unavailable");
        ConcurrentDictionary<ulong, NearbyBoard> boards = new();
        BluetoothLEAdvertisementWatcher watcher = new() { ScanningMode = BluetoothLEScanningMode.Active, AllowExtendedAdvertisements = true };
        watcher.Received += (_, args) =>
        {
            if (!args.Advertisement.ServiceUuids.Contains(ServiceId) && !boards.ContainsKey(args.BluetoothAddress)) return;
            string name = args.Advertisement.LocalName;
            if (string.IsNullOrWhiteSpace(name)) name = boards.TryGetValue(args.BluetoothAddress, out NearbyBoard? old) ? old.Name : "ClawBox";
            boards[args.BluetoothAddress] = new(args.BluetoothAddress, name, args.RawSignalStrengthInDBm);
        };
        try
        {
            watcher.Start();
            await Task.Delay(TimeSpan.FromSeconds(12), cancellation);
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted) throw new WirelessException("bluetooth_unavailable");
            return boards.Values.OrderByDescending(d => d.Signal).ToArray();
        }
        finally { watcher.Stop(); }
    }
    public static async Task<BluetoothNetwork> Connect(ulong address, CancellationToken cancellation)
    {
        BluetoothNetwork link = new();
        using CancellationTokenRegistration cancel = cancellation.Register(link.Dispose);
        try
        {
            link.device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Public).AsTask(cancellation)
                ?? throw new WirelessException("bluetooth_connection");
            GattDeviceServicesResult services = await link.device.GetGattServicesForUuidAsync(ServiceId, BluetoothCacheMode.Uncached).AsTask(cancellation);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count != 1) throw new WirelessException("bluetooth_connection");
            link.service = services.Services[0];
            link.gattSession = link.service.Session;
            GattCharacteristicsResult characteristics = await link.service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellation);
            if (characteristics.Status != GattCommunicationStatus.Success) throw new WirelessException("bluetooth_connection");
            link.info = characteristics.Characteristics.Single(c => c.Uuid == InfoId);
            link.request = characteristics.Characteristics.Single(c => c.Uuid == RequestId);
            link.response = characteristics.Characteristics.Single(c => c.Uuid == ResponseId);
            cancellation.ThrowIfCancellationRequested();
            return link;
        }
        catch { link.Dispose(); throw; }
    }
    private static async Task<byte[]> Read(GattCharacteristic characteristic, CancellationToken cancellation)
    {
        GattReadResult result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellation);
        if (result.Status != GattCommunicationStatus.Success) throw new WirelessException("bluetooth_connection");
        using DataReader reader = DataReader.FromBuffer(result.Value);
        byte[] data = new byte[reader.UnconsumedBufferLength]; reader.ReadBytes(data); return data;
    }
    public async Task<JsonElement> Info(CancellationToken cancellation)
    {
        using JsonDocument document = JsonDocument.Parse(await Read(info!, cancellation));
        return document.RootElement.Clone();
    }
    public async Task<JsonElement> Exchange(JsonElement message, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        byte[] raw = [.. WirelessProtocol.Canonical(message), (byte)'\n'];
        if (raw.Length > 8192) throw new WirelessException("invalid_message");
        int count = Math.Clamp((int)(gattSession?.MaxPduSize ?? 23) - 3, 20, 180);
        for (int offset = 0; offset < raw.Length; offset += count)
        {
            using DataWriter writer = new(); writer.WriteBytes(raw.AsSpan(offset, Math.Min(count, raw.Length - offset)).ToArray());
            GattWriteResult result = await request!.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse).AsTask(cancellation);
            if (result.Status != GattCommunicationStatus.Success) throw new WirelessException("bluetooth_connection");
        }
        using MemoryStream buffer = new();
        uint? total = null;
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(150))
        {
            byte[] frame = await Read(response!, cancellation);
            if (frame.Length < 8) throw new WirelessException("invalid_frame");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(frame), offset = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4));
            if (length == 0) { await Task.Delay(120, cancellation); continue; }
            if (length > 8192 || offset != buffer.Length || total is not null && length != total || frame.Length == 8)
                throw new WirelessException("invalid_frame");
            total = length; buffer.Write(frame.AsSpan(8));
            if (buffer.Length > total) throw new WirelessException("invalid_frame");
            if (buffer.Length == total)
            {
                using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
                await Task.Delay(120, cancellation);
                return document.RootElement.Clone();
            }
        }
        throw new WirelessException("response_timeout");
    }
    public void Dispose()
    {
        disposed = true; info = request = response = null;
        gattSession?.Dispose(); gattSession = null;
        service?.Dispose(); service = null; device?.Dispose(); device = null;
    }
}

public sealed class WirelessSession(IWirelessLink link, WirelessProtocol protocol) : IDisposable
{
    private bool failed;
    public string Identity => protocol.Identity;
    public string HostKey => protocol.HostKey;
    public async Task Authenticate(CancellationToken cancellation)
    {
        try { _ = protocol.Decrypt(await link.Exchange(protocol.Accept(await link.Exchange(protocol.Hello, cancellation)), cancellation)); }
        catch { failed = true; Dispose(); throw; }
    }
    public async Task<JsonElement> Send<T>(T value, CancellationToken cancellation)
    {
        if (failed) throw new WirelessException("session_expired");
        try { return protocol.Decrypt(await link.Exchange(protocol.Encrypt(WirelessProtocol.Element(value)), cancellation)); }
        catch (WirelessException error) when (error.Code is "wifi_connection_failed" or "wifi_connection_timeout" or "invalid_password" or "desktop_setup_failed" or "saved_network_missing") { throw; }
        catch { failed = true; Dispose(); throw; }
    }
    public async Task<WirelessStatus> Status(CancellationToken cancellation)
    {
        JsonElement value = await Send(new { op = "status" }, cancellation);
        return new(value.GetProperty("connected").GetBoolean(), value.GetProperty("addresses").EnumerateArray().Select(a => a.GetString()!).ToArray(),
            value.TryGetProperty("ssid", out JsonElement name) ? name.GetString() : null,
            value.TryGetProperty("ssid_bytes", out JsonElement bytes) ? bytes.GetString() : null);
    }
    public async Task<WirelessNetwork[]> Scan(CancellationToken cancellation)
    {
        JsonElement value = await Send(new { op = "scan" }, cancellation);
        return value.GetProperty("networks").EnumerateArray().Select(n => new WirelessNetwork(n.GetProperty("ssid").GetString()!,
            n.GetProperty("ssid_bytes").GetString()!, n.GetProperty("strength").GetInt32(), n.GetProperty("security").GetString() != "open",
            n.TryGetProperty("saved", out JsonElement saved) && saved.GetBoolean())).ToArray();
    }
    public void Dispose() { link.Dispose(); protocol.Dispose(); }
}
