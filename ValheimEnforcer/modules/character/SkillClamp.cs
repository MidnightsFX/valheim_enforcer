using System.Collections.Generic;
using System.Linq;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// Bounds the skill levels a client reports to values the game itself can produce.
    ///
    /// A character's skills arrive from the client - in a delta's full SkillLevels map and in every full save -
    /// so a modified client can put any number in the field, including ones the game can never reach (above
    /// 100, negative, NaN, Infinity). Those are the values that are actually dangerous: written into the
    /// authoritative save they desync clients and can break skill math. This clamps every reported level into
    /// the valid [0, 100] range.
    ///
    /// It deliberately does NOT try to police the *rate* of a legitimate-looking gain. A per-window "no more
    /// than N levels per update" bound was considered and rejected: it makes the stored skill lag the player's
    /// real skill, which PreventExternalSkillRaises then rolls back on the next join - so it would strip skill
    /// a player legitimately earned in a burst - and it only delays a fabrication rather than preventing one.
    /// The real server-side skill enforcement for a returning character is
    /// <see cref="ReturningCharacterRules"/>, which clamps to the stored level against an authoritative
    /// baseline and has none of those problems.
    ///
    /// Pure data, so it is safe on the CharacterStore worker thread.
    /// </summary>
    internal static class SkillClamp {

        /// <summary>Valheim's maximum skill level.</summary>
        internal const float MaxSkillLevel = 100f;

        /// <summary>
        /// Clamps every level in <paramref name="incoming"/> into [0, 100] in place. A value outside that range
        /// (including NaN/Infinity from a crafted payload) is corrected and logged; a normal value is untouched.
        /// </summary>
        internal static void Apply(Dictionary<Skills.SkillType, float> incoming, string who) {
            if (incoming == null || incoming.Count == 0) { return; }

            foreach (Skills.SkillType skill in incoming.Keys.ToList()) {
                float reported = incoming[skill];
                if (reported >= 0f && reported <= MaxSkillLevel) { continue; } // in range (also rejects NaN, which fails both comparisons)

                float clamped;
                if (float.IsNaN(reported)) { clamped = 0f; }
                else if (reported > MaxSkillLevel) { clamped = MaxSkillLevel; }
                else { clamped = 0f; } // negative or -Infinity

                Logger.LogWarning($"Clamping {who}'s reported {skill} from {reported} to {clamped} (outside the valid 0-100 range).");
                incoming[skill] = clamped;
            }
        }
    }
}
