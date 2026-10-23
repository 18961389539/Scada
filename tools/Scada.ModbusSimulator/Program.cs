using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 1502;
var unitId = args.Length > 1 && byte.TryParse(args[1], out var u) ? u : (byte)1;

var simulator = new ModbusSimulator(unitId);
simulator.Start();

var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Console.WriteLine($"[sim] Modbus TCP simulator listening on 0.0.0.0:{port}, unit={unitId}");
Console.WriteLine("[sim] holding registers 0-199 (float pairs), coils 0-99, discrete inputs 0-99");
Console.WriteLine("[sim] press Ctrl+C to stop");

var valueTask = simulator.RunValueLoopAsync();
var clients = new ConcurrentBag<Task>();

try
{
    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        Console.WriteLine($"[sim] client connected {client.Client.RemoteEndPoint}");
        clients.Add(Task.Run(() => simulator.HandleClientAsync(client)));
    }
}
catch (OperationCanceledException)
{
}
finally
{
    listener.Stop();
    await valueTask;
}

internal sealed class ModbusSimulator
{
    private const int RegisterCount = 200;
    private const int BitCount = 100;

    private readonly byte _unitId;
    private readonly ushort[] _holding = new ushort[RegisterCount];
    private readonly ushort[] _input = new ushort[RegisterCount];
    private readonly bool[] _coils = new bool[BitCount];
    private readonly bool[] _discrete = new bool[BitCount];
    private readonly object _sync = new();
    private readonly DateTime _start = DateTime.UtcNow;

    private DateTime _writableHoldUntil = DateTime.MinValue;
    private float _writtenFrequency = 30f;

    public ModbusSimulator(byte unitId)
    {
        _unitId = unitId;
        for (var i = 0; i < BitCount; i++)
        {
            _discrete[i] = i % 3 != 2;
        }
    }

    public void Start()
    {
        UpdateValues();
    }

    public async Task RunValueLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync())
            {
                UpdateValues();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new byte[7];
            try
            {
                while (true)
                {
                    if (!await ReadExactAsync(stream, header, 7))
                    {
                        break;
                    }

                    var length = (ushort)((header[4] << 8) | header[5]);
                    if (length < 2 || length > 260)
                    {
                        break;
                    }

                    var pdu = new byte[length - 1];
                    if (!await ReadExactAsync(stream, pdu, pdu.Length))
                    {
                        break;
                    }

                    var response = Process(pdu);
                    if (response is null)
                    {
                        continue;
                    }

                    var adu = new byte[7 + response.Length];
                    adu[0] = header[0];
                    adu[1] = header[1];
                    adu[2] = 0;
                    adu[3] = 0;
                    var respLength = (ushort)(response.Length + 1);
                    adu[4] = (byte)(respLength >> 8);
                    adu[5] = (byte)(respLength & 0xFF);
                    adu[6] = pdu[0];
                    Buffer.BlockCopy(response, 0, adu, 7, response.Length);
                    await stream.WriteAsync(adu);
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            finally
            {
                Console.WriteLine($"[sim] client disconnected {client.Client.RemoteEndPoint}");
            }
        }
    }

    private byte[]? Process(byte[] pdu)
    {
        if (pdu.Length < 1)
        {
            return null;
        }

        var function = pdu[0];
        byte unit = _unitId;
        lock (_sync)
        {
            return function switch
            {
                1 => ReadBits(pdu, _coils, function),
                2 => ReadBits(pdu, _discrete, function),
                3 => ReadRegisters(pdu, _holding, function),
                4 => ReadRegisters(pdu, _input, function),
                5 => WriteSingleCoil(pdu, unit),
                6 => WriteSingleRegister(pdu, unit),
                15 => WriteMultipleCoils(pdu, unit),
                16 => WriteMultipleRegisters(pdu, unit),
                _ => Exception(function, 0x01)
            };
        }
    }

    private static byte[] ReadBits(byte[] pdu, bool[] store, byte function)
    {
        if (pdu.Length < 5)
        {
            return Exception(function, 0x03);
        }

        var address = (pdu[1] << 8) | pdu[2];
        var quantity = (pdu[3] << 8) | pdu[4];
        if (quantity < 1 || quantity > 2000 || address + quantity > store.Length)
        {
            return Exception(function, 0x02);
        }

        var byteCount = (quantity + 7) / 8;
        var response = new byte[2 + byteCount];
        response[0] = function;
        response[1] = (byte)byteCount;
        for (var i = 0; i < quantity; i++)
        {
            if (store[address + i])
            {
                response[2 + i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return response;
    }

    private static byte[] ReadRegisters(byte[] pdu, ushort[] store, byte function)
    {
        if (pdu.Length < 5)
        {
            return Exception(function, 0x03);
        }

        var address = (pdu[1] << 8) | pdu[2];
        var quantity = (pdu[3] << 8) | pdu[4];
        if (quantity < 1 || quantity > 125 || address + quantity > store.Length)
        {
            return Exception(function, 0x02);
        }

        var response = new byte[2 + quantity * 2];
        response[0] = function;
        response[1] = (byte)(quantity * 2);
        for (var i = 0; i < quantity; i++)
        {
            response[2 + i * 2] = (byte)(store[address + i] >> 8);
            response[3 + i * 2] = (byte)(store[address + i] & 0xFF);
        }

        return response;
    }

    private byte[] WriteSingleCoil(byte[] pdu, byte unit)
    {
        if (pdu.Length < 5)
        {
            return Exception(5, 0x03);
        }

        var address = (pdu[1] << 8) | pdu[2];
        var value = (pdu[3] << 8) | pdu[4];
        if (address >= _coils.Length || (value != 0 && value != 0xFF00))
        {
            return Exception(5, 0x02);
        }

        _coils[address] = value == 0xFF00;
        Console.WriteLine($"[sim] write coil {address} = {_coils[address]}");
        return pdu[..5];
    }

    private byte[] WriteSingleRegister(byte[] pdu, byte unit)
    {
        if (pdu.Length < 5)
        {
            return Exception(6, 0x03);
        }

        var address = (pdu[1] << 8) | pdu[2];
        var value = (ushort)((pdu[3] << 8) | pdu[4]);
        if (address >= _holding.Length)
        {
            return Exception(6, 0x02);
        }

        _holding[address] = value;
        Console.WriteLine($"[sim] write register {address} = {value}");
        return pdu[..5];
    }

    private byte[] WriteMultipleCoils(byte[] pdu, byte unit)
    {
        if (pdu.Length < 6)
        {
            return Exception(15, 0x03);
        }

        var address = (pdu[1] << 8) | pdu[2];
        var quantity = (pdu[3] << 8) | pdu[4];
        var bytes = pdu[5];
        if (quantity < 1 || quantity > 1968 || address + quantity > _coils.Length || pdu.Length < 6 + bytes)
        {
            return Exception(15, 0x02);
        }

        for (var i = 0; i < quantity; i++)
        {
            _coils[address + i] = (pdu[6 + i / 8] & (1 << (i % 8))) != 0;
        }

        Console.WriteLine($"[sim] write coils {address}..{address + quantity - 1}");
        return pdu[..5];
    }

    private byte[] WriteMultipleRegisters(byte[] pdu, byte unit)
    {
        if (pdu.Length < 6)
        {
            return Exception(16, 0x03);
        }

        var address = (pdu[1] << 8) | pdu[2];
        var quantity = (pdu[3] << 8) | pdu[4];
        if (quantity < 1 || quantity > 123 || address + quantity > _holding.Length || pdu.Length < 6 + quantity * 2)
        {
            return Exception(16, 0x02);
        }

        for (var i = 0; i < quantity; i++)
        {
            _holding[address + i] = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
        }

        if (address == 5 && quantity >= 2)
        {
            var bytes = new[] { (byte)(_holding[5] >> 8), (byte)(_holding[5] & 0xFF), (byte)(_holding[6] >> 8), (byte)(_holding[6] & 0xFF) };
            _writtenFrequency = Math.Clamp(BitConverter.ToSingle(bytes.Reverse().ToArray()), 0f, 60f);
            _writableHoldUntil = DateTime.UtcNow.AddSeconds(30);
            Console.WriteLine($"[sim] writable frequency set to {_writtenFrequency:F2} Hz (held 30s)");
        }

        return pdu[..5];
    }

    private void UpdateValues()
    {
        lock (_sync)
        {
            var t = (DateTime.UtcNow - _start).TotalSeconds;
            SetFloat(0, 103 + 12 * Math.Sin(t / 40));                 // 出口温度 91..115
            SetFloat(2, 1.6 + 0.15 * Math.Sin(t / 55 + 1));           // 出口压力
            SetFloat(4, 60 * Math.Sin(t / 50));                       // 汽包水位
            SetFloat(22, 60 * Math.Sin(t / 50));                      // 独立水位寄存器，不与频率 5/6 重叠
            var frequency = DateTime.UtcNow < _writableHoldUntil
                ? _writtenFrequency
                : 30 + 12 * Math.Sin(t / 30);
            SetFloat(5, frequency);                                    // 给水泵频率（可写）
            SetFloat(7, 7.5 + 2.5 * Math.Sin(t / 25 + 2));            // 烟气含氧量
            SetFloat(9, -40 + 40 * Math.Sin(t / 35 + 3));             // 炉膛负压
            SetFloat(11, 70 + 25 * Math.Sin(t / 45));                 // 主蒸汽流量
            SetFloat(13, 135 + 20 * Math.Sin(t / 60));                // 排烟温度
            SetFloat(15, 55 + 15 * Math.Sin(t / 20));                 // 鼓风机电流
            SetFloat(17, 65 + 20 * Math.Sin(t / 22 + 1));             // 引风机电流
            SetFloat(19, 25 + 5 * Math.Sin(t / 15));                  // 备用模拟量

            for (var i = 0; i < RegisterCount; i++)
            {
                _input[i] = _holding[i];
            }

            _coils[0] = ((int)t / 20) % 2 == 0;                        // 给水泵运行，20 秒翻转
            _coils[1] = ((int)t / 15) % 2 == 0;
        }
    }

    private void SetFloat(int address, double value)
    {
        var bytes = BitConverter.GetBytes((float)value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        _holding[address] = (ushort)((bytes[0] << 8) | bytes[1]);
        _holding[address + 1] = (ushort)((bytes[2] << 8) | bytes[3]);
    }

    private static byte[] Exception(byte function, byte code) => new[] { (byte)(function | 0x80), code };

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
