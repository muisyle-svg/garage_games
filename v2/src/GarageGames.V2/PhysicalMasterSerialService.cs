using System.IO.Ports;
using System.Text;

namespace GarageGames.V2;

public sealed class PhysicalMasterSerialService : BackgroundService
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly RunService _runs;
    private readonly ILogger<PhysicalMasterSerialService> _logger;
    private readonly MasterProtocolState _protocol = new();
    private SerialPort? _port;
    private CancellationTokenSource? _connectionCancellation;
    private ScanWaiter? _pendingScan;

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

        _runs.MarkDevicesUnverified();
        return GetSnapshot();
    }

    public MasterConnectionSnapshot Disconnect()
    {
        lock (_gate)
        {
            CloseConnectionLocked();
        }

        _runs.MarkDevicesUnverified();
        return GetSnapshot();
    }

    public async Task<RunRecord> ArmQueueAsync(RunService runs, string queueId, bool manualOfflineOverride,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _protocol.EnsureArmAllowed();
        }

        await ScanDevicesAsync(cancellationToken);
        lock (_gate)
        {
            _protocol.EnsureArmAllowed();
            return runs.Arm(queueId, manualOfflineOverride);
        }
    }

    public async Task<RunRecord> ArmCompetitorAsync(RunService runs, string competitorId, RunCategory category,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _protocol.EnsureArmAllowed();
        }

        await ScanDevicesAsync(cancellationToken);
        lock (_gate)
        {
            _protocol.EnsureArmAllowed();
            return runs.ArmCompetitor(competitorId, category);
        }
    }

    public async Task<DeviceScanResult> ScanDevicesAsync(CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken);
        ScanWaiter? waiterToClear = null;
        try
        {
            SerialPort? port;
            ScanWaiter waiter;
            lock (_gate)
            {
                port = _port?.IsOpen == true ? _port : null;
                if (port is null)
                {
                    return _runs.RecordDeviceScan(false, false, []);
                }

                _protocol.EnsureArmAllowed();
                waiter = new ScanWaiter(Guid.NewGuid().ToString("N")[..12]);
                _pendingScan = waiter;
                waiterToClear = waiter;
            }

            try
            {
                // The firmware rejects scans until it has seen a fresh controller status.
                await SendStatusAsync(cancellationToken);
                lock (_gate)
                {
                    if (!ReferenceEquals(_port, port) || !port.IsOpen)
                    {
                        if (ReferenceEquals(_pendingScan, waiter))
                        {
                            _pendingScan = null;
                        }
                        return _runs.RecordDeviceScan(false, false, []);
                    }
                }

                var bytes = Encoding.ASCII.GetBytes($"GG1 SCAN {waiter.ScanId}\n");
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
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                _logger.LogInformation("Physical master scan request failed ({ErrorType}).", exception.GetType().Name);
                lock (_gate)
                {
                    if (ReferenceEquals(_pendingScan, waiter))
                    {
                        _pendingScan = null;
                    }
                    var stillConnected = ReferenceEquals(_port, port) && port.IsOpen;
                    return _runs.RecordDeviceScan(stillConnected, false, []);
                }
            }

            var timeout = Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            var completedTask = await Task.WhenAny(waiter.Completion.Task, timeout);
            cancellationToken.ThrowIfCancellationRequested();
            var doneCount = completedTask == waiter.Completion.Task
                ? await waiter.Completion.Task
                : -1;

            DeviceScanResult scanResult;
            var busy = doneCount == -2;
            lock (_gate)
            {
                if (ReferenceEquals(_pendingScan, waiter))
                {
                    _pendingScan = null;
                }

                var stillConnected = ReferenceEquals(_port, port) && port.IsOpen;
                var completed = stillConnected && doneCount >= 0 && doneCount == waiter.DeviceIds.Count;
                scanResult = _runs.RecordDeviceScan(stillConnected, completed,
                    completed ? waiter.DeviceIds : []);
            }

            if (busy && scanResult.Connected)
            {
                // Firmware asks for a fresh controller status before another scan can be accepted.
                await SendStatusAsync(cancellationToken);
            }
            return scanResult;
        }
        finally
        {
            lock (_gate)
            {
                if (waiterToClear is not null && ReferenceEquals(_pendingScan, waiterToClear))
                {
                    _pendingScan = null;
                }
            }
            _scanGate.Release();
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
            var connectionEnded = false;
            lock (_gate)
            {
                if (ReferenceEquals(_port, port))
                {
                    CloseConnectionLocked();
                    connectionEnded = true;
                }
            }
            if (connectionEnded)
            {
                _runs.MarkDevicesUnverified();
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
                if (MasterProtocolCodec.TryParseScanReply(line, out var scanReply))
                {
                    if (_pendingScan is { } scan && string.Equals(scan.ScanId, scanReply.ScanId, StringComparison.Ordinal))
                    {
                        if (scanReply.Kind == "NODE" && scanReply.DeviceId is not null)
                        {
                            scan.DeviceIds.Add(scanReply.DeviceId);
                        }
                        else if (scanReply.Kind == "DONE" && scanReply.Count is int count)
                        {
                            scan.Completion.TrySetResult(count);
                        }
                        else if (scanReply.Kind == "BUSY")
                        {
                            scan.Completion.TrySetResult(-2);
                        }
                    }
                    return;
                }

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
            var connectionEnded = false;
            lock (_gate)
            {
                if (ReferenceEquals(_port, port))
                {
                    CloseConnectionLocked();
                    connectionEnded = true;
                }
            }
            if (connectionEnded)
            {
                _runs.MarkDevicesUnverified();
            }
        }
    }

    private void CloseConnectionLocked()
    {
        var port = _port;
        var cancellation = _connectionCancellation;
        _port = null;
        _connectionCancellation = null;
        _pendingScan?.Completion.TrySetResult(-1);
        _pendingScan = null;
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

    private sealed class ScanWaiter(string scanId)
    {
        public string ScanId { get; } = scanId;
        public HashSet<string> DeviceIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TaskCompletionSource<int> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
