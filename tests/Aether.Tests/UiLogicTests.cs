using System.Globalization;
using System.Windows.Data;
using Aether.Services;
using Aether.Services.WindowsServices;

namespace Aether.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Hand_edited_values_are_brought_back_into_range()
    {
        var s = new AppSettings { SampleIntervalMs = 0, UiScale = 5, ThermalComfortC = -20, AlertTempC = 0 }.Normalized();

        Assert.Equal(250, s.SampleIntervalMs);
        Assert.Equal(80, s.UiScale);
        Assert.Equal(45, s.ThermalComfortC);
        Assert.Equal(60, s.AlertTempC);
    }

    [Fact]
    public void Defaults_are_already_valid()
    {
        var d = new AppSettings();
        var n = new AppSettings().Normalized();
        Assert.Equal(d.SampleIntervalMs, n.SampleIntervalMs);
        Assert.Equal(d.AlertTempC, n.AlertTempC);
    }
}

public class EqualsConverterTests
{
    private readonly EqualsConverter _converter = new();

    [Fact]
    public void Radio_is_checked_only_for_the_active_page()
    {
        Assert.Equal(true, _converter.Convert("Network", typeof(bool), "Network", CultureInfo.InvariantCulture));
        Assert.Equal(false, _converter.Convert("Dashboard", typeof(bool), "Network", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Checking_a_radio_selects_its_page_and_unchecking_changes_nothing()
    {
        Assert.Equal("Network", _converter.ConvertBack(true, typeof(string), "Network", CultureInfo.InvariantCulture));
        Assert.Same(Binding.DoNothing, _converter.ConvertBack(false, typeof(string), "Network", CultureInfo.InvariantCulture));
    }
}

public class ServiceProfileTests
{
    [Fact]
    public void Every_catalog_service_name_is_unique()
    {
        var names = WindowsServiceCatalog.All.Select(s => s.ServiceName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Services_moved_from_the_optimization_tab_exist_in_the_catalog()
    {
        var catalog = WindowsServiceCatalog.All.Select(s => s.ServiceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "Fax", "RemoteRegistry", "RetailDemo", "MapsBroker", "WMPNetworkSvc", "PhoneSvc", "DiagTrack", "dmwappushservice" })
            Assert.Contains(name, catalog);
    }
}
