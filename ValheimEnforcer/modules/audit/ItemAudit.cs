using System;
using ValheimEnforcer.common;
using ValheimEnforcer.modules.character;
using Logger = ValheimEnforcer.Logger;
using static ValheimEnforcer.common.DataObjects;

namespace ValheimEnforcer.modules.audit {

    /// <summary>
    /// Records what a player gained and lost, off the character delta stream the mod already receives.
    ///
    /// This is the one signal that costs nothing new: <see cref="CharacterDeltaTracker"/> watches the local
    /// inventory client-side and sends the server a list of added and removed items, and the server already
    /// binds that payload to the connection it arrived on before doing anything with it. All that is added
    /// here is writing it down.
    ///
    /// What that means for the record, and what the config description says plainly:
    ///   - the client coalesces changes over a two second settle and is rate limited on top of that, so a
    ///     timestamp is when the server heard about it, not when it happened;
    ///   - it is a net diff against the client's own baseline, so picking something up and dropping it again
    ///     inside one window produces nothing at all;
    ///   - it is client-reported. A modified client that stops sending deltas stops producing item events -
    ///     but the container half of the audit is server-observed and does not depend on the client at all,
    ///     which is what makes the two worth having together.
    /// </summary>
    internal static class ItemAudit {

        /// <summary>
        /// Writes down one delta payload.
        ///
        /// Called from the delta handler after its identity binding and before the merge, so an event names
        /// the character that connection is really playing and is recorded whether or not the save that
        /// follows succeeds.
        /// </summary>
        internal static void Record(long sender, DeltaSummaryUpdate delta) {
            try {
                if (!AuditPolicy.Active()) { return; }
                if (delta == null || delta.ItemModifications == null || delta.ItemModifications.Count == 0) { return; }

                ZNetPeer peer = ZNet.instance?.GetPeer(sender);
                if (peer != null && !AuditPolicy.Recorded(peer)) { return; }

                // File under the socket's host name where we have one, so item events sit under the same
                // account spelling as the container and damage events. The payload id is the fallback for a
                // peer that has already gone; PlatformIds.Matches makes a query find either.
                string account = AuditPolicy.AccountOf(peer);
                if (string.IsNullOrEmpty(account)) { account = delta.HostID; }
                string character = delta.Name;
                if (string.IsNullOrEmpty(account)) { return; }

                foreach (ItemDelta change in delta.ItemModifications) {
                    if (change?.Item == null) { continue; }
                    AuditEvent entry = AuditPolicy.Event(account, character,
                        change.Op == ItemDeltaChangeType.Added ? AuditEvent.Kinds.ItemGained : AuditEvent.Kinds.ItemLost);
                    entry.Prefab = change.Item.prefabName;
                    entry.Qty = change.Item.m_stack;
                    entry.Quality = change.Item.m_quality;
                    // Only on a gain: it is the field that separates a crafted item from a conjured one, and
                    // it is the first thing an admin looks at. Meaningless on a loss.
                    if (change.Op == ItemDeltaChangeType.Added) {
                        entry.CrafterId = change.Item.m_crafterID;
                        entry.Crafter = change.Item.m_crafterName;
                    }
                    AuditPolicy.Record(entry);
                }
            } catch (Exception e) {
                // The delta handler must not fail over a recording problem; the save matters more.
                Logger.LogDebug($"Item audit could not record a delta: {e.Message}");
            }
        }
    }
}
