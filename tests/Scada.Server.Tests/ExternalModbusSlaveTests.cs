using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Server.Tests;

/// <summary>
/// 对外 Modbus TCP 从站：真起监听、真发 MBAP 帧。验证"上级主站读到的是站内实时值"，
/// 以及写入必须穿过控制闭环（设备未启用时不允许外部主站绕过约束直接动作）。
/// </summary>
public sealed class ExternalModbusSlaveTests : IClassFixture<ExternalModbusSlaveTests.ModbusSite>
{
    private readonly ModbusSite _site;

    public ExternalModbusSlaveTests(ModbusSite site) => _site = site;

    [Fact]
    public async Task Reads_float_from_holding_registers_using_configured_byte_order()
    {
        var cache = _site.Services.GetRequiredService<IRealtimeCache>();
        cache.Set(new TagValue(_site.TagA, 12.5, TagQuality.Good, ScadaTime.NowMs()));

        using var client = await ConnectAsync();
        // FC03 起始 0、数量 2：TagA 是 Float，占两个寄存器。
        var pdu = await SendAsync(client, 1, [0x03, 0x00, 0x00, 0x00, 0x02]);

        Assert.Equal(0x03, pdu[0]);
        Assert.Equal(4, pdu[1]);
        // ABCD：高字在前，整体大端。
        Assert.Equal(12.5f, BinaryPrimitives.ReadSingleBigEndian(pdu.AsSpan(2, 4)));
    }

    [Fact]
    public async Task Unmapped_address_reads_zero_and_unmapped_write_is_rejected()
    {
        using var client = await ConnectAsync();
        var read = await SendAsync(client, 2, [0x03, 0x10, 0x00, 0x00, 0x02]);
        Assert.Equal(0x03, read[0]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(read.AsSpan(2)));

        var write = await SendAsync(client, 3, [0x06, 0x10, 0x00, 0x00, 0x2A]);
        Assert.Equal(0x86, write[0]);
        Assert.Equal(0x02, write[1]);
    }

    [Fact]
    public async Task Writing_float_with_single_register_function_is_refused()
    {
        using var client = await ConnectAsync();
        // FC06 只能表达一个寄存器；对 Float 写半个值等于写脏数据，必须拒绝而不是猜高低字。
        var pdu = await SendAsync(client, 4, [0x06, 0x00, 0x00, 0x00, 0x01]);
        Assert.Equal(0x86, pdu[0]);
        Assert.Equal(0x03, pdu[1]);
    }

    [Fact]
    public async Task Write_through_control_loop_is_refused_when_device_is_disabled()
    {
        using var client = await ConnectAsync();
        // 测试站点的设备是停用状态：外部主站写入必须被控制闭环挡住，而不是直接改缓存。
        var pdu = await SendAsync(client, 5, [0x06, 0x00, 0x00, 0x00, 0x01], unit: 1);
        Assert.Equal(0x86, pdu[0]);
        Assert.True(pdu[1] is 0x03 or 0x04, $"意外异常码 0x{pdu[1]:X2}");
    }

    [Fact]
    public async Task Unknown_function_and_oversized_read_return_modbus_exceptions()
    {
        using var client = await ConnectAsync();
        var unknown = await SendAsync(client, 6, [0x07]);
        Assert.Equal(0x87, unknown[0]);
        Assert.Equal(0x01, unknown[1]);

        // 126 个寄存器超过标准上限 125。
        var oversized = await SendAsync(client, 7, [0x03, 0x00, 0x00, 0x00, 0x7E]);
        Assert.Equal(0x83, oversized[0]);
        Assert.Equal(0x03, oversized[1]);
    }

    [Fact]
    public async Task Unit_id_outside_configured_list_is_rejected()
    {
        using var client = await ConnectAsync();
        var pdu = await SendAsync(client, 8, [0x03, 0x00, 0x00, 0x00, 0x02], unit: 9);
        Assert.Equal(0x8B, pdu[0]);
        Assert.Equal(0x0B, pdu[1]);
    }

    private async Task<TcpClient> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _site.Port);
        return client;
    }

    private static async Task<byte[]> SendAsync(TcpClient client, ushort transactionId, byte[] pdu, byte unit = 1)
    {
        var stream = client.GetStream();
        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = unit;
        pdu.CopyTo(frame, 7);
        await stream.WriteAsync(frame);

        var header = new byte[7];
        await ReadExactAsync(stream, header);
        Assert.Equal(transactionId, BinaryPrimitives.ReadUInt16BigEndian(header));
        var declared = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        var body = new byte[declared - 1];
        await ReadExactAsync(stream, body);
        return body;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    public sealed class ModbusSite : AuthorizationTests.Site
    {
        // 取系统分配的空闲端口：随机区段在并行测试里可能撞上别的监听者，
        // 客户端会静默连到别人的 Modbus 服务，读到无关寄存器而不是本用例写入的值。
        public int Port { get; } = FreePort();

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        protected override void ConfigureSettings(IWebHostBuilder b)
        {
            b.UseSetting("External:ModbusSlave:Enabled", "true");
            b.UseSetting("External:ModbusSlave:ListenAddress", "127.0.0.1");
            b.UseSetting("External:ModbusSlave:Port", Port.ToString());
            // 按测点自身的 Area+Address 直通：TagA 是 HoldingRegister / address 0 / Float。
            b.UseSetting("External:ModbusSlave:AutoAddressing", "true");
            b.UseSetting("External:ModbusSlave:UnitIds:0", "1");
            b.UseSetting("External:ModbusSlave:AllowWrite", "true");
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            for (var i = 0; i < 60; i++)
            {
                try
                {
                    using var c = new TcpClient();
                    await c.ConnectAsync(IPAddress.Loopback, Port);
                    return;
                }
                catch { await Task.Delay(50); }
            }
            throw new TimeoutException("Modbus 从站未在预期端口监听");
        }
    }
}
