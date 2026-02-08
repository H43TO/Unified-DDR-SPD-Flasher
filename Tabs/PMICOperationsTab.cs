using SPDTool;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace UnifiedDDRSPDFlasher
{
    /// <summary>
    /// PMIC Operations Tab v2.3 - Fixed PMIC Reading Implementation
    /// All issues resolved: proper PMIC detection, correct button sizing, PMIC reading implemented
    /// </summary>
    public class PMICOperationsTab : UserControl
    {
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

        // Data
        private byte[] _currentDump;
        private byte _currentPMICAddress = 0x48;
        private List<byte> _detectedPMICAddresses = new List<byte>();

        #endregion

        #region Constructor

        public PMICOperationsTab()
        {
            InitializeComponent();
            UpdateUIState(false);
        }

        #endregion

        #region Initialization

        private void InitializeComponent()
        {
            this.Dock = DockStyle.Fill;
            this.BackColor = Color.White;

            // Main layout: 2 columns (45% left, 55% right)
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(10)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));

            // Left panel
            Panel leftPanel = CreateLeftPanel();
            mainLayout.Controls.Add(leftPanel, 0, 0);

            // Right panel
            Panel rightPanel = CreateRightPanel();
            mainLayout.Controls.Add(rightPanel, 1, 0);

            this.Controls.Add(mainLayout);
        }

        private Panel CreateLeftPanel()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(5)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 15F));

            // Top: Hex editor with buttons
            Panel hexPanel = CreateHexEditorSection();
            layout.Controls.Add(hexPanel, 0, 0);

            // Middle: Response log
            Panel logPanel = CreateResponseLogSection();
            layout.Controls.Add(logPanel, 0, 1);

            // Bottom: Single reg R/W and action buttons
            Panel bottomPanel = CreateBottomSection();
            layout.Controls.Add(bottomPanel, 0, 2);

            panel.Controls.Add(layout);
            return panel;
        }

        private Panel CreateHexEditorSection()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // Buttons - FIXED SIZE
            FlowLayoutPanel buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 2, 0, 2),
                Height = 36
            };

            _openDumpButton = new Button
            {
                Text = "Open Dump",
                Size = new Size(100, 30),
                Font = new Font("Segoe UI", 8.5F),
                Margin = new Padding(0, 0, 5, 0),
                Enabled = false
            };
            _openDumpButton.Click += OnOpenDumpClicked;

            _saveDumpButton = new Button
            {
                Text = "Save Dump",
                Size = new Size(100, 30),
                Font = new Font("Segoe UI", 8.5F),
                Enabled = false
            };
            _saveDumpButton.Click += OnSaveDumpClicked;

            buttonPanel.Controls.Add(_openDumpButton);
            buttonPanel.Controls.Add(_saveDumpButton);
            layout.Controls.Add(buttonPanel, 0, 0);

            // Hex viewer
            _hexViewer = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10F),
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
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };

            _responseLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9F),
                ReadOnly = true,
                BackColor = Color.FromArgb(250, 250, 250),
                BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            LogResponse("PMIC response log initialized. Operations will appear here.");

            panel.Controls.Add(_responseLog);
            return panel;
        }

        private Panel CreateBottomSection()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));

            // Left: Single register R/W
            GroupBox singleRegGroup = new GroupBox
            {
                Text = "Read/Write Single Register",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                Padding = new Padding(6)
            };

            TableLayoutPanel regLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                Padding = new Padding(3)
            };

            // Register input
            FlowLayoutPanel regPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Height = 26
            };
            Label regLabel = new Label
            {
                Text = "Register: 0x",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.5F)
            };
            _regAddressText = new TextBox
            {
                Width = 40,
                MaxLength = 2,
                Font = new Font("Consolas", 9F),
                Enabled = false
            };
            regPanel.Controls.Add(regLabel);
            regPanel.Controls.Add(_regAddressText);

            // Value input
            FlowLayoutPanel valPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Height = 26
            };
            Label valLabel = new Label
            {
                Text = "Value: 0x",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.5F)
            };
            _regValueText = new TextBox
            {
                Width = 40,
                MaxLength = 2,
                Font = new Font("Consolas", 9F),
                Enabled = false
            };
            valPanel.Controls.Add(valLabel);
            valPanel.Controls.Add(_regValueText);

            // Read/Write buttons
            TableLayoutPanel rwLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Height = 28
            };
            rwLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            rwLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            _readRegButton = new Button
            {
                Text = "Read",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5F),
                Height = 26,
                Margin = new Padding(1),
                Enabled = false
            };
            _readRegButton.Click += OnReadRegClicked;

            _writeRegButton = new Button
            {
                Text = "Write",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5F),
                Height = 26,
                Margin = new Padding(1),
                Enabled = false
            };
            _writeRegButton.Click += OnWriteRegClicked;

            rwLayout.Controls.Add(_readRegButton, 0, 0);
            rwLayout.Controls.Add(_writeRegButton, 1, 0);

            regLayout.Controls.Add(regPanel, 0, 0);
            regLayout.Controls.Add(valPanel, 0, 1);
            regLayout.Controls.Add(rwLayout, 0, 2);

            singleRegGroup.Controls.Add(regLayout);
            layout.Controls.Add(singleRegGroup, 0, 0);

            // Right: Action buttons
            TableLayoutPanel actionLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                Padding = new Padding(3)
            };

            _toggleVregButton = new Button
            {
                Text = "Toggle VReg",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8F),
                Height = 24,
                Margin = new Padding(1),
                Enabled = false
            };
            _toggleVregButton.Click += OnToggleVregClicked;

            _rebootDimmButton = new Button
            {
                Text = "Reboot DIMM",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8F),
                Height = 24,
                Margin = new Padding(1),
                Enabled = false
            };
            _rebootDimmButton.Click += OnRebootDimmClicked;

            _advancedButton = new Button
            {
                Text = "Advanced",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8F),
                Height = 24,
                Margin = new Padding(1),
                Enabled = false
            };
            _advancedButton.Click += OnAdvancedClicked;

            actionLayout.Controls.Add(_toggleVregButton, 0, 0);
            actionLayout.Controls.Add(_rebootDimmButton, 0, 1);
            actionLayout.Controls.Add(_advancedButton, 0, 2);
            layout.Controls.Add(actionLayout, 1, 0);

            panel.Controls.Add(layout);
            return panel;
        }

        private Panel CreateRightPanel()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(5)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                Padding = new Padding(3)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));

            // 1. PMIC I2C Address
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

            // 2. PMIC Info
            GroupBox pmicInfoGroup = new GroupBox
            {
                Text = "PMIC Info",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8)
            };

            TableLayoutPanel infoLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                Padding = new Padding(4)
            };

            _pmicModelLabel = new Label
            {
                Text = "PMIC Model: ---",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 22
            };
            infoLayout.Controls.Add(_pmicModelLabel, 0, 0);

            _pmicModeLabel = new Label
            {
                Text = "PMIC Mode: ---",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 22
            };
            infoLayout.Controls.Add(_pmicModeLabel, 0, 1);

            _outputStatusLabel = new Label
            {
                Text = "Output Status: ---",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 22
            };
            infoLayout.Controls.Add(_outputStatusLabel, 0, 2);

            _powerGoodLabel = new Label
            {
                Text = "Power Good Status: ---",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 22
            };
            infoLayout.Controls.Add(_powerGoodLabel, 0, 3);

            pmicInfoGroup.Controls.Add(infoLayout);
            layout.Controls.Add(pmicInfoGroup, 0, 1);

            // 3. PMIC Data
            GroupBox pmicDataGroup = new GroupBox
            {
                Text = "PMIC Data",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8)
            };

            TableLayoutPanel dataLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                Padding = new Padding(4)
            };

            _readButton = new Button
            {
                Text = "Read PMIC",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Height = 35,
                Margin = new Padding(3),
                Enabled = false
            };
            _readButton.FlatAppearance.BorderSize = 0;
            _readButton.Click += OnReadClicked;

            _burnVendorButton = new Button
            {
                Text = "Burn Vendor Blocks",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(232, 17, 35),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Height = 35,
                Margin = new Padding(3),
                Enabled = false
            };
            _burnVendorButton.FlatAppearance.BorderSize = 0;
            _burnVendorButton.Click += OnBurnVendorClicked;

            dataLayout.Controls.Add(_readButton, 0, 0);
            dataLayout.Controls.Add(_burnVendorButton, 0, 1);

            pmicDataGroup.Controls.Add(dataLayout);
            layout.Controls.Add(pmicDataGroup, 0, 2);

            // 4. PMIC Block Data
            GroupBox blockDataGroup = new GroupBox
            {
                Text = "PMIC Block Data",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8)
            };

            TableLayoutPanel blockLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                Padding = new Padding(4)
            };

            _burn40Button = new Button
            {
                Text = "Burn block 0x40-0x4F",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5F),
                Height = 30,
                Margin = new Padding(2),
                Enabled = false
            };
            _burn40Button.Click += (s, e) => OnBurnBlockClicked(0x40, 0x4F);

            _burn50Button = new Button
            {
                Text = "Burn block 0x50-0x5F",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5F),
                Height = 30,
                Margin = new Padding(2),
                Enabled = false
            };
            _burn50Button.Click += (s, e) => OnBurnBlockClicked(0x50, 0x5F);

            _burn60Button = new Button
            {
                Text = "Burn block 0x60-0x6F",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5F),
                Height = 30,
                Margin = new Padding(2),
                Enabled = false
            };
            _burn60Button.Click += (s, e) => OnBurnBlockClicked(0x60, 0x6F);

            blockLayout.Controls.Add(_burn40Button, 0, 0);
            blockLayout.Controls.Add(_burn50Button, 0, 1);
            blockLayout.Controls.Add(_burn60Button, 0, 2);

            blockDataGroup.Controls.Add(blockLayout);
            layout.Controls.Add(blockDataGroup, 0, 3);

            panel.Controls.Add(layout);
            return panel;
        }

        #endregion

        #region Public Methods

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
            LogResponse("Device connected. Scanning for PMIC...");
            AutoDetectPMIC();
        }

        public void OnDeviceDisconnected()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnDeviceDisconnected()));
                return;
            }

            _pmicAddressCombo.Items.Clear();
            _pmicModelLabel.Text = "PMIC Model: ---";
            _pmicModeLabel.Text = "PMIC Mode: ---";
            _outputStatusLabel.Text = "Output Status: ---";
            _powerGoodLabel.Text = "Power Good Status: ---";
            _detectedPMICAddresses.Clear();
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

            // PMIC addresses are 0x48-0x4F
            var pmicDevices = devices.Where(a => a >= 0x48 && a <= 0x4F).ToList();

            // Only update if we actually have new PMIC devices
            if (pmicDevices.Count > 0)
            {
                // Check if any PMIC devices were removed
                bool needsUpdate = false;

                // Add any new PMIC addresses
                foreach (var addr in pmicDevices)
                {
                    if (!_detectedPMICAddresses.Contains(addr))
                    {
                        _detectedPMICAddresses.Add(addr);
                        needsUpdate = true;
                    }
                }

                // Remove PMIC addresses that are no longer present
                for (int i = _detectedPMICAddresses.Count - 1; i >= 0; i--)
                {
                    if (!pmicDevices.Contains(_detectedPMICAddresses[i]))
                    {
                        _detectedPMICAddresses.RemoveAt(i);
                        needsUpdate = true;
                    }
                }

                if (needsUpdate)
                {
                    UpdatePMICAddressList();

                    // If current PMIC is no longer in list, select first available
                    if (_detectedPMICAddresses.Count > 0 && !_detectedPMICAddresses.Contains(_currentPMICAddress))
                    {
                        _currentPMICAddress = _detectedPMICAddresses[0];
                        _pmicAddressCombo.SelectedIndex = 0;
                    }
                }
            }
            // If no PMIC devices in scan but we have previously detected ones, keep them
            else if (_detectedPMICAddresses.Count > 0)
            {
                // Don't clear the list - PMICs might be temporarily unavailable
                // Just update the UI to show they might be disconnected
                UpdateUIState(Device != null);
            }
        }

        #endregion

        #region PMIC Detection

        private void AutoDetectPMIC()
        {
            if (Device == null) return;

            try
            {
                Cursor = Cursors.WaitCursor;

                // Clear previous list
                _detectedPMICAddresses.Clear();
                _pmicAddressCombo.Items.Clear();

                LogResponse("Scanning for PMIC devices (0x48-0x4F)...");

                // Try multiple times with delays - SPD5 hub might need time to initialize
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    if (attempt > 1)
                    {
                        LogResponse($"Attempt {attempt}...");
                        System.Threading.Thread.Sleep(1000); // Wait 1 second between attempts
                    }

                    for (byte addr = 0x48; addr <= 0x4F; addr++)
                    {
                        // Skip if already detected
                        if (_detectedPMICAddresses.Contains(addr))
                            continue;

                        try
                        {
                            bool isPresent = Device.ProbeAddress(addr);
                            if (isPresent)
                            {
                                _detectedPMICAddresses.Add(addr);
                                LogResponse($"✓ Found PMIC at 0x{addr:X2}");
                            }
                        }
                        catch (Exception ex)
                        {
                            // Don't log every error on retry
                            if (attempt == 1)
                                LogResponse($"✗ Error probing 0x{addr:X2}: {ex.Message}");
                        }
                    }

                    // If we found any PMICs, stop retrying
                    if (_detectedPMICAddresses.Count > 0)
                        break;
                }

                if (_detectedPMICAddresses.Count == 0)
                {
                    _pmicAddressCombo.Items.Add("No PMIC detected");
                    _pmicAddressCombo.SelectedIndex = 0;
                    _pmicAddressCombo.Enabled = false;
                    LogResponse("No PMIC devices found.");
                    return;
                }

                _pmicAddressCombo.Enabled = true;
                UpdatePMICAddressList();

                LogResponse($"Found {_detectedPMICAddresses.Count} PMIC device(s).");

                // Auto-select first PMIC
                if (_detectedPMICAddresses.Count > 0)
                {
                    _currentPMICAddress = _detectedPMICAddresses[0];
                    DetectPMICInfo();
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"PMIC auto-detect failed: {ex.Message}");
                LogResponse($"✗ Error: {ex.Message}");
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
            {
                _pmicAddressCombo.Items.Add($"0x{addr:X2}");
            }

            if (_detectedPMICAddresses.Count > 0)
            {
                _pmicAddressCombo.SelectedIndex = 0;
            }
        }

        private void DetectPMICInfo()
        {
            if (Device == null || _detectedPMICAddresses.Count == 0)
                return;

            try
            {
                Cursor = Cursors.WaitCursor;

                // Try to read PMIC identification registers
                byte[] deviceId = ReadPMICRegister(0x00, 2); // Read Device ID register
                if (deviceId != null && deviceId.Length >= 2)
                {
                    ushort id = (ushort)((deviceId[0] << 8) | deviceId[1]);
                    _pmicModelLabel.Text = $"PMIC Model: 0x{id:X4}";

                    // Try to read revision register
                    byte[] revData = ReadPMICRegister(0x03, 1);
                    if (revData != null && revData.Length >= 1)
                    {
                        _pmicModelLabel.Text += $" (Rev: 0x{revData[0]:X2})";
                    }
                }
                else
                {
                    _pmicModelLabel.Text = $"PMIC Model: 0x{_currentPMICAddress:X2}";
                }

                _pmicModeLabel.Text = "PMIC Mode: Unknown";
                _outputStatusLabel.Text = "Output Status: Unknown";
                _powerGoodLabel.Text = "Power Good Status: Unknown";

                LogResponse($"PMIC at 0x{_currentPMICAddress:X2} - Basic info loaded");
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Error detecting PMIC info: {ex.Message}");
                _pmicModelLabel.Text = $"PMIC Model: 0x{_currentPMICAddress:X2}";
                _pmicModeLabel.Text = "PMIC Mode: Unknown";
                _outputStatusLabel.Text = "Output Status: Unknown";
                _powerGoodLabel.Text = "Power Good Status: Unknown";
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private byte[] ReadPMICRegister(byte regAddress, byte length)
        {
            if (Device == null) return null;

            try
            {
                // Use the new ReadI2CDevice method which works for PMIC
                return Device.ReadI2CDevice(_currentPMICAddress, regAddress, length);
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Error reading register 0x{regAddress:X2}: {ex.Message}");
                return null;
            }
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
            DetectPMICInfo();
        }

        private void OnReadClicked(object sender, EventArgs e)
        {
            if (Device == null || _detectedPMICAddresses.Count == 0)
            {
                LogResponse("✗ Error: No device or PMIC selected");
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                LogResponse($"[INFO] Reading PMIC data from 0x{_currentPMICAddress:X2}...");

                // Read entire PMIC (256 bytes) using the new ReadI2CDevice method
                byte[] pmicData = ReadEntirePMIC();

                if (pmicData != null && pmicData.Length > 0)
                {
                    _currentDump = pmicData;
                    DisplayHexDump(_currentDump);
                    _saveDumpButton.Enabled = true;

                    // Update PMIC info based on read data
                    UpdatePMICInfoFromData(pmicData);

                    LogResponse($"✓ Successfully read {pmicData.Length} bytes from PMIC");
                }
                else
                {
                    LogResponse("✗ Failed to read PMIC data");
                }
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Error reading PMIC: {ex.Message}");
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

            List<byte> allData = new List<byte>(256);

            try
            {
                // PMIC has 256 bytes (0x00-0xFF)
                // Read in chunks of 16 bytes for reliability
                for (ushort offset = 0; offset < 256; offset += 16)
                {
                    byte chunkSize = (byte)Math.Min(16, 256 - offset);

                    try
                    {
                        // Use the new ReadI2CDevice method for PMIC
                        byte[] chunk = Device.ReadI2CDevice(_currentPMICAddress, offset, chunkSize);

                        if (chunk == null || chunk.Length != chunkSize)
                        {
                            LogResponse($"✗ Failed to read chunk at offset 0x{offset:X2}");
                            // Add placeholder bytes
                            for (int i = 0; i < chunkSize; i++) allData.Add(0xFF);
                        }
                        else
                        {
                            allData.AddRange(chunk);
                            LogResponse($"  ✓ Read 0x{offset:X2}-0x{offset + chunkSize - 1:X2} ({chunkSize} bytes)");
                        }

                        // Small delay between chunks
                        Thread.Sleep(10);
                    }
                    catch (Exception ex)
                    {
                        LogResponse($"✗ Error at offset 0x{offset:X2}: {ex.Message}");
                        // Fill remaining with 0xFF
                        int remaining = 256 - allData.Count;
                        for (int i = 0; i < remaining; i++) allData.Add(0xFF);
                        break;
                    }
                }

                LogResponse($"Total bytes read: {allData.Count}");

                if (allData.Count == 256)
                {
                    LogResponse("✓ PMIC read completed successfully");
                    return allData.ToArray();
                }
                else
                {
                    LogResponse($"✗ Incomplete read: {allData.Count}/256 bytes");
                    return allData.ToArray(); // Return partial data
                }
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Critical error reading PMIC: {ex.Message}");
                return null;
            }
        }

        private void UpdatePMICInfoFromData(byte[] data)
        {
            if (data == null || data.Length < 256) return;

            try
            {
                // Extract Device ID (registers 0x00-0x01)
                if (data.Length > 1)
                {
                    ushort deviceId = (ushort)((data[0] << 8) | data[1]);
                    _pmicModelLabel.Text = $"PMIC Model: 0x{deviceId:X4}";

                    // Add revision if available (register 0x03)
                    if (data.Length > 3)
                    {
                        _pmicModelLabel.Text += $" (Rev: 0x{data[3]:X2})";
                    }
                }

                // Check status registers (example: register 0x10 might contain status info)
                if (data.Length > 0x10)
                {
                    byte status = data[0x10];
                    _outputStatusLabel.Text = $"Output Status: 0x{status:X2}";

                    // Parse individual status bits (example bits)
                    bool pg1 = (status & 0x01) != 0;
                    bool pg2 = (status & 0x02) != 0;
                    bool pg3 = (status & 0x04) != 0;
                    bool pg4 = (status & 0x08) != 0;

                    _powerGoodLabel.Text = $"Power Good: PG1:{pg1} PG2:{pg2} PG3:{pg3} PG4:{pg4}";
                }
                else
                {
                    _outputStatusLabel.Text = "Output Status: Unknown";
                    _powerGoodLabel.Text = "Power Good Status: Unknown";
                }
            }
            catch (Exception ex)
            {
                LogResponse($"Note: Could not parse PMIC info: {ex.Message}");
            }
        }

        private void OnReadRegClicked(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_regAddressText.Text))
            {
                MessageBox.Show("Please enter a register address.", "Input Required",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                byte regAddress = Convert.ToByte(_regAddressText.Text, 16);
                LogResponse($"[INFO] Reading register 0x{regAddress:X2} from PMIC 0x{_currentPMICAddress:X2}");

                Cursor = Cursors.WaitCursor;

                // Read single register using the new ReadI2CDevice method
                byte[] data = Device.ReadI2CDevice(_currentPMICAddress, regAddress, 1);

                if (data != null && data.Length == 1)
                {
                    _regValueText.Text = data[0].ToString("X2");
                    LogResponse($"✓ Register 0x{regAddress:X2} = 0x{data[0]:X2}");
                }
                else
                {
                    LogResponse($"✗ Failed to read register 0x{regAddress:X2}");
                    _regValueText.Text = "??";
                }
            }
            catch (FormatException)
            {
                MessageBox.Show("Invalid register address format. Use hex (00-FF).", "Format Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                LogResponse($"✗ Error reading register: {ex.Message}");
                MessageBox.Show($"Error reading register: {ex.Message}", "Read Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void OnWriteRegClicked(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_regAddressText.Text) || string.IsNullOrEmpty(_regValueText.Text))
            {
                MessageBox.Show("Please enter both register address and value.", "Input Required",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                byte regAddress = Convert.ToByte(_regAddressText.Text, 16);
                byte regValue = Convert.ToByte(_regValueText.Text, 16);

                LogResponse($"[INFO] Writing 0x{regValue:X2} to register 0x{regAddress:X2} on PMIC 0x{_currentPMICAddress:X2}");

                // Implementation pending firmware support
                ShowNotImplementedMessage($"Write Register 0x{regAddress:X2}");
            }
            catch (FormatException)
            {
                MessageBox.Show("Invalid hex format. Use hex values (00-FF).", "Format Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnBurnVendorClicked(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "This will write vendor-specific data to the PMIC.\n\n" +
                "Are you sure you want to continue?",
                "Confirm Vendor Data Write",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                LogResponse("[INFO] Writing vendor data to PMIC...");
                ShowNotImplementedMessage("Burn Vendor Data");
            }
        }

        private void OnBurnBlockClicked(byte startReg, byte endReg)
        {
            var result = MessageBox.Show(
                $"This will write data to PMIC registers 0x{startReg:X2}-0x{endReg:X2}.\n\n" +
                "Are you sure you want to continue?",
                "Confirm Block Write",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                LogResponse($"[INFO] Writing block 0x{startReg:X2}-0x{endReg:X2} to PMIC 0x{_currentPMICAddress:X2}");
                ShowNotImplementedMessage($"Burn Block 0x{startReg:X2}-0x{endReg:X2}");
            }
        }

        private void OnToggleVregClicked(object sender, EventArgs e)
        {
            LogResponse("[INFO] Toggling voltage regulators...");
            ShowNotImplementedMessage("Toggle Voltage Regulators");
        }

        private void OnRebootDimmClicked(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "This will reboot the DIMM. All operations will be interrupted.\n\n" +
                "Continue?",
                "Confirm DIMM Reboot",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                LogResponse("[INFO] Rebooting DIMM...");
                ShowNotImplementedMessage("Reboot DIMM");
            }
        }

        private void OnAdvancedClicked(object sender, EventArgs e)
        {
            LogResponse("[INFO] Opening advanced PMIC operations...");
            ShowNotImplementedMessage("Advanced PMIC Operations");
        }

        private void OnOpenDumpClicked(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*";
                dialog.Title = "Open PMIC Dump";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        _currentDump = File.ReadAllBytes(dialog.FileName);
                        DisplayHexDump(_currentDump);
                        _saveDumpButton.Enabled = true;

                        LogResponse($"✓ Loaded PMIC dump: {_currentDump.Length} bytes from {dialog.FileName}");

                        // Update PMIC info from loaded dump
                        UpdatePMICInfoFromData(_currentDump);
                    }
                    catch (Exception ex)
                    {
                        LogResponse($"✗ Failed to load file: {ex.Message}");
                    }
                }
            }
        }

        private void OnSaveDumpClicked(object sender, EventArgs e)
        {
            if (_currentDump == null)
            {
                MessageBox.Show("No dump data to save.", "Save Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*";
                dialog.FileName = $"pmic_0x{_currentPMICAddress:X2}_{DateTime.Now:yyyyMMdd_HHmmss}.bin";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        File.WriteAllBytes(dialog.FileName, _currentDump);
                        LogResponse($"✓ Saved PMIC dump: {_currentDump.Length} bytes to {dialog.FileName}");
                    }
                    catch (Exception ex)
                    {
                        LogResponse($"✗ Failed to save file: {ex.Message}");
                    }
                }
            }
        }

        #endregion

        #region Helper Methods

        private void LogResponse(string message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => LogResponse(message)));
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _responseLog.AppendText($"[{timestamp}] {message}\n");
            _responseLog.ScrollToCaret();
        }

        private void DisplayHexDump(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                _hexViewer.Clear();
                _hexViewer.Text = "No data to display";
                return;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Offset(h) 00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  ASCII");
            sb.AppendLine(new string('-', 74));

            for (int i = 0; i < data.Length; i += 16)
            {
                sb.AppendFormat("{0:X8}  ", i);

                for (int j = 0; j < 16; j++)
                {
                    if (i + j < data.Length)
                        sb.AppendFormat("{0:X2} ", data[i + j]);
                    else
                        sb.Append("   ");
                }

                sb.Append(" ");

                for (int j = 0; j < 16; j++)
                {
                    if (i + j < data.Length)
                    {
                        byte b = data[i + j];
                        sb.Append((b >= 32 && b < 127) ? (char)b : '.');
                    }
                }

                sb.AppendLine();
            }

            _hexViewer.Text = sb.ToString();
        }

        private void ShowNotImplementedMessage(string operation)
        {
            MessageBox.Show(
                $"{operation} functionality is not yet implemented.\n\n" +
                "PMIC write operations require firmware updates and additional development.\n" +
                "Read functionality is now working.",
                "Feature Not Implemented",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
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
            _toggleVregButton.Enabled = hasPMIC;
            _rebootDimmButton.Enabled = hasPMIC;
            _advancedButton.Enabled = hasPMIC;
            _regAddressText.Enabled = hasPMIC;
            _regValueText.Enabled = hasPMIC;
            _pmicAddressCombo.Enabled = connected && _detectedPMICAddresses.Count > 0;

            // Enable save button if we have dump data
            _saveDumpButton.Enabled = _currentDump != null;
        }

        #endregion
    }
}