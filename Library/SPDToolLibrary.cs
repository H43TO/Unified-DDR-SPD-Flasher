// Added: New helper methods 

using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SPDTool
{
    /// <summary>
    /// Communication library for the RP2040‑based SPD programmer – version 3.8.5
    /// Provides read/write access to SPD EEPROMs (DDR3/4/5) and PMIC registers.
    /// Firmware version expected:  20260308
    ///
    /// <para><b>Thread safety:</b> All serial I/O is guarded by <c>_serialLock</c>.
    /// Public methods may be called from worker threads; UI callbacks should marshal
    /// back to the UI thread themselves.</para>
    /// </summary>
    public class SPDToolDevice : IDisposable
    {
        #region Constants

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

        // Alert codes
        private const byte ALERT_READY = 0x21;     // '!'
        private const byte ALERT_SLAVE_INC = 0x2B; // '+'
        private const byte ALERT_SLAVE_DEC = 0x2D; // '-'
        private const byte ALERT_CLOCK_INC = 0x2F; // '/'
        private const byte ALERT_CLOCK_DEC = 0x5C; // '\'

        // Pin identifiers
        public const byte PIN_HV_SWITCH = 0x00;      // HV programming enable (opto-coupler)
        public const byte PIN_SA1_SWITCH = 0x01;     // SA1 address select
        public const byte PIN_DEV_STATUS = 0x02;     // Status LED
        public const byte PIN_HV_CONVERTER = 0x03;   // HV boost converter enable
        public const byte PIN_DDR5_VIN_CTRL = 0x04;  // DDR5 VIN power enable
        public const byte PIN_PMIC_CTRL = 0x05;      // PMIC PWR_EN line
        public const byte PIN_PMIC_FLAG = 0x06;      // PMIC PWR_GOOD input
        public const byte PIN_RFU1 = 0x07;           // Reserved
        public const byte PIN_RFU2 = 0x08;           // Reserved

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

        // Firmware limits
        public const int MAX_READ_LENGTH = 64;
        public const int MAX_WRITE_LENGTH = 16;
        public const int MAX_DEVICE_NAME_LENGTH = 16;
        public const int MAX_EEPROM_READ = 32;

        // I2C clock modes
        public const byte I2C_CLOCK_100KHZ = 0;
        public const byte I2C_CLOCK_400KHZ = 1;
        public const byte I2C_CLOCK_1MHZ = 2;

        // PMIC MTP burn timing (JEDEC PMIC5100 §17.3.3)
        private const int PMIC_BURN_WAIT_MS = 200;
        private const int PMIC_BURN_POLL_TIMEOUT_MS = 1000;
        private const byte PMIC_BURN_COMPLETE_TOKEN = 0x5A;

        // PMIC vendor region unlock password (JEDEC default)
        private const byte PMIC_PASS_LSB_DEFAULT = 0x73;
        private const byte PMIC_PASS_MSB_DEFAULT = 0x94;

        // PMIC register map (JEDEC PMIC5100)
        private const byte PMIC_REG_PASS_LSB = 0x37;
        private const byte PMIC_REG_PASS_MSB = 0x38;
        private const byte PMIC_REG_PASS_CTRL = 0x39;
        private const byte PMIC_REG_VENDOR_START = 0x40;
        private const byte PMIC_REG_PG_STATUS = 0x08;
        private const byte PMIC_REG_PG_STATUS2 = 0x09;
        private const byte PMIC_REG_VR_CTRL = 0x32;
        private const byte PMIC_REG_PG_VOUT1V = 0x33;

        #endregion

        #region Fields

        private readonly SerialPort _serialPort;
        private readonly object _serialLock = new object();
        private bool _disposed;

        #endregion

        #region Events

        public event EventHandler<AlertEventArgs> AlertReceived;

        #endregion

        #region Constructor & Dispose

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
            Thread.Sleep(2500); // RP2040 boot time
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
            if (_disposed) return;
            if (disposing && _serialPort != null)
            {
                try
                {
                    var closeTask = Task.Run(() =>
                    {
                        try { _serialPort.Close(); _serialPort.Dispose(); }
                        catch { /* ignore */ }
                    });
                    if (!closeTask.Wait(TimeSpan.FromMilliseconds(250)))
                        System.Diagnostics.Debug.WriteLine("Serial port close timed out – abandoned.");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error disposing serial port: {ex.Message}");
                }
                finally { _disposed = true; }
            }
        }

        #endregion

        #region Low‑level communication

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
                        case RESPONSE_MARKER: return ReadFramedResponse();
                        case ALERT_MARKER: ReadAndRaiseAlert(); continue;
                        case READY_MARKER: return new[] { READY_MARKER };
                        case UNKNOWN_MARKER: throw new InvalidOperationException("Unsupported hardware.");
                        default: continue;
                    }
                }
                throw new TimeoutException($"No response in {timeoutMs} ms.");
            }
        }

        private byte[] ReadFramedResponse()
        {
            var length = ReadByteWithTimeout(50, "length");
            if (length == 0) return Array.Empty<byte>();

            var data = new byte[length];
            int totalRead = 0;
            var deadline = DateTime.Now.AddMilliseconds(100);
            while (totalRead < length && DateTime.Now < deadline)
            {
                if (_serialPort.BytesToRead > 0)
                    totalRead += _serialPort.Read(data, totalRead, length - totalRead);
                else
                    Thread.Sleep(1);
            }
            if (totalRead < length)
                throw new TimeoutException($"Incomplete data: {totalRead}/{length} bytes.");

            var receivedChecksum = ReadByteWithTimeout(50, "checksum");
            var calculated = (byte)data.Sum(b => b);
            if (calculated != receivedChecksum)
                throw new InvalidOperationException($"Checksum mismatch: dev 0x{receivedChecksum:X2}, calc 0x{calculated:X2}.");

            return data;
        }

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

        private void ReadAndRaiseAlert()
        {
            try { OnAlertReceived(new AlertEventArgs(ReadByteWithTimeout(50, "alert code"))); }
            catch (TimeoutException) { /* ignore */ }
        }

        protected virtual void OnAlertReceived(AlertEventArgs e) => AlertReceived?.Invoke(this, e);

        #endregion

        #region Basic device commands

        public bool Ping()
        {
            SendCommand(CMD_PING);
            var response = ReadResponse(500);
            return response?.Length > 0 && response[0] == READY_MARKER;
        }

        public uint GetVersion()
        {
            SendCommand(CMD_VERSION);
            var response = ReadResponse();
            if (response?.Length != 4)
                throw new InvalidOperationException($"Version response invalid: {response?.Length ?? 0} bytes.");
            return BitConverter.ToUInt32(response, 0);
        }

        public bool Test()
        {
            SendCommand(CMD_TEST);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        public string GetDeviceName()
        {
            SendCommand(CMD_NAME, CMD_GET);
            var response = ReadResponse();
            if (response == null || response.Length == 0) return string.Empty;
            int length = Array.IndexOf(response, (byte)0);
            if (length < 0) length = response.Length;
            return Encoding.ASCII.GetString(response, 0, length);
        }

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

        public bool FactoryReset()
        {
            SendCommand(CMD_FACTORY_RESET);
            var response = ReadResponse(3000);
            return response?.Length > 0 && response[0] == 1;
        }

        #endregion

        #region I2C bus commands

        public List<byte> ScanBus()
        {
            SendCommand(CMD_SCAN_BUS);
            var response = ReadResponse();
            var devices = new List<byte>();
            if (response?.Length > 0)
            {
                var mask = response[0];
                for (int i = 0; i < 8; i++)
                    if ((mask & (1 << i)) != 0)
                        devices.Add((byte)(SPD_ADDRESS_MIN + i));
            }
            return devices;
        }

        public bool ProbeAddress(byte address)
        {
            SendCommand(CMD_PROBE_ADDRESS, address);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        public byte GetI2CClockMode()
        {
            SendCommand(CMD_BUS_CLOCK, CMD_GET);
            var response = ReadResponse();
            if (response == null || response.Length == 0)
                throw new InvalidOperationException("Failed to get I2C clock mode.");
            return response[0];
        }

        public bool SetI2CClockMode(byte mode)
        {
            if (mode > I2C_CLOCK_1MHZ)
                throw new ArgumentException($"Mode must be {I2C_CLOCK_100KHZ}, {I2C_CLOCK_400KHZ}, or {I2C_CLOCK_1MHZ}.");
            SendCommand(CMD_BUS_CLOCK, mode);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        #endregion

        #region SPD reading

        public byte[] ReadSPD(byte address, ushort offset, byte length)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be 0x{SPD_ADDRESS_MIN:X2}‑0x{SPD_ADDRESS_MAX:X2}.");
            ValidateReadParams(offset, length);

            var offsetLow = (byte)(offset & 0xFF);
            var offsetHigh = (byte)((offset >> 8) & 0xFF);
            SendCommand(CMD_SPD_READ_PAGE, address, offsetLow, offsetHigh, length);

            var response = ReadResponse();
            return IsErrorResponse(response) ? null : response;
        }

        public byte[] ReadI2CDevice(byte address, ushort offset, byte length)
        {
            ValidateReadParams(offset, length);
            var offsetLow = (byte)(offset & 0xFF);
            var offsetHigh = (byte)((offset >> 8) & 0xFF);
            SendCommand(CMD_SPD_READ_PAGE, address, offsetLow, offsetHigh, length);
            var response = ReadResponse();
            return IsErrorResponse(response) ? null : response;
        }

        public byte[] ReadEntireSPD(byte address)
        {
            int size = GetSPDSizeBytes(address);
            if (size == 0) return null;

            var data = new List<byte>(size);
            bool isDdr5 = DetectDDR5(address);
            bool isDdr4 = DetectDDR4(address);
            int pages = isDdr5 ? 4 : (isDdr4 ? 2 : 1);

            for (int page = 0; page < pages; page++)
            {
                var pageBase = (ushort)(page * 256);
                for (ushort offset = 0; offset < 256; offset += MAX_READ_LENGTH)
                {
                    byte chunkSize = (byte)Math.Min(MAX_READ_LENGTH, 256 - offset);
                    var chunk = ReadSPD(address, (ushort)(pageBase + offset), chunkSize);
                    if (chunk == null || chunk.Length != chunkSize)
                        throw new InvalidOperationException($"Read failed at page {page}, offset {offset}.");
                    data.AddRange(chunk);
                }
            }
            if (data.Count > size) data.RemoveRange(size, data.Count - size);
            return data.ToArray();
        }

        #endregion

        #region SPD writing

        public bool WriteSPDByte(byte address, ushort offset, byte value)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be 0x{SPD_ADDRESS_MIN:X2}‑0x{SPD_ADDRESS_MAX:X2}.");
            if (offset > 1023) throw new ArgumentException("Offset must be 0‑1023.");

            var offsetLow = (byte)(offset & 0xFF);
            var offsetHigh = (byte)((offset >> 8) & 0xFF);
            SendCommand(CMD_SPD_WRITE_BYTE, address, offsetLow, offsetHigh, value);
            var response = ReadResponse(1500);
            return response?.Length > 0 && response[0] == 1;
        }

        public bool WriteI2CDevice(byte address, ushort offset, byte value)
        {
            if (!IsValidPMICAddress(address) && !IsValidSPDAddress(address))
                throw new ArgumentException("Address must be in PMIC or SPD range.");
            if (offset > 255) throw new ArgumentException("Offset must be 0‑255.");

            SendCommand(CMD_PMIC_WRITEREG, address, (byte)offset, value);
            var response = ReadResponse(1500);
            return response?.Length > 0 && response[0] == 1;
        }

        public bool WriteSPDPage(byte address, ushort offset, byte[] data)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be 0x{SPD_ADDRESS_MIN:X2}‑0x{SPD_ADDRESS_MAX:X2}.");
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty.");
            if (data.Length > MAX_WRITE_LENGTH)
                throw new ArgumentException($"Max write length is {MAX_WRITE_LENGTH}.");
            if ((offset % 16) + data.Length > 16)
                throw new ArgumentException("Write would cross a 16‑byte page boundary.");
            if (offset > 1023) throw new ArgumentException("Offset must be 0‑1023.");

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

        public bool WriteEntireSPD(byte address, byte[] data)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be 0x{SPD_ADDRESS_MIN:X2}‑0x{SPD_ADDRESS_MAX:X2}.");
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty.");

            int expectedSize = GetSPDSizeBytes(address);
            if (expectedSize == 0 || data.Length != expectedSize)
                throw new ArgumentException($"Data size {data.Length} does not match SPD size {expectedSize}.");

            if (DetectDDR4(address) || DetectDDR5(address))
                ClearRSWP(address);

            for (ushort offset = 0; offset < data.Length; offset += MAX_WRITE_LENGTH)
            {
                int chunkSize = Math.Min(MAX_WRITE_LENGTH, data.Length - offset);
                var chunk = new byte[chunkSize];
                Array.Copy(data, offset, chunk, 0, chunkSize);
                if (!WriteSPDPage(address, offset, chunk))
                    throw new InvalidOperationException($"Write failed at offset {offset}.");
                Thread.Sleep(20);
            }
            return true;
        }

        public bool TestWrite(byte address, ushort offset)
        {
            if (!IsValidSPDAddress(address))
                throw new ArgumentException($"Address must be 0x{SPD_ADDRESS_MIN:X2}‑0x{SPD_ADDRESS_MAX:X2}.");
            var offsetLow = (byte)(offset & 0xFF);
            var offsetHigh = (byte)((offset >> 8) & 0xFF);
            SendCommand(CMD_SPD_WRITE_TEST, address, offsetLow, offsetHigh);
            var response = ReadResponse(2000);
            return response?.Length > 0 && response[0] == 1;
        }

        #endregion

        #region Detection commands

        public bool DetectDDR4(byte address)
        {
            SendCommand(CMD_DDR4_DETECT, address);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        public bool DetectDDR5(byte address)
        {
            SendCommand(CMD_DDR5_DETECT, address);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        public byte GetSPDSizeCode(byte address)
        {
            SendCommand(CMD_SPD_SIZE, address);
            var response = ReadResponse();
            return response?[0] ?? 0;
        }

        public int GetSPDSizeBytes(byte address) =>
            GetSPDSizeCode(address) switch { 1 => 256, 2 => 512, 3 => 1024, _ => 0 };

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
            else info.Type = ModuleType.NotDetected;
            return info;
        }

        #endregion

        #region SPD5 hub registers

        public byte ReadSPD5HubRegister(byte address, byte register)
        {
            if (register >= 128) throw new ArgumentException("Register must be 0‑127.");
            SendCommand(CMD_SPD5_HUB_REG, address, register, CMD_GET);
            var response = ReadResponse();
            return response?[0] ?? 0xFF;
        }

        public bool WriteSPD5HubRegister(byte address, byte register, byte value)
        {
            if (register >= 128) throw new ArgumentException("Register must be 0‑127.");
            if (register != MR11 && register != MR12 && register != MR13)
                throw new ArgumentException("Only MR11, MR12, MR13 are writable.");
            SendCommand(CMD_SPD5_HUB_REG, address, register, CMD_ENABLE, value);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        #endregion

        #region Write protection

        public bool SetRSWP(byte address, byte block)
        {
            if (block > 15) throw new ArgumentException("Block must be 0‑15.");
            SendCommand(CMD_RSWP, address, block, CMD_ENABLE);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        public bool ClearRSWP(byte address)
        {
            SendCommand(CMD_RSWP, address, 0x00, CMD_DISABLE);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        public bool GetRSWP(byte address, byte block)
        {
            if (block > 15) throw new ArgumentException("Block must be 0‑15.");
            SendCommand(CMD_RSWP, address, block, CMD_GET);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

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

        public bool SetPSWP(byte address)
        {
            SendCommand(CMD_PSWP, address, CMD_ENABLE);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        public bool GetPSWP(byte address)
        {
            SendCommand(CMD_PSWP, address, CMD_GET);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        #endregion

        #region PMIC commands

        public byte[] ReadPMICDevice(byte address, ushort offset)
        {
            if (offset > 1023) throw new ArgumentException("Offset must be 0‑1023.");
            SendCommand(CMD_PMIC_READREG, address, (byte)(offset & 0xFF));
            var response = ReadResponse();
            return IsErrorResponse(response) ? null : response;
        }

        public byte[] ReadPMICADC(byte pmicAddress, byte writeOffset, byte[] writeValues, byte readOffset)
        {
            if (writeValues == null || writeValues.Length == 0)
                throw new ArgumentException("writeValues cannot be null or empty.");

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

        public bool UnlockVendorRegion(byte pmicAddress,
            byte passLsb = PMIC_PASS_LSB_DEFAULT,
            byte passMsb = PMIC_PASS_MSB_DEFAULT)
        {
            if (!EnableProgMode(pmicAddress)) return false;

            WriteI2CDevice(pmicAddress, PMIC_REG_PASS_LSB, passLsb);
            WriteI2CDevice(pmicAddress, PMIC_REG_PASS_MSB, passMsb);
            WriteI2CDevice(pmicAddress, PMIC_REG_PASS_CTRL, 0x40);

            var stat = ReadPMICDevice(pmicAddress, 0x45);
            return stat != null && stat.Length > 0 && stat[0] != 0;
        }

        public bool LockVendorRegion(byte pmicAddress)
        {
            return WriteI2CDevice(pmicAddress, PMIC_REG_PASS_CTRL, 0x00);
        }

        public bool ChangePMICPassword(byte pmicAddress,
            byte currentLsb, byte currentMsb,
            byte newLsb, byte newMsb)
        {
            if (!UnlockVendorRegion(pmicAddress, currentLsb, currentMsb))
                return false;

            WriteI2CDevice(pmicAddress, PMIC_REG_PASS_LSB, newLsb);
            WriteI2CDevice(pmicAddress, PMIC_REG_PASS_MSB, newMsb);
            WriteI2CDevice(pmicAddress, PMIC_REG_PASS_CTRL, 0x84);
            Thread.Sleep(PMIC_BURN_WAIT_MS);
            LockVendorRegion(pmicAddress);

            return UnlockVendorRegion(pmicAddress, newLsb, newMsb);
        }

        public bool EnableProgrammableMode(byte pmicAddress)
        {
            var reg = ReadPMICDevice(pmicAddress, 0x2F);
            if (reg == null || reg.Length == 0) return false;
            byte newVal = (byte)(reg[0] | 0x04);
            if (!WriteI2CDevice(pmicAddress, 0x2F, newVal)) return false;
            var verify = ReadPMICDevice(pmicAddress, 0x2F);
            return verify != null && verify.Length > 0 && (verify[0] & 0x04) != 0;
        }

        public bool EnableFullAccess(byte pmicAddress)
        {
            const byte Status_ofst = 0x5E;
            var status = ReadPMICDevice(pmicAddress, Status_ofst);
            if (status == null || status.Length == 0) return false;
            if (status[0] != 0) return true;
            return UnlockVendorRegion(pmicAddress);
        }

        public string GetPMICType(byte pmicAddress)
        {
            var reg3B = ReadPMICDevice(pmicAddress, 0x3B);
            var reg23 = ReadPMICDevice(pmicAddress, 0x23);
            if (reg3B == null || reg3B.Length == 0 || reg23 == null || reg23.Length == 0)
                return "Unknown";

            if (reg23[0] == 0)
                return (reg3B[0] & 0x40) == 0 ? "PMIC5100" : "PMIC5120";

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

        private bool IsPMIC5200(byte pmicAddress)
        {
            var reg3E = ReadPMICDevice(pmicAddress, 0x3E);
            return reg3E != null && reg3E.Length > 0 && reg3E[0] != 0;
        }

        public string GetPMICMode(byte pmicAddress)
        {
            try
            {
                var reg2F = ReadPMICDevice(pmicAddress, 0x2F);
                if (reg2F == null || reg2F.Length == 0) return "ERROR";
                bool isProgrammable = (reg2F[0] & 0x04) != 0;
                var stat = ReadPMICDevice(pmicAddress, 0x45);
                bool vendorUnlocked = stat != null && stat.Length > 0 && stat[0] != 0;
                return vendorUnlocked ? "Manufacturer Access"
                     : isProgrammable ? "Programmable"
                     : "Locked";
            }
            catch { return "ERROR"; }
        }

        public bool RebootDIMM()
        {
            try
            {
                if (SetPin(PIN_DDR5_VIN_CTRL, CMD_GET) != true) SetPin(PIN_DDR5_VIN_CTRL, CMD_ENABLE);
                SetPin(PIN_DDR5_VIN_CTRL, CMD_DISABLE);
                Thread.Sleep(1000);
                SetPin(PIN_DDR5_VIN_CTRL, CMD_ENABLE);
                return SetPin(PIN_DDR5_VIN_CTRL, CMD_GET);
            }
            catch { return false; }
        }

        // MTP Vendor-Region Programming
        public bool BurnBlock(byte pmicAddress, int blockNumber, byte[] blockData)
        {
            if (blockData == null) throw new ArgumentNullException(nameof(blockData));
            if (blockNumber != 99 && (blockNumber < 0 || blockNumber > 2))
                throw new ArgumentException("blockNumber must be 0, 1, 2, or 99.");
            if (blockNumber != 99 && blockData.Length < 16)
                throw new ArgumentException("blockData must be at least 16 bytes.");
            if (blockNumber == 99 && blockData.Length < 256)
                throw new ArgumentException("blockData must be 256 bytes for full dump.");

            try
            {
                if (blockNumber == 99)
                {
                    if (!UnlockVendorRegion(pmicAddress)) return false;
                    for (int reg = 0x00; reg <= 0xFF; reg++)
                        if (!WriteI2CDevice(pmicAddress, (ushort)reg, blockData[reg]))
                            return false;
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
                    LockVendorRegion(pmicAddress);
                    return true;
                }
                else
                {
                    byte[] chunk = new byte[16];
                    Array.Copy(blockData, 0, chunk, 0, 16);
                    if (!UnlockVendorRegion(pmicAddress)) return false;
                    bool result = BurnSingleBlock(pmicAddress, blockNumber, chunk);
                    LockVendorRegion(pmicAddress);
                    return result;
                }
            }
            catch
            {
                try { LockVendorRegion(pmicAddress); } catch { }
                throw;
            }
        }

        public bool WritePMICVendorBlock(byte pmicAddress, int blockNumber, byte[] fullDump)
        {
            if (fullDump == null || fullDump.Length < 256)
                throw new ArgumentException("fullDump must be 256 bytes.");
            if (blockNumber < 0 || blockNumber > 2)
                throw new ArgumentException("blockNumber must be 0, 1, or 2.");
            byte[] chunk = new byte[16];
            Array.Copy(fullDump, 0x40 + blockNumber * 16, chunk, 0, 16);
            return BurnBlock(pmicAddress, blockNumber, chunk);
        }

        public bool WritePMICFullDump(byte pmicAddress, byte[] fullDump)
        {
            if (fullDump == null || fullDump.Length < 256)
                throw new ArgumentException("fullDump must be 256 bytes.");
            return BurnBlock(pmicAddress, 99, fullDump);
        }

        private bool BurnSingleBlock(byte pmicAddress, int blockIndex, byte[] data)
        {
            byte[] burnCmds = { 0x81, 0x82, 0x85 };
            byte burnCmd = burnCmds[blockIndex];
            byte baseReg = (byte)(0x40 + blockIndex * 16);

            for (int i = 0; i < 16; i++)
                if (!WriteI2CDevice(pmicAddress, (ushort)(baseReg + i), data[i]))
                    return false;

            if (!WriteI2CDevice(pmicAddress, PMIC_REG_PASS_CTRL, burnCmd))
                return false;

            Thread.Sleep(PMIC_BURN_WAIT_MS);

            var deadline = DateTime.Now.AddMilliseconds(PMIC_BURN_POLL_TIMEOUT_MS);
            bool complete = false;
            while (DateTime.Now < deadline)
            {
                var result = ReadPMICDevice(pmicAddress, PMIC_REG_PASS_CTRL);
                if (result != null && result.Length > 0 && result[0] == PMIC_BURN_COMPLETE_TOKEN)
                {
                    complete = true;
                    break;
                }
                Thread.Sleep(25);
            }
            if (!complete) return false;

            // Restore unlock after burn
            return WriteI2CDevice(pmicAddress, PMIC_REG_PASS_CTRL, 0x40);
        }

        public bool EnableProgMode(byte pmicAddress)
        {
            var reg2F = ReadPMICDevice(pmicAddress, 0x2F);
            var reg32 = ReadPMICDevice(pmicAddress, 0x32);
            if (reg2F == null || reg2F.Length == 0 || reg32 == null || reg32.Length == 0)
                return false;

            if ((reg2F[0] & 0x04) != 0) return true; // already programmable

            bool vrEnabled = (reg32[0] & 0x80) != 0;
            if (vrEnabled)
            {
                SetPin(PIN_PMIC_CTRL, 0x00);
                Thread.Sleep(100);
            }

            byte new2F = (byte)(reg2F[0] | 0x04);
            if (!WriteI2CDevice(pmicAddress, 0x2F, new2F))
            {
                if (vrEnabled) SetPin(PIN_PMIC_CTRL, 0x01);
                return false;
            }

            if (vrEnabled)
            {
                SetPin(PIN_PMIC_CTRL, 0x01);
                Thread.Sleep(300);
            }

            var verify = ReadPMICDevice(pmicAddress, 0x2F);
            return verify != null && verify.Length > 0 && (verify[0] & 0x04) != 0;
        }

        // New high‑level PMIC helpers
        public bool GetVRegEnabled(byte pmicAddress)
        {
            var reg = ReadPMICDevice(pmicAddress, PMIC_REG_VR_CTRL);
            return reg != null && reg.Length > 0 && (reg[0] & 0x80) != 0;
        }

        public bool SetVRegEnable(byte pmicAddress, bool enable)
        {
            string mode = GetPMICMode(pmicAddress);
            if (mode == "Locked")
                return SetPin(PIN_PMIC_CTRL, enable ? CMD_ENABLE : CMD_DISABLE);

            var reg = ReadPMICDevice(pmicAddress, PMIC_REG_VR_CTRL);
            if (reg == null || reg.Length == 0) return false;
            byte newVal = enable ? (byte)(reg[0] | 0x80) : (byte)(reg[0] & ~0x80);
            return WriteI2CDevice(pmicAddress, PMIC_REG_VR_CTRL, newVal);
        }

        public bool ToggleVReg(byte pmicAddress, out bool newState)
        {
            bool current = GetVRegEnabled(pmicAddress);
            newState = !current;
            return SetVRegEnable(pmicAddress, newState);
        }

        public bool GetPGoodPinState() => GetPin(PIN_PMIC_FLAG) == 1;

        public bool SetPGoodOutputMode(byte pmicAddress, int mode)
        {
            if (mode < 0 || mode > 2) throw new ArgumentOutOfRangeException(nameof(mode));
            var reg = ReadPMICDevice(pmicAddress, PMIC_REG_VR_CTRL);
            if (reg == null || reg.Length == 0) return false;
            byte newVal = (byte)((reg[0] & ~0x18) | ((mode & 0x03) << 3));
            return WriteI2CDevice(pmicAddress, PMIC_REG_VR_CTRL, newVal);
        }

        public bool IsVendorRegionUnlocked(byte pmicAddress)
        {
            var stat = ReadPMICDevice(pmicAddress, 0x45);
            return stat != null && stat.Length > 0 && stat[0] != 0;
        }

        public bool GetProgMode(byte pmicAddress)
        {
            var reg = ReadPMICDevice(pmicAddress, 0x2F);
            return reg != null && reg.Length > 0 && (reg[0] & 0x04) != 0;
        }

        public string GetPMICPGoodStatus(byte pmicAddress)
        {
            try
            {
                var reg08 = ReadPMICDevice(pmicAddress, 0x08);
                var reg09 = ReadPMICDevice(pmicAddress, 0x09);
                var reg32 = ReadPMICDevice(pmicAddress, PMIC_REG_VR_CTRL);
                var reg33 = ReadPMICDevice(pmicAddress, PMIC_REG_PG_VOUT1V);
                if (reg08 == null || reg09 == null || reg32 == null || reg33 == null)
                    return "Read Error";

                bool swaFault = (reg08[0] & 0x20) != 0;
                bool swbFault = (reg08[0] & 0x08) != 0;
                bool swcFault = (reg08[0] & 0x04) != 0;
                bool ldo18Fault = (reg09[0] & 0x20) != 0;
                bool ldo10Fault = (reg33[0] & 0x04) != 0;

                var faults = new List<string>();
                if (swaFault) faults.Add("SWA not good");
                if (swbFault) faults.Add("SWB not good");
                if (swcFault) faults.Add("SWC not good");
                if (ldo18Fault) faults.Add("VOUT_1.8V not good");
                if (ldo10Fault) faults.Add("VOUT_1.0V not good");

                int pgMode = (reg32[0] >> 3) & 0x03;
                bool ioOD = (reg32[0] & 0x20) != 0;
                string pgControl = pgMode switch
                {
                    1 => "PWR_GOOD forced LOW by host",
                    2 => ioOD ? "PWR_GOOD forced open-drain (float)" : "PWR_GOOD forced HIGH",
                    _ => null
                };

                if (pgControl != null)
                    return faults.Count == 0 ? pgControl : $"{pgControl}; Faults: {string.Join(", ", faults)}";
                if (faults.Count == 0)
                    return "All rails good — PWR_GOOD asserted";
                return $"Fault: {string.Join(", ", faults)}";
            }
            catch (Exception ex)
            {
                return $"Read Error: {ex.Message}";
            }
        }

        public class PMICMeasurement
        {
            public string DeviceType { get; set; }
            public Dictionary<string, double> Voltages_mV { get; } = new();
            public Dictionary<string, double> Currents_mA { get; } = new();
            public Dictionary<string, double> Powers_mW { get; } = new();
            public double TotalPower_mW { get; set; } = double.NaN;
        }

        public PMICMeasurement ReadAllMeasurements(byte pmicAddress)
        {
            var result = new PMICMeasurement();
            try
            {
                result.DeviceType = GetPMICType(pmicAddress);
                var r1B = ReadPMICDevice(pmicAddress, 0x1B);
                var r1A = ReadPMICDevice(pmicAddress, 0x1A);
                var r32 = ReadPMICDevice(pmicAddress, 0x32);
                if (r1B == null || r1A == null || r32 == null) return null;

                bool powerMode = (r1B[0] & 0x40) != 0;
                bool totalPower = (r1A[0] & 0x02) != 0;
                double lsb = (r32[0] & 0x03) == 0 ? 125.0 : 31.25;

                var voltageChannels = GetVoltageChannelMap(result.DeviceType);
                var currentRegs = GetCurrentRegisterMap(result.DeviceType);

                if (voltageChannels.Any())
                {
                    var writeVals = voltageChannels.Values.Select(ch => (byte)(0x80 | (ch.code << 3))).ToArray();
                    var rawVoltages = ReadPMICADC(pmicAddress, 0x30, writeVals, 0x31);
                    if (rawVoltages?.Length == voltageChannels.Count)
                    {
                        int i = 0;
                        foreach (var kv in voltageChannels)
                            result.Voltages_mV[kv.Key] = rawVoltages[i++] * kv.Value.factor;
                    }
                }

                foreach (var rail in currentRegs)
                {
                    var raw = ReadPMICDevice(pmicAddress, rail.Value);
                    if (raw == null || raw.Length == 0) continue;
                    double value = raw[0] * lsb;
                    if (powerMode)
                        result.Powers_mW[rail.Key] = value;
                    else
                        result.Currents_mA[rail.Key] = value;
                }

                if (totalPower)
                {
                    var totalRaw = ReadPMICDevice(pmicAddress, 0x0C);
                    if (totalRaw?.Length > 0)
                        result.TotalPower_mW = totalRaw[0] * lsb;
                }
                return result;
            }
            catch { return null; }
        }

        private Dictionary<string, (byte code, double factor)> GetVoltageChannelMap(string deviceType)
        {
            var map = new Dictionary<string, (byte, double)>
            {
                ["VIN_BULK"] = (0x05, 70.0),
                ["VOUT_1.8V"] = (0x08, 15.0),
                ["VOUT_1.0V"] = (0x09, 15.0)
            };

            if (deviceType.StartsWith("PMIC50"))
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
            else if (deviceType.StartsWith("PMIC51"))
            {
                map["SWA"] = (0x00, 15.0);
                map["SWB"] = (0x02, 15.0);
                map["SWC"] = (0x03, 15.0);
            }
            else if (deviceType.StartsWith("PMIC52"))
            {
                map["SWA"] = (0x00, 15.0);
                map["SWD"] = (0x01, 15.0);
                map["SWB"] = (0x02, 15.0);
                map["SWC"] = (0x03, 15.0);
            }
            return map;
        }

        private Dictionary<string, byte> GetCurrentRegisterMap(string deviceType)
        {
            var map = new Dictionary<string, byte>();
            if (deviceType.StartsWith("PMIC50"))
            {
                map["SWA"] = 0x0C; map["SWB"] = 0x0D; map["SWC"] = 0x0E; map["SWD"] = 0x0F;
            }
            else if (deviceType.StartsWith("PMIC51"))
            {
                map["SWA"] = 0x0C; map["SWB"] = 0x0E; map["SWC"] = 0x0F;
            }
            else if (deviceType.StartsWith("PMIC52"))
            {
                map["SWA"] = 0x0C; map["SWD"] = 0x0D; map["SWB"] = 0x0E; map["SWC"] = 0x0F;
            }
            return map;
        }

        #endregion

        #region Pin control

        public bool SetPin(byte pin, byte state)
        {
            if (pin > PIN_RFU2) throw new ArgumentException($"Pin must be 0‑{PIN_RFU2}.");
            if (state != CMD_ENABLE && state != CMD_DISABLE && state != CMD_GET)
                throw new ArgumentException("State must be CMD_ENABLE, CMD_DISABLE, or CMD_GET.");

            SendCommand(CMD_PIN_CONTROL, pin, state);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        public byte GetPin(byte pin)
        {
            if (pin > PIN_RFU2) throw new ArgumentException($"Pin must be 0‑{PIN_RFU2}.");
            SendCommand(CMD_PIN_CONTROL, pin, CMD_GET);
            var response = ReadResponse();
            return response?[0] ?? 0;
        }

        public bool ResetPins()
        {
            SendCommand(CMD_PIN_RESET);
            var response = ReadResponse();
            return response?.Length > 0 && response[0] == 1;
        }

        public bool EnableHighVoltage() => SetPin(PIN_HV_SWITCH, CMD_ENABLE);
        public bool DisableHighVoltage() => SetPin(PIN_HV_SWITCH, CMD_DISABLE);
        public bool GetHighVoltageState() => GetPin(PIN_HV_SWITCH) == 1;
        public bool SetSA1State(bool state) => SetPin(PIN_SA1_SWITCH, state ? CMD_ENABLE : CMD_DISABLE);
        public bool GetSA1State() => GetPin(PIN_SA1_SWITCH) == 1;

        #endregion

        #region Internal EEPROM

        public byte[] ReadInternalEEPROM(ushort offset, ushort length)
        {
            if (offset > 255) throw new ArgumentException("Offset must be 0‑255.");
            if (length == 0 || length > MAX_EEPROM_READ)
                throw new ArgumentException($"Length must be 1‑{MAX_EEPROM_READ}.");
            if (offset + length > 256) throw new ArgumentException("Read would exceed storage bounds.");

            SendCommand(CMD_EEPROM, CMD_GET,
                (byte)(offset & 0xFF), (byte)((offset >> 8) & 0xFF),
                (byte)(length & 0xFF), (byte)((length >> 8) & 0xFF));
            var response = ReadResponse();
            return IsErrorResponse(response) ? null : response;
        }

        #endregion

        #region Helpers

        private static bool IsErrorResponse(byte[] response) => response == null || response.Length == 0;

        private static void ValidateReadParams(ushort offset, byte length)
        {
            if (length == 0 || length > MAX_READ_LENGTH)
                throw new ArgumentException($"Length must be 1‑{MAX_READ_LENGTH}.");
            if (offset > 1023) throw new ArgumentException("Offset must be 0‑1023.");
        }

        public static bool IsValidSPDAddress(byte address) =>
            address >= SPD_ADDRESS_MIN && address <= SPD_ADDRESS_MAX;

        public static bool IsValidPMICAddress(byte address) =>
            address >= PMIC_ADDRESS_MIN && address <= PMIC_ADDRESS_MAX;

        public static int SizeCodeToBytes(byte sizeCode) =>
            sizeCode switch { 1 => 256, 2 => 512, 3 => 1024, _ => 0 };

        public static byte CalculateChecksum(byte[] data) => (byte)data.Sum(b => b);

        #endregion
    }

    #region Supporting types

    public enum ModuleType { NotDetected, DDR3_Or_Other, DDR4, DDR5 }

    public class ModuleInfo
    {
        public byte Address { get; set; }
        public ModuleType Type { get; set; }
        public int Size { get; set; }
        public override string ToString() => $"Address: 0x{Address:X2}, Type: {Type}, Size: {Size} bytes";
    }

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