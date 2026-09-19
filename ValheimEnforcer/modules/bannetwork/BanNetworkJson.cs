using System;
using System.Collections.Generic;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// Reads and writes the newline-delimited JSON the ban network speaks.
    ///
    /// <para><b>Why NDJSON rather than one document.</b> This project has no JSON parser - only YamlDotNet
    /// and <see cref="JsonWellFormed"/>, a validator. One self-contained object per line means each line can
    /// be size-checked before it is parsed, validated on its own, and skipped when it is bad without losing
    /// the batch around it. A single nested document would give none of that. It is also why the feed's
    /// envelope - cursor, count, more-flag - travels in response headers: it cannot be a line of the body.</para>
    ///
    /// <para><b>Why the validator, and not just YamlDotNet.</b> YAML 1.2 is a JSON superset, so a YAML parser
    /// reads JSON - but it also reads things JSON cannot express, and one of them is dangerous here. YAML has
    /// anchors and aliases; JSON does not. A hostile or compromised endpoint could send a self-referential
    /// expansion, or nesting deep enough to recurse the parser into a StackOverflowException - which is not
    /// catchable and would take the dedicated server process down with it. JsonWellFormed is a strict JSON
    /// parser that rejects anchors outright, and it runs first. That turns "YAML reading JSON" into "YAML
    /// reading text already proven to be JSON", which closes the gap rather than hoping nobody finds it.</para>
    /// </summary>
    internal static class BanNetworkJson {

        /// <summary>
        /// Longest line accepted. Generous for an entry - a subject, four categories, a capped reason - and
        /// far below anything that could be an expansion attack. Checked BEFORE parsing, which is the point.
        /// </summary>
        internal const int MaxLineBytes = 4096;

        /// <summary>Ceiling on a whole response body, mirroring the per-channel caps in ValConfig.</summary>
        internal const int MaxResponseBytes = 8 * 1024 * 1024;

        /// <summary>
        /// JSON-compatible flow mappings, one per line - the same shape AuditLog writes its day files in.
        /// The JsonCompatible builder never emits a tab, a NaN or a YAML-only escape, so what this produces
        /// is readable by a strict JSON parser on the other side.
        /// </summary>
        private static readonly ISerializer LineSerializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
            .DisableAliases()
            .JsonCompatible()
            .Build();

        /// <summary>
        /// Parses one line into a record, or returns null with a reason.
        ///
        /// Order matters: length, then strict JSON validation, then deserialization. Each step is only safe
        /// because the one before it ran.
        /// </summary>
        internal static NetworkBanRecord ParseLine(string line, out string error) {
            error = null;
            if (string.IsNullOrEmpty(line)) { error = "empty line"; return null; }
            if (line.Length > MaxLineBytes) {
                error = $"line is {line.Length} bytes, over the {MaxLineBytes} byte limit";
                return null;
            }
            if (!JsonWellFormed.Validate(line, out string invalid)) {
                error = $"not valid JSON: {invalid}";
                return null;
            }

            try {
                // Safe now: the text has been proven to be JSON, so none of YAML's extra grammar is present.
                // Deserialized into a typed target, never into object - YamlDotNet resolves untyped scalars
                // by pattern, which is where a large integer or an unexpected "yes" would change meaning.
                DataObjects.NetworkBanEntry entry =
                    DataObjects.yamldeserializer.Deserialize<DataObjects.NetworkBanEntry>(line);
                NetworkBanRecord record = NetworkBanRecord.FromEntry(entry);
                if (record == null) { error = "no usable subject"; return null; }
                return record;
            } catch (Exception e) {
                error = e.Message;
                return null;
            }
        }

        /// <summary>Serializes a record as one NDJSON line, newline included.</summary>
        internal static string WriteLine(NetworkBanRecord record) {
            string json = LineSerializer.Serialize(record.ToEntry());
            // JsonCompatible already produces a single line; normalise anyway so a future change to the
            // serializer cannot silently turn one entry into several lines.
            return json.Replace("\r", "").Replace("\n", "") + "\n";
        }

        /// <summary>Serializes one outgoing report as an NDJSON line, newline included.</summary>
        internal static string WriteReportLine(DataObjects.BanReportLine line) {
            string json = LineSerializer.Serialize(line);
            return json.Replace("\r", "").Replace("\n", "") + "\n";
        }

        /// <summary>Builds a request body from a batch of reports.</summary>
        internal static string WriteReportBody(List<DataObjects.BanReportLine> lines) {
            StringBuilder sb = new StringBuilder();
            foreach (DataObjects.BanReportLine line in lines) { sb.Append(WriteReportLine(line)); }
            return sb.ToString();
        }

        /// <summary>
        /// Parses the per-line results the endpoint returns. A line that cannot be read is skipped rather
        /// than failing the batch: the worst case is that an accepted report is retried once, which the
        /// reportId idempotency key makes harmless.
        /// </summary>
        internal static List<DataObjects.BanReportResult> ParseResults(string body) {
            List<DataObjects.BanReportResult> results = new List<DataObjects.BanReportResult>();
            if (string.IsNullOrEmpty(body)) { return results; }
            foreach (string raw in body.Split('\n')) {
                string line = raw.Trim();
                if (line.Length == 0 || line.Length > MaxLineBytes) { continue; }
                if (!JsonWellFormed.Validate(line, out _)) { continue; }
                try {
                    DataObjects.BanReportResult parsed =
                        DataObjects.yamldeserializer.Deserialize<DataObjects.BanReportResult>(line);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.ReportId)) { results.Add(parsed); }
                } catch (Exception) {
                    // Skipped; see the note above.
                }
            }
            return results;
        }

        /// <summary>
        /// Parses a whole response body, skipping what it cannot read. <paramref name="skipped"/> counts the
        /// bad lines so a persistently malformed feed is visible rather than silently thinning the list.
        /// </summary>
        internal static List<NetworkBanRecord> ParseBody(string body, out int skipped) {
            List<NetworkBanRecord> records = new List<NetworkBanRecord>();
            skipped = 0;
            if (string.IsNullOrEmpty(body)) { return records; }

            foreach (string raw in body.Split('\n')) {
                string line = raw.Trim();
                if (line.Length == 0) { continue; }
                NetworkBanRecord record = ParseLine(line, out string error);
                if (record == null) {
                    skipped++;
                    Logger.LogDebug($"Ban network: skipping a feed line: {error}");
                    continue;
                }
                records.Add(record);
            }
            return records;
        }
    }
}
