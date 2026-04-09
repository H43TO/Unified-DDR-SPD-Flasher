// ADDED: Full implementation of BurnBlock, GetPMICPGoodStatus, and helper wrappers.


using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SPDTool
{
    /// <summary>
    /// Communication library for the RP2040‑based SPD programmer – version 3.8.3
    /// Provides read/write access to SPD EEPROMs (DDR3/4/5) and PMIC registers.
    /// Firmware version expected:  20260307
    ///
    /// <para><b>Thread safety:</b> All serial I/O is guarded by <c>_serialLock</c>.
    /// Public methods may be called from worker threads; UI callbacks should marshal
    /// back to the UI thread themselves.</para>
    /// </summary>
    public class SPDToolDevice : IDisposable
    {
        #region Constants (matching firmware)

        // Basic commands
        private const byte CMD_GET = 0xFF;
        private const byte CMD_DISABLE = 0x00;
        private const byte CMD_ENABLE = 0x01;

        // Diagnostics & info
        private const byte CMD_VERSION = 0x02;
        private const byte CMD_TEST = 0x03;
        private const byte CMD_PING = 0x04;
        private const byte CMD_NAME = 0x05;
        private const byte CMD_FACTORY_RESET = 0x06;

        // SPD / I2C operations
        private const byte CMD_SPD_READ_PAGE = 0x07;
        private const byte CMD_SPD_WRITE_BYTE = 0x08;
        private const byte CMD_SPD_WRITE_PAGE = 0x09;
        private const byte CMD_SPD_WRITE_TEST = 0x0A;
        private const byte CMD_DDR4_DETECT = 0x0B;
        private const byte CMD_DDR5_DETECT = 0x0C;
        private const byte CMD_SPD5_HUB_REG = 0x0D;
        private const byte CMD_SPD_SIZE = 0x0E;
        private const byte CMD_SCAN_BUS = 0x0F;
        private const byte CMD_BUS_CLOCK = 0x10;
        private const byte CMD_PROBE_ADDRESS = 0x11;
        private const byte CMD_PMIC_WRITEREG = 0x12;
        private const byte CMD_PMIC_READREG = 0x13;
        private const byte CMD_PIN_CONTROL = 0x14;
        private const byte CMD_PIN_RESET = 0x15;
        private const byte CMD_RSWP = 0x16;
        private const byte CMD_PSWP = 0x17;
        private const byte CMD_RSWP_REPORT = 0x18;
        private const byte CMD_EEPROM = 0x19;
        private const byte CMD_PMIC_READADC = 0x1A;

        // Response markers
        private const byte RESPONSE_MARKER = 0x26; // '&'
        private const byte ALERT_MARKER = 0x40;    // '@'
        private const byte UNKNOWN_MARKER = 0x3F;  // '?'
        private const byte READY_MARKER = 0x21;    // '!'

        // Alert codes (extracted from '@' messages)
        private const byte ALERT_READY = 0x21;     // '!'
        private const byte ALERT_SLAVE_INC = 0x2B; // '+'
        private const byte ALERT_SLAVE_DEC = 0x2D; // '-'
        private const byte ALERT_CLOCK_INC = 0x2F; // '/'
        private const byte ALERT_CLOCK_DEC = 0x5C; // '\'

        // Pin identifiers (for CMD_PIN_CONTROL)
        /// <summary>Controls the opto-coupler enabling the HV programming signal for DDR3/4.</summary>
        public const byte PIN_HV_SWITCH = 0x00;
        /// <summary>SA1 address-select switch – selects between two SPD devices on some adapters.</summary>
        public const byte PIN_SA1_SWITCH = 0x01;
        /// <summary>Status LED connected to the programmer board.</summary>
        public const byte PIN_DEV_STATUS = 0x02;
        /// <summary>Enable pin of the HV boost converter used for DDR3/4 WP clearing.</summary>
        public const byte PIN_HV_CONVERTER = 0x03;
        /// <summary>DDR5 VIN power-supply enable. Must be high for DDR5 access.</summary>
        public const byte PIN_DDR5_VIN_CTRL = 0x04;
        /// <summary>PWR_EN line of the PMIC. Enables all PMIC phases when asserted.</summary>
        public const byte PIN_PMIC_CTRL = 0x05;
        /// <summary>Connected to the PMIC's PWR_GOOD output pin (input to the programmer).</summary>
        public const byte PIN_PMIC_FLAG = 0x06;
        /// <summary>Reserved for future use.</summary>
        public const byte PIN_RFU1 = 0x07;
        /// <summary>Reserved for future use.</summary>
        public const byte PIN_RFU2 = 0x08;

        // SPD5 hub registers
        public const byte MR0 = 0x00;
        public const byte MR1 = 0x01;
        public const byte MR6 = 0x06;
        public const byte MR11 = 0x0B;   // Page register
        public const byte MR12 = 0x0C;   // RSWP blocks 0-7
        public const byte MR13 = 0x0D;   // RSWP blocks 8-15
        public const byte MR14 = 0x0E;
        public const byte MR18 = 0x12;
        public const byte MR20 = 0x14;
        public const byte MR48 = 0x30;   // Status
        public const byte MR52 = 0x34;

        // I2C address ranges
        public const byte SPD_ADDRESS_MIN = 0x50;
        public const byte SPD_ADDRESS_MAX = 0x57;
        public const byte PMIC_ADDRESS_MIN = 0x48;
        public const byte PMIC_ADDRESS_MAX = 0x4F;

        // RAM type masks (RSWP report)
        public const byte RSWP_DDR5 = 0x20;
        public const byte RSWP_DDR4 = 0x10;
        public const byte RSWP_DDR3 = 0x08;

        // Size limits (firmware constraints)
        public const int MAX_READ_LENGTH = 64;
        public const int MAX_WRITE_LENGTH = 16;
        public const int MAX_DEVICE_NAME_LENGTH = 16;
        public const int MAX_EEPROM_READ = 32;

        // I2C clock modes
        public const byte I2C_CLOCK_100KHZ = 0;
        public const byte I2C_CLOCK_400KHZ = 1;
        public const byte I2C_CLOCK_1MHZ = 2;

        // ADDED: PMIC MTP burn timing constants (JEDEC PMIC5100 §17.3.3)
        private const int PMIC_BURN_WAIT_MS = 200;   // Time for MTP cell to program
        private const int PMIC_BURN_POLL_TIMEOUT_MS = 1000; // Max time polling for completion
        private const byte PMIC_BURN_COMPLETE_TOKEN = 0x5A; // Register 0x39 value when done

        // ADDED: PMIC vendor region unlock password (JEDEC default – §17.3.1)
        private const byte PMIC_PASS_LSB_DEFAULT = 0x73;
        private const byte PMIC_PASS_MSB_DEFAULT = 0x94;

        // ADDED: PMIC register map (JEDEC PMIC5100)
        private const byte PMIC_REG_PASS_LSB = 0x37;  // Password LSB
        private const byte PMIC_REG_PASS_MSB = 0x38;  // Password MSB
        private const byte PMIC_REG_PASS_CTRL = 0x39; // Password control / burn command
        private const byte PMIC_REG_VENDOR_START = 0x40; // First vendor MTP register
        private const byte PMIC_REG_PG_STATUS = 0x08; // Power-good status (SWA/SWB/SWC)
        private const byte PMIC_REG_PG_STATUS2 = 0x09; // Power-good status (VOUT_1.8V)
        private const byte PMIC_REG_VR_CTRL = 0x32;   // VR enable / PWR_GOOD control
        private const byte PMIC_REG_PG_VOUT1V = 0x33; // VOUT_1.0V power-good

        #endregion

        #region Fields

        private readonly SerialPort _serialPort;
        private readonly object _serialLock = new object();
        private bool _disposed;

        #endregion

        #region Events

        /// <summary>Raised when an asynchronous alert is received from the device.</summary>
        public event EventHandler<AlertEventArgs> AlertReceived;

        #endregion

        #region Constructor & Dispose

        /// <summary>
        /// Initialises a new instance and opens the specified serial port.
        /// </summary>
        /// <param name="portName">Name of the serial port (e.g., "COM3").</param>
        /// <param name="baudRate">Baud rate; must match the firmware (default 115200).</param>
        public SPDToolDevice(string portName, int baudRate = 115200)
        {
            _serialPort = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 2000,
                WriteTimeout = 2000,
                DtrEnable = true,
                RtsEnable = true
            };

            _serialPort.Open();

            // Allow the RP2040 to boot and initialise
            Thread.Sleep(2500);

            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // Attempt to close the port safely with a timeout
                if (_serialPort != null)
                {
                    try
                    {
                        // Run Close/Dispose on a background thread to avoid UI freeze
                        var closeTask = Task.Run(() =>
                        {
                            try
                            {
                                _serialPort.Close();
                                _serialPort.Dispose();
                            }
                            catch
                            {
                                // Ignore exceptions during forced close
                            }
                        });

                        // Wait at most 0.25 seconds for the close to complete
                        if (!closeTask.Wait(TimeSpan.FromMilliseconds(250)))
                        {
                            // Timed out – the driver is hung. We abandon the port.
                            // The application will continue without freezing.
                            System.Diagnostics.Debug.WriteLine("Serial port close timed out – port abandoned.");
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but don't rethrow
                        System.Diagnostics.Debug.WriteLine($"Error disposing serial port: {ex.Message}");
                    }
                    finally
                    {
                        _disposed = true;
                    }
                }
            }
        }

        #endregion

        #region Low‑level communication

        /// <summary>
        /// Sends a command byte followed by optional parameters.
        /// </summary>
        private void SendCommand(byte command, params byte[] parameters)
        {
            lock (_serialLock)
            {
                _serialPort.DiscardInBuffer();

                var buffer = new byte[1 + parameters.Length];
                buffer[0] = command;
                if (parameters.Length > 0)
                    Array.Copy(parameters, 0, buffer, 1, parameters.Length);

                _serialPort.Write(buffer, 0, buffer.Length);
            }
        }

        /// <summary>
        /// Reads a framed response from the device.
        /// </summary>
        /// <param name="timeoutMs">Total time to wait for a response.</param>
        /// <returns>Response payload (data bytes only, checksum removed).</returns>
        /// <exception cref="TimeoutException">No valid response within timeout.</exception>
        /// <exception cref="InvalidOperationException">Checksum mismatch or hardware error.</exception>
        private byte[] ReadResponse(int timeoutMs = 2000)
        {
            lock (_serialLock)
            {
                var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

                while (DateTime.Now < deadline)
                {
                    if (_serialPort.BytesToRead == 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    var marker = (byte)_serialPort.ReadByte();

                    switch (marker)
                    {
                        case RESPONSE_MARKER:
                            return ReadFramedResponse();

                        case ALERT_MARKER:
                            ReadAndRaiseAlert();
                            continue; // still waiting for the actual response

                        case READY_MARKER:
                            return new[] { READY_MARKER };

                        case UNKNOWN_MARKER:
                            throw new InvalidOperationException("Device reports unsupported hardware.");

                        default:
                            // Unexpected byte – discard and continue
                            continue;
                    }
                }

                throw new TimeoutException($"No valid response received in {timeoutMs} ms.");
            }
        }

        /// <summary>
        /// Reads a complete response frame: length, data, checksum.
        /// </summary>
        private byte[] ReadFramedResponse()
        {
            // Length byte
            var length = ReadByteWithTimeout(50, "length");
            if (length == 0)
                return Array.Empty<byte>();

            // Data bytes
            var data = new byte[length];
            var totalRead = 0;
            var dataDeadline = DateTime.Now.AddMilliseconds(100);

            while (totalRead < length && DateTime.Now < dataDeadline)
            {
                if (_serialPort.BytesToRead > 0)
                    totalRead += _serialPort.Read(data, totalRead, length - totalRead);
                else
                    Thread.Sleep(1);
            }

            if (totalRead < length)
                throw new TimeoutException($"Incomplete data: got {totalRead} of {length} bytes.");

            // Checksum
            var receivedChecksum = ReadByteWithTimeout(50, "checksum");
            var calculated = (byte)data.Sum(b => b);

            if (calculated != receivedChecksum)
                throw new InvalidOperationException(
                    $"Checksum mismatch: device 0x{receivedChecksum:X2}, calculated 0x{calculated:X2}.");

            return data;
        }

        /// <summary>
        /// Reads a single byte with a short timeout.
        /// </summary>
        private byte ReadByteWithTimeout(int timeoutMs, string context)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                if (_serialPort.BytesToRead > 0)
                    return (byte)_serialPort.ReadByte();

                Thread.Sleep(1);
            }

            throw new TimeoutException($"Expected {context} byte not received.");
        }

        /// <summary>
        /// Reads an alert code and raises the AlertReceived event.
        /// </summary>
        private void ReadAndRaiseAlert()
        {
            try
            {
                var alertCode = ReadByteWithTimeout(50, "alert code");
                OnAlertReceived(new AlertEventArgs(alertCode));
            }
            catch (TimeoutException)
            {
                // Alert without a code is ignored
            }
        }

        protected virtual void OnAlertReceived(AlertEventArgs e) =>
            AlertReceived?.Invoke(this, e);

        #endregion

        #region Basic device commands

        /// <summary>Checks if the device is responding.</summary>
        public bool Ping()
        {
            SendCommand(CMD_PING);
            var response = ReadResponse(500);
            return response?.Length > 0 && response[0] == READY_MARKER;
        }

        /// <summary>Retrieves the firmware version as a 32‑bit integer (e.g., 20260307).</summary>
        public uint GetVersion()
        {
            SendCommand(CMD_VERSION);
            var response = ReadResponse();

            if (response?.Length != 4)
                throw new InvalidOperationException($"Version response invalid: {response?.Length ?? 0} bytes.");

            return BitConverter.ToUInt32(response, 0);
        }

        /// <summary>Runs a simple communication test.</summary>
        public bool Test()
        {
            SendCommand(CMD_TEST);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Gets the user‑defined device name (max 16 ASCII chars).</summary>
        public string GetDeviceName()
        {
            SendCommand(CMD_NAME, CMD_GET);
            var response = ReadResponse();

            if (response == null || response.Length == 0)
                return string.Empty;

            // Trim trailing nulls (The uC may send full 16 bytes)
            var length = Array.IndexOf(response, (byte)0);
            if (length < 0) length = response.Length;

            return Encoding.ASCII.GetString(response, 0, length);
        }

        /// <summary>Sets the device name.</summary>
        /// <param name="name">Up to 16 ASCII characters.</param>
        /// <returns>True if successful.</returns>
        public bool SetDeviceName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Name cannot be null or empty.", nameof(name));

            if (name.Length > MAX_DEVICE_NAME_LENGTH)
                throw new ArgumentException($"Name must be ≤ {MAX_DEVICE_NAME_LENGTH} characters.", nameof(name));

            var nameBytes = Encoding.ASCII.GetBytes(name);
            var parameters = new byte[1 + nameBytes.Length];
            parameters[0] = (byte)nameBytes.Length;
            Array.Copy(nameBytes, 0, parameters, 1, nameBytes.Length);

            SendCommand(CMD_NAME, parameters);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Restores factory defaults (clears settings).</summary>
        public bool FactoryReset()
        {
            SendCommand(CMD_FACTORY_RESET);
            var response = ReadResponse(3000);
            return response?.Length > 0 && response[0] == 1;
        }

        #endregion

        #region I2C bus commands

        /// <summary>Scans the I2C bus for SPD devices (addresses 0x50‑0x57).</summary>
        /// <returns>List of detected 7‑bit addresses.</returns>
        public List<byte> ScanBus()
        {
            SendCommand(CMD_SCAN_BUS);
            var response = ReadResponse();

            var devices = new List<byte>();
            if (response?.Length > 0)
            {
                var mask = response[0];
                for (var i = 0; i < 8; i++)
                {
                    if ((mask & (1 << i)) != 0)
                        devices.Add((byte)(SPD_ADDRESS_MIN + i));
                }
            }
            return devices;
        }

        /// <summary>Checks whether a specific I2C address responds.</summary>
        /// <param name="address">7‑bit address (0x00‑0x7F).</param>
        public bool ProbeAddress(byte address)
        {
            SendCommand(CMD_PROBE_ADDRESS, address);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Gets the current I2C bus clock mode.</summary>
        /// <returns>0=100kHz, 1=400kHz, 2=1MHz.</returns>
        public byte GetI2CClockMode()
        {
            SendCommand(CMD_BUS_CLOCK, CMD_GET);
            var response = ReadResponse();

            if (response == null || response.Length == 0)
                throw new InvalidOperationException("Failed to get I2C clock mode.");

            return response[0];
        }

        /// <summary>Sets the I2C bus clock mode.</summary>
        /// <param name="mode">0=100kHz, 1=400kHz, 2=1MHz.</param>
        public bool SetI2CClockMode(byte mode)
        {
            if (mode > I2C_CLOCK_1MHZ)
                throw new ArgumentException($"Mode must be {I2C_CLOCK_100KHZ}, {I2C_CLOCK_400KHZ}, or {I2C_CLOCK_1MHZ}.", nameof(mode));

            SendCommand(CMD_BUS_CLOCK, mode);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        #endregion

        #region SPD reading

        /// <summary>
        /// Reads a block of up to 64 bytes from an SPD EEPROM.
        /// Automatically handles paging for DDR4/5 if the address is in SPD range.
        /// </summary>
        /// <param name="address">I2C address (0x50‑0x57).</param>
        /// <param name="offset">Byte offset (0‑1023).</param>
        /// <param name="length">Number of bytes to read (1‑64).</param>
        /// <returns>Data read, or null on I2C error.</returns>
        public byte[] ReadSPD(byte address, ushort offset, byte length)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be 0x{SPD_ADDRESS_MIN:X2}‑0x{SPD_ADDRESS_MAX:X2}.", nameof(address));

            ValidateReadParams(offset, length);

            var offsetLow = (byte)(offset & 0xFF);
            var offsetHigh = (byte)((offset >> 8) & 0xFF);
            SendCommand(CMD_SPD_READ_PAGE, address, offsetLow, offsetHigh, length);

            var response = ReadResponse();
            return IsErrorResponse(response) ? null : response;
        }

        /// <summary>
        /// Reads bytes from any I2C device using the SPD read command.
        /// For PMIC devices, consider using <see cref="ReadPMICDevice"/> instead.
        /// </summary>
        public byte[] ReadI2CDevice(byte address, ushort offset, byte length)
        {
            ValidateReadParams(offset, length);

            var offsetLow = (byte)(offset & 0xFF);
            var offsetHigh = (byte)((offset >> 8) & 0xFF);
            SendCommand(CMD_SPD_READ_PAGE, address, offsetLow, offsetHigh, length);

            var response = ReadResponse();
            return IsErrorResponse(response) ? null : response;
        }

        /// <summary>Reads the complete SPD EEPROM (auto‑detects size).</summary>
        /// <param name="address">I2C address (0x50‑0x57).</param>
        /// <returns>Full SPD data, or null if detection fails.</returns>
        public byte[] ReadEntireSPD(byte address)
        {
            var size = GetSPDSizeBytes(address);
            if (size == 0)
                return null;

            var data = new List<byte>(size);
            var isDdr5 = DetectDDR5(address);
            var isDdr4 = DetectDDR4(address);

            // Number of 256‑byte pages
            var pages = isDdr5 ? 4 : (isDdr4 ? 2 : 1);

            for (var page = 0; page < pages; page++)
            {
                var pageBase = (ushort)(page * 256);
                for (ushort offset = 0; offset < 256; offset += MAX_READ_LENGTH)
                {
                    var chunkSize = (byte)Math.Min(MAX_READ_LENGTH, 256 - offset);
                    var chunk = ReadSPD(address, (ushort)(pageBase + offset), chunkSize);

                    if (chunk == null || chunk.Length != chunkSize)
                        throw new InvalidOperationException($"Read failed at page {page}, offset {offset}.");

                    data.AddRange(chunk);
                }
            }

            // Trim if size is not an exact multiple of 256 (unlikely)
            if (data.Count > size)
                data.RemoveRange(size, data.Count - size);

            return data.ToArray();
        }

        #endregion

        #region SPD writing

        /// <summary>Writes a single byte to an SPD EEPROM.</summary>
        public bool WriteSPDByte(byte address, ushort offset, byte value)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be 0x{SPD_ADDRESS_MIN:X2}‑0x{SPD_ADDRESS_MAX:X2}.", nameof(address));

            if (offset > 1023)
                throw new ArgumentException("Offset must be 0‑1023.", nameof(offset));

            var offsetLow = (byte)(offset & 0xFF);
            var offsetHigh = (byte)((offset >> 8) & 0xFF);
            SendCommand(CMD_SPD_WRITE_BYTE, address, offsetLow, offsetHigh, value);

            var response = ReadResponse(1500);
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Writes a single byte to any I2C device (uses PMIC write command).</summary>
        public bool WriteI2CDevice(byte address, ushort offset, byte value)
        {
            if (!IsValidPMICAddress(address) && !IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be in PMIC or SPD range.", nameof(address));

            if (offset > 255)
                throw new ArgumentException("Offset must be 0‑255 for this command.", nameof(offset));

            SendCommand(CMD_PMIC_WRITEREG, address, (byte)offset, value);
            var response = ReadResponse(1500);
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Writes up to 16 bytes to an SPD EEPROM without crossing a page boundary.</summary>
        public bool WriteSPDPage(byte address, ushort offset, byte[] data)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be 0x{SPD_ADDRESS_MIN:X2}‑0x{SPD_ADDRESS_MAX:X2}.", nameof(address));

            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty.", nameof(data));

            if (data.Length > MAX_WRITE_LENGTH)
                throw new ArgumentException($"Max write length is {MAX_WRITE_LENGTH}.", nameof(data));

            if ((offset % 16) + data.Length > 16)
                throw new ArgumentException("Write would cross a 16‑byte page boundary.", nameof(data));

            if (offset > 1023)
                throw new ArgumentException("Offset must be 0‑1023.", nameof(offset));

            var offsetLow = (byte)(offset & 0xFF);
            var offsetHigh = (byte)((offset >> 8) & 0xFF);
            var parameters = new byte[4 + data.Length];
            parameters[0] = address;
            parameters[1] = offsetLow;
            parameters[2] = offsetHigh;
            parameters[3] = (byte)data.Length;
            Array.Copy(data, 0, parameters, 4, data.Length);

            SendCommand(CMD_SPD_WRITE_PAGE, parameters);
            var response = ReadResponse(2000);
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Writes a complete SPD image, handling paging and write protection.</summary>
        public bool WriteEntireSPD(byte address, byte[] data)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be 0x{SPD_ADDRESS_MIN:X2}‑0x{SPD_ADDRESS_MAX:X2}.", nameof(address));

            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty.", nameof(data));

            var expectedSize = GetSPDSizeBytes(address);
            if (expectedSize == 0 || data.Length != expectedSize)
                throw new ArgumentException($"Data size {data.Length} does not match SPD size {expectedSize}.", nameof(data));

            // Clear reversible write protection if present (DDR4/5)
            if (DetectDDR4(address) || DetectDDR5(address))
                ClearRSWP(address);

            // Write in 16‑byte chunks, respecting page boundaries
            for (ushort offset = 0; offset < data.Length; offset += MAX_WRITE_LENGTH)
            {
                var chunkSize = Math.Min(MAX_WRITE_LENGTH, data.Length - offset);
                var chunk = new byte[chunkSize];
                Array.Copy(data, offset, chunk, 0, chunkSize);

                if (!WriteSPDPage(address, offset, chunk))
                    throw new InvalidOperationException($"Write failed at offset {offset}.");

                Thread.Sleep(20); // EEPROM write cycle delay
            }

            return true;
        }

        /// <summary>Tests write capability at a given offset (invert/restore).</summary>
        public bool TestWrite(byte address, ushort offset)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be 0x{SPD_ADDRESS_MIN:X2}‑0x{SPD_ADDRESS_MAX:X2}.", nameof(address));

            var offsetLow = (byte)(offset & 0xFF);
            var offsetHigh = (byte)((offset >> 8) & 0xFF);
            SendCommand(CMD_SPD_WRITE_TEST, address, offsetLow, offsetHigh);
            var response = ReadResponse(2000);
            return response?.Length > 0 && response[0] == 1;
        }

        #endregion

        #region Detection commands

        /// <summary>Returns true if a DDR4 module is detected at the address.</summary>
        public bool DetectDDR4(byte address)
        {
            SendCommand(CMD_DDR4_DETECT, address);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Returns true if a DDR5 module is detected at the address.</summary>
        public bool DetectDDR5(byte address)
        {
            SendCommand(CMD_DDR5_DETECT, address);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Gets the SPD size code (1=256, 2=512, 3=1024 bytes).</summary>
        public byte GetSPDSizeCode(byte address)
        {
            SendCommand(CMD_SPD_SIZE, address);
            var response = ReadResponse();
            return response?[0] ?? 0;
        }

        /// <summary>Gets the SPD size in bytes (256, 512, 1024) or 0 if unknown.</summary>
        public int GetSPDSizeBytes(byte address) =>
            GetSPDSizeCode(address) switch
            {
                1 => 256,
                2 => 512,
                3 => 1024,
                _ => 0
            };

        /// <summary>Detects module type and size at a given address.</summary>
        public ModuleInfo DetectModule(byte address)
        {
            var info = new ModuleInfo { Address = address };

            if (DetectDDR5(address))
            {
                info.Type = ModuleType.DDR5;
                info.Size = 1024;
            }
            else if (DetectDDR4(address))
            {
                info.Type = ModuleType.DDR4;
                info.Size = 512;
            }
            else if (ProbeAddress(address))
            {
                info.Type = ModuleType.DDR3_Or_Other;
                info.Size = GetSPDSizeBytes(address);
            }
            else
            {
                info.Type = ModuleType.NotDetected;
            }

            return info;
        }

        #endregion

        #region SPD5 hub registers

        /// <summary>Reads a management register from a DDR5 SPD hub.</summary>
        /// <param name="address">I2C address (0x50‑0x57).</param>
        /// <param name="register">Register number (0‑127).</param>
        /// <returns>Register value, or 0xFF on error.</returns>
        public byte ReadSPD5HubRegister(byte address, byte register)
        {
            if (register >= 128)
                throw new ArgumentException("Register must be 0‑127.", nameof(register));

            SendCommand(CMD_SPD5_HUB_REG, address, register, CMD_GET);
            var response = ReadResponse();
            return response?[0] ?? 0xFF;
        }

        /// <summary>Writes to a writable SPD5 hub register (MR11‑MR13).</summary>
        public bool WriteSPD5HubRegister(byte address, byte register, byte value)
        {
            if (register >= 128)
                throw new ArgumentException("Register must be 0‑127.", nameof(register));

            if (register != MR11 && register != MR12 && register != MR13)
                throw new ArgumentException("Only MR11, MR12, MR13 are writable.", nameof(register));

            SendCommand(CMD_SPD5_HUB_REG, address, register, CMD_ENABLE, value);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        #endregion

        #region Write protection

        /// <summary>Sets reversible software write protection for a block.</summary>
        /// <param name="address">SPD address (0x50‑0x57).</param>
        /// <param name="block">Block number (0‑15 for DDR5, 0‑3 for DDR3/4).</param>
        public bool SetRSWP(byte address, byte block)
        {
            if (block > 15)
                throw new ArgumentException("Block must be 0‑15.", nameof(block));

            SendCommand(CMD_RSWP, address, block, CMD_ENABLE);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Clears all reversible software write protection.</summary>
        public bool ClearRSWP(byte address)
        {
            SendCommand(CMD_RSWP, address, 0x00, CMD_DISABLE);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Checks if a specific block is write‑protected.</summary>
        public bool GetRSWP(byte address, byte block)
        {
            if (block > 15)
                throw new ArgumentException("Block must be 0‑15.", nameof(block));

            SendCommand(CMD_RSWP, address, block, CMD_GET);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Reports which RAM types support reversible write protection.</summary>
        public RSWPSupport GetRSWPSupport()
        {
            SendCommand(CMD_RSWP_REPORT);
            var response = ReadResponse();

            var support = new RSWPSupport();
            if (response?.Length > 0)
            {
                var mask = response[0];
                support.DDR5Supported = (mask & RSWP_DDR5) != 0;
                support.DDR4Supported = (mask & RSWP_DDR4) != 0;
                support.DDR3Supported = (mask & RSWP_DDR3) != 0;
            }
            return support;
        }

        /// <summary>
        /// Activates permanent software write protection.
        /// <para><b>WARNING:</b> This is IRREVERSIBLE. Does not work on DDR4/5 modules.</para>
        /// </summary>
        public bool SetPSWP(byte address)
        {
            SendCommand(CMD_PSWP, address, CMD_ENABLE);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Checks if permanent write protection is active.</summary>
        public bool GetPSWP(byte address)
        {
            SendCommand(CMD_PSWP, address, CMD_GET);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        #endregion

        #region PMIC commands

        /// <summary>Reads a single register from a PMIC (dedicated command).</summary>
        /// <param name="address">I2C address (any 7‑bit).</param>
        /// <param name="offset">Register offset (0‑1023).</param>
        /// <returns>Single‑byte array with the value, or null on error.</returns>
        public byte[] ReadPMICDevice(byte address, ushort offset)
        {
            if (offset > 1023)
                throw new ArgumentException("Offset must be 0‑1023.", nameof(offset));

            SendCommand(CMD_PMIC_READREG, address, (byte)(offset & 0xFF));
            var response = ReadResponse();
            return IsErrorResponse(response) ? null : response;
        }

        /// <summary>
        /// Reads multiple ADC channels from a PMIC.
        /// </summary>
        /// <param name="pmicAddress">I2C address of the PMIC.</param>
        /// <param name="writeOffset">Register to write channel selections (e.g., 0x30).</param>
        /// <param name="writeValues">Array of values to write (one per channel).</param>
        /// <param name="readOffset">Register to read ADC results from (e.g., 0x31).</param>
        /// <returns>Array of read bytes (same length as writeValues) or null on error.</returns>
        public byte[] ReadPMICADC(byte pmicAddress, byte writeOffset, byte[] writeValues, byte readOffset)
        {
            if (writeValues == null || writeValues.Length == 0)
                throw new ArgumentException("writeValues cannot be null or empty.", nameof(writeValues));

            var parameters = new byte[3 + writeValues.Length + 1];
            parameters[0] = pmicAddress;
            parameters[1] = writeOffset;
            parameters[2] = (byte)writeValues.Length;
            Array.Copy(writeValues, 0, parameters, 3, writeValues.Length);
            parameters[3 + writeValues.Length] = readOffset;

            SendCommand(CMD_PMIC_READADC, parameters);
            var response = ReadResponse(500);
            return IsErrorResponse(response) ? null : response;
        }

        /// <summary>
        /// Unlocks PMIC vendor-region access using the supplied password bytes.
        /// Writes the password to registers 0x37/0x38, then writes 0x40 to 0x39.
        /// This corresponds to JEDEC PMIC5100 §17.3.1.
        /// </summary>
        /// <param name="pmicAddress">I2C address of the PMIC.</param>
        /// <param name="passLsb">Password LSB (default JEDEC: 0x73).</param>
        /// <param name="passMsb">Password MSB (default JEDEC: 0x94).</param>
        /// <returns><c>true</c> if the vendor region appears unlocked after the sequence.</returns>
        public bool UnlockVendorRegion(byte pmicAddress,
            byte passLsb = PMIC_PASS_LSB_DEFAULT,
            byte passMsb = PMIC_PASS_MSB_DEFAULT)
        {
            if (!SmartEnableProgrammableMode(pmicAddress)) { return false; }

            // Write password LSB, MSB, then the unlock command 0x40
            WriteI2CDevice(pmicAddress, PMIC_REG_PASS_LSB, passLsb);
            WriteI2CDevice(pmicAddress, PMIC_REG_PASS_MSB, passMsb);
            WriteI2CDevice(pmicAddress, PMIC_REG_PASS_CTRL, 0x40);

            // A non-zero read from 0x45 indicates vendor-region access is active.
            var stat = ReadPMICDevice(pmicAddress, 0x45);
            return stat != null && stat.Length > 0 && stat[0] != 0;
        }

        /// <summary>
        /// Locks the PMIC vendor region by writing 0x00 to register 0x39.
        /// Call this after any MTP burn operation is complete.
        /// </summary>
        /// <param name="pmicAddress">I2C address of the PMIC.</param>
        /// <returns><c>true</c> if the write succeeded.</returns>
        public bool LockVendorRegion(byte pmicAddress)
        {
            return WriteI2CDevice(pmicAddress, PMIC_REG_PASS_CTRL, 0x00);
        }

        /// <summary>
        /// Changes the PMIC vendor-region access password (JEDEC PMIC5100 §17.3.2).
        /// Requires the vendor region to already be unlocked with the current password.
        /// </summary>
        /// <param name="pmicAddress">I2C address of the PMIC.</param>
        /// <param name="currentLsb">Current password LSB.</param>
        /// <param name="currentMsb">Current password MSB.</param>
        /// <param name="newLsb">New password LSB.</param>
        /// <param name="newMsb">New password MSB.</param>
        /// <returns><c>true</c> if the password was changed successfully.</returns>
        public bool ChangePMICPassword(byte pmicAddress,
            byte currentLsb, byte currentMsb,
            byte newLsb, byte newMsb)
        {
            // Step 1 – unlock with current password
            if (!UnlockVendorRegion(pmicAddress, currentLsb, currentMsb))
                return false;

            // Step 2 – write new password into the protected password registers (0x37/0x38).
            // Per JEDEC, the new password must be written while the old unlock is active.
            WriteI2CDevice(pmicAddress, PMIC_REG_PASS_LSB, newLsb);
            WriteI2CDevice(pmicAddress, PMIC_REG_PASS_MSB, newMsb);

            // Step 3 – commit: send burn command for the password registers (0x84 per §17.3.2).
            WriteI2CDevice(pmicAddress, PMIC_REG_PASS_CTRL, 0x84);
            Thread.Sleep(PMIC_BURN_WAIT_MS);

            // Step 4 – lock
            LockVendorRegion(pmicAddress);

            // Verify the new password works
            return UnlockVendorRegion(pmicAddress, newLsb, newMsb);
        }

        /// <summary>
        /// Enables programmable mode on PMICs that ship locked by default.
        /// Per JEDEC PMIC5100 §17.2, this sets bit 2 of register 0x2F (PROG_MODE_EN).
        /// Requires vendor-region access to be active.
        /// </summary>
        /// <param name="pmicAddress">I2C address of the PMIC.</param>
        /// <returns><c>true</c> if programmable mode was confirmed active.</returns>
        public bool EnableProgrammableMode(byte pmicAddress)
        {
            // Read current value of 0x2F
            var reg = ReadPMICDevice(pmicAddress, 0x2F);
            if (reg == null || reg.Length == 0)
                return false;

            // Set bit 2 (PROG_MODE_EN)
            byte newVal = (byte)(reg[0] | 0x04);
            if (!WriteI2CDevice(pmicAddress, 0x2F, newVal))
                return false;

            // Verify
            var verify = ReadPMICDevice(pmicAddress, 0x2F);
            return verify != null && verify.Length > 0 && (verify[0] & 0x04) != 0;
        }

        /// <summary>Attempts to unlock full access to a protected PMIC (vendor‑specific).</summary>
        public bool EnableFullAccess(byte pmicAddress)
        {
            const byte Status_ofst = 0x5E;

            var status = ReadPMICDevice(pmicAddress, Status_ofst);
            if (status == null || status.Length == 0)
                return false;

            // If already unlocked, return true
            if (status[0] != 0)
                return true;

            return UnlockVendorRegion(pmicAddress);
        }

        /// <summary>Identifies the PMIC type (e.g., "PMIC5100", "PMIC5000") based on registers.</summary>
        public string GetPMICType(byte pmicAddress)
        {
            var reg3B = ReadPMICDevice(pmicAddress, 0x3B);
            var reg23 = ReadPMICDevice(pmicAddress, 0x23);

            if (reg3B == null || reg3B.Length == 0 || reg23 == null || reg23.Length == 0)
                return "Unknown";

            // reg23 == 0 indicates a 3‑rail PMIC (UDIMM/SODIMM family)
            if (reg23[0] == 0)
                return (reg3B[0] & 0x40) == 0 ? "PMIC5100" : "PMIC5120";

            // 4‑rail PMIC (RDIMM/LRDIMM/CAMM2 family)
            var code = ((reg3B[0] >> 6) & 0x03) * 2 + (reg3B[0] & 0x01);
            return code switch
            {
                0 => "PMIC5010",
                1 => IsPMIC5200(pmicAddress) ? "PMIC5200" : "PMIC5000",
                2 or 3 => "PMIC5020",
                4 or 5 => "PMIC5030",
                _ => "Unknown"
            };
        }

        /// Helper to distinguish PMIC5000 vs PMIC5200 based on a specific register bit.
        private bool IsPMIC5200(byte pmicAddress)
        {
            var reg3E = ReadPMICDevice(pmicAddress, 0x3E);
            return reg3E != null && reg3E.Length > 0 && reg3E[0] != 0;
        }

        /// <summary>Determines the programming mode of a PMIC.</summary>
        /// <returns>"Manufacturer Access", "Programmable", "Locked", or "ERROR".</returns>
        public string GetPMICMode(byte pmicAddress)
        {
            try
            {
                var reg2F = ReadPMICDevice(pmicAddress, 0x2F);
                if (reg2F == null || reg2F.Length == 0)
                    return "ERROR";

                var isProgrammable = (reg2F[0] & 0x04) != 0; // bit 2

                var stat = ReadPMICDevice(pmicAddress, 0x45);
                var vendorUnlocked = stat != null && stat.Length > 0 && stat[0] != 0;

                return vendorUnlocked ? "Manufacturer Access"
                     : isProgrammable ? "Programmable"
                     : "Locked";
            }
            catch
            {
                return "ERROR";
            }
        }

        /// <summary>Power-cycles the DIMM by toggling the DDR5_VIN_CTRL pin.</summary>
        public bool RebootDIMM()
        {
            try
            {
                if (SetPin(PIN_DDR5_VIN_CTRL, CMD_GET) != true) SetPin(PIN_DDR5_VIN_CTRL, CMD_ENABLE);

                SetPin(PIN_DDR5_VIN_CTRL, CMD_DISABLE);
                Thread.Sleep(500);
                SetPin(PIN_DDR5_VIN_CTRL, CMD_ENABLE);

                return SetPin(PIN_DDR5_VIN_CTRL, CMD_GET);
            }
            catch
            {
                return false;
            }
        }

        // ADDED: ─────────────────────────────────────────────────────────────────────
        // BurnBlock – JEDEC PMIC5100 §17.3.3 MTP Vendor-Region Programming
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes a 16-byte block of data to one of the PMIC's MTP vendor-region blocks
        /// and executes the JEDEC MTP burn sequence.
        ///
        /// <para><b>Vendor MTP map (JEDEC PMIC5100 §17.3.3):</b></para>
        /// <list type="table">
        ///   <listheader><term>Block</term><description>Register range</description></listheader>
        ///   <item><term>0</term><description>0x40–0x4F</description></item>
        ///   <item><term>1</term><description>0x50–0x5F</description></item>
        ///   <item><term>2</term><description>0x60–0x6F</description></item>
        ///   <item><term>99</term><description>Writes ALL 256 registers then burns all three blocks.</description></item>
        /// </list>
        ///
        /// <para>The method uses only existing <see cref="WriteI2CDevice"/> and
        /// <see cref="ReadPMICDevice"/> commands – no new firmware command is needed.</para>
        /// </summary>
        /// <param name="pmicAddress">I2C address of the PMIC (0x48–0x4F).</param>
        /// <param name="blockNumber">0 = 0x40–0x4F, 1 = 0x50–0x5F, 2 = 0x60–0x6F,
        ///   99 = full 256-byte dump.</param>
        /// <param name="blockData">
        ///   For blockNumber 0/1/2: exactly 16 bytes of data to program.
        ///   For blockNumber 99: the full 256-byte PMIC register dump.
        /// </param>
        /// <returns><c>true</c> on success, <c>false</c> on any failure.</returns>
        /// <exception cref="ArgumentException">Invalid block number or data length.</exception>
        public bool BurnBlock(byte pmicAddress, int blockNumber, byte[] blockData)
        {
            if (blockData == null)
                throw new ArgumentNullException(nameof(blockData));

            if (blockNumber != 99 && blockNumber < 0 || blockNumber > 2 && blockNumber != 99)
                throw new ArgumentException("blockNumber must be 0, 1, 2, or 99.", nameof(blockNumber));

            if (blockNumber != 99 && blockData.Length < 16)
                throw new ArgumentException("blockData must be at least 16 bytes for a single-block burn.", nameof(blockData));

            if (blockNumber == 99 && blockData.Length < 256)
                throw new ArgumentException("blockData must be exactly 256 bytes for a full dump burn.", nameof(blockData));

            try
            {
                if (blockNumber == 99)
                {
                    // ── Full dump mode ──────────────────────────────────────────────
                    // 1. Unlock vendor region with default password
                    if (!UnlockVendorRegion(pmicAddress))
                        return false;

                    // 2. Write all 256 volatile registers (0x00–0xFF)
                    //    Registers below 0x40 are fully volatile; no MTP burn is needed.
                    for (int reg = 0x00; reg <= 0xFF; reg++)
                    {
                        if (!WriteI2CDevice(pmicAddress, (ushort)reg, blockData[reg]))
                            return false;
                    }

                    // 3. Burn all three vendor MTP blocks sequentially
                    for (int blk = 0; blk < 3; blk++)
                    {
                        byte[] chunk = new byte[16];
                        Array.Copy(blockData, 0x40 + blk * 16, chunk, 0, 16);

                        if (!BurnSingleBlock(pmicAddress, blk, chunk))
                        {
                            LockVendorRegion(pmicAddress);
                            return false;
                        }
                    }

                    // 4. Lock vendor region
                    LockVendorRegion(pmicAddress);
                    return true;
                }
                else
                {
                    // ── Single-block mode ──────────────────────────────────────────
                    // Extract exactly 16 bytes for the selected block
                    byte[] chunk = new byte[16];
                    Array.Copy(blockData, 0, chunk, 0, 16);

                    // Unlock → burn → lock
                    if (!UnlockVendorRegion(pmicAddress))
                        return false;

                    bool result = BurnSingleBlock(pmicAddress, blockNumber, chunk);
                    LockVendorRegion(pmicAddress);
                    return result;
                }
            }
            catch (Exception)
            {
                // Best-effort lock on error
                try { LockVendorRegion(pmicAddress); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Convenience overload: burns a single vendor block (0, 1, or 2) using 16 bytes
        /// extracted from a caller-supplied full 256-byte dump at the appropriate offset.
        /// </summary>
        /// <param name="pmicAddress">I2C address of the PMIC.</param>
        /// <param name="blockNumber">0, 1, or 2.</param>
        /// <param name="fullDump">256-byte PMIC register dump; bytes [0x40+blockNumber*16 .. +15]
        ///   are used.</param>
        public bool WritePMICVendorBlock(byte pmicAddress, int blockNumber, byte[] fullDump)
        {
            if (fullDump == null || fullDump.Length < 256)
                throw new ArgumentException("fullDump must be 256 bytes.", nameof(fullDump));
            if (blockNumber < 0 || blockNumber > 2)
                throw new ArgumentException("blockNumber must be 0, 1, or 2.", nameof(blockNumber));

            byte[] chunk = new byte[16];
            Array.Copy(fullDump, 0x40 + blockNumber * 16, chunk, 0, 16);
            return BurnBlock(pmicAddress, blockNumber, chunk);
        }

        /// <summary>
        /// Burns all three vendor blocks (0x40–0x6F) from a full 256-byte dump.
        /// Equivalent to calling <see cref="BurnBlock"/> with blockNumber=99.
        /// </summary>
        public bool WritePMICFullDump(byte pmicAddress, byte[] fullDump)
        {
            if (fullDump == null || fullDump.Length < 256)
                throw new ArgumentException("fullDump must be 256 bytes.", nameof(fullDump));

            return BurnBlock(pmicAddress, 99, fullDump);
        }

        /// <summary>
        /// Core helper: writes 16 bytes to the block's registers and executes the MTP
        /// burn command for that block. The caller is responsible for unlocking/locking
        /// the vendor region around this call.
        /// </summary>
        /// <param name="pmicAddress">I2C address of the PMIC.</param>
        /// <param name="blockIndex">0, 1, or 2.</param>
        /// <param name="data">Exactly 16 bytes to program.</param>
        /// <returns><c>true</c> on success.</returns>
        private bool BurnSingleBlock(byte pmicAddress, int blockIndex, byte[] data)
        {
            // Burn command codes per JEDEC PMIC5100 §17.3.3
            byte[] burnCmds = { 0x81, 0x82, 0x85 };
            byte burnCmd = burnCmds[blockIndex];
            byte baseReg = (byte)(0x40 + blockIndex * 16);

            // Step 1 – write the 16 data bytes to MTP registers
            for (int i = 0; i < 16; i++)
            {
                if (!WriteI2CDevice(pmicAddress, (ushort)(baseReg + i), data[i]))
                    return false;
            }

            // Step 2 – issue the burn command
            if (!WriteI2CDevice(pmicAddress, PMIC_REG_PASS_CTRL, burnCmd))
                return false;

            // Step 3 – wait at least 200 ms for the MTP cell to program
            Thread.Sleep(PMIC_BURN_WAIT_MS);

            // Step 4 – poll for completion (register 0x39 → 0x5A means done)
            var pollDeadline = DateTime.Now.AddMilliseconds(PMIC_BURN_POLL_TIMEOUT_MS);
            bool burnComplete = false;
            while (DateTime.Now < pollDeadline)
            {
                var result = ReadPMICDevice(pmicAddress, PMIC_REG_PASS_CTRL);
                if (result != null && result.Length > 0 && result[0] == PMIC_BURN_COMPLETE_TOKEN)
                {
                    burnComplete = true;
                    break;
                }
                Thread.Sleep(25);
            }

            if (!burnComplete)
                return false;

            // Step 5 – restore vendor region unlock (0x40) so subsequent reads work
            // (The burn command overwrote 0x39; we must re‑enable access for verification
            // and further operations.)
            if (!WriteI2CDevice(pmicAddress, PMIC_REG_PASS_CTRL, 0x40))
                return false;

            return true;
        }

        /// <summary>
        /// Intelligently enables Programmable Mode (JEDEC PMIC5100 §6.4).
        /// - If already programmable, does nothing.
        /// - If VR is on, toggles the PMIC_CTRL pin to disable regulators,
        ///   writes 0x2F[2]=1, then re‑enables regulators.
        /// - If VR is off, simply writes the register.
        /// </summary>
        public bool SmartEnableProgrammableMode(byte pmicAddress)
        {
            // Read current mode (0x2F bit2) and VR enable (0x32 bit7)
            var reg2F = ReadPMICDevice(pmicAddress, 0x2F);
            var reg32 = ReadPMICDevice(pmicAddress, 0x32);
            if (reg2F == null || reg2F.Length == 0 || reg32 == null || reg32.Length == 0)
                return false;

            bool isProgMode = (reg2F[0] & 0x04) != 0;
            if (isProgMode)
                return true; // already programmable

            bool vrEnabled = (reg32[0] & 0x80) != 0;

            // If VR is on, we must turn it off to allow writing to 0x2F (protected in Secure Mode)
            if (vrEnabled)
            {
                // Use PMIC_CTRL pin to disable regulators (register write is blocked in Secure Mode)
                SetPin(SPDToolDevice.PIN_PMIC_CTRL, 0x00);   // drive low → VR disable
                Thread.Sleep(100); // wait for regulators to ramp down
            }

            // Now write programmable mode bit
            byte new2F = (byte)(reg2F[0] | 0x04);
            if (!WriteI2CDevice(pmicAddress, 0x2F, new2F))
            {
                // Attempt to restore VR if it was on
                if (vrEnabled)
                    SetPin(SPDToolDevice.PIN_PMIC_CTRL, 0x01);
                return false;
            }

            // Re‑enable VR if it was originally on
            if (vrEnabled)
            {
                SetPin(SPDToolDevice.PIN_PMIC_CTRL, 0x01);   // drive high → VR enable
                                                                    // Wait for power good (simplified: wait typical startup time)
                Thread.Sleep(300);
            }

            // Verify the change
            var verify = ReadPMICDevice(pmicAddress, 0x2F);
            return verify != null && verify.Length > 0 && (verify[0] & 0x04) != 0;
        }

        // ADDED: ─────────────────────────────────────────────────────────────────────
        // GetPMICPGoodStatus – JEDEC PMIC5100 registers 0x08, 0x09, 0x32, 0x33
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a human-readable description of the PMIC power-good status by
        /// inspecting the relevant fault and control registers.
        ///
        /// <para><b>Registers used (JEDEC PMIC5100):</b></para>
        /// <list type="bullet">
        ///   <item>0x08 – Switching-regulator fault status (SWA bit 5, SWB bit 3, SWC bit 2)</item>
        ///   <item>0x09 – LDO fault status (VOUT_1.8V bit 5)</item>
        ///   <item>0x33 – VOUT_1.0V status (bit 2)</item>
        ///   <item>0x32 – VR control: bits [4:3] = PWR_GOOD output mode, bit 5 = IO type</item>
        /// </list>
        /// </summary>
        /// <param name="pmicAddress">I2C address of the PMIC (0x48–0x4F).</param>
        /// <returns>A descriptive status string; "Read Error" if communication fails.</returns>
        public string GetPMICPGoodStatus(byte pmicAddress)
        {
            try
            {
                // Read all relevant status registers
                var reg08 = ReadPMICDevice(pmicAddress, 0x08);
                var reg09 = ReadPMICDevice(pmicAddress, 0x09);
                var reg32 = ReadPMICDevice(pmicAddress, PMIC_REG_VR_CTRL);
                var reg33 = ReadPMICDevice(pmicAddress, PMIC_REG_PG_VOUT1V);

                if (reg08 == null || reg09 == null || reg32 == null || reg33 == null)
                    return "Read Error";

                // ── Decode fault bits ──
                // JEDEC PMIC5100 §7.x: bit HIGH means fault present (active-high fault flag)
                bool swaFault = (reg08[0] & 0x20) != 0; // bit 5
                bool swbFault = (reg08[0] & 0x08) != 0; // bit 3
                bool swcFault = (reg08[0] & 0x04) != 0; // bit 2
                bool ldo18Fault = (reg09[0] & 0x20) != 0; // VOUT_1.8V bit 5
                bool ldo10Fault = (reg33[0] & 0x04) != 0; // VOUT_1.0V bit 2

                var faults = new List<string>();
                if (swaFault) faults.Add("SWA not good");
                if (swbFault) faults.Add("SWB not good");
                if (swcFault) faults.Add("SWC not good");
                if (ldo18Fault) faults.Add("VOUT_1.8V not good");
                if (ldo10Fault) faults.Add("VOUT_1.0V not good");

                // ── Decode PWR_GOOD output control (reg 0x32 bits [4:3]) ──
                // 00 = normal (follows internal PG logic)
                // 01 = forced LOW
                // 10 = forced HIGH / open-drain float
                int pgMode = (reg32[0] >> 3) & 0x03;
                bool ioOD = (reg32[0] & 0x20) != 0; // bit 5: open-drain if set

                string pgControl;
                switch (pgMode)
                {
                    case 1: pgControl = "PWR_GOOD forced LOW by host"; break;
                    case 2: pgControl = ioOD ? "PWR_GOOD forced open-drain (float)" : "PWR_GOOD forced HIGH"; break;
                    default: pgControl = null; break; // normal operation
                }

                // ── Compose status string ──
                if (pgControl != null)
                    return faults.Count == 0
                        ? pgControl
                        : $"{pgControl}; Faults: {string.Join(", ", faults)}";

                if (faults.Count == 0)
                    return "All rails good — PWR_GOOD asserted";

                return $"Fault: {string.Join(", ", faults)}";
            }
            catch (Exception ex)
            {
                return $"Read Error: {ex.Message}";
            }
        }

        /// <summary>Container for a full set of PMIC measurements.</summary>
        public class PMICMeasurement
        {
            public string DeviceType { get; set; }
            public Dictionary<string, double> Voltages_mV { get; } = new();
            public Dictionary<string, double> Currents_mA { get; } = new();
            public Dictionary<string, double> Powers_mW { get; } = new();
            public double TotalPower_mW { get; set; } = double.NaN;
        }

        /// <summary>Reads all available ADC measurements from a PMIC.</summary>
        public PMICMeasurement ReadAllMeasurements(byte pmicAddress)
        {
            var result = new PMICMeasurement();

            try
            {
                result.DeviceType = GetPMICType(pmicAddress);

                // Read configuration registers that affect scaling
                var r1B = ReadPMICDevice(pmicAddress, 0x1B);
                var r1A = ReadPMICDevice(pmicAddress, 0x1A);
                var r32 = ReadPMICDevice(pmicAddress, 0x32);
                if (r1B == null || r1A == null || r32 == null)
                    return null;

                var powerMode = (r1B[0] & 0x40) != 0;      // true = power measurement
                var totalPower = (r1A[0] & 0x02) != 0;     // true = total power in 0x0C
                var resolution = r32[0] & 0x03;            // 0=coarse (125mV/A), 1=fine (31.25)

                // Get channel maps based on device type
                var voltageChannels = GetVoltageChannelMap(result.DeviceType);
                var currentRegs = GetCurrentRegisterMap(result.DeviceType);

                // Read voltages
                if (voltageChannels.Any())
                {
                    var writeVals = voltageChannels.Values
                        .Select(ch => (byte)(0x80 | (ch.code << 3)))
                        .ToArray();

                    var rawVoltages = ReadPMICADC(pmicAddress, 0x30, writeVals, 0x31);
                    if (rawVoltages?.Length == voltageChannels.Count)
                    {
                        var i = 0;
                        foreach (var kv in voltageChannels)
                        {
                            result.Voltages_mV[kv.Key] = rawVoltages[i] * kv.Value.factor;
                            i++;
                        }
                    }
                }

                // Read currents / powers
                var lsb = resolution == 0 ? 125.0 : 31.25; // mA or mW per LSB
                foreach (var rail in currentRegs)
                {
                    var raw = ReadPMICDevice(pmicAddress, rail.Value);
                    if (raw == null || raw.Length == 0) continue;

                    var value = raw[0] * lsb;
                    if (powerMode)
                        result.Powers_mW[rail.Key] = value;
                    else
                        result.Currents_mA[rail.Key] = value;
                }

                // Total power mode
                if (totalPower)
                {
                    var totalRaw = ReadPMICDevice(pmicAddress, 0x0C);
                    if (totalRaw?.Length > 0)
                        result.TotalPower_mW = totalRaw[0] * lsb;
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        // Helper: voltage channel codes and LSB factors (mV per LSB)
        private Dictionary<string, (byte code, double factor)> GetVoltageChannelMap(string deviceType)
        {
            var map = new Dictionary<string, (byte, double)>
            {
                ["VIN_BULK"] = (0x05, 70.0),
                ["VOUT_1.8V"] = (0x08, 15.0),
                ["VOUT_1.0V"] = (0x09, 15.0)
            };

            if (deviceType.StartsWith("PMIC50")) // 5000 series (RDIMM family)
            {
                map["SWA"] = (0x00, 15.0);
                map["SWB"] = (0x01, 15.0);
                map["SWC"] = (0x02, 15.0);
                map["SWD"] = (0x03, 15.0);
                if (deviceType == "PMIC5020" || deviceType == "PMIC5030")
                {
                    map["SWE"] = (0x04, 15.0);
                    map["SWF"] = (0x0A, 15.0);
                }
            }
            else if (deviceType.StartsWith("PMIC51")) // UDIMM/SODIMM (3 rails)
            {
                map["SWA"] = (0x00, 15.0);
                map["SWB"] = (0x02, 15.0);
                map["SWC"] = (0x03, 15.0);
            }
            else if (deviceType.StartsWith("PMIC52")) // CAMM2 (4 rails)
            {
                map["SWA"] = (0x00, 15.0);
                map["SWD"] = (0x01, 15.0);
                map["SWB"] = (0x02, 15.0);
                map["SWC"] = (0x03, 15.0);
            }

            return map;
        }

        // Helper: current/power register addresses (8‑bit for simplicity)
        private Dictionary<string, byte> GetCurrentRegisterMap(string deviceType)
        {
            var map = new Dictionary<string, byte>();
            if (deviceType.StartsWith("PMIC50"))
            {
                map["SWA"] = 0x0C;
                map["SWB"] = 0x0D;
                map["SWC"] = 0x0E;
                map["SWD"] = 0x0F;
            }
            else if (deviceType.StartsWith("PMIC51"))
            {
                map["SWA"] = 0x0C;
                map["SWB"] = 0x0E;
                map["SWC"] = 0x0F;
            }
            else if (deviceType.StartsWith("PMIC52"))
            {
                map["SWA"] = 0x0C;
                map["SWD"] = 0x0D;
                map["SWB"] = 0x0E;
                map["SWC"] = 0x0F;
            }
            return map;
        }

        /// <summary>Reads all 256 registers from a PMIC in chunks.</summary>
        public byte[] ReadAllPMICRegisters(byte pmicAddress)
        {
            const int chunkSize = 64;
            var allData = new List<byte>(256);
            for (ushort offset = 0; offset < 256; offset += chunkSize)
            {
                byte len = (byte)Math.Min(chunkSize, 256 - offset);
                byte[] chunk = ReadI2CDevice(pmicAddress, offset, len);
                if (chunk == null || chunk.Length != len)
                {
                    // pad with 0xFF on failure
                    for (int i = 0; i < len; i++) allData.Add(0xFF);
                }
                else
                {
                    allData.AddRange(chunk);
                }
                Thread.Sleep(10);
            }
            return allData.ToArray();
        }

        /// <summary>Returns true if the VR_ENABLE bit (0x32[7]) is set.</summary>
        public bool GetVREnabled(byte pmicAddress)
        {
            var reg = ReadPMICDevice(pmicAddress, PMIC_REG_VR_CTRL);
            return reg != null && reg.Length > 0 && (reg[0] & 0x80) != 0;
        }

        /// <summary>Toggles the PMIC output regulators on/off using the appropriate method
        /// (pin control if locked, register write otherwise).</summary>
        public bool ToggleVREnable(byte pmicAddress)
        {
            string mode = GetPMICMode(pmicAddress);
            bool currentlyOn = GetVREnabled(pmicAddress);

            if (mode == "Locked")
            {
                // Use PMIC_CTRL pin because register writes are blocked
                bool pinHigh = GetPin(PIN_PMIC_CTRL) == 1;
                SetPin(PIN_PMIC_CTRL, pinHigh ? CMD_DISABLE : CMD_ENABLE);
            }
            else
            {
                // Programmable / Manufacturer mode: write register 0x32
                byte newVal = currentlyOn ? (byte)0x00 : (byte)0x80;
                WriteI2CDevice(pmicAddress, PMIC_REG_VR_CTRL, newVal);
            }
            Thread.Sleep(150); // allow transition
            return true;
        }

        /// <summary>Sets the PWR_GOOD output control mode (0x32 bits [4:3]).
        /// 0 = normal, 1 = forced LOW, 2 = forced HIGH.</summary>
        public bool SetPowerGoodMode(byte pmicAddress, int mode)
        {
            if (mode < 0 || mode > 2)
                throw new ArgumentOutOfRangeException(nameof(mode), "Mode must be 0, 1, or 2.");
            var reg = ReadPMICDevice(pmicAddress, PMIC_REG_VR_CTRL);
            if (reg == null || reg.Length == 0) return false;
            byte newVal = (byte)((reg[0] & ~0x18) | ((mode & 0x03) << 3));
            return WriteI2CDevice(pmicAddress, PMIC_REG_VR_CTRL, newVal);
        }

        /// <summary>Checks if the vendor region is currently unlocked (0x45 != 0).</summary>
        public bool IsVendorRegionUnlocked(byte pmicAddress)
        {
            var stat = ReadPMICDevice(pmicAddress, 0x45);
            return stat != null && stat.Length > 0 && stat[0] != 0;
        }

        #endregion

        #region Pin control

        /// <summary>Sets the state of a control pin.</summary>
        /// <param name="pin">Pin identifier (PIN_xxx constants).</param>
        /// <param name="state">CMD_ENABLE, CMD_DISABLE, or CMD_GET.</param>
        public bool SetPin(byte pin, byte state)
        {
            if (pin > PIN_RFU2)
                throw new ArgumentException($"Pin must be 0‑{PIN_RFU2}.", nameof(pin));

            if (state != CMD_ENABLE && state != CMD_DISABLE && state != CMD_GET)
                throw new ArgumentException("State must be CMD_ENABLE, CMD_DISABLE, or CMD_GET.", nameof(state));

            SendCommand(CMD_PIN_CONTROL, pin, state);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Reads the current state of a pin.</summary>
        public byte GetPin(byte pin)
        {
            if (pin > PIN_RFU2)
                throw new ArgumentException($"Pin must be 0‑{PIN_RFU2}.", nameof(pin));

            SendCommand(CMD_PIN_CONTROL, pin, CMD_GET);
            var response = ReadResponse();

            return response?[0] ?? 0;
        }

        /// <summary>Resets all pins to their default state.</summary>
        public bool ResetPins()
        {
            SendCommand(CMD_PIN_RESET);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        /// <summary>Enables the 9V programming voltage (if supported by hardware).</summary>
        public bool EnableHighVoltage() => SetPin(PIN_HV_SWITCH, CMD_ENABLE);

        /// <summary>Disables the 9V programming voltage.</summary>
        public bool DisableHighVoltage() => SetPin(PIN_HV_SWITCH, CMD_DISABLE);

        /// <summary>Returns true if high voltage is enabled.</summary>
        public bool GetHighVoltageState() => GetPin(PIN_HV_SWITCH) == 1;

        /// <summary>Sets the SA1 pin state (used to select between two SPD devices on some adapters).</summary>
        public bool SetSA1State(bool state) =>
            SetPin(PIN_SA1_SWITCH, state ? CMD_ENABLE : CMD_DISABLE);

        /// <summary>Gets the SA1 pin state.</summary>
        public bool GetSA1State() => GetPin(PIN_SA1_SWITCH) == 1;

        #endregion

        #region Internal EEPROM (device settings)

        /// <summary>Reads from the device's internal flash storage.</summary>
        /// <param name="offset">0‑255.</param>
        /// <param name="length">1‑32, must not exceed bounds.</param>
        public byte[] ReadInternalEEPROM(ushort offset, ushort length)
        {
            if (offset > 255)
                throw new ArgumentException("Offset must be 0‑255.", nameof(offset));
            if (length is 0 or > MAX_EEPROM_READ)
                throw new ArgumentException($"Length must be 1‑{MAX_EEPROM_READ}.", nameof(length));
            if (offset + length > 256)
                throw new ArgumentException("Read would exceed storage bounds.");

            SendCommand(CMD_EEPROM, CMD_GET,
                (byte)(offset & 0xFF), (byte)((offset >> 8) & 0xFF),
                (byte)(length & 0xFF), (byte)((length >> 8) & 0xFF));

            var response = ReadResponse();
            return IsErrorResponse(response) ? null : response;
        }

        #endregion

        #region Helpers

        private static bool IsErrorResponse(byte[] response) =>
            response == null || response.Length == 0;

        private static void ValidateReadParams(ushort offset, byte length)
        {
            if (length is 0 or > MAX_READ_LENGTH)
                throw new ArgumentException($"Length must be 1‑{MAX_READ_LENGTH}.", nameof(length));
            if (offset > 1023)
                throw new ArgumentException("Offset must be 0‑1023.", nameof(offset));
        }

        /// <summary>Checks if the address is within the SPD range (0x50‑0x57).</summary>
        public static bool IsValidSPDAddress(byte address) =>
            address >= SPD_ADDRESS_MIN && address <= SPD_ADDRESS_MAX;

        /// <summary>Checks if the address is within the PMIC range (0x48‑0x4F).</summary>
        public static bool IsValidPMICAddress(byte address) =>
            address >= PMIC_ADDRESS_MIN && address <= PMIC_ADDRESS_MAX;

        /// <summary>Converts a size code to bytes (1→256, 2→512, 3→1024, else 0).</summary>
        public static int SizeCodeToBytes(byte sizeCode) =>
            sizeCode switch
            {
                1 => 256,
                2 => 512,
                3 => 1024,
                _ => 0
            };

        /// <summary>Calculates the simple sum checksum used by the firmware.</summary>
        public static byte CalculateChecksum(byte[] data) =>
            (byte)data.Sum(b => b);

        #endregion
    }

    #region Supporting types

    /// <summary>Identifies the type of memory module.</summary>
    public enum ModuleType
    {
        NotDetected,
        DDR3_Or_Other,
        DDR4,
        DDR5
    }

    /// <summary>Information about a detected module.</summary>
    public class ModuleInfo
    {
        public byte Address { get; set; }
        public ModuleType Type { get; set; }
        public int Size { get; set; }

        public override string ToString() =>
            $"Address: 0x{Address:X2}, Type: {Type}, Size: {Size} bytes";
    }

    /// <summary>Indicates which memory types support reversible write protection (RSWP).</summary>
    public class RSWPSupport
    {
        public bool DDR5Supported { get; set; }
        public bool DDR4Supported { get; set; }
        public bool DDR3Supported { get; set; }

        public override string ToString()
        {
            var supported = new List<string>();
            if (DDR5Supported) supported.Add("DDR5");
            if (DDR4Supported) supported.Add("DDR4");
            if (DDR3Supported) supported.Add("DDR3");
            return supported.Count > 0 ? string.Join(", ", supported) : "None";
        }
    }

    /// <summary>Event arguments for asynchronous alerts sent by the device.</summary>
    public class AlertEventArgs : EventArgs
    {
        public byte AlertCode { get; }
        public string AlertType { get; }

        public AlertEventArgs(byte alertCode)
        {
            AlertCode = alertCode;
            AlertType = alertCode switch
            {
                0x21 => "Ready",
                0x2B => "Slave Count Increased",
                0x2D => "Slave Count Decreased",
                0x2F => "Clock Speed Increased",
                0x5C => "Clock Speed Decreased",
                _ => $"Unknown (0x{alertCode:X2})"
            };
        }

        public override string ToString() => $"Alert: {AlertType} (0x{AlertCode:X2})";
    }

    #endregion
}