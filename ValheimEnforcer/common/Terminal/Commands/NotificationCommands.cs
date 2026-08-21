using System;
using System.Collections.Generic;
using System.Linq;
using ValheimEnforcer.modules.notifications;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        /// <summary>
        /// The last time a test notification was accepted, so a held-down key cannot walk the webhook into
        /// Discord's rate limiter. Getting a webhook temporarily throttled would silence the real
        /// notifications too, which is a bad trade for a preview command.
        /// </summary>
        private static DateTime lastTestNotification = DateTime.MinValue;
        private static readonly TimeSpan TestNotificationCooldown = TimeSpan.FromSeconds(3);

        private static void RegisterNotificationCommands() {
            _ = new EnforcerCommand("enforcer-notify-test",
                "Format: <event>|list Posts one Discord notification using sample data, to preview a template from Notifications.yaml. Ignores the Notify* switches but still needs a webhook URL. eg: enforcer-notify-test PlayerJoined",
                NotifyTest, CommandArea.Notifications, NotificationEvents,
                serverAuthoritative: true, requiresAdmin: true,
                aliases: "Enforcer-Test-Notification");
        }

        private static List<string> NotificationEvents(string[] input) {
            if (input.Length > 2) { return new List<string>(); }
            List<string> options = TerminalArgs.Names<NotificationEvent>();
            options.Insert(0, "list");
            return options;
        }

        private static void NotifyTest(EnforcerCommandArgs args) {
            string requested = args.Args.GetString(0, null);
            string known = string.Join(", ", Enum.GetNames(typeof(NotificationEvent)));

            if (string.IsNullOrEmpty(requested)) {
                args.Output.Error($"An event is required. One of: {known}");
                return;
            }
            if (string.Equals(requested, "list", StringComparison.OrdinalIgnoreCase)) {
                foreach (string name in Enum.GetNames(typeof(NotificationEvent))) {
                    args.Output.Detail($"  {name} -> {NotificationTemplates.CategoryOf((NotificationEvent)Enum.Parse(typeof(NotificationEvent), name))} webhook", log: false);
                }
                args.Output.Info($"{Enum.GetNames(typeof(NotificationEvent)).Length} notification event(s).", log: false);
                return;
            }

            // IsDefined as well as TryParse: TryParse happily accepts a bare number for any enum, so "99"
            // would otherwise come through as a NotificationEvent nothing can render.
            if (!Enum.TryParse(requested, true, out NotificationEvent evt) || !Enum.IsDefined(typeof(NotificationEvent), evt)) {
                args.Output.Error($"Unknown event '{requested}'. One of: {known}");
                return;
            }

            NotificationCategory category = NotificationTemplates.CategoryOf(evt);
            if (!DiscordNotifier.IsValidWebhookUrl(DiscordNotifier.ResolveUrl(category))) {
                args.Output.Error($"No usable webhook URL for the {category} category. Set Discord.WebhookUrl on the server, or the URL for that category.");
                return;
            }
            if (DateTime.UtcNow - lastTestNotification < TestNotificationCooldown) {
                args.Output.Warning("A test notification was just sent - wait a moment before sending another.");
                return;
            }

            lastTestNotification = DateTime.UtcNow;
            // The same bag the load-time validity check renders with, so what this previews is exactly what
            // that check accepted.
            DiscordNotifier.Notify(evt, NotificationTemplates.SampleTokens());
            args.Output.Info($"Posted a sample {evt} notification to the {category} webhook. Discord takes a moment; check the channel.");
        }
    }
}
