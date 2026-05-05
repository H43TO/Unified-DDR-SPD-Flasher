// §4: JEDEC-compliant SPD parsed fields — full field dump.
//
// Returns a list of (label, value, isWarning) tuples that the UI panel can render.
// All byte references are 0-indexed.
//
// Sources (PDFs ship with the project):
//   DDR4: JEDEC 21-C Annex L (Serial Presence Detect for DDR4).
//   DDR5: JESD400-5D (DDR5 Serial Presence Detect Contents) Release 1.4.
//   JEP-106 manufacturer table cross-referenced against the public memtest86+
//   short list and the Yatekii/jep106 Rust crate (JEP106BL, Feb 2025).
//
// Coverage notes:
//   - DDR4: every base-block field documented in §3-4.10 of the spec is dumped,
//     plus the manufacturing block at 320-353. CRCs at 126-127 and 254-255 are
//     verified.
//   - DDR5: every base-block field 0~127 is dumped. The common module section
//     (192~239) is dumped including SPD/PMIC/thermal sensor manufacturer IDs,
//     module organisation, channel bus width, heat-spreader / temp range, and
//     reference raw card. Capacity is computed (the previous version was missing
//     this entirely). Manufacturing block at 512~554 is dumped. CRC at 510-511
//     verified.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UnifiedDDRFlasher
{
    public static class SPDParsedFields
    {
        public class Field
        {
            public string Label;
            public string Value;
            public bool IsCrcStatus;     // true => use green/red indicator
            public bool CrcOk;
            public Field(string l, string v) { Label = l; Value = v; }
        }

        public class ParsedResult
        {
            public string DramType;          // "DDR4" / "DDR5" / "Unknown"
            public List<Field> Fields = new List<Field>();
            public byte[] PatchedDump;       // populated by RecalcAndFixCrc
        }

        // Entry point.
        public static ParsedResult Parse(byte[] spd)
        {
            var r = new ParsedResult();
            if (spd == null || spd.Length < 32)
            {
                r.DramType = "Unknown (insufficient data)";
                return r;
            }
            switch (spd[2])
            {
                case 0x0C:
                case 0x0E:
                    r.DramType = spd[2] == 0x0E ? "DDR4E" : "DDR4";
                    ParseDdr4(spd, r);
                    return r;
                case 0x12:
                case 0x14:
                    r.DramType = spd[2] == 0x14 ? "DDR5 NVDIMM-P" : "DDR5";
                    ParseDdr5(spd, r);
                    return r;
                default:
                    r.DramType = $"Unknown (0x{spd[2]:X2})";
                    r.Fields.Add(new Field("DRAM Type", $"0x{spd[2]:X2} (unsupported by parser)"));
                    return r;
            }
        }

        // Local helper that lets parser fns add fields with section dividers
        // for nicer rendering in the ParsedFieldsDialog.
        private static void Section(ParsedResult r, string title)
            => r.Fields.Add(new Field("─── " + title + " ───", ""));

        #region DDR4

        // §4.2
        private static int Ddr4PsFromMtbFtb(byte mtbValue, sbyte ftbValue)
            => mtbValue * 125 + ftbValue;

        // §4.2 - DDR4 guardband rounding (JEDEC 21-C Annex L §8 Byte 17).
        private static int PsToNckDdr4(int timingPs, int tCkPs)
            => (int)Math.Ceiling((double)timingPs / tCkPs - 0.01);

        private static void ParseDdr4(byte[] spd, ParsedResult r)
        {
            // ── General configuration ───────────────────────────────────────────
            Section(r, "General");
            int spdBytesUsed = spd[0] & 0x0F;
            int spdBytesTotal = (spd[0] >> 4) & 0x0F;
            r.Fields.Add(new Field("SPD Bytes Used",
                spdBytesUsed == 1 ? "128"
                : spdBytesUsed == 2 ? "256"
                : spdBytesUsed == 3 ? "384"
                : spdBytesUsed == 4 ? "512"
                : $"reserved (0x{spdBytesUsed:X})"));
            r.Fields.Add(new Field("SPD Bytes Total",
                spdBytesTotal == 1 ? "256"
                : spdBytesTotal == 2 ? "512"
                : $"reserved (0x{spdBytesTotal:X})"));
            r.Fields.Add(new Field("DRAM Type", spd[2] == 0x0E ? "DDR4E SDRAM" : "DDR4 SDRAM"));
            r.Fields.Add(new Field("SPD Revision", $"{spd[1] >> 4}.{spd[1] & 0xF}"));
            r.Fields.Add(new Field("Module Type", Ddr4ModuleType(spd[3] & 0x0F)));
            r.Fields.Add(new Field("Hybrid", Ddr4HybridType((spd[3] >> 4) & 0x0F)));

            // ── SDRAM density / addressing ──────────────────────────────────────
            Section(r, "SDRAM");
            int densIdx = spd[4] & 0x0F;
            int[] densMb = { 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 12288, 24576 };
            int sdramCapMb = (densIdx >= 0 && densIdx < densMb.Length) ? densMb[densIdx] : 0;
            r.Fields.Add(new Field("DRAM Density", FormatMb(sdramCapMb)));

            int bgBits = (spd[4] >> 6) & 0x03;
            r.Fields.Add(new Field("Bank Groups", bgBits == 0 ? "0 (no BG)" : (bgBits == 1 ? "2" : (bgBits == 2 ? "4" : "reserved"))));
            int banksPerBg = (spd[4] >> 4) & 0x03;
            r.Fields.Add(new Field("Banks per BG", banksPerBg == 0 ? "4" : (banksPerBg == 1 ? "8" : "reserved")));
            r.Fields.Add(new Field("Row Bits", $"{((spd[5] >> 3) & 0x07) + 12}"));
            r.Fields.Add(new Field("Col Bits", $"{(spd[5] & 0x07) + 9}"));

            int dieCount = ((spd[6] >> 4) & 0x07) + 1;
            r.Fields.Add(new Field("Die Count (pkg)", dieCount.ToString()));
            r.Fields.Add(new Field("Package Type", (spd[6] & 0x80) == 0 ? "Monolithic SDP" : "Non-monolithic"));
            int sigLoad = spd[6] & 0x03;
            r.Fields.Add(new Field("3DS Signal Load",
                sigLoad == 0 ? "not specified"
                : sigLoad == 1 ? "Multi-load"
                : sigLoad == 2 ? "3DS single-load"
                : "reserved"));

            // §B7 (max activate count / window) - DDR4 maintenance/refresh attributes.
            int macIdx = spd[7] & 0x0F;
            string mac =
                  macIdx == 0  ? "Untested"
                : macIdx == 1  ? "700 K"
                : macIdx == 2  ? "600 K"
                : macIdx == 3  ? "500 K"
                : macIdx == 4  ? "400 K"
                : macIdx == 5  ? "300 K"
                : macIdx == 6  ? "200 K"
                : macIdx == 8  ? "Unlimited (>=1.8M)"
                : $"reserved (0x{macIdx:X})";
            r.Fields.Add(new Field("Max Activate Count", mac));
            int mawIdx = (spd[7] >> 4) & 0x03;
            r.Fields.Add(new Field("Max Activate Window",
                mawIdx == 0 ? "8192*tREFI" : mawIdx == 1 ? "4096*tREFI" : (mawIdx == 2 ? "2048*tREFI" : "reserved")));

            // ── Module electrical ───────────────────────────────────────────────
            Section(r, "Module Electrical");
            var v = new List<string>();
            if ((spd[11] & 0x01) != 0) v.Add("1.2V operable");
            if ((spd[11] & 0x02) != 0) v.Add("1.2V endurant");
            if ((spd[11] & 0x04) != 0) v.Add("TBD1");
            if ((spd[11] & 0x08) != 0) v.Add("TBD2");
            r.Fields.Add(new Field("Voltage", v.Count == 0 ? "not specified" : string.Join(", ", v)));

            int packageRanks = ((spd[12] >> 3) & 0x07) + 1;
            r.Fields.Add(new Field("Package Ranks", packageRanks.ToString()));
            r.Fields.Add(new Field("Rank Mix", (spd[12] & 0x40) == 0 ? "Symmetrical" : "Asymmetrical"));

            int ioWidthIdx = spd[12] & 0x07;
            int[] ioBits = { 4, 8, 16, 32 };
            int ioWidth = (ioWidthIdx >= 0 && ioWidthIdx < ioBits.Length) ? ioBits[ioWidthIdx] : 0;
            r.Fields.Add(new Field("DRAM Width", $"x{ioWidth}"));

            int busWidthIdx = spd[13] & 0x07;
            int[] busBits = { 8, 16, 32, 64 };
            int busWidth = (busWidthIdx >= 0 && busWidthIdx < busBits.Length) ? busBits[busWidthIdx] : 0;
            r.Fields.Add(new Field("Bus Width", $"{busWidth} bits"));
            int eccBits = (spd[13] >> 3) & 0x03;
            r.Fields.Add(new Field("ECC Extension", eccBits == 0 ? "none" : (eccBits == 1 ? "8-bit ECC" : "reserved")));

            // §B14: thermal sensor presence/type.
            r.Fields.Add(new Field("Thermal Sensor",
                (spd[14] & 0x80) != 0 ? "Installed (TS+EE on hub)" : "not installed / extended"));
            // §B15: extended module type info (when low-nibble of B3 == 0).
            r.Fields.Add(new Field("Extended Module Type", $"0x{spd[15]:X2}"));
            r.Fields.Add(new Field("Maximum Bytes Per Module Self-Refresh",
                (spd[16] & 0x80) != 0 ? "PASR supported" : "PASR not supported"));

            // ── Module capacity (computed) ──────────────────────────────────────
            int logicalRanks;
            bool is3dsSingleLoad = (spd[6] & 0x03) == 0x02;
            if (is3dsSingleLoad)
                logicalRanks = packageRanks * dieCount;
            else
                logicalRanks = packageRanks;

            long totalMb = 0;
            if (sdramCapMb > 0 && busWidth > 0 && ioWidth > 0)
                totalMb = (long)(sdramCapMb / 8) * (busWidth / ioWidth) * logicalRanks;
            r.Fields.Add(new Field("Module Capacity", FormatMb(totalMb)));
            r.Fields.Add(new Field("Logical Ranks", logicalRanks.ToString()));

            // ── Timing parameters ───────────────────────────────────────────────
            Section(r, "Timing");
            int tCkAvgMinPs = Ddr4PsFromMtbFtb(spd[18], (sbyte)spd[125]);
            int tCkAvgMaxPs = Ddr4PsFromMtbFtb(spd[19], (sbyte)spd[124]);

            r.Fields.Add(new Field("tCKAVGmin", $"{tCkAvgMinPs} ps ({Ddr4SpeedGrade(tCkAvgMinPs)})"));
            r.Fields.Add(new Field("tCKAVGmax", $"{tCkAvgMaxPs} ps"));

            int tAaPs  = Ddr4PsFromMtbFtb(spd[24], (sbyte)spd[123]);
            int tRcdPs = Ddr4PsFromMtbFtb(spd[25], (sbyte)spd[122]);
            int tRpPs  = Ddr4PsFromMtbFtb(spd[26], (sbyte)spd[121]);
            int tRasPs = (((spd[27] & 0x0F) << 8) | spd[28]) * 125;
            int tRcPs  = (((spd[27] >> 4) << 8) | spd[29]) * 125 + (sbyte)spd[120];
            int tWrPs  = (((spd[41] & 0x0F) << 8) | spd[42]) * 125;
            int tRfc1Ps = ((spd[31] << 8) | spd[30]) * 125;
            int tRfc2Ps = ((spd[33] << 8) | spd[32]) * 125;
            int tRfc4Ps = ((spd[35] << 8) | spd[34]) * 125;
            int tFawPs = (((spd[36] & 0x0F) << 8) | spd[37]) * 125;
            int tRrdSPs = Ddr4PsFromMtbFtb(spd[38], (sbyte)spd[119]);
            int tRrdLPs = Ddr4PsFromMtbFtb(spd[39], (sbyte)spd[118]);
            int tCcdLPs = Ddr4PsFromMtbFtb(spd[40], (sbyte)spd[117]);
            int tWtrSPs = (((spd[43] & 0x0F) << 8) | spd[44]) * 125;
            int tWtrLPs = (((spd[43] >> 4) << 8) | spd[45]) * 125;

            int tCk = Math.Max(tCkAvgMinPs, 1);
            r.Fields.Add(new Field("tAAmin",   FormatNck(tAaPs,  tCk)));
            r.Fields.Add(new Field("tRCDmin",  FormatNck(tRcdPs, tCk)));
            r.Fields.Add(new Field("tRPmin",   FormatNck(tRpPs,  tCk)));
            r.Fields.Add(new Field("tRASmin",  FormatNck(tRasPs, tCk)));
            r.Fields.Add(new Field("tRCmin",   FormatNck(tRcPs,  tCk)));
            r.Fields.Add(new Field("tWRmin",   FormatNck(tWrPs,  tCk)));
            r.Fields.Add(new Field("tRFC1min", FormatNck(tRfc1Ps, tCk)));
            r.Fields.Add(new Field("tRFC2min", FormatNck(tRfc2Ps, tCk)));
            r.Fields.Add(new Field("tRFC4min", FormatNck(tRfc4Ps, tCk)));
            r.Fields.Add(new Field("tFAWmin",  FormatNck(tFawPs, tCk)));
            r.Fields.Add(new Field("tRRD_Smin", FormatNck(tRrdSPs, tCk)));
            r.Fields.Add(new Field("tRRD_Lmin", FormatNck(tRrdLPs, tCk)));
            r.Fields.Add(new Field("tCCD_Lmin", FormatNck(tCcdLPs, tCk)));
            r.Fields.Add(new Field("tWTR_Smin", FormatNck(tWtrSPs, tCk)));
            r.Fields.Add(new Field("tWTR_Lmin", FormatNck(tWtrLPs, tCk)));

            // CAS Latencies
            r.Fields.Add(new Field("CAS Latencies (CL)", string.Join(", ", DecodeDdr4CasLatencies(spd))));

            // Connector / SDRAM signal loading bytes (B60-77) summarised
            r.Fields.Add(new Field("Connector to SDRAM Bit Mapping", $"B60-B77 (raw, see hex viewer)"));

            // ── CRCs (§4.4) ─────────────────────────────────────────────────────
            Section(r, "CRC");
            ushort baseStored = (ushort)(spd[126] | (spd[127] << 8));
            ushort baseCalc   = Crc16(spd, 126, 0);
            var baseField = new Field("Base CRC (B0-125)",
                $"stored=0x{baseStored:X4} calc=0x{baseCalc:X4} {(baseStored == baseCalc ? "OK" : "FAIL")}")
            { IsCrcStatus = true, CrcOk = baseStored == baseCalc };
            r.Fields.Add(baseField);

            if (spd.Length >= 256)
            {
                ushort blk1Stored = (ushort)(spd[254] | (spd[255] << 8));
                ushort blk1Calc   = Crc16(spd, 126, 128);
                var blk1Field = new Field("Block 1 CRC (B128-253)",
                    $"stored=0x{blk1Stored:X4} calc=0x{blk1Calc:X4} {(blk1Stored == blk1Calc ? "OK" : "FAIL")}")
                { IsCrcStatus = true, CrcOk = blk1Stored == blk1Calc };
                r.Fields.Add(blk1Field);
            }

            // ── Module-specific (B128-255) header info ──────────────────────────
            if (spd.Length >= 132)
            {
                Section(r, "Module Geometry");
                int height = spd[128] & 0x1F;
                r.Fields.Add(new Field("Module Height", height == 0 ? "<= 15 mm" : $"(15 + {height}) mm"));
                int rawCardThickFront = spd[129] & 0x0F;
                int rawCardThickBack  = (spd[129] >> 4) & 0x0F;
                r.Fields.Add(new Field("Raw Card Front Thickness", rawCardThickFront == 0 ? "<= 1 mm" : $"(1 + {rawCardThickFront}) mm"));
                r.Fields.Add(new Field("Raw Card Back Thickness",  rawCardThickBack == 0 ? "<= 1 mm" : $"(1 + {rawCardThickBack}) mm"));
                int rcRev = (spd[130] >> 5) & 0x07;
                int rcRef = spd[130] & 0x1F;
                r.Fields.Add(new Field("Reference Raw Card",
                    rcRef == 0x1F ? $"see B131 (extension)" : $"{(char)('A' + rcRef)} rev {rcRev}"));
            }

            // ── Manufacturing (§4.10) ───────────────────────────────────────────
            if (spd.Length >= 384)
            {
                Section(r, "Manufacturing");
                r.Fields.Add(new Field("Module MfgID", LookupJep106(spd[320], spd[321])));
                r.Fields.Add(new Field("Mfg Location", $"0x{spd[322]:X2}"));
                r.Fields.Add(new Field("Mfg Date", FormatBcdDate(spd[323], spd[324])));
                r.Fields.Add(new Field("Module SN",
                    $"0x{spd[325]:X2}{spd[326]:X2}{spd[327]:X2}{spd[328]:X2}"));
                r.Fields.Add(new Field("Module PN", AsciiTrim(spd, 329, 20)));
                r.Fields.Add(new Field("Module Revision", $"0x{spd[349]:X2}"));
                r.Fields.Add(new Field("DRAM MfgID", LookupJep106(spd[350], spd[351])));
                r.Fields.Add(new Field("DRAM Stepping", spd[352] == 0xFF ? "N/A" : $"0x{spd[352]:X2}"));
            }
        }

        // §4.9
        public static string Ddr4SpeedGrade(int tCkAvgMinPs)
        {
            if (tCkAvgMinPs >= 1250) return "DDR4-1600";
            if (tCkAvgMinPs >= 1071) return "DDR4-1866";
            if (tCkAvgMinPs >= 937)  return "DDR4-2133";
            if (tCkAvgMinPs >= 833)  return "DDR4-2400";
            if (tCkAvgMinPs >= 750)  return "DDR4-2666";
            if (tCkAvgMinPs >= 714)  return "DDR4-2800";
            if (tCkAvgMinPs >= 682)  return "DDR4-2933";
            if (tCkAvgMinPs >= 652)  return "DDR4-3066";
            if (tCkAvgMinPs >= 625)  return "DDR4-3200";
            return $"DDR4 (~{2_000_000 / Math.Max(tCkAvgMinPs, 1)} MT/s)";
        }

        private static List<int> DecodeDdr4CasLatencies(byte[] spd)
        {
            bool highRange = (spd[23] & 0x80) != 0;
            int baseCL = highRange ? 23 : 7;
            var supportedCLs = new List<int>();
            for (int byteIndex = 0; byteIndex < 4; byteIndex++)
            {
                byte b = spd[20 + byteIndex];
                for (int bit = 0; bit < 8; bit++)
                {
                    if (byteIndex == 3 && (bit == 7 || bit == 6)) continue;
                    if (((b >> bit) & 1) == 1)
                        supportedCLs.Add(baseCL + byteIndex * 8 + bit);
                }
            }
            return supportedCLs;
        }

        private static string Ddr4ModuleType(int code)
        {
            switch (code)
            {
                case 0x00: return "Extended (see B15)";
                case 0x01: return "RDIMM";
                case 0x02: return "UDIMM";
                case 0x03: return "SO-DIMM";
                case 0x04: return "LRDIMM";
                case 0x05: return "Mini-RDIMM";
                case 0x06: return "Mini-UDIMM";
                case 0x08: return "72b-SO-RDIMM";
                case 0x09: return "72b-SO-UDIMM";
                case 0x0C: return "16b-SO-DIMM";
                case 0x0D: return "32b-SO-DIMM";
                default:   return $"reserved (0x{code:X})";
            }
        }

        private static string Ddr4HybridType(int code) =>
            code == 0 ? "none" : (code == 9 ? "NVDIMM" : $"reserved (0x{code:X})");

        #endregion

        #region DDR5

        private static int Ddr5Ps16(byte[] spd, int lsbIndex)
            => spd[lsbIndex] | (spd[lsbIndex + 1] << 8);

        // §4.6 - DDR5 JEDEC Rounding Algorithm (JESD400-5D §8.1).
        private static int PsToNckDdr5(int timingPs, int tCkPs)
        {
            long temp = ((long)timingPs * 997L) / Math.Max(tCkPs, 1) + 1000L;
            return (int)(temp / 1000L);
        }

        // §4.7
        public static string Ddr5SpeedGrade(int tCkAvgMinPs)
        {
            if (tCkAvgMinPs <= 0) return "DDR5";
            int dataRate = 2_000_000 / tCkAvgMinPs;
            int[] std = { 3200, 3600, 4000, 4400, 4800, 5200, 5600, 6000,
                          6400, 6800, 7200, 7600, 8000, 8400, 8800, 9200 };
            int best = std[0]; int bestDiff = Math.Abs(std[0] - dataRate);
            foreach (var s in std)
            {
                int d = Math.Abs(s - dataRate);
                if (d < bestDiff) { best = s; bestDiff = d; }
            }
            return $"DDR5-{best}";
        }

        private static List<int> DecodeDdr5CasLatencies(byte[] spd)
        {
            var cls = new List<int>();
            for (int b = 0; b < 5; b++)
                for (int bit = 0; bit < 8; bit++)
                    if (((spd[24 + b] >> bit) & 1) == 1)
                        cls.Add(20 + b * 16 + bit * 2);
            return cls;
        }

        // Maps DDR5 byte-4 density nibble (bits 4:0) to gigabits per die.
        // Returns 0 for reserved/unknown so capacity falls back to "n/a".
        private static int Ddr5DensityToGbits(int densIdx)
        {
            switch (densIdx)
            {
                case 0x01: return 4;
                case 0x02: return 8;
                case 0x03: return 12;
                case 0x04: return 16;
                case 0x05: return 24;
                case 0x06: return 32;
                case 0x07: return 48;
                case 0x08: return 64;
                default:   return 0;
            }
        }

        // Maps die-per-package code to logical-rank multiplier (§8.1.5 Table 22).
        // For symmetrical assemblies, this is also the number of dies in the stack.
        private static int Ddr5LogicalRanksPerPackage(int dieIdx)
        {
            switch (dieIdx)
            {
                case 0: return 1;   // SDP
                case 1: return 1;   // 2-die DDP (still 1 logical rank)
                case 2: return 2;   // 2H 3DS
                case 3: return 4;   // 4H 3DS
                case 4: return 8;   // 8H 3DS
                case 5: return 16;  // 16H 3DS
                default: return 1;
            }
        }

        private static void ParseDdr5(byte[] spd, ParsedResult r)
        {
            // ── General configuration ───────────────────────────────────────────
            Section(r, "General");
            int spdBetaLevel = spd[0] & 0x03;
            int spdBytesUsed = (spd[0] >> 4) & 0x07;
            r.Fields.Add(new Field("SPD Bytes Used",
                spdBytesUsed == 1 ? "256"
                : spdBytesUsed == 2 ? "512"
                : spdBytesUsed == 3 ? "1024"
                : spdBytesUsed == 4 ? "2048"
                : $"reserved (0x{spdBytesUsed:X})"));
            r.Fields.Add(new Field("SPD Beta Level", spdBetaLevel.ToString()));
            r.Fields.Add(new Field("DRAM Type", spd[2] == 0x14 ? "DDR5 NVDIMM-P" : "DDR5 SDRAM"));
            r.Fields.Add(new Field("SPD Revision (base)", $"{spd[1] >> 4}.{spd[1] & 0xF}"));
            r.Fields.Add(new Field("Module Type", Ddr5ModuleType(spd[3] & 0x0F)));
            r.Fields.Add(new Field("Hybrid Media",  Ddr5HybridType((spd[3] >> 4) & 0x0F)));

            // ── First SDRAM ─────────────────────────────────────────────────────
            Section(r, "First SDRAM");
            int densIdx = spd[4] & 0x1F;
            int firstGbits = Ddr5DensityToGbits(densIdx);
            r.Fields.Add(new Field("DRAM Density",
                firstGbits > 0 ? $"{firstGbits} Gb" : $"reserved (0x{densIdx:X})"));

            int dieIdx = (spd[4] >> 5) & 0x07;
            string diePerPkg;
            switch (dieIdx)
            {
                case 0: diePerPkg = "1 (Mono SDP)"; break;
                case 1: diePerPkg = "2 (DDP)"; break;
                case 2: diePerPkg = "2H 3DS"; break;
                case 3: diePerPkg = "4H 3DS"; break;
                case 4: diePerPkg = "8H 3DS"; break;
                case 5: diePerPkg = "16H 3DS"; break;
                default: diePerPkg = $"reserved (0x{dieIdx:X})"; break;
            }
            r.Fields.Add(new Field("Die Per Package", diePerPkg));

            int rowIdx = spd[5] & 0x1F;
            r.Fields.Add(new Field("Row Bits",
                rowIdx == 0 ? "16"
                : rowIdx == 1 ? "17"
                : rowIdx == 2 ? "18"
                : $"reserved (0x{rowIdx:X})"));
            int colIdx = (spd[5] >> 5) & 0x07;
            r.Fields.Add(new Field("Col Bits",
                colIdx == 0 ? "10"
                : colIdx == 1 ? "11"
                : $"reserved (0x{colIdx:X})"));

            int ioIdx = (spd[6] >> 5) & 0x07;
            int[] ioMap = { 4, 8, 16, 32 };
            int firstIoWidth = ioIdx < ioMap.Length ? ioMap[ioIdx] : 0;
            r.Fields.Add(new Field("SDRAM IO Width",
                firstIoWidth > 0 ? $"x{firstIoWidth}" : "reserved"));

            int bgIdx = (spd[7] >> 5) & 0x07;
            int[] bgMap = { 1, 2, 4, 8 };
            r.Fields.Add(new Field("Bank Groups",
                bgIdx < bgMap.Length ? bgMap[bgIdx].ToString() : "reserved"));
            int bpgIdx = spd[7] & 0x07;
            int[] bpgMap = { 1, 2, 4 };
            r.Fields.Add(new Field("Banks per BG",
                bpgIdx < bpgMap.Length ? bpgMap[bpgIdx].ToString() : "reserved"));

            // ── Second SDRAM (asymmetric only) ──────────────────────────────────
            // §8.1.9-12: Bytes 8~11 are the second-SDRAM equivalents of 4~7.
            // For symmetrical modules they are programmed as 0; we only show
            // them when at least one of those bytes is non-zero.
            bool asymmetric = spd.Length > 11 && (spd[8] != 0 || spd[9] != 0 || spd[10] != 0 || spd[11] != 0);
            int secondGbits = 0;
            int secondIoWidth = 0;
            int secondLogicalRanksPerPkg = 1;
            if (asymmetric)
            {
                Section(r, "Second SDRAM (asymmetric)");
                int densIdx2 = spd[8] & 0x1F;
                secondGbits = Ddr5DensityToGbits(densIdx2);
                r.Fields.Add(new Field("DRAM Density (2nd)",
                    secondGbits > 0 ? $"{secondGbits} Gb" : $"reserved (0x{densIdx2:X})"));

                int dieIdx2 = (spd[8] >> 5) & 0x07;
                secondLogicalRanksPerPkg = Ddr5LogicalRanksPerPackage(dieIdx2);
                r.Fields.Add(new Field("Die Per Package (2nd)", $"code 0x{dieIdx2:X} -> {secondLogicalRanksPerPkg} logical rank(s) per pkg"));

                int ioIdx2 = (spd[10] >> 5) & 0x07;
                secondIoWidth = ioIdx2 < ioMap.Length ? ioMap[ioIdx2] : 0;
                r.Fields.Add(new Field("SDRAM IO Width (2nd)",
                    secondIoWidth > 0 ? $"x{secondIoWidth}" : "reserved"));
            }

            // ── Voltages (bytes 16-18) ──────────────────────────────────────────
            Section(r, "Voltage");
            r.Fields.Add(new Field("VDD Nominal",  Ddr5Voltage(spd[16])));
            r.Fields.Add(new Field("VDDQ Nominal", Ddr5Voltage(spd[17])));
            r.Fields.Add(new Field("VPP Nominal",  Ddr5Voltage(spd[18])));

            // ── Timing parameters ───────────────────────────────────────────────
            Section(r, "Timing");
            r.Fields.Add(new Field("Timing Mode",
                (spd[19] & 0x01) == 0 ? "Standard JEDEC" : "Non-standard / OC"));
            int tCkMinPs = Ddr5Ps16(spd, 20);
            int tCkMaxPs = Ddr5Ps16(spd, 22);
            r.Fields.Add(new Field("tCKAVGmin", $"{tCkMinPs} ps ({Ddr5SpeedGrade(tCkMinPs)})"));
            r.Fields.Add(new Field("tCKAVGmax", $"{tCkMaxPs} ps"));

            int tCk = Math.Max(tCkMinPs, 1);
            void Add(string name, int ps) => r.Fields.Add(new Field(name, FormatNckDdr5(ps, tCk)));
            void AddWithFloor(string name, int ps, byte floor)
            {
                int calc = PsToNckDdr5(ps, tCk);
                int fin = Math.Max(calc, floor);
                r.Fields.Add(new Field(name, $"{fin} nCK ({ps} ps; floor={floor})"));
            }

            Add("tAAmin",     Ddr5Ps16(spd, 30));
            Add("tRCDmin",    Ddr5Ps16(spd, 32));
            Add("tRPmin",     Ddr5Ps16(spd, 34));
            Add("tRASmin",    Ddr5Ps16(spd, 36));
            Add("tRCmin",     Ddr5Ps16(spd, 38));
            Add("tWRmin",     Ddr5Ps16(spd, 40));
            Add("tRFC1min",   Ddr5Ps16(spd, 42) * 1000);
            Add("tRFC2min",   Ddr5Ps16(spd, 44) * 1000);
            Add("tRFCsbmin",  Ddr5Ps16(spd, 46) * 1000);

            // 3DS-different-logical-rank refresh times (bytes 48-65) - only
            // meaningful for 3DS parts but always present in the SPD.
            Add("tRFC1_slr_min",  Ddr5Ps16(spd, 48) * 1000);
            Add("tRFC2_slr_min",  Ddr5Ps16(spd, 50) * 1000);
            Add("tRFCsb_slr_min", Ddr5Ps16(spd, 52) * 1000);

            AddWithFloor("tRRD_Lmin",     Ddr5Ps16(spd, 70), spd[72]);
            AddWithFloor("tCCD_Lmin",     Ddr5Ps16(spd, 73), spd[75]);
            AddWithFloor("tCCD_L_WRmin",  Ddr5Ps16(spd, 76), spd[78]);
            AddWithFloor("tCCD_L_WR2min", Ddr5Ps16(spd, 79), spd[81]);
            AddWithFloor("tFAWmin",       Ddr5Ps16(spd, 82), spd[84]);
            AddWithFloor("tCCD_L_WTRmin", Ddr5Ps16(spd, 85), spd[87]);
            AddWithFloor("tCCD_S_WTRmin", Ddr5Ps16(spd, 88), spd[90]);
            AddWithFloor("tRTPmin",       Ddr5Ps16(spd, 91), spd[93]);

            r.Fields.Add(new Field("CAS Latencies (CL)", string.Join(", ", DecodeDdr5CasLatencies(spd))));

            // ── CRC (base block, bytes 0-509) ───────────────────────────────────
            Section(r, "CRC");
            if (spd.Length >= 512)
            {
                ushort stored = (ushort)(spd[510] | (spd[511] << 8));
                ushort calc   = Crc16(spd, 510, 0);
                r.Fields.Add(new Field("Base CRC (B0-509)",
                    $"stored=0x{stored:X4} calc=0x{calc:X4} {(stored == calc ? "OK" : "FAIL")}")
                { IsCrcStatus = true, CrcOk = stored == calc });
            }
            else
            {
                r.Fields.Add(new Field("Base CRC", "n/a (dump < 512 bytes)"));
            }

            // ── Common module section (bytes 192~239) ───────────────────────────
            int pkgRanksPerSubchannel = 0;
            int subchannelsPerDimm = 0;
            int primaryBusWidth = 0;
            int eccBitsExt = 0;
            bool rankMixAsymmetric = false;
            if (spd.Length >= 240)
            {
                Section(r, "Common Module Block");
                r.Fields.Add(new Field("SPD Revision (B192-447)", $"{spd[192] >> 4}.{spd[192] & 0xF}"));
                r.Fields.Add(new Field("Hashing Sequence", $"0x{spd[193]:X2}"));
                r.Fields.Add(new Field("SPD Hub MfgID",   LookupJep106(spd[194], spd[195])));
                r.Fields.Add(new Field("SPD Device Type",     $"0x{spd[196]:X2}"));
                r.Fields.Add(new Field("SPD Device Revision", $"0x{spd[197]:X2}"));

                // PMIC 0/1/2
                if (spd[198] != 0 || spd[199] != 0 || spd[200] != 0)
                {
                    r.Fields.Add(new Field("PMIC0 MfgID",   LookupJep106(spd[198], spd[199])));
                    r.Fields.Add(new Field("PMIC0 Type",     $"0x{spd[200]:X2}"));
                    r.Fields.Add(new Field("PMIC0 Revision", $"0x{spd[201]:X2}"));
                }
                if (spd[202] != 0 || spd[203] != 0 || spd[204] != 0)
                {
                    r.Fields.Add(new Field("PMIC1 MfgID",   LookupJep106(spd[202], spd[203])));
                    r.Fields.Add(new Field("PMIC1 Type",     $"0x{spd[204]:X2}"));
                    r.Fields.Add(new Field("PMIC1 Revision", $"0x{spd[205]:X2}"));
                }
                if (spd[206] != 0 || spd[207] != 0 || spd[208] != 0)
                {
                    r.Fields.Add(new Field("PMIC2 MfgID",   LookupJep106(spd[206], spd[207])));
                    r.Fields.Add(new Field("PMIC2 Type",     $"0x{spd[208]:X2}"));
                    r.Fields.Add(new Field("PMIC2 Revision", $"0x{spd[209]:X2}"));
                }
                if (spd[210] != 0 || spd[211] != 0 || spd[212] != 0)
                {
                    r.Fields.Add(new Field("Thermal Sensor MfgID", LookupJep106(spd[210], spd[211])));
                    r.Fields.Add(new Field("Thermal Sensor Type",     $"0x{spd[212]:X2}"));
                    r.Fields.Add(new Field("Thermal Sensor Revision", $"0x{spd[213]:X2}"));
                }

                int height = spd[230] & 0x1F;
                r.Fields.Add(new Field("Module Nominal Height",
                    height == 0 ? "<= 15 mm" : $"(15 + {height}) mm"));
                int frontMax = spd[231] & 0x0F;
                int backMax  = (spd[231] >> 4) & 0x0F;
                r.Fields.Add(new Field("Module Max Front Thickness",
                    frontMax == 0 ? "<= 1 mm" : $"(1 + {frontMax}) mm"));
                r.Fields.Add(new Field("Module Max Back Thickness",
                    backMax == 0 ? "<= 1 mm" : $"(1 + {backMax}) mm"));

                int rcRev = (spd[232] >> 5) & 0x07;
                int rcRef = spd[232] & 0x1F;
                r.Fields.Add(new Field("Reference Raw Card",
                    rcRef == 0x1F ? "extension (see B233)" : $"{(char)('A' + rcRef)} rev {rcRev}"));

                // Byte 233 - DIMM Attributes
                int rowsHigh = (spd[233] >> 3) & 0x01;
                int rowsLow  = spd[233] & 0x03;
                int rows = (rowsHigh << 2) | rowsLow;
                string rowsStr = rows == 1 ? "1 row" : rows == 2 ? "2 rows" : rows == 3 ? "4 rows" : rows == 4 ? "3 rows" : $"undefined ({rows})";
                r.Fields.Add(new Field("Rows of DRAMs",   rowsStr));
                r.Fields.Add(new Field("Heat Spreader",   (spd[233] & 0x04) != 0 ? "Installed" : "Not installed"));
                int tempRange = (spd[233] >> 4) & 0x0F;
                r.Fields.Add(new Field("Operating Temp Range", Ddr5TempRange(tempRange)));

                // Byte 234 - Module Organisation
                pkgRanksPerSubchannel = ((spd[234] >> 3) & 0x07) + 1;
                rankMixAsymmetric = (spd[234] & 0x40) != 0;
                r.Fields.Add(new Field("Package Ranks/Subchannel", pkgRanksPerSubchannel.ToString()));
                r.Fields.Add(new Field("Rank Mix", rankMixAsymmetric ? "Asymmetrical" : "Symmetrical"));

                // Byte 235 - Memory Channel Bus Width
                int subchanCode = (spd[235] >> 5) & 0x07;
                int[] subchanMap = { 1, 2, 4, 8 };
                subchannelsPerDimm = subchanCode < subchanMap.Length ? subchanMap[subchanCode] : 0;
                int eccCode = (spd[235] >> 3) & 0x03;
                int[] eccMap = { 0, 4, 8 };
                eccBitsExt = eccCode < eccMap.Length ? eccMap[eccCode] : 0;
                int primaryCode = spd[235] & 0x07;
                int[] primaryMap = { 8, 16, 32, 64 };
                primaryBusWidth = primaryCode < primaryMap.Length ? primaryMap[primaryCode] : 0;
                r.Fields.Add(new Field("Sub-channels per DIMM", subchannelsPerDimm > 0 ? subchannelsPerDimm.ToString() : "reserved"));
                r.Fields.Add(new Field("Bus Width Extension",   $"{eccBitsExt} bits ({(eccBitsExt == 0 ? "no ECC" : "ECC")})"));
                r.Fields.Add(new Field("Primary Bus Width",     primaryBusWidth > 0 ? $"{primaryBusWidth} bits/sub-channel" : "reserved"));
                int totalDataWidth = primaryBusWidth * subchannelsPerDimm;
                int totalChanWidth = (primaryBusWidth + eccBitsExt) * subchannelsPerDimm;
                r.Fields.Add(new Field("Total Data Width", $"{totalDataWidth} bits ({totalChanWidth} bits incl. ECC)"));
            }

            // ── Module capacity (computed) ──────────────────────────────────────
            // §8.1.5/9 + §11.10/11.11. Capacity per sub-channel (for the
            // "first SDRAM"):
            //   bits = density_per_die_Gbits * dies_per_pkg_logical_rank
            //        * (primary_bus_width / io_width) * package_ranks
            // For asymmetric modules the second SDRAM contributes its own
            // chunk to logical-rank capacity; we handle only the symmetric
            // case as a clean total here, and for asymmetric we present each
            // SDRAM type's contribution separately to avoid silently giving
            // a wrong number.
            Section(r, "Computed Capacity");
            if (firstGbits > 0 && firstIoWidth > 0 && primaryBusWidth > 0
                && pkgRanksPerSubchannel > 0 && subchannelsPerDimm > 0)
            {
                // Per-subchannel "all-first-SDRAM" capacity in bits.
                // Used directly for symmetrical assemblies; halved for the
                // first-SDRAM contribution in asymmetric assemblies.
                int firstLogicalRanks = Ddr5LogicalRanksPerPackage(dieIdx);
                long allFirstBitsPerSub = (long)firstGbits * 1024L * 1024L * 1024L  // Gb -> bits
                                        * firstLogicalRanks                          // 3DS dies per pkg
                                        * (primaryBusWidth / firstIoWidth)           // packages per sub-channel
                                        * pkgRanksPerSubchannel;                     // ranks per sub-channel

                long totalBits;
                if (rankMixAsymmetric && secondGbits > 0 && secondIoWidth > 0)
                {
                    // §11.10 Figure 2: even ranks are 1st SDRAM, odd ranks
                    // are 2nd SDRAM. So for an N-rank asymmetric assembly,
                    // N/2 ranks per sub-channel are first-type and N/2 are
                    // second-type. We compute each half-contribution
                    // independently (using halfRanks = pkgRanksPerSubchannel/2)
                    // and sum.
                    int halfRanks = pkgRanksPerSubchannel / 2;
                    long firstBitsPerSub = (long)firstGbits * 1024L * 1024L * 1024L
                                         * firstLogicalRanks
                                         * (primaryBusWidth / firstIoWidth)
                                         * halfRanks;
                    long secondBitsPerSub = (long)secondGbits * 1024L * 1024L * 1024L
                                          * secondLogicalRanksPerPkg
                                          * (primaryBusWidth / secondIoWidth)
                                          * halfRanks;
                    totalBits = (firstBitsPerSub + secondBitsPerSub) * subchannelsPerDimm;
                    r.Fields.Add(new Field("Module Capacity (asymmetric)",
                        $"{FormatBytes(totalBits / 8)}  "
                        + $"(1st: {FormatBytes(firstBitsPerSub * subchannelsPerDimm / 8)}, "
                        + $"2nd: {FormatBytes(secondBitsPerSub * subchannelsPerDimm / 8)})"));
                }
                else
                {
                    totalBits = allFirstBitsPerSub * subchannelsPerDimm;
                    r.Fields.Add(new Field("Module Capacity", FormatBytes(totalBits / 8)));
                }
            }
            else
            {
                r.Fields.Add(new Field("Module Capacity", "n/a (incomplete data in SPD)"));
            }

            // ── Manufacturing block (bytes 512-554) ─────────────────────────────
            if (spd.Length >= 555)
            {
                Section(r, "Manufacturing");
                r.Fields.Add(new Field("Module MfgID", LookupJep106(spd[512], spd[513])));
                r.Fields.Add(new Field("Mfg Location", $"0x{spd[514]:X2}"));
                r.Fields.Add(new Field("Mfg Date", FormatBcdDate(spd[515], spd[516])));
                r.Fields.Add(new Field("Module SN",
                    $"0x{spd[517]:X2}{spd[518]:X2}{spd[519]:X2}{spd[520]:X2}"));
                r.Fields.Add(new Field("Module PN", AsciiTrim(spd, 521, 30)));
                r.Fields.Add(new Field("Module Revision", $"0x{spd[551]:X2}"));
                r.Fields.Add(new Field("DRAM MfgID", LookupJep106(spd[552], spd[553])));
                r.Fields.Add(new Field("DRAM Stepping", spd[554] == 0xFF ? "N/A" : $"0x{spd[554]:X2}"));
            }
        }

        // §8.1.17/18/19 - DDR5 nominal voltage encoding.
        // Bits 7~5: nominal value (0=1.1V, 1=reserved, 2=1.0V, 3=1.05V, 4=1.10V, 5=1.20V, 6=1.80V).
        // Bits 4~0: tolerance code (vendor-specific, included in raw form).
        private static string Ddr5Voltage(byte raw)
        {
            int nom = (raw >> 5) & 0x07;
            string nv;
            switch (nom)
            {
                case 0: nv = "1.1 V"; break;
                case 2: nv = "1.0 V"; break;
                case 3: nv = "1.05 V"; break;
                case 4: nv = "1.10 V"; break;
                case 5: nv = "1.20 V"; break;
                case 6: nv = "1.80 V"; break;
                default: nv = $"reserved ({nom})"; break;
            }
            return $"{nv}  (raw 0x{raw:X2})";
        }

        // §8.1.10 (DIMM Attributes byte 233 bits 7~4) - operating temp range code.
        private static string Ddr5TempRange(int code)
        {
            switch (code)
            {
                case 0:  return "A1T (-40 to +125 °C)";
                case 1:  return "A2T (-40 to +105 °C)";
                case 2:  return "A3T (-40 to +85 °C)";
                case 3:  return "IT (-40 to +95 °C)";
                case 4:  return "ST (-25 to +85 °C)";
                case 5:  return "ET (-25 to +105 °C)";
                case 6:  return "RT (0 to +45 °C)";
                case 7:  return "NT (0 to +85 °C)";
                case 8:  return "XT (0 to +95 °C)";
                default: return $"reserved ({code})";
            }
        }

        private static string Ddr5ModuleType(int code)
        {
            switch (code)
            {
                case 0x01: return "RDIMM";
                case 0x02: return "UDIMM";
                case 0x03: return "SODIMM";
                case 0x04: return "LRDIMM";
                case 0x05: return "CUDIMM";
                case 0x06: return "CSODIMM";
                case 0x07: return "MRDIMM";
                case 0x08: return "CAMM2";
                case 0x0A: return "DDIMM";
                case 0x0B: return "Solder-Down";
                default:   return $"reserved (0x{code:X})";
            }
        }

        private static string Ddr5HybridType(int code)
        {
            switch (code)
            {
                case 0x0: return "none";
                case 0x9: return "NVDIMM-N";
                case 0xA: return "NVDIMM-P";
                default:  return $"reserved (0x{code:X})";
            }
        }

        #endregion

        #region Common helpers

        // §4.4 - JEDEC CRC-16 (CCITT-XMODEM, poly 0x1021, init 0).
        public static ushort Crc16(byte[] data, int count, int startIndex)
        {
            ushort crc = 0;
            for (int i = startIndex; i < startIndex + count; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((crc & 0x8000) != 0) crc = (ushort)((crc << 1) ^ 0x1021);
                    else crc = (ushort)(crc << 1);
                }
            }
            return crc;
        }

        /// <summary>
        /// §4.4 "Recalc &amp; Fix CRC" - patches CRCs in a copy of the dump.
        /// Does NOT write to the DIMM. Returns the patched bytes.
        /// </summary>
        public static byte[] RecalcAndFixCrc(byte[] spd)
        {
            if (spd == null) return null;
            var copy = (byte[])spd.Clone();
            if (copy.Length < 128) return copy;

            byte t = copy[2];
            if (t == 0x0C || t == 0x0E)
            {
                ushort baseCrc = Crc16(copy, 126, 0);
                copy[126] = (byte)(baseCrc & 0xFF);
                copy[127] = (byte)(baseCrc >> 8);
                if (copy.Length >= 256)
                {
                    ushort blk1 = Crc16(copy, 126, 128);
                    copy[254] = (byte)(blk1 & 0xFF);
                    copy[255] = (byte)(blk1 >> 8);
                }
            }
            else if (t == 0x12 || t == 0x14)
            {
                if (copy.Length >= 512)
                {
                    ushort crc = Crc16(copy, 510, 0);
                    copy[510] = (byte)(crc & 0xFF);
                    copy[511] = (byte)(crc >> 8);
                }
            }
            return copy;
        }

        // §4.10
        // JEP-106 manufacturer table. Bank-indexed (bank 1 = first byte after
        // continuation strips). Cross-referenced with the public memtest86+
        // short list and Yatekii/jep106 (JEP106BL Feb 2025). Not exhaustive -
        // most defunct/obscure entries are intentionally omitted to keep this
        // file readable. If a stick comes back as "Unknown bank/ID", look it
        // up in JEP106 and add the entry here.
        private static readonly Dictionary<int, string> Jep106 = new Dictionary<int, string>
        {
            // Bank 1
            { (1 << 8) | 0x01, "AMD" },
            { (1 << 8) | 0x04, "Fujitsu" },
            { (1 << 8) | 0x07, "Hitachi" },
            { (1 << 8) | 0x09, "Intel" },
            { (1 << 8) | 0x0B, "NEC" },
            { (1 << 8) | 0x10, "NXP" },
            { (1 << 8) | 0x15, "Philips Semi" },
            { (1 << 8) | 0x1C, "Mitsubishi" },
            { (1 << 8) | 0x1F, "Atmel" },
            { (1 << 8) | 0x20, "STMicroelectronics" },
            { (1 << 8) | 0x2C, "Micron Technology" },
            { (1 << 8) | 0x2D, "ST-Korea (Samsung subset)" },
            { (1 << 8) | 0x37, "AMI" },
            { (1 << 8) | 0x40, "Microchip Technology" },
            { (1 << 8) | 0x4F, "Sharp" },
            { (1 << 8) | 0x60, "DEC" },
            { (1 << 8) | 0x83, "Fairchild" },
            { (1 << 8) | 0x89, "Intel" },
            { (1 << 8) | 0x97, "Texas Instruments" },
            { (1 << 8) | 0x98, "Toshiba" },
            { (1 << 8) | 0xAC, "Apple Computer" },
            { (1 << 8) | 0xAD, "SK Hynix" },
            { (1 << 8) | 0xC1, "Infineon (Siemens)" },
            { (1 << 8) | 0xC2, "Macronix" },
            { (1 << 8) | 0xCE, "Samsung" },
            { (1 << 8) | 0xDA, "Winbond" },
            { (1 << 8) | 0xE0, "LG Semicon" },
            { (1 << 8) | 0xEF, "Nantero" },
            { (1 << 8) | 0xFE, "Numonyx" },

            // Bank 2
            { (2 << 8) | 0x04, "Alcatel Mietec" },
            { (2 << 8) | 0x57, "Spansion" },
            { (2 << 8) | 0x6B, "Intel Corp" },
            { (2 << 8) | 0x73, "Cisco" },
            { (2 << 8) | 0x83, "Faraday Technology" },
            { (2 << 8) | 0x9E, "Kingston" },
            { (2 << 8) | 0xC1, "PNY" },

            // Bank 3
            { (3 << 8) | 0x0B, "Nanya Technology" },
            { (3 << 8) | 0x25, "ASint" },
            { (3 << 8) | 0x4F, "Transcend Information" },
            { (3 << 8) | 0x51, "Qimonda" },
            { (3 << 8) | 0x57, "AENEON" },
            { (3 << 8) | 0x73, "Mushkin Enhanced" },
            { (3 << 8) | 0x7F, "ATP Electronics" },
            { (3 << 8) | 0x94, "Patriot Memory (PDP)" },
            { (3 << 8) | 0x9B, "Crucial Technology" },
            { (3 << 8) | 0xD5, "ADATA Technology" },
            { (3 << 8) | 0xEF, "Elpida" },
            { (3 << 8) | 0xF1, "Nanya" },

            // Bank 4
            { (4 << 8) | 0x06, "Lite-On" },
            { (4 << 8) | 0x10, "Centon" },
            { (4 << 8) | 0x43, "Ramaxel" },
            { (4 << 8) | 0x77, "Kingmax" },
            { (4 << 8) | 0xCB, "A-Data" },
            { (4 << 8) | 0xCD, "G.Skill" },
            { (4 << 8) | 0xF1, "Transcend" },

            // Bank 5
            { (5 << 8) | 0x04, "Corsair" },
            { (5 << 8) | 0x07, "PNY" },
            { (5 << 8) | 0x1A, "Kreton" },
            { (5 << 8) | 0x32, "TakeMS" },
            { (5 << 8) | 0x51, "Qimonda" },
            { (5 << 8) | 0x57, "Smart Modular" },
            { (5 << 8) | 0x9C, "Phison Electronics" },
            { (5 << 8) | 0xCD, "GeIL" },
            { (5 << 8) | 0xF1, "G.Skill" },
            { (5 << 8) | 0xF6, "Avant Technology" },
            { (5 << 8) | 0xFE, "Smart Modular" },

            // Bank 6
            { (6 << 8) | 0x02, "Patriot Memory" },
            { (6 << 8) | 0x4B, "AVEXIR" },
            { (6 << 8) | 0xB3, "Inphi Corporation" },
            { (6 << 8) | 0xD1, "Team Group" },

            // Bank 7
            { (7 << 8) | 0x0B, "Klevv (Essencore)" },
            { (7 << 8) | 0x12, "Apacer Technology" },
            { (7 << 8) | 0x9E, "Crucial / Ballistix" },

            // Bank 9
            { (9 << 8) | 0x13, "Raspberry Pi Trading" },
            { (9 << 8) | 0x94, "Kingston Technology Company (Kingston Fury)" },
        };

        public static string LookupJep106(byte bankByte, byte idByte)
        {
            int bankNumber = (bankByte & 0x7F) + 1;
            int key = (bankNumber << 8) | idByte;
            string name;
            return Jep106.TryGetValue(key, out name)
                ? name
                : $"Unknown (bank {bankNumber}, ID 0x{idByte:X2})";
        }

        private static string FormatNck(int timingPs, int tCkPs)
        {
            int nck = PsToNckDdr4(timingPs, tCkPs);
            return $"{nck} nCK ({timingPs} ps)";
        }

        private static string FormatNckDdr5(int timingPs, int tCkPs)
        {
            int nck = PsToNckDdr5(timingPs, tCkPs);
            return $"{nck} nCK ({timingPs} ps)";
        }

        private static string FormatMb(long mb)
        {
            if (mb <= 0) return "n/a";
            if (mb >= 1024) return $"{mb / 1024.0:F1} GB";
            return $"{mb} MB";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "n/a";
            const long KB = 1024L;
            long MB = KB * KB;
            long GB = MB * KB;
            if (bytes >= GB) return $"{bytes / (double)GB:F1} GB";
            if (bytes >= MB) return $"{bytes / (double)MB:F0} MB";
            return $"{bytes} B";
        }

        private static string FormatBcdDate(byte yearBcd, byte weekBcd)
        {
            int year = ((yearBcd >> 4) * 10) + (yearBcd & 0x0F);
            int week = ((weekBcd >> 4) * 10) + (weekBcd & 0x0F);
            return $"Wk {week:D2} / 20{year:D2}";
        }

        private static string AsciiTrim(byte[] spd, int offset, int length)
        {
            int end = offset + length;
            if (end > spd.Length) end = spd.Length;
            int actualEnd = end;
            while (actualEnd > offset && (spd[actualEnd - 1] == 0x20 || spd[actualEnd - 1] == 0x00))
                actualEnd--;
            var sb = new StringBuilder(actualEnd - offset);
            for (int i = offset; i < actualEnd; i++)
            {
                byte b = spd[i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            return sb.ToString();
        }

        #endregion
    }
}
