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
 *
 * ========================================================================
 * COMBAT PARTICIPANT CONTROLLER
 * ========================================================================
 * 
 * PURPOSE:
 * Manages individual participant data and state within a duel, providing
 * methods to modify health, mana, pips, and track spell effects on the participant.
 * 
 * USAGE EXAMPLE:
 * var subCircle = new CombatDuelSubCircle(duelActor, radius, rotation, color, index);
 * subCircle.AssignParticipant(actor, participantObject);
 * subCircle.DamageParticipant(damage);
 * 
 * NOTE:
 * Each subcircle handles deck management, spell casting costs, and
 * participant-specific stat calculations during effect resolution.
 * 
 * TODO:
 * - This should derive from ZoneEntityComponent
 * - Check if creature can be stunned
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Imlight.CoreLib.Game.Combat;

internal enum CombatSlotType {
    
    Creature,
    Player

}

/// <summary>
/// Represents a position in the combat duel for a single participant, managing their combat state, spells, and effects.
/// </summary>
/// <remarks>
/// Each subcircle maintains a participant's combat data including their position in the duel, stats, cards, pips, 
/// and hanging effects. It provides methods to manipulate the participant's state through damage, healing, and
/// spell effects. Subcircles are assigned to either the player or monster team and positioned within the duel
/// according to the sigil template.
/// </remarks>
public class CombatDuelSubCircle {
    
    private const float AGGRO_TIME_IN_SECONDS = 0.75f;
    internal const byte MAX_PIP_COUNT = 7;

    private static readonly bool s_schoolPips = ConfigurationManager.GetValue("Battle.SchoolPips", true);
    private const byte PLAYER_HAND_SIZE = 7;

    internal string SlotName { get; set; }
    internal CombatSlotType SlotType { get; set; }
    internal int SlotIndex { get; private set; }
    internal Vector3 WorldPosition { get; set; }
    internal float WorldRotation { get; set; }
    internal IActorRef ParticipantActor { get; private set; }
    internal CoreObject ParticipantObject { get; private set; }
    internal ServerWizGameStats ParticipantGameStats { get; private set; }
    internal CombatParticipant CombatParticipant { get; private set; }
    internal bool AddedToDuel { get; set;}
    internal bool IsSummonedMinion { get; private set; }
    // A sandboxed wizard fights with a copy of their stats, so nothing of the duel reaches the saved wizard.
    internal bool IsSandboxed { get; private set; }
    internal List<SpellEffect> _hangingEffects { get {
        if (CombatParticipant is null) {
            return null;
        }
        if (CombatParticipant is not null && CombatParticipant.m_hangingEffects is null) {
            CombatParticipant.m_hangingEffects = [];
        }

        return CombatParticipant.m_hangingEffects;
    }}
    public uint AvailableSpells {
        get {
            if (_combatDeck is null) {
                return 0;
            }

            return (uint) _combatDeck.RemainingCardCount;
        }
    }
    public uint TotalSpells {
        get {
            if (_combatDeck is null) {
                return 0;
            }

            return (uint) _combatDeck.TotalCardCount;
        }
    }
    internal bool Occupied => ParticipantObject is not null;
    internal bool IsHumanPlayer => ParticipantObject?.m_templateID == 1;
    // A creature of the AI: neither a wizard nor a minion that a wizard or creature summoned.
    internal bool IsAiCreature => Occupied && !IsHumanPlayer && !IsSummonedMinion;
    // The side of the circle the participant stands on; a minion stands on the side of its summoner.
    internal CombatTeam OccupiedTeam => SlotType == CombatSlotType.Creature && Occupied ? CombatTeam.Monster : CombatTeam.Player;
    internal bool IsAlive => ParticipantGameStats?.m_currentHitpoints > 0;
    internal bool CheatNoFizzle;
    internal CombatDeck _combatDeck;
    internal readonly CombatDuelComponent _duelActor;
    internal Wizard _wizard;
    internal int _usedPipsForExperienceGain = 0;

    private readonly float _radius;
    private readonly float _rotation;
    private readonly Color _color;

    // ctor
    internal CombatDuelSubCircle(CombatDuelComponent duelActor, float radius, float rotation, Color color, int index) {
        _duelActor = duelActor;
        _radius = radius;
        _rotation = rotation;
        _color = color;
        SlotIndex = index;
    }

    internal CombatParticipant AssignParticipant(IActorRef actor, CoreObject participantObject, bool isSummonedMinion = false,
                                                 int minionOwnerSubCircle = 0) {
        ParticipantActor = actor;
        ParticipantObject = participantObject;
        IsSummonedMinion = isSummonedMinion;

        // Set the CombatParticipant based on what team they are.
        if (IsHumanPlayer) {
            InitializePlayerSubCircle();
        }
        else {
            InitializeCreatureSubCircle(isSummonedMinion, minionOwnerSubCircle);
        }

        // Inform the actor that they've been added to a duel.
        var msg = new COMBAT_106_PROTOCOL.MSG_ACTORADDEDTODUEL {
            DuelActor = _duelActor.ActorRef,
            Duel = _duelActor,
            SubCircle = this,
            SlotPosition = WorldPosition,
            SlotOrientation = WorldRotation
        };
        ParticipantActor.Tell(msg);

        // We don't need to await this.
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        PlayEntranceAnimation(participantObject, actor);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

        return this.CombatParticipant;
    }

    internal void RemoveParticipant() {
        ParticipantActor = null;
        ParticipantObject = null;
        CombatParticipant = null;
        AddedToDuel = false;
    }

    internal Hand DrawHand() {
        var newHand = _combatDeck.GetHand();
        CombatParticipant.m_pHand = newHand;

        return newHand;
    }

    internal Hand GetCurrentHand() {
        // As-is, no draw or refill: a discard frees a slot this turn and the client must see it open.
        return new() { m_spellList = _combatDeck?.LastGivenHand ?? [] };
    }

    /// <summary>
    /// Appends the given spells to the current hand, created from the template IDs.
    /// </summary>
    internal void AddSpellsToHand(uint[] spellTemplateIds) {
        if (_combatDeck is null) {
            return;
        }

        foreach (var tid in spellTemplateIds ?? []) {
            var spell = SpellFactory.GetSpell(tid);
            if (spell is not null) {
                _combatDeck.AddCardToHand(spell);
            }
        }
    }

    /// <summary>
    /// Empties the current hand.
    /// </summary>
    internal void ClearHand() {
        _combatDeck?.ClearHand();
    }

    /// <summary>
    /// Sets this participant's pips to exactly the given count of generic pips, no power pips, capped at
    /// the max.
    /// </summary>
    internal void SetPips(int count) {
        if (CombatParticipant is null) {
            return;
        }

        CombatParticipant.m_pipCount.m_genericPips = (byte) Math.Clamp(count, 0, MAX_PIP_COUNT);
        CombatParticipant.m_pipCount.m_powerPips = 0;
    }

    internal void DiscardCard(Spell spell) {
        _combatDeck.Discard(spell);
    }

    internal void ConsumeCard(Spell spell) {
        _combatDeck.ConsumeCard(spell);
    }

    internal Spell GetSpellFromLastHand(byte index) {
        if (_combatDeck.LastGivenHand is null || index >= _combatDeck.LastGivenHand.Count) {
            return null;
        }

        return _combatDeck.LastGivenHand[index];
    }

    // .overdose: the wizard's battles start with Quickening already cast on them.
    private bool _overdoseApplied;
    private static readonly Lazy<SpellTemplate> s_quickening = new(() => {
        var spells = SpellFactory.AllSpellTemplates().Select(t => t.Template).Where(t => t is not null && !t.m_Treasure).ToList();

        bool Named(SpellTemplate t, string word) => ((string) t.m_name ?? "").Contains(word, StringComparison.OrdinalIgnoreCase)
            || ((string) t.m_displayName ?? "").Contains(word, StringComparison.OrdinalIgnoreCase);

        // The internal name can differ from the card's name, so the ID list (which knows the card names) is asked first.
        var fromCatalog = IdCatalog.Search(IdCatalogKind.Spell, "Quickening", 20)
            .Select(e => CoreObjectFactory.GetCoreTemplate((uint) e.Id) as SpellTemplate)
            .Where(t => t?.m_effects is { Count: > 0 })
            .OrderBy(t => t.m_Treasure)
            .FirstOrDefault();

        return fromCatalog
            ?? spells.FirstOrDefault(t => string.Equals((string) t.m_name, "Quickening", StringComparison.OrdinalIgnoreCase))
            ?? spells.FirstOrDefault(t => Named(t, "Quickening"))
            ?? spells.FirstOrDefault(t => Named(t, "Quicken"));
    });

    private void ApplyOverdoseOnce() {
        if (_overdoseApplied || _wizard?.Overdose != true) {
            return;
        }

        _overdoseApplied = true;
        try {
            if (s_quickening.Value is not { } quickening) {
                Logger.Warning("Overdose: no Quickening spell was found.");

                return;
            }

            var effects = quickening.m_effects ?? [];
            Logger.Information("Overdose: {0} starts the battle with {1} ({2}).", Logger.Args(_wizard.CharId,
                (string) quickening.m_name, string.Join(", ", effects.Select(e => $"{e?.m_effectType} {e?.m_effectParam}"))));
            foreach (var effect in effects) {
                // A copy, so nothing changes the shared spell. Effects the battle can't take on are skipped.
                if (effect is null || effect.m_effectTarget == kEffectTarget.kGlobal) {
                    continue;
                }

                CombatEffectApplicator.ApplyEffect(effect with { m_effectTarget = kEffectTarget.kSelf }, [], this, [this]);
            }
        }
        catch (Exception e) {
            // Never let this take the battle (or the server) down.
            Logger.Error("Overdose failed for {0}: {1}", Logger.Args(_wizard.CharId, e.ToString()));
        }
    }

    /// <summary>
    /// The round's pips, then Archmastery. <paramref name="highestArchmastery"/> is the most Archmastery anyone
    /// in the battle has.
    /// </summary>
    internal void DoPipGain(float highestArchmastery) {
        GainPips();
        if (ArchmasteryActive) {
            ArchmasteryRound(highestArchmastery);
        }
    }

    private void GainPips() {
        ApplyOverdoseOnce();

        // Shadow pips sit in their own slots, so they roll whether or not the normal slots are full.
        RollShadowPip();

        // If the participant has the maximum amount of pips, do not gain any more (but maybe convert one).
        var pipCount = CombatParticipant.m_pipCount;
        if (TotalPips(pipCount) >= MAX_PIP_COUNT) {
            if (!ArchmasteryActive) {
                ConvertToSchoolPip();
            }

            return;
        }

        // Randomizer mode rolls how many pips come this round; otherwise it is one.
        var gains = Randomization.Randomizer.RandomizePips ? Randomization.Randomizer.RollPipGain() : 1;

        // Never leave someone with no pips at all, or nothing in their hand can be cast.
        if (gains == 0 && TotalPips(pipCount) == 0) {
            gains = 1;
        }
        // GainSchoolPips: the archmastery gauge fills by SchoolPipChance each round; once full, the next pip
        // comes as a school pip, no stats needed.
        var schoolGauge = ArchmasterySchool != 0 && !ArchmasteryActive;
        if (schoolGauge && _schoolGauge < GAUGE_FULL) {
            _schoolGauge = Math.Min(GAUGE_FULL, _schoolGauge + s_schoolPipChance);
        }

        for (var i = 0; i < gains; i++) {
            if (TotalPips(pipCount) >= MAX_PIP_COUNT) {
                return;
            }

            if (schoolGauge && _schoolGauge >= GAUGE_FULL && SchoolPipTarget() is { } gainSchool) {
                SetSchoolPips(pipCount, gainSchool, (byte) (SchoolPips(pipCount, gainSchool)!.Value + 1));
                _schoolGauge = 0;

                continue;
            }

            var gainedPowerPip = DeterminePowerPipGain(CombatParticipant);
            if (gainedPowerPip) {
                pipCount.m_powerPips++;
            }
            else {
                pipCount.m_genericPips++;
            }
        }
    }

    /// <summary>
    /// Every pip slot in use: generic, power and school pips.
    /// </summary>
    internal static int TotalPips(PipCount pips)
        => pips.m_genericPips + pips.m_powerPips + pips.m_balancePips + pips.m_deathPips + pips.m_firePips
           + pips.m_icePips + pips.m_lifePips + pips.m_mythPips + pips.m_stormPips;

    /// <summary>
    /// [Battle] SchoolPips: with every pip slot full, a round turns one power pip into a school pip of the
    /// participant's own school. School pips pay for the school pip cost some spells have.
    /// </summary>
    private void ConvertToSchoolPip() {
        var pipCount = CombatParticipant.m_pipCount;
        if (!s_schoolPips || pipCount.m_powerPips == 0) {
            return;
        }

        if (SchoolPipTarget() is not { } school) {
            return;
        }

        pipCount.m_powerPips--;
        SetSchoolPips(pipCount, school, (byte) (SchoolPips(pipCount, school)!.Value + 1));
    }

    // [Battle] GainSchoolPips: a wizard's new pip can come as a school pip (SchoolPipChance %), no stats needed.
    private static readonly bool s_gainSchoolPips = ConfigurationManager.GetValue("Battle.GainSchoolPips", false);
    private static readonly int s_schoolPipChance
        = Math.Clamp(ConfigurationManager.GetValue("Battle.SchoolPipChance", 25), 0, 100);
    // [Battle] RandomSchoolPips: school pips come in a random school instead of the participant's own.
    private static readonly bool s_randomSchoolPips = ConfigurationManager.GetValue("Battle.RandomSchoolPips", false);
    // [Battle] NaturalShadowPips: wizards get shadow pips without the shadow pip rating stat.
    private static readonly bool s_naturalShadowPips = ConfigurationManager.GetValue("Battle.NaturalShadowPips", false);
    private const int NATURAL_SHADOW_PIP_CHANCE = 25;

    /// <summary>
    /// The school a new school pip goes to: a random one with RandomSchoolPips, else the participant's own
    /// (null when they have no primary school).
    /// </summary>
    private MagicSchool? SchoolPipTarget() {
        if (s_randomSchoolPips) {
            return s_pipSchools[Random.Shared.Next(s_pipSchools.Length)];
        }

        // The school picked for the gauge in the deck, else the participant's own.
        foreach (var id in new[] { ArchmasterySchool, ParticipantGameStats.m_schoolID }) {
            var school = (MagicSchool) id;
            if (id != 0 && SchoolPips(CombatParticipant.m_pipCount, school) is not null) {
                return school;
            }
        }

        return null;
    }

    // [Battle] ShadowPips: shadow pips, which shadow spells cost, kept in their own slots.
    private static readonly bool s_shadowPips = ConfigurationManager.GetValue("Battle.ShadowPips", true);
    // 0 means no limit (up to NO_SHADOW_LIMIT, which keeps the count inside a byte).
    private const int NO_SHADOW_LIMIT = 50;
    private static readonly int s_playerMaxShadowPips
        = ShadowLimit(ConfigurationManager.GetValue("Battle.PlayerShadowPipLimit", 0));
    private static readonly int s_creatureMaxShadowPips
        = ShadowLimit(ConfigurationManager.GetValue("Battle.CreatureShadowPipLimit", 0));

    // [Battle] PlayerShadowPipMin: the fewest shadow pips a wizard holds; they top back up each round.
    private static readonly int s_playerMinShadowPips
        = Math.Clamp(ConfigurationManager.GetValue("Battle.PlayerShadowPipMin", 0), 0, NO_SHADOW_LIMIT);
    // [Battle] CreatureShadowPips: creatures can get shadow pips at all.
    private static readonly bool s_creatureShadowPips = ConfigurationManager.GetValue("Battle.CreatureShadowPips", true);
    // [Battle] RandomCreatureShadowPips: each creature, each battle, has CreatureShadowMagicChance % to get shadow magic.
    private static readonly bool s_randomCreatureShadowPips
        = ConfigurationManager.GetValue("Battle.RandomCreatureShadowPips", false);
    private static readonly int s_creatureShadowMagicChance
        = Math.Clamp(ConfigurationManager.GetValue("Battle.CreatureShadowMagicChance", 50), 0, 100);
    // [Randomizer] RandomizeShadowPips: each wizard's shadow pip limit is rolled (1 to 5) from the seed, and
    // each creature gets shadow magic at random.
    private static readonly bool s_randomizeShadowPips
        = Randomization.Randomizer.Enabled && !Randomization.Randomizer.Tame && ConfigurationManager.GetValue("Randomizer.RandomizeShadowPips", false);
    private const int RANDOM_SHADOW_LIMIT_MAX = 5;
    private bool? _creatureRolledShadowMagic;

    private int MaxShadowPips => _wizard is null ? s_creatureMaxShadowPips
        : ParticipantGameStats?.m_shadowPipSlots is int slots and > 0 ? Math.Min(slots, NO_SHADOW_LIMIT)
        : s_randomizeShadowPips ? 1 + Randomization.Randomizer.PickIndex(_wizard, "ShadowPipLimit", 0, RANDOM_SHADOW_LIMIT_MAX)
        : s_playerMaxShadowPips;

    private bool CreatureHasShadowMagic {
        get {
            if (!s_creatureShadowPips && !s_randomizeShadowPips) {
                return false;
            }

            if (!s_randomCreatureShadowPips && !s_randomizeShadowPips) {
                return true;
            }

            _creatureRolledShadowMagic ??= Random.Shared.Next(100) < s_creatureShadowMagicChance;

            return _creatureRolledShadowMagic.Value;
        }
    }

    private static int ShadowLimit(int value) => value <= 0 ? NO_SHADOW_LIMIT : Math.Min(value, NO_SHADOW_LIMIT);
    private static readonly int s_shadowPipChance
        = Math.Clamp(ConfigurationManager.GetValue("Battle.ShadowPipChance", 0), 0, 100);

    /// <summary>
    /// Each round, maybe gives a shadow pip. The chance is ShadowPipChance if set, else the participant's own
    /// shadow pip rating (from level and gear) as a percent.
    /// </summary>
    private void RollShadowPip() {
        var pipCount = CombatParticipant.m_pipCount;
        var max = MaxShadowPips;
        if (!HasShadowMagic) {
            return;
        }

        // [Battle] PlayerShadowPipMin: a wizard never holds fewer shadow pips than this.
        if (_wizard is not null && pipCount.m_shadowPips < Math.Min(s_playerMinShadowPips, max)) {
            pipCount.m_shadowPips = (byte) Math.Min(s_playerMinShadowPips, max);
        }

        if (pipCount.m_shadowPips >= max) {
            return;
        }

        // The chance fills the shadow gauge each round; a full gauge (GAUGE_FULL) gives the pip.
        _shadowGauge += ShadowPipChance();
        if (_shadowGauge >= GAUGE_FULL) {
            _shadowGauge -= GAUGE_FULL;
            pipCount.m_shadowPips++;
        }
    }

    private int ShadowPipChance() {
        var chance = s_shadowPipChance > 0
            ? s_shadowPipChance
            : (int) Math.Round(ParticipantGameStats.m_shadowPipRating + ParticipantGameStats.m_bonusShadowPipRating);
        var onRun = Imlight.CoreLib.Game.Runs.RoguelikeBoosts.RunShadowPips
                    && Imlight.CoreLib.Game.Runs.RoguelikeBoosts.OnRun(_wizard);
        if ((s_naturalShadowPips || HasOwnShadowMagic || onRun) && _wizard is not null && chance <= 0) {
            chance = NATURAL_SHADOW_PIP_CHANCE;
        }

        // A roguelike run's Potions of Shadow fill the gauge faster.
        chance += Imlight.CoreLib.Game.Runs.RoguelikeBoosts.Get(_wizard, Imlight.CoreLib.Game.Runs.RoguelikeBoosts.ShadowPip);

        return Math.Clamp(chance, 0, (int) GAUGE_FULL);
    }

    // Shadow and archmastery (school pip) gauges, 0 to GAUGE_FULL, shown around the pip slots.
    private const float GAUGE_FULL = 100f;
    // How many shadow pip slots the client lays out when there is no limit.
    private const int SHOWN_SHADOW_SLOTS_WITHOUT_LIMIT = 5;
    private float _shadowGauge;
    private float _schoolGauge;

    /// <summary>
    /// The school the archmastery gauge fills toward, as the client wants it (0 when none).
    /// </summary>
    internal uint ArchmasterySchool => !(s_gainSchoolPips || ArchmasteryActive) || _wizard is null ? 0
        : _deckArchmasterySchool != 0 ? _deckArchmasterySchool : ParticipantGameStats.m_schoolID;

    // The school picked for the gauge in the equipped deck, or 0 when none was picked.
    private uint _deckArchmasterySchool;

    internal static bool SchoolGaugeOn => s_gainSchoolPips || s_archmastery;

    private uint DeckArchmasterySchool() {
        var equipment = _wizard?.EquipmentBehavior;
        var deckSlot = equipment?.SlotList?.FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck);
        var deckItem = deckSlot?.ItemId is null ? null
            : equipment.EquippedItems?.FirstOrDefault(i => i.m_globalID == deckSlot.ItemId);

        return deckItem is not null && CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(deckItem, out var deck)
            ? deck.m_archmasterySchool
            : 0;
    }

    internal float ArchmasteryPoints => ArchmasterySchool == 0 ? 0
        : ArchmasteryActive ? Math.Min(_archmasteryPower, 1f) * GAUGE_FULL
        : _schoolGauge;

    // [Archmastery] Like the game: wizards gain Archmastery from level 50 (the game's level table), and their
    // Power Orb fills each round, fastest for whoever has the most Archmastery in the battle. Each point of
    // power turns a power pip into a school pip; with 3 points left, a white pip becomes a power pip.
    private static readonly bool s_archmastery = ConfigurationManager.GetValue("Archmastery.Enabled", true);
    // Gives wizards under level 50 the level 50 Archmastery, so they get school pips too.
    private static readonly bool s_archmasteryAllLevels = ConfigurationManager.GetValue("Archmastery.AllLevels", false);
    // Extra Archmastery for every wizard (stands in for gear and jewels).
    private static readonly float s_archmasteryBonus
        = Math.Max(0f, ConfigurationManager.GetValue("Archmastery.BonusRating", 0f));
    // How much the fastest orb fills in a round (1 = a school pip every round). 0 uses the game's own number.
    private static readonly float s_archmasteryFillOverride
        = Math.Clamp(ConfigurationManager.GetValue("Archmastery.FillPerRound", 0f), 0f, 1f);
    private const int ARCHMASTERY_LEVEL = 50;
    private const float ARCHMASTERY_MAX_POWER = 7f;
    private const float DEFAULT_ARCHMASTERY_FILL = 0.5f;
    private const float DEFAULT_POWER_PIP_COST = 3f;
    private float _archmasteryPower;
    private float? _archmasteryRating;

    private bool ArchmasteryActive => s_archmastery && _wizard is not null && ArchmasteryRating > 0;

    /// <summary>
    /// This wizard's Archmastery: the game's level table (0 under level 50) plus [Archmastery] BonusRating.
    /// Creatures have none.
    /// </summary>
    internal float ArchmasteryRating {
        get {
            if (!s_archmastery || _wizard is null || ParticipantGameStats is null) {
                return 0;
            }

            return _archmasteryRating ??= ComputeArchmasteryRating();
        }
    }

    private float ComputeArchmasteryRating() {
        try {
            var school = (MagicSchool) ParticipantGameStats.m_schoolID;
            var level = ParticipantGameStats.Level;
            var rating = Imlight.CoreLib.Shared.Character.MagicLevelsConfig.GetPlayerLevelInfo(school, level)?.m_archmastery ?? 0f;
            // A roguelike run gives Archmastery at every level, like AllLevels.
            var allLevels = s_archmasteryAllLevels
                            || (Imlight.CoreLib.Game.Runs.RoguelikeBoosts.RunArchmastery
                                && Imlight.CoreLib.Game.Runs.RoguelikeBoosts.OnRun(_wizard));
            if (rating <= 0 && (level >= ARCHMASTERY_LEVEL || allLevels)) {
                // The level table may have no number for this level; the level 50 one stands in.
                rating = Imlight.CoreLib.Shared.Character.MagicLevelsConfig.GetPlayerLevelInfo(school, ARCHMASTERY_LEVEL)?.m_archmastery ?? 0f;
                if (rating <= 0) {
                    rating = 1f;
                }
            }

            if (level < ARCHMASTERY_LEVEL && !allLevels) {
                rating = 0;
            }

            rating += s_archmasteryBonus;

            // A roguelike run's Potions of Mastery.
            rating += Imlight.CoreLib.Game.Runs.RoguelikeBoosts.Get(_wizard, Imlight.CoreLib.Game.Runs.RoguelikeBoosts.Archmastery);
            Logger.Debug("Archmastery for {0} (level {1}): {2}.", Logger.Args(_wizard.CharId, level, rating));

            return rating;
        }
        catch (Exception e) {
            Logger.Error("Archmastery could not be worked out for {0}: {1}", Logger.Args(_wizard.CharId, e.Message));

            return 0;
        }
    }

    private void ArchmasteryRound(float highest) {
        var (fillMax, powerPipCost) = ArchmasteryNumbers();
        var share = highest > 0 ? Math.Clamp(ArchmasteryRating / highest, 0f, 1f) : 1f;
        _archmasteryPower = Math.Min(ARCHMASTERY_MAX_POWER, _archmasteryPower + fillMax * share);

        // Power pips turn into school pips first, a point of power each.
        var pipCount = CombatParticipant.m_pipCount;
        while (_archmasteryPower >= 1f && pipCount.m_powerPips > 0 && SchoolPipTarget() is { } school) {
            pipCount.m_powerPips--;
            SetSchoolPips(pipCount, school, (byte) (SchoolPips(pipCount, school)!.Value + 1));
            _archmasteryPower -= 1f;
        }

        // Then, with enough power left, a white pip turns into a power pip.
        if (_archmasteryPower >= powerPipCost && pipCount.m_genericPips > 0) {
            pipCount.m_genericPips--;
            pipCount.m_powerPips++;
            _archmasteryPower -= powerPipCost;
        }
    }

    /// <summary>
    /// The fastest fill per round and the power a white pip costs, in school pips (1 = one school pip),
    /// from the game's WizStatisticEffectConfig when it has them.
    /// </summary>
    private static (float FillMax, float PowerPipCost) ArchmasteryNumbers() {
        var config = Imlight.CoreLib.Game.Effects.WizStatisticEffectConfigLoader.Config;
        var cost = config?.m_archmasteryConversionCost ?? 0f;
        var fill = s_archmasteryFillOverride > 0 ? s_archmasteryFillOverride
            : cost > 0 && config.m_archmasteryFillRateMax > 0 ? Math.Min(1f, config.m_archmasteryFillRateMax / cost)
            : DEFAULT_ARCHMASTERY_FILL;
        var powerPipCost = cost > 0 && config.m_archmasteryPowerPipCost > 0
            ? config.m_archmasteryPowerPipCost / cost
            : DEFAULT_POWER_PIP_COST;

        return (fill, powerPipCost);
    }

    /// <summary>
    /// Fills in what the client needs to lay out shadow pip slots and draw the shadow gauge. Wizards get
    /// shadow magic unlocked whenever shadow pips are on.
    /// </summary>
    internal void ApplyPipGauges(WizGameStats stats) {
        if (stats is null || !HasShadowMagic) {
            return;
        }

        var max = MaxShadowPips;
        stats.m_shadowMagicUnlocked = true;
        stats.m_shadowPipMax = max >= NO_SHADOW_LIMIT ? SHOWN_SHADOW_SLOTS_WITHOUT_LIMIT : max;
        stats.m_shadowPipRating = ShadowPipChance();
        stats.m_shadowPipRateAccumulated = _shadowGauge;
        stats.m_shadowPipRateThreshold = GAUGE_FULL;
        stats.m_shadowPipRatePercentage = (int) _shadowGauge;
    }

    /// <summary>
    /// Shown shadow pip slots in the settings the server uses, for the login stats.
    /// </summary>
    internal static int ShownPlayerShadowSlots
        => s_playerMaxShadowPips >= NO_SHADOW_LIMIT ? SHOWN_SHADOW_SLOTS_WITHOUT_LIMIT : s_playerMaxShadowPips;

    internal static bool ShadowPipsOn => s_shadowPips;

    // A wizard who used .intothedarkness has shadow magic of their own, whatever the toggles say.
    private bool HasOwnShadowMagic => _wizard is not null && ParticipantGameStats?.m_shadowMagicUnlocked == true;

    private bool HasShadowMagic => _wizard is null ? s_shadowPips && CreatureHasShadowMagic : s_shadowPips || HasOwnShadowMagic;

    private static readonly MagicSchool[] s_pipSchools = [
        MagicSchool.Balance, MagicSchool.Death, MagicSchool.Fire, MagicSchool.Ice,
        MagicSchool.Life, MagicSchool.Myth, MagicSchool.Storm,
    ];

    private static byte? SchoolPips(PipCount pips, MagicSchool school) => school switch {
        MagicSchool.Balance => pips.m_balancePips,
        MagicSchool.Death   => pips.m_deathPips,
        MagicSchool.Fire    => pips.m_firePips,
        MagicSchool.Ice     => pips.m_icePips,
        MagicSchool.Life    => pips.m_lifePips,
        MagicSchool.Myth    => pips.m_mythPips,
        MagicSchool.Storm   => pips.m_stormPips,
        _ => null,
    };

    private static void SetSchoolPips(PipCount pips, MagicSchool school, byte value) {
        switch (school) {
            case MagicSchool.Balance: pips.m_balancePips = value; break;
            case MagicSchool.Death:   pips.m_deathPips = value; break;
            case MagicSchool.Fire:    pips.m_firePips = value; break;
            case MagicSchool.Ice:     pips.m_icePips = value; break;
            case MagicSchool.Life:    pips.m_lifePips = value; break;
            case MagicSchool.Myth:    pips.m_mythPips = value; break;
            case MagicSchool.Storm:   pips.m_stormPips = value; break;
        }
    }

    // The school pips a spell costs, school by school.
    private static IEnumerable<(MagicSchool School, byte Cost)> SchoolPipCosts(SpellRank rank) {
        if (rank is null) {
            yield break;
        }

        yield return (MagicSchool.Balance, rank.m_balancePips);
        yield return (MagicSchool.Death, rank.m_deathPips);
        yield return (MagicSchool.Fire, rank.m_firePips);
        yield return (MagicSchool.Ice, rank.m_icePips);
        yield return (MagicSchool.Life, rank.m_lifePips);
        yield return (MagicSchool.Myth, rank.m_mythPips);
        yield return (MagicSchool.Storm, rank.m_stormPips);
    }

    /// <summary>
    /// Whether the participant has the school pips a spell costs.
    /// </summary>
    internal bool HasSchoolPipsForSpell(Spell spell)
        => (AnyPipPaysSchoolCosts
               // A roguelike run: any school pip, then any power pip, pays a school pip cost.
               ? s_pipSchools.Sum(school => SchoolPips(CombatParticipant.m_pipCount, school) ?? 0)
                 + CombatParticipant.m_pipCount.m_powerPips
                 >= SchoolPipCosts(spell.m_pipCost).Sum(c => c.Cost)
           : s_anySchoolPips
               ? s_pipSchools.Sum(school => SchoolPips(CombatParticipant.m_pipCount, school) ?? 0)
                 >= SchoolPipCosts(spell.m_pipCost).Sum(c => c.Cost)
               : SchoolPipCosts(spell.m_pipCost).All(c => c.Cost == 0 || (SchoolPips(CombatParticipant.m_pipCount, c.School) ?? 0) >= c.Cost))
           && (spell.m_pipCost is null || CombatParticipant.m_pipCount.m_shadowPips >= spell.m_pipCost.m_shadowPips);

    // [Battle] AnySchoolPips: a school pip of any school pays for any school's pip cost.
    private static readonly bool s_anySchoolPips = ConfigurationManager.GetValue("Battle.AnySchoolPips", false);

    // [Roguelike] AnyPipForSchoolPips: on a run, any school pip and then any power pip pays a school pip cost, so
    // the Archmastery cards that cost school pips can be cast.
    private static readonly bool s_runAnyPip = ConfigurationManager.GetValue("Roguelike.AnyPipForSchoolPips", true);

    private bool AnyPipPaysSchoolCosts => s_runAnyPip && Imlight.CoreLib.Game.Runs.RoguelikeBoosts.OnRun(_wizard);

    /// <summary>
    /// On a run any school pip pays any school's cost, but the game client only lets a card be picked when the
    /// pips it shows match the card's schools. So before the pips are sent, the wizard's school pips are given the
    /// schools their hand asks for: the card that needs the most school pips first, then the next, as far as the
    /// pips go. Whatever is left keeps the school most of the pips had.
    /// </summary>
    internal void SuitSchoolPipsToHand() {
        if (!AnyPipPaysSchoolCosts || _combatDeck?.LastGivenHand is not { Count: > 0 } hand) {
            return;
        }

        var pips = CombatParticipant.m_pipCount;
        var total = s_pipSchools.Sum(school => SchoolPips(pips, school) ?? 0);
        if (total == 0) {
            return;
        }

        var leftoverSchool = s_pipSchools.OrderByDescending(school => SchoolPips(pips, school) ?? 0).First();
        var assigned = s_pipSchools.ToDictionary(school => school, _ => 0);
        var used = 0;

        var needs = hand
            .Where(spell => spell?.m_pipCost is not null && !CombatActionResolver.IsXPipSpell(spell))
            .Select(spell => SchoolPipCosts(spell.m_pipCost).Where(c => c.Cost > 0)
                .GroupBy(c => c.School).ToDictionary(g => g.Key, g => g.Sum(c => (int) c.Cost)))
            .Where(need => need.Count > 0)
            .OrderByDescending(need => need.Values.Sum());

        foreach (var need in needs) {
            var extra = need.Sum(kv => Math.Max(0, kv.Value - assigned[kv.Key]));
            if (extra == 0 || used + extra > total) {
                continue;
            }

            foreach (var (school, cost) in need) {
                assigned[school] = Math.Max(assigned[school], cost);
            }

            used += extra;
        }

        if (used == 0) {
            return;
        }

        assigned[leftoverSchool] += total - used;
        foreach (var school in s_pipSchools) {
            SetSchoolPips(pips, school, (byte) assigned[school]);
        }
    }

    /// <summary>
    /// Spends the school pips a spell costs.
    /// </summary>
    internal void DeductSchoolPips(Spell spell) {
        var pipCount = CombatParticipant.m_pipCount;
        if (spell.m_pipCost is { m_shadowPips: > 0 } rank) {
            pipCount.m_shadowPips = (byte) Math.Max(0, pipCount.m_shadowPips - rank.m_shadowPips);
        }

        var unpaid = 0;
        foreach (var (school, cost) in SchoolPipCosts(spell.m_pipCost)) {
            if (cost > 0 && SchoolPips(pipCount, school) is { } have) {
                SetSchoolPips(pipCount, school, (byte) Math.Max(0, have - cost));
                unpaid += Math.Max(0, cost - have);
            }
        }

        // With AnySchoolPips, what the spell's own schools could not pay comes from the other schools, the
        // fullest first.
        while ((s_anySchoolPips || AnyPipPaysSchoolCosts) && unpaid > 0) {
            var fullest = s_pipSchools.OrderByDescending(school => SchoolPips(pipCount, school) ?? 0).First();
            var have = SchoolPips(pipCount, fullest) ?? 0;
            if (have == 0) {
                break;
            }

            var take = Math.Min(have, unpaid);
            SetSchoolPips(pipCount, fullest, (byte) (have - take));
            unpaid -= take;
        }

        // On a run, power pips pay whatever is left.
        if (AnyPipPaysSchoolCosts && unpaid > 0 && pipCount.m_powerPips > 0) {
            var take = Math.Min((int) pipCount.m_powerPips, unpaid);
            pipCount.m_powerPips = (byte) (pipCount.m_powerPips - take);
        }
    }

    internal bool HasSchoolMastery(uint magicSchoolID) {
        if (ParticipantGameStats.m_schoolID == magicSchoolID) {
            return true;
        }

        return (MagicSchool) magicSchoolID switch {
            MagicSchool.Storm   => ParticipantGameStats.m_stormMastery   > 0,
            MagicSchool.Fire    => ParticipantGameStats.m_fireMastery    > 0,
            MagicSchool.Ice     => ParticipantGameStats.m_iceMastery     > 0,
            MagicSchool.Myth    => ParticipantGameStats.m_mythMastery    > 0,
            MagicSchool.Life    => ParticipantGameStats.m_lifeMastery    > 0,
            MagicSchool.Death   => ParticipantGameStats.m_deathMastery   > 0,
            MagicSchool.Balance => ParticipantGameStats.m_balanceMastery > 0,
            _ => false,
        };
    }

    internal bool HasSchoolMastery(string school) {
        if (Enum.TryParse<MagicSchool>(school, out var magicSchool)) {
            return HasSchoolMastery((uint)magicSchool);
        }

        Logger.Warning("Failed to parse magic school \"{0}\" from string.", Logger.Args(school));

        return false;
    }

    internal T GetStatBySchool<T>(List<T> list, string magicSchool) {
        if (list is null) {
            return default;
        }
        if (!typeof(T).IsPrimitive && !typeof(T).IsEnum) {
            throw new ArgumentException("List items must be primitive types or enums");
        }

        // Check if we're over the max index.
        var maxIndex = MagicSchools.GetMaxMagicSchoolIndex();
        if (list.Count < maxIndex) {
            throw new ArgumentException("List must have a count equal to the max magic school index.");
        }

        var index = (int) MagicSchools.GetMagicSchool(magicSchool).m_schoolIndex;

        return list[index];
    }

    internal bool HasPipsForSpell(Spell spell) {
        // X-pip spells scale to any pip count; no fixed minimum.
        if (CombatActionResolver.IsXPipSpell(spell)) {
            return true;
        }

        var spellRank = spell.m_pipCost.m_spellRank;
        var genericPips = CombatParticipant.m_pipCount.m_genericPips;
        var powerPips = CombatParticipant.m_pipCount.m_powerPips;
        var isMastered = HasSchoolMastery(spell.m_magicSchoolID);

        // Power pips count as 2 generic pips if the spell is mastered.
        var totalPips = isMastered
            ? genericPips + (powerPips * 2)
            : genericPips + powerPips;

        // On a run the game lets school pips left over after the spell's school cost pay its rank, like power pips
        // (a card it offers would otherwise be cast as a pass).
        if (AnyPipPaysSchoolCosts && spell.m_pipCost is not null) {
            var pips = CombatParticipant.m_pipCount;
            var leftover = s_pipSchools.Sum(school => SchoolPips(pips, school) ?? 0)
                           - SchoolPipCosts(spell.m_pipCost).Sum(c => c.Cost);
            if (leftover < 0) {
                // Power pips paid the rest of the school cost.
                totalPips -= (isMastered ? 2 : 1) * Math.Min(powerPips, -leftover);
            }
            else {
                totalPips += (isMastered ? 2 : 1) * leftover;
            }
        }

        return totalPips >= spellRank && HasSchoolPipsForSpell(spell) && HasManaForSpell(spell);
    }

    internal bool TryStun() {
        // todo: check if this creature can be stunned.
        CombatParticipant.m_stunned = 1;

        return true;
    }

    internal void DamageParticipant(int damage) {
        // If the participant is a player, update their health.
        if (_wizard is not null && !IsSandboxed) {
            var currentHealth = ParticipantGameStats.m_currentHitpoints;
            var newHealth = currentHealth - damage;

            // Make sure the health doesn't go below 0.
            if (newHealth < 0) {
                newHealth = 0;
            }

            // Update the health of the player.
            _wizard.UpdateHealth(newHealth);
        }
        else {
            // Make sure the health doesn't go below 0.
            if (ParticipantGameStats.m_currentHitpoints - damage < 0) {
                ParticipantGameStats.m_currentHitpoints = 0;
            }
            else {
                ParticipantGameStats.m_currentHitpoints -= damage;
            }
        }
    }

    internal void HealParticipant(int heal) {
        // Healing never brings a defeated participant back.
        if (!IsAlive) {
            return;
        }

        // If the participant is a player, update their health.
        if (_wizard is not null && !IsSandboxed) {
            var currentHealth = ParticipantGameStats.m_currentHitpoints;
            var newHealth = currentHealth + heal;

            // Make sure the health doesn't go above the max health.
            if (newHealth > ParticipantGameStats.m_baseHitpoints) {
                newHealth = ParticipantGameStats.m_baseHitpoints;
            }

            // Update the health of the player.
            _wizard.UpdateHealth(newHealth);
        }
        else {
            // Make sure the health doesn't go above the max health.
            if (ParticipantGameStats.m_currentHitpoints + heal > ParticipantGameStats.m_baseHitpoints) {
                ParticipantGameStats.m_currentHitpoints = ParticipantGameStats.m_baseHitpoints;
            }
            else {
                ParticipantGameStats.m_currentHitpoints += heal;
            }
        }
    }

    // [Battle] UnlimitedMana: spells cost no mana, and mana refills in the Commons.
    internal static readonly bool UnlimitedMana = ConfigurationManager.GetValue("Battle.UnlimitedMana", false);

    internal void DeductMana(int mana) {
        // If the participant is a player, update their mana.
        // Creature's don't have mana.
        if (_wizard is null || UnlimitedMana || mana <= 0) {
            return;
        }

        var currentMana = ParticipantGameStats.m_currentMana;
        var newMana = Math.Max(0, currentMana - mana);

        // Update the mana of the player, and the mana globe on their screen. A PvP duel only changes its copy.
        if (IsSandboxed) {
            ParticipantGameStats.m_currentMana = newMana;
        }
        else {
            _wizard.UpdateMana(newMana);
        }
        ParticipantActor.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEMANA {
            Mana = newMana,
            MaxMana = ParticipantGameStats.GetClientTypeAlternative().m_baseMana,
            DisplayDiff = 1,
        });
    }

    /// <summary>
    /// A wizard needs at least as much mana as the spell's pip cost to cast it (unless UnlimitedMana is on).
    /// </summary>
    private bool HasManaForSpell(Spell spell)
        => _wizard is null || UnlimitedMana || ParticipantGameStats.m_currentMana >= spell.m_pipCost.m_spellRank;

    internal void DeductPips(MagicSchool school, byte spellRank, bool fullPipConserve = false) {
        var isMastered = HasSchoolMastery((uint) school);
        var pipCount = CombatParticipant.m_pipCount;

        // Deduct pips based on the spell rank.
        // We have a second conditional here incase of byte overflow.
        while (spellRank is > 0 and < (MAX_PIP_COUNT * 2)) {
            if (isMastered && pipCount.m_powerPips > 0) {
                pipCount.m_powerPips--;

                // A power pip is worth 2 here; paying the last 1 with it wastes half. Pip conserve
                // (100% with FullPipConserve) hands that half back as a normal pip.
                if (spellRank == 1) {
                    if (fullPipConserve) {
                        pipCount.m_genericPips++;
                    }

                    break;
                }

                spellRank -= 2;
            }
            else if (!isMastered && pipCount.m_powerPips > 0) {
                pipCount.m_powerPips--;
                spellRank--;
            }
            else if (pipCount.m_powerPips == 0 && pipCount.m_genericPips > 0) {
                pipCount.m_genericPips--;
                spellRank--;
            }
            else if (pipCount.m_powerPips == 0 && pipCount.m_genericPips == 0) {
                // On a run, school pips left after the school cost pay the rest, like power pips.
                var fullest = AnyPipPaysSchoolCosts
                    ? s_pipSchools.OrderByDescending(s => SchoolPips(pipCount, s) ?? 0).First()
                    : (MagicSchool?) null;
                if (fullest is not { } fromSchool || (SchoolPips(pipCount, fromSchool) ?? 0) == 0) {
                    break;
                }

                SetSchoolPips(pipCount, fromSchool, (byte) (SchoolPips(pipCount, fromSchool)!.Value - 1));
                spellRank = (byte) Math.Max(0, spellRank - (isMastered ? 2 : 1));
            }
        }
    }

    internal void DeductAllPips() {
        var ourPipCount = CombatParticipant.m_pipCount;
        ourPipCount.m_powerPips = 0;
        ourPipCount.m_genericPips = 0;
        ourPipCount.m_shadowPips = 0;
        foreach (var school in s_pipSchools) {
            SetSchoolPips(ourPipCount, school, 0);
        }
    }

    internal void Reshuffle() => _combatDeck.Reshuffle();

    /// <summary>
    /// Draws a random treasure card from the vault and adds it to the current hand.
    /// </summary>
    /// <returns>The drawn spell, or null if no vault cards available or hand is full.</returns>
    internal Spell DrawFromVault() => _combatDeck.DrawFromVault();

    /// <summary>
    /// Gets the number of treasure cards remaining in the vault.
    /// </summary>
    internal int VaultRemainingCount => _combatDeck.VaultRemainingCount;

    /// <summary>
    /// Gets the number of treasure cards currently in the hand.
    /// </summary>
    internal int TreasureCardsInHand => _combatDeck.TreasureCardsInHand;

    /// <summary>
    /// Permanently consumes a successfully cast treasure card from the vault.
    /// </summary>
    internal uint ConsumeFromVault(Spell spell) => _combatDeck.ConsumeFromVault(spell);

    private void InitializePlayerSubCircle() {
        // todo: this method is a mess.
        var queryCharacterMsg = new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD();
        _wizard = ParticipantActor
            .Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(queryCharacterMsg)
            .Result
            .Wizard;
        _deckArchmasterySchool = DeckArchmasterySchool();
        if (_deckArchmasterySchool != 0) {
            Logger.Debug("Deck school pip gauge school: {0}.", Logger.Args(_deckArchmasterySchool));
        }

        IsSandboxed = _duelActor.Duel.m_bPVP;
        ParticipantGameStats = IsSandboxed ? _wizard.GameStats.CopyForDuel() : _wizard.GameStats;
        var combatStats = ParticipantGameStats.GetCombatGameStats();

        // Collage spells the player has learned and temporary spells (perhaps from equipment)
        // into one list to create the combat hand. Treasure cards go into a separate vault.
        var allSpells = new List<CombatDeckSpellData>();
        var vaultSpells = new List<CombatDeckSpellData>();
        if (_wizard.SpellbookBehavior.SpellList is not null) {
            foreach (var spell in _wizard.SpellbookBehavior.SpellList) {
                var template = CoreObjectFactory.GetCoreTemplate(spell.m_templateID);
                var isTreasureCard = template is SpellTemplate spellTemplate
                    && (spellTemplate.m_Treasure || ((string)spellTemplate.m_name).EndsWith(" TC"));

                // A deck entry the player hasn't learned is a treasure card if they own it as one (item cards
                // live in TemporarySpells). Spells a starter deck comes with are never learned, and must stay
                // regular spells even once the player has learned others (such as randomizer starting spells).
                if (!isTreasureCard
                    && _wizard.SpellbookBehavior.LearnedSpellTemplateIds is { Count: > 0 }
                    && !_wizard.SpellbookBehavior.LearnedSpellTemplateIds.Contains(spell.m_templateID)
                    && _wizard.SpellbookBehavior.TreasureCardCount(spell.m_templateID) > 0) {
                    isTreasureCard = true;
                }

                if (isTreasureCard) {
                    // Treasure cards go to the vault (separate pool, drawn on demand).
                    vaultSpells.Add(new CombatDeckSpellData {
                        TemplateId = spell.m_templateID,
                        Quantity = spell.m_quantity,
                        IsTreasureCard = true
                    });
                }
                else {
                    // Regular spells go to the main deck.
                    allSpells.Add(new CombatDeckSpellData {
                        TemplateId = spell.m_templateID,
                        Quantity = spell.m_quantity,
                    });
                }
            }
        }

        // Count temporary spells as 1 quantity, skipping any that the player
        // has excluded via the spell deck UI (MSG_UPDATEITEMSPELLEXCLUSIONLIST).
        var temporarySpells = new List<CombatDeckSpellData>();
        var equippedDeckId = _wizard.EquipmentBehavior.SlotList
            .FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck)?.ItemId;
        var skipped = new Dictionary<uint, int>();
        foreach (var tempSpell in _wizard.SpellbookBehavior.TemporarySpells) {
            // Skip as many copies of this item spell as are crossed out for the equipped deck.
            if (equippedDeckId != null) {
                skipped.TryGetValue(tempSpell.m_templateID, out var done);
                if (done < _wizard.SpellbookBehavior.ExcludedItemSpellCount(equippedDeckId.Value, tempSpell.m_templateID)) {
                    skipped[tempSpell.m_templateID] = done + 1;

                    continue;
                }
            }

            // Randomizer mode may swap each gear card for another spell of the same type and pip cost.
            var itemCardId = Randomization.Randomizer.RollItemCard(tempSpell.m_templateID);

            // If the spell data already exists, increase the quantity.. otherwise add it.
            var existingSpell = temporarySpells.Find(s => s.TemplateId == itemCardId);
            if (existingSpell is not null) {
                existingSpell.Quantity++;
            }
            else {
                temporarySpells.Add(new CombatDeckSpellData {
                    TemplateId = itemCardId,
                    Quantity = 1,
                    IsItemCard = true
                });
            }
        }
        allSpells.AddRange(temporarySpells);
        Logger.Information("Battle deck for {0}: deck {1}, {2} item cards from gear, {3} left out as crossed out ({4}); saved crossed out: {5}.",
            Logger.Args(_wizard.CharId, equippedDeckId, _wizard.SpellbookBehavior.TemporarySpells.Count, skipped.Values.Sum(),
                string.Join(", ", skipped.Select(pair => $"{pair.Key} x{pair.Value}")),
                string.Join("; ", _wizard.SpellbookBehavior.ExcludedItemSpellIds.Select(pair => $"deck {pair.Key}: {string.Join(", ", pair.Value)}"))));
        _combatDeck = new CombatDeck(allSpells, vaultSpells, PLAYER_HAND_SIZE);

        CombatParticipant = new CombatParticipant {
            m_ownerID = ParticipantObject.m_globalID,
            m_templateID = 219902325553, // recorded from live
            m_isPlayer = true,
            m_zoneID = _duelActor.SigilId,
            m_isMonster = 0,
            m_teamID = (int) OccupiedTeam,
            m_primaryMagicSchoolID = (int) _wizard.MagicSchoolBehavior.MagicSchool,
            m_pipCount = DetermineStartingPips(),
            m_pipRoundRates = new(),
            m_originalTeam = (int) OccupiedTeam,
            m_maxHandSize = PLAYER_HAND_SIZE,
            m_playerHealth = ParticipantGameStats.m_currentHitpoints,
            m_maxPlayerHealth = ParticipantGameStats.m_baseHitpoints,
            // The client's crit sim reads the participant level from m_mobLevel (it zeroes its
            // crit chance below the level threshold when this is missing).
            m_mobLevel = ParticipantGameStats.Level,
            m_myTeamTurn = _duelActor.Duel.m_firstTeamToAct == (int) OccupiedTeam,
            m_pGameStats = combatStats,
            m_pPlayDeck = new PlayDeck(),
            m_subcircle = SlotIndex,
            m_dynamicSymbol = DynamicSymbol,
            m_PipsSuspended = false,
            m_shadowPipRateThreshold = GAUGE_FULL,
            m_maxArchmasteryPoints = GAUGE_FULL,
            m_archmasterySchool = ArchmasterySchool,
            m_pvp = IsSandboxed,

            m_color = _color,
            m_rotation = _rotation,
            m_radius = _radius,
        };
        ApplyPipGauges(CombatParticipant.m_pGameStats);
    }

    private void InitializeCreatureSubCircle(bool asMinion = false, int minionOwnerSubCircle = 0) {
        var queryGameStatsMsg = new COMBAT_106_PROTOCOL.MSG_QUERYCREATURESTATS();
        var creatureStats = ParticipantActor
            .Ask<COMBAT_106_PROTOCOL.MSG_CREATURESTATS>(queryGameStatsMsg)
            .Result;

        // Convert the creature stats to a combat deck.
        var spellData = new List<CombatDeckSpellData>();
        foreach (var spell in creatureStats.SpellList) {
            if (spell is null) {
                continue;
            }

            spellData.Add(new CombatDeckSpellData {
                TemplateId = spell.m_templateID,
                Quantity = spell.m_quantity
            });
        }

        _combatDeck = new CombatDeck(spellData, [], PLAYER_HAND_SIZE);

        ParticipantGameStats = creatureStats.GameStats;
        CombatParticipant = new CombatParticipant {
            m_ownerID = ParticipantObject.m_globalID,
            m_templateID = 2199023290637, // Captured 2199023290637 from live
            m_isPlayer = false,
            m_zoneID = _duelActor.SigilId,
            m_isMonster = 0, // Live server sends 0
            // Minions are creatures on the player team; m_isPlayer stays false.
            m_teamID = (int) OccupiedTeam,
            m_originalTeam = asMinion ? (int) OccupiedTeam : 0,
            m_isMinion = asMinion,
            m_maxHandSize = PLAYER_HAND_SIZE,
            m_primaryMagicSchoolID = (int) creatureStats.MagicSchool,
            m_pipCount = DetermineStartingPips(),
            m_pipRoundRates = new(),
            m_playerHealth = creatureStats.GameStats.m_currentHitpoints,
            m_maxPlayerHealth = creatureStats.GameStats.m_baseHitpoints,
            // todo: a PvE minion has always been told it acts with the creatures, is that right?
            m_myTeamTurn = _duelActor.Duel.m_firstTeamToAct == (int) (_duelActor.Duel.m_bPVP ? OccupiedTeam : CombatTeam.Monster),
            m_pGameStats = creatureStats.GameStats.GetCombatGameStats(),
            m_mobLevel = creatureStats.CombatLevel,

            m_minionStartingHealth = asMinion ? creatureStats.GameStats.m_currentHitpoints : 0,
            m_curMaxHP = asMinion ? creatureStats.GameStats.m_baseHitpoints : 0,
            // The client links a minion to its owner through this sub-circle.
            m_minionSubCircle = asMinion ? minionOwnerSubCircle : 0,

            m_subcircle = SlotIndex,
            m_dynamicSymbol = (DynamicSigilSymbol) (SlotIndex + 1),
            m_pvp = _duelActor.Duel.m_bPVP,

            m_color = _color,
            m_rotation = _rotation,
            m_radius = _radius,
        };
        ApplyPipGauges(CombatParticipant.m_pGameStats);
    }

    private async Task PlayEntranceAnimation(CoreObject participantObject, IActorRef participantActor) {
        // Send the "Sigil" state to the zone so the client transitions the object.
        _duelActor.ZoneBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = participantObject.m_globalID,
            State = StringHash.Compute("Sigil")
        });

        // Send aggro to the participant.
        _duelActor.ZoneBroadcast(new WIZARD_12_PROTOCOL.MSG_AGGRO {
            GlobalID = participantObject.m_globalID,
            LocX = WorldPosition.X,
            LocY = WorldPosition.Y,
            LocZ = WorldPosition.Z,
            Yaw = WorldRotation,
            SigilGID = _duelActor.SigilId
        });

        // Set the actual position of the game object to the sigil.
        participantObject.m_location = new Vector3(WorldPosition.X, WorldPosition.Y, WorldPosition.Z);
        participantObject.m_orientation = new Vector3(0, 0, WorldRotation);

        // Wait the amount of time it takes for the actor to enter the sigil, then set
        // their state to stationary.
        await Task.Delay((int) (AGGRO_TIME_IN_SECONDS * 1000));

        // Broadcast the "Stationary" state to the zone so the client knows the
        // entrance animation is complete and the participant is now at the sigil.
        _duelActor.ZoneBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = participantObject.m_globalID,
            State = StringHash.Compute("Stationary")
        });
    }

    // The circles of the creature side show the symbols 1 to 4 and the others 13 to 16, whoever stands on them.
    private DynamicSigilSymbol DynamicSymbol
        => (DynamicSigilSymbol) (SlotType == CombatSlotType.Creature ? SlotIndex + 1 : SlotIndex + 9);

    private PipCount DetermineStartingPips() {
        var pipCount = new PipCount() {
            m_powerPips = ParticipantGameStats.m_startingPowerPips,
            m_genericPips = ParticipantGameStats.m_startingPips
        };

        // Ensure that the total number of pips does not exceed MAX_PIP_COUNT.
        if (pipCount.m_genericPips + pipCount.m_powerPips > MAX_PIP_COUNT) {
            int excessPips = pipCount.m_genericPips + pipCount.m_powerPips - MAX_PIP_COUNT;

            // Reduce generic pips first if there is an excess
            if (excessPips <= pipCount.m_genericPips) {
                pipCount.m_genericPips -= (byte) excessPips;
            }
            else {
                // If excess pips are more than generic pips, set generic pips to 0
                // and adjust power pips accordingly
                excessPips -= pipCount.m_genericPips;
                pipCount.m_genericPips = 0;
                pipCount.m_powerPips -= (byte) excessPips;
            }
        }

        return pipCount;
    }

    private bool DeterminePowerPipGain(CombatParticipant participant) {
        var stats = participant.m_pGameStats;
        var powerPipChance = 100 * (stats.m_powerPipBase + stats.m_powerPipBonusPercentAll);

        var powerPipRoll = Random.Shared.Next(0, 100);
        return powerPipRoll <= powerPipChance;
    }

}
