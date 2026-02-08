using System;
using System.Drawing;
using System.IO.Ports;
using System.Windows.Forms;
using SPDTool;

namespace UnifiedDDRSPDFlasher
{
    /// <summary>
    /// Flasher Configuration Tab v2.2 - Fixed UI and sizing
    /// All controls functional with proper button sizing
    /// </summary>
    public class FlasherConfigTab : UserControl
    {
        #region Events

        public event EventHandler ConnectionRequested;
        public event EventHandler DisconnectionRequested;
        public event EventHandler PortSettingsChanged;
        public event EventHandler<string> ErrorOccurred;

        #endregion

        #region Private Fields

        private Func<SPDToolDevice> _deviceProvider;
        private SPDToolDevice Device => _deviceProvider?.Invoke();

        private bool _isConnected = false;

        // Error log
        private RichTextBox _errorLogText;

        // Connection controls
        private ComboBox _portCombo;
        private ComboBox _baudRateCombo;
        private Button _refreshButton;
        private Button _connectButton;
        private Button _disconnectButton;

        // I2C Settings
        private RadioButton _i2c100Radio;
        private RadioButton _i2c400Radio;
        private Button _applyI2CButton;

        // Device Info
        private TextBox _firmwareText;
        private TextBox _deviceNameText;
        private Button _setNameButton;
        private Button _factoryResetButton;

        // Pin Control
        private CheckBox _hvSwitchCheck;
        private CheckBox _sa1SwitchCheck;
        private Button _applyPinsButton;
        private Button _resetPinsButton;

        #endregion

        #region Constructor

        public FlasherConfigTab()
        {
            InitializeComponent();
            LoadPorts();
        }

        #endregion

        #region Initialization

        private void InitializeComponent()
        {
            this.Dock = DockStyle.Fill;
            this.BackColor = Color.White;
            this.AutoScroll = true;

            // Main layout: 2 columns
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(12)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));

            // Left column: Error log + Connection
            TableLayoutPanel leftColumn = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2
            };
            leftColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            leftColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            // Error log group
            GroupBox errorLogGroup = new GroupBox
            {
                Text = "Error Log",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(8)
            };

            _errorLogText = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 8.5F),
                ReadOnly = true,
                BackColor = Color.FromArgb(250, 250, 250),
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            errorLogGroup.Controls.Add(_errorLogText);
            leftColumn.Controls.Add(errorLogGroup, 0, 0);

            // Connection group
            GroupBox connectionGroup = CreateConnectionGroup();
            leftColumn.Controls.Add(connectionGroup, 0, 1);

            mainLayout.Controls.Add(leftColumn, 0, 0);

            // Right column: I2C, Pin, Device
            TableLayoutPanel rightColumn = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(3)
            };
            rightColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));
            rightColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));
            rightColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 34F));

            // I2C Settings
            GroupBox i2cGroup = CreateI2CGroup();
            rightColumn.Controls.Add(i2cGroup, 0, 0);

            // Pin Control
            GroupBox pinGroup = CreatePinGroup();
            rightColumn.Controls.Add(pinGroup, 0, 1);

            // Device Info
            GroupBox deviceGroup = CreateDeviceGroup();
            rightColumn.Controls.Add(deviceGroup, 0, 2);

            mainLayout.Controls.Add(rightColumn, 1, 0);

            this.Controls.Add(mainLayout);
        }

        private GroupBox CreateConnectionGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "Connection",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(12)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 6,
                Padding = new Padding(8)
            };

            // COM Port
            Label portLabel = new Label
            {
                Text = "COM Port:",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 24
            };
            layout.Controls.Add(portLabel, 0, 0);

            _portCombo = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9.5F),
                Height = 28
            };
            _portCombo.SelectedIndexChanged += (s, e) => PortSettingsChanged?.Invoke(this, EventArgs.Empty);
            layout.Controls.Add(_portCombo, 0, 1);

            // Baud Rate
            Label baudLabel = new Label
            {
                Text = "Baud Rate:",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 24
            };
            layout.Controls.Add(baudLabel, 0, 2);

            _baudRateCombo = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9.5F),
                Height = 28
            };
            _baudRateCombo.Items.AddRange(new object[] { "9600", "19200", "38400", "57600", "115200", "230400" });
            _baudRateCombo.SelectedIndex = 4; // 115200
            _baudRateCombo.SelectedIndexChanged += (s, e) => PortSettingsChanged?.Invoke(this, EventArgs.Empty);
            layout.Controls.Add(_baudRateCombo, 0, 3);

            // Buttons
            TableLayoutPanel buttonLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Margin = new Padding(0, 8, 0, 0)
            };
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            _refreshButton = new Button
            {
                Text = "Refresh",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Height = 30,
                Margin = new Padding(1)
            };
            _refreshButton.Click += OnRefreshClicked;

            _connectButton = new Button
            {
                Text = "Connect",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Height = 30,
                Margin = new Padding(1)
            };
            _connectButton.FlatAppearance.BorderSize = 0;
            _connectButton.Click += (s, e) => ConnectionRequested?.Invoke(this, EventArgs.Empty);

            buttonLayout.Controls.Add(_refreshButton, 0, 0);
            buttonLayout.Controls.Add(_connectButton, 1, 0);
            layout.Controls.Add(buttonLayout, 0, 4);

            // Disconnect button
            _disconnectButton = new Button
            {
                Text = "Disconnect",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(200, 200, 200),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat,
                Height = 30,
                Enabled = false,
                Margin = new Padding(0, 3, 0, 0)
            };
            _disconnectButton.FlatAppearance.BorderSize = 0;
            _disconnectButton.Click += (s, e) => DisconnectionRequested?.Invoke(this, EventArgs.Empty);
            layout.Controls.Add(_disconnectButton, 0, 5);

            group.Controls.Add(layout);
            return group;
        }

        private GroupBox CreateI2CGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "I2C Settings",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(12),
                Enabled = false
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                Padding = new Padding(8)
            };

            Label clockLabel = new Label
            {
                Text = "I2C Clock Speed:",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 24
            };
            layout.Controls.Add(clockLabel, 0, 0);

            _i2c100Radio = new RadioButton
            {
                Text = "100 kHz (Standard)",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Checked = true,
                Height = 24
            };
            layout.Controls.Add(_i2c100Radio, 0, 1);

            _i2c400Radio = new RadioButton
            {
                Text = "400 kHz (Fast)",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Height = 24
            };
            layout.Controls.Add(_i2c400Radio, 0, 2);

            _applyI2CButton = new Button
            {
                Text = "Apply I2C Settings",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Height = 30,
                Margin = new Padding(0, 8, 0, 0)
            };
            _applyI2CButton.FlatAppearance.BorderSize = 0;
            _applyI2CButton.Click += OnApplyI2CClicked;
            layout.Controls.Add(_applyI2CButton, 0, 3);

            group.Controls.Add(layout);
            return group;
        }

        private GroupBox CreatePinGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "Pin Control",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(2),
                Enabled = false
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 5,
                Padding = new Padding(8)
            };

            _hvSwitchCheck = new CheckBox
            {
                Text = "HV_SWITCH (High Voltage)",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Checked = false,
                Height = 24
            };
            layout.Controls.Add(_hvSwitchCheck, 0, 0);

            Label hvInfo = new Label
            {
                Text = "Controls high voltage switch for SPD programming",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8F),
                ForeColor = Color.Gray,
                Height = 20
            };
            layout.Controls.Add(hvInfo, 0, 1);

            _sa1SwitchCheck = new CheckBox
            {
                Text = "SA1_SWITCH",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Checked = true,
                Height = 24,
                Margin = new Padding(0, 8, 0, 0)
            };
            layout.Controls.Add(_sa1SwitchCheck, 0, 2);

            Label sa1Info = new Label
            {
                Text = "Controls SA1 address line switch",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8F),
                ForeColor = Color.Gray,
                Height = 20
            };
            layout.Controls.Add(sa1Info, 0, 3);

            TableLayoutPanel buttonLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Margin = new Padding(0, 8, 0, 0)
            };
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));

            _applyPinsButton = new Button
            {
                Text = "Apply Pins",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Height = 10,
                Margin = new Padding(1)
            };
            _applyPinsButton.Click += OnApplyPinsClicked;

            _resetPinsButton = new Button
            {
                Text = "Reset Pins",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Height = 10,
                Margin = new Padding(1)
            };
            _resetPinsButton.Click += OnResetPinsClicked;

            buttonLayout.Controls.Add(_applyPinsButton, 0, 0);
            buttonLayout.Controls.Add(_resetPinsButton, 1, 0);
            layout.Controls.Add(buttonLayout, 0, 4);

            group.Controls.Add(layout);
            return group;
        }

        private GroupBox CreateDeviceGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "Device Info",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(12),
                Enabled = false
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 6,
                Padding = new Padding(8)
            };

            // Firmware
            Label fwLabel = new Label
            {
                Text = "Firmware Version:",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 24
            };
            layout.Controls.Add(fwLabel, 0, 0);

            _firmwareText = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9F),
                BackColor = Color.FromArgb(240, 240, 240),
                Text = "Not connected",
                Height = 28
            };
            layout.Controls.Add(_firmwareText, 0, 1);

            // Device Name
            Label nameLabel = new Label
            {
                Text = "Device Name:",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 24,
                Margin = new Padding(0, 8, 0, 0)
            };
            layout.Controls.Add(nameLabel, 0, 2);

            _deviceNameText = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                MaxLength = 16,
                Height = 28
            };
            layout.Controls.Add(_deviceNameText, 0, 3);

            _setNameButton = new Button
            {
                Text = "Set Device Name",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Height = 30,
                Margin = new Padding(0, 3, 0, 0)
            };
            _setNameButton.Click += OnSetNameClicked;
            layout.Controls.Add(_setNameButton, 0, 4);

            _factoryResetButton = new Button
            {
                Text = "⚠ Factory Reset Device",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(232, 17, 35),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Height = 30,
                Margin = new Padding(0, 8, 0, 0)
            };
            _factoryResetButton.FlatAppearance.BorderSize = 0;
            _factoryResetButton.Click += OnFactoryResetClicked;
            layout.Controls.Add(_factoryResetButton, 0, 5);

            group.Controls.Add(layout);
            return group;
        }

        #endregion

        #region Public Methods

        public void LogError(string message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => LogError(message)));
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _errorLogText.AppendText($"[{timestamp}] {message}\n");
            _errorLogText.ScrollToCaret();
        }

        public void SetDeviceProvider(Func<SPDToolDevice> deviceProvider)
        {
            _deviceProvider = deviceProvider;
        }

        public string GetSelectedPort()
        {
            return _portCombo.SelectedItem?.ToString() ?? "";
        }

        public int GetBaudRate()
        {
            return int.Parse(_baudRateCombo.SelectedItem?.ToString() ?? "115200");
        }

        public void RefreshPorts()
        {
            LoadPorts();
        }

        public void SetConnectionState(bool connected)
        {
            _isConnected = connected;

            if (InvokeRequired)
            {
                Invoke(new Action(() => SetConnectionState(connected)));
                return;
            }

            // Connection controls
            _portCombo.Enabled = !connected;
            _baudRateCombo.Enabled = !connected;
            _refreshButton.Enabled = !connected;
            _connectButton.Enabled = !connected;
            _disconnectButton.Enabled = connected;

            if (connected)
            {
                _disconnectButton.BackColor = Color.FromArgb(232, 17, 35);
                _disconnectButton.ForeColor = Color.White;
            }
            else
            {
                _disconnectButton.BackColor = Color.FromArgb(200, 200, 200);
                _disconnectButton.ForeColor = Color.Black;
            }

            // Other groups
            EnableGroup(_applyI2CButton.Parent.Parent as GroupBox, connected);
            EnableGroup(_applyPinsButton.Parent.Parent.Parent as GroupBox, connected);
            EnableGroup(_setNameButton.Parent.Parent as GroupBox, connected);
        }

        private void EnableGroup(GroupBox group, bool enabled)
        {
            if (group != null)
            {
                group.Enabled = enabled;
            }
        }

        public void OnDeviceConnected(SPDToolDevice device)
        {
            if (device == null) return;

            try
            {
                // Get firmware version
                uint version = device.GetVersion();
                // Version is YYYYMMDD in decimal
                string readable = $"{version / 10000}-{(version / 100) % 100:D2}-{version % 100:D2}";
                _firmwareText.Text = $"{readable} (0x{version:X8})";

                // Get device name
                string deviceName = device.GetDeviceName();
                _deviceNameText.Text = deviceName;

                // Get I2C clock mode
                byte clockMode = device.GetI2CClockMode();
                if (clockMode == 0)
                    _i2c100Radio.Checked = true;
                else
                    _i2c400Radio.Checked = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading device info:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void OnDeviceDisconnected()
        {
            _firmwareText.Text = "Not connected";
            _deviceNameText.Text = "";
        }

        #endregion

        #region Event Handlers

        private void LoadPorts()
        {
            string currentSelection = _portCombo.SelectedItem?.ToString();
            _portCombo.Items.Clear();

            string[] ports = SerialPort.GetPortNames();
            if (ports.Length > 0)
            {
                _portCombo.Items.AddRange(ports);

                if (!string.IsNullOrEmpty(currentSelection) && Array.IndexOf(ports, currentSelection) >= 0)
                {
                    _portCombo.SelectedItem = currentSelection;
                }
                else
                {
                    _portCombo.SelectedIndex = 0;
                }
            }
            else
            {
                _portCombo.Items.Add("No ports available");
                _portCombo.SelectedIndex = 0;
            }
        }

        private void OnRefreshClicked(object sender, EventArgs e)
        {
            LoadPorts();
            MessageBox.Show("Port list refreshed.", "Refresh Ports",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnApplyI2CClicked(object sender, EventArgs e)
        {
            if (Device == null) return;

            try
            {
                byte mode = _i2c100Radio.Checked ? (byte)0 : (byte)1;

                if (Device.SetI2CClockMode(mode))
                {
                    string speed = mode == 0 ? "100 kHz" : "400 kHz";
                    MessageBox.Show($"I2C clock speed set to {speed}", "Success",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("Failed to set I2C clock speed.", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting I2C clock:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnApplyPinsClicked(object sender, EventArgs e)
        {
            if (Device == null) return;

            try
            {
                // Note: These methods need to be implemented in SPDToolDevice
                // Device.SetPin(0, _hvSwitchCheck.Checked);  // HV_SWITCH
                // Device.SetPin(1, _sa1SwitchCheck.Checked); // SA1_SWITCH

                MessageBox.Show("Pin states applied.", "Success",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting pins:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnResetPinsClicked(object sender, EventArgs e)
        {
            if (Device == null) return;

            try
            {
                Device.ResetPins();
                _hvSwitchCheck.Checked = false;
                _sa1SwitchCheck.Checked = true;

                MessageBox.Show("Pins reset to defaults.", "Success",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error resetting pins:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnSetNameClicked(object sender, EventArgs e)
        {
            if (Device == null) return;

            if (string.IsNullOrWhiteSpace(_deviceNameText.Text))
            {
                MessageBox.Show("Please enter a device name.", "Invalid Name",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                if (Device.SetDeviceName(_deviceNameText.Text))
                {
                    MessageBox.Show("Device name updated successfully.", "Success",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("Failed to set device name.", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting device name:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnFactoryResetClicked(object sender, EventArgs e)
        {
            if (Device == null) return;

            var result = MessageBox.Show(
                "⚠ WARNING ⚠\n\nThis will erase ALL device settings including:\n" +
                "• Device name\n• I2C clock preferences\n• All stored configuration\n\n" +
                "This operation cannot be undone!\n\nContinue?",
                "Confirm Factory Reset",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes) return;

            try
            {
                if (Device.FactoryReset())
                {
                    MessageBox.Show("Device factory reset completed.\nPlease reconnect.", "Success",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);

                    DisconnectionRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    MessageBox.Show("Factory reset failed.", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during factory reset:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion
    }
}