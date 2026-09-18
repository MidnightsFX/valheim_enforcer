using BepInEx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;
using Logger = ValheimEnforcer.Logger;

namespace ValheimEnforcer.modules.audit {

    /// <summary>
    /// Lets an admin connected as a client see what history the server holds for a player, and pull it down
    /// to their own machine.
    ///
    /// The console relay cannot carry this. CommandOutputRPC is a line channel with a 256 line ceiling, and a
    /// week of one player's activity is a file rather than a screenful - so this is a transfer channel of its
    /// own, with the same admin gate.
    ///
    /// The rule that shapes every method below: a request names a PLAYER and a DATE RANGE, never a file. The
    /// server composes its own filenames from validated dates and refuses anything that does not resolve
    /// inside its own audit folder. A client that could name a file could read any file the server process
    /// can, and the accounts allowed to send this are precisely the ones worth stealing.
    /// </summary>
    internal static class AuditTransfer {

        internal const byte ModeList = 0;
        internal const byte ModeDownload = 1;

        internal const string DownloadFolder = "AuditDownloads";

        /// <summary>Cap on rows in a listing reply, so a very old audit folder cannot make an enormous packet.</summary>
        private const int MaxListRows = 400;

        /// <summary>Cap on events in one download, matched to the payload ceiling rather than to a day count.</summary>
        private const int MaxDownloadEvents = 200000;

        // ---- Request building (client) --------------------------------------------------------------------

        internal static ZPackage BuildRequest(byte mode, string account, string character, DateTime fromDay, DateTime toDay) {
            ZPackage package = new ZPackage();
            package.Write(mode);
            package.Write(account ?? "");
            package.Write(character ?? "");
            package.Write(Day(fromDay));
            package.Write(Day(toDay));
            return package;
        }

        // ---- Serving (server) -----------------------------------------------------------------------------

        /// <summary>
        /// Answers one request. The caller has already established that the sender is an admin and that the
        /// package is within its size limit; everything that validates the CONTENT of the request is here.
        /// </summary>
        internal static void Serve(long sender, ZPackage request) {
            byte mode;
            string account;
            string character;
            string fromRaw;
            string toRaw;
            try {
                mode = request.ReadByte();
                account = request.ReadString();
                character = request.ReadString();
                fromRaw = request.ReadString();
                toRaw = request.ReadString();
            } catch (Exception e) {
                Logger.LogWarning($"Dropping a malformed audit request from {sender}: {e.Message}");
                return;
            }

            if (!AuditPolicy.Active()) {
                Reply(sender, mode, false, "The audit log is switched off on this server (EnableAuditLog).", null);
                return;
            }

            // Both reach Path.Combine on this side and on the requester's, so they are checked before either
            // is used - the same rule the character store follows for every client-supplied path segment.
            if (!PeerIdentity.IsSafeToken(account) || !PeerIdentity.IsSafeToken(character)) {
                Logger.LogWarning($"Refusing an audit request from {sender}: unsafe account id or character name.");
                Reply(sender, mode, false, "That account id or character name is not one this server will look up.", null);
                return;
            }

            if (mode == ModeList) {
                ServeList(sender, account, character);
                return;
            }
            if (mode != ModeDownload) {
                Logger.LogWarning($"Refusing an audit request from {sender}: unknown mode {mode}.");
                return;
            }

            if (!TryDay(fromRaw, out DateTime from) || !TryDay(toRaw, out DateTime to)) {
                Reply(sender, mode, false, "A download needs two dates in yyyy-MM-dd form.", null);
                return;
            }
            if (to < from) { DateTime swap = from; from = to; to = swap; }

            // Clamped rather than refused: an admin asking for more than is kept should get what there is,
            // not an error telling them to work out the retention window themselves.
            int maxDays = ValConfig.AuditMaxDownloadDays != null ? Math.Max(1, ValConfig.AuditMaxDownloadDays.Value) : 7;
            int retention = ValConfig.AuditRetentionDays != null ? Math.Max(1, ValConfig.AuditRetentionDays.Value) : 7;
            DateTime floor = DateTime.UtcNow.Date.AddDays(-(Math.Min(maxDays, retention) - 1));
            DateTime clampedFrom = from < floor ? floor : from;
            DateTime clampedTo = to > DateTime.UtcNow.Date ? DateTime.UtcNow.Date : to;
            if (clampedTo < clampedFrom) { clampedTo = clampedFrom; }

            ServeDownload(sender, account, character, clampedFrom, clampedTo,
                          clamped: clampedFrom != from || clampedTo != to);
        }

        private static void ServeList(long sender, string account, string character) {
            // Reading and filtering every day file is disk work in the megabytes. It goes to the audit
            // thread, which also owns these files, so a listing cannot race a flush half way through one.
            AuditLog.SubmitJob(() => {
                List<string> rows = new List<string>();
                long totalEvents = 0;
                try {
                    List<AuditLog.DayInfo> days = AuditLog.AvailableDays();
                    foreach (AuditLog.DayInfo day in days) {
                        if (rows.Count >= MaxListRows) { break; }
                        List<AuditEvent> events = AuditLog.Query(account, character, day.Day, day.Day.AddDays(1).AddTicks(-1),
                                                                 null, 0, out bool _);
                        totalEvents += events.Count;
                        rows.Add($"{Day(day.Day)}|{events.Count}|{day.Bytes}");
                    }
                } catch (Exception e) {
                    Logger.LogWarning($"Could not build an audit listing for {character}: {e.Message}");
                }

                long events2 = totalEvents;
                AuditLog.QueueMainThread(() => {
                    ZPackage payload = new ZPackage();
                    payload.Write(rows.Count);
                    foreach (string row in rows) { payload.Write(row); }
                    Reply(sender, ModeList, true,
                          $"{events2} recorded event(s) for {character} across {rows.Count} day(s), keeping {ValConfig.AuditRetentionDays.Value} day(s).",
                          payload);
                });
            });
        }

        private static void ServeDownload(long sender, string account, string character,
                                          DateTime from, DateTime to, bool clamped) {
            AuditLog.SubmitJob(() => {
                byte[] compressed = null;
                int count = 0;
                string problem = null;
                try {
                    List<AuditEvent> events = AuditLog.Query(account, character, from, to.AddDays(1).AddTicks(-1),
                                                             null, MaxDownloadEvents, out bool capped);
                    count = events.Count;
                    if (count == 0) {
                        problem = $"Nothing recorded for {character} between {Day(from)} and {Day(to)}.";
                    } else {
                        StringBuilder text = new StringBuilder();
                        foreach (string line in AuditLog.ToLines(events)) { text.AppendLine(line); }
                        compressed = Gzip.CompressText(text.ToString());
                        if (capped) {
                            Logger.LogWarning($"An audit download for {character} hit the {MaxDownloadEvents} event ceiling; the oldest events in the range are not included.");
                        }
                    }
                } catch (Exception e) {
                    problem = "The server could not read that history; see the server log.";
                    Logger.LogWarning($"Could not build an audit download for {character}: {e}");
                }

                byte[] finished = compressed;
                int finishedCount = count;
                string failure = problem;
                AuditLog.QueueMainThread(() => {
                    if (finished == null) {
                        Reply(sender, ModeDownload, false, failure ?? "Nothing to send.", null);
                        return;
                    }
                    int limit = ValConfig.MaxAuditDownloadBytes;
                    if (finished.Length > limit) {
                        Reply(sender, ModeDownload, false,
                              $"That range compresses to {finished.Length} bytes, over the {limit} byte transfer limit. Ask for fewer days.", null);
                        return;
                    }
                    ZPackage payload = new ZPackage();
                    // The reply carries who it is about. The client validates these before they reach a path
                    // rather than trusting them - but carrying them beats the client remembering what it
                    // asked for, which mislabels the file if two downloads are ever in flight at once.
                    payload.Write(account);
                    payload.Write(character);
                    payload.Write(Day(from));
                    payload.Write(Day(to));
                    payload.Write(finishedCount);
                    payload.Write(finished);
                    Reply(sender, ModeDownload, true,
                          $"{finishedCount} event(s) for {character} from {Day(from)} to {Day(to)}"
                          + (clamped ? " (the range was clamped to what this server keeps)" : "") + ".",
                          payload);
                });
            });
        }

        private static void Reply(long sender, byte mode, bool ok, string message, ZPackage payload) {
            try {
                ZPackage response = new ZPackage();
                response.Write(mode);
                response.Write(ok);
                response.Write(message ?? "");
                response.Write(payload != null ? payload.GetArray() : new byte[0]);
                ValConfig.AuditDataRPC.SendPackage(sender, response);
            } catch (Exception e) {
                Logger.LogWarning($"Could not send an audit reply to {sender}: {e.Message}");
            }
        }

        // ---- Receiving (client) ---------------------------------------------------------------------------

        /// <summary>
        /// Handles a reply on the admin's own machine. The caller has already established it came from the
        /// server and is within its size limit.
        /// </summary>
        internal static void Receive(ZPackage response) {
            byte mode;
            bool ok;
            string message;
            byte[] payloadBytes;
            try {
                mode = response.ReadByte();
                ok = response.ReadBool();
                message = response.ReadString();
                payloadBytes = response.ReadByteArray();
            } catch (Exception e) {
                Logger.LogWarning($"Could not read an audit reply from the server: {e.Message}");
                return;
            }

            if (!ok) {
                TerminalManager.PrintResponse(OutputLevel.Error, message);
                return;
            }

            ZPackage payload = new ZPackage(payloadBytes);
            if (mode == ModeList) {
                PrintListing(payload, message);
                return;
            }
            if (mode == ModeDownload) {
                SaveDownload(payload, message);
                return;
            }
            Logger.LogWarning($"Ignoring an audit reply with unknown mode {mode}.");
        }

        private static void PrintListing(ZPackage payload, string message) {
            try {
                int rows = payload.ReadInt();
                if (rows <= 0) {
                    TerminalManager.PrintResponse(OutputLevel.Info, "The server holds no audit history for that character.");
                    return;
                }
                TerminalManager.PrintResponse(OutputLevel.Info, "Day          Events    Day file size");
                for (int i = 0; i < rows; i++) {
                    string[] parts = payload.ReadString().Split('|');
                    if (parts.Length != 3) { continue; }
                    TerminalManager.PrintResponse(OutputLevel.Detail,
                        $"  {parts[0]}   {parts[1],7}    {Bytes(parts[2])}");
                }
                TerminalManager.PrintResponse(OutputLevel.Info, message);
            } catch (Exception e) {
                TerminalManager.PrintResponse(OutputLevel.Error, $"Could not read the listing the server sent: {e.Message}");
            }
        }

        private static void SaveDownload(ZPackage payload, string message) {
            try {
                string account = payload.ReadString();
                string character = payload.ReadString();
                string from = payload.ReadString();
                string to = payload.ReadString();
                int count = payload.ReadInt();
                byte[] compressed = payload.ReadByteArray();

                // Not a security check - WriteLocal re-validates every segment before it touches a path -
                // but an admin needs to be told if what came back is not what they asked for.
                if (!string.IsNullOrEmpty(LastRequestCharacter)
                    && !string.Equals(character, LastRequestCharacter, StringComparison.OrdinalIgnoreCase)) {
                    TerminalManager.PrintResponse(OutputLevel.Warning,
                        $"The server answered about '{character}', not the '{LastRequestCharacter}' this machine asked for. Saving it under the name the server gave.");
                }

                string text = Gzip.DecompressText(compressed, ValConfig.MaxAuditDownloadBytes);
                if (text == null) {
                    TerminalManager.PrintResponse(OutputLevel.Error,
                        "The server's reply expanded past the size limit and was discarded. Ask for fewer days.");
                    return;
                }

                string path = WriteLocal(account, character, from, to, text);
                if (path == null) {
                    TerminalManager.PrintResponse(OutputLevel.Error, "Could not write the downloaded history; see the log.");
                    return;
                }
                TerminalManager.PrintResponse(OutputLevel.Info, message);
                TerminalManager.PrintResponse(OutputLevel.Info, $"Saved {count} event(s) to {path}");
            } catch (Exception e) {
                TerminalManager.PrintResponse(OutputLevel.Error, $"Could not save the downloaded history: {e.Message}");
            }
        }

        /// <summary>
        /// Who this machine last asked about, used only to notice when a reply is about somebody else.
        ///
        /// The filename comes from the reply rather than from here, because two downloads in flight would
        /// make this the wrong answer. That is safe because every segment is re-validated with
        /// <see cref="PeerIdentity.IsSafeToken"/> before it reaches a path - the server can pick a name, but
        /// only ever a harmless one inside our own folder.
        /// </summary>
        internal static string LastRequestCharacter { get; private set; }

        internal static void RememberRequest(string account, string character) {
            LastRequestCharacter = character;
        }

        /// <summary>
        /// Writes a downloaded history under BepInEx/config/ValheimEnforcer/AuditDownloads. Returns the full
        /// path, or null when it could not be written.
        ///
        /// The received-time suffix means a second download of the same range never silently overwrites the
        /// first - an admin comparing two pulls has to be able to keep both.
        /// </summary>
        internal static string WriteLocal(string account, string character, string from, string to, string text) {
            try {
                if (!PeerIdentity.IsSafeToken(account) || !PeerIdentity.IsSafeToken(character)) {
                    Logger.LogWarning("Refusing to save a downloaded history under an unsafe account id or character name.");
                    return null;
                }
                string folder = Path.Combine(Paths.ConfigPath, ValConfig.ValheimEnforcer, DownloadFolder, account);
                Directory.CreateDirectory(folder);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(folder, $"{character}-{from}_to_{to}-{stamp}.yaml");
                File.WriteAllText(path, text, Encoding.UTF8);
                return path;
            } catch (Exception e) {
                Logger.LogWarning($"Could not write a downloaded audit history: {e.Message}");
                return null;
            }
        }

        // ---- Helpers --------------------------------------------------------------------------------------

        internal static string Day(DateTime day) {
            return day.ToString(AuditLog.DayFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parses a date the strict way. Nothing else is accepted - no relative forms, no partial dates, and
        /// certainly nothing that could survive being pasted into a filename.
        /// </summary>
        internal static bool TryDay(string raw, out DateTime day) {
            day = DateTime.MinValue;
            if (string.IsNullOrEmpty(raw) || raw.Length != AuditLog.DayFormat.Length) { return false; }
            foreach (char c in raw) {
                if (c != '-' && (c < '0' || c > '9')) { return false; }
            }
            return DateTime.TryParseExact(raw, AuditLog.DayFormat, CultureInfo.InvariantCulture,
                                          DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out day);
        }

        private static string Bytes(string raw) {
            if (!long.TryParse(raw, out long bytes)) { return raw; }
            if (bytes < 1024) { return $"{bytes} B"; }
            if (bytes < 1024 * 1024) { return $"{bytes / 1024.0:F1} KB"; }
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
    }
}
