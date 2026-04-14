// UPDATED v3.1: Auto-connect on startup via AppSettings (JSON), error counter reset on connect.
//               disconnect check timer properly disposed, error counter reset on connect.
using SPDTool;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Windows.Forms;

namespace UnifiedDDRSPDFlasher
{
    /// <summary>
    /// Main form for the Unified DDR SPD Flasher – version 3.1
    /// </summary>
    public class MainForm : Form
    {
        #region Constants

        private const string APP_VERSION = "v3.1";
        private const int BUS_MONITOR_INTERVAL_MS = 500;
        private const int DISCONNECT_CHECK_INTERVAL_MS = 2000;
        private const int CONNECT_PING_RETRIES = 5;
        private const int BAUD_RATE = 115200;

        #endregion

        #region Private Fields

        private SPDToolDevice _spdDevice;
        private TabControl _mainTabControl;

        private SPDOperationsTab _spdTab;
        private PMICOperationsTab _pmicTab;
        private FlasherConfigTab _configTab;

        private Panel _statusBar;
        private Label _errorLabel;
        private Panel _connectionIndicator;
        private Label _comPortLabel;
        private Label _connectionStatusLabel;

        private bool _isConnected = false;
        private string _currentPort = "";
        private int _errorCount = 0;

        private System.Windows.Forms.Timer _busMonitorTimer;
        private System.Windows.Forms.Timer _disconnectCheckTimer;
        private System.Windows.Forms.Timer _autoConnectTimer;

        #endregion

        #region Constructor

        public MainForm()
        {
            AppSettings.Load();
            InitializeCustomComponents();
            SetupEventHandlers();
            UpdateConnectionState(false);

            // Schedule auto-connect attempt after form is fully loaded
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
            this.Text = $"Unified DDR SPD Flasher {APP_VERSION}";
            this.Size = new Size(1600, 1200);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimumSize = new Size(1200, 750);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Icon = SystemIcons.Application;

            TableLayoutPanel mainLayout = new TableLayoutPanel
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

            _mainTabControl.TabPages.Add(new TabPage("SPD Operations")
            {
                Controls = { _spdTab },
                Padding = new Padding(3)
            });
            _mainTabControl.TabPages.Add(new TabPage("PMIC Operations")
            {
                Controls = { _pmicTab },
                Padding = new Padding(3)
            });
            _mainTabControl.TabPages.Add(new TabPage("Flasher Configuration")
            {
                Controls = { _configTab },
                Padding = new Padding(3)
            });

            // Start on the Flasher Configuration tab so users connect before doing anything
            _mainTabControl.SelectedIndex = 2;

            mainLayout.Controls.Add(_mainTabControl, 0, 0);
            mainLayout.Controls.Add(CreateBottomStatusBar(), 0, 1);

            this.Controls.Add(mainLayout);

            _busMonitorTimer = new System.Windows.Forms.Timer
            {
                Interval = BUS_MONITOR_INTERVAL_MS
            };
            _busMonitorTimer.Tick += OnBusMonitorTick;

            _disconnectCheckTimer = new System.Windows.Forms.Timer
            {
                Interval = DISCONNECT_CHECK_INTERVAL_MS
            };
            _disconnectCheckTimer.Tick += OnDisconnectCheckTick;
        }

        private Panel CreateBottomStatusBar()
        {
            Panel statusBar = new Panel
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

            FlowLayoutPanel rightPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false
            };

            _comPortLabel = new Label
            {
                Text = "Port: —",
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

        /// <summary>
        /// Fires once after startup. If the remembered port is available, pre-selects it
        /// and initiates a connection silently.
        /// </summary>
        private void OnAutoConnectTimerTick(object sender, EventArgs e)
        {
            _autoConnectTimer.Stop();
            _autoConnectTimer.Dispose();
            _autoConnectTimer = null;

            string savedPort = AppSettings.LastPort;
            if (string.IsNullOrEmpty(savedPort)) return;

            // Check that the saved port is currently available
            string[] available = SerialPort.GetPortNames();
            bool portExists = Array.Exists(available, p =>
                string.Equals(p, savedPort, StringComparison.OrdinalIgnoreCase));

            if (!portExists)
            {
                LogMessage($"[AutoConnect] Saved port {savedPort} not available – skipping.");
                return;
            }

            LogMessage($"[AutoConnect] Attempting connection to remembered port {savedPort}…");

            // Pre-select the port in the config tab and trigger connection
            _configTab.SelectPort(savedPort);
            OnConnectRequested(this, EventArgs.Empty);
        }

        #endregion

        #region Connection Management

        private void OnConnectRequested(object sender, EventArgs e)
        {
            try
            {
                string portName = _configTab.GetSelectedPort();

                if (string.IsNullOrEmpty(portName) || portName == "No ports available")
                {
                    LogError("Please select a valid COM port first.");
                    MessageBox.Show("Please select a valid COM port first.", "Connection Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                LogMessage($"Connecting to {portName} at {BAUD_RATE} baud…");

                _spdDevice = new SPDToolDevice(portName, BAUD_RATE);

                bool pingSuccess = false;
                for (int attempt = 0; attempt < CONNECT_PING_RETRIES; attempt++)
                {
                    try
                    {
                        if (_spdDevice.Ping()) { pingSuccess = true; break; }
                    }
                    catch { System.Threading.Thread.Sleep(100); }
                }

                if (!pingSuccess)
                    throw new Exception("Device did not respond to ping after 5 attempts");

                uint version = _spdDevice.GetVersion();
                string devName = _spdDevice.GetDeviceName();

                _currentPort = portName;
                _isConnected = true;
                _errorCount = 0;
                UpdateErrorDisplay();

                // Persist the successfully-used port for next startup auto-connect
                AppSettings.LastPort = portName;
                AppSettings.Save();

                _spdDevice.AlertReceived += OnDeviceAlert;

                _spdTab.OnDeviceConnected(_spdDevice);
                _pmicTab.OnDeviceConnected(_spdDevice);
                _configTab.OnDeviceConnected(_spdDevice);

                _busMonitorTimer.Start();
                _disconnectCheckTimer.Start();

                UpdateConnectionState(true);

                LogMessage($"Connected: {devName} (FW: 0x{version:X8}) on {portName}");

                MessageBox.Show(
                    $"Connected successfully!\n\nDevice: {(string.IsNullOrEmpty(devName) ? "(unnamed)" : devName)}\nFirmware: 0x{version:X8}",
                    "Connected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _isConnected = false;
                UpdateConnectionState(false);

                LogError($"Connection failed: {ex.Message}");
                MessageBox.Show(
                    $"Failed to connect:\n\n{ex.Message}\n\n" +
                    "Check:\n1. Device is powered\n2. Correct COM port selected\n3. Drivers installed\n4. No other software using the port",
                    "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _configTab.LogError($"Connection failed: {ex.Message}");

                _spdDevice?.Dispose();
                _spdDevice = null;
            }
        }

        private void OnDisconnectRequested(object sender, EventArgs e)
        {
            try
            {
                _busMonitorTimer.Stop();
                _disconnectCheckTimer.Stop();

                if (_spdDevice != null)
                {
                    _spdDevice.AlertReceived -= OnDeviceAlert;
                    _spdDevice.Dispose();
                    _spdDevice = null;
                }

                _isConnected = false;
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

        private void OnDisconnectCheckTick(object sender, EventArgs e)
        {
            if (_spdDevice == null || !_isConnected) return;
            try
            {
                if (!_spdDevice.Ping())
                {
                    LogError("Device not responding — assuming disconnected");
                    OnDisconnectRequested(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                LogError($"Disconnect check failed: {ex.Message}");
                _configTab.LogError($"Disconnect check: {ex.Message}");
                OnDisconnectRequested(this, EventArgs.Empty);
            }
        }

        private void OnDeviceAlert(object sender, AlertEventArgs e)
        {
            if (InvokeRequired) { Invoke(new Action(() => OnDeviceAlert(sender, e))); return; }

            LogMessage($"[ALERT] {e.AlertType}");

            // Bus change alerts → immediate scan
            if (e.AlertCode == 0x2B || e.AlertCode == 0x2D)
                OnBusMonitorTick(this, EventArgs.Empty);
        }

        private void OnBusMonitorTick(object sender, EventArgs e)
        {
            if (_spdDevice == null || !_isConnected) return;

            try
            {
                var spdDevices = _spdDevice.ScanBus();
                var pmicDevices = new System.Collections.Generic.List<byte>();

                for (byte addr = 0x48; addr <= 0x4F; addr++)
                {
                    try { if (_spdDevice.ProbeAddress(addr)) pmicDevices.Add(addr); }
                    catch { }
                }

                var allDevices = new System.Collections.Generic.List<byte>();
                allDevices.AddRange(spdDevices);
                allDevices.AddRange(pmicDevices);

                if (InvokeRequired)
                {
                    Invoke(new Action(() =>
                    {
                        _spdTab.UpdateDetectedDevices(allDevices);
                        _pmicTab.UpdateDetectedDevices(allDevices);
                    }));
                }
                else
                {
                    _spdTab.UpdateDetectedDevices(allDevices);
                    _pmicTab.UpdateDetectedDevices(allDevices);
                }
            }
            catch (Exception ex)
            {
                LogError($"Bus scan error: {ex.Message}");
                _configTab.LogError($"Bus scan error: {ex.Message}");
            }
        }

        private void UpdateConnectionState(bool connected)
        {
            if (InvokeRequired) { Invoke(new Action(() => UpdateConnectionState(connected))); return; }

            _isConnected = connected;

            _spdTab.Enabled = connected;
            _pmicTab.Enabled = connected;

            _connectionIndicator.BackColor = connected ? Color.LimeGreen : Color.Red;
            _comPortLabel.Text = connected ? $"Port: {_currentPort}" : "Port: —";

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

            // Properly dispose all timers
            if (_busMonitorTimer != null)
            {
                _busMonitorTimer.Stop();
                _busMonitorTimer.Tick -= OnBusMonitorTick;
                _busMonitorTimer.Dispose();
                _busMonitorTimer = null;
            }
            if (_disconnectCheckTimer != null)
            {
                _disconnectCheckTimer.Stop();
                _disconnectCheckTimer.Tick -= OnDisconnectCheckTick;
                _disconnectCheckTimer.Dispose();
                _disconnectCheckTimer = null;
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