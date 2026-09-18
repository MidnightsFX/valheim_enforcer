using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ValheimEnforcer.common {

    internal static class ConfigFileWatcher {

        private class WatchEntry {
            public DateTime LastWriteUTC { get; set; }
            public long FileLength { get; set; }

            /// <summary>
            /// Every owner that asked to hear about this file, in registration order.
            ///
            /// A list rather than one delegate because a file can legitimately have two owners: the generic
            /// config dispatch in <see cref="ValConfig"/> watches every yaml file it creates, and a module
            /// that owns one of those files may want its own reload as well.
            /// </summary>
            public readonly List<Action<string>> Callbacks = new List<Action<string>>();

            public void Update(DateTime lastwrite, long len) {
                LastWriteUTC = lastwrite;
                FileLength = len;
            }
        }

        private static Dictionary<string, WatchEntry> WatchedFiles = new Dictionary<string, WatchEntry>();
        private static ConfigFileWatcherBehaviour watchProcess;

        internal static void Initialize() {
            if (watchProcess != null) return;
            GameObject go = new GameObject("VE_ConfigFileWatcher");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            watchProcess = go.AddComponent<ConfigFileWatcherBehaviour>();
            Logger.LogDebug("ConfigFileWatcher initialized.");
        }

        /// <summary>
        /// Starts watching a file, or adds another callback to one already watched.
        ///
        /// Registering the same path twice used to be Dictionary.Add, which threw - and every Register call in
        /// the mod runs from the ValConfig constructor, which runs from Awake, so one duplicated path did not
        /// just lose a callback. It left Awake at "cfg = new ValConfig(Config)" and took the whole of the rest
        /// of it: the asset bundle, the SetModsActive hooks, TerminalManager (every enforcer command),
        /// CheatDetector, CharacterDeltaTracker - and Harmony.CreateAndPatchAll, so not one patch was applied.
        /// The plugin loaded, logged one line about a dictionary key, and enforced nothing. Loadouts.yaml was
        /// registered twice exactly this way. A second owner is a normal thing to want, and is now just added.
        /// </summary>
        internal static void Register(string fullPath, Action<string> onChanged) {
            if (string.IsNullOrEmpty(fullPath) || onChanged == null) { return; }

            if (WatchedFiles.TryGetValue(fullPath, out WatchEntry existing)) {
                // Delegate equality compares target and method, so re-running the same registration - a
                // reloaded module calling Initialize twice - does not stack a second identical callback.
                if (existing.Callbacks.Contains(onChanged)) {
                    Logger.LogDebug($"ConfigFileWatcher already watching {fullPath} with this callback.");
                    return;
                }
                existing.Callbacks.Add(onChanged);
                Logger.LogDebug($"ConfigFileWatcher added another callback for {fullPath} ({existing.Callbacks.Count} total).");
                return;
            }

            WatchEntry entry = new WatchEntry() { LastWriteUTC = DateTime.MinValue, FileLength = 0 };
            if (File.Exists(fullPath)) {
                FileInfo info = new FileInfo(fullPath);
                entry.Update(info.LastWriteTimeUtc, info.Length);
            }
            entry.Callbacks.Add(onChanged);
            WatchedFiles.Add(fullPath, entry);
            Logger.LogDebug($"ConfigFileWatcher watching {fullPath}");
        }

        /// <summary>
        /// Records a write we made ourselves so the next poll does not read it back as an external edit.
        ///
        /// Without this, every file the mod rewrites (Mods.yaml on startup, and again whenever resolved hashes
        /// land) bounces straight back in through the change callback one poll later. That is wasted work at
        /// best, and at worst it re-parses our own output as though an admin had typed it.
        /// Call immediately after the write completes.
        /// </summary>
        internal static void NoteSelfWrite(string fullPath) {
            if (!WatchedFiles.TryGetValue(fullPath, out WatchEntry entry)) { return; }
            try {
                FileInfo info = new FileInfo(fullPath);
                if (!info.Exists) { return; }
                entry.Update(info.LastWriteTimeUtc, info.Length);
            } catch (Exception e) {
                // Leaving the entry stale only costs one redundant reload, so this is never worth failing on.
                Logger.LogDebug($"Could not record our own write to {fullPath}: {e.Message}");
            }
        }



        internal class ConfigFileWatcherBehaviour : MonoBehaviour {
            private float nextPollTime;

            public void Update() {
                if (Time.unscaledTime < nextPollTime) { return; }

                nextPollTime = Time.unscaledTime + ValConfig.ConfigPollIntervalSeconds.Value;
                Poll();
            }

            private static void Poll() {
                if (WatchedFiles.Count == 0) { return; }

                // Snapshot: a callback is free to Register another file - a module reading one config that
                // names a second - and mutating the dictionary mid-foreach would throw out of Poll, past the
                // per-callback guard below, and then again on every frame after that.
                List<string> keys = new List<string>(WatchedFiles.Keys);

                foreach (string key in keys) {
                    if (File.Exists(key) == false) { continue; }

                    FileInfo info = new FileInfo(key);
                    DateTime mtime = info.LastWriteTimeUtc;
                    long size = info.Length;

                    WatchEntry we = WatchedFiles[key];

                    //Logger.LogDebug($"Comparing file details:\n lastwrite: {mtime} == {we.LastWriteUTC} ({mtime == we.LastWriteUTC})\n  size {size} == {we.FileLength} ({size == we.FileLength})");
                    if (mtime == we.LastWriteUTC && size == we.FileLength) { continue; }

                    we.Update(mtime, size);

                    // Guarded one at a time: with several owners on a file, the first one throwing must not
                    // cost the others their notification. ToArray so a callback may register its own.
                    foreach (Action<string> callback in we.Callbacks.ToArray()) {
                        try {
                            callback(key);
                        } catch (Exception e) {
                            Logger.LogWarning($"ConfigFileWatcher callback for {key} threw: {e.Message}");
                        }
                    }
                }
            }
        }
    }
}
