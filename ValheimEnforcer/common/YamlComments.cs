using System;
using System.Collections.Generic;
using System.Text;

namespace ValheimEnforcer.common {

    /// <summary>
    /// Carries the comments in a generated YAML file across a rewrite.
    ///
    /// YamlDotNet drops comments on round trip: the deserializer never sees them and the serializer has nothing
    /// to write back. Every file this mod regenerates therefore used to lose them - the header banner
    /// Mods.yaml is created with was gone by the end of the first launch, and a note an admin left on an entry
    /// ("pinned by hand, do not touch") disappeared with it. Silently deleting text somebody typed is the bug
    /// this fixes.
    ///
    /// The approach is textual rather than a real YAML round trip: <see cref="Capture"/> reads the old file and
    /// remembers each comment block against the document path of the line it sat above, and
    /// <see cref="Reapply"/> puts those blocks back into the newly serialized text at the same paths. That is
    /// enough because the files involved are machine generated - block mappings, uniform indentation, no flow
    /// style and no multi-line scalars - and it means nothing about the emitted YAML itself changes.
    ///
    /// Only whole-line comments are preserved. A comment sharing a line with a value is discarded either way,
    /// since the serializer rewrites that line from the object graph.
    /// </summary>
    internal static class YamlComments {

        /// <summary>
        /// Precedes comments whose entry has since left the file. They are moved here rather than dropped:
        /// losing admin-authored text is the failure being fixed, and an orphan usually means the mod was
        /// removed, which is exactly when the note explaining it is worth reading.
        /// </summary>
        internal const string OrphanNotice = "# --- The comments below were attached to entries that are no longer in this file ---";

        /// <summary>Comment blocks lifted out of a file, keyed by the document path they were anchored to.</summary>
        internal sealed class Captured {
            /// <summary>Path -> the comment lines that sat directly above it, verbatim.</summary>
            internal readonly Dictionary<string, List<string>> Blocks = new Dictionary<string, List<string>>();
            /// <summary>Anchor paths in the order they appeared, so orphans keep their original ordering.</summary>
            internal readonly List<string> Order = new List<string>();
            /// <summary>Comments after the last mapping line, which have nothing below them to anchor to.</summary>
            internal readonly List<string> Trailing = new List<string>();

            /// <summary>
            /// True when the file opened with a comment block. Used to decide whether a regenerated file needs
            /// its header banner put back - see ModManager.PersistModSettings.
            /// </summary>
            internal bool HasLeadingBlock { get; set; }

            internal bool IsEmpty {
                get { return Blocks.Count == 0 && Trailing.Count == 0; }
            }

            internal void Add(string path, List<string> block) {
                if (Blocks.ContainsKey(path)) { return; }
                Blocks.Add(path, block);
                Order.Add(path);
            }
        }

        /// <summary>
        /// Reads every comment block out of <paramref name="existingText"/>. Returns an empty capture for null
        /// or empty input, which is the normal case the first time a file is written.
        /// </summary>
        internal static Captured Capture(string existingText) {
            Captured captured = new Captured();
            if (string.IsNullOrEmpty(existingText)) { return captured; }

            PathTracker tracker = new PathTracker();
            List<string> pending = new List<string>();
            bool seenAnchor = false;

            foreach (string line in SplitLines(existingText)) {
                if (IsComment(line)) {
                    pending.Add(line);
                    continue;
                }
                if (IsBlank(line)) {
                    // Kept only once a block has started, so the spacing inside and below a banner survives
                    // while the blank lines between unrelated entries are left to the serializer.
                    if (pending.Count > 0) { pending.Add(line); }
                    continue;
                }

                string path = tracker.Next(line);
                if (path == null) {
                    // A line we cannot describe (an unexpected scalar continuation). Holding onto the block
                    // rather than dropping it costs at worst a slightly misplaced comment; discarding it costs
                    // the admin their text.
                    continue;
                }

                if (pending.Count > 0) {
                    List<string> block = TrimLeadingBlanks(pending);
                    if (block.Count > 0) {
                        captured.Add(path, block);
                        if (!seenAnchor) { captured.HasLeadingBlock = true; }
                    }
                    pending.Clear();
                }
                seenAnchor = true;
            }

            // Blank lines at the end of the file are the serializer's business, not a comment's, so unlike an
            // anchored block the trailing one is trimmed on both sides. Without that a comment at EOF gains a
            // blank line on every rewrite.
            captured.Trailing.AddRange(TrimTrailingBlanks(TrimLeadingBlanks(pending)));
            return captured;
        }

        /// <summary>
        /// Writes the captured comments back into freshly serialized YAML, matching them up by document path.
        /// </summary>
        internal static string Reapply(string newText, Captured captured) {
            if (captured == null || captured.IsEmpty || string.IsNullOrEmpty(newText)) { return newText; }

            string newline = DetectNewline(newText);
            List<string> lines = SplitLines(newText);

            // A generated file ends with a newline, which shows up as a final empty element. Dropping it here
            // and restoring it at the end keeps trailing comments from landing after a stray blank line.
            bool endedWithNewline = lines.Count > 0 && lines[lines.Count - 1].Length == 0;
            if (endedWithNewline) { lines.RemoveAt(lines.Count - 1); }

            PathTracker tracker = new PathTracker();
            HashSet<string> placed = new HashSet<string>();
            StringBuilder output = new StringBuilder();

            foreach (string line in lines) {
                if (!IsComment(line) && !IsBlank(line)) {
                    string path = tracker.Next(line);
                    if (path != null && !placed.Contains(path) && captured.Blocks.TryGetValue(path, out List<string> block)) {
                        placed.Add(path);
                        foreach (string comment in block) { output.Append(comment).Append(newline); }
                    }
                }
                output.Append(line).Append(newline);
            }

            foreach (string comment in captured.Trailing) { output.Append(comment).Append(newline); }
            AppendOrphans(output, captured, placed, newline);

            string result = output.ToString();
            if (!endedWithNewline && result.EndsWith(newline, StringComparison.Ordinal)) {
                result = result.Substring(0, result.Length - newline.Length);
            }
            return result;
        }

        /// <summary>
        /// Re-emits blocks whose anchor is gone from the new document. The notice is written only when the file
        /// does not already carry one, so a file that has collected orphans before does not grow a second
        /// header every time it is rewritten.
        /// </summary>
        private static void AppendOrphans(StringBuilder output, Captured captured, HashSet<string> placed, string newline) {
            List<string> orphans = new List<string>();
            foreach (string path in captured.Order) {
                if (placed.Contains(path)) { continue; }
                orphans.Add(path);
            }
            if (orphans.Count == 0) { return; }

            Logger.LogInfo($"Keeping comments for {orphans.Count} entry/entries no longer present in the file: {string.Join(", ", orphans.ToArray())}");

            if (!captured.Trailing.Contains(OrphanNotice)) {
                output.Append(OrphanNotice).Append(newline);
            }
            foreach (string path in orphans) {
                foreach (string comment in captured.Blocks[path]) { output.Append(comment).Append(newline); }
            }
        }

        /// <summary>
        /// Tracks where in a YAML document a line sits, as a path built from the mapping keys above it -
        /// "requiredMods", "requiredMods/Azumatt.AzuCraftyBoxes", "requiredMods/Azumatt.AzuCraftyBoxes/version".
        /// The path is what a comment is anchored to, so a note stays with its entry even when the serializer
        /// reorders or renumbers everything around it.
        /// </summary>
        private sealed class PathTracker {
            private readonly List<int> indents = new List<int>();
            private readonly List<string> keys = new List<string>();
            private string sequenceParent;
            private int sequenceIndex;

            /// <summary>The path of <paramref name="line"/>, or null if it is not a mapping key or list item.</summary>
            internal string Next(string line) {
                int indent = CountIndent(line);
                string body = line.Substring(indent);
                if (body.Length == 0 || body[0] == '#') { return null; }

                if (body[0] == '-' && (body.Length == 1 || body[1] == ' ')) {
                    // Sequence entries are not popped against: YamlDotNet emits them at the same indent as the
                    // key that owns them, so the enclosing key is still the top of the stack.
                    string parent = Join();
                    if (parent != sequenceParent) {
                        sequenceParent = parent;
                        sequenceIndex = 0;
                    }
                    return $"{parent}[{sequenceIndex++}]";
                }

                string key = ParseKey(body);
                if (key == null) { return null; }

                // Compared by actual indent rather than a fixed nesting width, so a hand-edited file that uses
                // a different indent still resolves to the same paths.
                while (indents.Count > 0 && indents[indents.Count - 1] >= indent) {
                    indents.RemoveAt(indents.Count - 1);
                    keys.RemoveAt(keys.Count - 1);
                }
                indents.Add(indent);
                keys.Add(key);
                sequenceParent = null;
                sequenceIndex = 0;
                return Join();
            }

            private string Join() {
                return string.Join("/", keys.ToArray());
            }
        }

        /// <summary>
        /// The key of a mapping line, unquoted, or null if the line is not one. Unquoting matters for matching:
        /// whether a GUID needs quoting depends on the value beside it, so the same entry can be written
        /// quoted one time and bare the next.
        /// </summary>
        private static string ParseKey(string body) {
            char quote = body[0];
            if (quote == '"' || quote == '\'') {
                int close = body.IndexOf(quote, 1);
                if (close < 0 || close + 1 >= body.Length || body[close + 1] != ':') { return null; }
                return body.Substring(1, close - 1);
            }

            for (int i = 0; i < body.Length; i++) {
                // Only a colon that ends the key - one followed by a space or the end of the line - separates a
                // key from its value. A colon inside an unquoted value ("local:1.2.3") does not.
                if (body[i] == ':' && (i + 1 == body.Length || body[i + 1] == ' ')) {
                    string key = body.Substring(0, i).TrimEnd();
                    return key.Length == 0 ? null : key;
                }
            }
            return null;
        }

        /// <summary>
        /// The line ending <paramref name="text"/> already uses, so anything prepended to serializer output
        /// matches it instead of mixing the two.
        /// </summary>
        internal static string DetectNewline(string text) {
            return !string.IsNullOrEmpty(text) && text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        }

        private static int CountIndent(string line) {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) { i++; }
            return i;
        }

        private static bool IsComment(string line) {
            int indent = CountIndent(line);
            return indent < line.Length && line[indent] == '#';
        }

        private static bool IsBlank(string line) {
            return CountIndent(line) >= line.Length;
        }

        private static List<string> SplitLines(string text) {
            List<string> lines = new List<string>();
            foreach (string line in text.Split('\n')) {
                lines.Add(line.TrimEnd('\r'));
            }
            return lines;
        }

        private static List<string> TrimLeadingBlanks(List<string> block) {
            int start = 0;
            while (start < block.Count && IsBlank(block[start])) { start++; }
            return block.GetRange(start, block.Count - start);
        }

        private static List<string> TrimTrailingBlanks(List<string> block) {
            int end = block.Count;
            while (end > 0 && IsBlank(block[end - 1])) { end--; }
            return block.GetRange(0, end);
        }
    }
}
