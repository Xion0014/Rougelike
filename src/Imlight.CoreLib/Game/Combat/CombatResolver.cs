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
 * COMBAT ACTION RESOLUTION SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Serves as the entry point for resolving all combat actions, coordinating
 * the sequence of spells cast during a round and implementing the core
 * mathematical resolution of combat effects.
 * 
 * USAGE EXAMPLE:
 * var resolver = new CombatResolver(duel, subCircles);
 * resolver.Reset();
 * resolver.AddCombatMove(CombatMoveType.Attack, caster, target, spell);
 * float cinematicTime = resolver.ApplyQueuedCombatActions(out combatActionListObj);
 * 
 * NOTE:
 * This system works in conjunction with several specialized combat classes:
 * 
 * - CombatDuelComponent:    Orchestrates the overall duel, manages participants and phases
 * - CombatResolver:         Processes and resolves combat actions during the execution phase
 * - CombatDuelSubCircle:    Handles individual participant state and position
 * - CombatActionResolver:   Processes queued actions and resolves target selection
 * - CombatEffectApplicator: Applies spell effects with proper modifications
 * - CombatCharms:           Manages offensive modifiers that affect outgoing damage/healing
 * - CombatWards:            Handles defensive modifiers that affect incoming damage
 * - CombatDeck:             Controls spell deck management, drawing and discarding
 * - CombatEffectStack:      Tracks random/variable effect selection using bit-packing
 * 
 * The resolver determines hit/fizzle mechanics, processes spell accuracy,
 * manages the execution order, and coordinates timing of animations.
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Spells;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Cryptography;

namespace Imlight.CoreLib.Game.Combat;

public class QueuedCombatAction {
    
    public CombatDuelSubCircle SpellCaster;
    public CombatDuelSubCircle SelectedTarget;
    public Spell Spell;
    public SpellTemplate SpellTemplate;
    
}

/// <summary>
/// Processes and resolves combat actions during the execution phase of a duel round.
/// </summary>
/// <remarks>
/// Responsible for determining the order of combat actions, handling fizzles, and coordinating 
/// the effects of spells on targets. Manages the flow of a combat turn by processing queued 
/// actions and calculating cinematic timing for visual feedback.
/// </remarks>
public class CombatResolver(Duel duel, CombatDuelSubCircle[] actorSubCircles) {
    
    private const int SPELL_FIZZLE_TIME = 4;

    // [Battle] ManaOnFizzle: a fizzled spell still costs its mana.
    // [Battle] FullPipConserve: a power pip only half used on a spell always comes back as a normal pip.
    private static readonly bool s_fullPipConserve = ConfigurationManager.GetValue("Battle.FullPipConserve", false);

    private static readonly bool s_manaOnFizzle = ConfigurationManager.GetValue("Battle.ManaOnFizzle", false);
    private const int SPELL_PASS_TIME = 1;
    private const float SPELL_CAST_TIME = 5.0f;
    private const float HANGING_EFFECT_CONSUME_TIME = 1.0f;
    private const float OVER_TIME_ACTIVATION_TIME = 2.0f;
    private const float DEATH_ANIMATION_TIME = 2.0f;

    private readonly Duel _duel = duel;

    private readonly CombatDuelSubCircle[] _subCircles = actorSubCircles;
    private CombatDuelSubCircle[] ActiveSubCircles => [.. _subCircles.Where(x => x.Occupied)];
    private List<QueuedCombatAction> _queuedCombatActions;

    // Daybreaker and Nightbringer: the spell is paid for (pips, card, fizzle check) when played, then casts on
    // the caster's next turn, at its start (Daybreaker) or its end (Nightbringer), even if they are stunned.
    private sealed record DelayedCast(QueuedCombatAction Action, bool AtStart, int Round);
    private readonly List<DelayedCast> _delayedCasts = [];

    /// <summary>
    /// The team that plans and acts now. When set, the other team queues no moves and is not made to pass. When
    /// <c>null</c>, every team acts in the same round.
    /// </summary>
    internal CombatTeam? ActingTeam { get; set; }

    public void Reset() =>
        _queuedCombatActions = [];

    /// <summary>
    /// Applies queued combat actions to the duel, processing each action in order.
    /// </summary>
    /// <param name="combatActionListObj">The object that will hold the list of combat actions.</param>
    /// <returns>The total cinematic time for the actions applied.</returns>
    public float ApplyQueuedCombatActions(out CombatActionListObj combatActionListObj) {
        Logger.Debug("Duel {0} | Applying combat actions..", 
            Logger.Args(_duel.m_duelID.Full, _duel.m_roundNum));

        combatActionListObj = new CombatActionListObj { m_actionList = [] };

        // Some subcircles may not have queued actions. Ensure they do by adding a pass action.
        AddCasterPassActionIfNeeded();
        SortQueuedActions();

        var cinematicTime = ProcessQueuedActions(combatActionListObj);

        return cinematicTime;
    }

    /// <summary>
    /// Adds a combat move to the queue for processing.
    /// </summary>
    /// <param name="type">The type of combat move (e.g., Attack, ChangeMind).</param>
    /// <param name="caster">The caster of the spell.</param>
    /// <param name="target">The target of the spell.</param>
    /// <param name="spell">The spell being cast.</param>
    public void AddCombatMove(CombatMoveType type,
                              CombatDuelSubCircle caster,
                              CombatDuelSubCircle target,
                              Spell spell) {
        if (!IsActing(caster)) {
            Logger.Debug("Duel {0} | Slot {1} | Move dropped, the other team is acting.",
                Logger.Args(_duel.m_duelID.Full, caster.SlotIndex));

            return;
        }

        // If this spell is already queued by the same caster, remove all of their queued actions.
        _queuedCombatActions.RemoveAll(x => x.SpellCaster == caster);

        if (type == CombatMoveType.ChangeMind) {
            // We can immediately return here. Anytime a caster doesn't have a queued action, they will pass their turn.
            return;
        }

        // Get the spell template.
        SpellTemplate spellTemplate = null;
        if (spell is not null) {
            spellTemplate = (SpellTemplate) CoreObjectFactory.GetCoreTemplate(spell.m_templateID);

            if (spellTemplate is null) {
                Logger.Error("Duel {0} | Slot {1} | Spell template {2} not found",
                    Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, spell.m_templateID));
                return;
            }

            // An enchanted card casts with the enchantment's changes.
            if (spell.m_enchantment != 0 && CoreObjectFactory.GetCoreTemplate(spell.m_enchantment) is SpellTemplate enchantment) {
                spellTemplate = CombatEnchantments.Apply(spellTemplate, enchantment, out _);
            }
        }

        var queuedAction = new QueuedCombatAction {
            SpellCaster = caster,
            Spell = type == CombatMoveType.Attack ? spell : null,
            SpellTemplate = spellTemplate,
            SelectedTarget = target
        };
        _queuedCombatActions.Add(queuedAction);

        LogQueuedCombatAction(type, caster, target, spell);
    }

    /// <summary>
    /// Checks if all participants have enqueued actions.
    /// </summary>
    /// <returns>True if all participants have enqueued actions, false otherwise.</returns>
    public bool HaveAllParticipantsEnqueuedActions() {
        // Every circle that still has to move must have a move of its own. Counting is not enough: a move
        // from a circle that is not expected (a dead player's client still passes) would stand in for
        // someone else's and end the round early, as if they had passed too.
        return _subCircles
            .Where(circle => circle.AddedToDuel && circle.IsAlive && IsActing(circle))
            .All(circle => _queuedCombatActions.Any(action => action.SpellCaster == circle));
    }

    /// <summary>The move a given caster has queued this round, or null if none. Used to re-broadcast a
    /// summoned minion's AI-chosen move at planning-phase start so the client telegraphs it.</summary>
    public QueuedCombatAction GetQueuedAction(CombatDuelSubCircle caster)
        => _queuedCombatActions.FirstOrDefault(a => a.SpellCaster == caster);

    private bool IsActing(CombatDuelSubCircle circle)
        => ActingTeam is null || circle.OccupiedTeam == ActingTeam;

    private void AddCasterPassActionIfNeeded() {
        var castersWithoutActions = ActiveSubCircles
            .Where(subCircle => subCircle.AddedToDuel && subCircle.IsAlive && IsActing(subCircle))
            .Except(_queuedCombatActions.Select(action => action.SpellCaster))
            .ToList();

        foreach (var subCircle in castersWithoutActions) {
            var queuedAction = new QueuedCombatAction {
                SpellCaster = subCircle,
                SelectedTarget = null,
                Spell = null
            };
            _queuedCombatActions.Add(queuedAction);
        }
    }

    private void SortQueuedActions() => _queuedCombatActions.Sort((a, b) => {
        var aSlot = a.SpellCaster.SlotIndex;
        var bSlot = b.SpellCaster.SlotIndex;

        var aTeam = (int) a.SpellCaster.OccupiedTeam;
        var bTeam = (int) b.SpellCaster.OccupiedTeam;

        // Check if both actions belong to the same team
        if (aTeam == bTeam) {
            // Within the same team, sort by slot index (ascending)
            return aSlot.CompareTo(bSlot);
        }
        else {
            // Teams are different, prioritize team who acts first
            if (aTeam == _duel.m_firstTeamToAct) {
                return -1; // Team a acts first
            }
            else if (bTeam == _duel.m_firstTeamToAct) {
                return 1; // Team b acts first
            }
            else {
                return 0; // This should not happen.
            }
        }
    });

    private float ProcessQueuedActions(CombatActionListObj combatActionList) {
        var cinematicTime = 0.0f;
        var instantCinematics = _subCircles[0]._duelActor.CheatInstantCinematics;

        _petCastsThisRound.Clear();
        PetCastCards.Clear();
        foreach (var action in _queuedCombatActions) {
            cinematicTime += WithPetMayCasts(() => CastDelayed(action.SpellCaster, atStart: true, combatActionList, cinematicTime),
                combatActionList, cinematicTime);
            cinematicTime += WithPetMayCasts(() => ProcessQueuedAction(action, combatActionList, cinematicTime),
                combatActionList, cinematicTime);
            cinematicTime += WithPetMayCasts(() => CastDelayed(action.SpellCaster, atStart: false, combatActionList, cinematicTime),
                combatActionList, cinematicTime);
        }

        return instantCinematics ? 0 : cinematicTime;
    }

    // ---------------------------------------------------------------------
    // Pet May Casts (ported from Niduus's fork; [PetCasting] in Imlight.ini)
    // ---------------------------------------------------------------------

    // How many May Casts each owner's pet has done this round ([PetCasting] MaxPerRound).
    private readonly Dictionary<CombatDuelSubCircle, int> _petCastsThisRound = [];

    /// <summary>
    /// This round's pet May Cast cards per owner. The game casts only cards it finds in the caster's hand, so the duel
    /// sends the owner a hand holding them before the round's actions (CombatDuelComponent).
    /// </summary>
    internal readonly Dictionary<CombatDuelSubCircle, List<Spell>> PetCastCards = [];

    /// <summary>
    /// Runs one step of the round, then lets pets May Cast for what happened in it: a spell cast on their side, a
    /// teammate taking damage or being healed, or a pass. A May Cast can set off more ([PetCasting] Chain).
    /// </summary>
    private float WithPetMayCasts(Func<float> step, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (!PetMayCasts.Enabled || PetMayCasts.MaxPerRound == 0) {
            return step();
        }

        var health = HealthNow();
        var before = combatActionList.m_actionList.Count;
        var cinematicTime = step();

        try {
            for (var link = 0; link <= PetMayCasts.MaxChain; link++) {
                var nextHealth = HealthNow();
                var nextBefore = combatActionList.m_actionList.Count;
                var time = PetMayCast(health, before, combatActionList, cinematicTimeSoFar + cinematicTime);
                if (time <= 0) {
                    break;
                }

                cinematicTime += time;
                if (!PetMayCasts.Chain) {
                    break;
                }

                health = nextHealth;
                before = nextBefore;
            }
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Pet May Cast failed: {1}", Logger.Args(_duel.m_duelID.Full, e));
        }

        return cinematicTime;
    }

    private bool BattleDecided()
        => _subCircles.Where(c => c.Occupied && c.AddedToDuel && c.IsAlive).Select(c => c.OccupiedTeam).Distinct().Count() < 2;

    private Dictionary<CombatDuelSubCircle, int> HealthNow()
        => _subCircles.Where(c => c.Occupied && c.AddedToDuel && c.ParticipantGameStats is not null)
            .ToDictionary(c => c, c => c.ParticipantGameStats.m_currentHitpoints);

    private float PetMayCast(Dictionary<CombatDuelSubCircle, int> health, int before, CombatActionListObj combatActionList,
                             float cinematicTimeSoFar) {
        var cast = new HashSet<CombatTeam>();
        var damaged = new HashSet<CombatTeam>();
        var healed = new HashSet<CombatTeam>();
        var passed = new HashSet<CombatTeam>();
        for (var i = before; i < combatActionList.m_actionList.Count; i++) {
            var done = combatActionList.m_actionList[i];
            if (done.m_spell is null
                    && _subCircles.FirstOrDefault(c => c.Occupied && c.SlotIndex == done.m_spellCaster) is { } passer) {
                passed.Add(passer.OccupiedTeam);
            }

            if (done.m_spell is not null && done.m_spellHits == (char) 1 && (PetMayCasts.Chain || !done.m_petCast)
                    && _subCircles.FirstOrDefault(c => c.Occupied && c.SlotIndex == done.m_spellCaster) is { } caster) {
                cast.Add(caster.OccupiedTeam);

                // A damage or heal spell landing on a teammate counts even when gear took all of it.
                var effects = (CoreObjectFactory.GetCoreTemplate(done.m_spell.m_templateID) as SpellTemplate)?.m_effects ?? [];
                var damages = effects.Any(e => e is not null && s_damageEffects.Contains(e.m_effectType));
                var heals = effects.Any(e => e is not null && s_healEffects.Contains(e.m_effectType));
                foreach (var slot in done.m_targetSubcircleList ?? []) {
                    if (_subCircles.FirstOrDefault(c => c.Occupied && c.SlotIndex == slot) is not { } hit) {
                        continue;
                    }

                    if (damages) {
                        damaged.Add(hit.OccupiedTeam);
                    }

                    if (heals) {
                        healed.Add(hit.OccupiedTeam);
                    }
                }
            }
        }

        foreach (var (circle, hitpoints) in health) {
            var now = circle.ParticipantGameStats?.m_currentHitpoints ?? hitpoints;
            if (now < hitpoints) {
                damaged.Add(circle.OccupiedTeam);
            }
            else if (now > hitpoints) {
                healed.Add(circle.OccupiedTeam);
            }
        }

        // Nothing happened, or the battle is already won or lost.
        if (cast.Count + damaged.Count + healed.Count + passed.Count == 0 || BattleDecided()) {
            return 0;
        }

        var cinematicTime = 0.0f;
        var owners = _subCircles.Where(c => c.Occupied && c.AddedToDuel && c.IsAlive && c._wizard is not null).ToList();
        foreach (var owner in owners) {
            var team = owner.OccupiedTeam;
            if (!cast.Contains(team) && !damaged.Contains(team) && !healed.Contains(team) && !passed.Contains(team)) {
                continue;
            }

            var used = _petCastsThisRound.GetValueOrDefault(owner);
            foreach (var mayCast in PetMayCasts.ForWizard(owner._wizard).OrderBy(_ => Random.Shared.Next())) {
                if (used >= PetMayCasts.MaxPerRound || !owner.IsAlive) {
                    break;
                }

                var chances = PetMayCasts.Chances(mayCast);
                var reason = cast.Contains(team) && PetMayCasts.Roll(chances.Cast) ? "a cast"
                    : damaged.Contains(team) && PetMayCasts.Roll(chances.Damage) ? "damage"
                    : healed.Contains(team) && PetMayCasts.Roll(chances.Heal) ? "a heal"
                    : passed.Contains(team) && PetMayCasts.Roll(chances.Pass) ? "a pass"
                    : null;
                if (reason is null) {
                    continue;
                }

                var time = CastPetSpell(owner, mayCast, reason, combatActionList, cinematicTimeSoFar + cinematicTime);
                if (time > 0) {
                    cinematicTime += time;
                    used++;
                }
            }

            _petCastsThisRound[owner] = used;
        }

        return cinematicTime;
    }

    private static readonly HashSet<kSpellEffects> s_damageEffects = [
        kSpellEffects.kDamage, kSpellEffects.kDamageNoCrit, kSpellEffects.kDamageOverTime, kSpellEffects.kMaxHealthDamage,
        kSpellEffects.kStealHealth, kSpellEffects.kDamagePerTotalPipPower, kSpellEffects.kMinionRankDamage,
        kSpellEffects.kDeferredDamage, kSpellEffects.kDivideDamage,
    ];

    private static readonly HashSet<kSpellEffects> s_healEffects = [
        kSpellEffects.kHeal, kSpellEffects.kHealOverTime, kSpellEffects.kHealPercent, kSpellEffects.kMaxHealthHeal,
        kSpellEffects.kSetHealPercent,
    ];

    private static readonly HashSet<kEffectTarget> s_enemyTargets = [
        kEffectTarget.kEnemySingle, kEffectTarget.kEnemyTeam, kEffectTarget.kEnemyTeamAllAtOnce,
        kEffectTarget.kAtLeastOneEnemy, kEffectTarget.kPreselectedEnemySingle, kEffectTarget.kMultiTargetEnemy,
        kEffectTarget.kEnemyMinion,
    ];

    /// <summary>The target of a pet's free cast: the owner, or an enemy (the owner's own pick if it is one) for a card that hits enemies.</summary>
    private CombatDuelSubCircle PickPetTarget(CombatDuelSubCircle owner, SpellTemplate template) {
        if (!(template.m_effects ?? []).Any(effect => effect is not null && s_enemyTargets.Contains(effect.m_effectTarget))) {
            return owner;
        }

        var picked = _queuedCombatActions.FirstOrDefault(a => a.SpellCaster == owner)?.SelectedTarget;
        if (picked is not null && picked.IsAlive && picked.AddedToDuel && picked.OccupiedTeam != owner.OccupiedTeam) {
            return picked;
        }

        return owner._duelActor.ActiveSubCircles
            .FirstOrDefault(c => c.IsAlive && c.AddedToDuel && c.OccupiedTeam != owner.OccupiedTeam);
    }

    private float CastPetSpell(CombatDuelSubCircle owner, PetMayCasts.MayCast mayCast, string reason,
                               CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        // The game's pet copy of the card, free: the pip cost comes off the copy (see ShowInHand).
        var card = SpellFactory.GetSpell(mayCast.SpellTemplateId);
        var spell = card is null ? null : (PetMayCasts.AsItemCard ? card with { m_itemCard = true } : card)
            with { m_pipCost = new SpellRank() };
        if (spell is null || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template) {
            return 0;
        }

        var target = PickPetTarget(owner, template);
        if (target is null || !target.IsAlive || !target.AddedToDuel) {
            return 0;
        }

        Logger.Information("Duel {0} | Slot {1} | Pet May Cast {2} casts {3} on slot {4} after {5}.",
            Logger.Args(_duel.m_duelID.Full, owner.SlotIndex, mayCast.Talent, mayCast.SpellName, target.SlotIndex, reason));
        owner._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar;
        var action = new QueuedCombatAction { SpellCaster = owner, SelectedTarget = target, Spell = spell, SpellTemplate = template };
        var before = combatActionList.m_actionList.Count;
        var cinematicTime = Math.Min(HandleSuccessfulAction(action, combatActionList, alreadyPaid: true), PetMayCasts.CastSeconds);
        ShowInHand(owner, spell, template, combatActionList, before);
        for (var i = before; i < combatActionList.m_actionList.Count; i++) {
            combatActionList.m_actionList[i].m_forceSpell = PetMayCasts.ForceSpell;
            // Flagged as the pet's, the game plays it with the pet's May Cast animation.
            if (PetMayCasts.ShowAsPetCast) {
                combatActionList.m_actionList[i].m_petCast = true;
                combatActionList.m_actionList[i].m_petCasted = true;
                combatActionList.m_actionList[i].m_petCastTarget = target.SlotIndex;
            }
        }

        return cinematicTime;
    }

    /// <summary>
    /// Puts a pet's free cast into its owner's hand for the game, which skips spells it can't find there, and takes its
    /// pip cost off the copy the game plays (after resolving: the server reads m_regularAdjust as a damage/heal bonus).
    /// </summary>
    private void ShowInHand(CombatDuelSubCircle owner, Spell spell, SpellTemplate template, CombatActionListObj combatActionList, int before) {
        if (!PetCastCards.TryGetValue(owner, out var cards)) {
            PetCastCards[owner] = cards = [];
        }

        var rank = template?.m_spellRank?.m_spellRank ?? 0;
        if (PetMayCasts.ZeroCost && rank > 0) {
            var free = spell with { m_regularAdjust = spell.m_regularAdjust - rank };
            for (var i = before; i < combatActionList.m_actionList.Count; i++) {
                if (ReferenceEquals(combatActionList.m_actionList[i].m_spell, spell)) {
                    combatActionList.m_actionList[i].m_spell = free;
                }
            }

            spell = free;
        }

        cards.Add(spell);
    }

    /// <summary>
    /// Casts the caster's Daybreaker (<paramref name="atStart"/>) or Nightbringer spells from an earlier round.
    /// </summary>
    private float CastDelayed(CombatDuelSubCircle caster, bool atStart, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var due = _delayedCasts
            .Where(d => d.Action.SpellCaster == caster && d.AtStart == atStart && d.Round < _duel.m_roundNum)
            .ToList();
        var cinematicTime = 0.0f;
        foreach (var delayed in due) {
            _delayedCasts.Remove(delayed);
            var action = delayed.Action;
            RetargetTeamSpell(action);
            if (!caster.IsAlive || action.SelectedTarget is null || !action.SelectedTarget.IsAlive
                || !action.SelectedTarget.AddedToDuel) {
                continue;
            }

            try {
                Logger.Information("Duel {0} | Slot {1} | {2} casts its delayed spell.",
                    Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, atStart ? "Daybreaker" : "Nightbringer"));
                caster._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar + cinematicTime;

                // The card goes out without its enchantment, or the game would play the delay a second time. Its
                // effects keep the enchantment's changes (they come from the queued spell template).
                var cast = new QueuedCombatAction {
                    SpellCaster = action.SpellCaster,
                    SelectedTarget = action.SelectedTarget,
                    Spell = DelayedCard(action.Spell),
                    SpellTemplate = action.SpellTemplate,
                };
                var before = combatActionList.m_actionList.Count;
                cinematicTime += HandleSuccessfulAction(cast, combatActionList, alreadyPaid: true);
                for (var i = before; i < combatActionList.m_actionList.Count; i++) {
                    combatActionList.m_actionList[i].m_delayed = true;
                }
            }
            catch (Exception e) {
                Logger.Error("Duel {0} | Slot {1} | delayed spell failed: {2}",
                    Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, e));
            }
        }

        return cinematicTime;
    }

    /// <summary>
    /// The game names one target even for a spell that hits a whole side (all enemies, all allies) or only touches the
    /// caster. When that target has died, the spell is aimed at a living member of the same side instead, so it is
    /// cast rather than passed. Spells with any single-target effect keep their target.
    /// </summary>
    private void RetargetTeamSpell(QueuedCombatAction action) {
        var target = action.SelectedTarget;
        if (target is null || action.Spell is null || (target.IsAlive && target.AddedToDuel)) {
            return;
        }

        var effects = (action.SpellTemplate?.m_effects ?? [])
            .SelectMany(CombatActionResolver.AllEffects)
            .Where(effect => effect is not null)
            .ToList();
        if (effects.Count == 0 || effects.Any(effect => effect.m_effectTarget is not (kEffectTarget.kEnemyTeam
                or kEffectTarget.kEnemyTeamAllAtOnce or kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce
                or kEffectTarget.kSelf or kEffectTarget.kGlobal or kEffectTarget.kInvalidTarget))) {
            return;
        }

        var living = ActiveSubCircles.FirstOrDefault(x => x.OccupiedTeam == target.OccupiedTeam && x.IsAlive && x.AddedToDuel);
        if (living is not null) {
            Logger.Debug("Duel {0} | Slot {1} | The spell's target is gone; it goes to slot {2} of the same side.",
                Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex, living.SlotIndex));
            action.SelectedTarget = living;
        }
    }

    private float ProcessQueuedAction(QueuedCombatAction action, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        {
            // If the caster is dead, skip this action.
            if (!action.SpellCaster.IsAlive) {
                Logger.Debug("Duel {0} | Slot {1} | Caster is dead. Skipping action.",
                    Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

                return cinematicTime;
            }

            // We want to invoke the overtime effects after we check for death.
            // This is because the overtime effects can kill a participant, and we want to see the animation.
            cinematicTime += InvokeOverTimeEffects(action.SpellCaster);

            // A null spell indicates the caster is passing their turn.
            if (action.Spell is null || action.SelectedTarget is null) {
                Logger.Debug("Duel {0} | Slot {1} | Caster is passing their turn.",
                    Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

                cinematicTime += HandlePassAction(action, combatActionList);

                return cinematicTime;
            }

            // An all-enemies (or all-allies) spell still goes off when the creature it was aimed at has died.
            RetargetTeamSpell(action);

            // If our target is gone or we're stunned, pass the turn.
            if (    action.SpellCaster.CombatParticipant.m_stunned > 0 
                || !action.SelectedTarget.IsAlive
                || !action.SelectedTarget.AddedToDuel) {
                action.SpellCaster.CombatParticipant.m_stunned--;

                cinematicTime += HandlePassAction(action, combatActionList);

                Logger.Debug("Duel {0} | Slot {1} | Spell cannot occur because target is dead or caster is stunned.",
                    Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

                return cinematicTime;
            }

            // Determine if this spell hits or fizzles.
            var spellHits = SpellHits(action.SpellCaster, action.Spell);
            if (!spellHits) {
                cinematicTime += HandleFizzleAction(action, combatActionList);

                // Increase pips used counter by 1, even if the spell fizzled.
                action.SpellCaster._usedPipsForExperienceGain++;

                if (s_manaOnFizzle && action.Spell is not null) {
                    action.SpellCaster.DeductMana(action.Spell.m_pipCost.m_spellRank);
                }
            }
            else if (IsDelayCast(action.Spell, out var atStart)) {
                // The enchanted card is cast now, which plays the Daybreaker or Nightbringer delay; the game reads
                // an action whose spell carries that enchantment as the delay itself. The card's own spell lands on
                // the caster's next turn. It is paid for now.
                var played = InitializeCombatAction(action);
                played.m_targetSubcircleList.Add(action.SelectedTarget.SlotIndex);
                DoSpellCastConsequences(action.SpellCaster, played);
                combatActionList.m_actionList.Add(played);
                _delayedCasts.Add(new DelayedCast(action, atStart, (int) _duel.m_roundNum));
                Logger.Information("Duel {0} | Slot {1} | {2} delays the spell to the caster's next turn.",
                    Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex, atStart ? "Daybreaker" : "Nightbringer"));
                cinematicTime += GetActionCinematicTime(action);
            }
            else {
                // Record when this caster's cinematic begins so a summoned minion appears with its cast.
                action.SpellCaster._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar + cinematicTime;
                cinematicTime += HandleSuccessfulAction(action, combatActionList);
            }
        }

        return cinematicTime;
    }

    /// <summary>
    /// True for a card enchanted with any Daybreaker or Nightbringer (every tier), and for the game's ready-made
    /// delayed spells such as "Frost Beetle - Nightbringer" or "Imp - Daybreaker".
    /// </summary>
    private static bool IsDelayCast(Spell spell, out bool atStart) {
        atStart = false;
        if (spell is null) {
            return false;
        }

        if (spell.m_enchantment != 0
            && CoreObjectFactory.GetCoreTemplate(spell.m_enchantment) is SpellTemplate enchantment
            && CombatEnchantments.IsDaybreakerOrNightbringer(enchantment)) {
            atStart = enchantment.m_name.ToString()?.Contains("Daybreaker", StringComparison.OrdinalIgnoreCase) == true;

            return true;
        }

        if (DelayedBaseName(spell) is not null) {
            atStart = CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is SpellTemplate own
                && own.m_name.ToString()?.EndsWith("Daybreaker", StringComparison.OrdinalIgnoreCase) == true;

            return true;
        }

        return false;
    }

    /// <summary>
    /// For a ready-made delayed spell ("Frost Beetle - Nightbringer"), the name of the card it casts.
    /// </summary>
    private static string DelayedBaseName(Spell spell) {
        if (CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate own) {
            return null;
        }

        var name = own.m_name.ToString() ?? "";
        foreach (var suffix in new[] { " - Nightbringer", " - Daybreaker" }) {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                return name[..^suffix.Length];
            }
        }

        return null;
    }

    /// <summary>
    /// The card as it is shown when the delayed spell lands: without the enchantment, or as its base card, so the
    /// game does not play the delay a second time.
    /// </summary>
    private static Spell DelayedCard(Spell spell) {
        if (spell.m_enchantment != 0) {
            return spell with { m_enchantment = 0 };
        }

        return DelayedBaseName(spell) is { } baseName && SpellFactory.GetSpell(baseName) is { } baseSpell ? baseSpell : spell;
    }

    private float HandleFizzleAction(QueuedCombatAction action, CombatActionListObj combatActionList) {
        var fizzleAction = InitializeCombatAction(action);
        fizzleAction.m_spellHits = (char) 0;
        fizzleAction.m_targetSubcircleList.Add(action.SelectedTarget.SlotIndex);
        combatActionList.m_actionList.Add(fizzleAction);

        Logger.Debug("Duel {0} | Slot {1} | Spell fizzled.",
            Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex));

        return SPELL_FIZZLE_TIME;
    }

    private float HandleSuccessfulAction(QueuedCombatAction action, CombatActionListObj combatActionList, bool alreadyPaid = false) {
        var cinematicTime = 0.0f;
        var combatAction = InitializeCombatAction(action);
        var spellWorthCasting = CombatActionResolver.ProcessedQueuedCombatAction(action, ref combatAction, ref cinematicTime);

        LogCombatAction(action, combatAction, spellWorthCasting);

        combatActionList.m_actionList.Add(combatAction);

        if (action.Spell is null) {
            return SPELL_PASS_TIME;
        }

        if (!alreadyPaid) {
            DoSpellCastConsequences(action.SpellCaster, combatAction);
        }

        return GetActionCinematicTime(action) + cinematicTime;
    }

    private float HandlePassAction(QueuedCombatAction action, CombatActionListObj combatActionList) {
        var passCombatAction = InitializeCombatAction(action);
        passCombatAction.m_spell = null;
        passCombatAction.m_spellHits = (char) 0;
        combatActionList.m_actionList.Add(passCombatAction);

        return SPELL_PASS_TIME;
    }

    private float InvokeOverTimeEffects(CombatDuelSubCircle caster) {
        // Get all DoT and HoT effects. Clone the list to avoid concurrent modification.
        var dotEffects = caster._hangingEffects
            .Where(x => x.m_effectType == kSpellEffects.kDamageOverTime)
            .ToList();
        var hotEffects = caster._hangingEffects
            .Where(x => x.m_effectType == kSpellEffects.kHealOverTime)
            .ToList();
        var cinematicTime = (dotEffects.Count + hotEffects.Count) * OVER_TIME_ACTIVATION_TIME;

        foreach (var effect in dotEffects) {
            var initialDamage = effect.m_paramPerRound;
            var wards = CombatWards.FindAppliedWards(caster, effect).ToList();
            var damage = CombatWards.GetIncomingDamageFromWards(wards, initialDamage);

            // We don't need to calculate stats from gear because the initial application already did that.

            cinematicTime += HANGING_EFFECT_CONSUME_TIME * wards.Count;
            caster.DamageParticipant(damage);
            effect.m_numRounds--;

            // Remove the effect if it's out of rounds.
            if (effect.m_numRounds <= 0) {
                caster._hangingEffects.Remove(effect);
            }
        }

        foreach (var effect in hotEffects) {
            // A heal over time ends with the participant: it never revives them, including after a DoT just now.
            if (!caster.IsAlive) {
                caster._hangingEffects.Remove(effect);
                cinematicTime -= OVER_TIME_ACTIVATION_TIME;
                continue;
            }

            // Todo: are there wards that increase incoming healing?
            // We don't need to calculate stats from gear because the initial application already did that.
            caster.HealParticipant(effect.m_paramPerRound);
            effect.m_numRounds--;

            // Remove the effect if it's out of rounds.
            if (effect.m_numRounds <= 0) {
                caster._hangingEffects.Remove(effect);
            }
        }

        // Is the participant dead after the effects?
        // If so, add a death animation time.
        if (!caster.IsAlive) {
            cinematicTime += DEATH_ANIMATION_TIME;
        }

        return cinematicTime;
    }

    private void LogQueuedCombatAction(CombatMoveType type, CombatDuelSubCircle caster, CombatDuelSubCircle target, Spell spell) {
        if (type == CombatMoveType.ChangeMind) {
            Logger.Debug("Duel {0} | Slot {1} | Caster changed their mind and is not casting a spell",
                Logger.Args(_duel.m_duelID.Full, caster.SlotIndex));

            return;
        }

        var targetOrSelf = target is null
            ? "null" : (target.SlotIndex == caster.SlotIndex ? "self" : target.SlotIndex.ToString());
        var spellOrPass = spell is null ? "pass" : spell.m_templateID.ToString();
        Logger.Debug("Duel {0} | Slot {1} | Caster is casting spell {2} against target {3}",
            Logger.Args(_duel.m_duelID.Full, caster.SlotIndex, spellOrPass, targetOrSelf));
    }

    private void LogCombatAction(QueuedCombatAction action, CombatAction combatAction, bool spellWorthCasting) {
        if (spellWorthCasting) {
            var targetsStringForLog = string.Join(", ", combatAction.m_targetSubcircleList);
            var critSuffix = combatAction.m_CritHitList?.Any(x => x.m_mult > 0f) == true ? " (Crits!)" : "";
            Logger.Debug("Duel {0} | Slot {1} | Spell {2} hits targets [{3}]{4}",
                Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex, action.Spell.m_templateID, targetsStringForLog, critSuffix));
        }
        else {
            Logger.Debug("Duel {0} | Slot {1} | Spell {3} not worth casting. Passing turn.",
                Logger.Args(_duel.m_duelID.Full, action.SpellCaster.SlotIndex, action.Spell.m_templateID));
            combatAction.m_spell = null;
        }
    }

    private static CombatAction InitializeCombatAction(QueuedCombatAction action) => new() {
        m_spellCaster = action.SpellCaster.SlotIndex,
        m_targetSubcircleList = [],
        m_showCast = true,
        m_spellHits = (char) 1,
        m_spell = action.Spell,
        m_serializedBlocks = "\u0000\u0000",
        m_shadowPactTarget = -1,
        m_petCastTarget = -1,
        m_CritHitList = [],
    };

    private static float GetActionCinematicTime(QueuedCombatAction action) {
        if (action.Spell is null) {
            return SPELL_PASS_TIME;
        }

        var spellName = SpellFactory.GetBaseSpellName(action.Spell.m_templateID);
        var cinematicFactory = SpellCinematics.Instance;

        // All spells will always have a summon time.
        var count = SpellCinematics.GetSpellSummonTime(spellName);

        // Check if this spell has a special casting time. If not, just add the default casting time.
        var castTime = SpellCinematics.GetSpellCastingTime(spellName);
        count += castTime > 0.1f ? castTime : SPELL_CAST_TIME;

        // Check to see if the spell has an act time. If it does, add it to the total time.
        // Otherwise, return the total time.
        var actTime = SpellCinematics.GetSpellActTime(spellName);
        if (actTime <= 0.1f) {
            return count + SpellCinematics.GetSpellTotalTime(spellName);
        }

        count += actTime;

        return count;
    }

    private static bool SpellHits(CombatDuelSubCircle caster, Spell spell) {
        if (caster is null || spell is null) {
            return false;
        }

        if (ConsumeDispell(caster, spell.m_magicSchoolID)) {
            return false;
        }

        if (caster.CheatNoFizzle) {
            return true;
        }

        var spellAccuracy = (int) spell.m_accuracy;
        var stats = caster.CombatParticipant.m_pGameStats;
        var school = MagicSchools.GetMagicSchool(spell.m_magicSchoolID);
        var schoolName = school.m_schoolName;

        var percentIncrease = caster.GetStatBySchool(stats.m_accBonusPercent, schoolName);
        var percentIncreaseAll = stats.m_accBonusPercentAll;
        var percentDecrease = caster.GetStatBySchool(stats.m_accReducePercent, schoolName);
        var percentDecreaseAll = stats.m_accReducePercentAll;

        // Convert to percentages for calculation
        var totalIncrease = (percentIncrease + percentIncreaseAll) * 100;
        var totalDecrease = (percentDecrease + percentDecreaseAll) * 100;

        // Apply percentages to the spell accuracy
        spellAccuracy *= (int) Math.Floor((1 + totalIncrease / 100.0) * (1 - totalDecrease / 100.0));

        // Apply any hanging accuracy effects
        spellAccuracy = ConsumeHangingAccuracyEffects(spellAccuracy, caster, spell.m_magicSchoolID);

        var hitChance = Random.Shared.Next(0, 100);
        
        return hitChance <= spellAccuracy;
    }

    /// <summary>
    /// Takes a played card out of the hand: a treasure card is used up for good (and leaves the deck), any
    /// other card is discarded and comes back on reshuffle.
    /// </summary>
    internal static void UseUpCard(CombatDuelSubCircle caster, Spell spell) {
        if (spell.m_treasureCard) {
            var consumedTemplateId = caster.ConsumeFromVault(spell);
            if (consumedTemplateId != 0 && caster._wizard != null && !caster.IsSandboxed) {
                // Persist the removal from the player's treasure card book.
                caster._wizard.SpellbookBehavior.RemoveTreasureCard(consumedTemplateId);
                WizardCollection.RemoveTreasureCard(caster._wizard, consumedTemplateId);

                // Also remove from the equipped deck's spell list so it doesn't
                // reappear when the player re-opens their deck after combat.
                var deckSlot = caster._wizard.EquipmentBehavior.SlotList
                    .FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck);
                if (deckSlot?.ItemId != null) {
                    caster._wizard.RemoveSpellFromDeck(consumedTemplateId, deckSlot.ItemId.Value);

                    // Tell the client to remove the TC from the deck UI.
                    var spellTemplate = CoreObjectFactory.GetCoreTemplate(consumedTemplateId) as SpellTemplate;
                    if (spellTemplate != null) {
                        var spellHash = StringHash.Compute(spellTemplate.m_name);
                        caster.ParticipantActor.Tell(
                            new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMDECK {
                                SpellID = (int) spellHash,
                                EnchantmentID = 0,
                                DeckID = deckSlot.ItemId.Value,
                                Success = 1,
                                Destroy = 0
                            }, ActorRefs.NoSender);
                    }
                }
            }
        }
        else {
            caster.DiscardCard(spell);
        }
    }

    private static void DoSpellCastConsequences(CombatDuelSubCircle caster, CombatAction action) {
        if (action.m_spell is null) {
            return;
        }

        // Treasure cards are permanently consumed on successful cast.
        // Regular spells are discarded and return to the deck on reshuffle.
        UseUpCard(caster, action.m_spell);

        // FullPipConserve: like a 100% pip conserve stat, a half-used power pip comes back as a normal pip.
        var conservePips = (s_fullPipConserve && caster._wizard is not null)
                           // A roguelike run's Potions of Thrift give a chance of it on each spell.
                           || Imlight.CoreLib.Game.Runs.RoguelikeBoosts.RollPipConserve(caster._wizard);

        // Spend any school pips the spell costs.
        caster.DeductSchoolPips(action.m_spell);

        // Deduce the players mana by the rank of the spell.
        caster.DeductMana(action.m_spell.m_pipCost.m_spellRank);

        // X-pip spells spend what they used (same GetXPipCost that chose the tier).
        if (CombatActionResolver.IsXPipSpell(action.m_spell)) {
            var xCost = CombatActionResolver.GetXPipCost(action.m_spell, caster);
            caster._usedPipsForExperienceGain += xCost;

            caster.DeductPips((MagicSchool) action.m_spell.m_magicSchoolID, xCost, conservePips);
        }
        else {
            // Increase the used pips for experience gain by the rank of the spell.
            // Even 0-rank spells will give the caster 1 pip for experience gain.
            caster._usedPipsForExperienceGain += Math.Max((byte) 1, action.m_spell.m_pipCost.m_spellRank);

            caster.DeductPips((MagicSchool) action.m_spell.m_magicSchoolID, action.m_spell.m_pipCost.m_spellRank, conservePips);
        }
    }

    private static bool ConsumeDispell(CombatDuelSubCircle caster, uint magicSchoolId) {
        var dispellHangingEffect = caster._hangingEffects
            .FirstOrDefault(x => x.m_effectType == kSpellEffects.kDispel
                     && StringHash.Compute(x.m_sDamageType) == magicSchoolId);

        if (dispellHangingEffect is not null) {
            caster._hangingEffects.Remove(dispellHangingEffect);
            return true;
        }
        else {
            return false;
        }
    }

    private static int ConsumeHangingAccuracyEffects(int startingAccuracy, CombatDuelSubCircle caster, uint magicSchoolId) {
        var accuracyHangingEffects = caster._hangingEffects
            .Where(x => x.m_effectType == kSpellEffects.kModifyAccuracy)
            .Where(x => x.m_damageType == magicSchoolId);

        foreach (var effect in accuracyHangingEffects) {
            startingAccuracy += (int) Math.Floor(1 + effect.m_effectParam / 100.0);
            caster._hangingEffects.Remove(effect);
        }

        return startingAccuracy;
    }

}
