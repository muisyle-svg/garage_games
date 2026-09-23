using System.IO.Ports;
using System.Text;

namespace GarageGames.V2;

public sealed class PhysicalMasterSerialService : BackgroundService
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly RunService _runs;
    private readonly ILogger<PhysicalMasterSerialService> _logger;
    private readonly MasterProtocolState _protocol = new();
    private SerialPort? _port;
    private CancellationTokenSource? _connectionCancellation;

    public PhysicalMasterSerialService(RunService runs, ILogger<PhysicalMasterSerialService> logger)
    {
        _runs = runs;
        _logger = logger;
    }

    public MasterConnectionSnapshot GetSnapshot()
    {
        string[] availablePorts;
        try
        {
            availablePorts = SerialPort.GetPortNames()
                .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            availablePorts = [];
        }

        lock (_gate)
        {
            return new MasterConnectionSnapshot(
                _port?.IsOpen == true,
                _port?.IsOpen == true ? _port.PortName : null,
                availablePorts,
                _protocol.Mode?.ToString().ToUpperInvariant(),
                _protocol.LastMessage);
        }
    }

    public MasterConnectionSnapshot Connect(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new CommandException("Select a COM port to connect the physical master.");
        }

        string[] available;
        try
        {
            available = SerialPort.GetPortNames();
        }
        catch
        {
            throw new CommandException("COM ports could not be enumerated. Check the serial driver and try again.");
        }
        var selectedPort = available.FirstOrDefault(name => string.Equals(name, portName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (selectedPort is null)
        {
            throw new CommandException($"Serial port '{portName.Trim()}' is not available.");
        }

        lock (_gate)
        {
            if (_port?.IsOpen == true)
            {
                if (string.Equals(_port.PortName, selectedPort, StringComparison.OrdinalIgnoreCase))
                {
                    return GetSnapshot();
                }

                throw new CommandException("Disconnect the current master port before selecting another port.");
            }

            var port = new SerialPort(selectedPort, 115200, Parity.None, 8, StopBits.One)
            {
                Encoding = Encoding.ASCII,
                NewLine = "\n",
                ReadTimeout = 250,
                WriteTimeout = 1000,
                DtrEnable = false,
                RtsEnable = false
            };

            try
            {
                port.Open();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                port.Dispose();
                throw new CommandException($"Could not open serial port '{selectedPort}'. Check that it is not in use and try again.");
            }

            _protocol.Reset();
            _port = port;
            var connectionCancellation = new CancellationTokenSource();
            _connectionCancellation = connectionCancellation;
            _ = Task.Run(() => ReadLoopAsync(port, connectionCancellation.Token));
        }

        return GetSnapshot();
    }

    public MasterConnectionSnapshot Disconnect()
    {
        lock (_gate)
        {
            CloseConnectionLocked();
        }

        return GetSnapshot();
    }

    public RunRecord ArmQueue(RunService runs, string queueId, bool manualOfflineOverride)
    {
        lock (_gate)
        {
            _protocol.EnsureArmAllowed();
            return runs.Arm(queueId, manualOfflineOverride);
        }
    }

    public RunRecord ArmCompetitor(RunService runs, string competitorId, RunCategory category)
    {
        lock (_gate)
        {
            _protocol.EnsureArmAllowed();
            return runs.ArmCompetitor(competitorId, category);
        }
    }

    public RunRecord StartVirtual(RunService runs)
    {
        lock (_gate)
        {
            _protocol.EnsureArmAllowed();
            return runs.StartMaster();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SendStatusAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Disconnect();
        await base.StopAsync(cancellationToken);
    }

    private async Task ReadLoopAsync(SerialPort port, CancellationToken cancellationToken)
    {
        var buffer = new byte[64];
        var line = new StringBuilder(MasterProtocolCodec.MaximumLineLength);
        var discardLine = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await port.BaseStream.ReadAsync(buffer.AsMemory(), cancellationToken);
                for (var index = 0; index < count; index++)
                {
                    var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        if (!discardLine && line.Length > 0)
                        {
                            ProcessLine(port, line.ToString().TrimEnd('\r'));
                        }
                        line.Clear();
                        discardLine = false;
                        continue;
                    }

                    if (discardLine)
                    {
                        continue;
                    }
                    if (value > 0x7f || line.Length >= MasterProtocolCodec.MaximumLineLength)
                    {
                        line.Clear();
                        discardLine = true;
                        continue;
                    }

                    line.Append((char)value);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _logger.LogInformation("Physical master serial connection ended ({ErrorType}).", exception.GetType().Name);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_port, port))
                {
                    CloseConnectionLocked();
                }
            }
        }
    }

    private void ProcessLine(SerialPort port, string line)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_port, port))
            {
                return;
            }

            try
            {
                _protocol.ProcessLine(line, (bootToken, sequence, startAllowed) =>
                    _runs.ReceivePhysicalMasterStart(bootToken, sequence, startAllowed));
            }
            catch
            {
                _logger.LogError("Physical master input could not be recorded; the serial connection remains available.");
            }
        }
    }

    private async Task SendStatusAsync(CancellationToken cancellationToken)
    {
        SerialPort? port;
        lock (_gate)
        {
            port = _port?.IsOpen == true ? _port : null;
        }
        if (port is null)
        {
            return;
        }

        var status = _runs.GetMasterStatus();
        var bytes = Encoding.ASCII.GetBytes(MasterProtocolCodec.FormatStatus(status) + "\n");
        try
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await port.BaseStream.WriteAsync(bytes.AsMemory(), cancellationToken);
                await port.BaseStream.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _logger.LogInformation("Physical master status write failed ({ErrorType}).", exception.GetType().Name);
            lock (_gate)
            {
                if (ReferenceEquals(_port, port))
                {
                    CloseConnectionLocked();
                }
            }
        }
    }

    private void CloseConnectionLocked()
    {
        var port = _port;
        var cancellation = _connectionCancellation;
        _port = null;
        _connectionCancellation = null;
        _protocol.Reset();
        cancellation?.Cancel();
        if (port is not null)
        {
            try
            {
                port.Close();
            }
            catch
            {
            }
            port.Dispose();
        }
        cancellation?.Dispose();
    }
}
