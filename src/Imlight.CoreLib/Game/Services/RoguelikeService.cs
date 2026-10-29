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
using System.Collections.Generic;
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

        // A run whose welcome was never closed (the wizard logged out first) shows it again.
        if (wizard.Roguelike.AwaitingStart) {
            ShowStartDialog(wizard);

            return;
        }

        // Runs from before seeds existed get one now.
        if (wizard.Roguelike.Seed == 0) {
            wizard.Roguelike.Seed = NewSeed();
            WizardCollection.UpdateCharacterRoguelike(wizard);
        }

        RoguelikeCreatures.Register(wizard);
        GiveRunMount(wizard);
        PlayRunMusic(newTrack: true);

        // However the wizard got to a zone the run keeps them out of (logging in there, say), back they go.
        if (RoguelikeConfig.TravelBlockedFor(wizard) && RoguelikeConfig.IsBlockedZone(wizard.Zone)
            && !RoguelikeConfig.IsStaffTeleport(wizard.CharId, consume: true)) {
            wizard.Roguelike.ReturnPending = false;
            WizardCollection.UpdateCharacterRoguelike(wizard);
            if (SendToPhaseZone(wizard, force: true)) {
                return;
            }
        }

        // Every zone change starts a new session, so walking into a shop or a house lands here too. Only a
        // fled battle (which sends the wizard to the hub) sends them back; otherwise they may roam freely.
        if (wizard.Roguelike.ReturnPending) {
            wizard.Roguelike.ReturnPending = false;
            WizardCollection.UpdateCharacterRoguelike(wizard);
            if (SendToPhaseZone(wizard)) {
                return;
            }
        }

        if (wizard.Roguelike.ArcPromptPending && wizard.Roguelike.Phase is RoguelikePhase.Shop or RoguelikePhase.Complete) {
            ShowArcPrompt(wizard);
        }

        if (wizard.Roguelike.Phase == RoguelikePhase.Street) {
            StartTickTimer();
            GrantStreetQuests(wizard);
            ShowRunTimer(wizard, add: true);
        }
        else if (wizard.Roguelike.Phase == RoguelikePhase.Boss) {
            GrantBossQuests(wizard);
        }
    }

    /// <summary>
    /// Hands out the tier's TierNBossQuests on first reaching its boss zone, requirements skipped: some boss rooms
    /// only give a side quest of their own, while the boss fight belongs to a main quest from elsewhere.
    /// </summary>
    private void GrantBossQuests(Wizard wizard) {
        var run = wizard.Roguelike;
        var tier = RoguelikeConfig.GetTier(run.Tier);
        if (tier is null || tier.BossQuests.Count == 0 || run.BossQuestsGrantedTier >= run.Tier
            || !string.Equals(wizard.Zone, RoguelikeConfig.ResolveZone(tier.Boss), StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        run.BossQuestsGrantedTier = run.Tier;
        WizardCollection.UpdateCharacterRoguelike(wizard);

        foreach (var quest in tier.BossQuests) {
            // "Quest@Goal 3" hands the quest out and moves it on to that goal (a boss fight late in a quest).
            var at = quest.IndexOf('@');
            TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND {
                Action = "forcegrant",
                QuestName = at > 0 ? quest[..at].Trim() : quest,
            });
            if (at > 0) {
                TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND { Action = "advanceto", QuestName = quest });
            }
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

        var left = Math.Max(0, RoguelikeConfig.StreetSecondsFor(run.Tier) - run.StreetSeconds);
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
            m_duration = $"{RoguelikeConfig.StreetSecondsFor(run.Tier)}s",
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

        var before = RoguelikeConfig.StreetSecondsFor(run.Tier) - run.StreetSeconds;
        run.StreetSeconds += TICK_SECONDS;
        var left = RoguelikeConfig.StreetSecondsFor(run.Tier) - run.StreetSeconds;
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
        Achievements.Count(wizard, "BattlesWon");
        Achievements.Count(wizard, "Kills", mobs.Length);

        // The battle's music gave way to the zone's own as it ended; bring the run's track back.
        if (RoguelikeConfig.RandomMusic || RoguelikeConfig.RandomBattleMusic) {
            Timers.StartSingleTimer(MUSIC_TIMER_KEY,
                new SERVICE_101_PROTOCOL.MSG_ROGUELIKE_COMMAND { Action = "music", Argument = "resume" },
                TimeSpan.FromSeconds(3));
        }

        // What the battle gave, told in one box so two finds don't pile up on top of each other.
        var finds = new List<string>();
        if (mobs.Length > 0 && RoguelikeStock.RollCrownDrop(wizard) is { } crownItem) {
            GiveCrownDrop(wizard, crownItem);
            finds.Add("Rare find! A piece of crown gear dropped from that battle.");
        }

        if (mobs.Length > 0 && RoguelikeStock.RollJewel(wizard) is { } jewelId) {
            finds.Add(GiveJewel(wizard, jewelId));
            Achievements.Count(wizard, "JewelsFound");
        }

        if (mobs.Length > 0 && RoguelikeBoosts.RollAfterBattle(wizard) is { } potion) {
            finds.Add(potion);
            WizardCollection.UpdateCharacterGameStats(wizard);
        }

        if (finds.Count > 0) {
            InformGameClient(string.Join("\n\n", finds), isImportant: true);
        }

        // A chance to learn a spell: always worth a try after a boss, sometimes after any battle.
        var isTierBoss = run.Phase == RoguelikePhase.Boss && mobs.Any(id => RoguelikeConfig.IsTierBoss(run.Tier, id));
        if (mobs.Length > 0
            && RoguelikeStock.RollSpellToLearn(wizard, isTierBoss ? RoguelikeStock.BossSpellChance : RoguelikeStock.BattleSpellChance) is { } spellId) {
            TeachSpell(wizard, spellId);
            Achievements.Count(wizard, "SpellsLearned");
        }

        // Sometimes a pet: any age, stat talents only, every stat at its maximum.
        Achievements.Moment petMoment = null;
        if (mobs.Length > 0 && RoguelikePets.RollsDrop(wizard, isTierBoss) && GiveRunPet(wizard) is { } pet) {
            Achievements.Count(wizard, "PetsFound");
            petMoment = new Achievements.Moment("PetFound", Amount: Imlight.CoreLib.Game.Pet.PetProgress.GetBehavior(pet)?.m_level ?? 0);
        }

        if (isTierBoss) {
            Achievements.Count(wizard, "BossesBeaten");
            AnnounceAchievements(Achievements.Check(wizard, petMoment));
            ClearBoss(wizard, message.Rounds);

            return;
        }

        AnnounceAchievements(Achievements.Check(wizard, petMoment));

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

        if (message.DiedInDuel) {
            Achievements.Count(GetActiveWizard(), "Deaths");
            Achievements.Count(GetActiveWizard(), "DeathsThisTier");
        }
        else {
            Achievements.Count(GetActiveWizard(), "Flees");
        }

        AnnounceAchievements(Achievements.Check(GetActiveWizard()));

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
            case "begin":
                // The welcome was never closed (or the game never said so): start anyway.
                BeginRunAfterWelcome(wizard);
                break;
            case "next":
                NextTier(wizard);
                break;
            case "battlemusic":
                PlayBattleMusic();
                break;
            case "music" when message.Argument is "off" or "on":
                // ".run music off": no run music for this wizard, the game's own settings and zone music apply.
                MusicOff.Set(wizard.CharId, message.Argument == "off");
                InformGameClient(message.Argument == "off"
                    ? "Run music is off for you. Zone music comes back with your next zone change."
                    : "Run music is on for you.");
                if (message.Argument == "on") {
                    PlayRunMusic(newTrack: true);
                }

                break;
            case "music":
                // ".run music" rolls another track; the service's own timers replay the current one after a battle
                // and change it every MusicChangeMinutes outside battles.
                if (message.Argument == "rotate" && wizard.IsInDuel) {
                    break;
                }

                PlayRunMusic(newTrack: message.Argument != "resume");
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
            case "continue":
                ChooseArc(wizard, takeBreak: false);
                break;
            case "break":
                ChooseArc(wizard, takeBreak: true);
                break;
            case "logout":
                // Sent by this service's own timer, once the farewell has had a moment on screen.
                CloseSession();
                break;
            case "jewel":
                // Staff test: ".run jewel" gives a random jewel or pin, ".run jewel circle" (square, triangle, tear,
                // pet, pin) one of that shape.
                if (RoguelikeStock.PickJewel(wizard, message.Argument?.Trim()) is { } testJewel) {
                    InformGameClient(GiveJewel(wizard, testJewel));
                }
                else {
                    InformGameClient("No jewel fits that (or your level). Try .run jewel square, circle, triangle, tear, pet or pin.");
                }

                break;
            case "pet":
                // Staff test: ".run pet" gives a random run pet, ".run pet mega" (or 1 to 7) one of that age.
                if (wizard.Roguelike is null) {
                    InformGameClient("Start a run first.");
                    break;
                }

                var arg = message.Argument?.Trim();
                byte? petAge = string.IsNullOrEmpty(arg) ? null
                    : byte.TryParse(arg, out var n) && n is >= 1 and <= 7 ? n
                    : Imlight.CoreLib.Game.Pet.PetProgress.ParseLevel(arg);
                if (!string.IsNullOrEmpty(arg) && petAge is null) {
                    InformGameClient("Usage: .run pet [baby|teen|adult|ancient|epic|mega|ultra]");
                    break;
                }

                GiveRunPet(wizard, petAge);
                break;
            case "cards": {
                // What each piece of equipped gear's cards really are on this run (their tooltips show the gear's own).
                var lines = new List<string>();
                foreach (var item in wizard.EquipmentBehavior.EquippedItems ?? []) {
                    if (Imlight.CoreLib.Shared.Items.ItemHelper.GetItemTemplate(item) is { } gear
                        && Imlight.CoreLib.Game.Randomization.Randomizer.DescribeRunItemCards(wizard, gear) is { } gearCards) {
                        string key = gear.m_displayName;
                        lines.Add($"{(string.IsNullOrEmpty(key) ? "Gear" : Imlight.CoreLib.Shared.Resources.Locale.GetEnglishName(key) ?? "Gear")}: {gearCards}");
                    }
                }

                InformGameClient(lines.Count == 0 ? "None of your gear's cards are swapped." : "Your gear's cards on this run:\n" + string.Join("\n", lines),
                                 isImportant: true);
                break;
            }
            case "pwn": {
                // TEMPORARY staff test: teaches the Pwn developer spell (".run pwn giant" for Giant Pwn). Remove with
                // the command once testing is done.
                var pwnName = string.Equals(message.Argument?.Trim(), "giant", StringComparison.OrdinalIgnoreCase) ? "GiantPwn" : "Pwn";
                if (Imlight.CoreLib.Game.Spells.SpellFactory.GetSpell(pwnName) is { } pwn) {
                    TeachSpell(wizard, pwn.m_templateID);
                    InformGameClient($"{pwnName} learned. Put it in your deck from your spellbook.");
                }
                else {
                    InformGameClient($"The {pwnName} spell was not found in the game data.");
                }

                break;
            }
            case "goto":
                GotoPhase(wizard, message.Argument);
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
            case "stats":
                InformGameClient(RoguelikeBoosts.DescribeStats(wizard), isImportant: true);
                break;
            case "achievements":
                // After a boss (its experience has landed by now), or .run achievements.
                AnnounceAchievements(Achievements.Check(wizard));
                if (message.Argument == "list") {
                    InformGameClient(DescribeAchievements(wizard), isImportant: true);
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
        wizard.Roguelike = new RoguelikeRun { Seed = NewSeed(), AwaitingStart = RoguelikeConfig.StartDialog };
        WizardCollection.UpdateCharacterRoguelike(wizard);
        RoguelikeCreatures.Register(wizard);
        GiveRunMount(wizard);

        if (wizard.Roguelike.AwaitingStart) {
            // The wizard reads the welcome first; closing it starts the run.
            ShowStartDialog(wizard);

            return;
        }

        InformGameClient(StartText(wizard), isImportant: true);
        BeginFirstStreet(wizard);
    }

    private const string MUSIC_TIMER_KEY = "RoguelikeMusic";
    private const string ACHIEVEMENTS_TIMER_KEY = "RoguelikeAchievements";
    private const string MUSIC_ROTATE_KEY = "RoguelikeMusicRotate";
    private string _runTrack;

    // The zone's own music, to bring back after a random battle theme when zone music is not random.
    private string _zoneMusic => Imlight.CoreLib.Game.Zone.Core.ZoneLoader.MusicFileName(GetActiveWizard()?.Zone);

    /// <summary>
    /// With RandomMusic, plays a random track from MusicTracks (or the current one again, after a battle's own
    /// music), in place of the zone's music.
    /// </summary>
    /// <summary>Wizards who turned run music off (.run music off); kept in run-music-off.txt next to the server.</summary>
    private static readonly Imlight.CoreLib.Game.Combat.BattleModes MusicOff = new("run-music-off.txt");

    private bool MusicOffForMe => GetActiveWizard() is { } w && MusicOff.IsOn(w.CharId);

    private void PlayRunMusic(bool newTrack) {
        if (MusicOffForMe) {
            return;
        }

        // The shops play their own tune; random music on top of it would be two songs at once.
        if (GetActiveWizard()?.Zone is { } zone && RoguelikeConfig.NoRandomMusicZones.Contains(zone)) {
            Timers.Cancel(MUSIC_ROTATE_KEY);

            return;
        }

        if (!RoguelikeConfig.RandomMusic) {
            // Only battles are random: after one, the zone's own music, which the game may not bring back itself.
            if (!newTrack && RoguelikeConfig.RandomBattleMusic && !string.IsNullOrEmpty(_zoneMusic)) {
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_PLAYMUSIC {
                    FadeTime = 1.5f, Flags = (sbyte) RoguelikeConfig.MusicFlags, SoundFilename = _zoneMusic, StartDelay = 0.5f,
                });
            }

            return;
        }

        if (RoguelikeConfig.MusicTracks.Length == 0 || RoguelikeConfig.MusicTracks.Length == 0 || GetActiveWizard()?.Roguelike is null) {
            return;
        }

        if (newTrack || string.IsNullOrEmpty(_runTrack)) {
            var tracks = RoguelikeConfig.MusicTracks;
            var pick = tracks[Random.Shared.Next(tracks.Length)];
            if (tracks.Length > 1 && pick == _runTrack) {
                pick = tracks[(Array.IndexOf(tracks, pick) + 1 + Random.Shared.Next(tracks.Length - 1)) % tracks.Length];
            }

            _runTrack = pick;
            if (RoguelikeConfig.MusicChangeMinutes > 0) {
                var every = TimeSpan.FromMinutes(RoguelikeConfig.MusicChangeMinutes);
                Timers.StartPeriodicTimer(MUSIC_ROTATE_KEY,
                    new SERVICE_101_PROTOCOL.MSG_ROGUELIKE_COMMAND { Action = "music", Argument = "rotate" }, every, every);
            }
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PLAYMUSIC {
            FadeTime = 1.5f,
            Flags = (sbyte) RoguelikeConfig.MusicFlags,
            SoundFilename = _runTrack,
            StartDelay = 0.5f,
        });
    }

    private void PlayBattleMusic() {
        if (MusicOffForMe) {
            return;
        }

        var tracks = RoguelikeConfig.BattleMusicTracks;
        if (!RoguelikeConfig.RandomBattleMusic || tracks.Length == 0 || GetActiveWizard()?.Roguelike is null) {
            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PLAYMUSIC {
            FadeTime = 1.0f,
            Flags = (sbyte) RoguelikeConfig.MusicFlags,
            SoundFilename = tracks[Random.Shared.Next(tracks.Length)],
            StartDelay = 0.5f,
        });
    }

    private bool _awaitingArcChoice;

    /// <summary>
    /// The end of the run's last tier: congratulations in a box, and Merle Ambrose asking (as a yes/no window, the
    /// game's own words) whether the wizard is ready to go on. Yes continues to the next arc; No takes a break and
    /// logs them out. Logging back in finds them at the shops, still asked.
    /// </summary>
    private void ShowArcPrompt(Wizard wizard) {
        var arc = ArcNumber(wizard.Roguelike.Tier);
        var last = wizard.Roguelike.Phase == RoguelikePhase.Complete;
        InformGameClient($"Congratulations on clearing Arc {arc}! {wizard.Roguelike.Kills} creatures defeated.\n\n" +
                         (last ? "That was the last arc there is, for now. " : "") +
                         $"Would you like to continue{(last ? "" : $" to Arc {arc + 1}")}, or take a break? Answer Merle Ambrose: Yes to continue, " +
                         "No to take a break (you will be logged out, and come back at the shops). " +
                         "You can also type .run continue or .run break.", isImportant: true);

        var dialog = new ActorDialog {
            m_dialogTag = "RoguelikeArc",
            m_dialogEntries = [
                new NPCDialogEntry {
                    m_dialog = RoguelikeConfig.ArcPromptLine,
                    m_actorTemplateID = RoguelikeConfig.StartDialogActor,
                    m_bypassCameraOnReview = true,
                    m_allowPlayerToMove = true,
                },
            ],
        };

        if (!new ObjectSerializer(Versionable: false).Serialize(dialog, 16, out var data)) {
            return;
        }

        _awaitingArcChoice = true;
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_ACTORDIALOG {
            MobileID = 0,
            QuestID = 0,
            GoalID = 0,
            CompletionType = "RoguelikeArc",
            ActorDialog = data,
            Persona = "",
            PersonaName = "",
            PersonaIcon = "",
            IsYesNo = 1,
        });
    }

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_SENDYESNOREPLY))]
    private void ReceiveYesNoReply(WIZARD3_56_PROTOCOL.MSG_SENDYESNOREPLY message) {
        if (!_awaitingArcChoice) {
            return;
        }

        ChooseArc(GetActiveWizard(), takeBreak: message.Success == 0);
    }

    /// <summary>Which arc a tier belongs to: 1 up to the first ArcEndTiers tier, 2 up to the next, and so on.</summary>
    private static int ArcNumber(int tier) => 1 + RoguelikeConfig.ArcEndTiers.Count(end => end < tier);

    private void ChooseArc(Wizard wizard, bool takeBreak) {
        var run = wizard?.Roguelike;
        if (run is not { ArcPromptPending: true }) {
            InformGameClient("That choice comes at the end of an arc.");

            return;
        }

        _awaitingArcChoice = false;
        run.ArcPromptPending = false;
        WizardCollection.UpdateCharacterRoguelike(wizard);

        if (takeBreak) {
            Logger.Information("[Roguelike] {0} takes a break after tier {1}.", Logger.Args(wizard.CharId, run.Tier));
            InformGameClient("Enjoy your break! When you log back in, you'll be at the shops.", isImportant: true);
            SendToSocket(new EXTENDEDBASE_2_PROTOCOL.MSG_FORCE_DISCONNECT {
                Type = 0,
                TimeStamp = DateTime.UtcNow.ToString("O"),
                Message = "Enjoy your break! When you log back in, you'll be at the shops.",
            });
            Timers.StartSingleTimer(LOGOUT_TIMER_KEY, new SERVICE_101_PROTOCOL.MSG_ROGUELIKE_COMMAND { Action = "logout" },
                                    TimeSpan.FromSeconds(3));

            return;
        }

        Logger.Information("[Roguelike] {0} continues after tier {1}.", Logger.Args(wizard.CharId, run.Tier));
        InformGameClient(run.Phase == RoguelikePhase.Complete
            ? "There are no more arcs yet! Until the next one opens, the shops and trainers are yours."
            : $"Arc {ArcNumber(run.Tier + 1)} awaits! Spend your gold, then use the Rate My Stitch tome to start tier {run.Tier + 1}.",
            isImportant: true);
    }

    private const string LOGOUT_TIMER_KEY = "RoguelikeLogout";

    private const string START_TIMER_KEY = "RoguelikeStartDialog";
    private static readonly TimeSpan s_startDialogTimeout = TimeSpan.FromSeconds(120);
    private DateTime _startDialogShownAt;
    private bool _awaitingStartDialog;

    private static string StartText(Wizard wizard)
        => $"Your run begins! Survive {FormatTime(RoguelikeConfig.StreetSecondsFor(wizard.Roguelike.Tier))} on " +
           "each street, then face its boss. Win, and the shops are yours. If you fall in battle, your wizard is " +
           "gone for good. Type .run to check your progress.";

    /// <summary>
    /// Shows the run's welcome as Merle Ambrose speaking. The run starts (the teleport to the first street, and its
    /// countdown) when the wizard closes it, or after a while if the game never says it was closed.
    /// </summary>
    private void ShowStartDialog(Wizard wizard) {
        // The dialog window only shows the game's own text (locale keys), so the run's rules go in a box on
        // top of it, and the dialog is Merle Ambrose speaking lines the game already has.
        InformGameClient(StartText(wizard) + "\n\nClose Merle Ambrose's words when you are ready: that starts the run.",
                         isImportant: true);
        var dialog = new ActorDialog {
            m_dialogTag = "RoguelikeStart",
            m_dialogEntries = [
                .. RoguelikeConfig.StartDialogLines.Select(key => new NPCDialogEntry {
                    m_dialog = key,
                    m_actorTemplateID = RoguelikeConfig.StartDialogActor,
                    m_bypassCameraOnReview = true,
                    m_allowPlayerToMove = true,
                }),
            ],
        };

        var serializer = new ObjectSerializer(Versionable: false);
        if (!serializer.Serialize(dialog, 16, out var data)) {
            Logger.Error("[Roguelike] Could not serialize the start dialog; starting the run now.");
            BeginRunAfterWelcome(wizard);

            return;
        }

        _awaitingStartDialog = true;
        _startDialogShownAt = DateTime.UtcNow;
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_ACTORDIALOG {
            MobileID = 0,
            QuestID = 0,
            GoalID = 0,
            CompletionType = "RoguelikeStart",
            ActorDialog = data,
            Persona = "",
            PersonaName = "",
            PersonaIcon = "",
        });
        Timers.StartSingleTimer(START_TIMER_KEY, new SERVICE_101_PROTOCOL.MSG_ROGUELIKE_COMMAND { Action = "begin" },
                                s_startDialogTimeout);
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_COMPLETEDIALOG))]
    private void ReceiveCompleteDialog(WIZARD_12_PROTOCOL.MSG_COMPLETEDIALOG message) {
        // The game sends an empty completion type when a dialog window closes. Anything closed in the first moment
        // after the welcome went out belongs to an earlier dialog.
        string completionType = message.CompletionType;
        if (!_awaitingStartDialog || !string.IsNullOrEmpty(completionType)
            || DateTime.UtcNow - _startDialogShownAt < TimeSpan.FromSeconds(1)) {
            return;
        }

        BeginRunAfterWelcome(GetActiveWizard());
    }

    /// <summary>Starts a run whose welcome was read: off to the first street, where the countdown begins.</summary>
    private void BeginRunAfterWelcome(Wizard wizard) {
        _awaitingStartDialog = false;
        Timers.Cancel(START_TIMER_KEY);
        if (wizard?.Roguelike is not { AwaitingStart: true } run) {
            return;
        }

        run.AwaitingStart = false;
        WizardCollection.UpdateCharacterRoguelike(wizard);
        Logger.Information("[Roguelike] {0} starts their run.", Logger.Args(wizard.CharId));
        BeginFirstStreet(wizard);
    }

    private void BeginFirstStreet(Wizard wizard) {
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

    private void ClearBoss(Wizard wizard, int rounds = 0) {
        var run = wizard.Roguelike;
        run.BossesDefeated++;
        run.Phase = run.Tier >= RoguelikeConfig.TierCount ? RoguelikePhase.Complete : RoguelikePhase.Shop;
        // The end of an arc (ArcEndTiers, and the last tier) asks the wizard whether to go on or take a break.
        run.ArcPromptPending = run.Phase == RoguelikePhase.Complete || RoguelikeConfig.ArcEndTiers.Contains(run.Tier);
        WizardCollection.UpdateCharacterRoguelike(wizard);
        GiveBossTrainingPoints(wizard);
        GiveBossRewards(wizard, run.Tier);
        if (RoguelikeBoosts.GiveBossBoosts(wizard, run.Tier) is { } bossBoosts) {
            WizardCollection.UpdateCharacterGameStats(wizard);
            InformGameClient(bossBoosts, isImportant: true);
        }

        // Achievements for the tier, then a look again once the boss's experience has landed (levels).
        AnnounceAchievements(Achievements.Check(wizard, new Achievements.Moment("TierCleared", run.Tier, rounds)));
        Achievements.Reset(wizard, "DeathsThisTier");
        Timers.StartSingleTimer(ACHIEVEMENTS_TIMER_KEY,
            new SERVICE_101_PROTOCOL.MSG_ROGUELIKE_COMMAND { Action = "achievements" }, TimeSpan.FromSeconds(5));

        // Streets are too short to finish every quest; the boss settles them all, with their rewards.
        if (RoguelikeConfig.CompleteQuestsAfterBoss) {
            TellOtherServices(new SERVICE_101_PROTOCOL.MSG_ROGUELIKE_COMPLETEQUESTS());
        }

        if (run.ArcPromptPending) {
            // The "continue or take a break" question comes once the wizard is back at the shops.
            Logger.Information("[Roguelike] {0} cleared tier {1}, the end of an arc ({2} creatures defeated).",
                Logger.Args(wizard.CharId, run.Tier, run.Kills));
        }
        else {
            InformGameClient($"Boss defeated! Tier {run.Tier} cleared. Spend your gold here, then use the Rate My Stitch " +
                             "tome to visit the spell trainers (Mr. Lincoln brings you back) or to start the next street. " +
                             "(.run train and .run next work too.)", isImportant: true);
        }

        PlaceAfterBattle();
    }

    /// <summary>
    /// The boss's own reward, as it is (no multiplier): BossGold (or TierNBossGold) and TierNBossExp, which gives the
    /// levels the next tier needs once the run stops levelling wizards through battles (FlatRatesFromTier).
    /// </summary>
    private void GiveBossRewards(Wizard wizard, int tier) {
        var gold = RoguelikeConfig.BossGoldFor(tier);
        var exp = RoguelikeConfig.GetTier(tier)?.BossExp ?? 0;
        if (RoguelikeConfig.GetTier(tier) is { BossLevel: > 0 } leveled) {
            // TierNBossLevel: whatever it takes to reach that level, from the game's own experience table.
            var target = Math.Min(leveled.BossLevel, Imlight.CoreLib.Shared.Character.MagicLevelsConfig.MaxLevel);
            var needed = (long) Imlight.CoreLib.Shared.Character.MagicLevelsConfig.GetExperiencePointsAtLevel(target)
                         - wizard.MagicSchoolBehavior.ExperiencePoints - exp;
            if (needed > 0) {
                exp += (int) Math.Min(needed, int.MaxValue - exp);
            }
        }
        if (gold <= 0 && exp <= 0) {
            return;
        }

        if (gold > 0) {
            Imlight.CoreLib.Game.DropTables.LootGranter.UpdateWizardGold(SessionActor.ActorRef, wizard, gold);
        }

        if (exp > 0) {
            SessionActor.ActorRef.Tell(new CHARACTER_103_PROTOCOL.MSG_GAINXP { XP = exp });
        }

        var parts = new List<string>();
        if (gold > 0) {
            parts.Add($"{gold:N0} gold");
        }

        if (exp > 0) {
            parts.Add($"{exp:N0} experience");
        }

        Logger.Information("[Roguelike] {0} beat tier {1}'s boss: {2}.", Logger.Args(wizard.CharId, tier, string.Join(" and ", parts)));
        InformGameClient($"Boss reward: {string.Join(" and ", parts)}!", isImportant: true);
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

    /// <summary>Teaches a spell the way a drop does, so the game shows its "New Spell Learned" window.</summary>
    private void TeachSpell(Wizard wizard, uint spellId) {
        var loot = new Imlight.CoreLib.WizardData.Models.World.DropTableResult();
        loot.Items.Add(new Imlight.CoreLib.WizardData.Models.World.DropItemResult { ItemId = spellId.ToString(), Quantity = 1 });
        Imlight.CoreLib.Game.DropTables.LootGranter.GrantAndDisplay(SessionActor.ActorRef, wizard, loot);
        Logger.Information("[Roguelike] {0} learned spell {1} from a battle.", Logger.Args(wizard.CharId, spellId));
    }

    /// <summary>Tells the wizard of achievements they just earned.</summary>
    private void AnnounceAchievements(List<Achievements.Achievement> earned) {
        foreach (var achievement in earned) {
            InformGameClient($"Achievement unlocked: {achievement.Name}! {achievement.Description}", isImportant: true);
        }
    }

    private static string DescribeAchievements(Wizard wizard) {
        var earned = Achievements.All.Where(a => Achievements.HasEarned(wizard, a.Id)).Select(a => a.Name).ToList();

        return earned.Count == 0
            ? $"No achievements yet (0 of {Achievements.All.Count}). The launcher's Achievements page shows what there is to earn."
            : $"Achievements ({earned.Count} of {Achievements.All.Count}): {string.Join(", ", earned)}.";
    }

    /// <summary>Gives the wizard a run pet (RoguelikePets): a random one, or of the given age (1 Baby to 7 Ultra).</summary>
    private WizClientObjectItem GiveRunPet(Wizard wizard, byte? age = null) {
        var pet = RoguelikePets.Build(wizard, age);
        if (pet is null || !wizard.AddPetToInventory(pet)) {
            Logger.Warning("[Roguelike] Could not give {0} a pet.", Logger.Args(wizard.CharId));
            if (age is not null) {
                InformGameClient("Could not build a pet. Check the server log.");
            }

            return null;
        }

        if (!Imlight.CoreLib.Shared.Items.ItemSync.SendAdded(SessionActor.ActorRef, wizard, pet)) {
            Logger.Warning("[Roguelike] Could not show {0} their new pet.", Logger.Args(wizard.CharId));
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = pet.m_globalID,
            ItemLocation = 1,
            ItemTemplateID = (uint) pet.m_templateID,
        });

        var talents = RoguelikePets.ShownTalents(pet);
        var petName = RoguelikePets.NameOf(pet);
        InformGameClient($"A pet found you{(string.IsNullOrEmpty(petName) ? "" : $": {petName}")}! It is {RoguelikePets.AgeOf(pet)}" +
                         (string.IsNullOrEmpty(talents) ? "." : $", with {talents}.") + " It is in your backpack.",
                         isImportant: true);
        Logger.Information("[Roguelike] {0} got pet {1} ({2}): {3}.",
            Logger.Args(wizard.CharId, pet.m_templateID.Full, RoguelikePets.AgeOf(pet), talents));

        return pet;
    }

    /// <summary>Gives a jewel or pin and returns the line that tells of it.</summary>
    private string GiveJewel(Wizard wizard, uint jewelId) {
        GiveCrownDrop(wizard, jewelId);
        var template = Imlight.CoreLib.Shared.Resources.CoreObjectFactory.GetCoreTemplate(jewelId) as WizItemTemplate;
        var jewelName = template is null ? null : Imlight.CoreLib.Shared.Resources.Locale.GetEnglishName(template.m_displayName.ToString());
        var shape = template is not null && Imlight.CoreLib.Shared.Items.JewelSockets.GetShape(template) is { } s
            ? Imlight.CoreLib.Shared.Items.JewelSockets.ShapeName(s) : "jewel";
        Logger.Information("[Roguelike] {0} got jewel {1} ({2}, {3}).", Logger.Args(wizard.CharId, jewelId, jewelName, shape));

        return $"A {shape} jewel dropped{(string.IsNullOrEmpty(jewelName) ? "" : $": {jewelName}")}! Socket it into your gear.";
    }

    private void GiveCrownDrop(Wizard wizard, uint templateId) {
        var loot = new Imlight.CoreLib.WizardData.Models.World.DropTableResult();
        loot.Items.Add(new Imlight.CoreLib.WizardData.Models.World.DropItemResult {
            ItemId = templateId.ToString(),
            Quantity = 1,
        });

        Imlight.CoreLib.Game.DropTables.LootGranter.GrantAndDisplay(SessionActor.ActorRef, wizard, loot);
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
        run.ArcPromptPending = false;
        WizardCollection.UpdateCharacterRoguelike(wizard);
        RoguelikeCreatures.Register(wizard);

        // A tier without a street goes straight to its boss.
        if (RoguelikeConfig.GetTier(run.Tier) is { HasStreet: false }) {
            run.Phase = RoguelikePhase.Boss;
            WizardCollection.UpdateCharacterRoguelike(wizard);
            InformGameClient($"Tier {run.Tier} begins: straight to the boss!", isImportant: true);
            SendToPhaseZone(wizard);

            return;
        }

        InformGameClient($"Tier {run.Tier} begins. Survive {FormatTime(RoguelikeConfig.StreetSecondsFor(run.Tier))}!");
        if (!SendToPhaseZone(wizard)) {
            StartTickTimer();
            GrantStreetQuests(wizard);
            ShowRunTimer(wizard, add: true);
        }
    }

    /// <summary>
    /// Staff test: ".run goto 3 boss" puts the run on that tier and phase (street, boss or shop; street when left
    /// out) and sends the wizard there, with the tier's quests handed out again.
    /// </summary>
    private void GotoPhase(Wizard wizard, string argument) {
        var run = wizard.Roguelike;
        var parts = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!RoguelikeConfig.Enabled || run is null) {
            InformGameClient("You have no run.");

            return;
        }

        if (parts.Length == 0 || !int.TryParse(parts[0], out var tier) || tier < 1 || tier > RoguelikeConfig.TierCount) {
            // No angle brackets: the game's chat reads them as markup and drops what is inside.
            InformGameClient(parts.Length > 0 && int.TryParse(parts[0], out var asked) && asked > RoguelikeConfig.TierCount
                ? $"This server has {RoguelikeConfig.TierCount} tiers, so there is no tier {asked}. Check Tier{asked} under [Roguelike] in Imlight.ini (the server log says what it read)."
                : $"Usage: .run goto (tier 1-{RoguelikeConfig.TierCount}) [street|boss|shop], for example .run goto 6 boss");

            return;
        }

        var phase = (parts.Length > 1 ? parts[1] : "street").ToLowerInvariant() switch {
            "boss" => RoguelikePhase.Boss,
            "shop" or "shops" => RoguelikePhase.Shop,
            _ => RoguelikeConfig.GetTier(tier) is { HasStreet: false } ? RoguelikePhase.Boss : RoguelikePhase.Street,
        };

        if (wizard.IsInDuel) {
            InformGameClient("Finish your battle first.");

            return;
        }

        run.AwaitingStart = false;
        run.Tier = tier;
        run.Phase = phase;
        run.StreetSeconds = 0;
        run.QuestsGrantedTier = tier - 1;
        run.BossQuestsGrantedTier = tier - 1;
        run.ReturnPending = false;
        WizardCollection.UpdateCharacterRoguelike(wizard);
        RoguelikeCreatures.Register(wizard);
        HideRunTimer();
        Timers.Cancel(TICK_TIMER_KEY);

        Logger.Information("[Roguelike] {0} jumps to tier {1}, {2}.", Logger.Args(wizard.CharId, tier, phase));
        InformGameClient($"Tier {tier}, {phase.ToString().ToLowerInvariant()}.");
        if (SendToPhaseZone(wizard, force: true)) {
            return;
        }

        // Already there: start what arriving would have.
        if (phase == RoguelikePhase.Street) {
            StartTickTimer();
            GrantStreetQuests(wizard);
            ShowRunTimer(wizard, add: true);
        }
        else if (phase == RoguelikePhase.Boss) {
            GrantBossQuests(wizard);
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
                                     $"{FormatTime(Math.Max(0, RoguelikeConfig.StreetSecondsFor(run.Tier) - run.StreetSeconds))} " +
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
