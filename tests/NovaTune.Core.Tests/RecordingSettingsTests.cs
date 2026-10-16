using NovaTune.Core.Settings;
using Xunit;

namespace NovaTune.Core.Tests;

public class RecordingSettingsTests
{
    [Fact]
    public void Normalize_FixesInvalidRecordingValues()
    {
        var s = new AppSettings { Recording = null! };
        s.Normalize();
        Assert.NotNull(s.Recording);

        s.Recording.Fps = 144;
        s.Recording.Quality = 9;
        s.Recording.OverlayCorner = -3;
        s.Normalize();
        Assert.Equal(60, s.Recording.Fps);
        Assert.Equal(2, s.Recording.Quality);
        Assert.Equal(0, s.Recording.OverlayCorner);
    }

    [Fact]
    public void Normalize_KeepsValidValues()
    {
        var s = new AppSettings();
        s.Recording.Fps = 30;
        s.Recording.Quality = 0;
        s.Recording.OverlayCorner = 3;
        s.Normalize();
        Assert.Equal(30, s.Recording.Fps);
        Assert.Equal(0, s.Recording.Quality);
        Assert.Equal(3, s.Recording.OverlayCorner);
    }
}

public class NetworkAdapterFilterTests
{
    [Theory]
    [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", "Realtek PCIe GbE Family Controller-WFP Native MAC Layer LightWeight Filter-0000")]
    [InlineData("Ethernet-QoS Packet Scheduler-0000", "Realtek PCIe GbE Family Controller-QoS Packet Scheduler-0000")]
    [InlineData("Подключение по локальной сети* 3", "WAN Miniport (Network Monitor)")]
    [InlineData("vSwitch (Default Switch)-Hyper-V Virtual Switch Extension Filter-0000", "Hyper-V Virtual Switch Extension Adapter-Hyper-V Virtual Switch Extension Filter-0000")]
    public void ServiceAdapters_AreHidden(string name, string description) =>
        Assert.True(NovaTune.Core.Hardware.NetworkAdapterFilter.IsServiceAdapter(name, description));

    [Theory]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller")]
    [InlineData("Radmin VPN", "Famatech Radmin VPN Ethernet Adapter")]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz")]
    public void RealAdapters_AreShown(string name, string description) =>
        Assert.False(NovaTune.Core.Hardware.NetworkAdapterFilter.IsServiceAdapter(name, description));
}
