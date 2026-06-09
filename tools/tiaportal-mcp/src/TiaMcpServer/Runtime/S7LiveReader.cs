using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Sharp7;

namespace TiaMcpServer.Runtime
{
    // Read-only live-value reader over the S7 (ISO-on-TCP / RFC1006) protocol.
    // This is a RUNTIME channel, independent of TIA Openness: it talks directly to
    // the CPU on port 102 and never writes, forces, or changes CPU mode.
    //
    // S7-1200/1500 preconditions for absolute reads of DB areas:
    //   - "Permit access with PUT/GET communication" must be enabled on the CPU.
    //   - The DB being read must be NON-optimized (standard access). Optimized DBs
    //     have no fixed absolute layout and cannot be read by DBx.DBd offset.
    // M / I / Q areas do not have the optimized-DB restriction.

    public sealed class S7CpuIdentity
    {
        public bool Connected;
        public string ModuleTypeName = "";
        public string AsName = "";
        public string ModuleName = "";
        public string SerialNumber = "";
        public int PduLength;
        public string? Error;       // connection-level failure
        public string? SzlError;    // CPU identification (SZL) unavailable — common on S7-1200, not fatal
    }

    public sealed class S7ReadItem
    {
        public string Spec = "";
        public string Area = "";   // DB/M/I/Q
        public int Db;
        public int ByteOffset;
        public int BitOffset;
        public string Type = "";   // BOOL/BYTE/SINT/USINT/INT/UINT/WORD/DINT/UDINT/DWORD/REAL
        public object? Value;      // decoded value (null on error)
        public string? Error;
    }

    public sealed class S7ReadResult
    {
        public bool Ok;
        public string? Error;
        public bool IdentityConfirmed;
        public S7CpuIdentity Identity = new S7CpuIdentity();
        public List<S7ReadItem> Items = new List<S7ReadItem>();
        public long ElapsedMs;
    }

    public static class S7LiveReader
    {
        // Sharp7 area codes
        private const int AreaDB = 0x84;
        private const int AreaMK = 0x83; // M (merker)
        private const int AreaPE = 0x81; // I (process inputs)
        private const int AreaPA = 0x82; // Q (process outputs)
        private const int WLByte = 0x02;

        public static S7CpuIdentity ProbeIdentity(string ip, int rack, int slot)
        {
            var id = new S7CpuIdentity();
            var client = new S7Client();
            try
            {
                int res = client.ConnectTo(ip, rack, slot);
                if (res != 0)
                {
                    id.Error = $"Connect failed: {client.ErrorText(res)}";
                    return id;
                }
                id.Connected = true;
                id.PduLength = client.NegotiatedPduLength();

                var info = new S7Client.S7CpuInfo();
                int r = client.GetCpuInfo(ref info);
                if (r == 0)
                {
                    id.ModuleTypeName = (info.ModuleTypeName ?? "").Trim();
                    id.AsName = (info.ASName ?? "").Trim();
                    id.ModuleName = (info.ModuleName ?? "").Trim();
                    id.SerialNumber = (info.SerialNumber ?? "").Trim();
                }
                else
                {
                    id.SzlError = $"GetCpuInfo unavailable: {client.ErrorText(r)} (common on S7-1200; not a connection failure)";
                }
                return id;
            }
            catch (Exception ex)
            {
                id.Error = ex.Message;
                return id;
            }
            finally
            {
                try { client.Disconnect(); } catch { }
            }
        }

        // expectModuleContains: if non-empty, the CPU module type must contain this
        // (case-insensitive) substring or the read is aborted before touching any data.
        // This is the identity guard: e.g. pass "1211C" so we only ever read the
        // intended CPU and never an unrelated PLC that happens to answer on port 102.
        public static S7ReadResult ReadItems(string ip, int rack, int slot, IEnumerable<string> specs, string? expectModuleContains)
        {
            var result = new S7ReadResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var client = new S7Client();
            try
            {
                int res = client.ConnectTo(ip, rack, slot);
                if (res != 0)
                {
                    result.Error = $"Connect to {ip} (rack {rack}, slot {slot}) failed: {client.ErrorText(res)}";
                    return result;
                }
                result.Identity.Connected = true;
                result.Identity.PduLength = client.NegotiatedPduLength();

                var info = new S7Client.S7CpuInfo();
                int infoRes = client.GetCpuInfo(ref info);
                if (infoRes == 0)
                {
                    result.Identity.ModuleTypeName = (info.ModuleTypeName ?? "").Trim();
                    result.Identity.AsName = (info.ASName ?? "").Trim();
                    result.Identity.ModuleName = (info.ModuleName ?? "").Trim();
                    result.Identity.SerialNumber = (info.SerialNumber ?? "").Trim();
                }
                else
                {
                    result.Identity.SzlError = $"GetCpuInfo unavailable: {client.ErrorText(infoRes)} (common on S7-1200)";
                }

                // Identity guard: only ABORT on a positive mismatch. If SZL identity is
                // unavailable (S7-1200 commonly refuses GetCpuInfo), we cannot cross-check,
                // so we proceed on the caller-supplied IP and flag that it was unconfirmed.
                if (!string.IsNullOrWhiteSpace(expectModuleContains))
                {
                    if (!string.IsNullOrWhiteSpace(result.Identity.ModuleTypeName))
                    {
                        if (result.Identity.ModuleTypeName.IndexOf(expectModuleContains!, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            result.Error = $"Identity guard tripped: CPU at {ip} reports module '{result.Identity.ModuleTypeName}', " +
                                           $"which does not contain expected '{expectModuleContains}'. Aborted before reading any data.";
                            return result;
                        }
                        result.IdentityConfirmed = true;
                    }
                    // else: SZL unavailable -> IdentityConfirmed stays false, read proceeds.
                }

                foreach (var spec in specs)
                {
                    var item = ParseSpec(spec);
                    result.Items.Add(item);
                    if (item.Error != null) continue;
                    ReadOne(client, item);
                }

                result.Ok = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                try { client.Disconnect(); } catch { }
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
            }
        }

        private static void ReadOne(S7Client client, S7ReadItem item)
        {
            int area;
            switch (item.Area)
            {
                case "DB": area = AreaDB; break;
                case "M": area = AreaMK; break;
                case "I": area = AreaPE; break;
                case "Q": area = AreaPA; break;
                default: item.Error = $"Unknown area '{item.Area}'"; return;
            }

            int size = TypeSize(item.Type);
            var buffer = new byte[size];
            int r = client.ReadArea(area, item.Db, item.ByteOffset, size, WLByte, buffer);
            if (r != 0)
            {
                item.Error = client.ErrorText(r) +
                    " (S7-1200/1500: needs PUT/GET enabled and a non-optimized DB for absolute reads)";
                return;
            }

            try
            {
                item.Value = Decode(item.Type, buffer, item.BitOffset);
            }
            catch (Exception ex)
            {
                item.Error = "decode failed: " + ex.Message;
            }
        }

        private static int TypeSize(string type)
        {
            switch (type)
            {
                case "BOOL": return 1;
                case "BYTE":
                case "SINT":
                case "USINT": return 1;
                case "INT":
                case "UINT":
                case "WORD": return 2;
                case "DINT":
                case "UDINT":
                case "DWORD":
                case "REAL": return 4;
                default: return 1;
            }
        }

        private static object Decode(string type, byte[] b, int bit)
        {
            switch (type)
            {
                case "BOOL": return S7.GetBitAt(b, 0, bit);
                case "BYTE": return (int)b[0];
                case "USINT": return (int)b[0];
                case "SINT": return (int)(sbyte)b[0];
                case "INT": return (int)S7.GetIntAt(b, 0);
                case "UINT": return (int)S7.GetUIntAt(b, 0);
                case "WORD": return (int)S7.GetWordAt(b, 0);
                case "DINT": return S7.GetDIntAt(b, 0);
                case "UDINT": return (long)S7.GetUDIntAt(b, 0);
                case "DWORD": return (long)S7.GetDWordAt(b, 0);
                case "REAL": return Math.Round((double)S7.GetRealAt(b, 0), 6);
                default: return (int)b[0];
            }
        }

        // Parse classic S7 absolute addresses, optional ":TYPE" suffix:
        //   DB10.DBX2.3 / DB10.DBB4 / DB10.DBW6 / DB10.DBD8
        //   M0.0 / MB10 / MW12 / MD14   (same for I and Q)
        private static readonly Regex DbRx = new Regex(
            @"^DB(?<db>\d+)\.DB(?<sz>[XBWD])(?<byte>\d+)(?:\.(?<bit>\d+))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BitRx = new Regex(
            @"^(?<area>[MIQ])(?<byte>\d+)\.(?<bit>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SzRx = new Regex(
            @"^(?<area>[MIQ])(?<sz>[BWD])(?<byte>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static S7ReadItem ParseSpec(string raw)
        {
            var item = new S7ReadItem { Spec = raw };
            if (string.IsNullOrWhiteSpace(raw)) { item.Error = "empty spec"; return item; }

            string s = raw.Trim();
            string? typeOverride = null;
            int colon = s.IndexOf(':');
            if (colon >= 0)
            {
                typeOverride = s.Substring(colon + 1).Trim().ToUpperInvariant();
                s = s.Substring(0, colon).Trim();
            }

            var mDb = DbRx.Match(s);
            if (mDb.Success)
            {
                item.Area = "DB";
                item.Db = int.Parse(mDb.Groups["db"].Value, CultureInfo.InvariantCulture);
                item.ByteOffset = int.Parse(mDb.Groups["byte"].Value, CultureInfo.InvariantCulture);
                string sz = mDb.Groups["sz"].Value.ToUpperInvariant();
                if (sz == "X")
                {
                    item.BitOffset = mDb.Groups["bit"].Success ? int.Parse(mDb.Groups["bit"].Value, CultureInfo.InvariantCulture) : 0;
                    item.Type = "BOOL";
                }
                else item.Type = DefaultTypeForSize(sz);
                ApplyTypeOverride(item, typeOverride, sz);
                return item;
            }

            var mBit = BitRx.Match(s);
            if (mBit.Success)
            {
                item.Area = mBit.Groups["area"].Value.ToUpperInvariant();
                item.ByteOffset = int.Parse(mBit.Groups["byte"].Value, CultureInfo.InvariantCulture);
                item.BitOffset = int.Parse(mBit.Groups["bit"].Value, CultureInfo.InvariantCulture);
                item.Type = "BOOL";
                return item;
            }

            var mSz = SzRx.Match(s);
            if (mSz.Success)
            {
                item.Area = mSz.Groups["area"].Value.ToUpperInvariant();
                item.ByteOffset = int.Parse(mSz.Groups["byte"].Value, CultureInfo.InvariantCulture);
                string sz = mSz.Groups["sz"].Value.ToUpperInvariant();
                item.Type = DefaultTypeForSize(sz);
                ApplyTypeOverride(item, typeOverride, sz);
                return item;
            }

            item.Error = $"Unrecognized address '{raw}'. Use DB10.DBD0:REAL, DB1.DBX2.3, M0.0, MW12, etc.";
            return item;
        }

        // Convert a TIA watch-table absolute address to an S7 read spec.
        // "%DB1.DBX0.0" -> "DB1.DBX0.0", "%MW4" -> "MW4", "%DB1.DBD0" + float -> "DB1.DBD0:REAL".
        // Returns null for symbolic operands (e.g. "Crew_Data".X) which need no '%' and
        // cannot be read by absolute offset without symbol resolution.
        public static string? TiaAddressToSpec(string? tiaAddress, string? displayFormat)
        {
            if (string.IsNullOrWhiteSpace(tiaAddress)) return null;
            string a = tiaAddress!.Trim();
            if (!a.StartsWith("%")) return null;          // symbolic / non-absolute
            a = a.Substring(1).Trim();

            // Validate it parses as an absolute address before accepting.
            var probe = ParseSpec(a);
            if (probe.Error != null) return null;

            bool isFloat = !string.IsNullOrEmpty(displayFormat) &&
                (displayFormat!.IndexOf("float", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 displayFormat.IndexOf("real", StringComparison.OrdinalIgnoreCase) >= 0);
            if (isFloat && probe.Type == "DWORD")        // 4-byte slot displayed as float -> REAL
                return a + ":REAL";

            // Honor TIA "DEC_signed" so negative values match what the watch table shows
            // (default decoding of B/W/D is unsigned). "DEC_unsigned" keeps the default.
            bool isSigned = !string.IsNullOrEmpty(displayFormat) &&
                displayFormat!.IndexOf("signed", StringComparison.OrdinalIgnoreCase) >= 0 &&
                displayFormat.IndexOf("unsigned", StringComparison.OrdinalIgnoreCase) < 0;
            if (isSigned)
            {
                if (probe.Type == "WORD") return a + ":INT";
                if (probe.Type == "DWORD") return a + ":DINT";
                if (probe.Type == "BYTE") return a + ":SINT";
            }
            return a;
        }

        private static string DefaultTypeForSize(string sz)
        {
            switch (sz)
            {
                case "B": return "BYTE";
                case "W": return "WORD";
                case "D": return "DWORD";
                default: return "BYTE";
            }
        }

        private static void ApplyTypeOverride(S7ReadItem item, string? type, string sz)
        {
            if (string.IsNullOrEmpty(type)) return;
            int want = TypeSize(type!);
            int have = sz == "B" ? 1 : sz == "W" ? 2 : sz == "D" ? 4 : 1;
            if (type == "BOOL")
            {
                item.Error = $"Type BOOL is only valid for bit addresses (e.g. DB1.DBX2.3), not '{item.Spec}'.";
                return;
            }
            if (want != have)
            {
                item.Error = $"Type '{type}' ({want} byte(s)) does not match address size '{sz}' ({have} byte(s)).";
                return;
            }
            item.Type = type!;
        }
    }
}
