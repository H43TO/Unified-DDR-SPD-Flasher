// UPDATED v3.1:
//  - I2C bus speed selection moved from main tab into AdvancedConfigDialog (ComboBox).
//  - Auto-Connect is now a checkbox in AdvancedConfigDialog backed by AppSettings (JSON).
//  - Live COM-port refresh retained; I2C radio buttons and "Apply I2C Settings" removed from main UI.
using System;
using System.Drawing;
using System.IO.Ports;
using System.Threading;
using System.Windows.Forms;
using SPDTool;

namespace UnifiedDDRSPDFlasher
{
    /// <summary>
    /// Flasher Configuration Tab – version 3.1
    /// Main tab: Connection group (port, connect, disconnect, FW info, device name) + Error log.
    /// Advanced dialog: Set Device Name, Factory Reset, Auto-Connect checkbox, I2C speed, Manual Pin Control.
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

            // Main layout: 2 columns – left (error log + connection), right (info / hints)
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(12)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));

            // Left column
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

            // Right column: hints / info panel
            mainLayout.Controls.Add(CreateInfoPanel(), 1, 0);

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
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F)); // connect + advanced
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F)); // disconnect
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10F)); // spacer
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F)); // fw label
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // device name

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
            _toolTip.SetToolTip(_advancedButton, "Device name, factory reset, auto-connect, I2C speed, pin control.");

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

        /// <summary>Right-hand guide panel.</summary>
        private GroupBox CreateInfoPanel()
        {
            var group = new GroupBox
            {
                Text = "Getting Started Guide",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            // Use a RichTextBox so we can apply bold + normal formatting easily
            var rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(240, 240, 240),
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.FromArgb(40, 40, 40),
                ScrollBars = RichTextBoxScrollBars.Vertical
            };

            // Helper to append a bold heading line
            void H(string text)
            {
                rtb.SelectionFont = new Font("Segoe UI", 9.5F, FontStyle.Bold);
                rtb.SelectionColor = Color.FromArgb(0, 78, 152);
                rtb.AppendText(text + "\n");
            }
            // Helper to append a normal body line
            void B(string text)
            {
                rtb.SelectionFont = new Font("Segoe UI", 9F, FontStyle.Regular);
                rtb.SelectionColor = Color.FromArgb(40, 40, 40);
                rtb.AppendText(text + "\n");
            }
            void Sep() { B(""); }

            H("① Connect the Programmer");
            B("Plug the RP2040-based programmer into a USB port.");
            B("Windows will install the CDC serial driver automatically.");
            B("The COM port appears in the dropdown (auto-refreshed).");
            Sep();

            H("② Select COM Port & Connect");
            B("Pick the correct COM port from the dropdown on the left.");
            B("Click Connect. The firmware version and device name");
            B("will appear below the buttons on a successful connection.");
            Sep();

            H("③ SPD Operations Tab");
            B("• Read SPD – reads all bytes from the selected DIMM.");
            B("• Open Dump – loads a .bin file (works without a device).");
            B("• Parsed Fields – module type, capacity, speed, timings,");
            B("  CAS latencies, and manufacturer decoded automatically.");
            B("• CRC status shown after every read or open (✓ OK / ✗ FAIL).");
            B("• Recalc CRC – fixes CRC bytes in the in-memory dump.");
            B("• Write All – writes the loaded dump back to the DIMM.");
            Sep();

            H("④ PMIC Operations Tab");
            B("• Detects PMIC automatically after connection.");
            B("• Read PMIC – reads all 256 registers into the hex viewer.");
            B("• Live measurements update every second (SWA/B/C, VIN, LDO).");
            B("• Burn – writes vendor blocks (0x40-0x6F) or a full dump.");
            B("• Unlock Vendor before burning; the default JEDEC password");
            B("  (0x73 / 0x94) is used unless a custom one is saved.");
            Sep();

            H("⑤ Advanced Settings (click Advanced… when connected)");
            B("• Set a custom device name (stored in programmer EEPROM).");
            B("• Change I2C bus speed: 100 kHz / 400 kHz / 1 MHz.");
            B("  DDR5 works best at 1 MHz. DDR4 is safe at 400 kHz.");
            B("• Auto-Connect: remembers the last successful COM port");
            B("  and reconnects automatically on next application launch.");
            B("• Factory Reset clears all settings from programmer EEPROM.");
            B("• Manual Pin Control lets you toggle individual GPIO lines");
            B("  (HV converter, SA1, VIN_CTRL, PMIC_CTRL, etc.).");
            Sep();

            H("⑥ I2C Speed Reference");
            B("  100 kHz  Standard – use for unknown / older devices.");
            B("  400 kHz  Fast – compatible with all DDR4 SPD EEPROMs.");
            B("  1 MHz    Fast-Plus – required by JEDEC DDR5 spec.");
            Sep();

            H("Troubleshooting");
            B("• No ports in list → check USB cable and driver.");
            B("• Ping fails → device may need a power cycle.");
            B("• SPD read error → try a lower I2C speed.");
            B("• PMIC not detected → check VIN_CTRL pin and power.");

            rtb.SelectionStart = 0;
            group.Controls.Add(rtb);
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

        /// <summary>
        /// Programmatically selects a port in the dropdown (used for auto-connect).
        /// </summary>
        public void SelectPort(string portName)
        {
            if (InvokeRequired) { Invoke(new Action(() => SelectPort(portName))); return; }
            int idx = _portCombo.FindStringExact(portName);
            if (idx >= 0) _portCombo.SelectedIndex = idx;
        }

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
    // Advanced Configuration Dialog – v3.1
    // Contains: Device Name, Factory Reset, Auto-Connect, I2C Speed, Pin Control
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Advanced programmer options:
    /// – Set Device Name  – Factory Reset  – Auto-Connect checkbox  – I2C Speed  – Manual Pin Control
    /// </summary>
    internal sealed class AdvancedConfigDialog : Form
    {
        private readonly SPDToolDevice _device;
        private readonly Func<SPDToolDevice> _provider;
        private readonly EventHandler _disconnectHandler;
        private readonly EventHandler _connectHandler;
        private readonly FlasherConfigTab _parent;

        private TextBox _nameText;
        private ComboBox _i2cSpeedCombo;
        private CheckBox _autoConnectCheck;

        // Pin readout labels (updated by timer)
        private Label[] _pinStateLabels;
        private System.Windows.Forms.Timer _pinRefreshTimer;
        private const int AUTO_CONNECT_DELAY_MS = 500;

        private static readonly string[] I2CSpeedLabels =
        {
            "100 kHz  (Standard – safest)",
            "400 kHz  (Fast)",
            "1 MHz    (Fast-Plus – DDR5 recommended)"
        };

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
            Size = new Size(580, 680);
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
                RowCount = 5,
                ColumnCount = 1,
                Padding = new Padding(14)
            };
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 80F));   // Device Name
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 90F));   // Programmer Options + Auto-Connect
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 80F));   // I2C Speed
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // Pin Control
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));   // Close

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

            // ── Programmer Options (Factory Reset + Auto-Connect) ────────────
            GroupBox miscGroup = new GroupBox
            {
                Text = "Programmer Options",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            var miscLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                Padding = new Padding(2)
            };
            miscLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            miscLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var miscFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };

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

            miscFlow.Controls.Add(factoryBtn);
            miscLayout.Controls.Add(miscFlow, 0, 0);

            // Auto-connect checkbox (backed by AppSettings JSON file)
            _autoConnectCheck = new CheckBox
            {
                Text = "Auto-Connect to last used port on startup",
                Font = new Font("Segoe UI", 9F),
                AutoSize = true,
                Checked = AppSettings.AutoConnect,
                Margin = new Padding(2, 4, 0, 0)
            };
            _autoConnectCheck.CheckedChanged += (s, e) =>
            {
                AppSettings.AutoConnect = _autoConnectCheck.Checked;
                AppSettings.Save();
            };
            miscLayout.Controls.Add(_autoConnectCheck, 0, 1);

            miscGroup.Controls.Add(miscLayout);
            main.Controls.Add(miscGroup, 0, 1);

            // ── I2C Speed ────────────────────────────────────────────────────
            GroupBox i2cGroup = new GroupBox
            {
                Text = "I2C Bus Speed",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            var i2cFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };

            _i2cSpeedCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9F),
                Width = 300,
                Enabled = _device != null
            };
            _i2cSpeedCombo.Items.AddRange(I2CSpeedLabels);

            // Pre-select current device speed if connected
            if (_device != null)
            {
                try
                {
                    byte mode = _device.GetI2CClockMode();
                    _i2cSpeedCombo.SelectedIndex = Math.Min(mode, (byte)2);
                }
                catch { _i2cSpeedCombo.SelectedIndex = 0; }
            }
            else
            {
                _i2cSpeedCombo.SelectedIndex = 0;
            }

            var applyI2cBtn = new Button
            {
                Text = "Apply",
                Width = 80,
                Height = 26,
                Font = new Font("Segoe UI", 8.5F),
                Margin = new Padding(8, 0, 0, 0),
                Enabled = _device != null
            };
            applyI2cBtn.Click += OnApplyI2CClicked;

            i2cFlow.Controls.Add(_i2cSpeedCombo);
            i2cFlow.Controls.Add(applyI2cBtn);
            i2cGroup.Controls.Add(i2cFlow);
            main.Controls.Add(i2cGroup, 0, 2);

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
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54F));
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

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
            main.Controls.Add(pinGroup, 0, 3);

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
            main.Controls.Add(closeFlow, 0, 4);

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

        private void OnApplyI2CClicked(object sender, EventArgs e)
        {
            if (_device == null) return;
            try
            {
                byte mode = (byte)Math.Max(0, Math.Min(2, _i2cSpeedCombo.SelectedIndex));
                if (_device.SetI2CClockMode(mode))
                    MessageBox.Show($"I2C clock set to {I2CSpeedLabels[mode]}.", "Applied",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show($"Failed to set I2C clock.", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting I2C clock:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
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
    }
}