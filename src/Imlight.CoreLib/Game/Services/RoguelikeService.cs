/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

/*
 * The roguelike run loop. Each tier is a street and a boss:
 *
 *   Street  -> the wizard fights on the tier's street while a timer runs (time only counts while online).
 *   Boss    -> when the timer runs out, the wizard is sent to the tier's boss and must beat it.
 *   Shop    -> after the boss, the wizard goes to the shop zone to spend gold and training points,
 *              then types .run next to start the next tier.
 *   Complete-> every tier is cleared.
 *
 * Dying in battle deletes the wizard when [Roguelike] Permadeath is on (CombatService does the deleting).
 * Fleeing a battle sends the wizard to the hub, so this service sends them back where their run says.
 */

using System;
using System.Linq;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Runs;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal sealed class RoguelikeService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const string TICK_TIMER_KEY = "roguelike-tick";
    private const int TICK_SECONDS = 15;
    private const int MAX_ZONES_LISTED = 20;
    private const string PLACE_TIMER_KEY = "roguelike-place";
    // Gives the client time to finish its victory scene before a teleport.
    private static readonly TimeSpan s_afterBattleDelay = TimeSpan.FromSeconds(5);

    // True once this session has put the wizard where their run says (first attach after login).
    private bool _placedThisSession;

    // True after a fled battle sent the wizard to the hub; the next attach sends them back.
    private bool _returnAfterHub;

    // True when the street timer ran out mid-battle; the boss starts when the battle ends.
    private bool _bossPending;

    internal static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new RoguelikeService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        if (!RoguelikeConfig.Enabled || RoguelikeConfig.TierCount == 0) {
            return;
        }

        var wizard = GetActiveWizard();
        if (wizard is null || IsTutorialZone(wizard.Zone)) {
            return;
        }

        if (wizard.Roguelike is null) {
            StartRun(wizard);

            return;
        }

        if (!_placedThisSession || _returnAfterHub) {
            _placedThisSession = true;
            _returnAfterHub = false;
            if (SendToPhaseZone(wizard)) {
                return;
            }
        }

        if (wizard.Roguelike.Phase == RoguelikePhase.Street) {
            StartTickTimer();
        }
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ROGUELIKE_TICK))]
    private void ReceiveTick(SERVICE_101_PROTOCOL.MSG_ROGUELIKE_TICK message) {
        var wizard = GetActiveWizard();
        var run = wizard?.Roguelike;
        if (run is null || run.Phase != RoguelikePhase.Street) {
            Timers.Cancel(TICK_TIMER_KEY);

            return;
        }

        var before = RoguelikeConfig.StreetSeconds - run.StreetSeconds;
        run.StreetSeconds += TICK_SECONDS;
        var left = RoguelikeConfig.StreetSeconds - run.StreetSeconds;
        WizardCollection.UpdateCharacterRoguelike(wizard);

        if (left <= 0) {
            Timers.Cancel(TICK_TIMER_KEY);
            if (wizard.IsInDuel) {
                _bossPending = true;
                InformGameClient("Time's up! Finish this battle, then you face the boss.");

                return;
            }

            BeginBoss(wizard);

            return;
        }

        // Warn when the time left crosses 5 minutes and 1 minute.
        foreach (var warnAt in new[] { 300, 60 }) {
            if (before > warnAt && left <= warnAt) {
                InformGameClient($"{FormatTime(left)} left on this street before the boss.");
            }
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATWIN))]
    private void ReceiveCombatWin(COMBAT_106_PROTOCOL.MSG_COMBATWIN message) {
        var wizard = GetActiveWizard();
        var run = wizard?.Roguelike;
        if (!RoguelikeConfig.Enabled || run is null) {
            return;
        }

        var mobs = message.MobTemplateIds ?? [];
        run.Kills += mobs.Length;

        if (run.Phase == RoguelikePhase.Boss && mobs.Any(RoguelikeConfig.IsBossMob)) {
            ClearBoss(wizard);

            return;
        }

        WizardCollection.UpdateCharacterRoguelike(wizard);

        if (_bossPending && run.Phase == RoguelikePhase.Street) {
            _bossPending = false;
            BeginBoss(wizard, afterBattle: true);
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT))]
    private void ReceiveCombatDefeat(COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT message) {
        var run = GetActiveWizard()?.Roguelike;
        if (!RoguelikeConfig.Enabled || run is null) {
            return;
        }

        // A death with permadeath on deletes the wizard; there is nothing left to do.
        if (message.DiedInDuel && RoguelikeConfig.Permadeath) {
            return;
        }

        // The wizard fled (or permadeath is off) and is being sent to the hub. Send them back afterwards.
        _returnAfterHub = true;
        if (_bossPending) {
            _bossPending = false;
            run.Phase = RoguelikePhase.Boss;
            WizardCollection.UpdateCharacterRoguelike(GetActiveWizard());
        }
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ROGUELIKE_COMMAND))]
    private void ReceiveCommand(SERVICE_101_PROTOCOL.MSG_ROGUELIKE_COMMAND message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        switch ((message.Action ?? "").ToLowerInvariant()) {
            case "next":
                NextTier(wizard);
                break;
            case "place":
                // Sent by this service's own timer after a battle.
                SendToPhaseZone(wizard);
                break;
            case "here":
                InformGameClient($"You are in zone: {wizard.Zone}");
                break;
            case "zones":
                ListZones(message.Argument);
                break;
            default:
                InformGameClient(DescribeRun(wizard));
                break;
        }
    }

    private void StartRun(Wizard wizard) {
        wizard.Roguelike = new RoguelikeRun();
        WizardCollection.UpdateCharacterRoguelike(wizard);
        _placedThisSession = true;

        InformGameClient($"Your run begins! Survive {FormatTime(RoguelikeConfig.StreetSeconds)} on this street, " +
                         "then face its boss. If you fall in battle, your wizard is gone for good. " +
                         "Type .run to check your progress.", isImportant: true);

        if (!SendToPhaseZone(wizard)) {
            StartTickTimer();
        }
    }

    private void BeginBoss(Wizard wizard, bool afterBattle = false) {
        wizard.Roguelike.Phase = RoguelikePhase.Boss;
        WizardCollection.UpdateCharacterRoguelike(wizard);

        InformGameClient("Time's up! The boss awaits. Win, and the shops are yours.", isImportant: true);
        if (afterBattle) {
            PlaceAfterBattle();
        }
        else {
            SendToPhaseZone(wizard);
        }
    }

    private void PlaceAfterBattle()
        => Timers.StartSingleTimer(PLACE_TIMER_KEY,
                                   new SERVICE_101_PROTOCOL.MSG_ROGUELIKE_COMMAND { Action = "place" },
                                   s_afterBattleDelay);

    private void ClearBoss(Wizard wizard) {
        var run = wizard.Roguelike;
        run.BossesDefeated++;
        run.Phase = run.Tier >= RoguelikeConfig.TierCount ? RoguelikePhase.Complete : RoguelikePhase.Shop;
        WizardCollection.UpdateCharacterRoguelike(wizard);

        if (run.Phase == RoguelikePhase.Complete) {
            InformGameClient($"You cleared all {RoguelikeConfig.TierCount} tiers! Run complete with {run.Kills} " +
                             "creatures defeated.", isImportant: true);
        }
        else {
            InformGameClient($"Boss defeated! Tier {run.Tier} cleared. Spend your gold and training points, " +
                             "then type .run next to start the next street.", isImportant: true);
        }

        PlaceAfterBattle();
    }

    private void NextTier(Wizard wizard) {
        var run = wizard.Roguelike;
        if (!RoguelikeConfig.Enabled || run is null) {
            InformGameClient("Roguelike mode is off on this server.");

            return;
        }

        if (run.Phase != RoguelikePhase.Shop) {
            InformGameClient(run.Phase == RoguelikePhase.Complete
                ? "Your run is complete. There are no more streets."
                : "You can only move on from the shops, after beating the boss.");

            return;
        }

        run.Tier++;
        run.Phase = RoguelikePhase.Street;
        run.StreetSeconds = 0;
        WizardCollection.UpdateCharacterRoguelike(wizard);

        InformGameClient($"Tier {run.Tier} begins. Survive {FormatTime(RoguelikeConfig.StreetSeconds)}!");
        if (!SendToPhaseZone(wizard)) {
            StartTickTimer();
        }
    }

    /// <summary>
    /// Sends the wizard to the zone their run's phase belongs in, unless they are already there.
    /// </summary>
    /// <returns>True when a teleport was started.</returns>
    private bool SendToPhaseZone(Wizard wizard) {
        var run = wizard.Roguelike;
        var tier = RoguelikeConfig.GetTier(run.Tier);
        if (tier is null) {
            return false;
        }

        var (configured, isPrivate) = run.Phase switch {
            RoguelikePhase.Street => (tier.Street, false),
            RoguelikePhase.Boss => (tier.Boss, true),
            _ => (RoguelikeConfig.ShopZone, false),
        };

        var zone = RoguelikeConfig.ResolveZone(configured);
        if (zone is null) {
            Logger.Warning("[Roguelike] No zone matches \"{0}\". Fix it in Imlight.ini.", Logger.Args(configured));
            InformGameClient($"Roguelike setup problem: no zone matches \"{configured}\". " +
                             "Ask the server owner to check [Roguelike] in Imlight.ini.");

            return false;
        }

        if (string.Equals(wizard.Zone, zone, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        Teleport(zone, makePrivate: isPrivate);

        return true;
    }

    private void StartTickTimer() {
        if (Timers.IsTimerActive(TICK_TIMER_KEY)) {
            return;
        }

        var interval = TimeSpan.FromSeconds(TICK_SECONDS);
        Timers.StartPeriodicTimer(TICK_TIMER_KEY, new SERVICE_101_PROTOCOL.MSG_ROGUELIKE_TICK(), interval, interval);
    }

    private void ListZones(string filter) {
        var matches = RoguelikeConfig.FindZones(filter).Take(MAX_ZONES_LISTED + 1).ToList();
        if (matches.Count == 0) {
            InformGameClient($"No zones contain \"{filter}\".");

            return;
        }

        var shown = string.Join("\n", matches.Take(MAX_ZONES_LISTED));
        var more = matches.Count > MAX_ZONES_LISTED ? "\n(more not shown; narrow the search)" : "";
        InformGameClient(shown + more, isImportant: true);
    }

    private static string DescribeRun(Wizard wizard) {
        var run = wizard.Roguelike;
        if (!RoguelikeConfig.Enabled) {
            return "Roguelike mode is off on this server.";
        }

        if (run is null) {
            return "Your run has not started yet.";
        }

        var tier = RoguelikeConfig.GetTier(run.Tier);
        if (tier is null) {
            return "Roguelike mode has no tiers set up. Check [Roguelike] Streets and Bosses in Imlight.ini.";
        }

        var where = run.Phase switch {
            RoguelikePhase.Street => $"on {tier.Street}, " +
                                     $"{FormatTime(Math.Max(0, RoguelikeConfig.StreetSeconds - run.StreetSeconds))} " +
                                     "until the boss",
            RoguelikePhase.Boss => $"facing the boss in {tier.Boss}",
            RoguelikePhase.Shop => "shopping (type .run next when ready)",
            _ => "finished",
        };

        return $"Tier {run.Tier} of {RoguelikeConfig.TierCount}: {where}. " +
               $"Creatures defeated: {run.Kills}. Bosses defeated: {run.BossesDefeated}.";
    }

    private static string FormatTime(int seconds)
        => seconds >= 60 ? $"{seconds / 60}:{seconds % 60:00}" : $"{seconds} seconds";

    private static bool IsTutorialZone(string zone)
        => zone is not null && zone.Contains("Tutorial_", StringComparison.OrdinalIgnoreCase);

}
