using System;
using System.IO;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.bannetwork {

    /// <summary>
    /// What this server currently makes of its ban network registration.
    ///
    /// Terminal states are the point of this enum. A key that was rejected or revoked will still be rejected
    /// on the next attempt and the one after, so the scheduler stops calling entirely rather than retrying a
    /// dead key against a shared endpoint forever. Pending is the one failure that resolves on its own -
    /// somebody approves the registration - so it is the one that keeps probing, slowly.
    /// </summary>
    internal enum KeyState {
        Off,
        NoKey,
        Malformed,
        Pending,
        Rejected,
        Revoked,
        Suspended,
        Ok,
        Error,
    }

    internal static class KeyStates {

        internal static bool Terminal(KeyState state) {
            return state == KeyState.Rejected || state == KeyState.Revoked || state == KeyState.Suspended;
        }

        internal static string Describe(KeyState state, string note) {
            string suffix = string.IsNullOrEmpty(note) ? "" : $" - {note}";
            switch (state) {
                case KeyState.Off:       return "off (EnableBanNetwork is false)";
                case KeyState.NoKey:     return $"no key (put one in BanNetwork/{BanApiKey.FileName})";
                case KeyState.Malformed: return $"the key in BanNetwork/{BanApiKey.FileName} is not a valid key";
                case KeyState.Pending:   return $"registered, awaiting approval{suffix}";
                case KeyState.Rejected:  return $"rejected - the network does not recognise this key{suffix}";
                case KeyState.Revoked:   return $"revoked{suffix}";
                case KeyState.Suspended: return $"suspended{suffix}";
                case KeyState.Ok:        return "connected";
                default:                 return $"cannot reach the ban network{suffix}";
            }
        }
    }

    /// <summary>
    /// Owns BanNetwork/state.yaml: the pull cursor and everything else the scheduler needs to resume.
    ///
    /// Machine-owned and not watched - an admin editing this would be editing a bookmark, and the commands
    /// that exist for the two things they might actually want (force a sync, drop the cache) do the job
    /// without a text editor.
    /// </summary>
    internal static class BanNetworkState {

        internal const string FileName = "state.yaml";

        internal static string FilePath {
            get { return Path.Combine(BanOverrides.FolderPath, FileName); }
        }

        private static readonly object Gate = new object();

        internal static long Cursor;
        internal static DateTime? LastPullUtc;
        internal static DateTime? LastPushUtc;
        internal static KeyState Key = KeyState.Off;
        internal static string KeyNote;
        internal static int ConsecutiveFailures;
        internal static DateTime? NextAttemptUtc;
        internal static string ServerId;
        internal static string LastError;

        internal static void Load() {
            string path = FilePath;
            if (!File.Exists(path)) { return; }
            try {
                DataObjects.BanNetworkStateFile file =
                    DataObjects.yamldeserializer.Deserialize<DataObjects.BanNetworkStateFile>(File.ReadAllText(path));
                if (file == null) { return; }
                lock (Gate) {
                    Cursor = file.Cursor;
                    LastPullUtc = BanTime.Parse(file.LastPullUtc);
                    LastPushUtc = BanTime.Parse(file.LastPushUtc);
                    ConsecutiveFailures = file.ConsecutiveFailures;
                    NextAttemptUtc = BanTime.Parse(file.NextAttemptUtc);
                    ServerId = file.ServerId;
                    LastError = file.LastError;
                    // A terminal verdict is restored so a dead key does not get a fresh run of attempts on
                    // every restart. Anything else starts clean - a transient failure deserves a retry.
                    if (Enum.TryParse(file.KeyState, out KeyState restored) && KeyStates.Terminal(restored)) {
                        Key = restored;
                        KeyNote = file.KeyStateNote;
                    }
                }
            } catch (Exception e) {
                Logger.LogWarning($"Ban network: could not read {FileName} ({e.Message}); starting from a clean cursor.");
            }
        }

        internal static void Save() {
            try {
                DataObjects.BanNetworkStateFile file;
                lock (Gate) {
                    file = new DataObjects.BanNetworkStateFile {
                        Version = 1,
                        Cursor = Cursor,
                        LastPullUtc = LastPullUtc.HasValue ? BanTime.Stamp(LastPullUtc.Value) : null,
                        LastPushUtc = LastPushUtc.HasValue ? BanTime.Stamp(LastPushUtc.Value) : null,
                        KeyState = Key.ToString(),
                        KeyStateNote = KeyNote,
                        ConsecutiveFailures = ConsecutiveFailures,
                        NextAttemptUtc = NextAttemptUtc.HasValue ? BanTime.Stamp(NextAttemptUtc.Value) : null,
                        ServerId = ServerId,
                        LastError = LastError,
                    };
                }
                AtomicFile.WriteYaml(FilePath, file, DataObjects.yamlserializer);
            } catch (Exception e) {
                Logger.LogWarning($"Ban network: could not write {FileName}: {e.Message}");
            }
        }

        /// <summary>
        /// Records the outcome of an attempt and works out when to try again.
        ///
        /// The backoff is persisted rather than reset on restart, deliberately unlike ThunderstoreResolver -
        /// which treats a restart as a legitimate "try again now" because it talks to a CDN that does not
        /// care. Here a crash-looping server would hammer a shared endpoint once per boot, so the wait
        /// survives. It is capped on the way back in so a long outage cannot leave a server stuck.
        /// </summary>
        internal static void NoteFailure(KeyState state, string note, string error) {
            lock (Gate) {
                Key = state;
                KeyNote = note;
                LastError = error;
                if (KeyStates.Terminal(state)) {
                    ConsecutiveFailures = 0;
                    NextAttemptUtc = null;
                } else {
                    ConsecutiveFailures++;
                    NextAttemptUtc = DateTime.UtcNow.AddMinutes(BackoffMinutes(ConsecutiveFailures));
                }
            }
            Save();
            BanNotifications.StateChanged(state, note, error);
        }

        internal static void NoteSuccess() {
            lock (Gate) {
                Key = KeyState.Ok;
                KeyNote = null;
                LastError = null;
                ConsecutiveFailures = 0;
                NextAttemptUtc = null;
            }
            BanNotifications.StateChanged(KeyState.Ok, null, null);
        }

        /// <summary>1, 2, 5, 15, 30, then 60 minutes. Capped so a long outage cannot strand a server.</summary>
        internal static int BackoffMinutes(int failures) {
            switch (failures) {
                case 0:
                case 1: return 1;
                case 2: return 2;
                case 3: return 5;
                case 4: return 15;
                case 5: return 30;
                default: return 60;
            }
        }

        /// <summary>True when a terminal state or an unexpired backoff says not to call right now.</summary>
        internal static bool Blocked(out string why) {
            lock (Gate) {
                if (KeyStates.Terminal(Key)) {
                    why = KeyStates.Describe(Key, KeyNote);
                    return true;
                }
                if (NextAttemptUtc.HasValue && NextAttemptUtc.Value > DateTime.UtcNow) {
                    why = $"backing off until {BanTime.Stamp(NextAttemptUtc.Value)}";
                    return true;
                }
            }
            why = null;
            return false;
        }

        /// <summary>
        /// Clears whatever is currently stopping this server from calling - a terminal verdict, an unexpired
        /// backoff, or both - so the next attempt happens immediately.
        ///
        /// Called when api.key changes on disk and by enforcer-ban-network-sync. Both are an admin saying
        /// "I have fixed it, try again", and the backoff has to go as well as the terminal state: an admin
        /// who pastes a working key and is told to wait fifteen minutes will reasonably conclude it did not
        /// work, and paste it again.
        /// </summary>
        internal static void Retry() {
            lock (Gate) {
                if (KeyStates.Terminal(Key)) {
                    Key = KeyState.Off;
                    KeyNote = null;
                }
                ConsecutiveFailures = 0;
                NextAttemptUtc = null;
            }
            Save();
        }

        internal static void ResetCursor() {
            lock (Gate) { Cursor = 0; }
            Save();
        }
    }
}
