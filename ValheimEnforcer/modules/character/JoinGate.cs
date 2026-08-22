using System.Collections;
using UnityEngine;
using ValheimEnforcer.common;

namespace ValheimEnforcer.modules.character {

    /// <summary>
    /// Holds join validation back until the server has said whether it stores a character for this player.
    ///
    /// The server's answer rides Jotunn's initial synchronization, which is sent during the connection
    /// handshake and normally lands long before the world finishes loading - the wait usually completes on its
    /// first frame. But it is a paced, fragmented coroutine send, not something the client blocks on, and a
    /// large character is spread across several frames. Losing that race matters: with no answer the join path
    /// treats the character as new (see CharacterManager.ResolveSessionCharacter, which will not fall back to
    /// this machine's local save), and doing that to a returning player would confiscate their inventory.
    ///
    /// Waiting is safe. Throughout it CharacterManager.PlayerCharacter is null, so DeltaChangeTracker.Update
    /// bails out and uploads nothing, and SavePlayerCharacter refuses to invent a baseline. The wait is bounded
    /// by InitialCharacterSyncWaitSeconds and on timeout the join proceeds anyway, still treating the character
    /// as new - the deferral buys certainty when it can and never blocks the player from playing.
    /// </summary>
    internal static class JoinGate {

        private static GameObject host;

        internal static void BeginDeferredJoinValidation(Player player) {
            if (player == null) { return; }
            CharacterManager.JoinValidationPending = true;
            Behaviour().StartCoroutine(WaitThenValidate(CharacterManager.SessionGeneration));
        }

        private static JoinGateBehaviour Behaviour() {
            if (host == null) {
                host = new GameObject("VE_JoinGate");
                Object.DontDestroyOnLoad(host);
                host.hideFlags = HideFlags.HideAndDontSave;
            }
            return host.GetComponent<JoinGateBehaviour>() ?? host.AddComponent<JoinGateBehaviour>();
        }

        // Deliberately resolves Player.m_localPlayer at the end rather than capturing the instance that started
        // the wait. Vanilla re-instantiates the Player prefab freely - SkipIntro alone produces a second spawn
        // within moments - and those later spawns see JoinValidationPending and schedule nothing of their own.
        // Validating whoever is actually here when the wait ends is what keeps a respawn during the wait from
        // leaving the session with no join validation at all.
        private static IEnumerator WaitThenValidate(int generation) {
            float deadline = Time.unscaledTime + Mathf.Max(0, ValConfig.InitialCharacterSyncWaitSeconds.Value);
            Logger.LogInfo("Waiting for the server's stored character before validating this join.");

            while (Time.unscaledTime < deadline
                   && CharacterManager.SessionGeneration == generation
                   && CharacterManager.ServerCharacter == CharacterManager.ServerCharacterState.Unknown
                   && Player.m_localPlayer != null) {
                yield return null;
            }

            // The session this wait belongs to has ended. Touching JoinValidationPending now would clear a
            // flag that belongs to a newer session.
            if (CharacterManager.SessionGeneration != generation) {
                Logger.LogDebug("Deferred join validation abandoned: its session ended while it was waiting.");
                yield break;
            }

            CharacterManager.JoinValidationPending = false;

            if (Player.m_localPlayer == null) {
                Logger.LogWarning("Deferred join validation abandoned: the player left before the server answered.");
                yield break;
            }

            // A spawn during the wait can have run the join pipeline already.
            if (CharacterManager.JoinValidationComplete) {
                Logger.LogDebug("Deferred join validation skipped: this session was already validated.");
                yield break;
            }

            if (CharacterManager.ServerCharacter == CharacterManager.ServerCharacterState.Unknown) {
                Logger.LogWarning($"No answer from the server about this character within {ValConfig.InitialCharacterSyncWaitSeconds.Value}s. Continuing, and treating the character as new.");
            }

            CharacterManager.LoadAndValidatePlayer(Player.m_localPlayer);
            CharacterDeltaTracker.WatchInventory(Player.m_localPlayer);
        }
    }

    // Nothing but a coroutine host; the logic lives in JoinGate so it is testable as plain static code.
    internal class JoinGateBehaviour : MonoBehaviour { }
}
