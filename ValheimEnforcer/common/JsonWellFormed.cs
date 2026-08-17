using System;

namespace ValheimEnforcer.common {

    /// <summary>
    /// A strict well-formedness check for JSON, used to catch a broken notification template before it is
    /// posted rather than after Discord refuses it.
    ///
    /// Hand-rolled because nothing in this project can do the job. There is no JSON parser here, and the
    /// obvious substitute - handing the text to YamlDotNet, since YAML is a JSON superset - is too permissive
    /// to be useful: it accepts trailing commas, single-quoted strings and unquoted keys, all of which Discord
    /// rejects. A trailing comma in particular is the exact residue of deleting a field from a template, which
    /// is the single most common edit an admin makes, so missing it would leave the check catching everything
    /// except the failure people actually hit.
    ///
    /// This validates syntax only. It does not care whether Discord likes the resulting document.
    /// </summary>
    internal static class JsonWellFormed {

        /// <summary>Nesting cap. Discord payloads are three or four deep; this only stops a pathological file.</summary>
        private const int MaxDepth = 64;

        /// <summary>
        /// True when <paramref name="text"/> is a syntactically valid JSON document. On failure
        /// <paramref name="error"/> carries a message with a 1-based line and column.
        /// </summary>
        internal static bool Validate(string text, out string error) {
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "the document is empty"; return false; }

            int i = 0;
            try {
                SkipWhitespace(text, ref i);
                ParseValue(text, ref i, 0);
                SkipWhitespace(text, ref i);
                if (i < text.Length) { throw new JsonError(i, $"unexpected '{text[i]}' after the end of the document"); }
                return true;
            } catch (JsonError e) {
                error = $"{e.Reason} (line {LineOf(text, e.Index)}, column {ColumnOf(text, e.Index)})";
                return false;
            }
        }

        private sealed class JsonError : Exception {
            internal readonly int Index;
            internal readonly string Reason;
            internal JsonError(int index, string reason) : base(reason) { Index = index; Reason = reason; }
        }

        private static void SkipWhitespace(string s, ref int i) {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) { i++; }
        }

        private static void ParseValue(string s, ref int i, int depth) {
            if (depth > MaxDepth) { throw new JsonError(i, "nested too deeply"); }
            if (i >= s.Length) { throw new JsonError(i, "the document ends where a value was expected"); }

            switch (s[i]) {
                case '{': ParseObject(s, ref i, depth); return;
                case '[': ParseArray(s, ref i, depth); return;
                case '"': ParseString(s, ref i); return;
                case '\'': throw new JsonError(i, "single quotes are not valid JSON - use double quotes");
                case 't': Expect(s, ref i, "true"); return;
                case 'f': Expect(s, ref i, "false"); return;
                case 'n': Expect(s, ref i, "null"); return;
                default:
                    if (s[i] == '-' || (s[i] >= '0' && s[i] <= '9')) { ParseNumber(s, ref i); return; }
                    throw new JsonError(i, $"'{s[i]}' does not start a valid JSON value");
            }
        }

        private static void ParseObject(string s, ref int i, int depth) {
            i++; // {
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return; }

            while (true) {
                SkipWhitespace(s, ref i);
                if (i >= s.Length) { throw new JsonError(i, "the document ends inside an object - a '}' is missing"); }
                if (s[i] == '}') { throw new JsonError(i, "trailing comma before '}' - JSON does not allow one"); }
                if (s[i] != '"') { throw new JsonError(i, "an object key must be a double-quoted string"); }
                ParseString(s, ref i);

                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') { throw new JsonError(i, "expected ':' after an object key"); }
                i++;

                SkipWhitespace(s, ref i);
                ParseValue(s, ref i, depth + 1);

                SkipWhitespace(s, ref i);
                if (i >= s.Length) { throw new JsonError(i, "the document ends inside an object - a '}' is missing"); }
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return; }
                throw new JsonError(i, $"expected ',' or '}}' in an object but found '{s[i]}'");
            }
        }

        private static void ParseArray(string s, ref int i, int depth) {
            i++; // [
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return; }

            while (true) {
                SkipWhitespace(s, ref i);
                if (i >= s.Length) { throw new JsonError(i, "the document ends inside an array - a ']' is missing"); }
                if (s[i] == ']') { throw new JsonError(i, "trailing comma before ']' - JSON does not allow one"); }
                ParseValue(s, ref i, depth + 1);

                SkipWhitespace(s, ref i);
                if (i >= s.Length) { throw new JsonError(i, "the document ends inside an array - a ']' is missing"); }
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return; }
                throw new JsonError(i, $"expected ',' or ']' in an array but found '{s[i]}'");
            }
        }

        private static void ParseString(string s, ref int i) {
            int start = i;
            i++; // opening quote
            while (true) {
                if (i >= s.Length) { throw new JsonError(start, "a string is never closed - a '\"' is missing"); }
                char c = s[i];
                if (c == '"') { i++; return; }
                if (c == '\\') {
                    i++;
                    if (i >= s.Length) { throw new JsonError(i, "the document ends inside a string escape"); }
                    char e = s[i];
                    if (e == '"' || e == '\\' || e == '/' || e == 'b' || e == 'f' || e == 'n' || e == 'r' || e == 't') { i++; continue; }
                    if (e == 'u') {
                        if (i + 4 >= s.Length) { throw new JsonError(i, "a \\u escape needs four hex digits"); }
                        for (int k = 1; k <= 4; k++) {
                            char h = s[i + k];
                            bool hex = (h >= '0' && h <= '9') || (h >= 'a' && h <= 'f') || (h >= 'A' && h <= 'F');
                            if (!hex) { throw new JsonError(i + k, "a \\u escape needs four hex digits"); }
                        }
                        i += 5;
                        continue;
                    }
                    throw new JsonError(i, $"'\\{e}' is not a valid JSON escape");
                }
                // A raw newline or control character inside a string is invalid JSON, and is the usual sign of
                // a missing closing quote further back.
                if (c < 0x20) { throw new JsonError(i, "a string contains a raw control character - a '\"' is probably missing"); }
                i++;
            }
        }

        private static void ParseNumber(string s, ref int i) {
            int start = i;
            if (i < s.Length && s[i] == '-') { i++; }
            if (i >= s.Length || s[i] < '0' || s[i] > '9') { throw new JsonError(start, "a number is missing its digits"); }
            if (s[i] == '0') { i++; } else { while (i < s.Length && s[i] >= '0' && s[i] <= '9') { i++; } }
            if (i < s.Length && s[i] == '.') {
                i++;
                if (i >= s.Length || s[i] < '0' || s[i] > '9') { throw new JsonError(i, "a number has no digits after its decimal point"); }
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') { i++; }
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E')) {
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) { i++; }
                if (i >= s.Length || s[i] < '0' || s[i] > '9') { throw new JsonError(i, "a number has no digits in its exponent"); }
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') { i++; }
            }
        }

        private static void Expect(string s, ref int i, string literal) {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0) {
                throw new JsonError(i, $"expected '{literal}'");
            }
            i += literal.Length;
        }

        private static int LineOf(string s, int index) {
            int line = 1;
            for (int k = 0; k < index && k < s.Length; k++) { if (s[k] == '\n') { line++; } }
            return line;
        }

        private static int ColumnOf(string s, int index) {
            int col = 1;
            for (int k = index - 1; k >= 0 && k < s.Length; k--) {
                if (s[k] == '\n') { break; }
                col++;
            }
            return col;
        }
    }
}
