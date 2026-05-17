using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;
using NohddX.Storage.CoW;
using Xunit;

namespace NohddX.Tests.Storage;

/// <summary>
/// End-to-end test of the CoW overlay engine: writes go to the per-client
/// overlay, reads return overlay data where written and base data elsewhere,
/// the base file is never modified, and resetting the overlay reverts state.
/// </summary>
public class CowOverlayDiskTests : IDisposable
{
    private readonly string _root;
    private readonly NohddxOptions _options;
    private readonly CowOverlayDisk _engine;

    public CowOverlayDiskTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nohddx-cow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _options = new NohddxOptions
        {
            StorageBasePath = _root,
            BaseImagesPath = Path.Combine(_root, "bases"),
            OverlaysPath = Path.Combine(_root, "overlays"),
            SnapshotsPath = Path.Combine(_root, "snapshots"),
            CowBlockSizeBytes = 4096
        };

        Directory.CreateDirectory(_options.BaseImagesPath);
        Directory.CreateDirectory(_options.OverlaysPath);
        Directory.CreateDirectory(_options.SnapshotsPath);

        _engine = new CowOverlayDisk(Options.Create(_options), NullLogger<CowOverlayDisk>.Instance);
    }

    [Fact]
    public async Task Writes_do_not_modify_base_image()
    {
        var basePath = Path.Combine(_options.BaseImagesPath, "ubuntu.vhd");
        var basePattern = new byte[64 * 1024];
        for (int i = 0; i < basePattern.Length; i++) basePattern[i] = (byte)(i & 0xFF);
        await File.WriteAllBytesAsync(basePath, basePattern);
        var baseHashBefore = Sha256(basePattern);

        var clientId = "client-001";
        await using (var disk = await _engine.OpenDiskAsync(basePath, clientId))
        {
            // Overwrite the first 8 KB
            disk.Position = 0;
            var payload = new byte[8 * 1024];
            for (int i = 0; i < payload.Length; i++) payload[i] = 0xAA;
            await disk.WriteAsync(payload);
            await disk.FlushAsync();
        }

        var baseHashAfter = Sha256(await File.ReadAllBytesAsync(basePath));
        baseHashAfter.Should().BeEquivalentTo(baseHashBefore,
            "base image must remain immutable after CoW writes");
    }

    [Fact]
    public async Task Reads_return_overlay_for_written_blocks_and_base_otherwise()
    {
        var basePath = Path.Combine(_options.BaseImagesPath, "win.vhd");
        var basePattern = new byte[16 * 1024];
        for (int i = 0; i < basePattern.Length; i++) basePattern[i] = 0xBB;
        await File.WriteAllBytesAsync(basePath, basePattern);

        var clientId = "client-002";
        await using var disk = await _engine.OpenDiskAsync(basePath, clientId);

        // Write a recognizable pattern at offset 4096 (one full block)
        disk.Position = 4096;
        var written = new byte[4096];
        for (int i = 0; i < written.Length; i++) written[i] = 0xCC;
        await disk.WriteAsync(written);
        await disk.FlushAsync();

        // Read back: first block (0..4095) is unchanged base, second is overlay,
        // third is unchanged base.
        var buf = new byte[16 * 1024];
        disk.Position = 0;
        int read = await disk.ReadAsync(buf);
        read.Should().Be(buf.Length);

        for (int i = 0; i < 4096; i++) buf[i].Should().Be(0xBB, $"base at offset {i}");
        for (int i = 4096; i < 8192; i++) buf[i].Should().Be(0xCC, $"overlay at offset {i}");
        for (int i = 8192; i < buf.Length; i++) buf[i].Should().Be(0xBB, $"base at offset {i}");
    }

    [Fact]
    public async Task Reset_overlay_reverts_to_base_image()
    {
        var basePath = Path.Combine(_options.BaseImagesPath, "linux.vhd");
        var basePattern = new byte[8 * 1024];
        for (int i = 0; i < basePattern.Length; i++) basePattern[i] = 0x42;
        await File.WriteAllBytesAsync(basePath, basePattern);

        var clientId = "client-reset";
        await using (var disk = await _engine.OpenDiskAsync(basePath, clientId))
        {
            disk.Position = 0;
            await disk.WriteAsync(Enumerable.Repeat((byte)0xFF, 4096).ToArray());
            await disk.FlushAsync();
        }

        await _engine.ResetOverlayAsync(clientId);

        await using var disk2 = await _engine.OpenDiskAsync(basePath, clientId);
        var buf = new byte[4096];
        disk2.Position = 0;
        await disk2.ReadAsync(buf);
        buf.Should().AllSatisfy(b => b.Should().Be(0x42));
    }

    [Fact]
    public async Task Different_clients_have_isolated_overlays()
    {
        var basePath = Path.Combine(_options.BaseImagesPath, "shared.vhd");
        var basePattern = new byte[4096];
        for (int i = 0; i < basePattern.Length; i++) basePattern[i] = 0x10;
        await File.WriteAllBytesAsync(basePath, basePattern);

        await using (var d1 = await _engine.OpenDiskAsync(basePath, "alpha"))
        {
            d1.Position = 0;
            await d1.WriteAsync(Enumerable.Repeat((byte)0x21, 4096).ToArray());
            await d1.FlushAsync();
        }

        // beta should see base, not alpha's writes
        await using var d2 = await _engine.OpenDiskAsync(basePath, "beta");
        var buf = new byte[4096];
        d2.Position = 0;
        await d2.ReadAsync(buf);
        buf.Should().AllSatisfy(b => b.Should().Be(0x10),
            "client beta must not see client alpha's writes");
    }

    private static byte[] Sha256(byte[] data) =>
        System.Security.Cryptography.SHA256.HashData(data);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* ignore */ }
    }
}
