using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Forge.SolidWorks
{
    public class BomRow
    {
        public string PartNumber;
        public string Description;
        public string FileName;
        public string Configuration;
        public int Quantity;
    }

    public class ExportBomResult
    {
        public int RowCount;
        public int TotalQuantity;
        public string OutputFile;
        public bool Verified;
        public string Info;
        public string Error;
    }

    /// <summary>
    /// Tool 191 — export_bom (WRITE, ASSEMBLY). Structured BOM export to CSV: one row per distinct (file, config)
    /// combination among the assembly's top-level components, with a summed quantity — the same grouping a native
    /// SolidWorks BOM table shows, kills double data entry into an ERP/spreadsheet.
    ///
    /// Suppressed components are excluded (a suppressed part isn't actually in the assembly). Part Number and
    /// Description are read from the component's OWN custom properties (falls back to the bare filename when
    /// absent — never invents a value). Grouping key is (file path + configuration), not raw component name, so
    /// three instances of the same bolt part become ONE row with Quantity=3, matching how a real BOM table rolls
    /// up identical parts (this is what makes an ungrouped per-instance dump wrong).
    ///
    /// Verified INDEPENDENTLY: after writing, the CSV is re-read from disk (a totally separate code path — File.
    /// ReadAllLines, not the StreamWriter that wrote it) and its row count + quantity sum are cross-checked against
    /// a FRESH re-walk of the assembly's components, never against the in-memory row list this method already
    /// built. No solid-body geometry is written — the assembly document itself is never modified or saved.
    /// </summary>
    public static class ExportBom
    {
        public static bool IsIntent(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string c = cmd.ToLowerInvariant();
            // NARROW: requires an export-ish verb (not insert/add/create/generate/make, which InsertBomTable owns
            // for drawing BOM TABLES) WITH the bom/bill-of-materials noun.
            bool verb = Regex.IsMatch(c, @"\b(export|save|write|dump)\w*\b");
            bool obj = Regex.IsMatch(c, @"\bbom\b|\bbill\s+of\s+materials\b");
            return verb && obj;
        }

        public static async Task<ExportBomResult> Run(ISldWorks app, IModelDoc2 model, string intent, Func<string, string, string, string, Task> emit)
        {
            var res = new ExportBomResult();
            var asm = model as AssemblyDoc;
            if (asm == null) { res.Error = "Open the assembly you want a BOM export for."; return res; }

            string asmPath = null; try { asmPath = model.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(asmPath)) { res.Error = "The assembly has never been saved — no folder to write the BOM export next to."; return res; }

            await emit("Scribe", "reading components and rolling up quantities", "run", null);

            var rows = BuildRows(asm);
            if (rows.Count == 0) { res.Error = "No non-suppressed top-level components found — nothing to export."; return res; }

            string outDir = Path.GetDirectoryName(asmPath);
            string baseName = Path.GetFileNameWithoutExtension(asmPath);
            string outPath = Path.Combine(outDir, baseName + "-bom.csv");

            try { WriteCsv(outPath, rows); }
            catch (Exception ex) { res.Error = "Couldn't write the CSV (" + ex.GetType().Name + "): " + ex.Message; return res; }

            res.OutputFile = outPath;
            res.RowCount = rows.Count;
            foreach (var r in rows) res.TotalQuantity += r.Quantity;

            // ---- INDEPENDENT verification: re-read the file just written, cross-check against a FRESH component walk ----
            var reread = ReadCsvBack(outPath);
            var freshRows = BuildRows(asm);
            int freshQty = 0; foreach (var r in freshRows) freshQty += r.Quantity;

            res.Verified = reread != null && reread.Count == freshRows.Count &&
                           SumQty(reread) == freshQty && File.Exists(outPath);
            if (!res.Verified)
            {
                res.Error = "Wrote the CSV but the independent re-read didn't match (" +
                            (reread == null ? "unreadable" : reread.Count + " rows/" + SumQty(reread) + " qty") +
                            " vs a fresh walk's " + freshRows.Count + " rows/" + freshQty + " qty).";
                await emit("Scribe", null, "fail", res.Error);
                return res;
            }

            res.Info = res.RowCount + " BOM row(s), " + res.TotalQuantity + " total component(s) -> \"" + outPath + "\".";
            await emit("Scribe", null, "done", res.RowCount + " rows, " + res.TotalQuantity + " total qty");
            return res;
        }

        private static int SumQty(List<BomRow> rows) { int q = 0; foreach (var r in rows) q += r.Quantity; return q; }

        // top-level, non-suppressed components grouped by (file path + config) -> one BomRow with summed Quantity.
        private static List<BomRow> BuildRows(AssemblyDoc asm)
        {
            var byKey = new Dictionary<string, BomRow>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            object[] comps = null; try { comps = asm.GetComponents(false) as object[]; } catch { }
            foreach (var o in comps ?? new object[0])
            {
                var c = o as Component2; if (c == null) continue;
                bool suppressed = false; try { suppressed = c.IsSuppressed(); } catch { }
                if (suppressed) continue;

                string path = null; try { path = c.GetPathName(); } catch { }
                if (string.IsNullOrEmpty(path)) continue;
                string cfg = null; try { cfg = c.ReferencedConfiguration; } catch { }
                string key = path + "|" + (cfg ?? "");

                if (!byKey.TryGetValue(key, out var row))
                {
                    string pn = null, desc = null;
                    var cdoc = c.GetModelDoc2() as IModelDoc2;
                    if (cdoc != null)
                    {
                        try
                        {
                            var cpm = cdoc.Extension.get_CustomPropertyManager(cfg ?? "");
                            if (cpm != null)
                            {
                                string valOut = null, resolved = null;
                                try { cpm.Get4("Part Number", false, out valOut, out resolved); if (!string.IsNullOrWhiteSpace(valOut)) pn = valOut; } catch { }
                                try { cpm.Get4("Description", false, out valOut, out resolved); if (!string.IsNullOrWhiteSpace(valOut)) desc = valOut; } catch { }
                            }
                        }
                        catch { }
                    }
                    string fileName = Path.GetFileNameWithoutExtension(path);
                    row = new BomRow { PartNumber = pn ?? fileName, Description = desc ?? "", FileName = fileName, Configuration = cfg ?? "", Quantity = 0 };
                    byKey[key] = row;
                    order.Add(key);
                }
                row.Quantity++;
            }

            var rows = new List<BomRow>();
            foreach (var k in order) rows.Add(byKey[k]);
            return rows;
        }

        private static void WriteCsv(string path, List<BomRow> rows)
        {
            var sb = new StringBuilder();
            sb.Append("Item,Part Number,Description,Quantity,File Name,Configuration\r\n");
            int item = 1;
            foreach (var r in rows)
            {
                sb.Append(item++).Append(',')
                  .Append(CsvEscape(r.PartNumber)).Append(',')
                  .Append(CsvEscape(r.Description)).Append(',')
                  .Append(r.Quantity).Append(',')
                  .Append(CsvEscape(r.FileName)).Append(',')
                  .Append(CsvEscape(r.Configuration)).Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string CsvEscape(string s)
        {
            s = s ?? "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        // INDEPENDENT re-parse: plain File.ReadAllLines + manual CSV split, never the StreamWriter/StringBuilder path above.
        private static List<BomRow> ReadCsvBack(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var lines = File.ReadAllLines(path);
                var rows = new List<BomRow>();
                for (int i = 1; i < lines.Length; i++)   // skip header
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    var cols = SplitCsvLine(lines[i]);
                    if (cols.Count < 4) continue;
                    int qty; if (!int.TryParse(cols[3], out qty)) continue;
                    rows.Add(new BomRow { Quantity = qty });
                }
                return rows;
            }
            catch { return null; }
        }

        private static List<string> SplitCsvLine(string line)
        {
            var result = new List<string>();
            var cur = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else cur.Append(ch);
                }
                else
                {
                    if (ch == '"') inQuotes = true;
                    else if (ch == ',') { result.Add(cur.ToString()); cur.Clear(); }
                    else cur.Append(ch);
                }
            }
            result.Add(cur.ToString());
            return result;
        }
    }
}
