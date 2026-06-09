using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcpServer.Siemens
{
    // Static causal tracer orchestration: answers "why is tag X this value / what
    // sets it" by analysing the OFFLINE project logic. Fully read-only on the
    // project; never touches the live CPU. The returned gating operands can then be
    // live-read with ReadPlcLiveValuesS7 to see which condition is currently driving
    // the value. Cross-reference service is unavailable via Openness here, so we
    // export each code block to SimaticML and parse it with CausalTraceParser.
    public partial class Portal
    {
        public ModelContextProtocol.ResponseJsonReport TraceTagCause(string softwarePath, string tag, string blockScope = "")
        {
            var data = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["tag"] = tag,
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["readOnly"] = true
            };
            var warnings = new JsonArray();

            if (IsProjectNull())
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "No project open. Attach first.", Data = data };
            if (string.IsNullOrWhiteSpace(tag))
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "tag is required.", Data = data };

            string normTag = CausalTraceParser.NormalizeOperand(tag);

            List<PlcBlock> blocks;
            try { blocks = GetBlocks(softwarePath, blockScope ?? ""); }
            catch (Exception ex) { return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = $"GetBlocks failed: {ex.Message}", Data = data }; }

            var codeBlocks = blocks.Where(b => !(b is DataBlock)).ToList();
            data["scannedBlockCount"] = codeBlocks.Count;

            var tmpDir = Path.Combine(Path.GetTempPath(), "tia_trace_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);

            var writeSites = new JsonArray();
            var readSites = new JsonArray();
            var allConditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var block in codeBlocks)
                {
                    string blockName = block.Name;
                    string blockPath = GetBlockPath(block);
                    if (!block.IsConsistent) { warnings.Add($"Skipped inconsistent block '{blockName}' (compile first)."); continue; }

                    string xmlPath = Path.Combine(tmpDir, blockName + ".xml");
                    try { block.Export(new FileInfo(xmlPath), ExportOptions.None); }
                    catch (Exception ex) { warnings.Add($"Export failed for '{blockName}': {ex.Message}"); continue; }

                    XDocument doc;
                    try { doc = XDocument.Load(xmlPath); }
                    catch (Exception ex) { warnings.Add($"Parse failed for '{blockName}': {ex.Message}"); continue; }

                    CausalTraceParser.AnalyzeBlock(doc, blockName, blockPath, normTag, tag, writeSites, readSites, allConditions);
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }

            data["writeSites"] = writeSites;
            data["readSites"] = readSites;
            data["gatingConditions"] = new JsonArray(allConditions.OrderBy(x => x).Select(x => JsonValue.Create(x)).ToArray());
            if (warnings.Count > 0) data["warnings"] = warnings;

            string summary;
            if (writeSites.Count == 0)
                summary = $"No block writes '{tag}'. It may be set by HMI, an instruction's output, an indirect/optimized access this parser does not resolve, or the name differs from the project symbol.";
            else
                summary = $"'{tag}' is written at {writeSites.Count} site(s). {allConditions.Count} distinct gating condition operand(s) found. " +
                          "Live-read those with ReadPlcLiveValuesS7 to see which is currently driving the value.";

            return new ModelContextProtocol.ResponseJsonReport
            {
                Ok = true,
                Message = summary,
                Data = data,
                Warnings = warnings.Count > 0 ? warnings.Select(w => w!.ToString()).ToArray() : null,
                Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
            };
        }
    }
}
