using Jotunn.Managers;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimEnforcer.modules.modsync {

    /// <summary>
    /// The mod sync's UI, built out of Jotunn's own connection error window so it looks like the rest of it.
    ///
    /// When that window opens after a refusal from a server that offers downloads, and there is something to
    /// fix, it gets a fourth button. That button opens a copy of the same window holding the plan, with Cancel
    /// and "Install and restart". The copy rather than a panel of our own: it is already styled, sized and
    /// scrollable, and the layout of the original is known (Jotunn's CompatibilityWindow prefab).
    ///
    /// Lives on GUIManager.CustomGUIFront in the main menu only, next to the details updater, and does nothing
    /// at all in a frame where no server has offered.
    /// </summary>
    internal class ModSyncWindow : MonoBehaviour {

        private const string JotunnWindowName = "CompatibilityWindow(Clone)";
        private const string FixButtonName = "VE Fix Mods Button";
        private const string ConfigFileName = "MidnightsFX.ValheimEnforcer.cfg";

        // The Jotunn window the button was added to, so each window gets exactly one.
        private GameObject handledWindow;
        private ModSyncPlan plan;

        private GameObject dialog;
        private Text body;
        private Button confirm;
        private Text confirmLabel;
        private Component dialogLayout;
        private string intro;
        private ModSync.Phase shownPhase;
        private string shownStatus;
        private bool layoutPending;
        private float restartAt = -1f;

        public void Update() {
            if (!ModSync.HasOffer && dialog == null) { return; }
            if (GUIManager.CustomGUIFront == null) { return; }

            if (ModSync.HasOffer) {
                Transform window = GUIManager.CustomGUIFront.transform.Find(JotunnWindowName);
                if (window != null && window.gameObject != handledWindow) {
                    handledWindow = window.gameObject;
                    plan = ModSync.BuildPlan();
                    if (plan != null && plan.HasWork) {
                        AddFixButton(window);
                    } else if (plan != null && plan.Unfixable.Count > 0) {
                        // Info, not Debug: with nothing to install there is no button, and this line is then the
                        // only place a player or admin can find out why.
                        Logger.LogInfo($"This server offers to fix mods, but none of the problems can be fixed automatically: {string.Join("; ", plan.Unfixable.ToArray())}");
                    }
                }
            }

            if (dialog != null) { Refresh(); }
        }

        /// <summary>
        /// Adds "Fix mods" beside the three buttons Jotunn's window has, spacing all four along the bottom. The
        /// new one is a copy of the Close button, with Jotunn's click handler cleared off it.
        /// </summary>
        private void AddFixButton(Transform window) {
            Transform close = window.Find("Close Button");
            Transform log = window.Find("Open Log Button");
            Transform help = window.Find("Troubleshooting Button");
            if (close == null) {
                Logger.LogDebug("Jotunn's connection error window has no Close Button; not adding the mod sync button.");
                return;
            }

            GameObject fix = Instantiate(close.gameObject, window);
            fix.name = FixButtonName;
            SetLabel(fix.transform, "Fix mods");
            Button button = fix.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => OpenDialog(window.gameObject));

            Place(close, -360f);
            Place(log, -120f);
            Place(help, 120f);
            Place(fix.transform, 360f);
        }

        private void OpenDialog(GameObject window) {
            if (dialog != null || plan == null) { return; }
            if (ModSync.CurrentPhase == ModSync.Phase.Failed) { ModSync.Cancel(); }

            dialog = Instantiate(window, window.transform.parent);
            dialog.name = "VE Mod Sync Window";
            Transform copiedFix = dialog.transform.Find(FixButtonName);
            if (copiedFix != null) { Destroy(copiedFix.gameObject); }

            Transform content = dialog.transform.Find("Scroll View/Viewport/Content");
            body = content?.Find("Failed Connection Text")?.GetComponent<Text>();
            foreach (string other in new[] { "Local Version Text", "Remote Version Text", "Error Messages Text" }) {
                Text text = content?.Find(other)?.GetComponent<Text>();
                if (text != null) { text.text = ""; }
            }
            dialogLayout = dialog.GetComponent("CompatibilityWindow");

            Transform cancel = dialog.transform.Find("Close Button");
            Transform log = dialog.transform.Find("Open Log Button");
            Transform go = dialog.transform.Find("Troubleshooting Button");
            if (body == null || cancel == null || go == null) {
                Logger.LogWarning("Mod sync: Jotunn's connection error window is not laid out as expected; cannot show the mod sync window.");
                CloseDialog();
                return;
            }
            if (log != null) { log.gameObject.SetActive(false); }

            Button cancelButton = cancel.GetComponent<Button>();
            cancelButton.onClick = new Button.ButtonClickedEvent();
            cancelButton.onClick.AddListener(CloseDialog);
            SetLabel(cancel, "Cancel");
            Place(cancel, -240f);

            confirm = go.GetComponent<Button>();
            confirm.onClick = new Button.ButtonClickedEvent();
            confirm.onClick.AddListener(Confirm);
            confirmLabel = go.GetComponentInChildren<Text>();
            Place(go, 240f);

            intro = Compose();
            shownPhase = (ModSync.Phase)(-1);
            shownStatus = null;
            restartAt = -1f;
            Refresh();
        }

        private string Compose() {
            string text = "<color=#FFA13C>This server can fix your mods</color>\n"
                + "Every package comes from thunderstore.io, at the exact version this server pinned, and is checked against the file hashes the server recorded before anything is installed.\n\n"
                + plan.Describe();

            if (!ModSync.CanInstallHere) {
                return text + "Installing mods for you is not available on this system. Use the list above to fix them by hand.";
            }
            if (!ModSync.PlayerAllowsInstalls) {
                return text + $"To let a server install mods for you, set AllowServerModInstalls = true under [Client config] in BepInEx/config/{ConfigFileName}, then restart Valheim and join again. Until then, use the list above to fix them by hand.";
            }
            string rejoin = ModSync.RejoinAddress != null
                ? $" and rejoins {ModSync.RejoinAddress}"
                : ". Join the server again from the server list";
            return text + $"When you continue, the packages are downloaded and checked first. Then Valheim closes, the changes are applied, and it starts again{rejoin}. Anything replaced is moved to BepInEx/cache/ValheimEnforcer/modsync/replaced.";
        }

        private bool CanConfirm() {
            return ModSync.CanInstallHere && ModSync.PlayerAllowsInstalls && plan != null && plan.HasWork;
        }

        private void Confirm() {
            if (!CanConfirm() || ModSync.CurrentPhase == ModSync.Phase.Working || ModSync.CurrentPhase == ModSync.Phase.Ready) { return; }
            ModSync.Start(plan);
            Refresh();
        }

        private void CloseDialog() {
            ModSync.Cancel();
            restartAt = -1f;
            if (dialog != null) { Destroy(dialog); }
            dialog = null;
            body = null;
            confirm = null;
            confirmLabel = null;
            dialogLayout = null;
        }

        /// <summary>Follows the worker: progress while it runs, the failure if it fails, and the restart once it is done.</summary>
        private void Refresh() {
            ModSync.Phase phase = ModSync.CurrentPhase;
            string status = ModSync.Status;

            if (phase != shownPhase || status != shownStatus) {
                shownPhase = phase;
                shownStatus = status;
                switch (phase) {
                    case ModSync.Phase.Working:
                        body.text = intro + $"\n\n<color=#FFA13C>{status}...</color>";
                        confirm.gameObject.SetActive(true);
                        confirm.interactable = false;
                        confirmLabel.text = "Working...";
                        break;
                    case ModSync.Phase.Ready:
                        body.text = intro + "\n\n<color=#FFA13C>Everything downloaded and matches the server's record. Valheim will now close and start again.</color>";
                        confirm.interactable = false;
                        confirmLabel.text = "Restarting...";
                        // A moment for the player to read it before the window goes.
                        restartAt = Time.realtimeSinceStartup + 2f;
                        break;
                    case ModSync.Phase.Failed:
                        body.text = intro + $"\n\n<color=#FF6B6B>Could not fix your mods: {ModSync.Failure}</color>\nNothing was changed.";
                        confirm.gameObject.SetActive(false);
                        break;
                    default:
                        body.text = intro;
                        confirm.gameObject.SetActive(CanConfirm());
                        confirm.interactable = true;
                        confirmLabel.text = "Install and restart";
                        break;
                }
                // Jotunn's window sizes its scroll content from the text, so it has to be told the text changed.
                // A frame later, once Unity has measured the new text.
                layoutPending = true;
                return;
            }

            if (layoutPending) {
                layoutPending = false;
                UpdateLayout();
            }

            if (restartAt > 0f && Time.realtimeSinceStartup >= restartAt) {
                restartAt = -1f;
                if (!ModSync.Restart(out string why)) {
                    body.text = intro + $"\n\n<color=#FF6B6B>Could not restart Valheim to apply the changes: {why}</color>\nNothing was changed.";
                    ModSync.Cancel();
                    confirm.gameObject.SetActive(false);
                    layoutPending = true;
                }
            }
        }

        private void UpdateLayout() {
            if (dialogLayout == null) { return; }
            try {
                MethodInfo update = dialogLayout.GetType().GetMethod("UpdateTextPositions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                update?.Invoke(dialogLayout, null);
            } catch (Exception e) {
                Logger.LogDebug($"Could not re-layout the mod sync window: {e.Message}");
            }
        }

        private static void SetLabel(Transform button, string label) {
            Text text = button.GetComponentInChildren<Text>();
            if (text != null) { text.text = label; }
        }

        private static void Place(Transform button, float x) {
            if (button == null) { return; }
            RectTransform rect = button as RectTransform;
            if (rect == null) { return; }
            rect.anchoredPosition = new Vector2(x, rect.anchoredPosition.y);
        }
    }
}
