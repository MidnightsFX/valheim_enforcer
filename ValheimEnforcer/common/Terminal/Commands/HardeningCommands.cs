using System.Collections.Generic;
using BepInEx.Configuration;

namespace ValheimEnforcer.common {
    internal static partial class TerminalManager {

        private static void RegisterHardeningCommands() {
            _ = new EnforcerCommand("enforcer-harden",
                "Reports which of this server's defences against an injected cheat menu are switched off, and what each one leaves open. Reads settings and changes nothing. Server admins only.",
                HardeningReport, CommandArea.Diagnostics,
                serverAuthoritative: true, requiresAdmin: true,
                aliases: "Enforcer-Hardening");
        }

        /// <summary>
        /// One setting on the report, and what leaving it off actually costs.
        ///
        /// The cost line is the point of this command. Every setting here is already documented, but a server
        /// owner reading the config file top to bottom has no way to see that six unrelated-looking switches
        /// are the six things standing between a mod menu and their world - and that most of them ship off.
        /// </summary>
        private sealed class HardeningCheck {
            internal string Name;
            internal ConfigEntry<bool> Toggle;
            internal string Cost;
            // Null unless this setting only does anything when another one is already on.
            internal ConfigEntry<bool> Requires;
        }

        private static void HardeningReport(EnforcerCommandArgs args) {
            args.Output.Info("What an injected cheat menu runs into on this server, and what it does not.");
            args.Output.Detail("", log: false);

            args.Output.Info("Detection - the client looks at itself and reports back. A patched client can lie to these.");
            ReportGroup(args, new List<HardeningCheck> {
                new HardeningCheck { Name = "EnableCheatDetection", Toggle = ValConfig.EnableCheatDetection,
                    Cost = "nothing is scanned at all; every check below it is inert" },
                new HardeningCheck { Name = "DetectInjectedCheatAssemblies", Toggle = ValConfig.DetectInjectedCheatAssemblies, Requires = ValConfig.EnableCheatDetection,
                    Cost = "a cheat menu that is not a mod is invisible - no plugin file to hash, nothing in the declared mod list" },
                new HardeningCheck { Name = "DetectProxyLoaders", Toggle = ValConfig.DetectProxyLoaders, Requires = ValConfig.ScanLoadedModules,
                    Cost = "a loader dropped beside valheim.exe is not looked for; this is how Valheaven installs" },
                new HardeningCheck { Name = "ScanLoadedModules", Toggle = ValConfig.ScanLoadedModules, Requires = ValConfig.EnableCheatDetection,
                    Cost = "nothing examines the DLLs loaded into the game, which also switches off the proxy check" },
                new HardeningCheck { Name = "VerifyDoorstopTarget", Toggle = ValConfig.VerifyDoorstopTarget, Requires = ValConfig.DetectProxyLoaders,
                    Cost = "BepInEx's own loader is recognised by the files beside it rather than by what it started" },
                new HardeningCheck { Name = "DetectUnlistedGameFolderDlls", Toggle = ValConfig.DetectUnlistedGameFolderDlls, Requires = ValConfig.ScanLoadedModules,
                    Cost = "only DLLs with a known proxy name are looked at; any other DLL dropped beside valheim.exe is not" },
                new HardeningCheck { Name = "ReportUnaccountedCode", Toggle = ValConfig.ReportUnaccountedCode, Requires = ValConfig.EnableCheatDetection,
                    Cost = "code loaded into the game that no mod accounts for is only caught when the catalog already knows its name" },
                new HardeningCheck { Name = "DetectAntiCheatTampering", Toggle = ValConfig.DetectAntiCheatTampering, Requires = ValConfig.EnableCheatDetection,
                    Cost = "nothing checks whether the cheat detector itself has been patched" },
                new HardeningCheck { Name = "CheatScanHeartbeat", Toggle = ValConfig.CheatScanHeartbeat, Requires = ValConfig.EnableCheatDetection,
                    Cost = "a client whose cheat scan has stopped looks exactly like a clean one" },
            });
            // Heartbeat misses are only acted on through the contradiction tracker.
            if (ValConfig.CheatScanHeartbeat != null && ValConfig.CheatScanHeartbeat.Value
                && (ValConfig.ReportClientContradictions == null || !ValConfig.ReportClientContradictions.Value)) {
                args.Output.Detail("           note: missed heartbeats are logged; they only count toward ContradictionAction when ReportClientContradictions is on", log: false);
            }

            args.Output.Detail("", log: false);
            args.Output.Info("Server-authoritative - these read packets the server already holds. A patched client cannot get past them.");
            ReportGroup(args, new List<HardeningCheck> {
                new HardeningCheck { Name = "EnableRpcGuards", Toggle = ValConfig.EnableRpcGuards,
                    Cost = "impossible damage, mass teleports and mass deletion are all relayed unchecked" },
                new HardeningCheck { Name = "GuardDamageRpc", Toggle = ValConfig.GuardDamageRpc, Requires = ValConfig.EnableRpcGuards,
                    Cost = "a one-hit-kill or a million-damage weapon lands as written" },
                new HardeningCheck { Name = "GuardPlayerTeleportRpc", Toggle = ValConfig.GuardPlayerTeleportRpc, Requires = ValConfig.EnableRpcGuards,
                    Cost = "any client can relocate every other player on the server at once" },
                new HardeningCheck { Name = "GuardZdoDestruction", Toggle = ValConfig.GuardZdoDestruction, Requires = ValConfig.EnableRpcGuards,
                    Cost = "a client can delete objects it neither owns nor is anywhere near" },
                new HardeningCheck { Name = "GuardGlobalKeys", Toggle = ValConfig.GuardGlobalKeys, Requires = ValConfig.EnableRpcGuards,
                    Cost = "a client can hand itself every boss kill, free building and the world's damage rates" },
                new HardeningCheck { Name = "EnableStructureValidation", Toggle = ValConfig.EnableStructureValidation,
                    Cost = "world-generation geometry and indestructible pieces are accepted from clients" },
                new HardeningCheck { Name = "BlockSpawnObjectRPC", Toggle = ValConfig.BlockSpawnObjectRPC,
                    Cost = "the item and creature spawner works; nothing in the game legitimately sends that message" },
                new HardeningCheck { Name = "DetectCheatToolArtifacts", Toggle = ValConfig.DetectCheatToolArtifacts,
                    Cost = "objects and items carrying a known cheat tool's own markers go unnoticed" },
                new HardeningCheck { Name = "ServerSideJoinEnforcement", Toggle = ValConfig.ServerSideJoinEnforcement,
                    Cost = "the join rules are applied by the client only, which is the thing being defended against" },
            });
            // The inverse of every toggle above - on is the weaker setting - so it does not fit ReportGroup.
            if (ValConfig.CatchupOverwriteOnJoin != null && ValConfig.CatchupOverwriteOnJoin.Value) {
                args.Output.Warning("    [catch-up] CatchupOverwriteOnJoin", log: false);
                args.Output.Detail("           every returning character is adopted as its client holds it; turn this off once everyone has rejoined", log: false);
            }
            // A three-way setting rather than a toggle, so it does not fit ReportGroup either.
            string attestation = ValConfig.AttestationPolicy?.Value ?? "Off";
            if (string.Equals(attestation, "Off", System.StringComparison.OrdinalIgnoreCase)) {
                args.Output.Warning("    [OFF] AttestationPolicy", log: false);
                args.Output.Detail("           a client can replay one captured mod report every session instead of producing it", log: false);
            } else {
                args.Output.Detail($"    [ on] AttestationPolicy ({attestation})", log: false);
            }

            args.Output.Detail("", log: false);
            args.Output.Info("Evidence - these do not refuse anything; they are what you read afterwards.");
            ReportGroup(args, new List<HardeningCheck> {
                new HardeningCheck { Name = "DetectInventoryGrid", Toggle = ValConfig.DetectInventoryGrid,
                    Cost = "an item in a grid cell no inventory has goes unreported - the signature of a resized inventory" },
                new HardeningCheck { Name = "DetectItemOrigins", Toggle = ValConfig.DetectItemOrigins,
                    Cost = "spawned gear with no crafter, or a crafter nobody here has been, is not noticed" },
                new HardeningCheck { Name = "EnableAuditLog", Toggle = ValConfig.EnableAuditLog,
                    Cost = "there is no record of what anyone gained, took or dealt when you come to look" },
                new HardeningCheck { Name = "ReportClientContradictions", Toggle = ValConfig.ReportClientContradictions,
                    Cost = "guard trips are not correlated against what each client declared at join (enforcer-trust)" },
            });

            args.Output.Detail("", log: false);
            args.Output.Info("Actions currently configured:");
            args.Output.Detail($"    Cheat tool detected     {Describe(ValConfig.CheatDetectionAction)}   (dedicated menus and loaders are banned regardless)", log: false);
            args.Output.Detail($"    Network guard refusal   {Describe(ValConfig.RpcGuardAction)}", log: false);
            args.Output.Detail($"    Client contradictions   {Describe(ValConfig.ContradictionAction)}   at {ValConfig.ContradictionThreshold?.Value.ToString() ?? "?"} distinct guard(s)", log: false);
        }

        private static void ReportGroup(EnforcerCommandArgs args, List<HardeningCheck> checks) {
            foreach (HardeningCheck check in checks) {
                if (check.Toggle == null) { continue; }

                // A setting that is on but whose prerequisite is off is doing nothing, and saying "on" about it
                // would be the most misleading line on the report.
                bool gated = check.Requires != null && !check.Requires.Value;
                if (check.Toggle.Value && !gated) {
                    args.Output.Detail($"    [ on] {check.Name}", log: false);
                    continue;
                }

                string state = check.Toggle.Value ? "[ineffective]" : "[OFF]";
                args.Output.Warning($"    {state} {check.Name}", log: false);
                args.Output.Detail($"           {check.Cost}", log: false);
            }
        }

        private static string Describe(ConfigEntry<string> action) {
            return string.IsNullOrEmpty(action?.Value) ? "?" : action.Value;
        }
    }
}
