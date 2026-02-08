using System;
using System.IO.Ports;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SPDTool
{
    /// <summary>
    /// SPD Tool Communication Library
    /// Provides complete interface to RP2040-based SPD EEPROM Reader/Writer
    /// Firmware Version: 20240616
    /// </summary>
    public class SPDToolDevice : IDisposable
    {
        #region Constants

        // Command Bytes (MUST match Arduino Command enum)
        private const byte CMD_GET = 0xFF;
        private const byte CMD_DISABLE = 0x00;
        private const byte CMD_ENABLE = 0x01;

        // Diagnostics & info
        private const byte CMD_VERSION = 0x02;          // 2
        private const byte CMD_TEST = 0x03;             // 3
        private const byte CMD_PING = 0x04;             // 4
        private const byte CMD_NAME = 0x05;             // 5
        private const byte CMD_FACTORY_RESET = 0x06;    // 6

        // Control commands
        private const byte CMD_SPD_READ_PAGE = 0x07;    // 7
        private const byte CMD_SPD_WRITE_BYTE = 0x08;   // 8
        private const byte CMD_SPD_WRITE_PAGE = 0x09;   // 9
        private const byte CMD_SPD_WRITE_TEST = 0x0A;   // 10
        private const byte CMD_DDR4_DETECT = 0x0B;      // 11
        private const byte CMD_DDR5_DETECT = 0x0C;      // 12
        private const byte CMD_SPD5_HUB_REG = 0x0D;     // 13
        private const byte CMD_SPD_SIZE = 0x0E;         // 14
        private const byte CMD_SCAN_BUS = 0x0F;         // 15
        private const byte CMD_BUS_CLOCK = 0x10;        // 16
        private const byte CMD_PROBE_ADDRESS = 0x11;    // 17
        private const byte CMD_I2C_READ = 0x12;         // 18 - NOT IMPLEMENTED
        private const byte CMD_I2C_WRITE = 0x13;        // 19 - NOT IMPLEMENTED
        private const byte CMD_PIN_CONTROL = 0x14;      // 20
        private const byte CMD_PIN_RESET = 0x15;        // 21
        private const byte CMD_RSWP = 0x16;             // 22
        private const byte CMD_PSWP = 0x17;             // 23
        private const byte CMD_RSWP_REPORT = 0x18;      // 24
        private const byte CMD_EEPROM = 0x19;           // 25

        // Response/Alert Markers
        private const byte RESPONSE_MARKER = 0x26; // '&'
        private const byte ALERT_MARKER = 0x40;    // '@'
        private const byte UNKNOWN_MARKER = 0x3F;  // '?'
        private const byte READY_MARKER = 0x21;    // '!'

        // Alert Codes
        private const byte ALERT_READY = 0x21;      // '!'
        private const byte ALERT_SLAVE_INC = 0x2B;  // '+'
        private const byte ALERT_SLAVE_DEC = 0x2D;  // '-'
        private const byte ALERT_CLOCK_INC = 0x2F;  // '/'
        private const byte ALERT_CLOCK_DEC = 0x5C;  // '\'

        // Pin Types (MUST match Arduino pin enum)
        private const byte PIN_HV_SWITCH = 0x00;
        private const byte PIN_SA1_SWITCH = 0x01;
        private const byte PIN_HV_FEEDBACK = 0x02;  // Not directly controllable

        // SPD5 Hub Registers
        public const byte MR0 = 0x00;
        public const byte MR1 = 0x01;
        public const byte MR6 = 0x06;
        public const byte MR11 = 0x0B;  // Page register
        public const byte MR12 = 0x0C;  // RSWP blocks 0-7
        public const byte MR13 = 0x0D;  // RSWP blocks 8-15
        public const byte MR14 = 0x0E;
        public const byte MR18 = 0x12;
        public const byte MR20 = 0x14;
        public const byte MR48 = 0x30;  // Status register
        public const byte MR52 = 0x34;

        // I2C Address Ranges
        public const byte SPD_ADDRESS_MIN = 0x50;
        public const byte SPD_ADDRESS_MAX = 0x57;
        public const byte PMIC_ADDRESS_MIN = 0x48;
        public const byte PMIC_ADDRESS_MAX = 0x4F;

        // RAM Type Bitmasks (from Arduino)
        public const byte RSWP_DDR5 = 0x20;  // Bit 5
        public const byte RSWP_DDR4 = 0x10;  // Bit 4
        public const byte RSWP_DDR3 = 0x08;  // Bit 3

        // Constraints
        public const int MAX_READ_LENGTH = 64;
        public const int MAX_WRITE_LENGTH = 16;
        public const int MAX_DEVICE_NAME_LENGTH = 16;
        public const int MAX_EEPROM_READ = 32;  // From Arduino: responseBuffer[32]

        #endregion

        #region Private Fields

        private SerialPort _serialPort;
        private readonly object _serialLock = new object();
        private bool _disposed = false;

        #endregion

        #region Events

        /// <summary>
        /// Fired when an alert is received from the device
        /// </summary>
        public event EventHandler<AlertEventArgs> AlertReceived;

        #endregion

        #region Constructor and Disposal

        /// <summary>
        /// Initializes a new instance of the SPDToolDevice
        /// </summary>
        /// <param name="portName">Serial port name</param>
        /// <param name="baudRate">Baud rate (default 115200)</param>
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

            // Wait for device to initialize
            Thread.Sleep(2000);

            // Clear any pending data
            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();
        }

        /// <summary>
        /// Closes the serial port and releases resources
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    if (_serialPort != null)
                    {
                        if (_serialPort.IsOpen)
                            _serialPort.Close();
                        _serialPort.Dispose();
                    }
                }
                _disposed = true;
            }
        }

        #endregion

        #region Low-Level Communication

        /// <summary>
        /// Sends a command with parameters to the device
        /// </summary>
        private void SendCommand(byte command, params byte[] parameters)
        {
            lock (_serialLock)
            {
                _serialPort.DiscardInBuffer();

                byte[] data = new byte[1 + parameters.Length];
                data[0] = command;
                Array.Copy(parameters, 0, data, 1, parameters.Length);

                _serialPort.Write(data, 0, data.Length);
            }
        }

        /// <summary>
        /// Reads and validates a response from the device
        /// </summary>
        /// <param name="timeoutMs">Timeout in milliseconds</param>
        /// <returns>Response data bytes, or null on timeout</returns>
        private byte[] ReadResponse(int timeoutMs = 2000)
        {
            lock (_serialLock)
            {
                DateTime startTime = DateTime.Now;
                int totalTimeout = timeoutMs;

                while ((DateTime.Now - startTime).TotalMilliseconds < totalTimeout)
                {
                    if (_serialPort.BytesToRead > 0)
                    {
                        byte marker = (byte)_serialPort.ReadByte();

                        if (marker == RESPONSE_MARKER) // '&'
                        {
                            // Read length with timeout
                            int lengthWait = 0;
                            while (_serialPort.BytesToRead == 0 && lengthWait < 100)
                            {
                                Thread.Sleep(1);
                                lengthWait++;
                            }

                            if (_serialPort.BytesToRead == 0)
                                throw new TimeoutException("Response length byte not received");

                            byte length = (byte)_serialPort.ReadByte();

                            if (length == 0)
                                return new byte[0];

                            // Read data bytes
                            byte[] data = new byte[length];
                            int totalRead = 0;
                            int dataTimeout = 100; // Max 100ms for data
                            DateTime dataStart = DateTime.Now;

                            while (totalRead < length &&
                                   (DateTime.Now - dataStart).TotalMilliseconds < dataTimeout)
                            {
                                if (_serialPort.BytesToRead > 0)
                                {
                                    int bytesRead = _serialPort.Read(data, totalRead, length - totalRead);
                                    totalRead += bytesRead;
                                }
                                else
                                {
                                    Thread.Sleep(1);
                                }
                            }

                            if (totalRead < length)
                                throw new TimeoutException($"Response data incomplete: {totalRead}/{length} bytes");

                            // Read checksum
                            int checksumWait = 0;
                            while (_serialPort.BytesToRead == 0 && checksumWait < 50)
                            {
                                Thread.Sleep(1);
                                checksumWait++;
                            }

                            if (_serialPort.BytesToRead == 0)
                                throw new TimeoutException("Response checksum not received");

                            byte receivedChecksum = (byte)_serialPort.ReadByte();

                            // Verify checksum
                            byte calculatedChecksum = 0;
                            foreach (byte b in data)
                                calculatedChecksum += b;

                            if (calculatedChecksum != receivedChecksum)
                                throw new InvalidOperationException(
                                    $"Checksum mismatch: expected 0x{receivedChecksum:X2}, got 0x{calculatedChecksum:X2}");

                            return data;
                        }
                        else if (marker == ALERT_MARKER) // '@'
                        {
                            // Read alert code with short timeout
                            int alertWait = 0;
                            while (_serialPort.BytesToRead == 0 && alertWait < 50)
                            {
                                Thread.Sleep(1);
                                alertWait++;
                            }

                            if (_serialPort.BytesToRead == 0)
                                continue;

                            byte alertCode = (byte)_serialPort.ReadByte();
                            OnAlertReceived(new AlertEventArgs(alertCode));

                            // Continue waiting for actual response
                            continue;
                        }
                        else if (marker == READY_MARKER) // '!'
                        {
                            return new byte[] { READY_MARKER };
                        }
                        else if (marker == UNKNOWN_MARKER) // '?'
                        {
                            throw new InvalidOperationException("Device reports unsupported hardware");
                        }
                    }

                    Thread.Sleep(1);
                }

                throw new TimeoutException($"No response received within {timeoutMs}ms");
            }
        }

        protected virtual void OnAlertReceived(AlertEventArgs e)
        {
            AlertReceived?.Invoke(this, e);
        }

        #endregion

        #region Basic Device Commands

        /// <summary>
        /// Pings the device to check if it's responding
        /// </summary>
        /// <returns>True if device responds, false otherwise</returns>
        public bool Ping()
        {
            SendCommand(CMD_PING);
            var response = ReadResponse(500);
            return response != null && response.Length > 0 && response[0] == READY_MARKER;
        }

        /// <summary>
        /// Gets the firmware version from the device
        /// </summary>
        /// <returns>Firmware version as uint (e.g., 20240616)</returns>
        public uint GetVersion()
        {
            SendCommand(CMD_VERSION);
            var response = ReadResponse();

            if (response == null || response.Length != 4)
                throw new InvalidOperationException($"Invalid version response: {response?.Length ?? 0} bytes");

            // Arduino stores as 32-bit int, little-endian
            uint version = BitConverter.ToUInt32(response, 0);
            return version;
        }

        /// <summary>
        /// Tests device communication
        /// </summary>
        /// <returns>True if test passes</returns>
        public bool Test()
        {
            SendCommand(CMD_TEST);
            var response = ReadResponse();
            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Gets the device name
        /// </summary>
        /// <returns>Device name string</returns>
        public string GetDeviceName()
        {
            SendCommand(CMD_NAME, CMD_GET);
            var response = ReadResponse();

            if (response == null || response.Length == 0)
                return string.Empty;

            // Arduino returns the name directly, not null-terminated
            int length = response.Length;
            for (int i = 0; i < response.Length; i++)
            {
                if (response[i] == 0)
                {
                    length = i;
                    break;
                }
            }

            return System.Text.Encoding.ASCII.GetString(response, 0, length);
        }

        /// <summary>
        /// Sets the device name
        /// </summary>
        /// <param name="name">New device name (max 16 characters)</param>
        /// <returns>True if successful</returns>
        public bool SetDeviceName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Name cannot be null or empty", nameof(name));

            if (name.Length > MAX_DEVICE_NAME_LENGTH)
                throw new ArgumentException($"Name must be {MAX_DEVICE_NAME_LENGTH} characters or less", nameof(name));

            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);

            // Send: CMD_NAME + length byte + name bytes
            byte[] parameters = new byte[1 + nameBytes.Length];
            parameters[0] = (byte)nameBytes.Length;
            Array.Copy(nameBytes, 0, parameters, 1, nameBytes.Length);

            SendCommand(CMD_NAME, parameters);
            var response = ReadResponse();

            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Performs a factory reset (erases all settings)
        /// </summary>
        /// <returns>True if successful</returns>
        public bool FactoryReset()
        {
            SendCommand(CMD_FACTORY_RESET);
            var response = ReadResponse(3000); // Factory reset might take longer
            return response != null && response.Length > 0 && response[0] == 1;
        }

        #endregion

        #region I2C Bus Commands

        /// <summary>
        /// Scans the I2C bus for SPD devices
        /// </summary>
        /// <returns>List of detected 7-bit I2C addresses (0x50-0x57)</returns>
        public List<byte> ScanBus()
        {
            SendCommand(CMD_SCAN_BUS);
            var response = ReadResponse();

            var devices = new List<byte>();
            if (response != null && response.Length > 0)
            {
                byte mask = response[0];
                for (int i = 0; i < 8; i++)
                {
                    if ((mask & (1 << i)) != 0)
                        devices.Add((byte)(SPD_ADDRESS_MIN + i));
                }
            }

            return devices;
        }

        /// <summary>
        /// Probes a specific I2C address to check if a device responds
        /// </summary>
        /// <param name="address">7-bit I2C address (0x00-0xFF)</param>
        /// <returns>True if device responds</returns>
        public bool ProbeAddress(byte address)
        {
            SendCommand(CMD_PROBE_ADDRESS, address);
            var response = ReadResponse();
            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Gets the current I2C clock mode
        /// </summary>
        /// <returns>Clock mode (0 = 100kHz, 1 = 400kHz)</returns>
        public byte GetI2CClockMode()
        {
            SendCommand(CMD_BUS_CLOCK, CMD_GET);
            var response = ReadResponse();

            if (response == null || response.Length == 0)
                throw new InvalidOperationException("Failed to get I2C clock mode");

            return response[0]; // Returns 0 or 1
        }

        /// <summary>
        /// Sets the I2C clock mode
        /// </summary>
        /// <param name="mode">Clock mode (0 = 100kHz, 1 = 400kHz)</param>
        /// <returns>True if successful</returns>
        public bool SetI2CClockMode(byte mode)
        {
            if (mode > 1)
                throw new ArgumentException("Mode must be 0 (100kHz) or 1 (400kHz)", nameof(mode));

            SendCommand(CMD_BUS_CLOCK, mode);
            var response = ReadResponse();
            return response != null && response.Length > 0 && response[0] == 1;
        }

        #endregion

        #region SPD Reading Commands

        /// <summary>
        /// Reads bytes from SPD EEPROM
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <param name="offset">Byte offset (0-1023)</param>
        /// <param name="length">Number of bytes to read (1-64)</param>
        /// <returns>Array of read bytes, or null on error</returns>
        public byte[] ReadSPD(byte address, ushort offset, byte length)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be in range 0x{SPD_ADDRESS_MIN:X2}-0x{SPD_ADDRESS_MAX:X2}", nameof(address));

            if (length == 0 || length > MAX_READ_LENGTH)
                throw new ArgumentException($"Length must be 1-{MAX_READ_LENGTH}", nameof(length));

            if (offset > 1023)
                throw new ArgumentException("Offset must be 0-1023", nameof(offset));

            byte offsetLow = (byte)(offset & 0xFF);
            byte offsetHigh = (byte)((offset >> 8) & 0xFF);

            SendCommand(CMD_SPD_READ_PAGE, address, offsetLow, offsetHigh, length);
            var response = ReadResponse();

            if (response == null)
                return null;

            // Arduino returns 0x00 for error in single-byte response
            if (response.Length == 1 && response[0] == 0)
                return null;

            return response;
        }

        /// <summary>
        /// Reads bytes from any I2C device (including PMIC)
        /// </summary>
        /// <param name="address">7-bit I2C address (0x00-0xFF)</param>
        /// <param name="offset">Byte offset (0-1023)</param>
        /// <param name="length">Number of bytes to read (1-64)</param>
        /// <returns>Array of read bytes, or null on error</returns>
        public byte[] ReadI2CDevice(byte address, ushort offset, byte length)
        {
            if (length == 0 || length > MAX_READ_LENGTH)
                throw new ArgumentException($"Length must be 1-{MAX_READ_LENGTH}", nameof(length));

            if (offset > 1023)
                throw new ArgumentException("Offset must be 0-1023", nameof(offset));

            byte offsetLow = (byte)(offset & 0xFF);
            byte offsetHigh = (byte)((offset >> 8) & 0xFF);

            // Use the same command as ReadSPD but without address validation
            SendCommand(CMD_SPD_READ_PAGE, address, offsetLow, offsetHigh, length);
            var response = ReadResponse();

            if (response == null)
                return null;

            // Arduino returns 0x00 for error in single-byte response
            if (response.Length == 1 && response[0] == 0)
                return null;

            return response;
        }

        /// <summary>
        /// Reads the entire SPD EEPROM contents with automatic page handling
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <returns>Complete SPD data, or null on error</returns>
        public byte[] ReadEntireSPD(byte address)
        {
            int size = GetSPDSizeBytes(address);
            if (size == 0)
                return null;

            List<byte> allData = new List<byte>(size);

            // Check if DDR5 - needs special handling for pages
            if (DetectDDR5(address))
            {
                // DDR5: 1024 bytes total, 256 bytes per logical page
                for (int page = 0; page < 4; page++)
                {
                    // For each page, read 256 bytes in chunks
                    ushort pageBase = (ushort)(page * 256);

                    for (ushort offset = 0; offset < 256; offset += MAX_READ_LENGTH)
                    {
                        byte chunkSize = (byte)Math.Min(MAX_READ_LENGTH, 256 - offset);
                        byte[] chunk = ReadSPD(address, (ushort)(pageBase + offset), chunkSize);

                        if (chunk == null || chunk.Length != chunkSize)
                        {
                            throw new InvalidOperationException($"Failed to read DDR5 page {page} at offset {offset}");
                        }

                        allData.AddRange(chunk);
                    }
                }

                // Trim to exact size
                if (allData.Count > size)
                {
                    allData.RemoveRange(size, allData.Count - size);
                }
            }
            else if (DetectDDR4(address))
            {
                // DDR4: 512 bytes total, 256 bytes per page
                // Read page 0 (offsets 0-255)
                for (ushort offset = 0; offset < 256; offset += MAX_READ_LENGTH)
                {
                    byte chunkSize = (byte)Math.Min(MAX_READ_LENGTH, 256 - offset);
                    byte[] chunk = ReadSPD(address, offset, chunkSize);

                    if (chunk == null || chunk.Length != chunkSize)
                    {
                        throw new InvalidOperationException($"Failed to read DDR4 page 0 at offset {offset}");
                    }

                    allData.AddRange(chunk);
                }

                // Read page 1 (offsets 256-511)
                for (ushort offset = 0; offset < 256; offset += MAX_READ_LENGTH)
                {
                    byte chunkSize = (byte)Math.Min(MAX_READ_LENGTH, 256 - offset);
                    byte[] chunk = ReadSPD(address, (ushort)(256 + offset), chunkSize);

                    if (chunk == null || chunk.Length != chunkSize)
                    {
                        throw new InvalidOperationException($"Failed to read DDR4 page 1 at offset {offset}");
                    }

                    allData.AddRange(chunk);
                }
            }
            else
            {
                // DDR3 or older: 256 bytes, single page
                for (ushort offset = 0; offset < size; offset += MAX_READ_LENGTH)
                {
                    byte chunkSize = (byte)Math.Min(MAX_READ_LENGTH, size - offset);
                    byte[] chunk = ReadSPD(address, offset, chunkSize);

                    if (chunk == null || chunk.Length != chunkSize)
                    {
                        throw new InvalidOperationException($"Failed to read at offset {offset}");
                    }

                    allData.AddRange(chunk);
                }
            }

            return allData.ToArray();
        }

        #endregion

        #region SPD Writing Commands

        /// <summary>
        /// Writes a single byte to SPD EEPROM
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <param name="offset">Byte offset (0-1023)</param>
        /// <param name="value">Byte value to write</param>
        /// <returns>True if successful</returns>
        public bool WriteSPDByte(byte address, ushort offset, byte value)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be in range 0x{SPD_ADDRESS_MIN:X2}-0x{SPD_ADDRESS_MAX:X2}", nameof(address));

            if (offset > 1023)
                throw new ArgumentException("Offset must be 0-1023", nameof(offset));

            byte offsetLow = (byte)(offset & 0xFF);
            byte offsetHigh = (byte)((offset >> 8) & 0xFF);

            SendCommand(CMD_SPD_WRITE_BYTE, address, offsetLow, offsetHigh, value);
            var response = ReadResponse(1500); // Writing takes longer

            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Writes multiple bytes to SPD EEPROM (page write)
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <param name="offset">Byte offset (0-1023)</param>
        /// <param name="data">Data to write (max 16 bytes, must not cross page boundary)</param>
        /// <returns>True if successful</returns>
        public bool WriteSPDPage(byte address, ushort offset, byte[] data)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be in range 0x{SPD_ADDRESS_MIN:X2}-0x{SPD_ADDRESS_MAX:X2}", nameof(address));

            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty", nameof(data));

            if (data.Length > MAX_WRITE_LENGTH)
                throw new ArgumentException($"Data length must be {MAX_WRITE_LENGTH} bytes or less", nameof(data));

            if ((offset % 16 + data.Length) > 16)
                throw new ArgumentException("Write would cross 16-byte page boundary", nameof(data));

            if (offset > 1023)
                throw new ArgumentException("Offset must be 0-1023", nameof(offset));

            byte offsetLow = (byte)(offset & 0xFF);
            byte offsetHigh = (byte)((offset >> 8) & 0xFF);

            byte[] parameters = new byte[4 + data.Length];
            parameters[0] = address;
            parameters[1] = offsetLow;
            parameters[2] = offsetHigh;
            parameters[3] = (byte)data.Length;
            Array.Copy(data, 0, parameters, 4, data.Length);

            SendCommand(CMD_SPD_WRITE_PAGE, parameters);
            var response = ReadResponse(2000); // Page write takes longer

            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Writes entire SPD data with proper page handling
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <param name="data">Data to write (must match SPD size)</param>
        /// <returns>True if successful</returns>
        public bool WriteEntireSPD(byte address, byte[] data)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be in range 0x{SPD_ADDRESS_MIN:X2}-0x{SPD_ADDRESS_MAX:X2}", nameof(address));

            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty", nameof(data));

            int expectedSize = GetSPDSizeBytes(address);
            if (expectedSize == 0 || data.Length != expectedSize)
                throw new ArgumentException($"Data size {data.Length} doesn't match expected SPD size {expectedSize}", nameof(data));

            // Clear write protection for DDR4/DDR5
            if (DetectDDR4(address) || DetectDDR5(address))
            {
                ClearRSWP(address);
            }

            // For DDR4: Ensure we're on page 0 before starting
            if (DetectDDR4(address))
            {
                // Write page 0 (offsets 0-255)
                for (ushort offset = 0; offset < 256; offset += MAX_WRITE_LENGTH)
                {
                    int chunkSize = Math.Min(MAX_WRITE_LENGTH, 256 - offset);
                    byte[] chunk = new byte[chunkSize];
                    Array.Copy(data, offset, chunk, 0, chunkSize);

                    if (!WriteSPDPage(address, offset, chunk))
                    {
                        throw new InvalidOperationException($"Failed to write DDR4 page 0 at offset {offset}");
                    }

                    // Small delay between writes
                    Thread.Sleep(20);
                }

                // Write page 1 (offsets 256-511)
                for (ushort offset = 0; offset < 256; offset += MAX_WRITE_LENGTH)
                {
                    int chunkSize = Math.Min(MAX_WRITE_LENGTH, 256 - offset);
                    byte[] chunk = new byte[chunkSize];
                    Array.Copy(data, 256 + offset, chunk, 0, chunkSize);

                    if (!WriteSPDPage(address, (ushort)(256 + offset), chunk))
                    {
                        throw new InvalidOperationException($"Failed to write DDR4 page 1 at offset {offset}");
                    }

                    // Small delay between writes
                    Thread.Sleep(20);
                }
            }
            else if (DetectDDR5(address))
            {
                // DDR5: Write in 16-byte chunks
                for (ushort offset = 0; offset < data.Length; offset += MAX_WRITE_LENGTH)
                {
                    int chunkSize = Math.Min(MAX_WRITE_LENGTH, data.Length - offset);
                    byte[] chunk = new byte[chunkSize];
                    Array.Copy(data, offset, chunk, 0, chunkSize);

                    if (!WriteSPDPage(address, offset, chunk))
                    {
                        throw new InvalidOperationException($"Failed to write DDR5 at offset {offset}");
                    }

                    // Small delay between writes
                    Thread.Sleep(20);
                }
            }
            else
            {
                // DDR3 or older: Write in 16-byte chunks
                for (ushort offset = 0; offset < data.Length; offset += MAX_WRITE_LENGTH)
                {
                    int chunkSize = Math.Min(MAX_WRITE_LENGTH, data.Length - offset);
                    byte[] chunk = new byte[chunkSize];
                    Array.Copy(data, offset, chunk, 0, chunkSize);

                    if (!WriteSPDPage(address, offset, chunk))
                    {
                        throw new InvalidOperationException($"Failed to write at offset {offset}");
                    }

                    // Small delay between writes
                    Thread.Sleep(20);
                }
            }

            return true;
        }

        /// <summary>
        /// Tests write capability at a specific offset by inverting and restoring a byte
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <param name="offset">Byte offset to test</param>
        /// <returns>True if write successful</returns>
        public bool TestWrite(byte address, ushort offset)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be in range 0x{SPD_ADDRESS_MIN:X2}-0x{SPD_ADDRESS_MAX:X2}", nameof(address));

            byte offsetLow = (byte)(offset & 0xFF);
            byte offsetHigh = (byte)((offset >> 8) & 0xFF);

            SendCommand(CMD_SPD_WRITE_TEST, address, offsetLow, offsetHigh);
            var response = ReadResponse(2000); // Test involves 3 operations

            return response != null && response.Length > 0 && response[0] == 1;
        }

        #endregion

        #region Detection Commands

        /// <summary>
        /// Detects if a DDR4 module is present at the specified address
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <returns>True if DDR4 detected</returns>
        public bool DetectDDR4(byte address)
        {
            SendCommand(CMD_DDR4_DETECT, address);
            var response = ReadResponse();
            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Detects if a DDR5 module is present at the specified address
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <returns>True if DDR5 detected</returns>
        public bool DetectDDR5(byte address)
        {
            SendCommand(CMD_DDR5_DETECT, address);
            var response = ReadResponse();
            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Gets the SPD EEPROM size code
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <returns>Size code (0=unknown, 1=256 bytes, 2=512 bytes, 3=1024 bytes)</returns>
        public byte GetSPDSizeCode(byte address)
        {
            SendCommand(CMD_SPD_SIZE, address);
            var response = ReadResponse();

            if (response == null || response.Length == 0)
                return 0;

            return response[0];
        }

        /// <summary>
        /// Gets the SPD EEPROM size in bytes
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <returns>Size in bytes (256, 512, 1024), or 0 if unknown</returns>
        public int GetSPDSizeBytes(byte address)
        {
            byte sizeCode = GetSPDSizeCode(address);

            switch (sizeCode)
            {
                case 1: return 256;
                case 2: return 512;
                case 3: return 1024;
                default: return 0;
            }
        }

        /// <summary>
        /// Detects module type and returns information
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <returns>Module information</returns>
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
                info.Size = 0;
            }

            return info;
        }

        #endregion

        #region SPD5 Hub Register Commands

        /// <summary>
        /// Reads a SPD5 hub management register
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <param name="register">Register number (0-127)</param>
        /// <returns>Register value, or 0xFF on error</returns>
        public byte ReadSPD5HubRegister(byte address, byte register)
        {
            if (register >= 128)
                throw new ArgumentException("Register must be 0-127", nameof(register));

            SendCommand(CMD_SPD5_HUB_REG, address, register, CMD_GET);
            var response = ReadResponse();

            if (response == null || response.Length == 0)
                return 0xFF;

            return response[0];
        }

        /// <summary>
        /// Writes a SPD5 hub management register
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <param name="register">Register number (MR11-MR13 only)</param>
        /// <param name="value">Value to write</param>
        /// <returns>True if successful</returns>
        public bool WriteSPD5HubRegister(byte address, byte register, byte value)
        {
            if (register >= 128)
                throw new ArgumentException("Register must be 0-127", nameof(register));

            // Only MR11-MR13 are writable (and some others, but we restrict for safety)
            if (!(register == MR11 || register == MR12 || register == MR13))
                throw new ArgumentException("Only registers MR11 (0x0B), MR12 (0x0C), and MR13 (0x0D) are writable", nameof(register));

            SendCommand(CMD_SPD5_HUB_REG, address, register, CMD_ENABLE, value);
            var response = ReadResponse();

            return response != null && response.Length > 0 && response[0] == 1;
        }

        #endregion

        #region Write Protection Commands

        /// <summary>
        /// Sets reversible software write protection for a block
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <param name="block">Block number (0-15 for DDR5, 0-3 for DDR3/DDR4)</param>
        /// <returns>True if successful</returns>
        public bool SetRSWP(byte address, byte block)
        {
            if (block > 15)
                throw new ArgumentException("Block must be 0-15", nameof(block));

            SendCommand(CMD_RSWP, address, block, CMD_ENABLE);
            var response = ReadResponse();

            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Clears all reversible software write protection
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <returns>True if successful</returns>
        public bool ClearRSWP(byte address)
        {
            SendCommand(CMD_RSWP, address, 0x00, CMD_DISABLE);
            var response = ReadResponse();

            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Gets the write protection status for a block
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <param name="block">Block number (0-15 for DDR5, 0-3 for DDR3/DDR4)</param>
        /// <returns>True if block is protected</returns>
        public bool GetRSWP(byte address, byte block)
        {
            if (block > 15)
                throw new ArgumentException("Block must be 0-15", nameof(block));

            SendCommand(CMD_RSWP, address, block, CMD_GET);
            var response = ReadResponse();

            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Gets RSWP support capabilities
        /// </summary>
        /// <returns>RSWP support information</returns>
        public RSWPSupport GetRSWPSupport()
        {
            SendCommand(CMD_RSWP_REPORT);
            var response = ReadResponse();

            if (response == null || response.Length == 0)
                return new RSWPSupport();

            byte mask = response[0];
            return new RSWPSupport
            {
                DDR5Supported = (mask & RSWP_DDR5) != 0,
                DDR4Supported = (mask & RSWP_DDR4) != 0,
                DDR3Supported = (mask & RSWP_DDR3) != 0
            };
        }

        /// <summary>
        /// Sets permanent software write protection (IRREVERSIBLE!)
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <returns>True if successful</returns>
        /// <remarks>
        /// WARNING: This is PERMANENT and IRREVERSIBLE!
        /// The EEPROM will become permanently read-only.
        /// This does NOT work on DDR4/DDR5 modules.
        /// </remarks>
        public bool SetPSWP(byte address)
        {
            SendCommand(CMD_PSWP, address, CMD_ENABLE);
            var response = ReadResponse();

            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Gets permanent software write protection status
        /// </summary>
        /// <param name="address">7-bit I2C address (0x50-0x57)</param>
        /// <returns>True if permanently protected</returns>
        public bool GetPSWP(byte address)
        {
            SendCommand(CMD_PSWP, address, CMD_GET);
            var response = ReadResponse();

            return response != null && response.Length > 0 && response[0] == 1;
        }

        #endregion

        #region Pin Control Commands

        /// <summary>
        /// Controls a GPIO pin
        /// </summary>
        /// <param name="pin">Pin type (PIN_HV_SWITCH or PIN_SA1_SWITCH)</param>
        /// <param name="state">State (CMD_ENABLE = on, CMD_DISABLE = off)</param>
        /// <returns>True if successful</returns>
        public bool SetPin(byte pin, byte state)
        {
            if (pin != PIN_HV_SWITCH && pin != PIN_SA1_SWITCH)
                throw new ArgumentException("Pin must be PIN_HV_SWITCH (0x00) or PIN_SA1_SWITCH (0x01)", nameof(pin));

            if (state != CMD_ENABLE && state != CMD_DISABLE && state != CMD_GET)
                throw new ArgumentException("State must be CMD_ENABLE (0x01), CMD_DISABLE (0x00), or CMD_GET (0xFF)", nameof(state));

            SendCommand(CMD_PIN_CONTROL, pin, state);
            var response = ReadResponse();

            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Gets the state of a GPIO pin
        /// </summary>
        /// <param name="pin">Pin type (PIN_HV_SWITCH or PIN_SA1_SWITCH)</param>
        /// <returns>Pin state (1 = on, 0 = off)</returns>
        public byte GetPin(byte pin)
        {
            if (pin != PIN_HV_SWITCH && pin != PIN_SA1_SWITCH)
                throw new ArgumentException("Pin must be PIN_HV_SWITCH (0x00) or PIN_SA1_SWITCH (0x01)", nameof(pin));

            SendCommand(CMD_PIN_CONTROL, pin, CMD_GET);
            var response = ReadResponse();

            if (response == null || response.Length == 0)
                throw new InvalidOperationException("Failed to get pin state");

            return response[0];
        }

        /// <summary>
        /// Resets all pins to default state
        /// </summary>
        /// <returns>True if successful</returns>
        public bool ResetPins()
        {
            SendCommand(CMD_PIN_RESET);
            var response = ReadResponse();

            return response != null && response.Length > 0 && response[0] == 1;
        }

        /// <summary>
        /// Enables high voltage (9V) for EEPROM programming
        /// </summary>
        /// <returns>True if successful</returns>
        public bool EnableHighVoltage()
        {
            return SetPin(PIN_HV_SWITCH, CMD_ENABLE);
        }

        /// <summary>
        /// Disables high voltage
        /// </summary>
        /// <returns>True if successful</returns>
        public bool DisableHighVoltage()
        {
            return SetPin(PIN_HV_SWITCH, CMD_DISABLE);
        }

        /// <summary>
        /// Gets high voltage state
        /// </summary>
        /// <returns>True if high voltage is enabled</returns>
        public bool GetHighVoltageState()
        {
            return GetPin(PIN_HV_SWITCH) == 1;
        }

        /// <summary>
        /// Controls SA1 pin state
        /// </summary>
        /// <param name="state">True to enable SA1, false to disable</param>
        /// <returns>True if successful</returns>
        public bool SetSA1State(bool state)
        {
            return SetPin(PIN_SA1_SWITCH, state ? CMD_ENABLE : CMD_DISABLE);
        }

        /// <summary>
        /// Gets SA1 pin state
        /// </summary>
        /// <returns>True if SA1 is enabled</returns>
        public bool GetSA1State()
        {
            return GetPin(PIN_SA1_SWITCH) == 1;
        }

        #endregion

        #region Internal EEPROM Commands

        /// <summary>
        /// Reads data from internal flash settings storage
        /// </summary>
        /// <param name="offset">Offset in flash (0-255)</param>
        /// <param name="length">Number of bytes to read (1-32)</param>
        /// <returns>Read data, or null on error</returns>
        public byte[] ReadInternalEEPROM(ushort offset, ushort length)
        {
            if (offset > 255)
                throw new ArgumentException("Offset must be 0-255", nameof(offset));

            if (length == 0 || length > MAX_EEPROM_READ)
                throw new ArgumentException($"Length must be 1-{MAX_EEPROM_READ}", nameof(length));

            if (offset + length > 256)
                throw new ArgumentException("Read would exceed flash bounds", nameof(length));

            byte offsetLow = (byte)(offset & 0xFF);
            byte offsetHigh = (byte)((offset >> 8) & 0xFF);
            byte lengthLow = (byte)(length & 0xFF);
            byte lengthHigh = (byte)((length >> 8) & 0xFF);

            SendCommand(CMD_EEPROM, CMD_GET, offsetLow, offsetHigh, lengthLow, lengthHigh);
            var response = ReadResponse();

            if (response == null || (response.Length == 1 && response[0] == 0))
                return null;

            return response;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Validates a 7-bit SPD I2C address
        /// </summary>
        public static bool IsValidSPDAddress(byte address)
        {
            return address >= SPD_ADDRESS_MIN && address <= SPD_ADDRESS_MAX;
        }

        /// <summary>
        /// Validates a 7-bit PMIC I2C address
        /// </summary>
        public static bool IsValidPMICAddress(byte address)
        {
            return address >= PMIC_ADDRESS_MIN && address <= PMIC_ADDRESS_MAX;
        }

        /// <summary>
        /// Converts a size code to bytes
        /// </summary>
        public static int SizeCodeToBytes(byte sizeCode)
        {
            switch (sizeCode)
            {
                case 1: return 256;
                case 2: return 512;
                case 3: return 1024;
                default: return 0;
            }
        }

        /// <summary>
        /// Calculates checksum for data (used internally)
        /// </summary>
        public static byte CalculateChecksum(byte[] data)
        {
            byte checksum = 0;
            foreach (byte b in data)
                checksum += b;
            return checksum;
        }

        #endregion
    }

    #region Supporting Classes and Enums

    /// <summary>
    /// Module type enumeration
    /// </summary>
    public enum ModuleType
    {
        NotDetected,
        DDR3_Or_Other,
        DDR4,
        DDR5
    }

    /// <summary>
    /// Module information
    /// </summary>
    public class ModuleInfo
    {
        public byte Address { get; set; }
        public ModuleType Type { get; set; }
        public int Size { get; set; }

        public override string ToString()
        {
            return $"Address: 0x{Address:X2}, Type: {Type}, Size: {Size} bytes";
        }
    }

    /// <summary>
    /// RSWP support information
    /// </summary>
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

    /// <summary>
    /// Alert event arguments
    /// </summary>
    public class AlertEventArgs : EventArgs
    {
        public byte AlertCode { get; }
        public string AlertType { get; }

        public AlertEventArgs(byte alertCode)
        {
            AlertCode = alertCode;

            switch (alertCode)
            {
                case 0x21: // '!'
                    AlertType = "Ready";
                    break;
                case 0x2B: // '+'
                    AlertType = "Slave Count Increased";
                    break;
                case 0x2D: // '-'
                    AlertType = "Slave Count Decreased";
                    break;
                case 0x2F: // '/'
                    AlertType = "Clock Speed Increased";
                    break;
                case 0x5C: // '\'
                    AlertType = "Clock Speed Decreased";
                    break;
                default:
                    AlertType = $"Unknown (0x{alertCode:X2})";
                    break;
            }
        }

        public override string ToString()
        {
            return $"Alert: {AlertType} (0x{AlertCode:X2})";
        }
    }

    #endregion
}