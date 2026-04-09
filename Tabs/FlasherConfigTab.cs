// ADDED: Live COM-port refresh (no manual Refresh button needed), device name & FW version
//        moved into Connection group, "Set Device Name" / "Factory Reset" / "Auto-connect"
//        moved to Advanced menu, full Manual Pin Control panel in Advanced dialog.
using System;
using System.Drawing;
using System.IO.Ports;
using System.Threading;
using System.Windows.Forms;
using SPDTool;

namespace UnifiedDDRSPDFlasher
{
    /// <summary>
    /// Flasher Configuration Tab – version 3.0
    /// Redesigned UI: Connection group now includes FW version and device name readout.
    /// Advanced menu exposes: Set Device Name, Factory Reset, Auto-Connect, Manual Pin Control.
    /// COM port list refreshes automatically via a timer.
    /// </summary>
    public class FlasherConfigTab : UserControl
    {
        #region Constants

        private const int DEFAULT_BAUD_RATE = 115200;
        private const int PORT_REFRESH_INTERVAL_MS = 2000;

        #endregion

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

        // Connection controls
        private ComboBox _portCombo;
        private Button _connectButton;
        private Button _disconnectButton;
        private Button _advancedButton;
        private Label _firmwareLabel;
        private Label _deviceNameLabel;

        // I2C Settings
        private RadioButton _i2c100Radio;
        private RadioButton _i2c400Radio;
        private RadioButton _i2c1MRadio;
        private Button _applyI2CButton;

        // Error log
        private RichTextBox _errorLogText;

        // Timers
        private System.Windows.Forms.Timer _portRefreshTimer;

        // State
        private string[] _lastKnownPorts = Array.Empty<string>();

        private ToolTip _toolTip;

        #endregion

        #region Constructor

        public FlasherConfigTab()
        {
            _toolTip = new ToolTip { AutoPopDelay = 6000, InitialDelay = 400 };
            InitializeComponent();

            // Live COM-port refresh
            _portRefreshTimer = new System.Windows.Forms.Timer { Interval = PORT_REFRESH_INTERVAL_MS };
            _portRefreshTimer.Tick += OnPortRefreshTick;
            _portRefreshTimer.Start();

            LoadPorts();
        }

        #endregion

        #region Initialization

        private void InitializeComponent()
        {
            this.Dock = DockStyle.Fill;
            this.BackColor = Color.FromArgb(240, 240, 240);

            // Main layout: 2 columns
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(12)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));

            // Left: Error log + Connection
            TableLayoutPanel leftCol = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2
            };
            leftCol.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            leftCol.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            leftCol.Controls.Add(CreateErrorLogGroup(), 0, 0);
            leftCol.Controls.Add(CreateConnectionGroup(), 0, 1);
            mainLayout.Controls.Add(leftCol, 0, 0);

            // Right: I2C settings only (Device group merged into Connection)
            TableLayoutPanel rightCol = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 1,
                Padding = new Padding(3)
            };
            rightCol.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            rightCol.Controls.Add(CreateI2CGroup(), 0, 0);
            mainLayout.Controls.Add(rightCol, 1, 0);

            this.Controls.Add(mainLayout);
        }

        private GroupBox CreateErrorLogGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "Error Log",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            _errorLogText = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 8.5F),
                ReadOnly = true,
                BackColor = Color.FromArgb(250, 250, 250),
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            group.Controls.Add(_errorLogText);
            return group;
        }

        private GroupBox CreateConnectionGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "Connection",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(12),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 7,
                Padding = new Padding(6)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F)); // label
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F)); // combo
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F)); // buttons
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F)); // disconnect
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10F)); // spacer
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F)); // fw label
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // device name

            // COM Port label
            var portLabel = new Label
            {
                Text = "COM Port:  (auto-refreshed)",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5F),
                TextAlign = ContentAlignment.BottomLeft,
                ForeColor = Color.Gray
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

            // Connect / Advanced row
            TableLayoutPanel buttonRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Margin = new Padding(0, 4, 0, 0)
            };
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));

            _connectButton = new Button
            {
                Text = "Connect",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Height = 30,
                Margin = new Padding(0, 0, 3, 0)
            };
            _connectButton.FlatAppearance.BorderSize = 0;
            _connectButton.Click += (s, e) => ConnectionRequested?.Invoke(this, EventArgs.Empty);
            _toolTip.SetToolTip(_connectButton, "Connect to the selected COM port.");

            _advancedButton = new Button
            {
                Text = "Advanced…",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                FlatStyle = FlatStyle.Flat,
                Height = 30,
                Margin = new Padding(0)
            };
            _advancedButton.FlatAppearance.BorderColor = Color.Silver;
            _advancedButton.Click += OnAdvancedClicked;
            _toolTip.SetToolTip(_advancedButton, "Device name, factory reset, auto-connect, pin control.");

            buttonRow.Controls.Add(_connectButton, 0, 0);
            buttonRow.Controls.Add(_advancedButton, 1, 0);
            layout.Controls.Add(buttonRow, 0, 2);

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
            layout.Controls.Add(_disconnectButton, 0, 3);

            // Spacer
            layout.Controls.Add(new Panel(), 0, 4);

            // Firmware version (read-only, shown after connect)
            _firmwareLabel = new Label
            {
                Text = "Firmware: —",
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 8.5F),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray
            };
            layout.Controls.Add(_firmwareLabel, 0, 5);

            _deviceNameLabel = new Label
            {
                Text = "Device name: —",
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 8.5F),
                TextAlign = ContentAlignment.TopLeft,
                ForeColor = Color.DimGray
            };
            layout.Controls.Add(_deviceNameLabel, 0, 6);

            group.Controls.Add(layout);
            return group;
        }

        private GroupBox CreateI2CGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "I2C Bus Settings",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(12),
                Enabled = false,
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 5,
                Padding = new Padding(8)
            };

            var clockLabel = new Label
            {
                Text = "I2C Clock Speed:",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 24
            };
            layout.Controls.Add(clockLabel, 0, 0);

            _i2c100Radio = new RadioButton { Text = "100 kHz  (Standard – safest)", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F), Checked = true, Height = 24 };
            _i2c400Radio = new RadioButton { Text = "400 kHz  (Fast)", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F), Height = 24 };
            _i2c1MRadio = new RadioButton { Text = "1 MHz    (Fast-Plus – DDR5 recommended)", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F), Height = 24 };

            _toolTip.SetToolTip(_i2c100Radio, "Standard mode. Use when signal integrity is a concern.");
            _toolTip.SetToolTip(_i2c400Radio, "Fast mode. Compatible with most DIMM SPD devices.");
            _toolTip.SetToolTip(_i2c1MRadio, "Fast-Plus mode. Required by JEDEC DDR5 SPD spec for fast page reads.");

            layout.Controls.Add(_i2c100Radio, 0, 1);
            layout.Controls.Add(_i2c400Radio, 0, 2);
            layout.Controls.Add(_i2c1MRadio, 0, 3);

            _applyI2CButton = new Button
            {
                Text = "Apply I2C Settings",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Height = 32,
                Margin = new Padding(0, 10, 0, 0)
            };
            _applyI2CButton.FlatAppearance.BorderSize = 0;
            _applyI2CButton.Click += OnApplyI2CClicked;
            _toolTip.SetToolTip(_applyI2CButton, "Send the selected I2C clock mode to the programmer firmware.");
            layout.Controls.Add(_applyI2CButton, 0, 4);

            group.Controls.Add(layout);
            return group;
        }

        #endregion

        #region Public Methods

        public void LogError(string message)
        {
            if (InvokeRequired) { Invoke(new Action(() => LogError(message))); return; }

            string ts = DateTime.Now.ToString("HH:mm:ss");
            _errorLogText.SelectionColor = message.Contains("[ERROR]") ? Color.DarkRed : Color.Black;
            _errorLogText.AppendText($"[{ts}] {message}\n");
            _errorLogText.ScrollToCaret();
        }

        public void SetDeviceProvider(Func<SPDToolDevice> deviceProvider) =>
            _deviceProvider = deviceProvider;

        public string GetSelectedPort() => _portCombo.SelectedItem?.ToString() ?? "";
        public int GetBaudRate() => DEFAULT_BAUD_RATE;

        public void RefreshPorts() => LoadPorts();

        public void SetConnectionState(bool connected)
        {
            _isConnected = connected;
            if (InvokeRequired) { Invoke(new Action(() => SetConnectionState(connected))); return; }

            _portCombo.Enabled = !connected;
            _connectButton.Enabled = !connected;
            _disconnectButton.Enabled = connected;

            if (connected)
            {
                _disconnectButton.BackColor = Color.FromArgb(192, 0, 0);
                _disconnectButton.ForeColor = Color.White;
            }
            else
            {
                _disconnectButton.BackColor = Color.FromArgb(200, 200, 200);
                _disconnectButton.ForeColor = Color.Black;
            }

            // Enable I2C group when connected
            var i2cParent = _applyI2CButton.Parent?.Parent as GroupBox;
            if (i2cParent != null) i2cParent.Enabled = connected;
        }

        public void OnDeviceConnected(SPDToolDevice device)
        {
            if (device == null) return;
            try
            {
                uint version = device.GetVersion();
                string readable = $"{version / 10000}-{(version / 100) % 100:D2}-{version % 100:D2}";
                _firmwareLabel.Text = $"Firmware: {readable}";

                string devName = device.GetDeviceName();
                _deviceNameLabel.Text = $"Device: {(string.IsNullOrEmpty(devName) ? "(unnamed)" : devName)}";

                byte clockMode = device.GetI2CClockMode();
                if (clockMode == 0) _i2c100Radio.Checked = true;
                else if (clockMode == 1) _i2c400Radio.Checked = true;
                else _i2c1MRadio.Checked = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading device info:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                ErrorOccurred?.Invoke(this, $"Device info read failed: {ex.Message}");
            }
        }

        public void OnDeviceDisconnected()
        {
            _firmwareLabel.Text = "Firmware: —";
            _deviceNameLabel.Text = "Device: —";
        }

        #endregion

        #region Event Handlers

        // Live port refresh – silently update list only when ports change
        private void OnPortRefreshTick(object sender, EventArgs e)
        {
            if (_isConnected) return;
            string[] current = SerialPort.GetPortNames();
            if (!string.Join(",", current).Equals(string.Join(",", _lastKnownPorts)))
            {
                _lastKnownPorts = current;
                LoadPorts();
            }
        }

        private void LoadPorts()
        {
            if (InvokeRequired) { Invoke(new Action(LoadPorts)); return; }

            string current = _portCombo.SelectedItem?.ToString();
            _portCombo.Items.Clear();

            string[] ports = SerialPort.GetPortNames();
            if (ports.Length > 0)
            {
                _portCombo.Items.AddRange(ports);
                if (!string.IsNullOrEmpty(current) && Array.IndexOf(ports, current) >= 0)
                    _portCombo.SelectedItem = current;
                else
                    _portCombo.SelectedIndex = 0;
            }
            else
            {
                _portCombo.Items.Add("No ports available");
                _portCombo.SelectedIndex = 0;
            }
        }

        private void OnApplyI2CClicked(object sender, EventArgs e)
        {
            if (Device == null) return;
            try
            {
                byte mode = _i2c100Radio.Checked ? (byte)0 : (_i2c400Radio.Checked ? (byte)1 : (byte)2);
                string[] names = { "100 kHz", "400 kHz", "1 MHz" };

                if (Device.SetI2CClockMode(mode))
                    LogError($"[INFO] I2C clock set to {names[mode]}");
                else
                    LogError($"[ERROR] Failed to set I2C clock to {names[mode]}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting I2C clock:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                ErrorOccurred?.Invoke(this, $"Setting I2C clock failed: {ex.Message}");
            }
        }

        // ADDED: Advanced menu button
        private void OnAdvancedClicked(object sender, EventArgs e)
        {
            using var dlg = new AdvancedConfigDialog(Device, _deviceProvider,
                DisconnectionRequested, ConnectionRequested, this);
            dlg.ShowDialog(this);

            // Refresh name after dialog (user may have changed it)
            if (Device != null)
            {
                try { _deviceNameLabel.Text = $"Device: {Device.GetDeviceName()}"; }
                catch { }
            }
        }

        #endregion
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ADDED: Advanced Configuration Dialog
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Advanced programmer options:
    /// – Set Device Name  – Factory Reset  – Auto-Connect  – Manual Pin Control
    /// </summary>
    internal sealed class AdvancedConfigDialog : Form
    {
        private readonly SPDToolDevice _device;
        private readonly Func<SPDToolDevice> _provider;
        private readonly EventHandler _disconnectHandler;
        private readonly EventHandler _connectHandler;
        private readonly FlasherConfigTab _parent;

        private TextBox _nameText;

        // Pin readout labels (updated by timer)
        private Label[] _pinStateLabels;
        private System.Windows.Forms.Timer _pinRefreshTimer;
        private const int AUTO_CONNECT_DELAY_MS = 500;
        private static readonly (string name, byte id, string tip)[] PinDefs =
        {
            ("HV Switch",     SPDToolDevice.PIN_HV_SWITCH,      "Opto-coupler that enables ~9 V HV programming for DDR3/4."),
            ("SA1 Switch",    SPDToolDevice.PIN_SA1_SWITCH,     "Selects between two SPD devices on some adapters."),
            ("Dev Status",    SPDToolDevice.PIN_DEV_STATUS,     "Status LED on the programmer board."),
            ("HV Converter",  SPDToolDevice.PIN_HV_CONVERTER,  "Enable pin of the HV boost converter (DDR3/4 WP clearing)."),
            ("DDR5 VIN Ctrl", SPDToolDevice.PIN_DDR5_VIN_CTRL, "DDR5 power-supply enable. Must be high for DDR5 access."),
            ("PMIC CTRL",     SPDToolDevice.PIN_PMIC_CTRL,     "PWR_EN line of the PMIC. Forces all phases active."),
            ("PMIC FLAG",     SPDToolDevice.PIN_PMIC_FLAG,     "PMIC PWR_GOOD input (read-only)."),
            ("RFU1",          SPDToolDevice.PIN_RFU1,          "Reserved for future use."),
            ("RFU2",          SPDToolDevice.PIN_RFU2,          "Reserved for future use."),
        };

        public AdvancedConfigDialog(SPDToolDevice device, Func<SPDToolDevice> provider,
            EventHandler disconnectHandler, EventHandler connectHandler, FlasherConfigTab parent)
        {
            _device = device;
            _provider = provider;
            _disconnectHandler = disconnectHandler;
            _connectHandler = connectHandler;
            _parent = parent;

            Text = "Advanced Configuration";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(560, 600);
            BackColor = Color.FromArgb(240, 240, 240);

            BuildUI();

            _pinRefreshTimer = new System.Windows.Forms.Timer { Interval = 800 };
            _pinRefreshTimer.Tick += (s, e) => RefreshPinStates();
            if (_device != null) _pinRefreshTimer.Start();

            this.FormClosed += (s, e) => _pinRefreshTimer.Stop();
        }

        private void BuildUI()
        {
            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                ColumnCount = 1,
                Padding = new Padding(14)
            };
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 90F));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 80F));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

            // ── Device Name ──────────────────────────────────────────────────
            GroupBox nameGroup = new GroupBox
            {
                Text = "Device Name",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            var nameFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            _nameText = new TextBox
            {
                Width = 200,
                MaxLength = 16,
                Font = new Font("Segoe UI", 9F),
                Text = (_device != null) ? TryGetName() : "",
                Enabled = _device != null
            };
            var setNameBtn = new Button
            {
                Text = "Set Name",
                Width = 90,
                Height = 26,
                Font = new Font("Segoe UI", 8.5F),
                Margin = new Padding(6, 0, 0, 0),
                Enabled = _device != null
            };
            setNameBtn.Click += OnSetNameClicked;
            nameFlow.Controls.Add(_nameText);
            nameFlow.Controls.Add(setNameBtn);
            nameGroup.Controls.Add(nameFlow);
            main.Controls.Add(nameGroup, 0, 0);

            // ── Factory Reset + Auto-connect ─────────────────────────────────
            GroupBox miscGroup = new GroupBox
            {
                Text = "Programmer Options",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            var miscFlow = new FlowLayoutPanel { Dock = DockStyle.Fill };

            var factoryBtn = new Button
            {
                Text = "⚠ Factory Reset",
                Width = 140,
                Height = 28,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(192, 0, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, 8, 0),
                Enabled = _device != null
            };
            factoryBtn.FlatAppearance.BorderSize = 0;
            factoryBtn.Click += OnFactoryResetClicked;

            var autoBtn = new Button
            {
                Text = "Auto-Connect",
                Width = 130,
                Height = 28,
                Font = new Font("Segoe UI", 9F),
                Margin = new Padding(0)
            };
            autoBtn.Click += OnAutoConnectClicked;

            miscFlow.Controls.Add(factoryBtn);
            miscFlow.Controls.Add(autoBtn);
            miscGroup.Controls.Add(miscFlow);
            main.Controls.Add(miscGroup, 0, 1);

            // ── Manual Pin Control ────────────────────────────────────────────
            GroupBox pinGroup = new GroupBox
            {
                Text = "Manual Pin Control  (live readout)",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            var pinTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = PinDefs.Length,
                ColumnCount = 4,
                AutoScroll = true
            };
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F)); // name
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54F));  // state
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));  // set hi
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); // set lo

            _pinStateLabels = new Label[PinDefs.Length];

            var tt = new ToolTip { AutoPopDelay = 6000, InitialDelay = 400 };

            for (int i = 0; i < PinDefs.Length; i++)
            {
                var (pinName, pinId, tip) = PinDefs[i];
                int capturedI = i;
                byte capturedPin = pinId;

                var lbl = new Label
                {
                    Text = pinName,
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 8.5F),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(2, 0, 0, 0)
                };
                tt.SetToolTip(lbl, tip);

                _pinStateLabels[i] = new Label
                {
                    Text = _device != null ? ReadPin(pinId) : "—",
                    Dock = DockStyle.Fill,
                    Font = new Font("Consolas", 8.5F, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.DimGray
                };

                var hiBtn = new Button
                {
                    Text = "Set HIGH",
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 8F),
                    Height = 24,
                    Margin = new Padding(1),
                    Enabled = _device != null && pinId != SPDToolDevice.PIN_PMIC_FLAG
                };
                hiBtn.Click += (s, e) =>
                {
                    _device?.SetPin(capturedPin, 0x01);
                    _pinStateLabels[capturedI].Text = ReadPin(capturedPin);
                };

                var loBtn = new Button
                {
                    Text = "Set LOW",
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 8F),
                    Height = 24,
                    Margin = new Padding(1),
                    Enabled = _device != null && pinId != SPDToolDevice.PIN_PMIC_FLAG
                };
                loBtn.Click += (s, e) =>
                {
                    _device?.SetPin(capturedPin, 0x00);
                    _pinStateLabels[capturedI].Text = ReadPin(capturedPin);
                };

                pinTable.Controls.Add(lbl, 0, i);
                pinTable.Controls.Add(_pinStateLabels[i], 1, i);
                pinTable.Controls.Add(hiBtn, 2, i);
                pinTable.Controls.Add(loBtn, 3, i);
            }

            // Reset all button at bottom of pin group
            var resetAllBtn = new Button
            {
                Text = "Reset All Pins to Default",
                Dock = DockStyle.Bottom,
                Font = new Font("Segoe UI", 9F),
                Height = 28,
                Enabled = _device != null
            };
            resetAllBtn.Click += (s, e) =>
            {
                _device?.ResetPins();
                RefreshPinStates();
            };

            var pinPanelWrapper = new Panel { Dock = DockStyle.Fill };
            pinPanelWrapper.Controls.Add(pinTable);
            pinPanelWrapper.Controls.Add(resetAllBtn);
            pinGroup.Controls.Add(pinPanelWrapper);
            main.Controls.Add(pinGroup, 0, 2);

            // ── Close ─────────────────────────────────────────────────────────
            var closeFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0)
            };
            var closeBtn = new Button
            {
                Text = "Close",
                Width = 90,
                Height = 30,
                Font = new Font("Segoe UI", 9F)
            };
            closeBtn.Click += (s, e) => Close();
            closeFlow.Controls.Add(closeBtn);
            main.Controls.Add(closeFlow, 0, 3);

            this.Controls.Add(main);
        }

        private string TryGetName()
        {
            try { return _device.GetDeviceName(); }
            catch { return ""; }
        }

        private string ReadPin(byte pin)
        {
            try { return _device.GetPin(pin) == 1 ? "HIGH" : "LOW"; }
            catch { return "—"; }
        }

        private void RefreshPinStates()
        {
            if (_device == null || _pinStateLabels == null) return;
            if (InvokeRequired) { Invoke(new Action(RefreshPinStates)); return; }

            for (int i = 0; i < PinDefs.Length; i++)
            {
                try
                {
                    byte state = _device.GetPin(PinDefs[i].id);
                    _pinStateLabels[i].Text = state == 1 ? "HIGH" : "LOW";
                    _pinStateLabels[i].ForeColor = state == 1 ? Color.DarkGreen : Color.DarkRed;
                }
                catch
                {
                    _pinStateLabels[i].Text = "—";
                    _pinStateLabels[i].ForeColor = Color.Gray;
                }
            }
        }

        private void OnSetNameClicked(object sender, EventArgs e)
        {
            if (_device == null) return;
            string name = _nameText.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Enter a device name (1–16 characters).", "Invalid",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                bool ok = _device.SetDeviceName(name);
                MessageBox.Show(ok ? "Device name updated." : "Failed to set device name.",
                    ok ? "Success" : "Error", MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnFactoryResetClicked(object sender, EventArgs e)
        {
            if (_device == null) return;
            var r = MessageBox.Show(
                "⚠ WARNING ⚠\n\nThis will erase ALL device settings:\n" +
                "• Device name\n• I2C clock preference\n• All stored configuration\n\n" +
                "This CANNOT be undone. Continue?",
                "Factory Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (r != DialogResult.Yes) return;

            try
            {
                if (_device.FactoryReset())
                {
                    MessageBox.Show("Factory reset complete. Reconnect the device.", "Done",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    _disconnectHandler?.Invoke(_parent, EventArgs.Empty);
                    Close();
                }
                else
                {
                    MessageBox.Show("Factory reset failed.", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnAutoConnectClicked(object sender, EventArgs e)
        {
            // Cycle through available ports and attempt a connection
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length == 0)
            {
                MessageBox.Show("No COM ports found.", "Auto-Connect",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // If already connected, disconnect first
            if (_device != null)
                _disconnectHandler?.Invoke(_parent, EventArgs.Empty);

            Thread.Sleep(AUTO_CONNECT_DELAY_MS);

            // Raise ConnectionRequested for each port until one works (MainForm will handle it)
            // We simply raise with the default port selection; MainForm handles the logic.
            _connectHandler?.Invoke(_parent, EventArgs.Empty);
            Close();
        }
    }
}