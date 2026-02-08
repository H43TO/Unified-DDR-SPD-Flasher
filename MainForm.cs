using System;
using System.Drawing;
using System.Windows.Forms;
using SPDTool;

namespace UnifiedDDRSPDFlasher
{
    /// <summary>
    /// Main form for the Unified DDR SPD Flasher v2.2
    /// Fixed all connection and display issues
    /// </summary>
    public class MainForm : Form
    {
        #region Private Fields

        private SPDToolDevice _spdDevice;
        private TabControl _mainTabControl;

        // Tab pages
        private SPDOperationsTab _spdTab;
        private PMICOperationsTab _pmicTab;
        private FlasherConfigTab _configTab;

        // Bottom status bar - FIXED: Added connection label reference
        private Panel _statusBar;
        private Label _errorLabel;
        private Panel _connectionIndicator;
        private Label _comPortLabel;
        private Label _connectionStatusLabel; // NEW: Reference to connection status label

        // Connection state
        private bool _isConnected = false;
        private string _currentPort = "";
        private int _errorCount = 0;

        // Auto-detection timer
        private System.Windows.Forms.Timer _busMonitorTimer;

        #endregion

        #region Constructor

        public MainForm()
        {
            InitializeCustomComponents();
            SetupEventHandlers();
            UpdateConnectionState(false);
        }

        #endregion

        #region Initialization

        private void InitializeCustomComponents()
        {
            // Set form properties
            this.Text = "Unified DDR SPD Flasher v2.2";
            this.Size = new Size(1500, 900);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Icon = SystemIcons.Application;

            // Main layout panel
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));

            // Tab control for main content
            _mainTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                Padding = new Point(10, 5)
            };

            // Initialize tabs
            _spdTab = new SPDOperationsTab();
            _pmicTab = new PMICOperationsTab();
            _configTab = new FlasherConfigTab();

            // Add tabs to control
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

            mainLayout.Controls.Add(_mainTabControl, 0, 0);

            // Bottom status bar
            _statusBar = CreateBottomStatusBar();
            mainLayout.Controls.Add(_statusBar, 0, 1);

            this.Controls.Add(mainLayout);

            // Setup bus monitoring timer for auto-detection
            _busMonitorTimer = new System.Windows.Forms.Timer();
            _busMonitorTimer.Interval = 2000; // Check every 2 seconds
            _busMonitorTimer.Tick += OnBusMonitorTick;
        }

        private Panel CreateBottomStatusBar()
        {
            Panel statusBar = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                Padding = new Padding(8, 0, 8, 0)
            };

            // Left side - Errors
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

            // Right side panel
            FlowLayoutPanel rightPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false
            };

            // COM Port label
            _comPortLabel = new Label
            {
                Text = "Using COM: ---",
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 8.5F),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(8, 4, 0, 0)
            };

            // Connection indicator
            _connectionIndicator = new Panel
            {
                Width = 12,
                Height = 12,
                BackColor = Color.Red,
                Margin = new Padding(8, 6, 0, 0)
            };

            // Connection status label - STORE REFERENCE
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

        #region Connection Management

        private void OnConnectRequested(object sender, EventArgs e)
        {
            try
            {
                string portName = _configTab.GetSelectedPort();
                int baudRate = _configTab.GetBaudRate();

                if (string.IsNullOrEmpty(portName) || portName == "No ports available")
                {
                    LogError("Please select a valid COM port first.");
                    MessageBox.Show("Please select a valid COM port first.", "Connection Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                LogMessage($"Attempting connection to {portName} at {baudRate} baud...");

                // Create device connection
                _spdDevice = new SPDToolDevice(portName, baudRate);

                // Test connection with retry
                bool pingSuccess = false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        if (_spdDevice.Ping())
                        {
                            pingSuccess = true;
                            break;
                        }
                    }
                    catch
                    {
                        System.Threading.Thread.Sleep(100);
                    }
                }

                if (!pingSuccess)
                {
                    throw new Exception("Device not responding to ping after 3 attempts");
                }

                // Get device info
                uint version = _spdDevice.GetVersion();
                string deviceName = _spdDevice.GetDeviceName();

                _currentPort = portName;
                _isConnected = true;

                // Setup alert handler
                _spdDevice.AlertReceived += OnDeviceAlert;

                // Notify tabs
                _spdTab.OnDeviceConnected(_spdDevice);
                _pmicTab.OnDeviceConnected(_spdDevice);
                _configTab.OnDeviceConnected(_spdDevice);

                // Start bus monitoring
                _busMonitorTimer.Start();

                // Update UI
                UpdateConnectionState(true);

                LogMessage($"? Connected to {deviceName} (FW: 0x{version:X8}) on {portName}");

                MessageBox.Show($"Connected successfully!\n\nDevice: {deviceName}\nFirmware: 0x{version:X8}",
                    "Connection Successful",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _isConnected = false;
                UpdateConnectionState(false);

                LogError($"Connection failed: {ex.Message}");
                MessageBox.Show($"Failed to connect:\n\n{ex.Message}\n\n" +
                    "Check:\n1. Device is powered\n2. Correct COM port\n3. Drivers installed\n4. No other software using the port",
                    "Connection Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                if (_spdDevice != null)
                {
                    _spdDevice.Dispose();
                    _spdDevice = null;
                }
            }
        }

        private void OnDisconnectRequested(object sender, EventArgs e)
        {
            try
            {
                // Stop bus monitoring
                _busMonitorTimer.Stop();

                if (_spdDevice != null)
                {
                    // Remove event handler to prevent memory leak
                    _spdDevice.AlertReceived -= OnDeviceAlert;
                    _spdDevice.Dispose();
                    _spdDevice = null;
                }

                _isConnected = false;
                _currentPort = "";

                // Notify tabs
                _spdTab.OnDeviceDisconnected();
                _pmicTab.OnDeviceDisconnected();
                _configTab.OnDeviceDisconnected();

                // Update UI
                UpdateConnectionState(false);

                LogMessage("Device disconnected");
            }
            catch (Exception ex)
            {
                LogError($"Error during disconnect: {ex.Message}");
            }
        }

        private void OnDeviceAlert(object sender, AlertEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnDeviceAlert(sender, e)));
                return;
            }

            LogMessage($"[ALERT] {e.AlertType}");

            // Handle bus changes - trigger scan
            if (e.AlertCode == 0x2B || e.AlertCode == 0x2D)
            {
                OnBusMonitorTick(this, EventArgs.Empty);
            }
        }

        private void OnBusMonitorTick(object sender, EventArgs e)
        {
            if (_spdDevice != null && _isConnected)
            {
                try
                {
                    var devices = _spdDevice.ScanBus();

                    if (InvokeRequired)
                    {
                        Invoke(new Action(() => {
                            _spdTab.UpdateDetectedDevices(devices);
                            _pmicTab.UpdateDetectedDevices(devices);
                        }));
                    }
                    else
                    {
                        _spdTab.UpdateDetectedDevices(devices);
                        _pmicTab.UpdateDetectedDevices(devices);
                    }
                }
                catch (Exception ex)
                {
                    // Log but don't show error dialog for bus scan failures
                    System.Diagnostics.Debug.WriteLine($"Bus scan error: {ex.Message}");
                }
            }
        }

        // FIXED: Properly updates connection status in status bar
        private void UpdateConnectionState(bool connected)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => UpdateConnectionState(connected)));
                return;
            }

            _isConnected = connected;

            // Update tabs
            _spdTab.Enabled = connected;
            _pmicTab.Enabled = connected;

            // Update status bar - FIXED: Use stored reference
            _connectionIndicator.BackColor = connected ? Color.LimeGreen : Color.Red;
            _comPortLabel.Text = connected ? $"Using COM: {_currentPort}" : "Using COM: ---";

            // FIXED: Update connection status label text and color
            if (_connectionStatusLabel != null)
            {
                _connectionStatusLabel.Text = connected ? "Connected" : "Disconnected";
                _connectionStatusLabel.ForeColor = connected ? Color.LimeGreen : Color.LightGray;
            }

            // Update config tab
            _configTab.SetConnectionState(connected);
        }

        private void OnPortSettingsChanged(object sender, EventArgs e)
        {
            if (_isConnected)
            {
                var result = MessageBox.Show(
                    "Changing port settings requires reconnection. Disconnect now?",
                    "Port Settings Changed",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    OnDisconnectRequested(this, EventArgs.Empty);
                }
            }
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
            if (InvokeRequired)
            {
                Invoke(new Action(() => UpdateErrorDisplay()));
            }
            else
            {
                UpdateErrorDisplay();
            }
            LogMessage($"[ERROR] {message}");
        }

        private void UpdateErrorDisplay()
        {
            if (_errorLabel != null)
            {
                _errorLabel.Text = $"Errors: {_errorCount}";
                _errorLabel.ForeColor = _errorCount > 0 ? Color.Yellow : Color.White;
            }
        }

        private void LogMessage(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            System.Diagnostics.Debug.WriteLine($"[MainForm] {timestamp}: {message}");
        }

        #endregion

        #region Form Events

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_isConnected)
            {
                var result = MessageBox.Show(
                    "Device is still connected. Close anyway?",
                    "Confirm Exit",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.No)
                {
                    e.Cancel = true;
                    return;
                }

                OnDisconnectRequested(this, EventArgs.Empty);
            }

            // Properly dispose timer to prevent memory leak
            if (_busMonitorTimer != null)
            {
                _busMonitorTimer.Stop();
                _busMonitorTimer.Tick -= OnBusMonitorTick;
                _busMonitorTimer.Dispose();
                _busMonitorTimer = null;
            }

            base.OnFormClosing(e);
        }

        #endregion
    }
}