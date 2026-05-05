// §5: Full CLI mode. When invoked with arguments, runs headless and exits with
// a per-spec exit code. When invoked without arguments, launches the WinForms GUI.

using UDFCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UnifiedDDRFlasher
{
    static class Program
    {
        // Attach to the parent process console (cmd / PowerShell) so that
        // Console.Write* calls are visible when the exe is built as WinExe.
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        // §5.3 exit codes
        private const int EXIT_OK            = 0;
        private const int EXIT_NOT_FOUND     = 1;
        private const int EXIT_I2C_ERROR     = 2;
        private const int EXIT_VERIFY_FAIL   = 3;
        private const int EXIT_CRC_ERROR     = 4;
        private const int EXIT_WRITE_ERROR   = 5;
        private const int EXIT_USAGE         = 6;

        [STAThread]
        static int Main(string[] args)
        {
            if (args == null || args.Length == 0)
                return RunGui();
            return RunCli(args);
        }

        #region GUI entry

        private static int RunGui()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            try { Application.Run(new MainForm()); }
            catch (Exception ex) { ShowErrorDialog("Application Startup Error", ex); return 1; }
            return 0;
        }

        private static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
            => ShowErrorDialog("Unhandled Thread Exception", e.Exception);

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                ShowErrorDialog("Unhandled Exception", ex);
        }

        private static void ShowErrorDialog(string title, Exception ex)
        {
            string message = $"An error occurred:\n\n{ex.Message}\n\nType: {ex.GetType().Name}";
            if (ex.InnerException != null) message += $"\n\nInner Exception: {ex.InnerException.Message}";
            message += $"\n\nStack Trace:\n{ex.StackTrace}";
            try { MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch { Console.Error.WriteLine($"{title}: {ex}"); }
        }

        #endregion

        #region CLI entry

        // §5.1 parsed options
        private class CliOptions
        {
            public string Port;
            public bool   AutoDetect;
            public int    Baud        = 115200;
            public int    TimeoutMs   = 5000;
            public string OutputFormat = "text";
            public bool   Quiet;
            public string LogFile;
            public int    ReconnectTimeoutSec = 0; // 0 = disabled
            public string Command;
            public List<string> Positional = new List<string>();
            public Dictionary<string, string> Switches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Flags = new List<string>();
        }

        private static int RunCli(string[] args)
        {
            // WinExe processes have no console by default. Attach to the
            // parent terminal (cmd / PowerShell); allocate a new one only
            // if there is no parent console (e.g. double-clicked).
            if (!AttachConsole(-1 /* ATTACH_PARENT_PROCESS */))
                AllocConsole();

            CliOptions opts;
            try { opts = ParseArgs(args); }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Argument error: {ex.Message}");
                PrintUsage();
                return EXIT_USAGE;
            }

            if (string.IsNullOrEmpty(opts.Command))
            {
                PrintUsage();
                return EXIT_USAGE;
            }

            // --help / -h
            if (string.Equals(opts.Command, "help", StringComparison.OrdinalIgnoreCase) ||
                opts.Command == "--help" || opts.Command == "-h")
            {
                PrintUsage();
                return EXIT_OK;
            }

            // Commands that don't need a device connection
            if (string.Equals(opts.Command, "spd", StringComparison.OrdinalIgnoreCase) &&
                opts.Positional.Count > 0 &&
                string.Equals(opts.Positional[0], "parse-file", StringComparison.OrdinalIgnoreCase))
            {
                return CmdSpdParseFile(opts);
            }

            // Resolve port
            string port = opts.Port;
            if (string.IsNullOrEmpty(port))
            {
                if (opts.AutoDetect)
                {
                    port = AutoDetectPort();
                    if (port == null)
                    {
                        WriteLine(opts, "ERROR: No UDF device detected on any COM port.");
                        return EXIT_NOT_FOUND;
                    }
                    WriteLine(opts, $"[UDF] Auto-detected port {port}");
                }
                else
                {
                    Console.Error.WriteLine("--port is required (or use --auto-detect)");
                    return EXIT_USAGE;
                }
            }

            try
            {
                using (var device = new UDFDevice(port, opts.Baud))
                {
                    return DispatchCommand(device, port, opts);
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("CMD_PING"))
            {
                WriteLine(opts, $"ERROR: {ex.Message}");
                return EXIT_NOT_FOUND;
            }
            catch (IOException ex) when (opts.ReconnectTimeoutSec > 0)
            {
                // USB cable was yanked mid-command (or the RP2040 re-enumerated).
                // Poll for the port to reappear up to --reconnect-timeout seconds.
                // Zero overhead when reconnect is disabled (the branch isn't entered).
                WriteLine(opts, $"[UDF] Connection lost: {ex.Message}");
                WriteLine(opts, $"[UDF] Waiting up to {opts.ReconnectTimeoutSec}s for {port} to reconnect...");

                var deadline = DateTime.UtcNow.AddSeconds(opts.ReconnectTimeoutSec);
                while (DateTime.UtcNow < deadline)
                {
                    System.Threading.Thread.Sleep(300);
                    if (!System.IO.Ports.SerialPort.GetPortNames()
                            .Contains(port, StringComparer.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        // Port reappeared — re-open and re-dispatch the same command.
                        WriteLine(opts, $"[UDF] {port} reappeared, reconnecting...");
                        using (var device = new UDFDevice(port, opts.Baud))
                        {
                            return DispatchCommand(device, port, opts);
                        }
                    }
                    catch (Exception reconnectEx)
                    {
                        // Still not stable — keep waiting.
                        WriteLine(opts, $"[UDF] Reconnect attempt failed: {reconnectEx.Message}");
                    }
                }

                WriteLine(opts, $"ERROR: {port} did not reconnect within {opts.ReconnectTimeoutSec}s.");
                return EXIT_NOT_FOUND;
            }
            catch (Exception ex)
            {
                WriteLine(opts, $"ERROR: {ex.Message}");
                return EXIT_I2C_ERROR;
            }
        }

        private static int DispatchCommand(UDFDevice device, string port, CliOptions opts)
        {
            switch (opts.Command.ToLowerInvariant())
            {
                case "ping":          return CmdPing(device, port, opts);
                case "version":       return CmdVersion(device, port, opts);
                case "test":          return CmdTest(device, opts);
                case "name":          return CmdName(device, opts);
                case "i2c-speed":     return CmdI2cSpeed(device, opts);
                case "scan":          return CmdScan(device, opts);
                case "detect":        return CmdDetect(device, opts);
                case "rswp-support":  return CmdRswpSupport(device, opts);
                case "reboot-dimm":   return CmdRebootDimm(device, opts);
                case "spd":           return CmdSpd(device, opts);
                case "rswp":          return CmdRswp(device, opts);
                case "pmic":          return CmdPmic(device, opts);
                case "factory-reset": return CmdFactoryReset(device, opts);
                case "pin":           return CmdPin(device, opts);
                case "eeprom":        return CmdEeprom(device, opts);
                default:
                    Console.Error.WriteLine($"Unknown command: {opts.Command}");
                    PrintUsage();
                    return EXIT_USAGE;
            }
        }

        #endregion

        #region Argument parser

        private static CliOptions ParseArgs(string[] args)
        {
            var opts = new CliOptions();
            int i = 0;
            var positional = new List<string>();

            while (i < args.Length)
            {
                string a = args[i];
                switch (a)
                {
                    case "--port":        opts.Port = RequireValue(args, ++i, "--port"); break;
                    case "--auto-detect": opts.AutoDetect = true; break;
                    case "--baud":        opts.Baud = int.Parse(RequireValue(args, ++i, "--baud")); break;
                    case "--timeout":     opts.TimeoutMs = int.Parse(RequireValue(args, ++i, "--timeout")); break;
                    case "--reconnect-timeout": opts.ReconnectTimeoutSec = int.Parse(RequireValue(args, ++i, "--reconnect-timeout")); break;
                    case "--output-format":
                    {
                        string v = RequireValue(args, ++i, "--output-format");
                        if (v != "text" && v != "json" && v != "hex")
                            throw new ArgumentException("--output-format must be text|json|hex");
                        opts.OutputFormat = v;
                        break;
                    }
                    case "--quiet":    opts.Quiet = true; break;
                    case "--log-file": opts.LogFile = RequireValue(args, ++i, "--log-file"); break;

                    // Switches that take a value
                    case "--out":
                    case "--in":
                    case "--block":
                    case "--lsb":
                    case "--msb":
                    case "--cur-lsb":
                    case "--cur-msb":
                    case "--new-lsb":
                    case "--new-msb":
                    case "--offset":
                    case "--length":
                    case "--value":
                    case "--register":
                    case "--mode":
                        opts.Switches[a] = RequireValue(args, ++i, a);
                        break;

                    // Boolean flags
                    case "--no-verify":
                    case "--parsed":
                    case "--ticks":
                    case "--full":
                    case "--enable":
                    case "--disable":
                        opts.Flags.Add(a);
                        break;

                    default:
                        positional.Add(a);
                        break;
                }
                i++;
            }

            if (positional.Count > 0)
            {
                opts.Command = positional[0];
                for (int p = 1; p < positional.Count; p++) opts.Positional.Add(positional[p]);
            }
            return opts;
        }

        private static string RequireValue(string[] args, int idx, string optName)
        {
            if (idx >= args.Length) throw new ArgumentException($"{optName} requires a value");
            return args[idx];
        }

        private static byte ParseAddress(string s)
        {
            if (string.IsNullOrEmpty(s)) throw new ArgumentException("address required");
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToByte(s.Substring(2), 16);
            return Convert.ToByte(s, 16);
        }

        private static byte ParseByte(string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToByte(s.Substring(2), 16);
            if (s.All(char.IsDigit))
                return byte.Parse(s);
            return Convert.ToByte(s, 16);
        }

        private static ushort ParseRegister(string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt16(s.Substring(2), 16);
            if (s.All(char.IsDigit))
                return ushort.Parse(s);
            return Convert.ToUInt16(s, 16);
        }

        #endregion

        #region Output helpers

        private static void WriteLine(CliOptions opts, string line)
        {
            if (!opts.Quiet) Console.WriteLine(line);
            if (!string.IsNullOrEmpty(opts.LogFile))
                try { File.AppendAllText(opts.LogFile, line + Environment.NewLine); } catch { }
        }

        private static void WriteRaw(CliOptions opts, string s)
        {
            Console.Write(s);
            if (!string.IsNullOrEmpty(opts.LogFile))
                try { File.AppendAllText(opts.LogFile, s); } catch { }
        }

        // Minimal JSON emitter (avoids a NuGet dep).
        private static string ToJson(object o)
        {
            var sb = new StringBuilder();
            EmitJson(sb, o, 0);
            return sb.ToString();
        }

        private static void EmitJson(StringBuilder sb, object o, int depth)
        {
            string indent  = new string(' ', depth * 2);
            string indent1 = new string(' ', (depth + 1) * 2);
            if (o == null) { sb.Append("null"); return; }
            if (o is string s)  { sb.Append('"').Append(JsonEscape(s)).Append('"'); return; }
            if (o is bool   b)  { sb.Append(b ? "true" : "false"); return; }
            if (o is byte || o is sbyte || o is short || o is ushort ||
                o is int  || o is uint  || o is long  || o is ulong)
            { sb.Append(o.ToString()); return; }
            if (o is double d) { sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (o is float  f) { sb.Append(f.ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (o is IDictionary<string, object> dict)
            {
                sb.Append("{\n");
                int idx = 0;
                foreach (var kv in dict)
                {
                    sb.Append(indent1).Append('"').Append(JsonEscape(kv.Key)).Append("\": ");
                    EmitJson(sb, kv.Value, depth + 1);
                    if (idx++ < dict.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(indent).Append('}');
                return;
            }
            if (o is System.Collections.IEnumerable en)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in en)
                {
                    if (!first) sb.Append(", ");
                    EmitJson(sb, item, depth + 1);
                    first = false;
                }
                sb.Append(']');
                return;
            }
            sb.Append('"').Append(JsonEscape(o.ToString())).Append('"');
        }

        private static string JsonEscape(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:   sb.Append(c);      break;
                }
            }
            return sb.ToString();
        }

        #endregion

        #region Basic / diagnostic commands

        private static int CmdPing(UDFDevice device, string port, CliOptions opts)
        {
            bool ok  = device.Ping();
            uint ver = device.GetVersion();
            if (opts.OutputFormat == "json")
            {
                var obj = new Dictionary<string, object>
                {
                    ["status"]   = ok ? "ok" : "fail",
                    ["port"]     = port,
                    ["firmware"] = $"0x{ver:X8}"
                };
                WriteRaw(opts, ToJson(obj) + "\n");
            }
            else
            {
                WriteLine(opts, $"[UDF] firmware=0x{ver:X8} port={port}");
                WriteLine(opts, ok ? "OK" : "FAIL");
            }
            return ok ? EXIT_OK : EXIT_NOT_FOUND;
        }

        private static int CmdVersion(UDFDevice device, string port, CliOptions opts)
        {
            uint ver = device.GetVersion();
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object> { ["firmware"] = $"0x{ver:X8}" }) + "\n");
            else
                WriteLine(opts, $"firmware=0x{ver:X8}");
            return EXIT_OK;
        }

        private static int CmdTest(UDFDevice device, CliOptions opts)
        {
            bool ok = device.Test();
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object> { ["self_test"] = ok ? "pass" : "fail" }) + "\n");
            else
                WriteLine(opts, ok ? "Self-test: PASS" : "Self-test: FAIL");
            return ok ? EXIT_OK : EXIT_I2C_ERROR;
        }

        private static int CmdName(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count == 0 || opts.Positional[0] == "get")
            {
                string name = device.GetDeviceName();
                if (opts.OutputFormat == "json")
                    WriteRaw(opts, ToJson(new Dictionary<string, object> { ["name"] = name }) + "\n");
                else
                    WriteLine(opts, name);
                return EXIT_OK;
            }
            if (opts.Positional[0] == "set" && opts.Positional.Count >= 2)
            {
                bool ok = device.SetDeviceName(opts.Positional[1]);
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            Console.Error.WriteLine("usage: name [get|set <name>]");
            return EXIT_USAGE;
        }

        private static int CmdI2cSpeed(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count == 0)
            {
                byte mode = device.GetI2CClockMode();
                if (opts.OutputFormat == "json")
                    WriteRaw(opts, ToJson(new Dictionary<string, object>
                    { ["mode"] = mode, ["label"] = SpeedLabel(mode) }) + "\n");
                else
                    WriteLine(opts, $"i2c-speed={mode} ({SpeedLabel(mode)})");
                return EXIT_OK;
            }
            byte newMode = byte.Parse(opts.Positional[0]);
            bool set = device.SetI2CClockMode(newMode);
            WriteLine(opts, set ? $"OK i2c-speed={newMode} ({SpeedLabel(newMode)})" : "FAIL");
            return set ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static string SpeedLabel(byte mode) =>
            mode == 0 ? "100kHz" : mode == 1 ? "400kHz" : mode == 2 ? "1MHz" : "?";

        private static int CmdScan(UDFDevice device, CliOptions opts)
        {
            var spds  = device.ScanBus();
            var pmics = new List<byte>();
            for (byte a = 0x48; a <= 0x4F; a++)
                if (device.ProbeAddress(a)) pmics.Add(a);

            if (opts.OutputFormat == "json")
            {
                var obj = new Dictionary<string, object>
                {
                    ["spd"]  = spds .Select(b => $"0x{b:X2}").ToList(),
                    ["pmic"] = pmics.Select(b => $"0x{b:X2}").ToList()
                };
                WriteRaw(opts, ToJson(obj) + "\n");
            }
            else
            {
                WriteLine(opts, $"SPD:  {string.Join(" ", spds .Select(b => $"0x{b:X2}"))}");
                WriteLine(opts, $"PMIC: {string.Join(" ", pmics.Select(b => $"0x{b:X2}"))}");
            }
            return EXIT_OK;
        }

        private static int CmdDetect(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1) { Console.Error.WriteLine("usage: detect <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[0]);
            var info  = device.DetectModule(addr);
            if (opts.OutputFormat == "json")
            {
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["type"]    = info.Type.ToString(),
                    ["size"]    = info.Size
                }) + "\n");
            }
            else
            {
                WriteLine(opts, $"[SPD] address=0x{addr:X2} type={info.Type} size={info.Size}");
            }
            return info.Type == ModuleType.NotDetected ? EXIT_NOT_FOUND : EXIT_OK;
        }

        private static int CmdRswpSupport(UDFDevice device, CliOptions opts)
        {
            var sup = device.GetRSWPSupport();
            if (opts.OutputFormat == "json")
            {
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["ddr5"] = sup.DDR5Supported,
                    ["ddr4"] = sup.DDR4Supported,
                    ["ddr3"] = sup.DDR3Supported
                }) + "\n");
            }
            else
            {
                WriteLine(opts, $"RSWP support: {sup}");
            }
            return EXIT_OK;
        }

        private static int CmdRebootDimm(UDFDevice device, CliOptions opts)
        {
            WriteLine(opts, "Rebooting DIMM (toggling VIN_CTRL)...");
            bool ok = device.RebootDIMM();
            WriteLine(opts, ok ? "OK" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdFactoryReset(UDFDevice device, CliOptions opts)
        {
            bool ok = device.FactoryReset();
            WriteLine(opts, ok ? "OK" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        #endregion

        #region SPD command (full coverage)

        private static int CmdSpd(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1) { Console.Error.WriteLine("usage: spd <subcommand> [args]"); return EXIT_USAGE; }

            switch (opts.Positional[0].ToLowerInvariant())
            {
                case "read":
                case "dump":      return CmdSpdRead(device, opts);
                case "read-bytes":return CmdSpdReadBytes(device, opts);
                case "write":     return CmdSpdWrite(device, opts);
                case "write-byte":return CmdSpdWriteByte(device, opts);
                case "verify":    return CmdSpdVerify(device, opts);
                case "crc":       return CmdSpdCrc(device, opts);
                case "fix-crc":   return CmdSpdFixCrc(device, opts);
                case "test-write":return CmdSpdTestWrite(device, opts);
                case "parse":     return CmdSpdParse(device, opts);
                case "parse-file":return CmdSpdParseFile(opts);
                case "hub-reg":   return CmdSpdHubReg(device, opts);
                case "pswp":      return CmdSpdPswp(device, opts);
                default:
                    Console.Error.WriteLine($"Unknown spd subcommand: {opts.Positional[0]}");
                    return EXIT_USAGE;
            }
        }

        // spd read <address> [--out file.bin] [--parsed]
        private static int CmdSpdRead(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: spd read <address> [--out file.bin] [--parsed]"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            var data  = device.ReadEntireSPD(addr);
            if (data == null) { WriteLine(opts, "FAIL: read returned null"); return EXIT_I2C_ERROR; }

            string outPath; opts.Switches.TryGetValue("--out", out outPath);
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllBytes(outPath, data);

            if (opts.OutputFormat == "hex")
            {
                var sb = new StringBuilder(data.Length * 3);
                for (int i = 0; i < data.Length; i++) sb.AppendLine($"{data[i]:X2}");
                WriteRaw(opts, sb.ToString());
            }
            else if (opts.OutputFormat == "json")
            {
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["size"]    = data.Length,
                    ["bytes"]   = BitConverter.ToString(data).Replace("-", "")
                }) + "\n");
            }
            else
            {
                WriteLine(opts, $"[SPD] address=0x{addr:X2} size={data.Length}");
                if (opts.Flags.Contains("--parsed"))
                    EmitFullParsedSummary(opts, data);
            }
            return EXIT_OK;
        }

        // spd read-bytes <address> <offset> <length> [--out file.bin]
        private static int CmdSpdReadBytes(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 4)
            {
                Console.Error.WriteLine("usage: spd read-bytes <address> <offset> <length>");
                return EXIT_USAGE;
            }
            byte   addr   = ParseAddress(opts.Positional[1]);
            ushort offset = ParseRegister(opts.Positional[2]);
            byte   length = byte.Parse(opts.Positional[3]);

            var data = device.ReadSPD(addr, offset, length);
            if (data == null) { WriteLine(opts, "FAIL: read returned null"); return EXIT_I2C_ERROR; }

            string outPath; opts.Switches.TryGetValue("--out", out outPath);
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllBytes(outPath, data);

            if (opts.OutputFormat == "hex")
                WriteRaw(opts, BitConverter.ToString(data).Replace("-", " ") + "\n");
            else if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["offset"]  = offset,
                    ["length"]  = data.Length,
                    ["bytes"]   = BitConverter.ToString(data).Replace("-", "")
                }) + "\n");
            else
                WriteLine(opts, $"[SPD] 0x{addr:X2} offset={offset} len={data.Length}: {BitConverter.ToString(data).Replace("-", " ")}");

            return EXIT_OK;
        }

        // spd write <address> --in file.bin [--no-verify]
        private static int CmdSpdWrite(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: spd write <address> --in file.bin [--no-verify]"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            string inPath; opts.Switches.TryGetValue("--in", out inPath);
            if (string.IsNullOrEmpty(inPath)) { Console.Error.WriteLine("--in required"); return EXIT_USAGE; }

            var data = File.ReadAllBytes(inPath);
            bool ok  = device.WriteEntireSPD(addr, data);
            if (!ok) { WriteLine(opts, "FAIL"); return EXIT_WRITE_ERROR; }

            if (!opts.Flags.Contains("--no-verify"))
            {
                var verify = device.ReadEntireSPD(addr);
                if (verify == null || !verify.SequenceEqual(data))
                { WriteLine(opts, "FAIL: verify"); return EXIT_VERIFY_FAIL; }
            }
            WriteLine(opts, "OK");
            return EXIT_OK;
        }

        // spd write-byte <address> <offset> <value> [--no-verify]
        private static int CmdSpdWriteByte(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 4)
            {
                Console.Error.WriteLine("usage: spd write-byte <address> <offset> <value> [--no-verify]");
                return EXIT_USAGE;
            }
            byte   addr   = ParseAddress(opts.Positional[1]);
            ushort offset = ParseRegister(opts.Positional[2]);
            byte   value  = ParseByte(opts.Positional[3]);

            bool ok = device.WriteSPDByte(addr, offset, value);
            if (!ok) { WriteLine(opts, "FAIL"); return EXIT_WRITE_ERROR; }

            if (!opts.Flags.Contains("--no-verify"))
            {
                var rb = device.ReadSPD(addr, offset, 1);
                if (rb == null || rb.Length == 0 || rb[0] != value)
                { WriteLine(opts, "FAIL: verify"); return EXIT_VERIFY_FAIL; }
            }
            WriteLine(opts, "OK");
            return EXIT_OK;
        }

        // spd verify <address> --in file.bin
        private static int CmdSpdVerify(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: spd verify <address> --in file.bin"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            string inPath; opts.Switches.TryGetValue("--in", out inPath);
            if (string.IsNullOrEmpty(inPath)) { Console.Error.WriteLine("--in required"); return EXIT_USAGE; }

            var data   = File.ReadAllBytes(inPath);
            var actual = device.ReadEntireSPD(addr);
            bool eq    = actual != null && actual.SequenceEqual(data);
            WriteLine(opts, eq ? "OK" : "FAIL: mismatch");
            return eq ? EXIT_OK : EXIT_VERIFY_FAIL;
        }

        // spd crc <address>
        private static int CmdSpdCrc(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: spd crc <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            var  data = device.ReadEntireSPD(addr);
            if (data == null) return EXIT_I2C_ERROR;
            bool isDdr5 = data.Length >= 3 && data[2] == 0x12;
            bool ok     = isDdr5 ? VerifyDdr5Crc(data) : VerifyDdr4Crc(data);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object> { ["crc"] = ok ? "ok" : "fail" }) + "\n");
            else
                WriteLine(opts, ok ? "CRC OK" : "CRC FAIL");
            return ok ? EXIT_OK : EXIT_CRC_ERROR;
        }

        // spd fix-crc <address>  — recalculate and patch CRC bytes on the DIMM
        private static int CmdSpdFixCrc(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: spd fix-crc <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);

            var data = device.ReadEntireSPD(addr);
            if (data == null) { WriteLine(opts, "FAIL: could not read SPD"); return EXIT_I2C_ERROR; }

            byte[] patched = SPDParsedFields.RecalcAndFixCrc(data);
            if (patched == null) { WriteLine(opts, "FAIL: CRC patch returned null"); return EXIT_I2C_ERROR; }

            bool anyFixed = false;
            for (ushort i = 0; i < patched.Length; i++)
            {
                if (patched[i] == data[i]) continue;
                bool ok = device.WriteSPDByte(addr, i, patched[i]);
                if (!ok) { WriteLine(opts, $"FAIL: write at offset {i}"); return EXIT_WRITE_ERROR; }
                anyFixed = true;
            }

            WriteLine(opts, anyFixed ? "OK: CRC patched and written to DIMM" : "CRC was already correct — no bytes written");
            return EXIT_OK;
        }

        // spd test-write <address> <offset>
        private static int CmdSpdTestWrite(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 3)
            {
                Console.Error.WriteLine("usage: spd test-write <address> <offset>");
                return EXIT_USAGE;
            }
            byte   addr   = ParseAddress(opts.Positional[1]);
            ushort offset = ParseRegister(opts.Positional[2]);
            bool ok = device.TestWrite(addr, offset);
            WriteLine(opts, ok ? "Write test: OK (byte is writable)" : "Write test: FAIL (write-protected or error)");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        // spd parse <address>  — read SPD, then emit full JEDEC field dump
        private static int CmdSpdParse(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: spd parse <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            WriteLine(opts, $"Reading SPD at 0x{addr:X2}...");
            var data = device.ReadEntireSPD(addr);
            if (data == null) { WriteLine(opts, "FAIL: could not read SPD"); return EXIT_I2C_ERROR; }
            return EmitFullParsedSummary(opts, data);
        }

        // spd parse-file <path.bin>  — parse a .bin without a device
        private static int CmdSpdParseFile(CliOptions opts)
        {
            // Positional[0]="parse-file", Positional[1]=path  (or --in)
            string path = opts.Positional.Count >= 2 ? opts.Positional[1] : null;
            string s;
            if (string.IsNullOrEmpty(path) && opts.Switches.TryGetValue("--in", out s)) path = s;
            if (string.IsNullOrEmpty(path))
            {
                Console.Error.WriteLine("usage: spd parse-file <path.bin>  (or --in <path.bin>)");
                return EXIT_USAGE;
            }
            if (!File.Exists(path)) { Console.Error.WriteLine($"File not found: {path}"); return EXIT_NOT_FOUND; }
            return EmitFullParsedSummary(opts, File.ReadAllBytes(path));
        }

        // spd hub-reg <get|set> <address> <register> [value]
        private static int CmdSpdHubReg(UDFDevice device, CliOptions opts)
        {
            // Positional: [0]=hub-reg [1]=get|set [2]=address [3]=register [4]=value(set only)
            if (opts.Positional.Count < 4)
            {
                Console.Error.WriteLine("usage: spd hub-reg <get|set> <address> <register> [value]");
                Console.Error.WriteLine("  register: MR0 MR1 MR6 MR11 MR12 MR13 MR14 MR18 MR20 MR48 MR52");
                Console.Error.WriteLine("  writable: MR11 MR12 MR13");
                return EXIT_USAGE;
            }
            string sub  = opts.Positional[1].ToLowerInvariant();
            byte   addr = ParseAddress(opts.Positional[2]);
            byte   reg  = ParseMrName(opts.Positional[3]);

            if (sub == "get")
            {
                byte val = device.ReadSPD5HubRegister(addr, reg);
                if (opts.OutputFormat == "json")
                    WriteRaw(opts, ToJson(new Dictionary<string, object>
                    { ["address"] = $"0x{addr:X2}", ["register"] = $"MR{reg} (0x{reg:X2})", ["value"] = $"0x{val:X2}" }) + "\n");
                else
                    WriteLine(opts, $"hub-reg 0x{addr:X2} MR{reg}=0x{val:X2}");
                return EXIT_OK;
            }
            if (sub == "set")
            {
                if (opts.Positional.Count < 5) { Console.Error.WriteLine("value required"); return EXIT_USAGE; }
                byte value = ParseByte(opts.Positional[4]);
                bool ok = device.WriteSPD5HubRegister(addr, reg, value);
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            Console.Error.WriteLine("hub-reg subcommand must be get or set");
            return EXIT_USAGE;
        }

        // spd pswp <get|set> <address>
        private static int CmdSpdPswp(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 3)
            {
                Console.Error.WriteLine("usage: spd pswp <get|set> <address>");
                return EXIT_USAGE;
            }
            string sub  = opts.Positional[1].ToLowerInvariant();
            byte   addr = ParseAddress(opts.Positional[2]);

            if (sub == "get")
            {
                bool set = device.GetPSWP(addr);
                if (opts.OutputFormat == "json")
                    WriteRaw(opts, ToJson(new Dictionary<string, object>
                    { ["address"] = $"0x{addr:X2}", ["pswp"] = set ? "protected" : "open" }) + "\n");
                else
                    WriteLine(opts, $"PSWP 0x{addr:X2}: {(set ? "PROTECTED" : "OPEN")}");
                return EXIT_OK;
            }
            if (sub == "set")
            {
                bool ok = device.SetPSWP(addr);
                WriteLine(opts, ok ? "OK (PSWP set — this is permanent and cannot be undone!)" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            Console.Error.WriteLine("pswp subcommand must be get or set");
            return EXIT_USAGE;
        }

        #endregion

        #region RSWP command

        private static int CmdRswp(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: rswp <get|set|clear> <address>"); return EXIT_USAGE; }
            string sub  = opts.Positional[0];
            byte   addr = ParseAddress(opts.Positional[1]);

            switch (sub)
            {
                case "get":
                {
                    byte block = 0;
                    string b; if (opts.Switches.TryGetValue("--block", out b)) block = byte.Parse(b);
                    bool set = device.GetRSWP(addr, block);
                    if (opts.OutputFormat == "json")
                        WriteRaw(opts, ToJson(new Dictionary<string, object>
                        { ["address"] = $"0x{addr:X2}", ["block"] = block, ["state"] = set ? "protected" : "open" }) + "\n");
                    else
                        WriteLine(opts, $"rswp address=0x{addr:X2} block={block} state={(set ? "PROTECTED" : "OPEN")}");
                    return EXIT_OK;
                }
                case "set":
                {
                    string b; if (!opts.Switches.TryGetValue("--block", out b)) { Console.Error.WriteLine("--block required"); return EXIT_USAGE; }
                    byte block = byte.Parse(b);
                    bool ok = device.SetRSWP(addr, block);
                    WriteLine(opts, ok ? "OK" : "FAIL");
                    return ok ? EXIT_OK : EXIT_WRITE_ERROR;
                }
                case "clear":
                {
                    bool ok = device.ClearRSWP(addr);
                    WriteLine(opts, ok ? "OK" : "FAIL");
                    return ok ? EXIT_OK : EXIT_WRITE_ERROR;
                }
                default:
                    Console.Error.WriteLine($"Unknown rswp subcommand: {sub}");
                    return EXIT_USAGE;
            }
        }

        #endregion

        #region PMIC command (full coverage)

        private static int CmdPmic(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1) { Console.Error.WriteLine("usage: pmic <subcommand> [args]"); return EXIT_USAGE; }

            switch (opts.Positional[0].ToLowerInvariant())
            {
                case "read":               return CmdPmicRead(device, opts);
                case "write":              return CmdPmicWrite(device, opts);
                case "reg-read":           return CmdPmicRegRead(device, opts);
                case "reg-write":          return CmdPmicRegWrite(device, opts);
                case "unlock":             return CmdPmicUnlock(device, opts);
                case "lock":               return CmdPmicLock(device, opts);
                case "measure":            return CmdPmicMeasure(device, opts);
                case "toggle-vreg":        return CmdPmicToggleVreg(device, opts);
                case "enable-vreg":        return CmdPmicSetVreg(device, opts, true);
                case "disable-vreg":       return CmdPmicSetVreg(device, opts, false);
                case "vreg-state":         return CmdPmicVregState(device, opts);
                case "reboot-dimm":        return CmdRebootDimm(device, opts);
                case "type":               return CmdPmicType(device, opts);
                case "mode":               return CmdPmicMode(device, opts);
                case "enable-prog-mode":   return CmdPmicEnableProgMode(device, opts);
                case "enable-full-access": return CmdPmicEnableFullAccess(device, opts);
                case "is-unlocked":        return CmdPmicIsUnlocked(device, opts);
                case "pgood-status":       return CmdPmicPgoodStatus(device, opts);
                case "pgood-pin":          return CmdPmicPgoodPin(device, opts);
                case "set-pgood-mode":     return CmdPmicSetPgoodMode(device, opts);
                case "change-password":    return CmdPmicChangePassword(device, opts);
                default:
                    Console.Error.WriteLine($"Unknown pmic subcommand: {opts.Positional[0]}");
                    return EXIT_USAGE;
            }
        }

        private static int CmdPmicRead(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic read <address> [--out file.bin]"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            var data  = new byte[256];
            for (int reg = 0; reg < 256; reg++)
            {
                var r = device.ReadPMICDevice(addr, (ushort)reg);
                data[reg] = (r == null || r.Length == 0) ? (byte)0xFF : r[0];
            }
            string outPath; opts.Switches.TryGetValue("--out", out outPath);
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllBytes(outPath, data);

            if (opts.OutputFormat == "hex")
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 256; i++) sb.AppendLine($"{data[i]:X2}");
                WriteRaw(opts, sb.ToString());
            }
            else if (opts.OutputFormat == "json")
            {
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["bytes"] = BitConverter.ToString(data).Replace("-", "") }) + "\n");
            }
            else
            {
                WriteLine(opts, $"PMIC 0x{addr:X2}: 256 bytes read");
            }
            return EXIT_OK;
        }

        private static int CmdPmicWrite(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic write <address> --in file.bin [--full | --block 0-2]"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            string inPath; opts.Switches.TryGetValue("--in", out inPath);
            if (string.IsNullOrEmpty(inPath)) { Console.Error.WriteLine("--in required"); return EXIT_USAGE; }
            var data = File.ReadAllBytes(inPath);

            if (opts.Flags.Contains("--full"))
            {
                if (data.Length != 256) { Console.Error.WriteLine("--full requires 256-byte input"); return EXIT_USAGE; }
                bool ok = device.WritePMICFullDump(addr, data);
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            string blkS;
            if (opts.Switches.TryGetValue("--block", out blkS))
            {
                int blk = int.Parse(blkS);
                bool ok = device.BurnBlock(addr, blk, data);
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            Console.Error.WriteLine("--full or --block required");
            return EXIT_USAGE;
        }

        private static int CmdPmicRegRead(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 3) { Console.Error.WriteLine("usage: pmic reg-read <address> <register>"); return EXIT_USAGE; }
            byte   addr = ParseAddress(opts.Positional[1]);
            ushort reg  = ParseRegister(opts.Positional[2]);
            var r = device.ReadPMICDevice(addr, reg);
            if (r == null || r.Length == 0) return EXIT_I2C_ERROR;
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["register"] = $"0x{reg:X2}", ["value"] = $"0x{r[0]:X2}" }) + "\n");
            else
                WriteLine(opts, $"reg=0x{reg:X2} value=0x{r[0]:X2}");
            return EXIT_OK;
        }

        private static int CmdPmicRegWrite(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 4) { Console.Error.WriteLine("usage: pmic reg-write <address> <register> <value>"); return EXIT_USAGE; }
            byte   addr  = ParseAddress(opts.Positional[1]);
            ushort reg   = ParseRegister(opts.Positional[2]);
            byte   value = ParseByte(opts.Positional[3]);
            bool ok = device.WriteI2CDevice(addr, reg, value);
            WriteLine(opts, ok ? "OK" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdPmicUnlock(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic unlock <address> [--lsb 73 --msb 94]"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            byte lsb = 0x73, msb = 0x94;
            string s;
            if (opts.Switches.TryGetValue("--lsb", out s)) lsb = ParseByte(s);
            if (opts.Switches.TryGetValue("--msb", out s)) msb = ParseByte(s);
            bool ok = device.UnlockVendorRegion(addr, lsb, msb);
            WriteLine(opts, ok ? "OK" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdPmicLock(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic lock <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            bool ok = device.LockVendorRegion(addr);
            WriteLine(opts, ok ? "OK" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdPmicMeasure(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic measure <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            var m = device.ReadAllMeasurements(addr);
            if (m == null) return EXIT_I2C_ERROR;

            if (opts.OutputFormat == "json")
            {
                var obj = new Dictionary<string, object>
                {
                    ["device"]      = m.DeviceType,
                    ["voltages_mV"] = m.Voltages_mV.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                    ["currents_mA"] = m.Currents_mA.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                    ["powers_mW"]   = m.Powers_mW  .ToDictionary(kv => kv.Key, kv => (object)kv.Value)
                };
                if (!double.IsNaN(m.TotalPower_mW)) obj["total_mW"] = m.TotalPower_mW;
                WriteRaw(opts, ToJson(obj) + "\n");
            }
            else
            {
                WriteLine(opts, $"Device: {m.DeviceType}");
                foreach (var kv in m.Voltages_mV) WriteLine(opts, $"  {kv.Key}: {kv.Value:F0} mV");
                foreach (var kv in m.Currents_mA) WriteLine(opts, $"  {kv.Key}: {kv.Value:F1} mA");
                foreach (var kv in m.Powers_mW)   WriteLine(opts, $"  {kv.Key}: {kv.Value:F1} mW");
                if (!double.IsNaN(m.TotalPower_mW)) WriteLine(opts, $"  Total: {m.TotalPower_mW:F1} mW");
            }
            return EXIT_OK;
        }

        // pmic toggle-vreg <address>
        private static int CmdPmicToggleVreg(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic toggle-vreg <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            bool newState;
            bool ok = device.ToggleVReg(addr, out newState);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["result"] = ok ? "ok" : "fail", ["vreg"] = newState ? "enabled" : "disabled" }) + "\n");
            else
                WriteLine(opts, ok ? $"OK — VReg is now {(newState ? "ENABLED" : "DISABLED")}" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        // pmic enable-vreg <address>  /  pmic disable-vreg <address>
        private static int CmdPmicSetVreg(UDFDevice device, CliOptions opts, bool enable)
        {
            string cmd = enable ? "enable-vreg" : "disable-vreg";
            if (opts.Positional.Count < 2) { Console.Error.WriteLine($"usage: pmic {cmd} <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            bool ok   = device.SetVRegEnable(addr, enable);
            WriteLine(opts, ok ? $"OK — VReg {(enable ? "enabled" : "disabled")}" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        // pmic vreg-state <address>
        private static int CmdPmicVregState(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic vreg-state <address>"); return EXIT_USAGE; }
            byte addr    = ParseAddress(opts.Positional[1]);
            bool enabled = device.GetVRegEnabled(addr);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["vreg"] = enabled ? "enabled" : "disabled" }) + "\n");
            else
                WriteLine(opts, $"VReg 0x{addr:X2}: {(enabled ? "ENABLED" : "DISABLED")}");
            return EXIT_OK;
        }

        // pmic type <address>
        private static int CmdPmicType(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic type <address>"); return EXIT_USAGE; }
            byte   addr = ParseAddress(opts.Positional[1]);
            string type = device.GetPMICType(addr);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["type"] = type }) + "\n");
            else
                WriteLine(opts, $"PMIC 0x{addr:X2}: {type}");
            return EXIT_OK;
        }

        // pmic mode <address>
        private static int CmdPmicMode(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic mode <address>"); return EXIT_USAGE; }
            byte   addr = ParseAddress(opts.Positional[1]);
            string mode = device.GetPMICMode(addr);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["mode"] = mode }) + "\n");
            else
                WriteLine(opts, $"PMIC 0x{addr:X2} mode: {mode}");
            return EXIT_OK;
        }

        // pmic enable-prog-mode <address>
        private static int CmdPmicEnableProgMode(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic enable-prog-mode <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            bool ok   = device.EnableProgMode(addr);
            WriteLine(opts, ok ? "OK — Programmable mode enabled" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        // pmic enable-full-access <address>
        private static int CmdPmicEnableFullAccess(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic enable-full-access <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            bool ok   = device.EnableFullAccess(addr);
            WriteLine(opts, ok ? "OK — Full (manufacturer) access enabled" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        // pmic is-unlocked <address>
        private static int CmdPmicIsUnlocked(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic is-unlocked <address>"); return EXIT_USAGE; }
            byte addr     = ParseAddress(opts.Positional[1]);
            bool unlocked = device.IsVendorRegionUnlocked(addr);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["unlocked"] = unlocked }) + "\n");
            else
                WriteLine(opts, $"Vendor region 0x{addr:X2}: {(unlocked ? "UNLOCKED" : "LOCKED")}");
            return EXIT_OK;
        }

        // pmic pgood-status <address>
        private static int CmdPmicPgoodStatus(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic pgood-status <address>"); return EXIT_USAGE; }
            byte   addr = ParseAddress(opts.Positional[1]);
            string stat = device.GetPMICPGoodStatus(addr);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["pgood_status"] = stat }) + "\n");
            else
                WriteLine(opts, $"PGOOD 0x{addr:X2}: {stat}");
            return EXIT_OK;
        }

        // pmic pgood-pin  — reads the hardware PMIC_FLAG pin; no address needed
        private static int CmdPmicPgoodPin(UDFDevice device, CliOptions opts)
        {
            bool high = device.GetPGoodPinState();
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["pgood_pin"] = high ? "high" : "low" }) + "\n");
            else
                WriteLine(opts, $"PGOOD pin: {(high ? "HIGH (power good)" : "LOW (fault / disabled)")}");
            return EXIT_OK;
        }

        // pmic set-pgood-mode <address> <0|1|2>
        private static int CmdPmicSetPgoodMode(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 3)
            {
                Console.Error.WriteLine("usage: pmic set-pgood-mode <address> <0|1|2>");
                Console.Error.WriteLine("  0 = hardware / JEDEC control");
                Console.Error.WriteLine("  1 = force LOW (PWR_GOOD deasserted)");
                Console.Error.WriteLine("  2 = force HIGH or open-drain");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            int  mode = int.Parse(opts.Positional[2]);
            if (mode < 0 || mode > 2) { Console.Error.WriteLine("mode must be 0, 1, or 2"); return EXIT_USAGE; }
            bool ok = device.SetPGoodOutputMode(addr, mode);
            WriteLine(opts, ok ? $"OK — PGOOD mode set to {mode}" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        // pmic change-password <address> <cur_lsb> <cur_msb> <new_lsb> <new_msb>
        // Also accepts: pmic change-password <address> --cur-lsb x --cur-msb x --new-lsb x --new-msb x
        private static int CmdPmicChangePassword(UDFDevice device, CliOptions opts)
        {
            byte addr, curLsb, curMsb, newLsb, newMsb;
            string s;

            if (opts.Positional.Count >= 6)
            {
                addr   = ParseAddress(opts.Positional[1]);
                curLsb = ParseByte(opts.Positional[2]);
                curMsb = ParseByte(opts.Positional[3]);
                newLsb = ParseByte(opts.Positional[4]);
                newMsb = ParseByte(opts.Positional[5]);
            }
            else if (opts.Positional.Count >= 2 && opts.Switches.TryGetValue("--cur-lsb", out s))
            {
                addr   = ParseAddress(opts.Positional[1]);
                curLsb = ParseByte(s);
                opts.Switches.TryGetValue("--cur-msb", out s); curMsb = ParseByte(s ?? "94");
                opts.Switches.TryGetValue("--new-lsb", out s); newLsb = ParseByte(s ?? "73");
                opts.Switches.TryGetValue("--new-msb", out s); newMsb = ParseByte(s ?? "94");
            }
            else
            {
                Console.Error.WriteLine("usage: pmic change-password <address> <cur_lsb> <cur_msb> <new_lsb> <new_msb>");
                Console.Error.WriteLine("  or:  pmic change-password <address> --cur-lsb <x> --cur-msb <x> --new-lsb <x> --new-msb <x>");
                return EXIT_USAGE;
            }

            bool ok = device.ChangePMICPassword(addr, curLsb, curMsb, newLsb, newMsb);
            WriteLine(opts, ok ? "OK — password changed and verified" : "FAIL (wrong current password or burn error)");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        #endregion

        #region Pin command

        private static int CmdPin(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1) { Console.Error.WriteLine("usage: pin <get|set|reset> [args]"); return EXIT_USAGE; }
            string sub = opts.Positional[0];

            if (sub == "reset")
            {
                bool ok = device.ResetPins();
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("pin name required"); return EXIT_USAGE; }
            byte pin = MapPinName(opts.Positional[1]);

            if (sub == "get")
            {
                byte v = device.GetPin(pin);
                if (opts.OutputFormat == "json")
                    WriteRaw(opts, ToJson(new Dictionary<string, object>
                    { ["pin"] = opts.Positional[1], ["state"] = v }) + "\n");
                else
                    WriteLine(opts, $"pin={opts.Positional[1]} state={v}");
                return EXIT_OK;
            }
            if (sub == "set")
            {
                if (opts.Positional.Count < 3) { Console.Error.WriteLine("set requires 0|1"); return EXIT_USAGE; }
                byte state = byte.Parse(opts.Positional[2]);
                bool ok    = device.SetPin(pin, state);
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            return EXIT_USAGE;
        }

        private static byte MapPinName(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "hv-switch":  return UDFDevice.PIN_HV_SWITCH;
                case "sa1":        return UDFDevice.PIN_SA1_SWITCH;
                case "status":     return UDFDevice.PIN_DEV_STATUS;
                case "hv-conv":    return UDFDevice.PIN_HV_CONVERTER;
                case "vin-ctrl":   return UDFDevice.PIN_DDR5_VIN_CTRL;
                case "pmic-ctrl":  return UDFDevice.PIN_PMIC_CTRL;
                case "pmic-flag":  return UDFDevice.PIN_PMIC_FLAG;
                default: throw new ArgumentException($"Unknown pin name: {name}");
            }
        }

        #endregion

        #region EEPROM command

        // eeprom read <offset> <length> [--out file.bin]
        private static int CmdEeprom(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1) { Console.Error.WriteLine("usage: eeprom read <offset> <length>"); return EXIT_USAGE; }
            if (!string.Equals(opts.Positional[0], "read", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Unknown eeprom subcommand: {opts.Positional[0]}");
                return EXIT_USAGE;
            }
            if (opts.Positional.Count < 3) { Console.Error.WriteLine("usage: eeprom read <offset> <length>"); return EXIT_USAGE; }

            ushort offset = ParseRegister(opts.Positional[1]);
            ushort length = ushort.Parse(opts.Positional[2]);

            var data = device.ReadInternalEEPROM(offset, length);
            if (data == null) { WriteLine(opts, "FAIL: internal EEPROM read returned null"); return EXIT_I2C_ERROR; }

            string outPath; opts.Switches.TryGetValue("--out", out outPath);
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllBytes(outPath, data);

            if (opts.OutputFormat == "hex")
                WriteRaw(opts, BitConverter.ToString(data).Replace("-", " ") + "\n");
            else if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["offset"] = offset, ["length"] = data.Length, ["bytes"] = BitConverter.ToString(data).Replace("-", "") }) + "\n");
            else
                WriteLine(opts, $"[EEPROM] offset={offset} length={data.Length}: {BitConverter.ToString(data).Replace("-", " ")}");

            return EXIT_OK;
        }

        #endregion

        #region SPD parsed output

        private static int EmitFullParsedSummary(CliOptions opts, byte[] spd)
        {
            var result = SPDParsedFields.Parse(spd);

            if (opts.OutputFormat == "json")
            {
                // Emit flat key→value; section headers (empty Value) are skipped.
                var fields = new Dictionary<string, object>();
                foreach (var f in result.Fields)
                    if (!string.IsNullOrEmpty(f.Value))
                        fields[f.Label] = f.Value;

                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["dram_type"] = result.DramType,
                    ["fields"]    = fields
                }) + "\n");
            }
            else
            {
                WriteLine(opts, $"DRAM Type: {result.DramType}");
                foreach (var f in result.Fields)
                {
                    if (string.IsNullOrEmpty(f.Value))
                    {
                        // Section header line
                        WriteLine(opts, "");
                        WriteLine(opts, f.Label);
                    }
                    else
                    {
                        WriteLine(opts, $"  {f.Label,-36} {f.Value}");
                    }
                }
            }
            return EXIT_OK;
        }

        #endregion

        #region Misc helpers

        private static string AutoDetectPort()
        {
            foreach (var port in SerialPort.GetPortNames())
            {
                try
                {
                    using (var dev = new UDFDevice(port, 115200))
                    {
                        if (dev.Ping()) return port;
                    }
                }
                catch { /* not our device */ }
            }
            return null;
        }

        private static byte ParseMrName(string s)
        {
            // Accept "MR11", "mr11", "0x0B", or plain decimal "11"
            string u = s.ToUpperInvariant();
            if (u.StartsWith("MR"))
                return (byte)int.Parse(u.Substring(2));
            return ParseByte(s);
        }

        private static bool VerifyDdr4Crc(byte[] spd)
        {
            if (spd.Length < 128) return false;
            ushort crc    = Crc16(spd, 0, 126);
            ushort stored = (ushort)(spd[126] | (spd[127] << 8));
            return crc == stored;
        }

        private static bool VerifyDdr5Crc(byte[] spd)
        {
            if (spd.Length < 512) return false;
            ushort crc    = Crc16(spd, 0, 510);
            ushort stored = (ushort)(spd[510] | (spd[511] << 8));
            return crc == stored;
        }

        private static ushort Crc16(byte[] data, int offset, int length)
        {
            ushort crc = 0;
            for (int i = 0; i < length; i++)
            {
                crc = (ushort)(crc ^ (data[offset + i] << 8));
                for (int b = 0; b < 8; b++)
                {
                    if ((crc & 0x8000) != 0) crc = (ushort)((crc << 1) ^ 0x1021);
                    else crc = (ushort)(crc << 1);
                }
            }
            return crc;
        }

        #endregion

        #region Usage

        private static void PrintUsage()
        {
            Console.WriteLine(
@"Unified DDR Flasher  (v3.9.0)  — CLI reference

Usage: Unified-DDR-Flasher.exe <command> [options]

GLOBAL OPTIONS
  --port COM3                COM port  (required unless --auto-detect)
  --auto-detect              Use the first detected UDF device
  --baud 115200              Baud rate  (default 115200)
  --timeout 5000             Per-command timeout ms  (default 5000)
  --output-format text|json|hex
  --quiet                    Suppress informational output
  --log-file <path>          Append all output to a file
  --reconnect-timeout <sec>  On USB disconnect, wait up to N seconds for the
                             port to reappear before failing  (default: 0 = off)

──────────────────────────────────────────────────────────────────────
DEVICE / DIAGNOSTICS
  ping                         Connectivity check + firmware version
  version                      Print firmware version
  test                         Device self-test (CMD_TEST)
  name [get | set <name>]      Read or write device label
  i2c-speed [0|1|2]            Read/set I2C clock  0=100k 1=400k 2=1M
  scan                         List all I2C devices on the bus
  detect <address>             Identify module type at hex I2C address
  rswp-support                 Report firmware RSWP capabilities
  reboot-dimm                  Power-cycle DIMM via VIN_CTRL pin
  factory-reset                Erase internal device EEPROM to defaults

──────────────────────────────────────────────────────────────────────
SPD OPERATIONS   (address is a hex I2C address, e.g. 50 or 0x50)

  spd read   <addr> [--out file.bin] [--parsed]
             Read whole SPD; --parsed prints decoded JEDEC fields.

  spd read-bytes <addr> <offset> <length> [--out file.bin]
             Read a byte range (up to 64 bytes).

  spd write  <addr> --in file.bin [--no-verify]
             Write entire SPD from binary file.

  spd write-byte <addr> <offset> <value> [--no-verify]
             Write a single byte at offset.

  spd verify <addr> --in file.bin
             Compare live SPD against a reference file.

  spd crc    <addr>
             Verify stored CRC against recalculated value.

  spd fix-crc <addr>
             Recalculate CRC and patch the corrected bytes back to DIMM.

  spd test-write <addr> <offset>
             Non-destructive write-capability probe at offset.

  spd parse  <addr>
             Full JEDEC field decode — DDR4 (JEDEC 21-C Annex L)
             or DDR5 (JESD400-5D).  Includes timing, capacity,
             manufacturer IDs (JEP-106), CRC verification.

  spd parse-file <path.bin>
             Decode a binary dump without a connected device.
             Also accepts:  spd parse-file --in <path.bin>

  spd hub-reg get <addr> <register>
  spd hub-reg set <addr> <register> <value>
             DDR5 SPD5-hub mode registers.
             Register names: MR0 MR1 MR6 MR11 MR12 MR13 MR14 MR18
                             MR20 MR48 MR52  (or hex: 0x0B / decimal: 11)
             Only MR11, MR12, MR13 are writable per JEDEC.

  spd pswp get <addr>
  spd pswp set <addr>
             Permanent Software Write Protect — set is IRREVERSIBLE.

──────────────────────────────────────────────────────────────────────
RSWP  (Reversible Software Write Protection)
  rswp get   <addr> [--block 0-15]
  rswp set   <addr> --block 0-15
  rswp clear <addr>

──────────────────────────────────────────────────────────────────────
PMIC OPERATIONS

  --- Register access ---
  pmic read        <addr> [--out file.bin]   Dump all 256 registers
  pmic write       <addr> --in file.bin [--full | --block 0-2]
  pmic reg-read    <addr> <register>
  pmic reg-write   <addr> <register> <value>

  --- Identification ---
  pmic type        <addr>        Chip model  (PMIC5000/5100/5200 …)
  pmic mode        <addr>        Locked / Programmable / Manufacturer Access

  --- Access control ---
  pmic enable-prog-mode   <addr>   Enable programmable mode (reg 0x2F[2])
  pmic enable-full-access <addr>   Unlock vendor region (default password)
  pmic is-unlocked        <addr>   Check vendor-region lock state
  pmic unlock  <addr> [--lsb 73 --msb 94]
  pmic lock    <addr>
  pmic change-password <addr> <cur_lsb> <cur_msb> <new_lsb> <new_msb>
               Burns a new MTP password.  Named-switch form also accepted:
               --cur-lsb x --cur-msb x --new-lsb x --new-msb x

  --- Voltage regulator ---
  pmic vreg-state  <addr>        Current VReg enabled/disabled state
  pmic enable-vreg <addr>        Enable VReg
  pmic disable-vreg <addr>       Disable VReg
  pmic toggle-vreg  <addr>       Toggle VReg and report new state

  --- Power ---
  pmic reboot-dimm               Power-cycle DIMM (same as top-level)
  pmic pgood-pin                 Read PGOOD hardware pin (no addr needed)
  pmic pgood-status  <addr>      Detailed power-good rail status
  pmic set-pgood-mode <addr> <0|1|2>
               0 = HW/JEDEC   1 = force LOW   2 = force HIGH/OD

  --- ADC measurements ---
  pmic measure  <addr>           Voltages (mV), currents (mA), powers (mW)

──────────────────────────────────────────────────────────────────────
PIN CONTROL
  pin get   <pin-name>
  pin set   <pin-name> <0|1>
  pin reset
  Pin names: hv-switch | sa1 | status | hv-conv | vin-ctrl | pmic-ctrl | pmic-flag

──────────────────────────────────────────────────────────────────────
INTERNAL DEVICE EEPROM
  eeprom read <offset> <length> [--out file.bin]
             Read from programmer's own storage (max 32 bytes per call).

──────────────────────────────────────────────────────────────────────
EXIT CODES
  0  Success
  1  Device not found / ping failed
  2  I2C / communication error
  3  Verification mismatch
  4  CRC error
  5  Write error
  6  Usage / argument error
");
        }

        #endregion
    }
}
