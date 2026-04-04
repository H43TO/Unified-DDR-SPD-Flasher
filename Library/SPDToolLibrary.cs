using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SPDTool
{
    /// <summary>
    /// Communication library for the RP2040‑based SPD programmer.
    /// Provides read/write access to SPD EEPROMs (DDR3/4/5) and PMIC registers.
    /// Firmware version expected: 20260307.
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
        private const byte PIN_HV_SWITCH = 0x00; // Controlls the opto-coupler that enables the high-voltage programming signals for DDR3/4. This gets controlled by the firmware automatically during DDR3/4 operations, but can be manually toggled for testing or custom use. If enabled, it creates a ~25ms HV pulse. Check FW source for details.
        private const byte PIN_SA1_SWITCH = 0x01;
        private const byte PIN_DEV_STATUS = 0x02; // Connected to an LED on the programmer board; can be used to indicate status, activity, or errors in a custom way.
        private const byte PIN_HV_CONVERTER = 0x03; // Connected to the high‑voltage boost converter's enable pin, which used for programming DDR3/4 modules. Must be enabled for DDR3/4 WP to be disable.
        private const byte PIN_DDR5_VIN_CTRL = 0x04; // Controlls the power supply to DDR5 modules. Must be enabled for DDR5 detection and access, otherwise the module will appear unresponsive. Does not affect DDR3/4 modules.
        private const byte PIN_PMIC_CTRL = 0x05; // Connected the PWR_EN line of the PMICs. Enabling this pin makes all the PMIC phases active, regardless of the PMIC's voltage regulator register settings. Refer to the datasheet/JEDEC spec for more info.
        private const byte PIN_PMIC_FLAG = 0x06; // Connected to the PMIC's PGOOD pin. Refer to the datasheet/JEDEC spec for more info.
        private const byte PIN_RFU1 = 0x07; // Reserved for future use; currently has no function. 
        private const byte PIN_RFU2 = 0x08; // Reserved for future use; currently has no function. 

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
            if (!_disposed)
            {
                if (disposing)
                    _serialPort?.Dispose();

                _disposed = true;
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
            //Console.WriteLine(BitConverter.ToString(response));
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
            //Console.WriteLine(BitConverter.ToString(response));
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

        /// <summary>Attempts to unlock full access to a protected PMIC (vendor‑specific).</summary>
        public bool EnableFullAccess(byte pmicAddress)
        {
            const byte LSB_ofst = 0x37;
            const byte MSB_ofst = 0x38;
            const byte PassCtrl_ofst = 0x39;
            const byte Status_ofst = 0x5E;

            const byte LSB = 0x73;
            const byte MSB = 0x94;
            const byte PassCtrl = 0x40;

            var status = ReadPMICDevice(pmicAddress, Status_ofst);
            if (status == null || status.Length == 0)
                return false;

            // If already unlocked, return true
            if (status[0] != 0)
                return true;

            // Perform unlock sequence
            WriteI2CDevice(pmicAddress, LSB_ofst, LSB);
            WriteI2CDevice(pmicAddress, MSB_ofst, MSB);
            WriteI2CDevice(pmicAddress, PassCtrl_ofst, PassCtrl);

            status = ReadPMICDevice(pmicAddress, Status_ofst);
            return status != null && status.Length > 0 && status[0] != 0;
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

        //TODO: implement PMIC vendor block programing as per JEDEC standard for PMIC5010/5100/5200(and other) pmics. We should write to the specific registers, then send the burn command to 0x39, then read 0x39 to confirm success. 
        //Registers below 0x40 are volatile and are configured by vendor bytes starting from reg 0x40. There registers are MTP(multiple time programable), so no need to worry about bricking.
        //The spacific values, that we wanna burn should be read from the current, loaded dump.
        public bool BurnBlock(byte pmicAddress, int blockNumber) 
        {
            // block number 0 should represent writing the whole vendor block
            // block number 99 should represent writing ALL of the registers(all 256), not just the vendor blocks

            return false;
        }

        //TODO: Implement based on JEDEC PMIC standard(PMIC5100), regarding Power Good register and pin config
        public string GetPMICPGoodStatus(byte pmicAddress)
        {
            return "to be implemented";
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
                    var codes = voltageChannels.Values.Select(ch => ch.code).ToArray();

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
                // Note: SWE/SWF would be >255 (16‑bit registers) – omitted here for brevity.
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