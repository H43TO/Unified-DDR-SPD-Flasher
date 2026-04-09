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
using SPDTool;

namespace UnifiedDDRSPDFlasher
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

        private Func<SPDToolDevice> _deviceProvider;
        private SPDToolDevice Device => _deviceProvider?.Invoke();

        private RichTextBox _hexViewer;
        private Button _openDumpButton;
        private Button _saveDumpButton;
        private ComboBox _moduleAddressCombo;
        private Label _detectedGenLabel;
        private Label _spdSizeLabel;
        private FlowLayoutPanel _writeProtectionPanel;
        private Button _readButton;
        private Button _verifyButton;
        private Button _writeAllButton;

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

            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(10)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43F));

            mainLayout.Controls.Add(CreateHexEditorPanel(), 0, 0);
            mainLayout.Controls.Add(CreateControlPanel(), 1, 0);

            this.Controls.Add(mainLayout);
        }

        private Panel CreateHexEditorPanel()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
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

            // ADDED: Improved hex viewer – Consolas 9.5pt, horizontal scrollable
            _hexViewer = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 16.5F),
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
                Text = "Module I2C Address:",
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

            TableLayoutPanel infoLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                Padding = new Padding(5)
            };
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 17F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 17F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 17F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 49F));

            _detectedGenLabel = new Label
            {
                Text = "Detected Generation: —",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(3),
                Height = 20
            };
            infoLayout.Controls.Add(_detectedGenLabel, 0, 0);

            _spdSizeLabel = new Label
            {
                Text = "SPD Size: —",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(3),
                Height = 20
            };
            infoLayout.Controls.Add(_spdSizeLabel, 0, 1);

            Label wpLabel = new Label
            {
                Text = "Write Protection (click block to toggle):",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(3),
                Height = 20
            };
            infoLayout.Controls.Add(wpLabel, 0, 2);

            _writeProtectionPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = true,
                Height = 36
            };
            infoLayout.Controls.Add(_writeProtectionPanel, 0, 3);

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
                RowCount = 2,
                Padding = new Padding(5)
            };
            dataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            dataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

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

            spdDataGroup.Controls.Add(dataLayout);
            layout.Controls.Add(spdDataGroup, 0, 2);

            panel.Controls.Add(layout);
            return panel;
        }

        #endregion

        #region Public Methods

        public void SetDeviceProvider(Func<SPDToolDevice> deviceProvider) =>
            _deviceProvider = deviceProvider;

        public void OnDeviceConnected(SPDToolDevice device)
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
            _writeProtectionPanel.Controls.Clear();
            _moduleAddressCombo.Items.Clear();
            _currentModuleInfo = null;
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
                _saveDumpButton.Enabled = true;
                _verifyButton.Enabled = true;
                _writeAllButton.Enabled = true;
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
            // Page 0 = bytes 0x000–0x07F, page 1 = 0x080–0x0FF, etc.
            int pagesNeeded = (totalSize + 127) / 128;

            for (byte page = 0; page < pagesNeeded; page++)
            {
                try
                {
                    // Switch to the correct page
                    if (!Device.WriteSPD5HubRegister(address, SPDToolDevice.MR11, page))
                        throw new Exception($"Failed to set page {page} via MR11");

                    System.Threading.Thread.Sleep(PAGE_SWITCH_DELAY_MS);

                    // Read 128 bytes from this page in READ_CHUNK_SIZE chunks.
                    // The firmware accepts linear offsets; we pass 0..127 relative to page start.
                    int bytesReadThisPage = 0;
                    while (bytesReadThisPage < 128 && allData.Count < totalSize)
                    {
                        byte chunkSize = (byte)Math.Min(READ_CHUNK_SIZE, 128 - bytesReadThisPage);
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
                    _writeAllButton.Text = $"Writing… {bytesWritten}/{totalBytes}";
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(WRITE_DELAY_MS);
                }

                string msg = errors == 0
                    ? $"Successfully wrote {totalBytes} bytes to SPD!"
                    : $"Write completed with {errors} error(s). Verify is recommended.";
                MessageBox.Show(msg, errors == 0 ? "Write Complete" : "Write Complete with Errors",
                    MessageBoxButtons.OK, errors == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Write failed: {ex.Message}");
                MessageBox.Show($"Write failed:\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                _writeAllButton.Text = "Write All";
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
                _saveDumpButton.Enabled = true;

                bool hasModule = _currentModuleInfo != null && _detectedAddresses.Count > 0;
                _verifyButton.Enabled = hasModule;
                _writeAllButton.Enabled = hasModule;

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
            _hexViewer.SelectionFont = new Font("Consolas", 13.5F, FontStyle.Bold);
            _hexViewer.SelectionColor = Color.FromArgb(0, 78, 152);
            _hexViewer.SelectionBackColor = Color.FromArgb(228, 236, 255);
            _hexViewer.AppendText("Offset    00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F   ASCII\n");
            _hexViewer.AppendText(new string('═', 71) + "\n");

            for (int i = 0; i < data.Length; i += 16)
            {
                Color bg = rowBg[(i / 16) % 2];
                int bytesInLine = Math.Min(16, data.Length - i);

                // Offset
                _hexViewer.SelectionFont = new Font("Consolas", 13.5F);
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
            _moduleAddressCombo.Enabled = hasModule;
        }

        #endregion
    }
}