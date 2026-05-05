// ADDED: Improved hex dump display – fixed-width Consolas font, alternating row backgrounds,
//        separator at byte 8, highlighted header. DDR5 RSWP cleared before write (verified).
//        Bug fix: DDR5 page read used wrong offset; chunk addresses now computed relative to
//        the linear address space rather than the 128-byte page window.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using UDFCore;

namespace UnifiedDDRFlasher
{
    /// <summary>
    /// SPD Operations Tab – version 3.0
    /// </summary>
    public class SPDOperationsTab : UserControl
    {
        #region Constants

        private const int READ_RETRIES = 3;
        private const int WRITE_RETRIES = 3;
        private const int PAGE_SWITCH_DELAY_MS = 10;
        private const int WRITE_DELAY_MS = 20;
        private const int READ_CHUNK_SIZE = 32;

        #endregion

        #region Events

        public event EventHandler<string> ErrorOccurred;

        #endregion

        #region Private Fields

        private Func<UDFDevice> _deviceProvider;
        private UDFDevice Device => _deviceProvider?.Invoke();

        /// <summary>
        /// True for the family of exceptions that means "the USB/serial
        /// device went away mid-operation". Used by user-action handlers
        /// (Read SPD, Write All, etc.) to suppress modal popups during a
        /// disconnect - the disconnect-detection loop in MainForm will
        /// finish tearing down within DISCONNECT_CHECK_INTERVAL_MS, so a
        /// popup here is just noise that the user has to dismiss.
        /// </summary>
        private static bool IsDisconnectException(Exception ex) =>
            ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ObjectDisposedException
            || ex is InvalidOperationException;

        private RichTextBox _hexViewer;
        private ProgressBar _writeProgress;
        private Button _openDumpButton;
        private Button _saveDumpButton;
        private ComboBox _moduleAddressCombo;
        private Label _detectedGenLabel;
        private Label _spdSizeLabel;
        // Quick-glance summary fields hoisted into Module Info from the
        // (now removed) inline Parsed Fields panel. Populated whenever a
        // dump is loaded - either from a fresh read or from disk.
        private Label _speedGradeLabel;
        private Label _capacityLabel;
        private Label _busWidthLabel;
        private Label _crcStatusLabel;
        private FlowLayoutPanel _writeProtectionPanel;
        private Button _readButton;
        private Button _verifyButton;
        private Button _writeAllButton;
        private Button _parsedFieldsButton;

        private byte[] _currentDump;
        private byte _currentAddress = 0x50;
        private ModuleInfo _currentModuleInfo;
        private List<byte> _detectedAddresses = new List<byte>();

        private ToolTip _toolTip;

        #endregion

        #region Constructor

        public SPDOperationsTab()
        {
            _toolTip = new ToolTip { AutoPopDelay = 6000, InitialDelay = 400 };
            InitializeComponent();
            UpdateUIState(false);
        }

        #endregion

        #region Initialization

        private void InitializeComponent()
        {
            this.Dock = DockStyle.Fill;
            this.BackColor = Color.FromArgb(240, 240, 240);

            // Single full-width row: hex viewer | control panel.
            // The previous v3.0 layout had a second row holding a collapsible
            // Parsed Fields group box; that panel duplicated the modal dialog
            // (opened by the "Parsed Fields..." button) and wasted vertical
            // space, so the inline panel was removed. The four most useful
            // fields (speed, capacity, DRAM bus width, CRC) now live inside the
            // Module Info box on the right. The full breakdown is still
            // available via the button -> dialog.
            var topSplit = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(10)
            };
            topSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57F));
            topSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43F));
            topSplit.Controls.Add(CreateHexEditorPanel(), 0, 0);
            topSplit.Controls.Add(CreateControlPanel(), 1, 0);

            this.Controls.Add(topSplit);
        }

        private Panel CreateHexEditorPanel()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // Buttons
            TableLayoutPanel buttonLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(0, 2, 0, 2)
            };
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            _openDumpButton = new Button
            {
                Text = "Open Dump",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10F),
                Height = 30,
                Margin = new Padding(5),
                Enabled = false
            };
            _openDumpButton.Click += OnOpenDumpClicked;
            _toolTip.SetToolTip(_openDumpButton, "Load an SPD dump from a binary file.");

            _saveDumpButton = new Button
            {
                Text = "Save Dump",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10F),
                Height = 30,
                Margin = new Padding(5),
                Enabled = false
            };
            _saveDumpButton.Click += OnSaveDumpClicked;
            _toolTip.SetToolTip(_saveDumpButton, "Save the current SPD data to a binary file.");

            buttonLayout.Controls.Add(_openDumpButton, 0, 0);
            buttonLayout.Controls.Add(_saveDumpButton, 1, 0);
            layout.Controls.Add(buttonLayout, 0, 0);

            // §6.2: write progress bar - sits between the action buttons and the
            // hex viewer; visible only during a Write All operation.
            _writeProgress = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Visible = false,
                Style = ProgressBarStyle.Continuous
            };
            layout.Controls.Add(_writeProgress, 0, 1);

            // ADDED: Improved hex viewer - Consolas 9.5pt, horizontal scrollable
            _hexViewer = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5F),
                ReadOnly = true,
                BackColor = Color.White,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                BorderStyle = BorderStyle.FixedSingle
            };
            layout.Controls.Add(_hexViewer, 0, 2);

            panel.Controls.Add(layout);
            return panel;
        }

        private Panel CreateControlPanel()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                ColumnCount = 1,
                Padding = new Padding(5)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));

            // 1. Address selector
            Panel addressPanel = new Panel { Dock = DockStyle.Fill };
            Label addressLabel = new Label
            {
                Text = "I2C Address:",
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.BottomLeft
            };
            _moduleAddressCombo = new ComboBox
            {
                Dock = DockStyle.Top,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Consolas", 9.5F),
                Height = 28
            };
            _moduleAddressCombo.SelectedIndexChanged += OnModuleAddressChanged;
            addressPanel.Controls.Add(_moduleAddressCombo);
            addressPanel.Controls.Add(addressLabel);
            layout.Controls.Add(addressPanel, 0, 0);

            // 2. Module Info
            GroupBox moduleInfoGroup = new GroupBox
            {
                Text = "Module Info",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            // Eight rows: detected gen, SPD size, speed, capacity, DRAM bus width,
            // CRC status, "WP:" header label, then the WP block strip.
            // Each summary row is fixed-height so the WP strip absorbs the rest.
            TableLayoutPanel infoLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 8,
                Padding = new Padding(5)
            };
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));   // gen
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));   // size
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));   // speed
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));   // capacity
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));   // DRAM bus width
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));   // CRC
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));   // "WP:" header
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // WP blocks fill the rest

            // Local helper - keeps the boilerplate from drowning the layout.
            Label MakeRowLabel(string text) => new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(3),
                AutoEllipsis = true
            };

            _detectedGenLabel = MakeRowLabel("Detected Generation: —");
            infoLayout.Controls.Add(_detectedGenLabel, 0, 0);

            _spdSizeLabel = MakeRowLabel("SPD Size: —");
            infoLayout.Controls.Add(_spdSizeLabel, 0, 1);

            // Promoted from the (removed) inline parsed-fields panel: the four
            // numbers most people glance at first - speed grade, total capacity,
            // DRAM bus width, CRC status. Populated by UpdateModuleSummary() whenever
            // _currentDump changes (read or load-from-disk).
            _speedGradeLabel = MakeRowLabel("Speed: —");
            infoLayout.Controls.Add(_speedGradeLabel, 0, 2);

            _capacityLabel = MakeRowLabel("Capacity: —");
            infoLayout.Controls.Add(_capacityLabel, 0, 3);

            _busWidthLabel = MakeRowLabel("DRAM Bus Width: —");
            infoLayout.Controls.Add(_busWidthLabel, 0, 4);

            _crcStatusLabel = MakeRowLabel("CRC: —");
            infoLayout.Controls.Add(_crcStatusLabel, 0, 5);

            Label wpLabel = MakeRowLabel("Write Protection (click block to toggle):");
            infoLayout.Controls.Add(wpLabel, 0, 6);

            _writeProtectionPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = true
            };
            infoLayout.Controls.Add(_writeProtectionPanel, 0, 7);

            moduleInfoGroup.Controls.Add(infoLayout);
            layout.Controls.Add(moduleInfoGroup, 0, 1);

            // 3. SPD Data
            GroupBox spdDataGroup = new GroupBox
            {
                Text = "SPD Data",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            TableLayoutPanel dataLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                Padding = new Padding(5)
            };
            dataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
            dataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            dataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));

            _readButton = new Button
            {
                Text = "Read SPD",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(3),
                Height = 35
            };
            _readButton.FlatAppearance.BorderSize = 0;
            _readButton.Click += OnReadClicked;
            _toolTip.SetToolTip(_readButton, "Read the full SPD contents from the selected module.");
            dataLayout.Controls.Add(_readButton, 0, 0);

            TableLayoutPanel verifyWriteLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2
            };
            verifyWriteLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            verifyWriteLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            _verifyButton = new Button
            {
                Text = "Verify",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Margin = new Padding(3),
                Height = 32,
                Enabled = false
            };
            _verifyButton.Click += OnVerifyClicked;
            _toolTip.SetToolTip(_verifyButton, "Compare the loaded dump against the module's current SPD content.");

            _writeAllButton = new Button
            {
                Text = "Write All",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(192, 0, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(3),
                Height = 32,
                Enabled = false
            };
            _writeAllButton.FlatAppearance.BorderSize = 0;
            _writeAllButton.Click += OnWriteAllClicked;
            _toolTip.SetToolTip(_writeAllButton, "Write the loaded dump to the selected SPD module. RSWP is cleared automatically for DDR4/5.");

            verifyWriteLayout.Controls.Add(_verifyButton, 0, 0);
            verifyWriteLayout.Controls.Add(_writeAllButton, 1, 0);
            dataLayout.Controls.Add(verifyWriteLayout, 0, 1);

            // §4: parsed-fields panel access via a dedicated button
            _parsedFieldsButton = new Button
            {
                Text = "Parsed Fields...",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Margin = new Padding(3),
                Height = 32,
                Enabled = false
            };
            _parsedFieldsButton.Click += OnParsedFieldsClicked;
            _toolTip.SetToolTip(_parsedFieldsButton,
                "Display JEDEC-decoded SPD fields (timings, capacity, CAS latencies, CRCs).");
            dataLayout.Controls.Add(_parsedFieldsButton, 0, 2);

            spdDataGroup.Controls.Add(dataLayout);
            layout.Controls.Add(spdDataGroup, 0, 2);

            panel.Controls.Add(layout);
            return panel;
        }

        #endregion

        #region Public Methods

        public void SetDeviceProvider(Func<UDFDevice> deviceProvider) =>
            _deviceProvider = deviceProvider;

        public void OnDeviceConnected(UDFDevice device)
        {
            if (InvokeRequired) { Invoke(new Action(() => OnDeviceConnected(device))); return; }
            UpdateUIState(true);
            AutoDetectModules();
        }

        public void OnDeviceDisconnected()
        {
            if (InvokeRequired) { Invoke(new Action(OnDeviceDisconnected)); return; }

            _detectedGenLabel.Text = "Detected Generation: —";
            _spdSizeLabel.Text = "SPD Size: —";
            ResetModuleSummary();
            _writeProtectionPanel.Controls.Clear();
            _moduleAddressCombo.Items.Clear();
            _currentModuleInfo = null;
            _currentDump = null;
            _hexViewer.Clear();
            UpdateUIState(false);
        }

        public void UpdateDetectedDevices(List<byte> devices)
        {
            if (InvokeRequired) { Invoke(new Action(() => UpdateDetectedDevices(devices))); return; }

            var spdDevices = devices.Where(a => a >= 0x50 && a <= 0x57).ToList();

            if (!spdDevices.SequenceEqual(_detectedAddresses))
            {
                _detectedAddresses = new List<byte>(spdDevices);
                AutoDetectModules();
            }
        }

        #endregion

        #region Auto Detection

        private void AutoDetectModules()
        {
            if (Device == null) return;

            try
            {
                Cursor = Cursors.WaitCursor;
                _moduleAddressCombo.Items.Clear();
                _detectedAddresses.Clear();

                var devices = Device.ScanBus();
                var spdDevices = devices.Where(a => a >= 0x50 && a <= 0x57).ToList();

                if (spdDevices.Count == 0)
                {
                    _moduleAddressCombo.Items.Add("No modules detected");
                    _moduleAddressCombo.SelectedIndex = 0;
                    _moduleAddressCombo.Enabled = false;
                    _detectedGenLabel.Text = "Detected Generation: No DIMM";
                    _spdSizeLabel.Text = "SPD Size: —";
                    ResetModuleSummary();
                    _writeProtectionPanel.Controls.Clear();
                    _writeProtectionPanel.Controls.Add(new Label
                    {
                        Text = "No DIMM detected",
                        Font = new Font("Segoe UI", 10F),
                        ForeColor = Color.Gray,
                        AutoSize = true
                    });
                    _currentModuleInfo = null;
                    UpdateUIState(true);
                    return;
                }

                _detectedAddresses = spdDevices;
                _moduleAddressCombo.Enabled = true;

                foreach (byte addr in spdDevices)
                {
                    try
                    {
                        var info = Device.DetectModule(addr);
                        _moduleAddressCombo.Items.Add($"0x{addr:X2} — {info.Type}");
                    }
                    catch
                    {
                        _moduleAddressCombo.Items.Add($"0x{addr:X2} — Unknown");
                    }
                }

                _moduleAddressCombo.SelectedIndex = 0;

                if (_detectedAddresses.Count > 0)
                {
                    _currentAddress = _detectedAddresses[0];
                    DetectCurrentModule();
                }

                UpdateUIState(true);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Auto-detect failed: {ex.Message}");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        #endregion

        #region Event Handlers

        private void OnModuleAddressChanged(object sender, EventArgs e)
        {
            if (_moduleAddressCombo.SelectedIndex < 0 || _detectedAddresses.Count == 0) return;
            if (_moduleAddressCombo.SelectedIndex >= _detectedAddresses.Count) return;

            _currentAddress = _detectedAddresses[_moduleAddressCombo.SelectedIndex];
            DetectCurrentModule();
            UpdateUIState(true);
        }

        private void DetectCurrentModule()
        {
            if (Device == null) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                _currentModuleInfo = Device.DetectModule(_currentAddress);
                _detectedGenLabel.Text = $"Detected Generation: {_currentModuleInfo.Type}";
                _spdSizeLabel.Text = $"SPD Size: {_currentModuleInfo.Size} bytes";
                UpdateWriteProtectionDisplay();
            }
            catch (Exception ex)
            {
                _detectedGenLabel.Text = "Detected Generation: Error";
                _spdSizeLabel.Text = "SPD Size: —";
                ErrorOccurred?.Invoke(this, $"Module detection failed: {ex.Message}");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void UpdateWriteProtectionDisplay()
        {
            _writeProtectionPanel.Controls.Clear();

            if (_currentModuleInfo?.Type != ModuleType.DDR5)
            {
                _writeProtectionPanel.Controls.Add(new Label
                {
                    Text = "Hardware WP cleared automatically on write",
                    Font = new Font("Segoe UI", 9F),
                    ForeColor = Color.DimGray,
                    AutoSize = true
                });
                return;
            }

            try
            {
                for (byte block = 0; block < 16; block++)
                {
                    byte capturedBlock = block;
                    bool isProtected = Device.GetRSWP(_currentAddress, block);

                    var blockLabel = new Label
                    {
                        Text = block.ToString(),
                        Width = 40,
                        Height = 40,
                        TextAlign = ContentAlignment.MiddleCenter,
                        BorderStyle = BorderStyle.FixedSingle,
                        BackColor = isProtected ? Color.LightCoral : Color.LightGreen,
                        Font = new Font("Segoe UI", 9F),
                        Margin = new Padding(1),
                        Cursor = Cursors.Hand
                    };
                    blockLabel.Click += (s, e) => OnWriteProtectionBlockClicked(capturedBlock);
                    _toolTip.SetToolTip(blockLabel, isProtected
                        ? $"Block {block}: Protected (click to toggle)"
                        : $"Block {block}: Writable (click to toggle)");

                    _writeProtectionPanel.Controls.Add(blockLabel);
                }
            }
            catch (Exception ex)
            {
                _writeProtectionPanel.Controls.Add(new Label
                {
                    Text = "Read error",
                    Font = new Font("Segoe UI", 10F),
                    ForeColor = Color.Red,
                    AutoSize = true
                });
                ErrorOccurred?.Invoke(this, $"Failed to read WP status: {ex.Message}");
            }
        }

        private void OnWriteProtectionBlockClicked(byte block)
        {
            if (_currentModuleInfo?.Type != ModuleType.DDR5) return;

            var result = MessageBox.Show(
                $"Toggle write protection for block {block}?",
                "Write Protection",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes) return;

            try
            {
                bool current = Device.GetRSWP(_currentAddress, block);
                if (current)
                    Device.ClearRSWP(_currentAddress);
                else
                    Device.SetRSWP(_currentAddress, block);

                UpdateWriteProtectionDisplay();
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"WP toggle failed: {ex.Message}");
            }
        }

        private void OnReadClicked(object sender, EventArgs e)
        {
            if (Device == null)
            {
                ErrorOccurred?.Invoke(this, "Device not connected");
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                _readButton.Enabled = false;

                _currentDump = ReadEntireSPDWithRetry(_currentAddress);

                if (_currentDump == null || _currentDump.Length == 0)
                {
                    ErrorOccurred?.Invoke(this, "Failed to read SPD — no data received");
                    MessageBox.Show("Failed to read SPD. Check connections and try again.",
                        "Read Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                DisplayHexDump(_currentDump);
                UpdateModuleSummary();
                _saveDumpButton.Enabled = true;
                _verifyButton.Enabled = true;
                _writeAllButton.Enabled = true;
                _parsedFieldsButton.Enabled = true;
            }
            catch (Exception ex) when (IsDisconnectException(ex))
            {
                // Device went away mid-read. The disconnect-check loop in
                // MainForm will handle teardown within ~500 ms; a popup
                // here would just stack on top of the disconnect notice.
                ErrorOccurred?.Invoke(this, $"Read aborted (device disconnected): {ex.Message}");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Read failed: {ex.Message}");
                MessageBox.Show($"Read failed:\n\n{ex.Message}\n\nCheck connections and device compatibility.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                _readButton.Enabled = true;
            }
        }

        private byte[] ReadEntireSPDWithRetry(byte address)
        {
            var info = Device.DetectModule(address);

            if (info.Type == ModuleType.DDR5)
                return ReadDDR5SPD(address, info.Size);
            else
                return ReadStandardSPD(address, info.Size);
        }

        /// <summary>
        /// Reads a DDR5 SPD by switching the SPD5 hub page register (MR11) and reading
        /// 128-byte windows. Each page covers offsets [page*128 .. page*128+127] in the
        /// linear address space.
        ///
        /// BUG FIX (v3.0): Previous version passed linear offsets directly to ReadSPD
        /// which is correct because the firmware translates them; however the chunk offset
        /// calculation was wrong (it was always offset 0..127 within a page rather than
        /// the correct linear offset). Fixed by always using linear offsets.
        /// </summary>
        private byte[] ReadDDR5SPD(byte address, int totalSize)
        {
            var allData = new List<byte>();

            // JEDEC DDR5 SPD: the SPD5 hub exposes 128-byte pages via MR11 (page register).
            // Page 0 = bytes 0x000-0x07F, page 1 = 0x080-0x0FF, etc.
            int pagesNeeded = (totalSize + 127) / 128;

            for (byte page = 0; page < pagesNeeded; page++)
            {
                try
                {
                    // §1.5: read exactly bytesThisPage, not always 128. When totalSize
                    // isn't a multiple of 128 the previous code would underrun the inner
                    // loop and the outer loop would request a non-existent extra page.
                    int bytesRemainingTotal = totalSize - allData.Count;
                    if (bytesRemainingTotal <= 0) break;
                    int bytesThisPage = Math.Min(128, bytesRemainingTotal);

                    // Switch to the correct page
                    if (!Device.WriteSPD5HubRegister(address, UDFDevice.MR11, page))
                        throw new Exception($"Failed to set page {page} via MR11");

                    System.Threading.Thread.Sleep(PAGE_SWITCH_DELAY_MS);

                    int bytesReadThisPage = 0;
                    while (bytesReadThisPage < bytesThisPage)
                    {
                        byte chunkSize = (byte)Math.Min(READ_CHUNK_SIZE, bytesThisPage - bytesReadThisPage);
                        // Linear address: page * 128 + offset within page
                        ushort linearOffset = (ushort)(page * 128 + bytesReadThisPage);

                        byte[] chunk = ReadWithRetry(address, linearOffset, chunkSize, READ_RETRIES);
                        if (chunk == null || chunk.Length != chunkSize)
                            throw new Exception($"Failed to read page {page} at offset 0x{linearOffset:X3}");

                        allData.AddRange(chunk);
                        bytesReadThisPage += chunkSize;
                    }
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"DDR5 page {page} read error: {ex.Message}");
                    return null;
                }
            }

            if (allData.Count > totalSize)
                allData.RemoveRange(totalSize, allData.Count - totalSize);

            return allData.ToArray();
        }

        private byte[] ReadStandardSPD(byte address, int size)
        {
            var allData = new List<byte>();

            for (ushort offset = 0; offset < size; offset += READ_CHUNK_SIZE)
            {
                byte chunkSize = (byte)Math.Min(READ_CHUNK_SIZE, size - offset);
                byte[] chunk = ReadWithRetry(address, offset, chunkSize, READ_RETRIES);
                if (chunk == null || chunk.Length != chunkSize)
                    throw new Exception($"Failed to read at offset 0x{offset:X3}");

                allData.AddRange(chunk);
            }

            return allData.ToArray();
        }

        private byte[] ReadWithRetry(byte address, ushort offset, byte length, int maxRetries)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    byte[] data = Device.ReadSPD(address, offset, length);
                    if (data != null && data.Length == length) return data;
                    System.Threading.Thread.Sleep(50 * (attempt + 1));
                }
                catch (Exception)
                {
                    if (attempt == maxRetries - 1) throw;
                    System.Threading.Thread.Sleep(100 * (attempt + 1));
                }
            }
            return null;
        }

        private void OnVerifyClicked(object sender, EventArgs e)
        {
            if (Device == null || _currentDump == null)
            {
                ErrorOccurred?.Invoke(this, "No dump loaded to verify");
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                _verifyButton.Enabled = false;

                byte[] readData = ReadEntireSPDWithRetry(_currentAddress);
                if (readData == null)
                {
                    ErrorOccurred?.Invoke(this, "Failed to read SPD for verification");
                    return;
                }

                int minLen = Math.Min(readData.Length, _currentDump.Length);
                var diffOffsets = new List<int>();

                for (int i = 0; i < minLen; i++)
                    if (readData[i] != _currentDump[i])
                        diffOffsets.Add(i);

                if (diffOffsets.Count == 0)
                {
                    MessageBox.Show("Verification PASSED!\nSPD matches the loaded dump.", "Verify",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    string diffList = diffOffsets.Count > 10
                        ? $"{diffOffsets.Count} bytes differ (first 10: {string.Join(", ", diffOffsets.Take(10).Select(o => $"0x{o:X3}"))})"
                        : $"Differences at: {string.Join(", ", diffOffsets.Select(o => $"0x{o:X3}"))}";

                    ErrorOccurred?.Invoke(this, $"Verification failed: {diffList}");
                    MessageBox.Show($"Verification FAILED!\n{diffOffsets.Count} byte(s) differ.\n\n{diffList}",
                        "Verify Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Verification error: {ex.Message}");
            }
            finally
            {
                Cursor = Cursors.Default;
                _verifyButton.Enabled = true;
            }
        }

        private void OnWriteAllClicked(object sender, EventArgs e)
        {
            if (Device == null || _currentDump == null)
            {
                ErrorOccurred?.Invoke(this, "No dump loaded");
                return;
            }

            var result = MessageBox.Show(
                $"⚠ WARNING ⚠\n\nThis will OVERWRITE the entire SPD at 0x{_currentAddress:X2}!\n\n" +
                $"Size: {_currentDump.Length} bytes\n\nThis cannot be undone. Continue?",
                "Confirm Write All", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result != DialogResult.Yes) return;

            try
            {
                Cursor = Cursors.WaitCursor;
                _writeAllButton.Enabled = false;

                // §6.2: show the progress bar; never mutate button text as a progress indicator.
                _writeProgress.Value = 0;
                _writeProgress.Maximum = _currentDump.Length;
                _writeProgress.Visible = true;

                // VERIFIED: RSWP is cleared for DDR5 before writing.
                // For DDR4, ClearRSWP is also attempted (some DDR4 modules ignore it safely).
                if (_currentModuleInfo?.Type == ModuleType.DDR5)
                    Device.ClearRSWP(_currentAddress);
                else if (_currentModuleInfo?.Type == ModuleType.DDR4)
                {
                    try { Device.ClearRSWP(_currentAddress); } catch { /* ignore */ }
                }

                int totalBytes = _currentDump.Length;
                int bytesWritten = 0;
                int errors = 0;

                for (ushort offset = 0; offset < totalBytes; offset += 16)
                {
                    int chunkSize = Math.Min(16, totalBytes - offset);
                    byte[] chunk = new byte[chunkSize];
                    Array.Copy(_currentDump, offset, chunk, 0, chunkSize);

                    // Brief inter-page delay for DDR4 page 2 (offset >= 256)
                    if (_currentModuleInfo?.Type == ModuleType.DDR4 && offset == 256)
                        System.Threading.Thread.Sleep(20);

                    bool writeSuccess = false;
                    for (int retry = 0; retry < WRITE_RETRIES; retry++)
                    {
                        try
                        {
                            writeSuccess = Device.WriteSPDPage(_currentAddress, offset, chunk);
                            if (writeSuccess) break;
                            System.Threading.Thread.Sleep(50 * (retry + 1));
                        }
                        catch
                        {
                            System.Threading.Thread.Sleep(100 * (retry + 1));
                        }
                    }

                    if (!writeSuccess)
                    {
                        errors++;
                        ErrorOccurred?.Invoke(this, $"Write failed at 0x{offset:X3}");

                        // Byte-by-byte fallback
                        for (int i = 0; i < chunkSize; i++)
                        {
                            try
                            {
                                if (Device.WriteSPDByte(_currentAddress, (ushort)(offset + i), chunk[i]))
                                    errors--;
                            }
                            catch { }
                        }

                        if (errors > 3)
                            throw new Exception($"Multiple write failures; stopping at 0x{offset:X3}");
                    }

                    bytesWritten += chunkSize;
                    // §6.2: update progress bar; never the button text.
                    _writeProgress.Value = Math.Min(bytesWritten, _writeProgress.Maximum);
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(WRITE_DELAY_MS);
                }

                string msg = errors == 0
                    ? $"Successfully wrote {totalBytes} bytes to SPD!"
                    : $"Write completed with {errors} error(s). Verify is recommended.";
                MessageBox.Show(msg, errors == 0 ? "Write Complete" : "Write Complete with Errors",
                    MessageBoxButtons.OK, errors == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex) when (IsDisconnectException(ex))
            {
                ErrorOccurred?.Invoke(this, $"Write aborted (device disconnected): {ex.Message}");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Write failed: {ex.Message}");
                MessageBox.Show($"Write failed:\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                _writeProgress.Visible = false;
                _writeProgress.Value = 0;
                _writeAllButton.Enabled = true;
            }
        }

        private void OnOpenDumpClicked(object sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*",
                Title = "Open SPD Dump"
            };

            if (dialog.ShowDialog() != DialogResult.OK) return;

            try
            {
                byte[] loaded = File.ReadAllBytes(dialog.FileName);

                if (_currentModuleInfo != null && _currentModuleInfo.Size > 0 &&
                    loaded.Length != _currentModuleInfo.Size)
                {
                    string warn = $"Loaded dump is {loaded.Length} bytes; SPD is {_currentModuleInfo.Size} bytes.\n\n" +
                                  "Writing this dump may cause issues. Continue?";
                    if (MessageBox.Show(warn, "Size Mismatch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
                        return;
                }

                _currentDump = loaded;
                DisplayHexDump(_currentDump);
                UpdateModuleSummary();
                _saveDumpButton.Enabled = true;

                bool hasModule = _currentModuleInfo != null && _detectedAddresses.Count > 0;
                _verifyButton.Enabled = hasModule;
                _writeAllButton.Enabled = hasModule;
                _parsedFieldsButton.Enabled = true;

                ErrorOccurred?.Invoke(this, $"[INFO] Loaded {loaded.Length} bytes from {dialog.FileName}");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to load file: {ex.Message}");
            }
        }

        private void OnSaveDumpClicked(object sender, EventArgs e)
        {
            if (_currentDump == null)
            {
                ErrorOccurred?.Invoke(this, "No dump data to save");
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*",
                FileName = $"spd_0x{_currentAddress:X2}_{DateTime.Now:yyyyMMdd_HHmmss}.bin"
            };

            if (dialog.ShowDialog() != DialogResult.OK) return;

            try
            {
                File.WriteAllBytes(dialog.FileName, _currentDump);
                MessageBox.Show($"Saved {_currentDump.Length} bytes.", "File Saved",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to save file: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// ADDED: Improved hex dump with alternating row backgrounds, 8-byte mid-group separator,
        /// and colour-coded offset column. Uses RichTextBox for character-level formatting.
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

            Color[] rowBg = { Color.White, Color.FromArgb(245, 245, 245) };

            // Header
            _hexViewer.SelectionFont = new Font("Consolas", 9.5F, FontStyle.Bold);
            _hexViewer.SelectionColor = Color.FromArgb(0, 78, 152);
            _hexViewer.SelectionBackColor = Color.FromArgb(228, 236, 255);
            _hexViewer.AppendText("Offset    00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F   ASCII\n");
            _hexViewer.AppendText(new string('═', 71) + "\n");

            for (int i = 0; i < data.Length; i += 16)
            {
                Color bg = rowBg[(i / 16) % 2];
                int bytesInLine = Math.Min(16, data.Length - i);

                // Offset
                _hexViewer.SelectionFont = new Font("Consolas", 9.5F);
                _hexViewer.SelectionColor = Color.FromArgb(100, 100, 100);
                _hexViewer.SelectionBackColor = bg;
                _hexViewer.AppendText($"{i:X8}  ");

                // Hex bytes
                for (int j = 0; j < 16; j++)
                {
                    if (j == 8)
                    {
                        _hexViewer.SelectionBackColor = bg;
                        _hexViewer.SelectionColor = Color.Black;
                        _hexViewer.AppendText(" ");
                    }

                    if (j < bytesInLine)
                    {
                        byte b = data[i + j];
                        _hexViewer.SelectionColor = b == 0x00 ? Color.Silver
                            : Color.Black;
                        _hexViewer.SelectionBackColor = bg;
                        _hexViewer.AppendText($"{b:X2} ");
                    }
                    else
                    {
                        _hexViewer.SelectionBackColor = bg;
                        _hexViewer.SelectionColor = Color.Black;
                        _hexViewer.AppendText("   ");
                    }
                }

                // ASCII
                _hexViewer.SelectionColor = Color.Black;
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

        private void UpdateUIState(bool connected)
        {
            bool hasModule = connected && _detectedAddresses.Count > 0;
            bool hasDump = _currentDump != null;

            _openDumpButton.Enabled = hasModule;
            _saveDumpButton.Enabled = hasDump;
            _readButton.Enabled = hasModule;
            _verifyButton.Enabled = hasModule && hasDump;
            _writeAllButton.Enabled = hasModule && hasDump;
            _parsedFieldsButton.Enabled = hasDump;
            _moduleAddressCombo.Enabled = hasModule;
        }

        // §4: open the parsed-fields dialog. Available even when disconnected
        // as long as a dump has been loaded from disk.
        private void OnParsedFieldsClicked(object sender, EventArgs e)
        {
            if (_currentDump == null)
            {
                MessageBox.Show("No SPD dump loaded.", "Parsed Fields",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new ParsedFieldsDialog(_currentDump))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.PatchedDump != null)
                {
                    _currentDump = dlg.PatchedDump;
                    DisplayHexDump(_currentDump);
                    UpdateModuleSummary();   // CRC went green; reflect it
                    ErrorOccurred?.Invoke(this,
                        "[INFO] CRC patched in memory; click 'Write All' to push to module.");
                }
            }
        }

        /// <summary>
        /// Pulls the four summary numbers (speed, capacity, DRAM bus width, CRC)
        /// out of <see cref="SPDParsedFields.Parse"/> and writes them into the
        /// Module Info row. Tolerant of partial dumps - any field the parser
        /// couldn't compute is shown as "—".
        ///
        /// Called from: Read SPD success, Open Dump success, post-Recalc-CRC.
        /// </summary>
        private void UpdateModuleSummary()
        {
            if (_currentDump == null || _currentDump.Length < 32)
            {
                ResetModuleSummary();
                return;
            }

            var result = SPDParsedFields.Parse(_currentDump);

            // We pull values straight out of the parsed list - no need to
            // duplicate the byte arithmetic here. Labels match the strings
            // used inside SPDParsedFields.cs.
            string speed = FindField(result, "tCKAVGmin");
            // tCKAVGmin reads back as e.g. "312 ps (DDR5-6400)". Extract the
            // grade from the parens for a cleaner display.
            string speedGrade = ExtractParen(speed);
            _speedGradeLabel.Text = $"Speed: {(string.IsNullOrEmpty(speedGrade) ? "—" : speedGrade)}";

            string cap = FindField(result, "Module Capacity");
            // Asymmetric DDR5 modules use a different label so they don't
            // collide with the symmetric one — fall back to that.
            if (string.IsNullOrEmpty(cap)) cap = FindField(result, "Module Capacity (asymmetric)");
            _capacityLabel.Text = $"Capacity: {(string.IsNullOrEmpty(cap) ? "—" : cap)}";

            // The dram chips data width
            string bus = FindField(result, "DRAM Bus Width");
            if (string.IsNullOrEmpty(bus)) bus = FindField(result, "DRAM Width"); //DDR4
            if (string.IsNullOrEmpty(bus)) bus = FindField(result, "SDRAM IO Width"); //DDR5
            _busWidthLabel.Text = $"DRAM Bus Width: {(string.IsNullOrEmpty(bus) ? "—" : bus)}";

            // CRC is special: SPDParsedFields tags those fields with IsCrcStatus
            // so we can colour the label and report a clear pass/fail.
            var crcFields = result.Fields.Where(f => f.IsCrcStatus).ToList();
            if (crcFields.Count == 0)
            {
                _crcStatusLabel.Text = "CRC: —";
                _crcStatusLabel.ForeColor = SystemColors.ControlText;
            }
            else
            {
                bool allOk = crcFields.All(f => f.CrcOk);
                if (crcFields.Count == 1)
                {
                    _crcStatusLabel.Text = allOk ? "CRC: OK" : "CRC: FAIL";
                }
                else
                {
                    int okCount = crcFields.Count(f => f.CrcOk);
                    _crcStatusLabel.Text = $"CRC: {okCount}/{crcFields.Count} OK";
                }
                _crcStatusLabel.ForeColor = allOk ? Color.DarkGreen : Color.DarkRed;
            }
        }

        private void ResetModuleSummary()
        {
            if (_speedGradeLabel != null)  _speedGradeLabel.Text  = "Speed: —";
            if (_capacityLabel != null)    _capacityLabel.Text    = "Capacity: —";
            if (_busWidthLabel != null)    _busWidthLabel.Text    = "DRAM Bus Width: —";
            if (_crcStatusLabel != null)
            {
                _crcStatusLabel.Text = "CRC: —";
                _crcStatusLabel.ForeColor = SystemColors.ControlText;
            }
        }

        private static string FindField(SPDParsedFields.ParsedResult r, string label)
        {
            foreach (var f in r.Fields)
                if (f.Label == label) return f.Value;
            return null;
        }

        private static string ExtractParen(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            if (open >= 0 && close > open) return s.Substring(open + 1, close - open - 1);
            return s;
        }

        #endregion
    }

    /// <summary>
    /// §4: modal dialog showing JEDEC-decoded SPD fields with a "Recalc &amp; Fix CRC"
    /// button. Patches CRC in memory only - does not write to the DIMM (§4.4).
    /// </summary>
    internal class ParsedFieldsDialog : Form
    {
        public byte[] PatchedDump { get; private set; }

        private readonly byte[] _spd;
        private RichTextBox _viewer;
        private Button _recalcButton;
        private Button _closeButton;

        public ParsedFieldsDialog(byte[] spd)
        {
            _spd = spd;
            Text = "Parsed SPD Fields (JEDEC 21-C / JESD400-5D)";
            Size = new Size(820, 740);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(600, 500);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                Padding = new Padding(8)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

            _viewer = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.White,
                Font = new Font("Consolas", 9.5F),
                DetectUrls = false,
                WordWrap = false,
                // The DDR5 dump produces ~80+ rows now (up from ~25 before
                // the field-coverage expansion). Force scrollbars so a
                // small window still shows everything.
                ScrollBars = RichTextBoxScrollBars.Both
            };

            var buttonRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 6, 0, 0)
            };
            _closeButton = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Width = 100, Height = 32 };
            _recalcButton = new Button { Text = "Recalc && Fix CRC", Width = 160, Height = 32, Margin = new Padding(8, 0, 0, 0) };
            _recalcButton.Click += OnRecalcClick;
            buttonRow.Controls.Add(_closeButton);
            buttonRow.Controls.Add(_recalcButton);

            layout.Controls.Add(_viewer, 0, 0);
            layout.Controls.Add(buttonRow, 0, 1);
            Controls.Add(layout);

            Render(_spd);
        }

        private void OnRecalcClick(object sender, EventArgs e)
        {
            PatchedDump = SPDParsedFields.RecalcAndFixCrc(_spd);
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Render(byte[] spd)
        {
            var result = SPDParsedFields.Parse(spd);
            _viewer.Clear();
            _viewer.SelectionFont = new Font("Consolas", 11F, FontStyle.Bold);
            _viewer.AppendText($"DRAM Type: {result.DramType}\n");
            _viewer.SelectionFont = new Font("Consolas", 9.5F);
            _viewer.AppendText(new string('-', 70) + "\n");

            // Compute padding only over real value rows (skip section dividers
            // which carry empty values - their labels are decorative).
            int maxLabelLen = 0;
            foreach (var f in result.Fields)
                if (!string.IsNullOrEmpty(f.Value) && f.Label.Length > maxLabelLen)
                    maxLabelLen = f.Label.Length;

            foreach (var f in result.Fields)
            {
                // Section divider row from SPDParsedFields.Section() - render
                // as a coloured header without the trailing colon.
                if (string.IsNullOrEmpty(f.Value))
                {
                    _viewer.SelectionFont  = new Font("Consolas", 9.5F, FontStyle.Bold);
                    _viewer.SelectionColor = Color.FromArgb(0, 78, 152);
                    _viewer.AppendText($"\n{f.Label}\n");
                    _viewer.SelectionColor = Color.Black;
                    _viewer.SelectionFont  = new Font("Consolas", 9.5F);
                    continue;
                }

                if (f.IsCrcStatus)
                {
                    _viewer.SelectionColor = f.CrcOk ? Color.DarkGreen : Color.DarkRed;
                    _viewer.AppendText($"{f.Label.PadRight(maxLabelLen + 2)}: {(f.CrcOk ? "[OK]" : "[FAIL]")} {f.Value}\n");
                    _viewer.SelectionColor = Color.Black;
                }
                else
                {
                    _viewer.AppendText($"{f.Label.PadRight(maxLabelLen + 2)}: {f.Value}\n");
                }
            }
        }
    }
}