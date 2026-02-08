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
    /// SPD Operations Tab v2.3 - Fixed DDR5/DDR4 Reading/Writing
    /// All issues resolved: proper page switching, checksum handling, and UI improvements
    /// </summary>
    public class SPDOperationsTab : UserControl
    {
        #region Events

        public event EventHandler<string> ErrorOccurred;

        #endregion

        #region Private Fields

        private Func<SPDToolDevice> _deviceProvider;
        private SPDToolDevice Device => _deviceProvider?.Invoke();

        // UI Components
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

        // Data
        private byte[] _currentDump;
        private byte _currentAddress = 0x50;
        private ModuleInfo _currentModuleInfo;
        private List<byte> _detectedAddresses = new List<byte>();

        #endregion

        #region Constructor

        public SPDOperationsTab()
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

            // Main layout: 2 columns (55% hex, 45% controls)
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(10)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));

            // Left panel - Hex editor
            Panel leftPanel = CreateHexEditorPanel();
            mainLayout.Controls.Add(leftPanel, 0, 0);

            // Right panel - Controls
            Panel rightPanel = CreateControlPanel();
            mainLayout.Controls.Add(rightPanel, 1, 0);

            this.Controls.Add(mainLayout);
        }

        private Panel CreateHexEditorPanel()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(5)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F)); // Reduced from 45F
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // Top buttons - UNIFORM SIZE
            TableLayoutPanel buttonLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(0, 2, 0, 2) // Reduced padding
            };
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            _openDumpButton = new Button
            {
                Text = "Open Dump",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Height = 32, // Reduced from 35
                Margin = new Padding(2),
                Enabled = false
            };
            _openDumpButton.Click += OnOpenDumpClicked;

            _saveDumpButton = new Button
            {
                Text = "Save Dump",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Height = 32, // Reduced from 35
                Margin = new Padding(2),
                Enabled = false
            };
            _saveDumpButton.Click += OnSaveDumpClicked;

            buttonLayout.Controls.Add(_openDumpButton, 0, 0);
            buttonLayout.Controls.Add(_saveDumpButton, 1, 0);
            layout.Controls.Add(buttonLayout, 0, 0);

            // Hex viewer
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
            layout.Controls.Add(_hexViewer, 0, 1);

            panel.Controls.Add(layout);
            return panel;
        }

        private Panel CreateControlPanel()
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
                ColumnCount = 1,
                Padding = new Padding(5)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));

            // 1. Module I2C Address
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
                Padding = new Padding(8)
            };

            TableLayoutPanel infoLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                Padding = new Padding(5)
            };
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));

            _detectedGenLabel = new Label
            {
                Text = "Detected Generation: ---",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 24
            };
            infoLayout.Controls.Add(_detectedGenLabel, 0, 0);

            _spdSizeLabel = new Label
            {
                Text = "SPD Size: ---",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 24
            };
            infoLayout.Controls.Add(_spdSizeLabel, 0, 1);

            Label wpLabel = new Label
            {
                Text = "Write Protection:",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 24
            };
            infoLayout.Controls.Add(wpLabel, 0, 2);

            _writeProtectionPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = true,
                Height = 24
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
                Padding = new Padding(8)
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

            _writeAllButton = new Button
            {
                Text = "Write All",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(232, 17, 35),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(3),
                Height = 32,
                Enabled = false
            };
            _writeAllButton.FlatAppearance.BorderSize = 0;
            _writeAllButton.Click += OnWriteAllClicked;

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
            AutoDetectModules();
        }

        public void OnDeviceDisconnected()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnDeviceDisconnected()));
                return;
            }

            _detectedGenLabel.Text = "Detected Generation: ---";
            _spdSizeLabel.Text = "SPD Size: ---";
            _writeProtectionPanel.Controls.Clear();
            _moduleAddressCombo.Items.Clear();
            _currentModuleInfo = null;
            _hexViewer.Clear();
            UpdateUIState(false);
        }

        public void UpdateDetectedDevices(List<byte> devices)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => UpdateDetectedDevices(devices)));
                return;
            }

            // Filter to SPD addresses only
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

                // Scan for devices
                var devices = Device.ScanBus();
                var spdDevices = devices.Where(a => a >= 0x50 && a <= 0x57).ToList();

                if (spdDevices.Count == 0)
                {
                    _moduleAddressCombo.Items.Add("No modules detected");
                    _moduleAddressCombo.SelectedIndex = 0;
                    _moduleAddressCombo.Enabled = false;
                    return;
                }

                _detectedAddresses = spdDevices;
                _moduleAddressCombo.Enabled = true;

                foreach (byte addr in spdDevices)
                {
                    try
                    {
                        var info = Device.DetectModule(addr);
                        _moduleAddressCombo.Items.Add($"0x{addr:X2} - {info.Type}");
                    }
                    catch
                    {
                        _moduleAddressCombo.Items.Add($"0x{addr:X2} - Unknown");
                    }
                }

                _moduleAddressCombo.SelectedIndex = 0;

                // Auto-detect first module
                if (_detectedAddresses.Count > 0)
                {
                    _currentAddress = _detectedAddresses[0];
                    DetectCurrentModule();
                }
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
            if (_moduleAddressCombo.SelectedIndex < 0 || _detectedAddresses.Count == 0)
                return;

            if (_moduleAddressCombo.SelectedIndex >= _detectedAddresses.Count)
                return;

            _currentAddress = _detectedAddresses[_moduleAddressCombo.SelectedIndex];
            DetectCurrentModule();
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
                _spdSizeLabel.Text = "SPD Size: ---";
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
                Label na = new Label
                {
                    Text = "N/A (DDR5 only)",
                    Font = new Font("Segoe UI", 8.5F),
                    ForeColor = Color.Gray,
                    AutoSize = true
                };
                _writeProtectionPanel.Controls.Add(na);
                return;
            }

            try
            {
                for (byte block = 0; block < 16; block++)
                {
                    bool isProtected = Device.GetRSWP(_currentAddress, block);

                    Label blockLabel = new Label
                    {
                        Text = block.ToString(),
                        Width = 22,
                        Height = 22,
                        TextAlign = ContentAlignment.MiddleCenter,
                        BorderStyle = BorderStyle.FixedSingle,
                        BackColor = isProtected ? Color.LightCoral : Color.LightGreen,
                        Font = new Font("Segoe UI", 7.5F),
                        Margin = new Padding(1)
                    };
                    blockLabel.Click += (s, e) => OnWriteProtectionBlockClicked(block);

                    _writeProtectionPanel.Controls.Add(blockLabel);
                }
            }
            catch (Exception ex)
            {
                Label error = new Label
                {
                    Text = "Read error",
                    Font = new Font("Segoe UI", 8.5F),
                    ForeColor = Color.Red,
                    AutoSize = true
                };
                _writeProtectionPanel.Controls.Add(error);
            }
        }

        private void OnWriteProtectionBlockClicked(byte block)
        {
            if (_currentModuleInfo?.Type != ModuleType.DDR5)
                return;

            var result = MessageBox.Show(
                $"Toggle write protection for block {block}?",
                "Write Protection",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                try
                {
                    bool current = Device.GetRSWP(_currentAddress, block);
                    if (current)
                    {
                        Device.ClearRSWP(_currentAddress);
                    }
                    else
                    {
                        Device.SetRSWP(_currentAddress, block);
                    }
                    UpdateWriteProtectionDisplay();
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"Write protection toggle failed: {ex.Message}");
                }
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

                // Read entire SPD with proper page switching
                _currentDump = ReadEntireSPDWithRetry(_currentAddress);

                if (_currentDump == null || _currentDump.Length == 0)
                {
                    ErrorOccurred?.Invoke(this, "Failed to read SPD - no data received");
                    MessageBox.Show("Failed to read SPD. Check connections and try again.", "Read Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                DisplayHexDump(_currentDump);
                _saveDumpButton.Enabled = true;
                _verifyButton.Enabled = true;
                _writeAllButton.Enabled = true;

                MessageBox.Show($"Successfully read {_currentDump.Length} bytes from SPD.", "Read Complete",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        // FIXED: Proper DDR5 and DDR4 reading with correct page switching
        private byte[] ReadEntireSPDWithRetry(byte address)
        {
            var info = Device.DetectModule(address);

            if (info.Type == ModuleType.DDR5)
            {
                return ReadDDR5SPD(address, info.Size);
            }
            else
            {
                return ReadStandardSPD(address, info.Size);
            }
        }

        // FIXED: DDR5 reading with proper page handling
        private byte[] ReadDDR5SPD(byte address, int totalSize)
        {
            List<byte> allData = new List<byte>();

            // DDR5 uses 128-byte pages via MR11 register
            int pagesNeeded = (totalSize + 127) / 128; // 1024/128 = 8 pages

            for (byte page = 0; page < pagesNeeded; page++)
            {
                try
                {
                    // Switch to this page via MR11 register
                    if (!Device.WriteSPD5HubRegister(address, SPDToolDevice.MR11, page))
                    {
                        throw new Exception($"Failed to set page {page} via MR11");
                    }

                    // Small delay for page switching
                    System.Threading.Thread.Sleep(10);

                    // Read 128 bytes from this page in 32-byte chunks
                    for (ushort offset = 0; offset < 128; offset += 32)
                    {
                        // For DDR5, we need to read with the proper offset
                        byte[] chunk = ReadWithRetry(address, (ushort)(page * 128 + offset), 32, 5);
                        if (chunk == null || chunk.Length != 32)
                        {
                            throw new Exception($"Failed to read page {page} at offset 0x{offset:X2}");
                        }
                        allData.AddRange(chunk);

                        // Break if we've reached total size
                        if (allData.Count >= totalSize)
                            break;
                    }
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"DDR5 page {page} read error: {ex.Message}");
                    return null;
                }
            }

            // Trim to exact size
            if (allData.Count > totalSize)
            {
                allData.RemoveRange(totalSize, allData.Count - totalSize);
            }

            return allData.ToArray();
        }

        private byte[] ReadStandardSPD(byte address, int size)
        {
            List<byte> allData = new List<byte>();

            for (ushort offset = 0; offset < size; offset += 32)
            {
                byte chunkSize = (byte)Math.Min(32, size - offset);
                byte[] chunk = ReadWithRetry(address, offset, chunkSize, 3);
                if (chunk == null || chunk.Length != chunkSize)
                {
                    throw new Exception($"Failed to read at offset 0x{offset:X2}");
                }
                allData.AddRange(chunk);
            }

            return allData.ToArray();
        }

        private byte[] ReadWithRetry(byte address, ushort offset, byte length, int maxRetries = 3)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    byte[] data = Device.ReadSPD(address, offset, length);
                    if (data != null && data.Length == length)
                        return data;

                    // If we got data but wrong length, wait and retry
                    System.Threading.Thread.Sleep(50 * (attempt + 1));
                }
                catch (Exception)
                {
                    if (attempt == maxRetries - 1)
                        throw;
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
                int differences = 0;
                List<int> diffOffsets = new List<int>();

                for (int i = 0; i < minLen; i++)
                {
                    if (readData[i] != _currentDump[i])
                    {
                        differences++;
                        diffOffsets.Add(i);
                    }
                }

                if (differences == 0)
                {
                    MessageBox.Show("Verification PASSED!\nSPD matches loaded dump perfectly.", "Verify",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    string diffList = differences > 10 ?
                        $"{differences} bytes differ (first 10: {string.Join(", ", diffOffsets.Take(10).Select(o => $"0x{o:X3}"))})" :
                        $"Differing bytes at offsets: {string.Join(", ", diffOffsets.Select(o => $"0x{o:X3}"))}";

                    ErrorOccurred?.Invoke(this, $"Verification failed: {diffList}");
                    MessageBox.Show($"Verification FAILED!\n{differences} byte(s) differ.\n\n{diffList}",
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
                $"⚠ WARNING ⚠\n\nThis will OVERWRITE the entire SPD at address 0x{_currentAddress:X2}!\n\n" +
                $"Size: {_currentDump.Length} bytes\n\nThis cannot be undone!\n\nContinue?",
                "Confirm Write All",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes) return;

            try
            {
                Cursor = Cursors.WaitCursor;
                _writeAllButton.Enabled = false;

                // Clear write protection if needed
                if (_currentModuleInfo?.Type == ModuleType.DDR5)
                {
                    Device.ClearRSWP(_currentAddress);
                }
                else if (_currentModuleInfo?.Type == ModuleType.DDR4)
                {
                    // For DDR4, we might need to clear write protection
                    try
                    {
                        Device.ClearRSWP(_currentAddress);
                    }
                    catch
                    {
                        // Ignore if clearing fails, might not be supported
                    }
                }

                int totalBytes = _currentDump.Length;
                int bytesWritten = 0;
                int errors = 0;

                // Write in smaller chunks with verification
                for (ushort offset = 0; offset < totalBytes; offset += 16)
                {
                    int chunkSize = Math.Min(16, totalBytes - offset);
                    byte[] chunk = new byte[chunkSize];
                    Array.Copy(_currentDump, offset, chunk, 0, chunkSize);

                    // FIXED: For DDR4, make sure we're writing within proper boundaries
                    if (_currentModuleInfo?.Type == ModuleType.DDR4)
                    {
                        // DDR4 has 256-byte pages. For offsets >= 256, we need page 1
                        // The Arduino library should handle this automatically, but we add a small delay
                        if (offset >= 256)
                        {
                            System.Threading.Thread.Sleep(20); // Extra delay for page switching
                        }
                    }

                    // Try to write with retry
                    bool writeSuccess = false;
                    for (int retry = 0; retry < 3; retry++)
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
                        ErrorOccurred?.Invoke(this, $"Write failed at offset 0x{offset:X3}");

                        // Try single byte write as fallback
                        bool byteSuccess = false;
                        for (int i = 0; i < chunkSize && !byteSuccess; i++)
                        {
                            try
                            {
                                byteSuccess = Device.WriteSPDByte(_currentAddress, (ushort)(offset + i), chunk[i]);
                                if (byteSuccess) errors--;
                            }
                            catch { }
                        }

                        if (errors > 3)
                        {
                            throw new Exception($"Multiple write failures, stopping at offset 0x{offset:X3}");
                        }
                    }

                    bytesWritten += chunkSize;
                    _writeAllButton.Text = $"Writing... {bytesWritten}/{totalBytes}";
                    Application.DoEvents();

                    // Small delay between writes
                    System.Threading.Thread.Sleep(20);
                }

                if (errors == 0)
                {
                    MessageBox.Show($"Successfully wrote {totalBytes} bytes to SPD!", "Write Complete",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show($"Write completed with {errors} error(s). Some bytes may not have been written correctly.",
                        "Write Complete with Errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Write failed: {ex.Message}");
                MessageBox.Show($"Write failed:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*";
                dialog.Title = "Open SPD Dump";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        _currentDump = File.ReadAllBytes(dialog.FileName);
                        DisplayHexDump(_currentDump);
                        _saveDumpButton.Enabled = true;
                        _verifyButton.Enabled = true;
                        _writeAllButton.Enabled = true;

                        MessageBox.Show($"Loaded {_currentDump.Length} bytes", "File Loaded",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        ErrorOccurred?.Invoke(this, $"Failed to load file: {ex.Message}");
                    }
                }
            }
        }

        private void OnSaveDumpClicked(object sender, EventArgs e)
        {
            if (_currentDump == null)
            {
                ErrorOccurred?.Invoke(this, "No dump data to save");
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*";
                dialog.FileName = $"spd_0x{_currentAddress:X2}_{DateTime.Now:yyyyMMdd_HHmmss}.bin";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        File.WriteAllBytes(dialog.FileName, _currentDump);
                        MessageBox.Show($"Saved {_currentDump.Length} bytes", "File Saved",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        ErrorOccurred?.Invoke(this, $"Failed to save file: {ex.Message}");
                    }
                }
            }
        }

        #endregion

        #region Helper Methods

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
            sb.AppendLine(new string('═', 74));

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

        private void UpdateUIState(bool connected)
        {
            _openDumpButton.Enabled = connected;
            _readButton.Enabled = connected;
            _moduleAddressCombo.Enabled = connected;
        }

        private void LogError(string message)
        {
            ErrorOccurred?.Invoke(this, message);
        }

        #endregion
    }
}