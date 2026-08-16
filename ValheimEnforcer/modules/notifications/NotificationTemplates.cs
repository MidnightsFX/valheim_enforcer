using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ValheimEnforcer.common;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.notifications {

    /// <summary>Every message this mod can post. One template in Notifications.yaml per entry.</summary>
    internal enum NotificationEvent {
        ServerStartup,
        ServerShutdown,
        WorldSaved,
        PlayerJoined,
        PlayerLeft,
        CheaterBanned,
        CharacterRejected,
        ModMismatch,
    }

    /// <summary>
    /// The routing groups an admin can point at separate channels. Deliberately coarser than
    /// <see cref="NotificationEvent"/>: a webhook URL per individual event would be eight secrets to manage for
    /// a split almost nobody wants, whereas "keep join spam out of the moderation channel" is the actual ask.
    /// </summary>
    internal enum NotificationCategory {
        PlayerActivity,
        ServerStatus,
        Moderation,
        ModMismatch,
    }

    /// <summary>
    /// Owns Notifications.yaml: the built-in defaults, the on-disk copy, and turning a template plus a bag of
    /// tokens into the exact JSON body posted to Discord.
    ///
    /// The governing rule, and the reason the file is worth editing at all: <b>this class adds nothing of its
    /// own to a message</b>. No default timestamp, no fallback title, no injected colour. It substitutes
    /// placeholders into the admin's text and hands the result to the notifier. That is what makes deleting a
    /// key from a template actually delete that part of the message - the previous design could not do it,
    /// because the embed builder stamped a timestamp on whether anyone wanted one or not. Anything added here
    /// later takes that property away.
    ///
    /// Nothing in here is allowed to throw at a caller. Every entry point runs inside a Harmony patch or an RPC
    /// handler on the server's main thread, and a notification that cannot be formatted is never worth taking
    /// the connect handshake down over - so a bad file degrades to the defaults and logs.
    /// </summary>
    internal static class NotificationTemplates {

        internal const string EmbeddedResourceName = "ValheimEnforcer.assets.Notifications.yaml";

        // Deliberately NOT DataObjects.yamlserializer: that one is built with DefaultValuesHandling.OmitDefaults,
        // which would drop an event an admin had deliberately blanked. The literal block style that keeps these
        // payloads readable across a rewrite is pinned per-property on NotificationTemplateSet, not here.
        private static readonly ISerializer serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .DisableAliases()
            .Build();

        /// <summary>
        /// Longest a single substituted value may be. Discord caps a field value at 1024 and a description at
        /// 4096, and a payload that breaches either is refused outright rather than trimmed. The old embed
        /// builder clamped per slot because it knew which slot it was filling; a literal template does not, so
        /// the cap has to be the one that is safe wherever the value lands.
        /// </summary>
        private const int TokenLimit = 1000;

        /// <summary>
        /// Tokens written to fill a description rather than a field, which have the larger budget. Kept just
        /// under Discord's 4096 so there is room for the rest of the document.
        /// </summary>
        private const int LongTokenLimit = 3900;
        private static readonly HashSet<string> LongTokens = new HashSet<string>(StringComparer.Ordinal) { "summary" };

        private static NotificationTemplateSet templates;
        private static NotificationTemplateSet defaults;
        private static string[] headerLines;

        private static readonly NotificationEvent[] AllEvents = (NotificationEvent[])Enum.GetValues(typeof(NotificationEvent));

        // ---- Defaults -------------------------------------------------------------------------------------

        /// <summary>
        /// The shipped templates, read from the embedded copy of Notifications.yaml. Embedded rather than built
        /// in C# so the JSON an admin sees on disk and the JSON this mod falls back to are the same artifact,
        /// editable as JSON in the repo instead of buried in a string literal.
        /// </summary>
        internal static NotificationTemplateSet Defaults() {
            if (defaults == null) { LoadEmbedded(); }
            return defaults;
        }

        /// <summary>The banner from the embedded file: its leading run of comment lines.</summary>
        internal static string[] FileHeaderLines {
            get {
                if (headerLines == null) { LoadEmbedded(); }
                return headerLines;
            }
        }

        /// <summary>The embedded file verbatim, which is what a missing Notifications.yaml is created from.</summary>
        internal static string GetDefaultConfig() {
            string text = ReadEmbedded();
            return text ?? "";
        }

        private static string ReadEmbedded() {
            try {
                using (Stream stream = typeof(ValheimEnforcer).Assembly.GetManifestResourceStream(EmbeddedResourceName)) {
                    if (stream == null) {
                        Logger.LogWarning($"Embedded notification templates '{EmbeddedResourceName}' were not found.");
                        return null;
                    }
                    using (StreamReader reader = new StreamReader(stream)) { return reader.ReadToEnd(); }
                }
            } catch (Exception e) {
                Logger.LogWarning($"Could not read the embedded notification templates: {e.Message}");
                return null;
            }
        }

        private static void LoadEmbedded() {
            defaults = new NotificationTemplateSet();
            headerLines = new string[0];

            string text = ReadEmbedded();
            if (text == null) { return; }

            List<string> banner = new List<string>();
            foreach (string line in text.Replace("\r\n", "\n").Split('\n')) {
                if (!line.StartsWith("#", StringComparison.Ordinal)) { break; }
                banner.Add(line);
            }
            headerLines = banner.ToArray();

            try {
                NotificationTemplateSet parsed = DataObjects.yamldeserializer.Deserialize<NotificationTemplateSet>(text);
                if (parsed != null) { defaults = parsed; }
            } catch (Exception e) {
                // Shipping a broken embedded file is a build mistake, not something an admin can cause, but
                // taking the plugin down over it would be worse than posting nothing.
                Logger.LogError($"The embedded notification templates could not be parsed: {e.Message}. Notifications will not post until this build is fixed.");
            }
        }

        // ---- Accessors ------------------------------------------------------------------------------------

        internal static NotificationCategory CategoryOf(NotificationEvent evt) {
            switch (evt) {
                case NotificationEvent.PlayerJoined:
                case NotificationEvent.PlayerLeft:
                    return NotificationCategory.PlayerActivity;
                case NotificationEvent.CheaterBanned:
                case NotificationEvent.CharacterRejected:
                    return NotificationCategory.Moderation;
                case NotificationEvent.ModMismatch:
                    return NotificationCategory.ModMismatch;
                case NotificationEvent.ServerStartup:
                case NotificationEvent.ServerShutdown:
                case NotificationEvent.WorldSaved:
                default:
                    return NotificationCategory.ServerStatus;
            }
        }

        internal static string Get(NotificationEvent evt) {
            if (templates == null) { templates = Defaults(); }
            return GetFrom(templates, evt);
        }

        private static string GetFrom(NotificationTemplateSet set, NotificationEvent evt) {
            if (set == null) { return null; }
            switch (evt) {
                case NotificationEvent.ServerStartup: return set.ServerStartup;
                case NotificationEvent.ServerShutdown: return set.ServerShutdown;
                case NotificationEvent.WorldSaved: return set.WorldSaved;
                case NotificationEvent.PlayerJoined: return set.PlayerJoined;
                case NotificationEvent.PlayerLeft: return set.PlayerLeft;
                case NotificationEvent.CheaterBanned: return set.CheaterBanned;
                case NotificationEvent.CharacterRejected: return set.CharacterRejected;
                case NotificationEvent.ModMismatch: return set.ModMismatch;
                default: return null;
            }
        }

        private static void Set(NotificationTemplateSet set, NotificationEvent evt, string template) {
            switch (evt) {
                case NotificationEvent.ServerStartup: set.ServerStartup = template; break;
                case NotificationEvent.ServerShutdown: set.ServerShutdown = template; break;
                case NotificationEvent.WorldSaved: set.WorldSaved = template; break;
                case NotificationEvent.PlayerJoined: set.PlayerJoined = template; break;
                case NotificationEvent.PlayerLeft: set.PlayerLeft = template; break;
                case NotificationEvent.CheaterBanned: set.CheaterBanned = template; break;
                case NotificationEvent.CharacterRejected: set.CharacterRejected = template; break;
                case NotificationEvent.ModMismatch: set.ModMismatch = template; break;
            }
        }

        // ---- Loading --------------------------------------------------------------------------------------

        /// <summary>
        /// Reads the file at startup. When it is missing an event - hand-trimmed, or written by a build from
        /// before that event existed - the built-in default fills the gap and the file is rewritten so the admin
        /// can see what they now have.
        /// </summary>
        internal static void Initialize() {
            string path = ValConfig.NotificationsFilePath;
            if (!File.Exists(path)) {
                // ValConfig.LoadYamlConfigs creates it before this runs; if it somehow did not, defaults still work.
                Logger.LogDebug("Notifications.yaml not present, using the built-in notification templates.");
                templates = Defaults();
                return;
            }
            try {
                if (LoadFromText(File.ReadAllText(path))) {
                    Logger.LogInfo("Notifications.yaml was missing one or more templates; filling them in from the defaults.");
                    Persist();
                }
            } catch (Exception e) {
                Logger.LogWarning($"Could not read {path}: {e.Message}. Using the built-in notification templates.");
                templates = Defaults();
            }
        }

        /// <summary>
        /// Replaces the live template set from YAML text. Returns true when the file is worth rewriting, which
        /// is only ever the case when an event was absent from it entirely.
        ///
        /// A parse failure keeps whatever was already loaded rather than reverting to defaults: an admin who
        /// fat-fingers a colon mid-session should keep the templates that were working a second ago.
        /// </summary>
        internal static bool LoadFromText(string yaml) {
            NotificationTemplateSet parsed;
            try {
                parsed = DataObjects.yamldeserializer.Deserialize<NotificationTemplateSet>(yaml ?? "");
            } catch (Exception e) {
                Logger.LogWarning($"Could not parse {ValConfig.NotificationsFileName}: {e.Message}. Keeping the templates already loaded.");
                return false;
            }
            if (parsed == null) { parsed = new NotificationTemplateSet(); }

            NotificationTemplateSet builtIn = Defaults();
            bool missing = false;
            bool broken = false;
            foreach (NotificationEvent evt in AllEvents) {
                string template = GetFrom(parsed, evt);
                if (string.IsNullOrWhiteSpace(template)) {
                    Set(parsed, evt, GetFrom(builtIn, evt));
                    missing = true;
                    continue;
                }
                if (!IsUsable(evt, template, out string problem)) {
                    // Loud, because the admin is looking at a message they wrote and wondering why Discord shows
                    // something else. The line and column are the whole point of checking here rather than
                    // letting Discord refuse it later.
                    Logger.LogWarning($"Notification template '{CamelName(evt)}' is not valid JSON: {problem}. Using the built-in default for it until this is fixed - your version of it is left in the file untouched.");
                    Set(parsed, evt, GetFrom(builtIn, evt));
                    broken = true;
                }
            }

            templates = parsed;
            Logger.LogDebug("Notification templates loaded.");

            // Rewrite only to fill in events the file never had - the case where an upgrade adds one. A template
            // that IS there but does not parse must stay on disk exactly as typed: the in-memory copy has been
            // swapped for the default so notifications keep working, and persisting that would overwrite the
            // half-finished edit the admin is in the middle of fixing with something they never wrote.
            if (broken && missing) {
                Logger.LogInfo("Not adding the missing notification templates to the file while another one is broken, so nothing overwrites the template being fixed.");
            }
            return missing && !broken;
        }

        /// <summary>
        /// Whether a template produces a valid payload once its placeholders are filled in.
        ///
        /// The check has to run on the rendered output, not the template text: a template is deliberately not
        /// valid JSON on its own, because "color": {colorGreen} reads as an object opening until the placeholder
        /// is substituted. Rendering with the sample tokens first means what gets validated is the shape of the
        /// thing that will actually be posted.
        /// </summary>
        internal static bool IsUsable(NotificationEvent evt, string template, out string problem) {
            string rendered = Substitute(template, SampleTokens());
            if (string.IsNullOrWhiteSpace(rendered)) { problem = "it renders to nothing"; return false; }
            return JsonWellFormed.Validate(rendered, out problem);
        }

        /// <summary>The camelCase key an event has in the file, for messages that point an admin at a line.</summary>
        internal static string CamelName(NotificationEvent evt) {
            string name = evt.ToString();
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// Every event-specific placeholder at once, with plausible values. Used both to validate a template at
        /// load and to drive Enforcer-Test-Notification, so what the command previews is exactly what the check
        /// accepted.
        /// </summary>
        internal static Dictionary<string, string> SampleTokens() {
            return new Dictionary<string, string>(StringComparer.Ordinal) {
                { "server", "Test Server" },
                { "world", "TestWorld" },
                { "onlinePlayers", "3" },
                { "timestamp", DateTime.UtcNow.ToString("o") },
                { "colorGreen", Green.ToString() },
                { "colorAmber", Amber.ToString() },
                { "colorRed", Red.ToString() },
                { "colorGrey", Grey.ToString() },
                { "statusColor", Green.ToString() },
                { "player", "TestViking" },
                { "character", "TestViking" },
                { "playerId", "76561190000000000" },
                { "isAdmin", "no" },
                { "disconnect", "Clean logout" },
                { "savedData", "Player Data up to date." },
                { "deltaWindow", "15" },
                { "reason", "Cheat detection: sample entry, no ban was issued" },
                { "detections", "SampleTool [process: sample.exe]" },
                { "action", "Test" },
                { "maxCharacters", "1" },
                { "summary", "This is a sample mod mismatch. Nobody was actually rejected." },
                { "missingMods", "com.example.SampleMod" },
                { "extraMods", "com.example.NotAllowed" },
                { "versionMismatches", "com.example.WrongVersion" },
                { "adminOnlyMods", "com.example.AdminTool" },
                { "hashMismatches", "com.example.Recompiled" },
                { "unverifiedMods", "com.example.Unpinned" },
            };
        }

        // ---- Writing --------------------------------------------------------------------------------------

        /// <summary>
        /// Serializes the live templates back to disk, keeping any comments the admin added and restoring the
        /// header banner if it went missing. Mirrors ModManager.PersistModSettings, including the self-write
        /// note that stops our own write bouncing back in through the watcher one poll later.
        /// </summary>
        internal static void Persist() {
            string path = ValConfig.NotificationsFilePath;
            try {
                string yaml = serializer.Serialize(templates);
                File.WriteAllText(path, WithPreservedComments(yaml));
                // Qualified: Jotunn.Utils has a ConfigFileWatcher of its own.
                common.ConfigFileWatcher.NoteSelfWrite(path);
            } catch (Exception e) {
                Logger.LogWarning($"Could not write {path}: {e.Message}");
            }
        }

        internal static string WithPreservedComments(string yaml) {
            string path = ValConfig.NotificationsFilePath;
            try {
                string existing = File.Exists(path) ? File.ReadAllText(path) : null;
                YamlComments.Captured captured = YamlComments.Capture(existing);
                string preserved = YamlComments.Reapply(yaml, captured);
                if (captured.HasLeadingBlock) { return preserved; }

                string newline = YamlComments.DetectNewline(yaml);
                return string.Join(newline, FileHeaderLines) + newline + newline + preserved;
            } catch (Exception e) {
                Logger.LogWarning($"Could not preserve the comments in {path}: {e.Message}. Writing it without them.");
                return yaml;
            }
        }

        // ---- Rendering ------------------------------------------------------------------------------------

        /// <summary>
        /// Builds the payload for an event: the admin's template with its placeholders filled in, and nothing
        /// else. Returns null when the template renders to nothing, which is an admin having blanked it out -
        /// Discord rejects a body-less post, so the send is skipped rather than attempted and logged as failed.
        /// </summary>
        internal static string Render(NotificationEvent evt, IDictionary<string, string> tokens) {
            string template = Get(evt);
            if (string.IsNullOrWhiteSpace(template)) { template = GetFrom(Defaults(), evt); }
            if (string.IsNullOrWhiteSpace(template)) { return null; }

            string body = Substitute(template, tokens);
            if (string.IsNullOrWhiteSpace(body)) {
                Logger.LogDebug($"Notification template for {evt} renders to nothing; skipping the post.");
                return null;
            }
            return body;
        }

        /// <summary>
        /// Replaces {placeholders} in a single left-to-right pass, escaping and truncating each value on the way
        /// in.
        ///
        /// Deliberately not a chain of string.Replace calls: a token whose *value* contains braces - a player
        /// name of "{player}", a mod summary quoting one - would then be rescanned and substituted again, with
        /// the outcome depending on dictionary ordering. One pass over the input never looks at what it has
        /// already written.
        ///
        /// Escaping is what makes a literal JSON template safe to hand user-controlled data. A character called
        /// Bj"orn substituted raw would end the string it sits in and corrupt the document, which the previous
        /// design made impossible only because it escaped while building the JSON itself.
        /// </summary>
        internal static string Substitute(string text, IDictionary<string, string> tokens) {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) { return text ?? ""; }

            StringBuilder sb = new StringBuilder(text.Length + 64);
            int i = 0;
            while (i < text.Length) {
                char c = text[i];
                if (c != '{') { sb.Append(c); i++; continue; }

                int close = FindPlaceholderEnd(text, i);
                if (close < 0) {
                    // Not a placeholder, so it is JSON's own object brace - emit it and move on by one. This
                    // is the common case: the body IS a JSON document, so the first '{' of every template is
                    // one of these. Scanning ahead to the next '}' instead would swallow the whole opening of
                    // the document up to the end of the first real placeholder.
                    sb.Append(c);
                    i++;
                    continue;
                }

                string name = text.Substring(i + 1, close - i - 1);
                string value;
                if (tokens != null && tokens.TryGetValue(name, out value)) {
                    sb.Append(Prepare(name, value));
                } else {
                    // Left verbatim so a mistyped placeholder is visible in the message instead of turning
                    // into a silent blank the admin has to go hunting for.
                    Logger.LogDebug($"Notification template placeholder '{{{name}}}' is not available for this event; leaving it as written.");
                    sb.Append(text, i, close - i + 1);
                }
                i = close + 1;
            }
            return sb.ToString();
        }

        /// <summary>Longest a placeholder name may be. Every real one is well under twenty characters.</summary>
        private const int MaxPlaceholderName = 64;

        /// <summary>
        /// The index of the '}' closing a {placeholder} that starts at <paramref name="start"/>, or -1 when
        /// this brace does not open one.
        ///
        /// A placeholder is a run of letters, digits and underscores between braces and nothing else. That
        /// narrowness is what lets the same scanner run over a JSON document without mistaking `{"embeds": ...`
        /// for a token: the character after the brace is a quote, so it is not one.
        /// </summary>
        private static int FindPlaceholderEnd(string text, int start) {
            int limit = Math.Min(text.Length, start + MaxPlaceholderName + 2);
            for (int k = start + 1; k < limit; k++) {
                char c = text[k];
                if (c == '}') {
                    return k > start + 1 ? k : -1; // "{}" is an empty JSON object, not a placeholder
                }
                bool identifier = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
                if (!identifier) { return -1; }
            }
            return -1;
        }

        private static string Prepare(string name, string value) {
            if (string.IsNullOrEmpty(value)) { return ""; }
            int limit = LongTokens.Contains(name) ? LongTokenLimit : TokenLimit;
            if (value.Length > limit) {
                Logger.LogDebug($"Notification placeholder '{{{name}}}' was {value.Length} characters and has been truncated to {limit}.");
                value = value.Substring(0, limit);
            }
            return EscapeJson(value);
        }

        /// <summary>
        /// Makes a value safe to drop inside a JSON string literal. Lifted from the embed builder this replaced,
        /// where it ran as the JSON was assembled; with a literal template there is no assembly step, so it has
        /// to run at substitution instead.
        /// </summary>
        internal static string EscapeJson(string value) {
            if (string.IsNullOrEmpty(value)) { return ""; }
            StringBuilder sb = new StringBuilder(value.Length + 8);
            foreach (char c in value) {
                switch (c) {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) { sb.Append("\\u").Append(((int)c).ToString("x4")); } else { sb.Append(c); }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
