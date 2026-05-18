using FluentAssertions;
using NohddX.Api.Storage;
using Xunit;

namespace NohddX.Tests.Storage;

/// <summary>
/// Parser tests use captured <c>smartctl -j</c> output samples — verifies
/// the Linux SMART path without needing a Linux box with actual disks.
/// JSON shapes pulled from smartctl 7.x release notes and real /dev/sda
/// captures.
/// </summary>
public class SmartctlJsonParserTests
{
    [Fact]
    public void ParseScan_extracts_device_names()
    {
        // Sample: smartctl --scan -j on a host with two disks
        var json = """
        {
          "json_format_version": [ 1, 0 ],
          "smartctl": { "version": [ 7, 4 ] },
          "devices": [
            { "name": "/dev/sda", "info_name": "/dev/sda", "type": "sat", "protocol": "ATA" },
            { "name": "/dev/nvme0", "info_name": "/dev/nvme0", "type": "nvme", "protocol": "NVMe" }
          ]
        }
        """;

        var paths = SmartctlJsonParser.ParseScan(json);

        paths.Should().Equal("/dev/sda", "/dev/nvme0");
    }

    [Fact]
    public void ParseScan_returns_empty_when_no_devices_key()
    {
        var json = """{ "smartctl": { "version": [7, 4] } }""";
        SmartctlJsonParser.ParseScan(json).Should().BeEmpty();
    }

    [Fact]
    public void ParseScan_returns_empty_on_malformed_json()
    {
        SmartctlJsonParser.ParseScan("{ not actually json").Should().BeEmpty();
    }

    [Fact]
    public void ParseInfo_extracts_model_serial_size_temp_smart_status_for_sata()
    {
        // Trimmed real smartctl output from a Samsung 870 EVO SATA SSD.
        var json = """
        {
          "model_name": "Samsung SSD 870 EVO 1TB",
          "serial_number": "S626NX0R401234",
          "user_capacity": { "blocks": 1953525168, "bytes": 1000204886016 },
          "temperature": { "current": 38 },
          "smart_status": { "passed": true },
          "device": { "name": "/dev/sda", "type": "sat", "protocol": "ATA" },
          "rotation_rate": 0
        }
        """;

        var disk = SmartctlJsonParser.ParseInfo(json);

        disk.Should().NotBeNull();
        disk!.Model.Should().Be("Samsung SSD 870 EVO 1TB");
        disk.SerialNumber.Should().Be("S626NX0R401234");
        disk.SizeBytes.Should().Be(1000204886016);
        disk.TemperatureCelsius.Should().Be(38);
        disk.PredictFailure.Should().BeFalse();
        disk.Health.Should().Be("Good");
        disk.Status.Should().Be("OK");
        disk.InterfaceType.Should().Be("ATA");
        disk.MediaType.Should().Be("SSD",
            "rotation_rate=0 means SSD regardless of bus protocol");
    }

    [Fact]
    public void ParseInfo_marks_failing_disk_with_PredictFailure_true()
    {
        var json = """
        {
          "model_name": "Failing Drive",
          "serial_number": "BAD-001",
          "smart_status": { "passed": false, "string": "Self-test failed: read error LBA 1234567" },
          "device": { "protocol": "ATA" }
        }
        """;

        var disk = SmartctlJsonParser.ParseInfo(json);

        disk.Should().NotBeNull();
        disk!.PredictFailure.Should().BeTrue("smart_status.passed=false");
        disk.Health.Should().Be("Failing");
        disk.Status.Should().Be("FAILING");
        disk.SmartReason.Should().Contain("read error");
    }

    [Fact]
    public void ParseInfo_handles_NVMe_protocol_without_rotation_rate()
    {
        var json = """
        {
          "model_name": "WD_BLACK SN770 1TB",
          "serial_number": "232121AE8E5C",
          "user_capacity": { "bytes": 1000204886016 },
          "temperature": { "current": 42 },
          "smart_status": { "passed": true },
          "device": { "name": "/dev/nvme0", "type": "nvme", "protocol": "NVMe" }
        }
        """;

        var disk = SmartctlJsonParser.ParseInfo(json);

        disk.Should().NotBeNull();
        disk!.MediaType.Should().Be("NVMe SSD",
            "NVMe protocol without rotation_rate must still be categorised correctly");
        disk.InterfaceType.Should().Be("NVMe");
        disk.TemperatureCelsius.Should().Be(42);
    }

    [Fact]
    public void ParseInfo_marks_hdd_with_rotation_rate()
    {
        var json = """
        {
          "model_name": "Seagate Barracuda",
          "serial_number": "ZHN0001",
          "rotation_rate": 7200,
          "smart_status": { "passed": true },
          "device": { "protocol": "ATA" }
        }
        """;

        var disk = SmartctlJsonParser.ParseInfo(json);

        disk.Should().NotBeNull();
        disk!.MediaType.Should().Be("HDD (7200 RPM)");
    }

    [Fact]
    public void ParseInfo_treats_implausible_temperature_as_missing()
    {
        // Some failing drives report bogus temperatures (300°C, 0°C, etc.)
        // The parser must reject these rather than panic-display them.
        var json = """
        {
          "model_name": "Glitchy Drive",
          "serial_number": "X",
          "temperature": { "current": 250 },
          "smart_status": { "passed": true }
        }
        """;

        var disk = SmartctlJsonParser.ParseInfo(json);

        disk.Should().NotBeNull();
        disk!.TemperatureCelsius.Should().BeNull(
            "temperatures outside the [1, 149] range are obviously bogus");
    }

    [Fact]
    public void ParseInfo_returns_null_when_blob_lacks_model_and_serial()
    {
        // smartctl error JSON has neither — must not pretend a disk exists.
        var json = """{ "smartctl": { "exit_status": 4 } }""";
        SmartctlJsonParser.ParseInfo(json).Should().BeNull();
    }

    [Fact]
    public void ParseInfo_handles_missing_smart_status_as_Unknown()
    {
        // Some USB enclosures / RAID controllers hide SMART entirely.
        var json = """
        {
          "model_name": "Behind USB enclosure",
          "serial_number": "USB-001"
        }
        """;

        var disk = SmartctlJsonParser.ParseInfo(json);

        disk.Should().NotBeNull();
        disk!.Status.Should().Be("Unknown");
        disk.Health.Should().Be("Good",
            "be optimistic when smartctl can't read SMART — the drive still works");
        disk.PredictFailure.Should().BeFalse();
    }

    [Fact]
    public void ParseInfo_returns_null_on_malformed_json()
    {
        SmartctlJsonParser.ParseInfo("not json at all").Should().BeNull();
    }
}
