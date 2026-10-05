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
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);
    // Sending every button's state at once overflowed the master's small serial buffer and
    // silently dropped the last button (by MAC). MasterEventLineSync picks only what's due;
    // at most EventLinesPerWrite ride along with the status lines and any more are spaced out.
    internal static readonly TimeSpan EventLinePacing = TimeSpan.FromMilliseconds(10);
    private const int EventLinesPerWrite = 2;
    private SerialPort? _port;
    private CancellationTokenSource? _connectionCancellation;
    private ScanWaiter? _pendingScan;
    private string? _lastTestDeviceId;
    private DateTimeOffset? _lastTestAt;
    private uint _identifySequence;
    private readonly MasterEventLineSync _eventSync = new();
    // The port the operator connected. If the connection drops without them disconnecting
    // (a bumped cable, a USB glitch, the master rebooting), it is reopened automatically.
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(2);
    private string? _reconnectPortName;
    private DateTimeOffset? _connectionLostAt;
    private DateTimeOffset _lastReconnectAttemptAt;

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
            var connected = _port?.IsOpen == true;
            return new MasterConnectionSnapshot(
                connected,
                connected ? _port!.PortName : null,
                availablePorts,
                _protocol.Mode?.ToString().ToUpperInvariant(),
                _protocol.LastMessage,
                _lastTestDeviceId,
                _lastTestAt,
                connected ? null : _reconnectPortName,
                connected ? null : _connectionLostAt);
        }
    }

    public async Task<MasterConnectionSnapshot> IdentifyButtonAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var normalizedId = (deviceId ?? string.Empty).Trim().ToUpperInvariant();
        if (!MasterProtocolCodec.IsValidDeviceId(normalizedId))
        {
            throw new CommandException("Choose an event assigned to a physical button before identifying it.");
        }
        if (!_runs.CanIdentifyPhysicalButtons())
        {
            throw new CommandException("Button identification is available only when no run is underway.");
        }

        SerialPort port;
        uint sequence;
        lock (_gate)
        {
            _protocol.EnsureArmAllowed();
            if (_protocol.Mode != MasterMode.Idle)
            {
                throw new CommandException("Connect the master in Garage Games idle mode before identifying a button.");
            }
            port = _port is { IsOpen: true } connectedPort
                ? connectedPort
                : throw new CommandException("Connect the physical master before identifying a button.");
            _identifySequence = _identifySequence == uint.MaxValue ? 1 : _identifySequence + 1;
            sequence = _identifySequence;
        }

        await SendProtocolLineAsync(port, MasterProtocolCodec.FormatIdentifyCommand(normalizedId, sequence), cancellationToken);
        return GetSnapshot();
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
            ResetEventSyncLocked();
            _port = port;
            _reconnectPortName = selectedPort;
            _connectionLostAt = null;
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
            // The operator chose to disconnect: don't reconnect on our own.
            _reconnectPortName = null;
            _connectionLostAt = null;
            CloseConnectionLocked();
        }

        _runs.MarkDevicesUnverified();
        return GetSnapshot();
    }

    public Task SendCurrentStatusAsync(CancellationToken cancellationToken = default) =>
        SendStatusAsync(cancellationToken);

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
        CancellationToken cancellationToken = default, int? durationLimitSeconds = null, bool replaceExistingOfficial = false)
    {
        RunService.ValidateRunDuration(durationLimitSeconds);
        lock (_gate)
        {
            _protocol.EnsureArmAllowed();
        }

        await ScanDevicesAsync(cancellationToken);
        lock (_gate)
        {
            _protocol.EnsureArmAllowed();
            return runs.ArmCompetitor(competitorId, category, durationLimitSeconds, replaceExistingOfficial);
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
        using var timer = new PeriodicTimer(StatusInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                TryReconnect();
                await SendStatusAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void TryReconnect()
    {
        string portName;
        lock (_gate)
        {
            if (_port?.IsOpen == true || _reconnectPortName is not { } wanted ||
                DateTimeOffset.UtcNow - _lastReconnectAttemptAt < ReconnectInterval)
            {
                return;
            }
            _lastReconnectAttemptAt = DateTimeOffset.UtcNow;
            portName = wanted;
        }

        try
        {
            Connect(portName);
            _logger.LogInformation("Physical master reconnected on {Port}.", portName);
        }
        catch (CommandException)
        {
            // Not back yet (unplugged, or still re-enumerating); try again shortly.
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
                            await ProcessLineAsync(port, line.ToString().TrimEnd('\r'), cancellationToken);
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

    private async Task ProcessLineAsync(SerialPort port, string line, CancellationToken cancellationToken)
    {
        string? reply = null;
        var pushStatus = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_port, port))
            {
                return;
            }

            try
            {
                if (MasterProtocolCodec.TryParseButtonTest(line, out var testPress))
                {
                    if (_protocol.Mode == MasterMode.Idle &&
                        string.Equals(_protocol.BootToken, testPress.BootToken, StringComparison.Ordinal) &&
                        _runs.CanIdentifyPhysicalButtons())
                    {
                        _lastTestDeviceId = testPress.DeviceId;
                        _lastTestAt = DateTimeOffset.UtcNow;
                    }
                    return;
                }

                if (MasterProtocolCodec.TryParseBonusPollReply(line, out var bonusBootToken, out var bonusRunToken, out var bonusDeviceId))
                {
                    if (_protocol.Mode == MasterMode.Idle && string.Equals(_protocol.BootToken, bonusBootToken, StringComparison.Ordinal))
                    {
                        _runs.ReceiveBonusPollReply(bonusRunToken, bonusDeviceId);
                    }
                    return;
                }

                // A bonus heartbeat; when it confirms the lit button, the buttons get a fresh status.
                var handledBeat = false;
                if (MasterProtocolCodec.TryParseBonusBeat(line, out var beatBootToken, out var beatRunToken, out var beatDeviceId, out var beatSequence))
                {
                    if (_protocol.Mode == MasterMode.Idle && string.Equals(_protocol.BootToken, beatBootToken, StringComparison.Ordinal))
                    {
                        pushStatus = _runs.ReceiveBonusBeat(beatRunToken, beatDeviceId, beatSequence);
                    }
                    handledBeat = true;
                }
                else if (MasterProtocolCodec.TryParseScanReply(line, out var scanReply))
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

                if (!handledBeat) _protocol.ProcessLine(line,
                    (bootToken, sequence, startAllowed) =>
                    {
                        var result = _runs.ReceivePhysicalMasterStart(bootToken, sequence, startAllowed);
                        pushStatus |= result.Disposition == MessageDisposition.Accepted;
                        return result;
                    },
                    (press, sessionAllowed) =>
                    {
                        var result = _runs.ReceivePhysicalSpokePress(press, sessionAllowed);
                        reply = MasterProtocolCodec.FormatPhysicalPressResult(press, result.State);
                        pushStatus |= result.Disposition == MessageDisposition.Accepted;
                        return result;
                    },
                    (keypad, sessionAllowed) =>
                    {
                        // Typed-entry updates return null: they only refresh the TV and need no reply.
                        var result = _runs.ReceivePhysicalKeypadInput(keypad, sessionAllowed);
                        if (result is not null)
                        {
                            reply = MasterProtocolCodec.FormatKeypadResult(keypad, result.State);
                            pushStatus |= result.Disposition == MessageDisposition.Accepted;
                        }
                        return result;
                    });
            }
            catch
            {
                _logger.LogError("Physical master input could not be recorded; the serial connection remains available.");
            }
        }

        if (reply is not null)
        {
            await SendProtocolLineAsync(port, reply, cancellationToken);
        }
        if (pushStatus)
        {
            await SendStatusAsync(cancellationToken);
        }
    }

    private async Task SendProtocolLineAsync(SerialPort port, string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_port, port) || !port.IsOpen)
                {
                    return;
                }
            }

            await port.BaseStream.WriteAsync(bytes.AsMemory(), cancellationToken);
            await port.BaseStream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
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

        try
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_port, port) || !port.IsOpen) return;
                }

                var (status, garageStatus, eventSnapshot) = _runs.GetMasterStatuses();
                var lines = new StringBuilder()
                    .Append(MasterProtocolCodec.FormatStatus(status)).Append('\n')
                    .Append(MasterProtocolCodec.FormatGarageStatus(garageStatus)).Append('\n')
                    .Append(MasterProtocolCodec.FormatBonusStatus(_runs.GetMasterBonusStatus())).Append('\n');
                List<MasterGarageEventStatus> eventLines;
                lock (_gate)
                {
                    eventLines = ReferenceEquals(_port, port) ? PickEventLinesLocked(eventSnapshot) : [];
                }
                foreach (var eventStatus in eventLines.Take(EventLinesPerWrite))
                {
                    lines.Append(MasterProtocolCodec.FormatGarageEventStatus(eventStatus)).Append('\n');
                }
                var bytes = Encoding.ASCII.GetBytes(lines.ToString());
                await port.BaseStream.WriteAsync(bytes.AsMemory(), cancellationToken);
                await port.BaseStream.FlushAsync(cancellationToken);
                foreach (var eventStatus in eventLines.Skip(EventLinesPerWrite))
                {
                    await Task.Delay(EventLinePacing, cancellationToken);
                    var eventLine = Encoding.ASCII.GetBytes(MasterProtocolCodec.FormatGarageEventStatus(eventStatus) + "\n");
                    await port.BaseStream.WriteAsync(eventLine.AsMemory(), cancellationToken);
                    await port.BaseStream.FlushAsync(cancellationToken);
                }
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

    private void ResetEventSyncLocked() => _eventSync.Reset();

    private List<MasterGarageEventStatus> PickEventLinesLocked(MasterGarageEventSnapshot snapshot) =>
        _eventSync.Pick(snapshot, DateTimeOffset.UtcNow);

    private void CloseConnectionLocked()
    {
        var port = _port;
        var cancellation = _connectionCancellation;
        if (port is not null && _reconnectPortName is not null)
        {
            // Dropped without the operator disconnecting: the status loop reopens it.
            _connectionLostAt ??= DateTimeOffset.UtcNow;
            _lastReconnectAttemptAt = DateTimeOffset.UtcNow;
        }
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
