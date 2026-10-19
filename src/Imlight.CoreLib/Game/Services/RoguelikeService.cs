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
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
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

    // The name of the run's on-screen timer token.
    private const string RUN_TOKEN_NAME = "ZT_RoguelikeStreetTimer";

    private static readonly Imcodec.ObjectProperty.ObjectSerializer s_tokenSerializer
        = new(Versionable: false, Behaviors: Imcodec.ObjectProperty.SerializerFlags.None);

    // An expire-time format being tried with .run timerui (absolute, relative, zero), for this session only.
    private string _timerUiOverride;

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

        // Runs from before seeds existed get one now.
        if (wizard.Roguelike.Seed == 0) {
            wizard.Roguelike.Seed = NewSeed();
            WizardCollection.UpdateCharacterRoguelike(wizard);
        }

        RoguelikeCreatures.Register(wizard);
        GiveRunMount(wizard);

        // Every zone change starts a new session, so walking into a shop or a house lands here too. Only a
        // fled battle (which sends the wizard to the hub) sends them back; otherwise they may roam freely.
        if (wizard.Roguelike.ReturnPending) {
            wizard.Roguelike.ReturnPending = false;
            WizardCollection.UpdateCharacterRoguelike(wizard);
            if (SendToPhaseZone(wizard)) {
                return;
            }
        }

        if (wizard.Roguelike.Phase == RoguelikePhase.Street) {
            StartTickTimer();
            GrantStreetQuests(wizard);
            ShowRunTimer(wizard, add: true);
        }
    }

    /// <summary>
    /// Shows the street's time left on screen. The game draws shown zone tokens with a duration as an on-screen
    /// hourglass (the Nightmare dungeon's gate timer is one), so the run sends a ZoneTokenTimer. The game counts it
    /// down by itself from the expire time, so it is sent once per zone; re-sending it restarts the hourglass.
    /// </summary>
    private void ShowRunTimer(Wizard wizard, bool add) {
        var run = wizard?.Roguelike;
        if (!RoguelikeConfig.ShowTimer || run is null || run.Phase != RoguelikePhase.Street) {
            return;
        }

        var left = Math.Max(0, RoguelikeConfig.StreetSeconds - run.StreetSeconds);
        var now = (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expireMode = (_timerUiOverride ?? RoguelikeConfig.TimerExpireMode).ToLowerInvariant();
        var token = new ZoneTokenTimer {
            m_debugName = RUN_TOKEN_NAME,
            m_description = RoguelikeConfig.TimerTitle,
            m_displayMode = ZTDisplayMode.ZTDM_Shown,
            m_storage = ZTStorage.ZTS_Player,
            m_inZone = true,
            m_enabled = true,
            m_discovered = true,
            // The full street time: the hourglass shows time left out of this, so it is half full at the halfway point.
            m_duration = $"{RoguelikeConfig.StreetSeconds}s",
            m_expireTime = expireMode switch {
                "relative" => (uint) left,
                "zero" => 0u,
                _ => now + (uint) left,
            },
        };

        if (!s_tokenSerializer.Serialize(token, (uint) (PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit),
                out var data)) {
            Logger.Warning("[Roguelike] Could not serialize the run timer token.");

            return;
        }

        // Replace any earlier copy so the countdown restarts from the server's time.
        if (!add) {
            SendToSocket(new WIZARD3_56_PROTOCOL.MSG_RemoveZoneToken {
                GlobalID = wizard.GameObjectID,
                TokenName = RUN_TOKEN_NAME,
            });
        }

        SendToSocket(new WIZARD3_56_PROTOCOL.MSG_AddZoneToken {
            GlobalID = wizard.GameObjectID,
            Add = data,
        });

        if (add) {
            Logger.Information("[Roguelike] Run timer token sent to {0}: {1}s left, expire mode {2}, {3} bytes.",
                Logger.Args(wizard.CharId, left, expireMode, data.Length));
        }
    }

    private void HideRunTimer() {
        var wizard = GetActiveWizard();
        if (RoguelikeConfig.ShowTimer && wizard is not null) {
            SendToSocket(new WIZARD3_56_PROTOCOL.MSG_RemoveZoneToken {
                GlobalID = wizard.GameObjectID,
                TokenName = RUN_TOKEN_NAME,
            });
        }
    }

    /// <summary>
    /// Hands out the tier's quests the first time the wizard stands on the tier's street. The run skips the
    /// Commons, so the quests the street's NPCs would expect (such as Ambrose's) are granted here instead,
    /// with their requirements skipped. Each quest's chain then carries on through the street's NPCs.
    /// </summary>
    private void GrantStreetQuests(Wizard wizard) {
        var run = wizard.Roguelike;
        var tier = RoguelikeConfig.GetTier(run.Tier);
        if (tier is null || run.QuestsGrantedTier >= run.Tier
            || !string.Equals(wizard.Zone, RoguelikeConfig.ResolveZone(tier.Street), StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        run.QuestsGrantedTier = run.Tier;
        WizardCollection.UpdateCharacterRoguelike(wizard);

        foreach (var quest in tier.Quests) {
            TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND {
                Action = "forcegrant",
                QuestName = quest,
            });
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

        if (mobs.Length > 0 && RoguelikeStock.RollCrownDrop(wizard) is { } crownItem) {
            GiveCrownDrop(wizard, crownItem);
        }

        if (mobs.Length > 0 && RoguelikeBoosts.RollAfterBattle(wizard) is { } potion) {
            InformGameClient(potion);
            WizardCollection.UpdateCharacterGameStats(wizard);
        }

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
        run.ReturnPending = true;
        if (_bossPending) {
            _bossPending = false;
            run.Phase = RoguelikePhase.Boss;
        }
        WizardCollection.UpdateCharacterRoguelike(GetActiveWizard());
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
            case "timerui":
                // Staff test: try a zone timer layout by name, for this session.
                HideRunTimer();
                _timerUiOverride = message.Argument?.Trim() ?? "";
                ShowRunTimer(wizard, add: true);
                InformGameClient(wizard.Roguelike?.Phase == RoguelikePhase.Street
                    ? $"Showing the run timer with expire mode \"{_timerUiOverride}\" (absolute, relative or zero). Put the one that works in TimerExpireMode."
                    : "The run timer only shows on a street.");
                break;
            case "train":
                GoTrain(wizard);
                break;
            case "return":
                if (wizard.Roguelike is null || !RoguelikeConfig.Enabled) {
                    InformGameClient("You have no run to return to.");
                }
                else if (wizard.IsInDuel) {
                    InformGameClient("Finish your battle first.");
                }
                else {
                    // Always moves, even within the same zone, so a wizard on the shared copy of their street
                    // lands in their run's private copy.
                    SendToPhaseZone(wizard, force: true);
                }
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
        wizard.Roguelike = new RoguelikeRun { Seed = NewSeed() };
        WizardCollection.UpdateCharacterRoguelike(wizard);
        RoguelikeCreatures.Register(wizard);
        GiveRunMount(wizard);

        InformGameClient($"Your run begins! Survive {FormatTime(RoguelikeConfig.StreetSeconds)} on this street, " +
                         "then face its boss. If you fall in battle, your wizard is gone for good. " +
                         "Type .run to check your progress.", isImportant: true);

        if (!SendToPhaseZone(wizard)) {
            StartTickTimer();
            GrantStreetQuests(wizard);
            ShowRunTimer(wizard, add: true);
        }
    }

    private void BeginBoss(Wizard wizard, bool afterBattle = false) {
        wizard.Roguelike.Phase = RoguelikePhase.Boss;
        WizardCollection.UpdateCharacterRoguelike(wizard);
        HideRunTimer();

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
        GiveBossTrainingPoints(wizard);

        if (run.Phase == RoguelikePhase.Complete) {
            InformGameClient($"You cleared all {RoguelikeConfig.TierCount} tiers! Run complete with {run.Kills} " +
                             "creatures defeated.", isImportant: true);
        }
        else {
            InformGameClient($"Boss defeated! Tier {run.Tier} cleared. Spend your gold here, then use the Rate My Stitch " +
                             "tome to visit the spell trainers (Mr. Lincoln brings you back) or to start the next street. " +
                             "(.run train and .run next work too.)", isImportant: true);
        }

        PlaceAfterBattle();
    }

    /// <summary>Hands out the run's random mount, once per run.</summary>
    private void GiveRunMount(Wizard wizard) {
        var run = wizard.Roguelike;
        if (!RoguelikeConfig.GiveMount || run is null || run.MountGiven) {
            return;
        }

        if (RoguelikeStock.PickRunMount() is not { } mountId) {
            Logger.Warning("[Roguelike] Could not find a mount to give {0}.", Logger.Args(wizard.CharId));

            return;
        }

        run.MountGiven = true;
        WizardCollection.UpdateCharacterRoguelike(wizard);

        var loot = new Imlight.CoreLib.WizardData.Models.World.DropTableResult();
        loot.Items.Add(new Imlight.CoreLib.WizardData.Models.World.DropItemResult { ItemId = mountId.ToString(), Quantity = 1 });
        Imlight.CoreLib.Game.DropTables.LootGranter.GrantAndDisplay(SessionActor.ActorRef, wizard, loot);

        var speed = RoguelikeConfig.MountSpeed > 0 ? $" Every mount runs at +{RoguelikeConfig.MountSpeed}% speed on a run." : "";
        InformGameClient($"A mount for your run is in your backpack. Equip it to get around faster!{speed}");
    }

    private void GiveCrownDrop(Wizard wizard, uint templateId) {
        var loot = new Imlight.CoreLib.WizardData.Models.World.DropTableResult();
        loot.Items.Add(new Imlight.CoreLib.WizardData.Models.World.DropItemResult {
            ItemId = templateId.ToString(),
            Quantity = 1,
        });

        Imlight.CoreLib.Game.DropTables.LootGranter.GrantAndDisplay(SessionActor.ActorRef, wizard, loot);
        InformGameClient("Rare find! A piece of crown gear dropped from that battle.");
    }

    private void GiveBossTrainingPoints(Wizard wizard) {
        var points = RoguelikeConfig.BossTrainingPoints;
        if (points <= 0) {
            return;
        }

        var total = wizard.MagicSchoolBehavior.TrainingPoints + points;
        wizard.UpdateTrainingPoints(total);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING { TrainingPoints = total });
        InformGameClient($"The boss's defeat earned you {points} training points. You now have {total}.");
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
        RoguelikeCreatures.Register(wizard);

        InformGameClient($"Tier {run.Tier} begins. Survive {FormatTime(RoguelikeConfig.StreetSeconds)}!");
        if (!SendToPhaseZone(wizard)) {
            StartTickTimer();
            GrantStreetQuests(wizard);
            ShowRunTimer(wizard, add: true);
        }
    }

    /// <summary>
    /// During the shop phase, sends the wizard to the spell trainers. .run return goes back to the shops,
    /// and .run next starts the next tier from either place.
    /// </summary>
    private void GoTrain(Wizard wizard) {
        var run = wizard.Roguelike;
        if (!RoguelikeConfig.Enabled || run is null || run.Phase is not (RoguelikePhase.Shop or RoguelikePhase.Complete)) {
            InformGameClient("You can visit the trainers after beating a boss.");

            return;
        }

        if (wizard.IsInDuel) {
            InformGameClient("Finish your battle first.");

            return;
        }

        var zone = RoguelikeConfig.ResolveZone(RoguelikeConfig.TrainZone);
        if (zone is null) {
            InformGameClient($"Roguelike setup problem: no zone matches \"{RoguelikeConfig.TrainZone}\".");

            return;
        }

        if (string.Equals(wizard.Zone, zone, StringComparison.OrdinalIgnoreCase)) {
            InformGameClient("You are already at the trainers.");

            return;
        }

        InformGameClient("Off to the trainers. Type .run return to go back to the shops, or .run next when you are ready.");
        Teleport(zone);
    }

    /// <summary>
    /// Sends the wizard to the zone their run's phase belongs in, unless they are already there.
    /// </summary>
    /// <returns>True when a teleport was started.</returns>
    private bool SendToPhaseZone(Wizard wizard, bool force = false) {
        var run = wizard.Roguelike;
        var tier = RoguelikeConfig.GetTier(run.Tier);
        if (tier is null) {
            return false;
        }

        var (configured, isPrivate) = run.Phase switch {
            // A private copy, so the run's own creatures spawn there.
            RoguelikePhase.Street => (tier.Street, RoguelikeCreatures.RandomStreetCreatures),
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

        if (!force && string.Equals(wizard.Zone, zone, StringComparison.OrdinalIgnoreCase)) {
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
            RoguelikePhase.Shop => "shopping (.run train for the spell trainers, .run next when ready)",
            _ => "finished",
        };

        return $"Tier {run.Tier} of {RoguelikeConfig.TierCount}: {where}. " +
               $"Creatures defeated: {run.Kills}. Bosses defeated: {run.BossesDefeated}. " +
               RoguelikeBoosts.Describe(run);
    }

    private static ulong NewSeed() {
        var seed = (ulong) Random.Shared.NextInt64(1, long.MaxValue);

        return seed == 0 ? 1 : seed;
    }

    private static string FormatTime(int seconds)
        => seconds >= 60 ? $"{seconds / 60}:{seconds % 60:00}" : $"{seconds} seconds";

    private static bool IsTutorialZone(string zone)
        => zone is not null && zone.Contains("Tutorial_", StringComparison.OrdinalIgnoreCase);

}
