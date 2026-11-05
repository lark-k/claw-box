using System.Runtime.InteropServices;
using System.Text;

namespace ClawBox.Desktop.Services;

public sealed class CurrentWifiException(string message) : Exception(message);
/// <summary>Reads the currently connected WLAN SSID without reading stored credentials.</summary>
public static class CurrentWifi
{
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, int opcode, IntPtr reserved, out int size, out IntPtr data, out int valueType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    public static (string Name, string Bytes) Read()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr handle) != 0)
            throw new CurrentWifiException("无法读取电脑当前 Wi-Fi，请检查无线网络权限。");
        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr list) != 0)
                throw new CurrentWifiException("无法读取电脑无线网卡。");
            try
            {
                int count = Marshal.ReadInt32(list);
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = IntPtr.Add(list, 8 + i * 532);
                    if (Marshal.ReadInt32(item, 528) != 1) continue;
                    Guid id = Marshal.PtrToStructure<Guid>(item);
                    if (WlanQueryInterface(handle, ref id, 7, IntPtr.Zero, out int size, out IntPtr data, out _) != 0) continue;
                    try
                    {
                        if (size < 556) continue;
                        int length = Marshal.ReadInt32(data, 520);
                        if (length is < 1 or > 32) continue;
                        byte[] bytes = new byte[length];
                        Marshal.Copy(IntPtr.Add(data, 524), bytes, 0, length);
                        return (Encoding.UTF8.GetString(bytes), Convert.ToBase64String(bytes));
                    }
                    finally { WlanFreeMemory(data); }
                }
            }
            finally { WlanFreeMemory(list); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
        throw new CurrentWifiException("电脑当前没有连接 Wi-Fi，或 Windows 未允许应用读取 Wi-Fi 信息。");
    }
}
