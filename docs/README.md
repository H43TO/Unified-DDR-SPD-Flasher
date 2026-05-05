# Unified DDR Flasher

A GUI and CLI tool for reading, writing, and programming SPD EEPROMs
(DDR4/DDR5) and PMIC registers on memory modules, using the
RP2040-based UDF programmer hardware.

---

## Features

- **Read & write SPD EEPROMs** — full 256/512/1024-byte dumps, with
  automatic write-protect handling for DDR4 (RSWP) and DDR5
  (block-level RSWP via SPD5 hub register MR12).
- **Decode SPD per JEDEC 21-C / JESD400-5D** — module type, capacity,
  CAS latencies, all primary timing parameters in nCK + ps, CRC
  verification with a **Recalc & Fix CRC** button, and support for
  all module form factors (UDIMM, RDIMM, LRDIMM, SO-DIMM, CAMM2 …).
- **Program PMICs** — read/write any register, burn vendor MTP blocks
  with proper unlock/lock sequencing, monitor live voltages, currents,
  and per-rail power.
- **Full CLI mode** — every operation is scriptable; output as text,
  JSON, or raw hex; well-defined exit codes; optional
  `--reconnect-timeout` for robust scripting over flaky cables.
- **Automatic connection recovery** — transparent single-retry on USB
  transient faults; I2C clock fallback from 400 kHz → 100 kHz on
  marginal socket contacts; host-level reconnect loop on USB
  re-enumeration.

---

## Supported Hardware

| Memory Type | Read | Write | RSWP | PMIC |
|---|---|---|---|---|
| DDR4 UDIMM / RDIMM / SO-DIMM | ✅ | ✅ | ✅ | ❌ |
| DDR5 UDIMM / RDIMM / SO-DIMM | ✅ | ✅ | ✅ | ✅ |
| DDR5 CUDIMM / CSODIMM / MRDIMM | ✅ | ✅ | ✅ | ✅ |

PMIC support covers PMIC5000 / 5010 / 5020 / 5030 / 5100 / 5120 / 5200.

---

## Requirements

- **Windows 10 (1903+) or Windows 11**
- **.NET Framework 4.8.1** runtime (pre-installed on Windows 11; available
  free from Microsoft for Windows 10)
- UDF programmer hardware connected via USB

---

## Installation

1. Download the latest release ZIP from the Releases page.
2. Extract and run `Unified-DDR-Flasher.exe` — no installer required.
3. Connect the UDF programmer via USB. Windows 10/11 installs the CDC
   serial driver automatically; the device enumerates as a `COMx` port.
4. If this is a fresh device, flash the firmware first (see
   **Firmware Update** below).

---

## Firmware Update

1. Hold the **BOOTSEL** button on the RP2040 board while plugging it in.
2. The board enumerates as a mass-storage drive named **RPI-RP2**.
3. Drag `firmware/UDF_fw.uf2` onto that drive. It reboots automatically.
4. The status LED goes steady-on once the firmware is ready.

If the application reports `"Firmware returned UNKNOWN (0x3F)"`, the firmware
predates this host build. Reflash with the matching `UDF_fw.uf2`.

---

## GUI Quick Start

1. Open the **Flasher Configuration** tab (default on first launch).
2. Pick the COM port and click **Connect**. The status bar turns green.
3. Switch to **SPD Operations**. Detected modules appear in the address
   dropdown. Pick one and click **Read SPD**.
4. The hex viewer shows the raw bytes. Click **Parsed Fields ▶** to expand
   the JEDEC-decoded panel (timings in nCK + ps, capacity, CAS latencies,
   CRC status).
5. To write, click **Open Dump…** to load a `.bin` file, then **Write All**.

The **Recalc & Fix CRC** button patches the in-memory dump so the next
**Write All** produces a valid CRC. It does **not** push to the DIMM by
itself.

---

## PMIC Operations

The PMIC tab supports DDR5 PMICs.

- **Read All Registers** dumps the full 256-byte register space.
- **Live Measurements** shows real-time voltages (mV), currents (mA), and
  per-rail powers (mW).
- **Burn Vendor Block** writes one of the three 16-byte MTP blocks. This is
  **irreversible** — confirmation is required.
- **Change Password** reprograms the vendor unlock password (MTP-burned).

---

## CLI Mode

All GUI features are available headless. Run with no arguments to open the
GUI; pass any argument to enter CLI mode.

```
Unified-DDR-Flasher.exe --help
Unified-DDR-Flasher.exe ping --port COM4
Unified-DDR-Flasher.exe scan --port COM4 --output-format json
Unified-DDR-Flasher.exe spd read 50 --port COM4 --out my_module.bin
Unified-DDR-Flasher.exe spd parse 50 --port COM4
Unified-DDR-Flasher.exe spd parse-file my_module.bin
Unified-DDR-Flasher.exe spd write 50 --port COM4 --in my_module.bin
Unified-DDR-Flasher.exe spd verify 50 --port COM4 --in my_module.bin
Unified-DDR-Flasher.exe spd fix-crc 50 --port COM4
Unified-DDR-Flasher.exe spd hub-reg get 50 MR11 --port COM4
Unified-DDR-Flasher.exe spd pswp get 50 --port COM4
Unified-DDR-Flasher.exe pmic measure 48 --port COM4 --output-format json
Unified-DDR-Flasher.exe pmic toggle-vreg 48 --port COM4
Unified-DDR-Flasher.exe pmic unlock 48 --port COM4
Unified-DDR-Flasher.exe pmic change-password 48 73 94 AA BB --port COM4
Unified-DDR-Flasher.exe pmic pgood-pin --port COM4
Unified-DDR-Flasher.exe eeprom read 0 16 --port COM4 --output-format hex
```

### Auto-detect

Use `--auto-detect` to skip `--port`; the tool walks COM ports and uses the
first one that responds to a ping.

```
Unified-DDR-Flasher.exe ping --auto-detect
```

### Reconnect on USB fault

For scripted long operations over marginal cables:

```
Unified-DDR-Flasher.exe spd write 50 --port COM4 --in dump.bin --reconnect-timeout 30
```

If the USB connection drops mid-command, the tool waits up to 30 s for the
port to reappear before failing.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Device not found / ping failed |
| 2 | I2C / SPD error |
| 3 | Verification failed |
| 4 | CRC error |
| 5 | Write error |
| 6 | Usage / argument error |

```cmd
Unified-DDR-Flasher.exe spd verify 50 --port COM4 --in golden.bin --quiet
if errorlevel 3 echo Module differs from golden image
```

---

## Settings

Settings are stored in
`%LOCALAPPDATA%\UnifiedDDRFlasher\settings.json`:

- `AutoConnect` — if `true`, the GUI tries to reconnect to the last used
  port on startup.
- `LastPort` — last successfully connected COM port.
- `PmicPasswords` — per-PMIC-type vendor unlock passwords (LSB + MSB
  bytes). Falls back to the JEDEC default `(0x73, 0x94)` when no
  type-specific password is stored.

---

## Building from Source

Open `UDF.sln` in **Visual Studio 2019+** with the .NET Framework 4.8.1
SDK installed. The solution contains two projects:

- **UDF-Core** — the protocol-only DLL (no UI dependencies). Source is
  private; the compiled `UDF-Core.dll` is committed to `lib/prebuilt/`.
- **Unified-DDR-Flasher** — the WinForms application, references the
  library through a configurable path.

### One-time setup

1. **Build (or obtain) `UDF-Core.dll`** — e.g. `C:\dev\udf-private\UDF-Core.dll`.

2. **Tell the build where it is.** Pick one of:

   - **Per-repo file (recommended):** copy
     `src\UDFCore.local.props.example` to `src\UDFCore.local.props` and
     edit `<UDFCoreLibraryPath>`. The file is gitignored.
   - **Environment variable:**
     ```
     setx UDFCoreLibraryPath "C:\dev\udf-private\UDF-Core.dll"
     ```
   - **Command-line override:**
     ```
     msbuild src\Unified-DDR-Flasher.csproj ^
             /p:Configuration=Release ^
             /p:UDFCoreLibraryPath=C:\dev\udf-private\UDF-Core.dll
     ```

3. **Restore NuGet packages:**
   ```
   nuget restore UDF.sln
   ```

### Build

```
msbuild src\Unified-DDR-Flasher.csproj /p:Configuration=Release
```

### Single-file output

The release build produces a **self-contained executable**. All referenced
libraries — `UDF-Core.dll`, `Newtonsoft.Json.dll`, `System.Text.Json.dll`,
and every transitive dependency — are embedded as compressed resources inside
`Unified-DDR-Flasher.exe` by
[Costura.Fody](https://github.com/Fody/Costura). Ship just two files:

```
Unified-DDR-Flasher.exe
Unified-DDR-Flasher.exe.config
```

---

## Troubleshooting

**"Device on COMx did not respond to CMD_PING after 5 attempts"**

1. Firmware is flashed (device should enumerate as a CDC serial port —
   if it shows as `RPI-RP2`, drag `UDF_fw.uf2` onto it).
2. No other software has the port open (PuTTY, another instance).
3. The cable supports data, not just power.

**"Firmware returned UNKNOWN (0x3F)"**

The firmware predates this host build. Reflash with the matching
`UDF_fw.uf2`.

**I2C / SPD errors on marginal socket contact**

The firmware and host library both implement automatic I2C clock fallback
(400 kHz → 100 kHz) and a single-retry after any transport fault. If errors
persist, try cleaning the DIMM gold contacts and reseating.

**"CRC FAIL" on a freshly-read module**

Either the module has been XMP/EXPO-modified (vendor CRC rules differ from
JEDEC's) or the SPD is genuinely corrupt. Use **Recalc & Fix CRC** if the
timings themselves are valid.

---

## License

Application source (`src/`): MIT License.  
Firmware (`fw/`) and core library (`lib/`): Proprietary — pre-built binaries
only; source not published.
