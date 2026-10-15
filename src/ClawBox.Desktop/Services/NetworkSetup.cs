using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClawBox.Desktop.Services;

public sealed record AdapterAddress(string Address, string Mask);
public sealed record NetworkAdapter(string Id, string Name, string Description, bool Up, bool Wired, bool HasGateway, AdapterAddress[] Addresses)
{
    public string Display => $"{Name} · {(Up ? "已连接" : "未连接")} · " + (Addresses.Length > 0 ? string.Join("，", Addresses.Select(a => a.Address)) : "暂无 IPv4");
}
public static class NetworkSetup
{
    public static bool IsDhcp(string id) => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(a => a.Id == id)?.GetIPProperties().GetIPv4Properties()?.IsDhcpEnabled == true;
    public static NetworkAdapter[] ReadAdapters()
    {
        List<NetworkAdapter> result = [];
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet && !EligibleWired(adapter.Name, adapter.Description)) continue;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet && !PhysicalWired(adapter.Id)) continue;
            try
            {
                IPInterfaceProperties properties = adapter.GetIPProperties();
                AdapterAddress[] addresses = properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => new AdapterAddress(a.Address.ToString(), a.IPv4Mask.ToString())).ToArray();
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet) addresses = ManualAddresses(adapter.Id) ?? addresses;
                result.Add(new(adapter.Id, adapter.Name, adapter.Description, adapter.OperationalStatus == OperationalStatus.Up,
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet,
                    properties.GatewayAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.Equals(IPAddress.Any)),
                    addresses));
            }
            catch (NetworkInformationException) { }
        }
        return result.ToArray();
    }
    private static bool PhysicalWired(string id)
    {
        using RegistryKey? connection = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\" + id + @"\Connection");
        string device = connection?.GetValue("PnpInstanceID") as string ?? "";
        return device.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || device.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
    }
    private static AdapterAddress[]? ManualAddresses(string id)
    {
        using RegistryKey? config = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + id);
        if (config?.GetValue("EnableDHCP") is not int dhcp || dhcp != 0) return null;
        string[] addresses = config.GetValue("IPAddress") as string[] ?? [];
        string[] masks = config.GetValue("SubnetMask") as string[] ?? [];
        return addresses.Zip(masks, (address, mask) => new AdapterAddress(address, mask)).Where(a => Ipv4(a.Address, out _) && ValidMask(a.Mask)).Distinct().ToArray();
    }
    public static bool EligibleWired(string name, string description)
        => !name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) && !new[] { "Virtual", "Hyper-V", "VPN", "TAP-", "TUN", "Loopback", "VMware", "Bluetooth", "Filter", "QoS", "Kernel Debug" }.Any(word => description.Contains(word, StringComparison.OrdinalIgnoreCase));
    public static bool SameSubnet(string first, string second, string mask)
    {
        if (!Ipv4(first, out byte[] a) || !Ipv4(second, out byte[] b) || !Ipv4(mask, out byte[] m)) return false;
        return Enumerable.Range(0, 4).All(i => (a[i] & m[i]) == (b[i] & m[i]));
    }
    public static SetupUpdate Diagnose(string host, NetworkAdapter[] adapters)
    {
        if (!Ipv4(host, out _)) return new("subnet", "waiting", "地址为主机名时，由设备端口检查确认可达；以太网 IP 设置适用于已知 IPv4 地址的直连板子。");
        if (adapters.Any(a => a.Addresses.Any(ip => ip.Address == host))) return new("subnet", "missing", "设备地址与电脑网卡地址相同，可能发生 IP 冲突。点击“设置以太网 IP”，为电脑选择不同的同网段地址。");
        NetworkAdapter[] wired = adapters.Where(a => a.Wired).ToArray();
        if (wired.Length == 0) return new("subnet", "missing", "没有检测到有线网卡。请连接网线或 USB 转网口，安装网卡驱动后重试。");
        NetworkAdapter? matched = wired.FirstOrDefault(a => a.Up && a.Addresses.Any(ip => SameSubnet(ip.Address, host, ip.Mask)));
        string Addresses(NetworkAdapter adapter) => adapter.Addresses.Length == 0 ? "暂无 IPv4" : $"{adapter.Addresses[0].Address} / {adapter.Addresses[0].Mask}";
        if (matched is not null) return new("subnet", "ready", $"{matched.Name}：{Addresses(matched)}，IPv4 配置匹配。");
        NetworkAdapter? configured = wired.FirstOrDefault(a => a.Addresses.Any(ip => SameSubnet(ip.Address, host, ip.Mask)));
        if (configured is not null) return new("subnet", "missing", $"{configured.Name} 已配置同网段地址 {Addresses(configured)}；请检查网线，无需重复修改 IP。");
        NetworkAdapter selected = wired.OrderByDescending(a => a.Up).First();
        return new("subnet", "missing", $"{selected.Name}：{Addresses(selected)}。请手动设为 {SuggestedAddress(host)} / 255.255.255.0。");
    }
    public static string SuggestedAddress(string host)
    {
        if (!Ipv4(host, out byte[] bytes)) return "192.168.137.1";
        bytes[3] = bytes[3] == 1 ? (byte)2 : (byte)1;
        return new IPAddress(bytes).ToString();
    }
    public static ProcessStartInfo AddressCommand(NetworkAdapter adapter, string address, string mask, bool dhcp, NetworkAdapter[] current)
    {
        NetworkAdapter? actual = current.SingleOrDefault(a => a.Id == adapter.Id);
        if (actual is null || !actual.Wired || actual.Name != adapter.Name) throw new InvalidOperationException("有线网卡已变化，请刷新并重新选择。此窗口不修改 Wi-Fi 网卡。");
        if (!dhcp)
        {
            if (!Ipv4(address, out byte[] bytes) || bytes[0] is 0 or 127 or >= 224 || !ValidMask(mask) ||
                !Ipv4(mask, out byte[] maskBytes) || Enumerable.Range(0, 4).All(i => (bytes[i] & ~maskBytes[i]) == 0) || Enumerable.Range(0, 4).All(i => (bytes[i] & ~maskBytes[i]) == (255 & ~maskBytes[i])))
                throw new InvalidOperationException("请输入有效的 IPv4 主机地址和连续子网掩码，不要使用网络地址或广播地址。");
        }
        ProcessStartInfo start = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe"))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string value in new[] { "interface", "ipv4", "set", "address", "name=" + actual.Name, "source=" + (dhcp ? "dhcp" : "static") }) start.ArgumentList.Add(value);
        if (!dhcp)
            foreach (string value in new[] { "address=" + address, "mask=" + mask, "gateway=none", "store=persistent" }) start.ArgumentList.Add(value);
        return start;
    }
    private static bool Ipv4(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Split('.').Length != 4 || !IPAddress.TryParse(value, out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        bytes = ip.GetAddressBytes(); return ip.ToString() == value;
    }
    private static bool ValidMask(string mask)
    {
        if (!Ipv4(mask, out byte[] bytes)) return false;
        uint value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        uint inverse = ~value;
        return value != 0 && inverse >= 3 && (inverse & (inverse + 1)) == 0;
    }
    public static bool OpenProperties(string adapterName)
    {
        object? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = ((dynamic)shell!).NameSpace(49); // ssfCONNECTIONS
            if (folder is null) return false;
            items = ((dynamic)folder).Items();
            for (int i = 0; i < (int)((dynamic)items).Count; i++)
            {
                object item = ((dynamic)items).Item(i);
                try
                {
                    if ((string)((dynamic)item).Name != adapterName) continue;
                    ((dynamic)item).InvokeVerb("properties"); return true;
                }
                finally { Marshal.FinalReleaseComObject(item); }
            }
            return false;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return false; }
        finally
        {
            foreach (object? value in new[] { items, folder, shell }) if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }
    public static void OpenSettings(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
}
