# Unified DDR Flasher — AI Agent Development Guideline

**Version:** 4.0
**Target AI model:** Claude Sonnet 4.6+
**Scope:** Ongoing maintenance, feature extension, and debugging of a
complete, production-quality codebase. All major features from v3.0 have
been implemented. This guideline describes the *current* state of the
project and the rules for extending it safely.

> ### Changelog v3.0 → v4.0
> - **Project renamed** throughout: `Unified DDR Flasher` (was `Unified DDR SPD Flasher`).
>   Executable: `Unified-DDR-Flasher.exe`. Library DLL: `UDF-Core.dll`.
>   C# namespace: `UnifiedDDRFlasher` (app) / `UDFCore` (library).
>   Device class: `UDFDevice`. Build props key: `UDFCoreLibraryPath`.
> - **All §1–§12 tasks from v3.0 are complete.** Guideline restructured
>   from a "fix list" to a "maintenance and extension" reference.
> - **§R (Reliability)** added: firmware watchdog, I2C bus recovery,
>   DTR-edge soft reset, progress heartbeat. Host retry layer, I2C clock
>   fallback, CLI reconnect loop.
> - **CLI fully expanded**: all library operations are now scriptable.
>   34 commands across 7 top-level groups. `--output-format json|hex|text`.
>   `--reconnect-timeout`. Exit codes 0–6.
> - **Target framework updated** to .NET Framework 4.8.1 (not .NET 8).
>   This is intentional — WinForms on .NET 8 requires Windows SDK
>   components that are not always present on end-user machines.
>   4.8.1 ships in-box with Windows 11.
> - **§10 constraints updated** to reflect completed implementation.

---

## 0. Project Overview

The Unified DDR Flasher is a commercial DDR SPD/PMIC programmer based on
an RP2040 microcontroller. The host software (`Unified-DDR-Flasher`, C#
WinForms .NET Framework 4.8.1) communicates with the firmware
(`UDF_fw.c/.h`) over a USB CDC serial port at 115 200 baud.

### Repository layout (current)

```
UDF/
├── src/                          ← C# WinForms project (PUBLIC)
│   ├── Program.cs                ← GUI entry + full CLI (34 commands)
│   ├── MainForm.cs               ← 3-tab shell, background timers
│   ├── AppSettings.cs            ← JSON settings, PMIC password store
│   ├── FodyWeavers.xml           ← Costura embed config
│   ├── App.config
│   ├── packages.config
│   ├── UDFCore.local.props       ← per-developer, gitignored
│   ├── UDFCore.local.props.example
│   ├── Unified-DDR-Flasher.csproj
│   ├── Properties/
│   │   └── AssemblyInfo.cs
│   └── Tabs/
│       ├── SPDOperationsTab.cs   ← SPD read/write/parse/RSWP/PSWP
│       ├── PMICOperationsTab.cs  ← PMIC read/write/burn/measure
│       ├── FlasherConfigTab.cs   ← Port select, i2c speed, pins
│       └── SPDParsedFields.cs    ← JEDEC DDR4+DDR5 parser + CRC
├── lib/                          ← PRIVATE (gitignored source)
│   ├── SPDToolLibrary.cs         ← Protocol + SPD parser source
│   ├── UDF-Core.csproj           ← Library project (private)
│   └── prebuilt/
│       ├── UDF-Core.dll          ← Compiled output (committed)
│       └── UDF-Core.xml          ← XML doc (committed)
├── fw/                           ← PRIVATE (gitignored source)
│   ├── UDF_fw.c
│   ├── UDF_fw.h
│   ├── CMakeLists.txt
│   └── pico_sdk_import.cmake
├── firmware/                     ← Pre-built binaries (committed)
│   ├── UDF_fw.uf2
│   └── UDF_fw.bin
├── docs/
│   ├── README.md                 ← Public GitHub README
│   └── INTERNAL_DEV.md          ← Private developer reference
├── UDF.sln
└── .gitignore
```

### Strategy for keeping library source private

`UDF-Core.dll` lives in `lib/prebuilt/` (tracked by git). The WinForms
`.csproj` references it via `$(UDFCoreLibraryPath)`. Costura.Fody embeds
it inside the `.exe` at build time. End users only need:

```
Unified-DDR-Flasher.exe
Unified-DDR-Flasher.exe.config
```

`.gitignore` excludes `lib/SPDToolLibrary.cs`, `lib/UDF-Core.csproj`,
and `fw/`. The prebuilt DLL and firmware binaries are the only artefacts
committed to the public branch.

---

## 1. Completed Feature Inventory

Every item below is **implemented and working**. Do not re-implement.
Use this as a reference for where things live when extending.

### 1.1 Serial protocol (lib/SPDToolLibrary.cs + fw/UDF_fw.c)

- `SemaphoreSlim` `_txLock` / `_rxLock` — serial I/O never deadlocks.
- Fully async API (`SendAndReceiveAsync`); sync wrappers via `RunSync`.
- `_drainOnNextSend` flag — `DiscardInBuffer` only after a timeout, not
  unconditionally.
- Dispose: `_disposed` → `_cts.Cancel()` → `_serialPort.Close()` +
  `Dispose()`. No fire-and-forget.
- `SendAndReceiveWithRetryAsync` — one automatic retry on
  `TimeoutException`, `EndOfStreamException`, or checksum mismatch.
  Zero overhead on the happy path.
- `FlushHandshakeAsync` — re-syncs both framing state machines before
  a retry (drain + 300 ms ping).
- I2C clock fallback in `ReadEntireSPDAsync` / `WriteEntireSPDAsync`:
  drops to 100 kHz on first null chunk, stays there for the rest of the
  operation.
- 5× `CMD_PING` handshake on connect; 500 ms each, 200 ms gaps.
- Module-type cache (`ConcurrentDictionary<byte, ModuleInfo>`).

### 1.2 Firmware reliability (fw/UDF_fw.c)

- **I2C bus recovery** (`recover_i2c_bus`): 9-pulse SCL bit-bang on
  `PICO_ERROR_TIMEOUT`, followed by peripheral re-init. JEDEC §3.1.16.
- **Hardware watchdog** (2 s): armed in `main()`, kicked in the main
  loop and inside `heartbeat_tick()`.
- **DTR-edge soft reset**: `stdio_usb_connected()` false→true triggers
  `recover_i2c_bus()` + cache invalidation before first command.
- **Progress heartbeat** (`heartbeat_tick()`): emits `ALERT_TICK` (`'.'`
  0x2E) every 100 ms during `writeSpdPage()` and `cmdPMICReadADC()`.
  Host discards it and resets its read timeout.

### 1.3 LED state machine (fw/UDF_fw.c)

| State | Condition | Pattern |
|-------|-----------|---------|
| `LED_IDLE` | USB connected, no command | Steady ON |
| `LED_BUSY` | Command executing | 5 Hz blink |
| `LED_WRITE` | `writeSpdPage()` running | 1 Hz pulse |
| `LED_FAULT` | HV feedback asserted without enable | 10 Hz blink |
| `LED_ERROR` | Last command returned false | 3 rapid flashes |
| (off) | USB disconnected | Steady OFF |

`CMD_PinControl` for `PIN_DEV_STATUS` is honoured **only** while
`led_state == LED_IDLE`; otherwise returns false.

### 1.4 SPD operations (lib + fw + src/Tabs/SPDOperationsTab.cs)

- `ReadEntireSPDAsync` / `WriteEntireSPDAsync` with I2C clock fallback.
- `ReadSPDAsync` (page-aligned chunks, 64 B max, DDR5 MR11 page switch).
- `WriteSPDByte` / `WriteSPDPage` with 10 ms EEPROM write delay.
- `TestWrite` — non-destructive write-capability probe.
- DDR5 page calculation off-by-one fix: `bytesThisPage = Min(128, remaining)`.
- RSWP: `SetRSWP`, `GetRSWP`, `ClearRSWP` (DDR4 HV + SA1, DDR5 MR12/13).
- PSWP: `SetPSWP`, `GetPSWP`.
- SPD5 hub registers: `ReadSPD5HubRegister`, `WriteSPD5HubRegister`.
- `RecalcAndFixCrc` patches CRC bytes in-memory (does not write to DIMM).

### 1.5 SPD parsed fields (src/Tabs/SPDParsedFields.cs)

Full JEDEC-compliant decode for DDR4 and DDR5:

**DDR4** (JEDEC 21-C Annex L Release 25):
- Module type (UDIMM/RDIMM/LRDIMM/SO-DIMM/Mini/72b-SO…)
- Capacity from B4/B6/B12/B13 with 3DS rank multiplication
- All timing parameters in MTB×125 + FTB (signed) → ps → nCK
- `PsToNckDdr4`: `ceil(ps/tCK − 0.01)` per JEDEC §8 Byte 17
- `Ddr4SpeedGrade`: thresholds validated against JEDEC (2133 = 937 ps)
- CAS latency bitmask decode (B20–B23, baseCL 7 or 23)
- Base CRC (B0–B125 → B126/127) + Block 1 CRC (B128–B253 → B254/255)
- JEP-106 manufacturer lookup
- Module PN (20 bytes ASCII), SN (4 bytes hex), date (BCD)

**DDR5** (JESD400-5D Release 1.4):
- Module type (UDIMM/RDIMM/LRDIMM/SO-DIMM/CUDIMM/CSODIMM/MRDIMM/CAMM2)
- 16-bit LE ps storage; tRFC family in ns (×1000 for ps)
- `PsToNckDdr5`: `trunc(trunc(ps×997)/tCK + 1000) / 1000`
- Lower-clock-limit byte for tRRD_L, tCCD_L, tFAW, etc.
- CAS latencies: 5 bytes, even values 20–98
- Single CRC at B510/511 covering B0–B509
- Mfg info at B512–B554; PN = 30 bytes ASCII

**UI**: collapsible `GroupBox` beside hex viewer; "Recalc & Fix CRC" button;
`ProgressBar` during writes; `Consolas 9.5pt` for hex viewer.

### 1.6 PMIC operations (src/Tabs/PMICOperationsTab.cs)

- Full 256-register dump read/write.
- Per-register read/write (`ReadPMICDevice`, `WriteI2CDevice`).
- Vendor region lock/unlock (MTP password, `R37`/`R38`/`R39`).
- Block burn (0–2, burn commands `0x81`/`0x82`/`0x85`).
- Password change (burn new password to MTP).
- Enable programmable mode (`R2F` bit 2).
- Enable full/manufacturer access (default password unlock).
- VReg enable/disable/toggle/state.
- PGOOD pin read + per-register PGOOD status.
- Set PGOOD output mode (0=HW, 1=force LOW, 2=force HIGH).
- ADC measurement: voltages (mV), currents (mA), powers (mW).
- Live measurement timer (re-entrancy fixed: stop before await, start in finally).
- DIMM reboot via `VIN_CTRL` toggle.
- PMIC type identification + mode string.

### 1.7 CLI mode (src/Program.cs)

34 commands across 7 groups:

```
DEVICE:    ping  version  test  name  i2c-speed  scan  detect
           rswp-support  reboot-dimm  factory-reset

SPD:       spd read  spd read-bytes  spd write  spd write-byte
           spd verify  spd crc  spd fix-crc  spd test-write
           spd parse  spd parse-file  spd hub-reg  spd pswp

RSWP:      rswp get  rswp set  rswp clear

PMIC:      pmic read  pmic write  pmic reg-read  pmic reg-write
           pmic type  pmic mode  pmic enable-prog-mode
           pmic enable-full-access  pmic is-unlocked
           pmic unlock  pmic lock  pmic change-password
           pmic vreg-state  pmic enable-vreg  pmic disable-vreg
           pmic toggle-vreg  pmic reboot-dimm  pmic pgood-pin
           pmic pgood-status  pmic set-pgood-mode  pmic measure

PIN:       pin get  pin set  pin reset

EEPROM:    eeprom read

GLOBAL:    --port  --auto-detect  --baud  --timeout
           --output-format text|json|hex
           --quiet  --log-file  --reconnect-timeout
```

Exit codes: 0=OK 1=not-found 2=I2C-error 3=verify-fail 4=CRC-error
5=write-error 6=usage-error.

`AttachConsole(-1)` / `AllocConsole()` in `RunCli()` ensures console
output works from WinExe builds.

`spd parse-file` works without a connected device.

### 1.8 Connection UX (src/MainForm.cs)

- `DisconnectCheckLoopAsync` on background task with `PeriodicTimer`.
- `BusMonitorLoopAsync` on background task; results via `BeginInvoke`.
- `--reconnect-timeout <sec>` CLI flag: polls for port reappearance.
- `AutoConnect` from `AppSettings`: reconnects to last port on startup.

### 1.9 Settings (src/AppSettings.cs)

JSON at `%LOCALAPPDATA%\UnifiedDDRFlasher\settings.json`:
- `AutoConnect`, `LastPort`
- `PmicPasswords`: per-type `(byte lsb, byte msb)`, fallback to JEDEC
  default `(0x73, 0x94)`.

### 1.10 Build / packaging (src/Unified-DDR-Flasher.csproj)

- Target: .NET Framework 4.8.1 `WinExe`.
- `UDFCoreLibraryPath` — resolved from `UDFCore.local.props`,
  environment variable, or `/p:` flag. Build fails fast with an
  actionable error if unset or file missing.
- Costura.Fody: embeds all references into the `.exe` at build time.
- Output: `Unified-DDR-Flasher.exe` + `Unified-DDR-Flasher.exe.config`.

### 1.11 Library project (lib/UDF-Core.csproj)

- .NET Framework 4.8.1 class library.
- Assembly: `UDF-Core.dll`, namespace `UDFCore`, class `UDFDevice`.
- Output to `lib/prebuilt/`.
- `AssemblyVersion` must match firmware `FW_VER` (format `YYYY.M.D.0`).

---

## 2. Serial Protocol Reference (complete)

### Framing

| Direction | Layout |
|-----------|--------|
| Host → FW | `[command byte] [parameter bytes...]` |
| FW → Host | `[marker] [length] [payload...] [checksum]` where checksum = sum of payload bytes mod 256 |

### Markers

| Byte | Symbol | Meaning |
|------|--------|---------|
| `0x26` | `&` | RESPONSE |
| `0x40` | `@` | ALERT (out-of-band, no length/checksum) |
| `0x3F` | `?` | UNKNOWN — command not recognised |
| `0xFF` | NAK | I2C read failure sentinel (1-byte payload in `&` frame) |

### Alert codes (second byte after `@`)

| Byte | Symbol | Meaning |
|------|--------|---------|
| `0x21` | `!` | READY (inside `&` frame for CMD_PING response) |
| `0x2B` | `+` | SLAVE_INC |
| `0x2D` | `-` | SLAVE_DEC |
| `0x2F` | `/` | CLOCK_INC |
| `0x5C` | `\` | CLOCK_DEC |
| `0x2E` | `.` | ALERT_TICK (progress heartbeat, host resets timeout counter) |

### Command codes

| Cmd | Hex | Name | FW handler | Response |
|-----|-----|------|------------|----------|
| `CMD_Get` | `0xFF` | modifier | — | — |
| `CMD_Disable` | `0x00` | modifier | — | — |
| `CMD_Enable` | `0x01` | modifier | — | — |
| `CMD_Version` | `0x02` | firmware version | `cmdVersion` | 4 bytes LE uint32 |
| `CMD_Test` | `0x03` | self-test | `cmdTest` | 1 byte (0/1) |
| `CMD_Ping` | `0x04` | connectivity | `cmdPing` | 1 byte `!` (0x21) |
| `CMD_Name` | `0x05` | get/set name | `cmdName` | string or 1 byte |
| `CMD_FactoryReset` | `0x06` | erase EEPROM | `cmdFactoryReset` | 1 byte (0/1) |
| `CMD_SpdReadPage` | `0x07` | read SPD bytes | `cmdReadSpdPage` | N bytes or NAK |
| `CMD_SpdWriteByte` | `0x08` | write 1 SPD byte | `cmdWriteSpdByte` | 1 byte (0/1) |
| `CMD_SpdWritePage` | `0x09` | write ≤16 bytes | `cmdWriteSpdPage` | 1 byte (0/1) |
| `CMD_SpdWriteTest` | `0x0A` | write probe | `cmdWriteTest` | 1 byte (0/1) |
| `CMD_Ddr4Detect` | `0x0B` | DDR4 probe | `cmdDdr4Detect` | 1 byte (0/1) |
| `CMD_Ddr5Detect` | `0x0C` | DDR5 probe | `cmdDdr5Detect` | 1 byte (0/1) |
| `CMD_Spd5HubReg` | `0x0D` | SPD5 hub MR | `cmdSpd5Hub` | 1 byte |
| `CMD_SpdSize` | `0x0E` | module size | `cmdSize` | 1 byte (1/2/3/4) |
| `CMD_ScanBus` | `0x0F` | scan 0x50–0x57 | `cmdScanBus` | 1 byte (bitmask) |
| `CMD_BusClock` | `0x10` | I2C clock | `cmdBusClock` | 1 byte |
| `CMD_ProbeAddress` | `0x11` | probe address | `cmdProbeBusAddress` | 1 byte (0/1) |
| `CMD_PMICWriteReg` | `0x12` | write PMIC reg | `cmdPMICWriteReg` | 1 byte (0/1) |
| `CMD_PMICReadReg` | `0x13` | read PMIC reg | `cmdPmicReadReg` | 1 byte |
| `CMD_PinControl` | `0x14` | GPIO read/write | `cmdPinControl` | 1 byte |
| `CMD_PinReset` | `0x15` | reset pins | `cmdPinReset` | 1 byte (0/1) |
| `CMD_Rswp` | `0x16` | RSWP operations | `cmdRSWP` | 1 byte (0/1) |
| `CMD_Pswp` | `0x17` | PSWP operations | `cmdPSWP` | 1 byte (0/1) |
| `CMD_RswpReport` | `0x18` | RSWP capability | `cmdRswpReport` | 1 byte (mask) |
| `CMD_Eeprom` | `0x19` | internal EEPROM | `cmdEeprom` | bytes or 1 byte |
| `CMD_PMICReadADC` | `0x1A` | PMIC ADC bulk | `cmdPMICReadADC` | N bytes |

---

## 3. Hardware Pin Map

| Logical name | Enum | GPIO | Dir | Default | Purpose |
|---|---|---|---|---|---|
| `STAT` | `PIN_DEV_STATUS` | 0 | OUT | 0 | Status LED |
| `HV_SW` | `PIN_HV_SWITCH` | 9 | OUT | 0 | SPD +9 V switching |
| `HV_FB` | (read-only) | 8 | IN | — | High-voltage feedback |
| `HV_EN` | `PIN_HV_CONVERTER` | 10 | OUT | 1 | 9 V boost enable |
| `SA1_EN` | `PIN_SA1_SWITCH` | 11 | OUT | 1 | DDR4 SA1 / page-select |
| `VIN_CTRL` | `PIN_DDR5_VIN_CTRL` | 16 | OUT | 1 | DDR5 VIN_BULK enable |
| `PWR_EN` | `PIN_PMIC_CTRL` | 14 | OUT | 0 | PMIC switching enable |
| `PWR_GOOD` | `PIN_PMIC_FLAG` | 15 | IN | — | PMIC PWR_GOOD |
| `RFU1` | `PIN_RFU1` | 18 | OUT | 0 | Reserved |
| `RFU2` | `PIN_RFU2` | 19 | OUT | 0 | Reserved |
| I2C SDA | — | 4 | bi | hi-Z | I2C0 SDA |
| I2C SCL | — | 5 | bi | hi-Z | I2C0 SCL |

---

## 4. JEDEC Compliance Rules (invariants — never relax)

### DDR4 (JEDEC 21-C Annex L Release 25)

- MTB = 125 ps; FTB = 1 ps (signed `int8`).
- `ps = mtbByte * 125 + (sbyte)ftbByte`
- `nCK = ceil(ps / tCK_ps − 0.01)` — the 0.01 guard is JEDEC-mandated.
- tCKAVGmin/max: display as ps + speed grade only — **never convert to nCK**.
- DDR4-2133 threshold = 937 ps (= 8×125 − 63), **not** 938 ps.
- Base CRC: `CRC16(spd[0..125])` stored at B126/127 (LE).
- Block 1 CRC: `CRC16(spd[128..253])` stored at B254/255 (LE).
- CRC polynomial: CCITT 0x1021, init 0x0000.

### DDR5 (JESD400-5D Release 1.4)

- No MTB/FTB. Timings are 16-bit LE ps (or ns for tRFC family).
- **tRFC1/RFC2/RFCsb are nanoseconds.** Multiply by 1000 before dividing by tCK.
- Rounding: `nCK = trunc(trunc(ps × 997) / tCK + 1000) / 1000` (integer arithmetic).
- Lower-clock-limit byte: `nCK = Max(calculated, limitByte)`.
- CAS latencies are even values 20–98 decoded from B24–B28 (5 bytes).
- Single CRC at B510/511 covering B0–B509.
- DRAM type byte = `0x12`. Never apply DDR4 offsets to a DDR5 dump.

### PMIC (JESD301-2 Rev 1.03 — PMIC5100)

- Default vendor password: LSB `0x73`, MSB `0x94`.
- Vendor unlock: write LSB to `R37`, MSB to `R38`, `0x01` to `R39`.
- Programmable mode: set `R2F[2]`.
- Block burn commands: block 0 → `0x81`, block 1 → `0x82`, block 2 → `0x85`.
- PGOOD status: `R08` bits 2:0 = SWA/SWB/SWC good flags.

---

## 5. Performance Constraints

The following performance characteristics are intentional and must not be
regressed by any change:

| Operation | Expected time | Notes |
|---|---|---|
| DDR5 SPD read (1024 B) | 1–2 s | ~16 page reads × 64 B |
| DDR4 SPD read (512 B) | ~1 s | |
| DDR5 SPD write (1024 B) | ~30 s | 10 ms EEPROM write delay per page |
| PMIC full register dump | < 2 s | 256 single-byte reads |
| CMD_PING round-trip | < 20 ms | |
| Bus scan (8 addresses) | < 200 ms | cached 200 ms |

`SendAndReceiveWithRetryAsync` adds **zero** overhead on the happy path
(one extra async frame). The retry path (≤ 350 ms) only fires on a
detected fault.

`ReadEntireSPDAsync` I2C clock fallback adds **zero** overhead when all
chunks succeed — a single `bool clockFallenBack` branch not taken.

Do not add polling loops, `Thread.Sleep`, or unconditional delays to the
hot read/write path.

---

## 6. Rules for All Changes

### Naming (invariant — never change)

| Thing | Name |
|---|---|
| Product | Unified DDR Flasher |
| Executable | `Unified-DDR-Flasher.exe` |
| Library DLL | `UDF-Core.dll` |
| Library namespace | `UDFCore` |
| Device class | `UDFDevice` |
| App namespace | `UnifiedDDRFlasher` |
| Settings folder | `%LOCALAPPDATA%\UnifiedDDRFlasher\` |
| Build props key | `UDFCoreLibraryPath` |
| Firmware files | `UDF_fw.c`, `UDF_fw.h`, `UDF_fw.uf2` |
| Solution | `UDF.sln` |

### Protocol (invariant — never change)

- Command byte values (`CMD_*` hex codes) are fixed. Existing firmware in
  the field will break if you renumber them.
- Response marker bytes (`&` `@` `?`) are fixed.
- Checksum algorithm (sum of payload bytes mod 256) is fixed.
- NAK sentinel `0xFF` (one-byte `&` frame payload) is fixed.
- `ALERT_TICK` = `'.'` (0x2E) is fixed.

New commands must use the next available hex code after `0x1A`.

### Public API (stable — additive only)

Do not rename, remove, or change the signature of any `public` method or
property on `UDFDevice` or `SPDParsedFields`. The DLL ABI must remain
backward-compatible across releases. New methods may be added freely.

### Thread safety (invariant)

Every `UDFDevice` method that touches the serial port must be safe to call
from a background thread. UI updates must use `Control.BeginInvoke`.
Never call `Control.Invoke` or block on the UI thread from a serial callback.

### Firmware (invariant)

- No VLAs in any command handler. Use fixed-size stack arrays.
- All command handlers must call `outputResponse()` on every exit path,
  even when the response is empty (`& 00 00`).
- `heartbeat_tick()` must be called inside any loop that can run longer
  than ~200 ms (it kicks the watchdog and emits `ALERT_TICK`).
- `recover_i2c_bus()` is called automatically from `i2c_write_timeout`
  and `i2c_read_timeout` on `PICO_ERROR_TIMEOUT`. Do not call it
  elsewhere without a matching cache invalidation.
- Compile cleanly with `-Wall -Wextra -Wvla -Werror=vla`.

### Error messages

Must be specific and actionable. Include address, offset, expected vs.
actual value where relevant. Never use "Operation failed." or "Error."
alone.

---

## 7. Known Intentional Design Decisions

These are deliberate choices, not bugs. Do not "fix" them.

**Target .NET Framework 4.8.1, not .NET 8.** WinForms on .NET 8 requires
Windows Desktop Runtime, which is not pre-installed. 4.8.1 is in-box on
Windows 11 and available as a free update for Windows 10.

**`OutputType=WinExe`.** Necessary for the WinForms host. Console output
is restored by `AttachConsole(-1)` / `AllocConsole()` in `RunCli()`.

**Single retry, not three.** A second failure after a `FlushHandshakeAsync`
re-sync indicates a genuine device problem, not a transient glitch. Adding
more retries masks real faults.

**I2C clock stays at 100 kHz after a fallback.** Toggling the clock
per-chunk would add latency and risk re-triggering the fallback. Once
fallen back, stay fallen back for the operation.

**`eeprom_flush()` is explicit, not per-byte.** Flash wear reduction.
The RAM shadow is always consistent; only flash lags behind.

**`SPDParsedFields.RecalcAndFixCrc()` does not write to the DIMM.** This
is intentional. The user must explicitly press "Write All" after fixing
the CRC. It avoids silent data modification.

**`spd parse-file` works without a connected device.** The CLI parses
a binary file from disk; useful for offline analysis and scripting.

---

## 8. Extension Guidelines

When adding a new feature, follow this checklist:

### New firmware command

1. Add `#define CMD_NewThing 0x1B` (next free hex) to `UDF_fw.h`.
2. Write `static void cmdNewThing(void)` in `UDF_fw.c`.
3. Call `heartbeat_tick()` inside any loop > ~200 ms.
4. Use fixed-size stack buffers only (no VLAs).
5. All exit paths must call `outputResponse()`.
6. Add a `case CMD_NewThing: cmdNewThing(); break;` in `parseCommand()`.
7. Add the corresponding public method to `UDFDevice` in `SPDToolLibrary.cs`.
8. Add the CLI command to `Program.cs` → `DispatchCommand()`.
9. Document in `docs/INTERNAL_DEV.md` command table.

### New CLI command

1. Add a `case "new-thing":` to `DispatchCommand()`.
2. Implement `CmdNewThing(UDFDevice, CliOptions)` returning an exit code.
3. Support `--output-format json` (use `ToJson(Dictionary<string,object>)`).
4. Add `--new-switch` to `ParseArgs` if needed.
5. Document in the `PrintUsage()` banner.
6. Document in `docs/README.md` CLI section.

### New PMIC subcommand

1. Add a `case "new-sub":` to `CmdPmic()`.
2. Call the corresponding `UDFDevice.NewPmicMethod()`.
3. If the operation can take > 200 ms, ensure firmware emits heartbeats.

### New SPD parsed field

1. Add parsing logic to `SPDParsedFields.Parse()`.
2. If DDR4: use `Ddr4PsFromMtbFtb` + `PsToNckDdr4`. Never apply to DDR5.
3. If DDR5: use `Ddr5Ps16` + `PsToNckDdr5`. Apply lower-clock-limit.
4. Append to the correct section of the returned `ParseResult.Fields` list.
5. Update JEDEC byte-reference tables in `docs/INTERNAL_DEV.md`.

---

## 9. Release Process

1. Bump `FW_VER` in `fw/UDF_fw.h` (date-decimal `YYYYMMDD`).
2. Build firmware:
   ```bash
   cd fw/ && mkdir -p build && cd build
   cmake -DPICO_SDK_PATH=$PICO_SDK_PATH ..
   make -j$(nproc)
   cp UDF_fw.uf2 ../../firmware/
   cp UDF_fw.bin ../../firmware/
   ```
3. Bump `AssemblyVersion` in `lib/UDF-Core.csproj` to `YYYY.M.D.0`.
4. Rebuild library:
   ```bash
   cd lib/
   msbuild UDF-Core.csproj /p:Configuration=Release
   ```
5. Run the Testing Checklist from `docs/INTERNAL_DEV.md`.
6. Update changelog in `docs/README.md`.
7. Commit:
   ```bash
   git add fw/UDF_fw.h firmware/ lib/prebuilt/
   git commit -m "release: vYYYY.M.D - <summary>"
   git tag -a "vYYYY.M.D" -m "<summary>"
   git push origin master --tags
   ```
8. Create GitHub release; attach `firmware/UDF_fw.uf2`.

---

## 10. Files the Agent Must Deliver (maintenance mode)

In maintenance mode, deliver only the files that were actually changed.
Never regenerate the full project unless explicitly asked.

| When changing | Files to deliver |
|---|---|
| Firmware (bug fix or new cmd) | `fw/UDF_fw.h`, `fw/UDF_fw.c` |
| Protocol (new command) | `fw/UDF_fw.h`, `fw/UDF_fw.c`, `lib/SPDToolLibrary.cs` |
| New CLI command | `src/Program.cs` |
| New SPD field | `src/Tabs/SPDParsedFields.cs` |
| New PMIC operation | `src/Tabs/PMICOperationsTab.cs`, `lib/SPDToolLibrary.cs` |
| Docs only | `docs/README.md`, `docs/INTERNAL_DEV.md` |
| Full release | All of the above + `lib/prebuilt/UDF-Core.dll` + `firmware/UDF_fw.uf2` |

*End of AI Agent Guideline v4.0*
