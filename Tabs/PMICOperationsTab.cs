// ADDED: Full implementation of BurnBlock-driven vendor write, Toggle Regulators,
//        Advanced dialog (password change, enable programmable mode, PWR_GOOD control),
//        Unlock/Lock Vendor Region buttons, improved hex display with alternate row colours,
//        and "Read Measurements" button separated from the legacy voltage toggle.
using Newtonsoft.Json;
using SPDTool;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace UnifiedDDRSPDFlasher
{
    /// <summary>
    /// PMIC Operations Tab – version 3.0
    /// Implements: full vendor-block MTP burning, advanced PMIC dialog,
    /// VR toggle, unlock/lock, improved hex display.
    /// </summary>
    public class PMICOperationsTab : UserControl
    {
        #region Constants

        private const int PMIC_STABILIZE_DELAY_MS = 300;
        private const int PMIC_PROBE_RETRIES = 3;
        private const int PMIC_READ_CHUNK_SIZE = 64;
        private const int PMIC_RETRY_DELAY_MS = 100;

        // Tooltip text for each button/control
        private const string TT_OPEN_DUMP = "Load a 256-byte PMIC register dump from a binary file.";
        private const string TT_SAVE_DUMP = "Save the currently displayed PMIC register dump to a binary file.";
        private const string TT_READ_REG = "Read a single PMIC register by address (hex 00–FF).";
        private const string TT_WRITE_REG = "Write a single byte to a PMIC register.";
        private const string TT_READ_MEAS = "Read all ADC measurements (voltages, currents) from the PMIC.";
        private const string TT_TOGGLE_VR = "Toggle the switching regulators ON or OFF via register 0x32 or the PMIC_CTRL pin.";
        private const string TT_REBOOT = "Power-cycle the DIMM by toggling the DDR5_VIN_CTRL pin.";
        private const string TT_ADVANCED = "Open advanced PMIC options: password change, programmable mode, PWR_GOOD control.";
        private const string TT_READ_PMIC = "Read all 256 registers from the selected PMIC into the hex viewer.";
        private const string TT_BURN_VENDOR = "Burn all three MTP vendor blocks (0x40–0x6F) from the loaded dump.";
        private const string TT_BURN_40 = "Burn MTP block 0 (registers 0x40–0x4F) from the loaded dump.";
        private const string TT_BURN_50 = "Burn MTP block 1 (registers 0x50–0x5F) from the loaded dump.";
        private const string TT_BURN_60 = "Burn MTP block 2 (registers 0x60–0x6F) from the loaded dump.";
        private const string TT_UNLOCK = "Unlock the PMIC vendor region (write password + 0x40 to reg 0x39).";
        private const string TT_LOCK = "Lock the PMIC vendor region (write 0x00 to reg 0x39).";

        #endregion

        #region Events

        public event EventHandler<string> ErrorOccurred;

        #endregion

        #region Private Fields

        private Func<SPDToolDevice> _deviceProvider;
        private SPDToolDevice Device => _deviceProvider?.Invoke();

        // Left side components
        private RichTextBox _hexViewer;
        private RichTextBox _responseLog;
        private Button _openDumpButton;
        private Button _saveDumpButton;
        private TextBox _regAddressText;
        private TextBox _regValueText;
        private Button _readRegButton;
        private Button _writeRegButton;
        private Button _readMeasButton;   // ADDED: dedicated measurement button
        private Button _toggleVregButton;
        private Button _rebootDimmButton;
        private Button _advancedButton;

        // Right side components
        private ComboBox _pmicAddressCombo;
        private Label _pmicModelLabel;
        private Label _pmicModeLabel;
        private Label _outputStatusLabel;
        private Label _powerGoodLabel;
        private Button _readButton;
        private Button _burnVendorButton;
        private Button _burn40Button;
        private Button _burn50Button;
        private Button _burn60Button;
        private Button _unlockButton;   // ADDED
        private Button _lockButton;     // ADDED

        // Data
        private byte[] _currentDump;
        private byte _currentPMICAddress = 0x48;
        private List<byte> _detectedPMICAddresses = new List<byte>();

        private System.Windows.Forms.Timer _pmicStabilizeTimer;
        private ToolTip _toolTip;

        #endregion

        #region Constructor

        public PMICOperationsTab()
        {
            _toolTip = new ToolTip { AutoPopDelay = 6000, InitialDelay = 400, ReshowDelay = 200 };
            InitializeComponent();
            UpdateUIState(false);

            _pmicStabilizeTimer = new System.Windows.Forms.Timer();
            _pmicStabilizeTimer.Interval = PMIC_STABILIZE_DELAY_MS;
            _pmicStabilizeTimer.Tick += OnPmicStabilizeTimerTick;
        }

        #endregion

        #region Initialization

        private void InitializeComponent()
        {
            this.Dock = DockStyle.Fill;
            this.BackColor = Color.FromArgb(240, 240, 240);

            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(10)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            mainLayout.Controls.Add(CreateLeftPanel(), 0, 0);
            mainLayout.Controls.Add(CreateRightPanel(), 1, 0);

            this.Controls.Add(mainLayout);
        }

        private Panel CreateLeftPanel()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 15F));

            layout.Controls.Add(CreateHexEditorSection(), 0, 0);
            layout.Controls.Add(CreateResponseLogSection(), 0, 1);
            layout.Controls.Add(CreateBottomSection(), 0, 2);

            panel.Controls.Add(layout);
            return panel;
        }

        private Panel CreateHexEditorSection()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3) };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // Toolbar
            FlowLayoutPanel buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 2, 0, 2),
                WrapContents = false
            };

            _openDumpButton = MakeButton("Open Dump", 140, false);
            _openDumpButton.Click += OnOpenDumpClicked;
            _toolTip.SetToolTip(_openDumpButton, TT_OPEN_DUMP);

            _saveDumpButton = MakeButton("Save Dump", 140, false);
            _saveDumpButton.Click += OnSaveDumpClicked;
            _toolTip.SetToolTip(_saveDumpButton, TT_SAVE_DUMP);

            buttonPanel.Controls.Add(_openDumpButton);
            buttonPanel.Controls.Add(_saveDumpButton);
            layout.Controls.Add(buttonPanel, 0, 0);

            // Hex viewer – monospaced, alternating row colours drawn in paint event
            _hexViewer = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 14F),
                ReadOnly = true,
                BackColor = Color.White,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                BorderStyle = BorderStyle.FixedSingle
            };
            layout.Controls.Add(_hexViewer, 0, 1);

            panel.Controls.Add(layout);
            return panel;
        }

        private Panel CreateResponseLogSection()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3) };

            Label lbl = new Label
            {
                Text = "Response Log",
                Dock = DockStyle.Top,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Height = 18,
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            _responseLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas",11F),
                ReadOnly = true,
                BackColor = Color.FromArgb(248, 248, 248),
                BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            LogResponse("PMIC response log initialised. Operations will appear here.");

            panel.Controls.Add(_responseLog);
            panel.Controls.Add(lbl);
            return panel;
        }

        private Panel CreateBottomSection()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0) };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));

            // ── Single-register R/W ──────────────────────────────────────────────
            GroupBox singleRegGroup = new GroupBox
            {
                Text = "Read / Write Register",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                Padding = new Padding(6),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            TableLayoutPanel regLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 2,
                Padding = new Padding(3)
            };
            regLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            regLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            regLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            regLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            FlowLayoutPanel addrPanel = BuildInlineTextBox("Reg: 0x", out _regAddressText, 38);
            FlowLayoutPanel valPanel = BuildInlineTextBox("Val: 0x", out _regValueText, 38);

            _readRegButton = MakeButton("Read", -1, false);
            _readRegButton.Dock = DockStyle.Fill;
            _readRegButton.Margin = new Padding(1);
            _readRegButton.Click += OnReadRegClicked;
            _toolTip.SetToolTip(_readRegButton, TT_READ_REG);

            _writeRegButton = MakeButton("Write", -1, false);
            _writeRegButton.Dock = DockStyle.Fill;
            _writeRegButton.Margin = new Padding(1);
            _writeRegButton.Click += OnWriteRegClicked;
            _toolTip.SetToolTip(_writeRegButton, TT_WRITE_REG);

            regLayout.Controls.Add(addrPanel, 0, 0);
            regLayout.Controls.Add(valPanel, 1, 0);
            regLayout.Controls.Add(_readRegButton, 0, 1);
            regLayout.Controls.Add(_writeRegButton, 1, 1);

            singleRegGroup.Controls.Add(regLayout);
            layout.Controls.Add(singleRegGroup, 0, 0);

            // ── Action buttons ───────────────────────────────────────────────────
            TableLayoutPanel actionLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 5,
                Padding = new Padding(3)
            };
            for (int i = 0; i < 5; i++)
                actionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));

            _readMeasButton = MakeButton("Read Measurements", -1, false);
            _readMeasButton.Dock = DockStyle.Fill;
            _readMeasButton.Margin = new Padding(1);
            _readMeasButton.Font = new Font("Segoe UI", 8F);
            _readMeasButton.Click += OnReadMeasClicked;
            _toolTip.SetToolTip(_readMeasButton, TT_READ_MEAS);

            _toggleVregButton = MakeButton("Toggle Regulators", -1, false);
            _toggleVregButton.Dock = DockStyle.Fill;
            _toggleVregButton.Margin = new Padding(1);
            _toggleVregButton.Font = new Font("Segoe UI", 8F);
            _toggleVregButton.Click += OnToggleVregClicked;
            _toolTip.SetToolTip(_toggleVregButton, TT_TOGGLE_VR);

            _rebootDimmButton = MakeButton("Reboot DIMM", -1, false);
            _rebootDimmButton.Dock = DockStyle.Fill;
            _rebootDimmButton.Margin = new Padding(1);
            _rebootDimmButton.Font = new Font("Segoe UI", 8F);
            _rebootDimmButton.Click += OnRebootDimmClicked;
            _toolTip.SetToolTip(_rebootDimmButton, TT_REBOOT);

            _unlockButton = MakeButton("Unlock Vendor", -1, false);
            _unlockButton.Dock = DockStyle.Fill;
            _unlockButton.Margin = new Padding(1);
            _unlockButton.Font = new Font("Segoe UI", 8F);
            _unlockButton.Click += OnUnlockVendorClicked;
            _toolTip.SetToolTip(_unlockButton, TT_UNLOCK);

            _advancedButton = MakeButton("Advanced…", -1, false);
            _advancedButton.Dock = DockStyle.Fill;
            _advancedButton.Margin = new Padding(1);
            _advancedButton.Font = new Font("Segoe UI", 8F);
            _advancedButton.Click += OnAdvancedClicked;
            _toolTip.SetToolTip(_advancedButton, TT_ADVANCED);

            actionLayout.Controls.Add(_readMeasButton, 0, 0);
            actionLayout.Controls.Add(_toggleVregButton, 0, 1);
            actionLayout.Controls.Add(_rebootDimmButton, 0, 2);
            actionLayout.Controls.Add(_unlockButton, 0, 3);
            actionLayout.Controls.Add(_advancedButton, 0, 4);

            layout.Controls.Add(actionLayout, 1, 0);

            panel.Controls.Add(layout);
            return panel;
        }

        private Panel CreateRightPanel()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                Padding = new Padding(3)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));

            // PMIC address selector
            Panel addressPanel = new Panel { Dock = DockStyle.Fill };
            Label addressLabel = new Label
            {
                Text = "PMIC I2C Address:",
                Dock = DockStyle.Top,
                Height = 20,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.BottomLeft
            };
            _pmicAddressCombo = new ComboBox
            {
                Dock = DockStyle.Top,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Consolas", 9.5F),
                Height = 28
            };
            _pmicAddressCombo.SelectedIndexChanged += OnPMICAddressChanged;
            addressPanel.Controls.Add(_pmicAddressCombo);
            addressPanel.Controls.Add(addressLabel);
            layout.Controls.Add(addressPanel, 0, 0);

            // PMIC Info
            layout.Controls.Add(CreatePMICInfoGroup(), 0, 1);

            // PMIC Data
            layout.Controls.Add(CreatePMICDataGroup(), 0, 2);

            // PMIC Block Data
            layout.Controls.Add(CreateBlockDataGroup(), 0, 3);

            panel.Controls.Add(layout);
            return panel;
        }

        private GroupBox CreatePMICInfoGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "PMIC Info",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            TableLayoutPanel infoLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                Padding = new Padding(4)
            };
            for (int i = 0; i < 4; i++)
                infoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));

            _pmicModelLabel = MakeInfoLabel("PMIC Model: —");
            _pmicModeLabel = MakeInfoLabel("PMIC Mode: —");
            _outputStatusLabel = MakeInfoLabel("Output Status: —");
            _powerGoodLabel = MakeInfoLabel("Power Good: —");

            infoLayout.Controls.Add(_pmicModelLabel, 0, 0);
            infoLayout.Controls.Add(_pmicModeLabel, 0, 1);
            infoLayout.Controls.Add(_outputStatusLabel, 0, 2);
            infoLayout.Controls.Add(_powerGoodLabel, 0, 3);

            group.Controls.Add(infoLayout);
            return group;
        }

        private GroupBox CreatePMICDataGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "PMIC Data",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            TableLayoutPanel dataLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                Padding = new Padding(4)
            };
            dataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            dataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            _readButton = new Button
            {
                Text = "Read PMIC",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Height = 34,
                Margin = new Padding(3),
                Enabled = false
            };
            _readButton.FlatAppearance.BorderSize = 0;
            _readButton.Click += OnReadClicked;
            _toolTip.SetToolTip(_readButton, TT_READ_PMIC);

            _burnVendorButton = new Button
            {
                Text = "Burn Vendor Blocks (0x40–0x6F)",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(192, 0, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Height = 34,
                Margin = new Padding(3),
                Enabled = false
            };
            _burnVendorButton.FlatAppearance.BorderSize = 0;
            _burnVendorButton.Click += OnBurnVendorClicked;
            _toolTip.SetToolTip(_burnVendorButton, TT_BURN_VENDOR);

            dataLayout.Controls.Add(_readButton, 0, 0);
            dataLayout.Controls.Add(_burnVendorButton, 0, 1);

            group.Controls.Add(dataLayout);
            return group;
        }

        private GroupBox CreateBlockDataGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "Single Block Burn",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            TableLayoutPanel blockLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                Padding = new Padding(4)
            };
            for (int i = 0; i < 4; i++)
                blockLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));

            _burn40Button = MakeButton("Burn block 0 (0x40–0x4F)", -1, false);
            _burn40Button.Dock = DockStyle.Fill;
            _burn40Button.Margin = new Padding(2);
            _burn40Button.Click += (s, e) => OnBurnBlockClicked(0x40, 0x4F);
            _toolTip.SetToolTip(_burn40Button, TT_BURN_40);

            _burn50Button = MakeButton("Burn block 1 (0x50–0x5F)", -1, false);
            _burn50Button.Dock = DockStyle.Fill;
            _burn50Button.Margin = new Padding(2);
            _burn50Button.Click += (s, e) => OnBurnBlockClicked(0x50, 0x5F);
            _toolTip.SetToolTip(_burn50Button, TT_BURN_50);

            _burn60Button = MakeButton("Burn block 2 (0x60–0x6F)", -1, false);
            _burn60Button.Dock = DockStyle.Fill;
            _burn60Button.Margin = new Padding(2);
            _burn60Button.Click += (s, e) => OnBurnBlockClicked(0x60, 0x6F);
            _toolTip.SetToolTip(_burn60Button, TT_BURN_60);

            _lockButton = MakeButton("Lock Vendor Region", -1, false);
            _lockButton.Dock = DockStyle.Fill;
            _lockButton.Margin = new Padding(2);
            _lockButton.Click += OnLockVendorClicked;
            _toolTip.SetToolTip(_lockButton, TT_LOCK);

            blockLayout.Controls.Add(_burn40Button, 0, 0);
            blockLayout.Controls.Add(_burn50Button, 0, 1);
            blockLayout.Controls.Add(_burn60Button, 0, 2);
            blockLayout.Controls.Add(_lockButton, 0, 3);

            group.Controls.Add(blockLayout);
            return group;
        }

        #endregion

        #region Public Methods

        public void LogResponse(string message, string level = "Info")
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => LogResponse(message, level)));
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _responseLog.SelectionColor = level switch
            {
                "Err" => Color.DarkRed,
                "Warn" => Color.OrangeRed,
                _ => Color.Black
            };
            // Light-red background for errors
            if (level == "Err")
                _responseLog.SelectionBackColor = Color.FromArgb(255, 240, 240);
            else
                _responseLog.SelectionBackColor = _responseLog.BackColor;

            _responseLog.AppendText($"[{timestamp}] {message}\n");
            _responseLog.ScrollToCaret();
        }

        public void SetDeviceProvider(Func<SPDToolDevice> deviceProvider)
        {
            _deviceProvider = deviceProvider;
        }

        public void OnDeviceConnected(SPDToolDevice device)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnDeviceConnected(device)));
                return;
            }
            UpdateUIState(true);
            LogResponse("Device connected. Scanning for PMIC…");
            AutoDetectPMIC();
        }

        public void OnDeviceDisconnected()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnDeviceDisconnected()));
                return;
            }

            _pmicStabilizeTimer.Stop();
            _pmicAddressCombo.Items.Clear();
            ResetInfoLabels();
            _detectedPMICAddresses.Clear();
            _hexViewer.Clear();
            LogResponse("Device disconnected.");
            UpdateUIState(false);
        }

        public void UpdateDetectedDevices(List<byte> devices)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => UpdateDetectedDevices(devices)));
                return;
            }

            var pmicDevices = devices.Where(a => a >= 0x48 && a <= 0x4F).ToList();
            bool needsUpdate = false;
            bool newPmicDetected = false;

            foreach (var addr in pmicDevices)
            {
                if (!_detectedPMICAddresses.Contains(addr))
                {
                    _detectedPMICAddresses.Add(addr);
                    needsUpdate = true;
                    newPmicDetected = true;
                    LogResponse($"✓ PMIC detected at 0x{addr:X2}");
                }
            }

            for (int i = _detectedPMICAddresses.Count - 1; i >= 0; i--)
            {
                if (!pmicDevices.Contains(_detectedPMICAddresses[i]))
                {
                    var removed = _detectedPMICAddresses[i];
                    _detectedPMICAddresses.RemoveAt(i);
                    needsUpdate = true;
                    LogResponse($"✗ PMIC at 0x{removed:X2} removed");
                }
            }

            if (needsUpdate)
            {
                UpdatePMICAddressList();

                if (_detectedPMICAddresses.Count > 0 && !_detectedPMICAddresses.Contains(_currentPMICAddress))
                {
                    _currentPMICAddress = _detectedPMICAddresses[0];
                    _pmicAddressCombo.SelectedIndex = 0;
                    _pmicStabilizeTimer.Stop();
                    _pmicStabilizeTimer.Start();
                }
                else if (_detectedPMICAddresses.Count == 0)
                {
                    _currentPMICAddress = 0;
                    OnDeviceDisconnected();
                }
                else if (newPmicDetected)
                {
                    _pmicStabilizeTimer.Stop();
                    _pmicStabilizeTimer.Start();
                }

                UpdateUIState(Device != null);
            }
        }

        #endregion

        #region PMIC Detection

        private void OnPmicStabilizeTimerTick(object sender, EventArgs e)
        {
            _pmicStabilizeTimer.Stop();
            if (_detectedPMICAddresses.Count > 0 && _currentPMICAddress != 0)
                DetectPMICInfo();
        }

        private void AutoDetectPMIC()
        {
            if (Device == null) return;

            try
            {
                Cursor = Cursors.WaitCursor;
                _detectedPMICAddresses.Clear();
                _pmicAddressCombo.Items.Clear();

                LogResponse("Scanning for PMIC devices (0x48–0x4F)…");

                for (int attempt = 1; attempt <= PMIC_PROBE_RETRIES; attempt++)
                {
                    if (attempt > 1)
                    {
                        LogResponse($"  Retry {attempt}…");
                        Thread.Sleep(PMIC_RETRY_DELAY_MS * 3);
                    }

                    for (byte addr = 0x48; addr <= 0x4F; addr++)
                    {
                        if (_detectedPMICAddresses.Contains(addr)) continue;
                        try
                        {
                            if (Device.ProbeAddress(addr))
                            {
                                _detectedPMICAddresses.Add(addr);
                                LogResponse($"✓ Found PMIC at 0x{addr:X2}");
                            }
                        }
                        catch (Exception ex)
                        {
                            if (attempt == 1)
                                LogResponse($"✗ Error probing 0x{addr:X2}: {ex.Message}", "Warn");
                        }
                    }

                    if (_detectedPMICAddresses.Count > 0) break;
                }

                if (_detectedPMICAddresses.Count == 0)
                {
                    _pmicAddressCombo.Items.Add("No PMIC detected");
                    _pmicAddressCombo.SelectedIndex = 0;
                    _pmicAddressCombo.Enabled = false;
                    LogResponse("No PMIC devices found.");
                    UpdateUIState(true);
                    return;
                }

                _pmicAddressCombo.Enabled = true;
                UpdatePMICAddressList();
                LogResponse($"Found {_detectedPMICAddresses.Count} PMIC device(s).");
                _currentPMICAddress = _detectedPMICAddresses[0];
                UpdateUIState(true);
                _pmicStabilizeTimer.Stop();
                _pmicStabilizeTimer.Start();
            }
            catch (Exception ex)
            {
                LogResponse($"✗ PMIC auto-detect failed: {ex.Message}", "Err");
                ErrorOccurred?.Invoke(this, $"PMIC auto-detect failed: {ex.Message}");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void UpdatePMICAddressList()
        {
            _pmicAddressCombo.Items.Clear();
            foreach (byte addr in _detectedPMICAddresses)
                _pmicAddressCombo.Items.Add($"0x{addr:X2}");

            if (_detectedPMICAddresses.Count > 0)
                _pmicAddressCombo.SelectedIndex = 0;
        }

        private void DetectPMICInfo()
        {
            if (Device == null) return;

            for (int attempt = 0; attempt < PMIC_PROBE_RETRIES; attempt++)
            {
                try
                {
                    string model = Device.GetPMICType(_currentPMICAddress);
                    string mode = Device.GetPMICMode(_currentPMICAddress);
                    string pgood = Device.GetPMICPGoodStatus(_currentPMICAddress);

                    // Build output-status string from measurements
                    string outputStatus = BuildOutputStatus();

                    _pmicModelLabel.Text = $"PMIC Model: {model}";
                    _pmicModeLabel.Text = $"PMIC Mode:  {mode}";
                    _outputStatusLabel.Text = $"Output: {outputStatus}";
                    _powerGoodLabel.Text = $"PWR_GOOD: {pgood}";

                    // Colour-code the power-good label
                    _powerGoodLabel.ForeColor = pgood.Contains("All rails good")
                        ? Color.DarkGreen : Color.DarkRed;

                    LogResponse($"PMIC 0x{_currentPMICAddress:X2} – {model} ({mode})");
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == PMIC_PROBE_RETRIES - 1)
                    {
                        _pmicModelLabel.Text = "PMIC Model: Read error";
                        _pmicModeLabel.Text = "PMIC Mode: Read error";
                        ErrorOccurred?.Invoke(this, $"Failed to read PMIC info: {ex.Message}");
                    }
                    else
                    {
                        Thread.Sleep(PMIC_RETRY_DELAY_MS);
                    }
                }
            }
        }

        /// <summary>Summarises switching-regulator status from register 0x32 for the info panel.</summary>
        private string BuildOutputStatus()
        {
            try
            {
                var r32 = Device.ReadPMICDevice(_currentPMICAddress, 0x32);
                if (r32 == null || r32.Length == 0) return "Unknown";

                bool vrEnabled = (r32[0] & 0x80) != 0; // bit 7
                return vrEnabled ? "Regulators ON" : "Regulators OFF";
            }
            catch { return "Unknown"; }
        }

        #endregion

        #region Event Handlers

        private void OnPMICAddressChanged(object sender, EventArgs e)
        {
            if (_pmicAddressCombo.SelectedIndex < 0 || _detectedPMICAddresses.Count == 0)
                return;
            if (_pmicAddressCombo.SelectedIndex >= _detectedPMICAddresses.Count)
                return;

            _currentPMICAddress = _detectedPMICAddresses[_pmicAddressCombo.SelectedIndex];
            LogResponse($"Selected PMIC: 0x{_currentPMICAddress:X2}");
            _pmicStabilizeTimer.Stop();
            _pmicStabilizeTimer.Start();
        }

        private void OnReadClicked(object sender, EventArgs e)
        {
            if (Device == null || _detectedPMICAddresses.Count == 0)
            {
                LogResponse("✗ No device or PMIC selected", "Err");
                return;
            }

            try
            {
                DetectPMICInfo();
                Cursor = Cursors.WaitCursor;
                LogResponse($"Reading PMIC 0x{_currentPMICAddress:X2}…");

                byte[] pmicData = ReadEntirePMIC();

                if (pmicData != null && pmicData.Length > 0)
                {
                    _currentDump = pmicData;
                    DisplayHexDump(_currentDump);
                    _saveDumpButton.Enabled = true;
                    LogResponse($"✓ Read {pmicData.Length} bytes from PMIC 0x{_currentPMICAddress:X2}");
                }
                else
                {
                    LogResponse("✗ Failed to read PMIC data", "Err");
                }
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Error reading PMIC: {ex.Message}", "Err");
                ErrorOccurred?.Invoke(this, $"PMIC read failed: {ex.Message}");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private byte[] ReadEntirePMIC()
        {
            if (Device == null) return null;
            var allData = new List<byte>(256);

            for (ushort offset = 0; offset < 256; offset += PMIC_READ_CHUNK_SIZE)
            {
                byte chunkSize = (byte)Math.Min(PMIC_READ_CHUNK_SIZE, 256 - offset);
                try
                {
                    byte[] chunk = Device.ReadI2CDevice(_currentPMICAddress, offset, chunkSize);
                    if (chunk == null || chunk.Length != chunkSize)
                    {
                        LogResponse($"  ✗ Chunk read failed at 0x{offset:X2} – padding with 0xFF", "Warn");
                        for (int i = 0; i < chunkSize; i++) allData.Add(0xFF);
                    }
                    else
                    {
                        allData.AddRange(chunk);
                        LogResponse($"  ✓ Read 0x{offset:X2}–0x{offset + chunkSize - 1:X2}");
                    }
                    Thread.Sleep(10);
                }
                catch (Exception ex)
                {
                    LogResponse($"  ✗ Error at 0x{offset:X2}: {ex.Message}", "Err");
                    int remaining = 256 - allData.Count;
                    for (int i = 0; i < remaining; i++) allData.Add(0xFF);
                    break;
                }
            }

            return allData.Count > 0 ? allData.ToArray() : null;
        }

        private void OnReadRegClicked(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_regAddressText.Text))
            {
                LogResponse("✗ Enter a register address (hex)", "Warn");
                return;
            }
            try
            {
                byte regAddress = Convert.ToByte(_regAddressText.Text, 16);
                Cursor = Cursors.WaitCursor;
                byte[] data = Device.ReadPMICDevice(_currentPMICAddress, regAddress);

                if (data != null && data.Length > 0)
                    LogResponse($"Reg 0x{regAddress:X2} = 0x{data[0]:X2}  ({data[0]:D3} dec, {Convert.ToString(data[0], 2).PadLeft(8, '0')} bin)");
                else
                    LogResponse($"✗ Failed to read register 0x{regAddress:X2}", "Err");
            }
            catch (FormatException)
            {
                LogResponse("✗ Invalid address format – enter 00–FF", "Warn");
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Error: {ex.Message}", "Err");
                ErrorOccurred?.Invoke(this, $"Register read failed: {ex.Message}");
            }
            finally { Cursor = Cursors.Default; }
        }

        private void OnWriteRegClicked(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_regAddressText.Text) || string.IsNullOrEmpty(_regValueText.Text))
            {
                LogResponse("✗ Enter both register address and value", "Warn");
                return;
            }
            try
            {
                byte regAddress = Convert.ToByte(_regAddressText.Text, 16);
                byte regValue = Convert.ToByte(_regValueText.Text, 16);

                Cursor = Cursors.WaitCursor;
                bool ok = Device.WriteI2CDevice(_currentPMICAddress, regAddress, regValue);

                // Read back to confirm
                Thread.Sleep(200); // some delay to make sure the device can keep up
                byte[] verify = Device.ReadPMICDevice(_currentPMICAddress, regAddress);
                string readBack = (verify != null && verify.Length > 0)
                    ? $"(read-back: 0x{verify[0]:X2})" : "(read-back failed)";

                if (ok)
                    LogResponse($"✓ Wrote 0x{regValue:X2} → reg 0x{regAddress:X2} {readBack}");
                else
                    LogResponse($"✗ Write failed for reg 0x{regAddress:X2}", "Err");
            }
            catch (FormatException)
            {
                LogResponse("✗ Invalid hex format – use 00–FF", "Warn");
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Error: {ex.Message}", "Err");
                ErrorOccurred?.Invoke(this, $"Register write failed: {ex.Message}");
            }
            finally { Cursor = Cursors.Default; }
        }

        // ADDED: Dedicated measurement reader (does not toggle VR)
        private void OnReadMeasClicked(object sender, EventArgs e)
        {
            if (Device == null || _currentPMICAddress == 0)
            {
                LogResponse("✗ No PMIC selected", "Err");
                return;
            }
            try
            {
                if(Device.GetPMICMode(_currentPMICAddress) == "Locked" && Device.ReadPMICDevice(_currentPMICAddress, 0x32)[0] == 0x00)
                {
                    LogResponse("❕ Please enable the regulators first or unlock the device! ", "Warn");
                    return;
                }
                Cursor = Cursors.WaitCursor;
                var measurements = Device.ReadAllMeasurements(_currentPMICAddress);
                if (measurements == null)
                {
                    LogResponse("✗ Failed to read measurements", "Err");
                    return;
                }

                LogResponse($"── Measurements for PMIC 0x{_currentPMICAddress:X2} ({measurements.DeviceType}) ──");

                foreach (var kv in measurements.Voltages_mV)
                    LogResponse($"  {kv.Key,12}: {kv.Value / 1000.0:F3} V");

                foreach (var kv in measurements.Currents_mA)
                    LogResponse($"  {kv.Key,12}: {kv.Value:F1} mA");

                foreach (var kv in measurements.Powers_mW)
                    LogResponse($"  {kv.Key,12}: {kv.Value:F1} mW");

                if (!double.IsNaN(measurements.TotalPower_mW))
                    LogResponse($"  {"Total Power",12}: {measurements.TotalPower_mW:F1} mW");

                LogResponse("────────────────────────────────");
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Error reading measurements: {ex.Message}", "Err");
            }
            finally { Cursor = Cursors.Default; }
        }

        // ADDED: Toggle regulators without reading voltages
        private void OnToggleVregClicked(object sender, EventArgs e)
        {
            if (Device == null || _currentPMICAddress == 0) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                var r32 = Device.ReadPMICDevice(_currentPMICAddress, 0x32);
                if (r32 == null || r32.Length == 0)
                {
                    LogResponse("✗ Could not read register 0x32", "Err");
                    return;
                }

                bool vrCurrentlyOn = (r32[0] & 0x80) != 0;

                // Check mode – if Locked, use the PMIC_CTRL pin instead
                string mode = Device.GetPMICMode(_currentPMICAddress);

                if (mode == "Locked")
                {
                    // Toggle PMIC_CTRL pin
                    bool pinState = Device.GetPin(SPDToolDevice.PIN_PMIC_CTRL) == 1;
                    Device.SetPin(SPDToolDevice.PIN_PMIC_CTRL,
                        pinState ? (byte)0x00 : (byte)0x01);
                    LogResponse($"PMIC is locked – toggled PMIC_CTRL pin {(pinState ? "LOW→HIGH" : "HIGH→LOW")}");
                }
                else
                {
                    // Programmable / Manufacturer mode: write 0x32
                    byte newVal = vrCurrentlyOn ? (byte)0x00 : (byte)0x80;
                    Device.WriteI2CDevice(_currentPMICAddress, 0x32, newVal);
                    LogResponse($"Switching regulators {(vrCurrentlyOn ? "disabled (0x00 → reg 0x32)" : "enabled (0x80 → reg 0x32)")}");
                }

                Thread.Sleep(150);
                DetectPMICInfo();
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Error toggling VR: {ex.Message}", "Err");
            }
            finally { Cursor = Cursors.Default; }
        }

        private void OnRebootDimmClicked(object sender, EventArgs e)
        {
            LogResponse("Rebooting DIMM…");
            if (Device.RebootDIMM())
            {
                DetectPMICInfo();
                LogResponse("✓ DIMM reboot command sent");
            }
            else
            {
                LogResponse("✗ Failed to send reboot command", "Err");
                ErrorOccurred?.Invoke(this, "Failed to reboot DIMM.");
            }
        }

        // ADDED: Unlock vendor region button
        private void OnUnlockVendorClicked(object sender, EventArgs e)
        {
            if (Device == null || _currentPMICAddress == 0) return;

            // Ask which password to use
            var dlg = MessageBox.Show(
                "Use the default JEDEC password (0x73 / 0x94)?\n\nClick 'Yes' for default, 'No' to enter a custom password.",
                "Unlock Vendor Region", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (dlg == DialogResult.Cancel) return;

            byte lsb = 0x73, msb = 0x94;

            if (dlg == DialogResult.No)
            {
                // Try to load saved password
                var saved = LoadSavedPassword();
                if (saved.HasValue)
                {
                    lsb = saved.Value.lsb;
                    msb = saved.Value.msb;
                    LogResponse("Using saved custom password.");
                }
                else
                {
                    LogResponse("No saved password found. Using default.", "Warn");
                }
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                bool ok = Device.UnlockVendorRegion(_currentPMICAddress, lsb, msb);
                LogResponse(ok
                    ? "✓ Vendor region unlocked successfully"
                    : "✗ Failed to unlock vendor region (wrong password?)", ok ? "Info" : "Err");
                DetectPMICInfo();
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Unlock error: {ex.Message}", "Err");
            }
            finally { Cursor = Cursors.Default; }
        }

        // ADDED: Lock vendor region button
        private void OnLockVendorClicked(object sender, EventArgs e)
        {
            if (Device == null || _currentPMICAddress == 0) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                bool ok = Device.LockVendorRegion(_currentPMICAddress);
                LogResponse(ok ? "✓ Vendor region locked" : "✗ Lock command failed", ok ? "Info" : "Err");
                DetectPMICInfo();
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Lock error: {ex.Message}", "Err");
            }
            finally { Cursor = Cursors.Default; }
        }

        // ADDED: Advanced dialog
        private void OnAdvancedClicked(object sender, EventArgs e)
        {
            if (Device == null || _currentPMICAddress == 0) return;
            using var dlg = new AdvancedPMICDialog(Device, _currentPMICAddress);
            dlg.ShowDialog(this);
            DetectPMICInfo(); // refresh info after dialog closes
        }

        // ADDED: Burn all vendor blocks (0x40–0x6F)
        private void OnBurnVendorClicked(object sender, EventArgs e)
        {
            if (_currentDump == null || _currentDump.Length < 256)
            {
                LogResponse("✗ Load a valid 256-byte PMIC dump first (use 'Open Dump' or 'Read PMIC')", "Warn");
                return;
            }

            var confirm = MessageBox.Show(
                $"⚠  This will program the MTP vendor region (0x40–0x6F) on PMIC 0x{_currentPMICAddress:X2}.\n\n" +
                "MTP registers support multiple program cycles, but incorrect data can render the PMIC non-functional.\n\n" +
                "The operation will take approximately 700 ms.\n\nContinue?",
                "Confirm Vendor Burn", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes) return;

            try
            {
                Cursor = Cursors.WaitCursor;
                LogResponse("Starting vendor block burn…");

                int errors = 0;
                for (int blk = 0; blk < 3; blk++)
                {
                    byte startReg = (byte)(0x40 + blk * 16);
                    LogResponse($"  Burning block {blk} (0x{startReg:X2}–0x{startReg + 15:X2})…");

                    byte[] chunk = new byte[16];
                    Array.Copy(_currentDump, startReg, chunk, 0, 16);

                    bool ok = Device.BurnBlock(_currentPMICAddress, blk, chunk);
                    LogResponse(ok ? $"  ✓ Block {blk} burned" : $"  ✗ Block {blk} failed", ok ? "Info" : "Err");
                    if (!ok) errors++;

                    // Verify readback
                    if (ok) VerifyBlock(blk, chunk);
                }

                if (errors == 0)
                    LogResponse("✓ All vendor blocks burned successfully");
                else
                    LogResponse($"⚠  Burn completed with {errors} block(s) failed", "Warn");
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Burn error: {ex.Message}", "Err");
                ErrorOccurred?.Invoke(this, $"Vendor burn failed: {ex.Message}");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        // ADDED: Burn single block
        private void OnBurnBlockClicked(byte startReg, byte endReg)
        {
            if (_currentDump == null || _currentDump.Length < 256)
            {
                LogResponse("✗ Load a valid 256-byte PMIC dump first", "Warn");
                return;
            }

            int blockIndex = (startReg - 0x40) / 16; // 0x40→0, 0x50→1, 0x60→2

            var confirm = MessageBox.Show(
                $"⚠  Burn MTP block {blockIndex} (0x{startReg:X2}–0x{endReg:X2}) on PMIC 0x{_currentPMICAddress:X2}?\n\n" +
                "Continue?",
                "Confirm Block Burn", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes) return;

            try
            {
                Cursor = Cursors.WaitCursor;
                byte[] chunk = new byte[16];
                Array.Copy(_currentDump, startReg, chunk, 0, 16);

                LogResponse($"Burning block {blockIndex} (0x{startReg:X2}–0x{endReg:X2})…");
                bool ok = Device.BurnBlock(_currentPMICAddress, blockIndex, chunk);
                LogResponse(ok
                    ? $"✓ Block {blockIndex} burned successfully"
                    : $"✗ Block {blockIndex} burn failed", ok ? "Info" : "Err");

                if (ok) VerifyBlock(blockIndex, chunk);
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Burn error: {ex.Message}", "Err");
                ErrorOccurred?.Invoke(this, $"Block burn failed: {ex.Message}");
            }
            finally { Cursor = Cursors.Default; }
        }

        /// <summary>
        /// Reads back a vendor block after burn and logs pass/fail.
        /// Temporarily unlocks the vendor region to perform the read,
        /// then re‑locks it to leave the PMIC in a secure state.
        /// </summary>
        private void VerifyBlock(int blockIndex, byte[] expected)
        {
            if (Device == null || _currentPMICAddress == 0)
                return;

            // Try to load saved custom password; otherwise use JEDEC default.
            var saved = LoadSavedPassword();
            byte passLsb = saved?.lsb ?? 0x73;
            byte passMsb = saved?.msb ?? 0x94;

            // Save current lock state? Not needed; we'll unlock, verify, and lock.
            bool unlockOk = Device.UnlockVendorRegion(_currentPMICAddress, passLsb, passMsb);
            if (!unlockOk)
            {
                LogResponse("  ✗ Verify failed – could not unlock vendor region", "Err");
                return;
            }

            try
            {
                byte baseReg = (byte)(0x40 + blockIndex * 16);
                int errors = 0;
                for (int i = 0; i < 16; i++)
                {
                    var rb = Device.ReadPMICDevice(_currentPMICAddress, (byte)(baseReg + i));
                    if (rb == null || rb.Length == 0 || rb[0] != expected[i])
                    {
                        errors++;
                        string got = (rb != null && rb.Length > 0) ? rb[0].ToString("X2") : "?";
                        LogResponse($"    Verify mismatch @ 0x{baseReg + i:X2}: expected 0x{expected[i]:X2}, got 0x{got}", "Warn");
                    }
                }
                LogResponse(errors == 0
                    ? $"  ✓ Block {blockIndex} verify OK"
                    : $"  ⚠  Block {blockIndex} verify: {errors} mismatch(es)",
                    errors == 0 ? "Info" : "Warn");
            }
            catch (Exception ex)
            {
                LogResponse($"  ✗ Verify failed: {ex.Message}", "Err");
            }
            finally
            {
                // Re‑lock the vendor region
                Device.LockVendorRegion(_currentPMICAddress);
            }
        }


        private void OnOpenDumpClicked(object sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*",
                Title = "Open PMIC Dump"
            };

            if (dialog.ShowDialog() != DialogResult.OK) return;

            try
            {
                byte[] loaded = File.ReadAllBytes(dialog.FileName);
                if (loaded.Length != 256)
                {
                    string msg = $"File is {loaded.Length} bytes; a PMIC dump must be exactly 256 bytes.\n\nLoad anyway?";
                    if (MessageBox.Show(msg, "Size Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                }

                _currentDump = loaded;
                DisplayHexDump(_currentDump);
                _saveDumpButton.Enabled = true;
                LogResponse($"✓ Loaded {loaded.Length} bytes from: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Load failed: {ex.Message}", "Err");
                ErrorOccurred?.Invoke(this, $"Failed to load PMIC dump: {ex.Message}");
            }
        }

        private void OnSaveDumpClicked(object sender, EventArgs e)
        {
            if (_currentDump == null)
            {
                LogResponse("✗ No dump data to save", "Warn");
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*",
                FileName = $"pmic_0x{_currentPMICAddress:X2}_{DateTime.Now:yyyyMMdd_HHmmss}.bin"
            };

            if (dialog.ShowDialog() != DialogResult.OK) return;

            try
            {
                File.WriteAllBytes(dialog.FileName, _currentDump);
                LogResponse($"✓ Saved {_currentDump.Length} bytes → {dialog.FileName}");
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Save failed: {ex.Message}", "Err");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Improved hex dump display with alternating row backgrounds and colour-coded regions.
        /// Rows at even index get white background; odd rows get a very light grey.
        /// Vendor MTP registers (0x40–0x6F) are colour-highlighted.
        /// </summary>
        private void DisplayHexDump(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                _hexViewer.Clear();
                _hexViewer.Text = "No data to display";
                return;
            }

            _hexViewer.Clear();
            _hexViewer.SuspendLayout();

            // Header line
            _hexViewer.SelectionFont = new Font("Consolas", 13F, FontStyle.Bold);
            _hexViewer.SelectionColor = Color.FromArgb(0, 78, 152);
            _hexViewer.SelectionBackColor = Color.FromArgb(230, 238, 255);
            _hexViewer.AppendText("Offset    00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F   ASCII\n");
            _hexViewer.AppendText(new string('─', 72) + "\n");

            Color[] rowBg = { Color.White, Color.FromArgb(245, 245, 245) };

            for (int i = 0; i < data.Length; i += 16)
            {
                Color bg = rowBg[(i / 16) % 2];

                // Offset
                _hexViewer.SelectionFont = new Font("Consolas", 13F);
                _hexViewer.SelectionColor = Color.FromArgb(80, 80, 80);
                _hexViewer.SelectionBackColor = bg;
                _hexViewer.AppendText($"{i:X8}  ");

                int bytesInLine = Math.Min(16, data.Length - i);

                for (int j = 0; j < 16; j++)
                {
                    // Extra space at the 8-byte midpoint
                    if (j == 8)
                    {
                        _hexViewer.SelectionColor = Color.Black;
                        _hexViewer.SelectionBackColor = bg;
                        _hexViewer.AppendText(" ");
                    }

                    if (j < bytesInLine)
                    {
                        int address = i + j;
                        byte value = data[address];
                        _hexViewer.SelectionColor = GetAddressColor(address, value);
                        _hexViewer.SelectionBackColor = bg;
                        _hexViewer.AppendText($"{value:X2} ");
                    }
                    else
                    {
                        _hexViewer.SelectionColor = Color.Black;
                        _hexViewer.SelectionBackColor = bg;
                        _hexViewer.AppendText("   ");
                    }
                }

                // ASCII column
                _hexViewer.SelectionColor = Color.FromArgb(100, 100, 100);
                _hexViewer.SelectionBackColor = bg;
                _hexViewer.AppendText("  ");
                for (int j = 0; j < bytesInLine; j++)
                {
                    byte b = data[i + j];
                    _hexViewer.AppendText((b >= 32 && b < 127) ? ((char)b).ToString() : ".");
                }

                _hexViewer.AppendText("\n");
            }

            _hexViewer.SelectionStart = 0;
            _hexViewer.ScrollToCaret();
            _hexViewer.ResumeLayout();
        }

        /// <summary>Returns display colour based on PMIC register address region.</summary>
        private static Color GetAddressColor(int address, byte value)
        {
            if (value == 0x00) return Color.Silver;        // zero bytes – de-emphasised
            if (address < 0x10) return Color.DarkBlue;    // control / status
            if (address < 0x20) return Color.Navy;
            if (address < 0x40) return Color.SteelBlue;   // volatile config
            if (address < 0x50) return Color.Purple;       // MTP block 0
            if (address < 0x60) return Color.DarkOrange;   // MTP block 1
            if (address < 0x70) return Color.DarkRed;      // MTP block 2
            return Color.Black;
        }

        private void UpdateUIState(bool connected)
        {
            bool hasPMIC = connected && _detectedPMICAddresses.Count > 0;

            _openDumpButton.Enabled = connected;
            _readButton.Enabled = hasPMIC;
            _burnVendorButton.Enabled = hasPMIC;
            _burn40Button.Enabled = hasPMIC;
            _burn50Button.Enabled = hasPMIC;
            _burn60Button.Enabled = hasPMIC;
            _readRegButton.Enabled = hasPMIC;
            _writeRegButton.Enabled = hasPMIC;
            _readMeasButton.Enabled = hasPMIC;
            _toggleVregButton.Enabled = hasPMIC;
            _rebootDimmButton.Enabled = hasPMIC;
            _advancedButton.Enabled = hasPMIC;
            _unlockButton.Enabled = hasPMIC;
            _lockButton.Enabled = hasPMIC;
            _regAddressText.Enabled = hasPMIC;
            _regValueText.Enabled = hasPMIC;
            _pmicAddressCombo.Enabled = connected && _detectedPMICAddresses.Count > 0;
            _saveDumpButton.Enabled = _currentDump != null;
        }

        private void ResetInfoLabels()
        {
            _pmicModelLabel.Text = "PMIC Model: —";
            _pmicModeLabel.Text = "PMIC Mode: —";
            _outputStatusLabel.Text = "Output Status: —";
            _powerGoodLabel.Text = "Power Good: —";
            _pmicModelLabel.ForeColor = Color.Gray;
            _pmicModeLabel.ForeColor = Color.Gray;
            _powerGoodLabel.ForeColor = Color.Gray;
        }

        // Convenience factory for uniform-looking buttons
        private static Button MakeButton(string text, int width, bool enabled)
        {
            var btn = new Button
            {
                Text = text,
                Font = new Font("Segoe UI", 9.5F),
                FlatStyle = FlatStyle.Flat,
                Height = 30,
                Enabled = enabled,
                Margin = new Padding(1)
            };
            btn.FlatAppearance.BorderColor = Color.Silver;
            if (width > 0) btn.Size = new Size(width, 30);
            return btn;
        }

        // Convenience factory for inline TextBox rows (label + textbox)
        private static FlowLayoutPanel BuildInlineTextBox(string label, out TextBox textBox, int boxWidth)
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = false,
                Padding = new Padding(0)
            };

            var lbl = new Label
            {
                Text = label,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.5F),
                Padding = new Padding(0, 4, 0, 0)
            };

            textBox = new TextBox
            {
                Width = boxWidth,
                MaxLength = 2,
                Font = new Font("Consolas", 9F),
                Enabled = false
            };

            panel.Controls.Add(lbl);
            panel.Controls.Add(textBox);
            return panel;
        }

        private static Label MakeInfoLabel(string text) => new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9F),
            TextAlign = ContentAlignment.MiddleLeft,
            Height = 22,
            ForeColor = Color.Gray,
            Padding = new Padding(2, 0, 0, 0)
        };

        #endregion

        #region Password persistence

        private static string PasswordFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pmic_password.json");

        /// <summary>Saves a custom PMIC password to JSON in the application directory.</summary>
        public static void SavePassword(byte lsb, byte msb)
        {
            var obj = new { lsb, msb, saved = DateTime.Now.ToString("O") };
            File.WriteAllText(PasswordFilePath, System.Text.Json.JsonSerializer.Serialize(obj));
        }

        /// <summary>Loads a previously saved custom PMIC password. Returns null if none.</summary>
        public static (byte lsb, byte msb)? LoadSavedPassword()
        {
            try
            {
                if (!File.Exists(PasswordFilePath)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(PasswordFilePath));
                byte lsb = doc.RootElement.GetProperty("lsb").GetByte();
                byte msb = doc.RootElement.GetProperty("msb").GetByte();
                return (lsb, msb);
            }
            catch { return null; }
        }

        #endregion
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ADDED: Advanced PMIC Dialog
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Advanced PMIC options dialog.
    /// Provides: custom password change (with save-to-file), programmable-mode
    /// enable, PWR_GOOD pin/register control, and PWR_GOOD pin state readout.
    /// </summary>
    internal sealed class AdvancedPMICDialog : Form
    {
        private readonly SPDToolDevice _device;
        private readonly byte _pmicAddress;

        private TextBox _newLsbText, _newMsbText, _curLsbText, _curMsbText;
        private Label _pgoodPinLabel;

        public AdvancedPMICDialog(SPDToolDevice device, byte pmicAddress)
        {
            _device = device;
            _pmicAddress = pmicAddress;
            BuildUI();
            Text = $"Advanced PMIC Options – 0x{pmicAddress:X2}";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Size = new Size(500, 480);
            this.BackColor = Color.FromArgb(240, 240, 240);
        }

        private void BuildUI()
        {
            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                ColumnCount = 1,
                Padding = new Padding(10)
            };
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 140F));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 80F));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 130F));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // ── Password change ──────────────────────────────────────────────
            GroupBox passGroup = new GroupBox
            {
                Text = "Change Vendor Region Password  (JEDEC §17.3.2)",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            var passLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 3,
                Padding = new Padding(4)
            };
            passLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
            passLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60F));
            passLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
            passLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            // Row 0: labels
            passLayout.Controls.Add(new Label { Text = "Current LSB (0x):", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8.5F) }, 0, 0);
            _curLsbText = MkTB(); passLayout.Controls.Add(_curLsbText, 1, 0);
            passLayout.Controls.Add(new Label { Text = "Current MSB (0x):", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8.5F) }, 2, 0);
            _curMsbText = MkTB(); passLayout.Controls.Add(_curMsbText, 3, 0);

            passLayout.Controls.Add(new Label { Text = "New LSB (0x):", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8.5F) }, 0, 1);
            _newLsbText = MkTB(); passLayout.Controls.Add(_newLsbText, 1, 1);
            passLayout.Controls.Add(new Label { Text = "New MSB (0x):", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8.5F) }, 2, 1);
            _newMsbText = MkTB(); passLayout.Controls.Add(_newMsbText, 3, 1);

            var changePassBtn = new Button
            {
                Text = "Change Password & Save",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Height = 30
            };
            changePassBtn.Click += OnChangePasswordClicked;
            passLayout.SetColumnSpan(changePassBtn, 4);
            passLayout.Controls.Add(changePassBtn, 0, 2);

            passGroup.Controls.Add(passLayout);
            main.Controls.Add(passGroup, 0, 0);

            // ── Enable programmable mode ─────────────────────────────────────
            GroupBox progGroup = new GroupBox
            {
                Text = "Enable Programmable Mode  (JEDEC §17.2)",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            var progLayout = new FlowLayoutPanel { Dock = DockStyle.Fill };
            var progLabel = new Label
            {
                Text = "PMICs that ship in 'Locked' mode have bit 2 of register 0x2F cleared.\n" +
                       "This enables programmable mode (requires vendor region to be unlocked first).",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 36,
                Font = new Font("Segoe UI", 8.5F)
            };
            var progBtn = new Button { Text = "Enable Programmable Mode", Height = 28, Width = 220, Font = new Font("Segoe UI", 9F) };
            progBtn.Click += OnEnableProgModeClicked;
            progLayout.Controls.Add(progLabel);
            progLayout.Controls.Add(progBtn);
            progGroup.Controls.Add(progLayout);
            main.Controls.Add(progGroup, 0, 1);

            // ── PWR_GOOD control ─────────────────────────────────────────────
            GroupBox pgGroup = new GroupBox
            {
                Text = "PWR_GOOD Pin / Register Control",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(5),
                ForeColor = Color.FromArgb(0, 78, 152),
            };

            var pgLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };

            _pgoodPinLabel = new Label
            {
                Text = "PWR_GOOD pin state: (reading…)",
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                Padding = new Padding(0, 0, 10, 0)
            };

            var readPgBtn = new Button { Text = "Refresh", Height = 28, Width = 90, Font = new Font("Segoe UI", 8.5F), Margin = new Padding(2) };
            readPgBtn.Click += (s, e) => RefreshPGoodPin();

            var forceLoBtn = new Button { Text = "Force PWR_GOOD LOW", Height = 28, Width = 170, Font = new Font("Segoe UI", 8.5F), Margin = new Padding(2) };
            forceLoBtn.Click += (s, e) => SetPGoodMode(1);

            var forceHiBtn = new Button { Text = "Force PWR_GOOD HIGH", Height = 28, Width = 170, Font = new Font("Segoe UI", 8.5F), Margin = new Padding(2) };
            forceHiBtn.Click += (s, e) => SetPGoodMode(2);

            var normalBtn = new Button { Text = "Normal (auto)", Height = 28, Width = 120, Font = new Font("Segoe UI", 8.5F), Margin = new Padding(2) };
            normalBtn.Click += (s, e) => SetPGoodMode(0);

            pgLayout.Controls.Add(_pgoodPinLabel);
            pgLayout.Controls.Add(readPgBtn);
            pgLayout.Controls.Add(forceLoBtn);
            pgLayout.Controls.Add(forceHiBtn);
            pgLayout.Controls.Add(normalBtn);
            pgGroup.Controls.Add(pgLayout);
            main.Controls.Add(pgGroup, 0, 2);

            // ── Close button ─────────────────────────────────────────────────
            var closeBtn = new Button
            {
                Text = "Close",
                Width = 90,
                Height = 30,
                Font = new Font("Segoe UI", 9F),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Margin = new Padding(0, 2, 0, 0)
            };
            closeBtn.Click += (s, e) => Close();
            var closePanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            closePanel.Controls.Add(closeBtn);
            main.Controls.Add(closePanel, 0, 3);

            this.Controls.Add(main);

            // Initial refresh
            RefreshPGoodPin();
        }

        private TextBox MkTB() => new TextBox { Width = 38, MaxLength = 2, Font = new Font("Consolas", 9F) };

        private void OnChangePasswordClicked(object sender, EventArgs e)
        {
            try
            {
                byte curLsb = Convert.ToByte(string.IsNullOrWhiteSpace(_curLsbText.Text) ? "73" : _curLsbText.Text, 16);
                byte curMsb = Convert.ToByte(string.IsNullOrWhiteSpace(_curMsbText.Text) ? "94" : _curMsbText.Text, 16);
                byte newLsb = Convert.ToByte(_newLsbText.Text, 16);
                byte newMsb = Convert.ToByte(_newMsbText.Text, 16);

                bool ok = _device.ChangePMICPassword(_pmicAddress, curLsb, curMsb, newLsb, newMsb);

                if (ok)
                {
                    PMICOperationsTab.SavePassword(newLsb, newMsb);
                    MessageBox.Show(
                        $"Password changed to LSB=0x{newLsb:X2} MSB=0x{newMsb:X2}.\n" +
                        "The new password has been saved to pmic_password.json in the application folder.",
                        "Password Changed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("Password change failed. Verify the current password is correct.",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (FormatException)
            {
                MessageBox.Show("Invalid hex value entered. Use two hex digits (00–FF).",
                    "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnEnableProgModeClicked(object sender, EventArgs e)
        {
            if (_device == null)
            {
                MessageBox.Show("Device not connected.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                bool success = _device.SmartEnableProgrammableMode(_pmicAddress);
                MessageBox.Show(success
                    ? "Programmable mode enabled successfully.\nThe PMIC is now in Programmable Mode."
                    : "Failed to enable Programmable Mode.\nCheck connection and try again.",
                    success ? "Success" : "Failed",
                    MessageBoxButtons.OK,
                    success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void RefreshPGoodPin()
        {
            try
            {
                byte pinState = _device.GetPin(SPDToolDevice.PIN_PMIC_FLAG);
                _pgoodPinLabel.Text = $"PWR_GOOD pin state: {(pinState == 1 ? "HIGH (asserted)" : "LOW (de-asserted)")}";
                _pgoodPinLabel.ForeColor = pinState == 1 ? Color.DarkGreen : Color.DarkRed;
            }
            catch (Exception ex)
            {
                _pgoodPinLabel.Text = $"PWR_GOOD pin state: Error ({ex.Message})";
            }
        }

        /// <summary>
        /// Sets PWR_GOOD output mode via register 0x32 bits [4:3].
        /// mode 0 = normal (hardware), 1 = forced LOW, 2 = forced HIGH.
        /// JEDEC PMIC5100 §7 / register 0x32.
        /// </summary>
        private void SetPGoodMode(int mode)
        {
            try
            {
                var r32 = _device.ReadPMICDevice(_pmicAddress, 0x32);
                if (r32 == null || r32.Length == 0) { ShowRegError(); return; }

                // Clear bits [4:3], set new mode
                byte newVal = (byte)((r32[0] & ~0x18) | ((mode & 0x03) << 3));
                bool ok = _device.WriteI2CDevice(_pmicAddress, 0x32, newVal);

                MessageBox.Show(ok
                    ? $"PWR_GOOD mode set to: {new[] { "Normal (auto)", "Forced LOW", "Forced HIGH" }[mode]}"
                    : "Failed to write register 0x32.",
                    ok ? "Done" : "Error", MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private static void ShowRegError() =>
            MessageBox.Show("Failed to read register 0x32.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}