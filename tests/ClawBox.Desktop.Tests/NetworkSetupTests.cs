using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class NetworkSetupTests
{
    private static NetworkAdapter Wired(string ip, string mask = "255.255.255.0", bool up = true) => new("wired-id", "以太网 USB", "USB network adapter", up, true, false, [new(ip, mask)]);
    [Theory]
    [InlineData("192.168.137.1", "192.168.137.2", "255.255.255.0", true)]
    [InlineData("192.168.1.1", "192.168.137.2", "255.255.255.0", false)]
    [InlineData("192.168.1.1", "192.168.137.2", "255.255.0.0", true)]
    [InlineData("192.168.137.129", "192.168.137.2", "255.255.255.128", false)]
    public void Subnet_check_uses_the_actual_adapter_mask(string first, string second, string mask, bool same)
        => Assert.Equal(same, NetworkSetup.SameSubnet(first, second, mask));
    [Fact]
    public void Hotel_wifi_does_not_determine_the_board_wired_subnet()
    {
        NetworkAdapter wifi = new("wifi", "WLAN", "Wi-Fi", true, false, true, [new("10.20.30.40", "255.255.255.0")]);
        Assert.Equal("ready", NetworkSetup.Diagnose("192.168.137.2", [wifi, Wired("192.168.137.1")]).Status);
        Assert.Equal("missing", NetworkSetup.Diagnose("192.168.137.2", [wifi, Wired("192.168.1.1")]).Status);
    }
    [Fact]
    public void Conflicting_address_and_unplugged_adapter_are_not_reported_as_ready()
    {
        Assert.Contains("IP 冲突", NetworkSetup.Diagnose("192.168.137.2", [Wired("192.168.137.2")]).Detail);
        Assert.Equal("missing", NetworkSetup.Diagnose("192.168.137.2", [Wired("192.168.137.1", up: false)]).Status);
        Assert.Contains("有线网卡", NetworkSetup.Diagnose("192.168.137.2", []).Detail);
    }
    [Fact]
    public void Static_address_command_targets_exactly_one_wired_adapter_and_keeps_shell_input_separate()
    {
        NetworkAdapter wired = Wired("192.168.1.1");
        var command = NetworkSetup.AddressCommand(wired, "192.168.137.1", "255.255.255.0", false, [wired]);
        Assert.Equal("runas", command.Verb); Assert.True(command.UseShellExecute);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, command.WindowStyle);
        Assert.EndsWith("netsh.exe", command.FileName);
        Assert.Contains("name=以太网 USB", command.ArgumentList); Assert.Contains("gateway=none", command.ArgumentList);
        Assert.DoesNotContain(command.ArgumentList, a => a.Contains("dns", StringComparison.OrdinalIgnoreCase) || a.Contains("WLAN"));
        Assert.Equal(10, command.ArgumentList.Count);
    }
    [Theory]
    [InlineData("192.168.137.0", "255.255.255.0")]
    [InlineData("192.168.137.255", "255.255.255.0")]
    [InlineData("192.168.137.1 & command", "255.255.255.0")]
    [InlineData("127.0.0.1", "255.255.255.0")]
    [InlineData("192.168.137.1", "255.0.255.0")]
    [InlineData("192.168.137.1", "0.0.0.0")]
    public void Invalid_addresses_and_masks_never_build_a_command(string address, string mask)
    {
        NetworkAdapter adapter = Wired("192.168.1.1");
        Assert.Throws<InvalidOperationException>(() => NetworkSetup.AddressCommand(adapter, address, mask, false, [adapter]));
    }
    [Fact]
    public void Wifi_missing_or_renamed_adapter_cannot_be_modified()
    {
        NetworkAdapter adapter = Wired("192.168.1.1");
        Assert.Throws<InvalidOperationException>(() => NetworkSetup.AddressCommand(adapter, "192.168.137.1", "255.255.255.0", false, []));
        Assert.Throws<InvalidOperationException>(() => NetworkSetup.AddressCommand(adapter, "192.168.137.1", "255.255.255.0", false, [adapter with { Name = "Changed" }]));
        Assert.Throws<InvalidOperationException>(() => NetworkSetup.AddressCommand(adapter, "192.168.137.1", "255.255.255.0", false, [adapter with { Wired = false }]));
    }
    [Fact]
    public void Dhcp_restore_has_no_static_address_or_gateway_arguments()
    {
        NetworkAdapter adapter = Wired("192.168.137.1");
        var command = NetworkSetup.AddressCommand(adapter, "", "", true, [adapter]);
        Assert.Contains("source=dhcp", command.ArgumentList); Assert.Equal(6, command.ArgumentList.Count);
    }
    [Fact]
    public void Common_virtual_adapters_are_filtered_from_board_ethernet_selection()
    {
        Assert.False(NetworkSetup.EligibleWired("vEthernet (WSL)", "Microsoft Hyper-V Network Adapter"));
        Assert.False(NetworkSetup.EligibleWired("VPN", "TAP-Windows Adapter"));
        Assert.True(NetworkSetup.EligibleWired("以太网", "Realtek USB GbE Family Controller"));
    }
}
