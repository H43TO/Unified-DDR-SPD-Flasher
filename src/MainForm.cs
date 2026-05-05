// Refactored for v3.0 guideline:
//   §1.1  All serial I/O moved off the UI thread - bus monitor and disconnect-check
//         now run on background Tasks driven by Task.Delay/CancellationToken loops.
//   §1.2  Connection lifetime managed by CancellationTokenSource; Dispose is synchronous.
//   §1.6  OnBusMonitorTick replaced with BusMonitorLoopAsync running on a Task.
//         Only Control.BeginInvoke is used to push results to the UI.
//   §3.4  Library performs the explicit CMD_PING handshake internally; this form
//         catches handshake failures from the constructor and surfaces them.
//
// Target: .NET Framework 4.8.1. PeriodicTimer is unavailable; the equivalent
// pattern is `while (!ct.IsCancellationRequested) { ... await Task.Delay(...) }`.

using UDFCore;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UnifiedDDRFlasher
{
    public class MainForm : Form
    {
        #region Constants

        private const string APP_VERSION = "v3.9.0";
        private const int BUS_MONITOR_INTERVAL_MS    = 500;
        private const int DISCONNECT_CHECK_INTERVAL_MS = 2000;
        private const int BAUD_RATE = 115200;

        #endregion

        #region Fields

        private UDFDevice _spdDevice;
        private TabControl _mainTabControl;

        private SPDOperationsTab _spdTab;
        private PMICOperationsTab _pmicTab;
        private FlasherConfigTab _configTab;

        private Panel _statusBar;
        private Label _errorLabel;
        private Panel _connectionIndicator;
        private Label _comPortLabel;
        private Label _connectionStatusLabel;

        private bool _isConnected;
        private string _currentPort = "";
        private int _errorCount;

        // §1.1/§1.6: cancellation source bound to the device lifetime.
        // Cancelled on disconnect; new one created on each connect.
        private CancellationTokenSource _connectionCts;
        private Task _busMonitorTask;
        private Task _disconnectCheckTask;

        // Auto-connect timer is the only Forms.Timer remaining (UI-only logic).
        private System.Windows.Forms.Timer _autoConnectTimer;

        #endregion

        #region Constructor

        public MainForm()
        {
            AppSettings.Load();
            InitializeCustomComponents();
            SetupEventHandlers();
            UpdateConnectionState(false);

            if (AppSettings.AutoConnect)
            {
                _autoConnectTimer = new System.Windows.Forms.Timer { Interval = 800 };
                _autoConnectTimer.Tick += OnAutoConnectTimerTick;
                _autoConnectTimer.Start();
            }
        }

        #endregion

        #region Initialization

        private void InitializeCustomComponents()
        {
            this.Text = $"Unified DDR Flasher {APP_VERSION}";
            this.Size = new Size(1600, 1200);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimumSize = new Size(1200, 750);
            this.StartPosition = FormStartPosition.CenterScreen;

            try { this.Icon = System.Drawing.SystemIcons.Application; } catch { /* headless */ }

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));

            _mainTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                Padding = new Point(10, 5)
            };

            _spdTab = new SPDOperationsTab();
            _pmicTab = new PMICOperationsTab();
            _configTab = new FlasherConfigTab();

            var spdPage = new TabPage("SPD Operations") { Padding = new Padding(3) };
            spdPage.Controls.Add(_spdTab);
            var pmicPage = new TabPage("PMIC Operations") { Padding = new Padding(3) };
            pmicPage.Controls.Add(_pmicTab);
            var configPage = new TabPage("Flasher Configuration") { Padding = new Padding(3) };
            configPage.Controls.Add(_configTab);

            _mainTabControl.TabPages.Add(spdPage);
            _mainTabControl.TabPages.Add(pmicPage);
            _mainTabControl.TabPages.Add(configPage);
            _mainTabControl.SelectedIndex = 2;

            mainLayout.Controls.Add(_mainTabControl, 0, 0);
            mainLayout.Controls.Add(CreateBottomStatusBar(), 0, 1);
            this.Controls.Add(mainLayout);
        }

        private Panel CreateBottomStatusBar()
        {
            var statusBar = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                Padding = new Padding(8, 0, 8, 0)
            };

            _errorLabel = new Label
            {
                Text = "Errors: 0",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5F),
                AutoSize = true,
                Dock = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 4, 0, 0)
            };

            var rightPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false
            };

            _comPortLabel = new Label
            {
                Text = "Port: -",
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 8.5F),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(8, 4, 0, 0)
            };

            _connectionIndicator = new Panel
            {
                Width = 12,
                Height = 12,
                BackColor = Color.Red,
                Margin = new Padding(8, 6, 0, 0)
            };

            _connectionStatusLabel = new Label
            {
                Text = "Disconnected",
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 8.5F),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(8, 4, 5, 0)
            };

            rightPanel.Controls.Add(_comPortLabel);
            rightPanel.Controls.Add(_connectionIndicator);
            rightPanel.Controls.Add(_connectionStatusLabel);

            statusBar.Controls.Add(_errorLabel);
            statusBar.Controls.Add(rightPanel);
            _statusBar = statusBar;
            return statusBar;
        }

        private void SetupEventHandlers()
        {
            _configTab.ConnectionRequested += OnConnectRequested;
            _configTab.DisconnectionRequested += OnDisconnectRequested;
            _configTab.PortSettingsChanged += OnPortSettingsChanged;

            _spdTab.SetDeviceProvider(() => _spdDevice);
            _pmicTab.SetDeviceProvider(() => _spdDevice);
            _configTab.SetDeviceProvider(() => _spdDevice);

            _spdTab.ErrorOccurred += OnErrorOccurred;
            _pmicTab.ErrorOccurred += OnErrorOccurred;
            _configTab.ErrorOccurred += OnErrorOccurred;
        }

        #endregion

        #region Auto-Connect

        private void OnAutoConnectTimerTick(object sender, EventArgs e)
        {
            _autoConnectTimer.Stop();
            _autoConnectTimer.Dispose();
            _autoConnectTimer = null;

            string savedPort = AppSettings.LastPort;
            if (string.IsNullOrEmpty(savedPort)) return;

            string[] available = SerialPort.GetPortNames();
            if (Array.IndexOf(available, savedPort) < 0)
            {
                bool found = false;
                foreach (var p in available)
                    if (string.Equals(p, savedPort, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                if (!found)
                {
                    LogMessage($"[AutoConnect] Saved port {savedPort} not available - skipping.");
                    return;
                }
            }

            LogMessage($"[AutoConnect] Attempting connection to remembered port {savedPort}");
            _configTab.SelectPort(savedPort);
            OnConnectRequested(this, EventArgs.Empty);
        }

        #endregion

        #region Connection Management

        /// <summary>
        /// §1.1: Connection itself is synchronous (the library constructor handles
        /// its own handshake), but we wrap the call so a slow handshake doesn't
        /// freeze the UI. Background tasks are started via StartBackgroundLoops.
        /// </summary>
        private async void OnConnectRequested(object sender, EventArgs e)
        {
            string portName = _configTab.GetSelectedPort();
            if (string.IsNullOrEmpty(portName) || portName == "No ports available")
            {
                LogError("Please select a valid COM port first.");
                MessageBox.Show("Please select a valid COM port first.", "Connection Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            LogMessage($"Connecting to {portName} at {BAUD_RATE} baud");

            try
            {
                // Library constructor performs the §3.4 explicit handshake.
                // Run it on a thread-pool thread so the UI stays responsive
                // during the 2.5 s boot delay + handshake retries.
                _spdDevice = await Task.Run(() => new UDFDevice(portName, BAUD_RATE)).ConfigureAwait(true);

                uint version = await Task.Run(() => _spdDevice.GetVersion()).ConfigureAwait(true);
                string devName = await Task.Run(() => _spdDevice.GetDeviceName()).ConfigureAwait(true);

                _currentPort = portName;
                _isConnected = true;
                _errorCount = 0;
                UpdateErrorDisplay();

                AppSettings.LastPort = portName;
                AppSettings.Save();

                _spdDevice.AlertReceived += OnDeviceAlert;

                _spdTab.OnDeviceConnected(_spdDevice);
                _pmicTab.OnDeviceConnected(_spdDevice);
                _configTab.OnDeviceConnected(_spdDevice);

                StartBackgroundLoops();
                UpdateConnectionState(true);

                LogMessage($"Connected: {devName} (FW: 0x{version:X8}) on {portName}");
                MessageBox.Show(
                    $"Connected successfully!\n\nDevice: {(string.IsNullOrEmpty(devName) ? "(unnamed)" : devName)}\nFirmware: 0x{version:X8}",
                    "Connected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                CleanupAfterFailedConnect();
            }
            catch (TimeoutException ex)
            {
                CleanupAfterFailedConnect();
                LogError($"Connection timeout: {ex.Message}");
                MessageBox.Show($"Connection timed out:\n\n{ex.Message}",
                    "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _configTab.LogError($"Connection timeout: {ex.Message}");
            }
            catch (Exception ex)
            {
                CleanupAfterFailedConnect();
                LogError($"Connection failed: {ex.Message}");
                MessageBox.Show(
                    $"Failed to connect:\n\n{ex.Message}\n\n" +
                    "Check:\n1. Device is powered\n2. Correct COM port selected\n3. Drivers installed\n4. No other software using the port",
                    "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _configTab.LogError($"Connection failed: {ex.Message}");
            }
        }

        private void CleanupAfterFailedConnect()
        {
            _isConnected = false;
            UpdateConnectionState(false);
            try { _spdDevice?.Dispose(); } catch { }
            _spdDevice = null;
        }

        /// <summary>
        /// §1.6: replaces the WinForms Timer-driven OnBusMonitorTick. Bus monitor
        /// and disconnect check both run on background Tasks.
        /// </summary>
        private void StartBackgroundLoops()
        {
            _connectionCts = new CancellationTokenSource();
            var ct = _connectionCts.Token;
            _busMonitorTask = Task.Run(() => BusMonitorLoopAsync(ct));
            _disconnectCheckTask = Task.Run(() => DisconnectCheckLoopAsync(ct));
        }

        private async Task BusMonitorLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(BUS_MONITOR_INTERVAL_MS, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;

                    var dev = _spdDevice;
                    if (dev == null) continue;

                    var spdDevices = await dev.ScanBusAsync(ct).ConfigureAwait(false);
                    var pmicDevices = new List<byte>();
                    for (byte addr = 0x48; addr <= 0x4F; addr++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (await dev.ProbeAddressAsync(addr, ct).ConfigureAwait(false))
                                pmicDevices.Add(addr);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (IOException) { throw; }                  // bubble out → disconnect handler below
                        catch (UnauthorizedAccessException) { throw; }
                        catch (InvalidOperationException) { throw; }
                        catch { /* per-address probe failure is non-fatal */ }
                    }

                    var allDevices = new List<byte>(spdDevices);
                    allDevices.AddRange(pmicDevices);

                    PostToUi(() =>
                    {
                        _spdTab.UpdateDetectedDevices(allDevices);
                        _pmicTab.UpdateDetectedDevices(allDevices);
                    });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                // §1.6 (regression fix): a yanked USB cable used to surface
                // here as a continuous stream of "Bus scan error: ..." log
                // entries every BUS_MONITOR_INTERVAL_MS, because IOException
                // hit the catch-all below and the loop kept running. Now we
                // treat IO/access/invalid-state as the canonical "device is
                // gone" signal: trigger the disconnect handler and exit.
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is UnauthorizedAccessException ||
                    ex is InvalidOperationException)
                {
                    PostToUi(() => TriggerDisconnect("bus monitor: " + ex.Message));
                    break;
                }
                catch (TimeoutException ex)
                {
                    PostToUi(() => LogError($"Bus scan timeout: {ex.Message}"));
                    // Timeouts can be transient (slow scan, busy bus). Keep looping;
                    // the disconnect-check loop is responsible for the kill switch.
                }
                catch (Exception ex)
                {
                    PostToUi(() => LogError($"Bus scan error: {ex.Message}"));
                    // Likewise: unknown errors stay non-fatal here.
                }
            }
        }

        private async Task DisconnectCheckLoopAsync(CancellationToken ct)
        {
            // We use TWO mechanisms to detect disconnect:
            //
            //   1. Fast path: SerialPort.GetPortNames() no longer lists the
            //      port we're connected to. This catches USB-CDC removal
            //      within ~500 ms whether or not any I/O is in flight, and
            //      doesn't depend on a ping making it through.
            //
            //   2. Liveness check: PingAsync. The library catches IO/access
            //      exceptions and returns false (see PingAsync in
            //      UDF-Core.cs), so a yanked cable produces a clean
            //      `ok == false` here. Catches firmware hangs that don't
            //      remove the port.
            //
            // Either trigger calls TriggerDisconnect() exactly once per
            // disconnect event; the loop then exits.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(DISCONNECT_CHECK_INTERVAL_MS, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;

                    var dev = _spdDevice;
                    if (dev == null) continue;

                    // 1. Fast path: is our port still enumerated?
                    string port = _currentPort;
                    if (!string.IsNullOrEmpty(port))
                    {
                        bool stillThere;
                        try
                        {
                            string[] names = SerialPort.GetPortNames();
                            stillThere = false;
                            foreach (var n in names)
                                if (string.Equals(n, port, StringComparison.OrdinalIgnoreCase))
                                { stillThere = true; break; }
                        }
                        catch
                        {
                            // GetPortNames itself almost never throws, but
                            // if it does we just skip the fast path and
                            // fall through to the ping check.
                            stillThere = true;
                        }
                        if (!stillThere)
                        {
                            PostToUi(() => TriggerDisconnect($"port {port} no longer enumerated"));
                            break;
                        }
                    }

                    // 2. Liveness ping. PingAsync returns false (not throws)
                    // for IO/access/disposed/timeout - see library notes.
                    bool ok = await dev.PingAsync(ct).ConfigureAwait(false);
                    if (!ok)
                    {
                        PostToUi(() => TriggerDisconnect("device not responding to ping"));
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is UnauthorizedAccessException ||
                    ex is InvalidOperationException)
                {
                    PostToUi(() => TriggerDisconnect("disconnect check: " + ex.Message));
                    break;
                }
                catch (Exception ex)
                {
                    // Genuinely unexpected error. Log and stop the loop -
                    // we don't want to spam errors, and the fast-path or
                    // user-initiated disconnect will clean up properly.
                    PostToUi(() => LogError($"Disconnect check failed: {ex.Message}"));
                    break;
                }
            }
        }

        /// <summary>
        /// Single entry point for "device went away unexpectedly". Idempotent:
        /// if _isConnected is already false, this is a no-op. Always invoked
        /// via PostToUi() so it runs on the UI thread.
        /// </summary>
        private void TriggerDisconnect(string reason)
        {
            if (!_isConnected) return;          // already handled
            LogError($"Disconnected ({reason})");
            OnDisconnectRequested(this, EventArgs.Empty);
        }

        private void OnDisconnectRequested(object sender, EventArgs e)
        {
            // Idempotency guard - if a previous TriggerDisconnect already
            // started teardown, don't run it again. Without this, the bus
            // monitor and the disconnect check could each call us in quick
            // succession on a USB-yank, which would re-Wait() on already-
            // disposed tasks.
            if (!_isConnected && _spdDevice == null) return;

            // Clear the flag IMMEDIATELY so any UI handler that fires
            // between here and the device.Dispose() below bails out
            // instead of trying to use _spdDevice.
            _isConnected = false;
            try
            {
                // §1.2: cancel background tasks first, then dispose the device.
                if (_connectionCts != null)
                {
                    try { _connectionCts.Cancel(); } catch { }
                }

                if (_spdDevice != null)
                {
                    _spdDevice.AlertReceived -= OnDeviceAlert;
                    try { _spdDevice.Dispose(); } catch (Exception ex) { LogMessage($"Dispose threw: {ex.Message}"); }
                    _spdDevice = null;
                }

                // Wait briefly for background tasks to unwind so we don't leak.
                try
                {
                    if (_busMonitorTask != null)
                        _busMonitorTask.Wait(500);
                    if (_disconnectCheckTask != null)
                        _disconnectCheckTask.Wait(500);
                }
                catch { /* AggregateException(OperationCanceledException) is expected */ }

                try { _connectionCts?.Dispose(); } catch { }
                _connectionCts = null;
                _busMonitorTask = null;
                _disconnectCheckTask = null;

                _currentPort = "";

                _spdTab.OnDeviceDisconnected();
                _pmicTab.OnDeviceDisconnected();
                _configTab.OnDeviceDisconnected();

                UpdateConnectionState(false);
                LogMessage("Device disconnected");
            }
            catch (Exception ex)
            {
                LogError($"Error during disconnect: {ex.Message}");
                _configTab.LogError($"Disconnect error: {ex.Message}");
            }
        }

        private void OnDeviceAlert(object sender, AlertEventArgs e)
        {
            PostToUi(() =>
            {
                LogMessage($"[ALERT] {e.AlertType}");
                // Slave-count change alerts force an immediate scan; the
                // library has already invalidated its module cache (§7.2).
                if (e.AlertCode == 0x2B || e.AlertCode == 0x2D)
                {
                    // No direct call; the bus-monitor task will pick it up
                    // on the next iteration. To make it snappier, we could
                    // signal via a separate AutoResetEvent - left as future work.
                }
            });
        }

        private void UpdateConnectionState(bool connected)
        {
            if (InvokeRequired) { Invoke(new Action(() => UpdateConnectionState(connected))); return; }

            _isConnected = connected;
            _spdTab.Enabled = connected;
            _pmicTab.Enabled = connected;

            _connectionIndicator.BackColor = connected ? Color.LimeGreen : Color.Red;
            _comPortLabel.Text = connected ? $"Port: {_currentPort}" : "Port: -";

            if (_connectionStatusLabel != null)
            {
                _connectionStatusLabel.Text = connected ? "Connected" : "Disconnected";
                _connectionStatusLabel.ForeColor = connected ? Color.LimeGreen : Color.LightGray;
            }
            _configTab.SetConnectionState(connected);
        }

        private void OnPortSettingsChanged(object sender, EventArgs e)
        {
            if (!_isConnected) return;
            var result = MessageBox.Show(
                "Changing port settings requires reconnection. Disconnect now?",
                "Port Settings Changed", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
                OnDisconnectRequested(this, EventArgs.Empty);
        }

        #endregion

        #region UI helpers

        private void PostToUi(Action action)
        {
            if (this.IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { /* form closing */ }
        }

        #endregion

        #region Error Handling

        private void OnErrorOccurred(object sender, string errorMessage)
        {
            LogError(errorMessage);
            _configTab.LogError(errorMessage);
        }

        private void LogError(string message)
        {
            _errorCount++;
            if (InvokeRequired) Invoke(new Action(UpdateErrorDisplay));
            else UpdateErrorDisplay();
            LogMessage($"[ERROR] {message}");
        }

        private void UpdateErrorDisplay()
        {
            if (_errorLabel == null) return;
            _errorLabel.Text = $"Errors: {_errorCount}";
            _errorLabel.ForeColor = _errorCount > 0 ? Color.Yellow : Color.White;
        }

        private void LogMessage(string message)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            System.Diagnostics.Debug.WriteLine($"[MainForm] {ts}: {message}");
        }

        #endregion

        #region Form Events

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_isConnected)
            {
                var result = MessageBox.Show(
                    "Device is still connected. Close anyway?",
                    "Confirm Exit", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.No) { e.Cancel = true; return; }
                OnDisconnectRequested(this, EventArgs.Empty);
            }

            if (_autoConnectTimer != null)
            {
                _autoConnectTimer.Stop();
                _autoConnectTimer.Dispose();
                _autoConnectTimer = null;
            }

            base.OnFormClosing(e);
        }

        #endregion
    }
}
