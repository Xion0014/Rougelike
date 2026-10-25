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
 * Boss cheats, stage 1 (the file format and the cheat files are Niduus's: BossCheats.cs, spiraldb-extra/BossCheats).
 * The engine is our own, on our combat code, and runs these blocks; a cheat that needs anything else is skipped and
 * logged once ("not supported yet"), so it never half-fires.
 *
 *   When     BattleStart, RoundStart, RoundEnd, FromRound, Pips, HealthBelow, PlayerCasts (and PlayerDamages,
 *            PlayerHeals, PlayerCharms, PlayerWards, PlayerAuras, PlayerGlobals, PlayerShadows, PlayerMinions),
 *            PlayerPasses, BossHit.
 *   If       Round, Every, Phase, Once, Chance, MaxTimes, Cooldown, FromRound, ToRound, PlayersMin/Max, HasAllies,
 *            NoAllies, AlliesUp/Down, AlliesMin/Max, HealthAbove/Under, Mark/MarkWho/NotMarked, and for spells:
 *            OnBoss, SingleTarget, MultiTarget, NotOnCaster, School, Kind, Category, NotCategory, CostMin/Max,
 *            CasterSchool, Spells (AnyVersion).
 *   Do       Cast, Polymorph (as a cast), CastBy, Say, Mark, Unmark, PickOne, InTurn, SetHealth, HealthTo, Heal, Pips,
 *            SetPhase, Wait, Defeat, RemoveCharms, RemoveWards; plus the old fields Cast, Casts, Line, SetPhase,
 *            SetHealth and Defeat.
 *
 * On a roguelike run the cheats work only in tiers from [Roguelike] BossCheatsFromTier on (6: arc 2), so arc 1 plays
 * as before. [Roguelike] BossCheats = false (the launcher's "Boss cheats" switch) turns them all off.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Runs;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    private static readonly int s_cheatsFromTier = Math.Max(0, ConfigurationManager.GetValue("Roguelike.BossCheatsFromTier", 6));

    // How long a cheat's line shows over the boss before what comes next.
    private const float CHEAT_LINE_SECONDS = 2.0f;

    private static readonly HashSet<string> s_supportedWhens = new(StringComparer.OrdinalIgnoreCase) {
        "BattleStart", "RoundStart", "RoundEnd", "FromRound", "Pips", "HealthBelow", "MyHealthBelow", "PlayerCasts",
        "PlayerDamages", "PlayerHeals", "PlayerCharms", "PlayerWards", "PlayerAuras", "PlayerGlobals", "PlayerShadows",
        "PlayerMinions", "PlayerPasses", "BossHit",
    };

    private static readonly HashSet<string> s_supportedDos = new(StringComparer.OrdinalIgnoreCase) {
        "Cast", "Polymorph", "CastBy", "Say", "Mark", "Unmark", "PickOne", "InTurn", "SetHealth", "HealthTo", "Heal",
        "Pips", "SetPhase", "Wait", "Defeat", "RemoveCharms", "RemoveWards", "Form",
    };

    // The kind of card each PlayerX When stands for (a PlayerCasts with that Category).
    private static readonly Dictionary<string, string> s_castWhenCategory = new(StringComparer.OrdinalIgnoreCase) {
        ["PlayerDamages"] = "Damage", ["PlayerHeals"] = "Heal", ["PlayerCharms"] = "Charm", ["PlayerWards"] = "Ward",
        ["PlayerAuras"] = "Aura", ["PlayerGlobals"] = "Global", ["PlayerShadows"] = "Shadow", ["PlayerMinions"] = "Minion",
    };

    private static readonly HashSet<string> s_unsupportedLogged = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<(CombatDuelSubCircle Boss, int Cheat)> _cheatsUsedOnce = [];
    private readonly Dictionary<(CombatDuelSubCircle Boss, int Cheat), (int Times, int Round)> _cheatCounts = [];
    private readonly Dictionary<CombatDuelSubCircle, (int Phase, int FirstRound)> _cheatPhases = [];
    private readonly HashSet<(CombatDuelSubCircle Boss, int Cheat)> _healthCheatsFired = [];
    private readonly Dictionary<CombatDuelSubCircle, HashSet<string>> _cheatMarks = [];
    private readonly List<WaitingCheatSteps> _waitingCheatSteps = [];
    private List<CombatDuelSubCircle> _lastCheatTargets = [];

    private sealed record WaitingCheatSteps(CombatDuelSubCircle Boss, BossCheats.BossCheat Cheat, List<BossCheats.CheatAction> Steps,
                                            int Round, CombatDuelSubCircle Trigger, CombatDuelSubCircle Receiver);

    // ---------------------------------------------------------------------
    // Who cheats
    // ---------------------------------------------------------------------

    /// <summary>
    /// Whether cheats work in this battle: on a run only from BossCheatsFromTier on (a wizard in it must be that far).
    /// </summary>
    private bool CheatsWorkHere() {
        if (!BossCheats.Enabled) {
            return false;
        }

        if (!RoguelikeConfig.Enabled) {
            return true;
        }

        return _subCircles.Any(c => c is { Occupied: true } && c._wizard?.Roguelike is { } run && run.Tier >= s_cheatsFromTier);
    }

    private static ulong CheatTemplateOf(CombatDuelSubCircle c)
        => c?.ParticipantObject is { } obj ? obj.m_templateID : 0;

    private List<(CombatDuelSubCircle Boss, List<BossCheats.BossCheat> Cheats)> Cheaters() {
        if (!CheatsWorkHere()) {
            return [];
        }

        return [.. _subCircles
            .Where(c => c is { Occupied: true, AddedToDuel: true } && !c.IsHumanPlayer && c.IsAlive)
            .Select(c => (c, BossCheats.For(CheatTemplateOf(c))))
            .Where(x => x.Item2.Count > 0)];
    }

    private string CheaterName(CombatDuelSubCircle boss) => BossCheats.NameOf(CheatTemplateOf(boss));

    private (int Phase, int FirstRound) CheatPhaseOf(CombatDuelSubCircle boss) {
        if (!_cheatPhases.TryGetValue(boss, out var phase)) {
            _cheatPhases[boss] = phase = (1, (int) _duel.m_roundNum);
        }

        return phase;
    }

    // The boss's round in its phase, from 1.
    private int CheatRound(CombatDuelSubCircle boss) => (int) _duel.m_roundNum - CheatPhaseOf(boss).FirstRound + 1;

    private static bool DueIn(BossCheats.BossCheat cheat, int round)
        => cheat.Round > 0 && cheat.Every > 0 ? round >= cheat.Round && (round - cheat.Round) % cheat.Every == 0
            : cheat.Round > 0 ? round == cheat.Round
            : cheat.Every <= 0 || round % cheat.Every == 0;

    private IEnumerable<CombatDuelSubCircle> Standing(Func<CombatDuelSubCircle, bool> which)
        => _subCircles.Where(c => c is { Occupied: true, AddedToDuel: true } && c.IsAlive && which(c));

    private static int MostHealthOf(CombatDuelSubCircle c) {
        // Healing stops at the base hit points (CombatDuelSubCircle.HealParticipant), so that is its most.
        return Math.Max(1, c.ParticipantGameStats?.m_baseHitpoints ?? 1);
    }

    private static double HealthPercent(CombatDuelSubCircle c)
        => (c.ParticipantGameStats?.m_currentHitpoints ?? 0) * 100.0 / MostHealthOf(c);

    // ---------------------------------------------------------------------
    // When
    // ---------------------------------------------------------------------

    /// <summary>The cheats of the start (or end) of a round: waited steps, BattleStart, RoundStart, FromRound, Pips.</summary>
    private float CheatsAtRound(bool atEnd, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        try {
            if (!atEnd) {
                cinematicTime += WaitedCheatSteps(combatActionList, cinematicTimeSoFar);
            }

            foreach (var (boss, cheats) in Cheaters()) {
                var round = CheatRound(boss);
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    if (!CheatInPhase(boss, cheat)) {
                        continue;
                    }

                    if (!atEnd && cheat.Is("Pips") && cheat.PipsOf.Equals("Enemy", StringComparison.OrdinalIgnoreCase)) {
                        foreach (var player in Standing(c => c.OccupiedTeam != boss.OccupiedTeam && CheatPipCount(c, cheat.PipsKind) >= cheat.Pips)
                                     .Where(_ => cheat.Pips > 0).ToList()) {
                            cinematicTime += TryCheat(boss, i, cheat, player, player, $"round {round}, {cheat.Pips}+ pips on slot {player.SlotIndex}",
                                combatActionList, cinematicTimeSoFar + cinematicTime);
                        }

                        continue;
                    }

                    var due = atEnd
                        ? cheat.Is("RoundEnd") && DueIn(cheat, round) && (cheat.Pips <= 0 || PipsReached(boss, cheat))
                        : cheat.Is("BattleStart") ? round == 1 && CheatPhaseOf(boss).Phase == 1
                        : cheat.Is("Pips") ? PipsReached(boss, cheat)
                        : cheat.Is("FromRound") ? round >= Math.Max(1, cheat.Round)
                                                  && (cheat.Every <= 1 || (round - Math.Max(1, cheat.Round)) % cheat.Every == 0)
                        : cheat.Is("RoundStart") && DueIn(cheat, round);
                    if (due) {
                        cinematicTime += TryCheat(boss, i, cheat, null, null, $"{(atEnd ? "end of " : "")}round {round}",
                            combatActionList, cinematicTimeSoFar + cinematicTime);
                    }
                }
            }

            cinematicTime += HealthCheats(combatActionList, cinematicTimeSoFar + cinematicTime);
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Boss cheats at round {1} failed: {2}", Logger.Args(_duel.m_duelID.Full, atEnd ? "end" : "start", e));
        }

        return cinematicTime;
    }

    private static int CheatPipCount(CombatDuelSubCircle c, string kind) {
        if (c.CombatParticipant?.m_pipCount is not { } pips) {
            return 0;
        }

        return string.Equals(kind, "Shadow", StringComparison.OrdinalIgnoreCase)
            ? pips.m_shadowPips : CombatDuelSubCircle.TotalPips(pips) + pips.m_shadowPips;
    }

    private bool PipsReached(CombatDuelSubCircle boss, BossCheats.BossCheat cheat)
        => cheat.Pips > 0 && (cheat.PipsOf.Equals("Enemy", StringComparison.OrdinalIgnoreCase)
            ? Standing(c => c.OccupiedTeam != boss.OccupiedTeam).Any(c => CheatPipCount(c, cheat.PipsKind) >= cheat.Pips)
            : CheatPipCount(boss, cheat.PipsKind) >= cheat.Pips);

    /// <summary>
    /// The cheats a move sets off: a player's spell landing (PlayerCasts and its kinds), a pass (PlayerPasses), a spell
    /// that hurt the boss (BossHit), and the boss's health falling (HealthBelow).
    /// </summary>
    private float CheatsAfterMove(QueuedCombatAction move, int before, Dictionary<CombatDuelSubCircle, int> healthBefore,
                                  CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        try {
            var mover = move.SpellCaster;
            var landed = combatActionList.m_actionList.Skip(before)
                .FirstOrDefault(a => a.m_spellCaster == mover?.SlotIndex && a.m_spell is not null && a.m_spellHits != (char) 0);
            var passed = move.Spell is null;
            var template = move.SpellTemplate ?? (move.Spell is null ? null : CoreObjectFactory.GetCoreTemplate(move.Spell.m_templateID) as SpellTemplate);
            var aimed = landed?.m_targetSubcircleList is { } list ? new HashSet<int>(list) : new HashSet<int>();

            foreach (var (boss, cheats) in Cheaters()) {
                if (mover is null || mover.OccupiedTeam == boss.OccupiedTeam) {
                    continue;
                }

                var hurt = healthBefore.TryGetValue(boss, out var was) && boss.ParticipantGameStats.m_currentHitpoints < was;
                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    if (!CheatInPhase(boss, cheat)) {
                        continue;
                    }

                    string reason = null;
                    if (passed && mover.IsAlive && cheat.Is("PlayerPasses")) {
                        reason = $"slot {mover.SlotIndex} passed";
                    }
                    else if (landed is not null && template is not null) {
                        var castWhen = cheat.Is("PlayerCasts") || s_castWhenCategory.ContainsKey(cheat.When);
                        if (castWhen && (!s_castWhenCategory.TryGetValue(cheat.When, out var kind) || CategoriesOf(template).Contains(kind))
                            && SpellMatches(cheat, template, aimed, boss, mover)) {
                            reason = $"slot {mover.SlotIndex} cast {(string) template.m_name}";
                        }
                        else if (cheat.Is("BossHit") && hurt && SpellMatches(cheat, template, aimed, boss, mover)) {
                            reason = $"slot {mover.SlotIndex} hit it with {(string) template.m_name}";
                        }
                    }

                    if (reason is not null) {
                        cinematicTime += TryCheat(boss, i, cheat, mover, move.SelectedTarget, reason, combatActionList,
                            cinematicTimeSoFar + cinematicTime);
                    }
                }
            }

            cinematicTime += HealthCheats(combatActionList, cinematicTimeSoFar + cinematicTime);
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Boss cheats after a move failed: {1}", Logger.Args(_duel.m_duelID.Full, e));
        }

        return cinematicTime;
    }

    // HealthBelow: the boss's health first falls under Percent (or Health); once per phase.
    private float HealthCheats(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        foreach (var (boss, cheats) in Cheaters()) {
            for (var i = 0; i < cheats.Count; i++) {
                var cheat = cheats[i];
                if (!(cheat.Is("HealthBelow") || cheat.Is("MyHealthBelow")) || !CheatInPhase(boss, cheat)
                    || _healthCheatsFired.Contains((boss, i))) {
                    continue;
                }

                var below = cheat.Health > 0
                    ? boss.ParticipantGameStats.m_currentHitpoints < cheat.Health
                    : cheat.Percent > 0 && HealthPercent(boss) < cheat.Percent;
                if (below) {
                    _healthCheatsFired.Add((boss, i));
                    cinematicTime += TryCheat(boss, i, cheat, null, null,
                        cheat.Health > 0 ? $"health under {cheat.Health}" : $"health under {cheat.Percent}%", combatActionList,
                        cinematicTimeSoFar + cinematicTime);
                }
            }
        }

        return cinematicTime;
    }

    // ---------------------------------------------------------------------
    // If
    // ---------------------------------------------------------------------

    private bool CheatInPhase(CombatDuelSubCircle boss, BossCheats.BossCheat cheat)
        => cheat.Phase <= 0 || cheat.Phase == CheatPhaseOf(boss).Phase;

    /// <summary>Why this engine can't run a cheat (a When, If or Do it does not have yet), or null.</summary>
    private static string NotSupported(BossCheats.BossCheat cheat) {
        if (!s_supportedWhens.Contains(cheat.When ?? "")) {
            return $"When {cheat.When}";
        }

        if (!string.IsNullOrEmpty(cheat.HasSpell)) {
            return "If has spell";
        }

        if (cheat.Creature > 0) {
            return "If creature";
        }

        if (!string.IsNullOrEmpty(cheat.Fizzle) && !cheat.Fizzle.Equals("Landed", StringComparison.OrdinalIgnoreCase)) {
            return $"Fizzle {cheat.Fizzle}";
        }

        if (cheat.Spawn > 0) {
            return "Spawn";
        }

        return cheat.Steps().FirstOrDefault(s => !s_supportedDos.Contains(s.Do ?? "") || (s.Is("Form"))) is { } step
            ? $"Do {step.Do}" : null;
    }

    private bool LimitsAllow(CombatDuelSubCircle boss, int index, BossCheats.BossCheat cheat, CombatDuelSubCircle trigger,
                             CombatDuelSubCircle receiver) {
        if (cheat.Once && _cheatsUsedOnce.Contains((boss, index))) {
            return false;
        }

        var (times, last) = _cheatCounts.GetValueOrDefault((boss, index));
        if ((cheat.MaxTimes > 0 && times >= cheat.MaxTimes) || (cheat.Cooldown > 0 && times > 0 && (int) _duel.m_roundNum - last <= cheat.Cooldown)) {
            return false;
        }

        var battleRound = (int) _duel.m_roundNum;
        if ((cheat.FromRound > 0 && !cheat.Is("FromRound") && battleRound < cheat.FromRound) || (cheat.ToRound > 0 && battleRound > cheat.ToRound)) {
            return false;
        }

        var players = Standing(c => c.OccupiedTeam != boss.OccupiedTeam).Count();
        if (players < cheat.PlayersMin || (cheat.PlayersMax > 0 && players > cheat.PlayersMax)) {
            return false;
        }

        var allies = Standing(c => c != boss && c.OccupiedTeam == boss.OccupiedTeam).ToList();
        if ((cheat.HasAllies && allies.Count == 0) || (cheat.NoAllies && allies.Count > 0)) {
            return false;
        }

        if (cheat.AlliesMin > 0 && allies.Count < cheat.AlliesMin) {
            return false;
        }

        if ((cheat.AlliesMax > 0 && allies.Count > cheat.AlliesMax) || (cheat.AlliesMax < 0 && allies.Count > 0)) {
            return false;
        }

        var allyTemplates = allies.Select(CheatTemplateOf).ToHashSet();
        if (BossCheats.Ids(cheat.AlliesUp).Any(id => !allyTemplates.Contains(id))
            || BossCheats.Ids(cheat.AlliesDown).Any(id => allyTemplates.Contains(id))) {
            return false;
        }

        var percent = HealthPercent(boss);
        if ((cheat.HealthAbove > 0 && percent <= cheat.HealthAbove) || (cheat.HealthUnder > 0 && percent >= cheat.HealthUnder)) {
            return false;
        }

        if (!string.IsNullOrEmpty(cheat.Mark)) {
            var marked = (cheat.MarkWho ?? "").ToLowerInvariant() switch {
                "receiver" => HasMark(receiver, cheat.Mark),
                "self" => HasMark(boss, cheat.Mark),
                "anyenemy" => Standing(c => c.OccupiedTeam != boss.OccupiedTeam).Any(c => HasMark(c, cheat.Mark)),
                _ => HasMark(trigger, cheat.Mark),
            };
            if (marked == cheat.NotMarked) {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(cheat.HasWho) || !string.IsNullOrEmpty(cheat.HasKind)) {
            var need = Math.Max(1, cheat.HasCount);
            bool Has(CombatDuelSubCircle who) => who is not null && HangingOfKind(who, cheat.HasKind) >= need;
            var has = (cheat.HasWho ?? "").ToLowerInvariant() switch {
                "receiver" => Has(receiver),
                "self" => Has(boss),
                "anyenemy" => Standing(c => c.OccupiedTeam != boss.OccupiedTeam).Any(Has),
                "anyally" => Standing(c => c.OccupiedTeam == boss.OccupiedTeam).Any(Has),
                _ => Has(trigger),
            };
            if (has == cheat.HasNot) {
                return false;
            }
        }

        return true;
    }

    // How many of its hanging effects are of these kinds (Shield, Trap, Blade, Weakness, Charm, Ward, DoT, HoT; commas;
    // empty: any). Auras are not counted yet.
    private static int HangingOfKind(CombatDuelSubCircle who, string kinds) {
        var wanted = (kinds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (who._hangingEffects ?? []).Count(e => e is not null && (wanted.Count == 0 ? KindsOfHanging(e).Count > 0
            : KindsOfHanging(e).Overlaps(wanted)));
    }

    private static HashSet<string> KindsOfHanging(SpellEffect e) {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var good = HangingIsGood(e);
        switch (e.m_effectType) {
            case kSpellEffects.kModifyOutgoingDamage or kSpellEffects.kModifyOutgoingDamageFlat:
                found.Add(good ? "Blade" : "Weakness");
                found.Add("Charm");
                break;
            case kSpellEffects.kModifyIncomingDamage or kSpellEffects.kModifyIncomingDamageFlat:
                found.Add(good ? "Shield" : "Trap");
                found.Add("Ward");
                break;
            case kSpellEffects.kAbsorbDamage:
                found.Add("Shield");
                found.Add("Ward");
                break;
            case kSpellEffects.kDamageOverTime:
                found.Add("DoT");
                break;
            case kSpellEffects.kHealOverTime:
                found.Add("HoT");
                break;
            default:
                if (s_cheatCharmEffects.Contains(e.m_effectType)) {
                    found.Add("Charm");
                }
                else if (s_cheatWardEffects.Contains(e.m_effectType)) {
                    found.Add("Ward");
                }

                break;
        }

        return found;
    }

    private static bool HangingIsGood(SpellEffect e) => e.m_disposition switch {
        kHangingDisposition.kBeneficial => true,
        kHangingDisposition.kHarmful => false,
        _ => e.m_effectType is kSpellEffects.kModifyIncomingDamage or kSpellEffects.kModifyIncomingDamageFlat
            ? e.m_effectParam < 0 : e.m_effectParam > 0,
    };

    private bool HasMark(CombatDuelSubCircle who, string mark)
        => who is not null && _cheatMarks.TryGetValue(who, out var marks) && marks.Contains(mark);

    private static bool SpellMatches(BossCheats.BossCheat cheat, SpellTemplate spell, ICollection<int> aimed, CombatDuelSubCircle boss,
                                     CombatDuelSubCircle mover) {
        var many = (spell.m_effects ?? []).Where(e => e is not null).SelectMany(CombatActionResolver.AllEffects).Any(e => e is not null && s_cheatManyTargets.Contains(e.m_effectTarget));
        if ((cheat.OnBoss && !aimed.Contains(boss.SlotIndex)) || (cheat.SingleTarget && many) || (cheat.MultiTarget && !many)
            || (cheat.NotOnCaster && mover is not null && aimed.Contains(mover.SlotIndex))) {
            return false;
        }

        var categories = CategoriesOf(spell);
        if ((cheat.NotCategory is { Length: > 0 } notCategory && SpellNameFamily.CategoryMatches(notCategory, categories))
            || (cheat.Category is { Length: > 0 } category && !SpellNameFamily.CategoryMatches(category, categories))) {
            return false;
        }

        if ((cheat.School.Length > 0 && !cheat.School.Equals((string) spell.m_sMagicSchoolName, StringComparison.OrdinalIgnoreCase))
            || (cheat.Kind.Length > 0 && !cheat.Kind.Equals((string) spell.m_sTypeName, StringComparison.OrdinalIgnoreCase))) {
            return false;
        }

        var cost = spell.m_spellRank is { m_xPipSpell: false } rank ? rank.m_spellRank : 0;
        if ((cheat.CostMin > 0 || cheat.CostMax != 0) && !SpellNameFamily.CostMatches(cost, cheat.CostMin, cheat.CostMax)) {
            return false;
        }

        if (cheat.CasterSchool is { Length: > 0 } casterSchool) {
            var school = mover?.ParticipantGameStats is { } stats ? ((MagicSchool) stats.m_schoolID).ToString() : null;
            if (!casterSchool.Equals(school, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        if (cheat.Spells is { Count: > 0 }) {
            string name = spell.m_name;
            string displayKey = spell.m_displayName;
            var shown = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);
            string spellBase = spell.m_spellBase;

            return SpellNameFamily.IsOneOf(cheat.Spells, cheat.AnyVersion, name, shown, cheat.AnyVersion ? spellBase : null);
        }

        return true;
    }

    private static readonly HashSet<kEffectTarget> s_cheatManyTargets = [
        kEffectTarget.kEnemyTeam, kEffectTarget.kEnemyTeamAllAtOnce, kEffectTarget.kFriendlyTeam,
        kEffectTarget.kFriendlyTeamAllAtOnce, kEffectTarget.kAtLeastOneEnemy, kEffectTarget.kMultiTargetEnemy,
        kEffectTarget.kMultiTargetFriendly, kEffectTarget.kGlobal,
    ];

    /// <summary>What a card does, from its type and effects (a cheat's Category): Damage, Heal, Shield, Trap...</summary>
    internal static HashSet<string> CategoriesOf(SpellTemplate spell) {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string type = spell.m_sTypeName;
        type ??= "";
        foreach (var name in new[] { "Aura", "Global", "Polymorph", "Steal", "Shadow", "Enchantment", "Manipulation", "Charm", "Ward" }) {
            if (type.Contains(name, StringComparison.OrdinalIgnoreCase)) {
                found.Add(name);
            }
        }

        var effects = (spell.m_effects ?? []).SelectMany(CombatActionResolver.AllEffects).Where(e => e is not null).ToList();
        static bool Good(SpellEffect e) => e.m_disposition switch {
            kHangingDisposition.kBeneficial => true,
            kHangingDisposition.kHarmful => false,
            _ => e.m_effectType is kSpellEffects.kModifyIncomingDamage or kSpellEffects.kModifyIncomingDamageFlat
                ? e.m_effectParam < 0 : e.m_effectParam > 0,
        };

        foreach (var e in effects) {
            if (e.m_effectTarget == kEffectTarget.kGlobal) {
                found.Add("Global");
            }

            if (s_damageEffects.Contains(e.m_effectType)) {
                found.Add("Damage");
            }

            if (s_healEffects.Contains(e.m_effectType)) {
                found.Add("Heal");
            }

            switch (e.m_effectType) {
                case kSpellEffects.kSummonCreature or kSpellEffects.kSpawnCreature:
                    found.Add("Minion");
                    break;
                case kSpellEffects.kDamageOverTime:
                    found.Add("DoT");
                    break;
                case kSpellEffects.kHealOverTime:
                    found.Add("HoT");
                    break;
                case kSpellEffects.kStun:
                    found.Add("Stun");
                    break;
                case kSpellEffects.kDispel:
                    found.Add("Dispel");
                    break;
                case kSpellEffects.kPolymorph:
                    found.Add("Polymorph");
                    break;
                case kSpellEffects.kStealCharm or kSpellEffects.kStealWard or kSpellEffects.kStealHealth:
                    found.Add("Steal");
                    break;
                case kSpellEffects.kModifyIncomingDamage or kSpellEffects.kAbsorbDamage or kSpellEffects.kModifyIncomingDamageFlat:
                    found.Add(Good(e) ? "Shield" : "Trap");
                    break;
                case kSpellEffects.kModifyOutgoingDamage or kSpellEffects.kModifyOutgoingDamageFlat:
                    found.Add(Good(e) ? "Blade" : "Weakness");
                    break;
            }
        }

        if (found.Contains("Aura")) {
            found.Remove("Blade");
        }

        if (!found.Contains("Aura") && (found.Contains("Blade") || found.Contains("Weakness")
                                        || effects.Any(e => e.m_effectType is kSpellEffects.kModifyAccuracy or kSpellEffects.kCritBoost
                                            or kSpellEffects.kModifyOutgoingHeal or kSpellEffects.kModifyOutgoingArmorPiercing))) {
            found.Add("Charm");
        }

        if (found.Contains("Shield") || found.Contains("Trap") || effects.Any(e => e.m_effectType == kSpellEffects.kAbsorbDamage)) {
            found.Add("Ward");
        }

        if (spell.m_Treasure) {
            found.Add("Treasure");
        }

        return found;
    }

    // ---------------------------------------------------------------------
    // Do
    // ---------------------------------------------------------------------

    private float TryCheat(CombatDuelSubCircle boss, int index, BossCheats.BossCheat cheat, CombatDuelSubCircle trigger,
                           CombatDuelSubCircle receiver, string reason, CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (!boss.IsAlive || BattleDecided()) {
            return 0;
        }

        if (NotSupported(cheat) is { } missing) {
            var key = $"{CheatTemplateOf(boss)}:{index}";
            if (s_unsupportedLogged.Add(key)) {
                Logger.Information("Boss cheat {0} #{1} ({2}) is not supported yet ({3}); it is skipped.",
                    Logger.Args(CheaterName(boss), index + 1, string.IsNullOrEmpty(cheat.Notes) ? cheat.When : cheat.Notes, missing));
            }

            return 0;
        }

        if (!LimitsAllow(boss, index, cheat, trigger, receiver) || !PetMayCasts.Roll(cheat.Chance)) {
            return 0;
        }

        if (cheat.Once) {
            _cheatsUsedOnce.Add((boss, index));
        }

        var count = _cheatCounts.GetValueOrDefault((boss, index)).Times;
        _cheatCounts[(boss, index)] = (count + 1, (int) _duel.m_roundNum);
        var steps = PickCheatSteps(cheat.Steps(), count);
        Logger.Information("Duel {0} | Slot {1} | {2} cheats after {3}: {4}.",
            Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, CheaterName(boss), reason, string.Join(", ", steps.Select(CheatStepText))));

        return RunCheatSteps(boss, cheat, steps, trigger, receiver, combatActionList, cinematicTimeSoFar);
    }

    // PickOne keeps one of its next Count choices (by Weights); InTurn the next one in turn each time the cheat goes off.
    private static List<BossCheats.CheatAction> PickCheatSteps(List<BossCheats.CheatAction> steps, int timesBefore) {
        var result = new List<BossCheats.CheatAction>();
        for (var i = 0; i < steps.Count; i++) {
            var step = steps[i];
            if (!step.Is("PickOne") && !step.Is("InTurn")) {
                result.Add(step);
                continue;
            }

            var group = Math.Max(1, step.Group);
            var rest = steps.Count - i - 1;
            var count = step.Count > 0 ? Math.Min(step.Count, rest / group) : rest / group;
            if (count <= 0) {
                continue;
            }

            int pick;
            if (step.Is("InTurn")) {
                pick = timesBefore % count;
            }
            else {
                var weights = (step.Weights ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => int.TryParse(w.Trim(), out var n) ? Math.Max(0, n) : 1).ToList();
                while (weights.Count < count) {
                    weights.Add(1);
                }

                var total = weights.Take(count).Sum();
                var roll = total > 0 ? System.Random.Shared.Next(total) : 0;
                pick = 0;
                while (pick < count - 1 && roll >= weights[pick]) {
                    roll -= weights[pick];
                    pick++;
                }
            }

            result.AddRange(steps.Skip(i + 1 + pick * group).Take(group));
            i += count * group;
        }

        return result;
    }

    private static string CheatStepText(BossCheats.CheatAction step)
        => step.Do + (step.Spell.Length > 0 ? $" {step.Spell}" : step.SpellID != 0 ? $" {step.SpellID}" : "")
           + (step.Line.Length > 0 ? $" \"{step.Line}\"" : "") + (step.Target.Length > 0 ? $" on {step.Target}" : "");

    private float RunCheatSteps(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, List<BossCheats.CheatAction> steps,
                                CombatDuelSubCircle trigger, CombatDuelSubCircle receiver, CombatActionListObj combatActionList,
                                float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        var lines = new List<string>();
        var defeat = false;
        _lastCheatTargets = [];
        for (var s = 0; s < steps.Count; s++) {
            var step = steps[s];
            if (!boss.IsAlive && !step.Is("Say")) {
                break;
            }

            switch (step.Do.ToLowerInvariant()) {
                case "wait": {
                    var later = steps.Skip(s + 1).ToList();
                    if (later.Count > 0) {
                        _waitingCheatSteps.Add(new WaitingCheatSteps(boss, cheat, later, (int) _duel.m_roundNum + Math.Max(1, step.Rounds), trigger, receiver));
                    }

                    s = steps.Count;
                    break;
                }
                case "cast" or "polymorph":
                    cinematicTime += CheatCast(boss, boss, cheat, step, trigger, receiver, lines, combatActionList, cinematicTimeSoFar + cinematicTime);
                    break;
                case "castby": {
                    var by = CheatCaster(boss, step);
                    if (by is null) {
                        break;
                    }

                    if (by != boss) {
                        cinematicTime += ShowCheatLines(lines, boss, combatActionList);
                    }

                    cinematicTime += CheatCast(boss, by, cheat, step, trigger, receiver, by == boss ? lines : [], combatActionList,
                        cinematicTimeSoFar + cinematicTime);
                    break;
                }
                case "say":
                    if (step.Line.Length > 0) {
                        if (CheatLineKey(step.Line) is { } key) {
                            lines.Add(key);
                        }
                        else {
                            Logger.Warning("Duel {0} | Boss cheat line \"{1}\" is not one of the game's lines.", Logger.Args(_duel.m_duelID.Full, step.Line));
                        }
                    }

                    if (step.Text.Length > 0) {
                        TellCheatPlayers($"{CheaterName(boss)}: {step.Text}");
                    }

                    break;
                case "mark":
                    foreach (var who in CheatTargets(boss, step.Target.Length > 0 ? step.Target : "Caster", null, trigger, receiver)) {
                        if (!_cheatMarks.TryGetValue(who, out var marks)) {
                            _cheatMarks[who] = marks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        }

                        marks.Add(step.Mark.Length > 0 ? step.Mark : cheat.Mark);
                    }

                    break;
                case "unmark": {
                    var mark = step.Mark.Length > 0 ? step.Mark : cheat.Mark;
                    var whom = step.Target.Length > 0 && !step.Target.Equals("Marked", StringComparison.OrdinalIgnoreCase)
                        ? CheatTargets(boss, step.Target, null, trigger, receiver)
                        : _cheatMarks.Keys.ToList();
                    foreach (var who in whom) {
                        if (_cheatMarks.TryGetValue(who, out var marks)) {
                            marks.Remove(mark);
                        }
                    }

                    break;
                }
                case "sethealth" when step.Amount > 0:
                    SetCheaterHealth(boss, step.Amount);
                    break;
                case "healthto":
                    foreach (var who in CheatTargets(boss, step.Target.Length > 0 ? step.Target : "Self", null, trigger, receiver)) {
                        var health = Math.Clamp((int) ((long) MostHealthOf(who) * step.Amount / 100), 1, MostHealthOf(who));
                        var change = health - who.ParticipantGameStats.m_currentHitpoints;
                        if (change > 0) {
                            who.HealParticipant(change);
                        }
                        else if (change < 0) {
                            who.DamageParticipant(-change);
                        }

                        SyncCheatHealth(who);
                    }

                    break;
                case "heal":
                    foreach (var who in CheatTargets(boss, step.Target.Length > 0 ? step.Target : "Self", null, trigger, receiver)) {
                        who.HealParticipant(step.Percent ? (int) ((long) MostHealthOf(who) * step.Amount / 100) : step.Amount);
                        SyncCheatHealth(who);
                    }

                    break;
                case "pips":
                    foreach (var who in CheatTargets(boss, step.Target.Length > 0 ? step.Target : "Self", null, trigger, receiver)) {
                        ChangeCheatPips(who, step.Kind, step.Amount);
                    }

                    break;
                case "setphase" when step.Phase > 0:
                    // The new phase counts its rounds from the next round, and its health cheats can go off again.
                    _cheatPhases[boss] = (step.Phase, (int) _duel.m_roundNum + 1);
                    _healthCheatsFired.RemoveWhere(k => k.Boss == boss);
                    Logger.Information("Duel {0} | Slot {1} | Boss moves on to phase {2}.", Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, step.Phase));
                    break;
                case "defeat":
                    defeat = true;
                    break;
                case "removecharms" or "removewards": {
                    var kinds = step.Is("RemoveCharms") ? s_cheatCharmEffects : s_cheatWardEffects;
                    foreach (var who in CheatTargets(boss, step.Target.Length > 0 ? step.Target : "Self", null, trigger, receiver)) {
                        RemoveCheatHanging(who, kinds, step.Kind, step.Count);
                    }

                    break;
                }
            }
        }

        if (defeat && boss.IsAlive) {
            boss.DamageParticipant(boss.ParticipantGameStats.m_currentHitpoints);
            SyncCheatHealth(boss);
        }

        cinematicTime += ShowCheatLines(lines, boss, combatActionList);

        return cinematicTime;
    }

    // Steps a Wait put off, due at the start of this round.
    private float WaitedCheatSteps(CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        var due = _waitingCheatSteps.Where(w => w.Round <= (int) _duel.m_roundNum).ToList();
        foreach (var waiting in due) {
            _waitingCheatSteps.Remove(waiting);
            if (waiting.Boss.IsAlive && !BattleDecided()) {
                cinematicTime += RunCheatSteps(waiting.Boss, waiting.Cheat, waiting.Steps, waiting.Trigger, waiting.Receiver,
                    combatActionList, cinematicTimeSoFar + cinematicTime);
            }
        }

        return cinematicTime;
    }

    // A free spell: no pips, never fizzles, works through stun. A Dispel of its school on the caster stops it unless
    // the step is dispel-proof (DispelImmune, true if left out).
    private float CheatCast(CombatDuelSubCircle boss, CombatDuelSubCircle by, BossCheats.BossCheat cheat, BossCheats.CheatAction step,
                            CombatDuelSubCircle trigger, CombatDuelSubCircle receiver, List<string> lines,
                            CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var (spell, template) = CheatSpell(step.Spell, step.SpellID);
        if (spell is null || template is null) {
            Logger.Warning("Duel {0} | Boss cheat spell \"{1}\" of {2} not found.",
                Logger.Args(_duel.m_duelID.Full, step.Spell.Length > 0 ? step.Spell : step.SpellID.ToString(), CheaterName(boss)));

            return 0;
        }

        spell = spell with { m_pipCost = new SpellRank() };
        var aim = step.Target.Length > 0 ? step.Target : cheat.Target;
        var cinematicTime = 0.0f;
        var targets = CheatTargets(boss, aim, template, trigger, receiver);
        _lastCheatTargets = targets;
        foreach (var target in targets) {
            if (!by.IsAlive || target is null || !target.IsAlive || !target.AddedToDuel) {
                continue;
            }

            by._duelActor.CurrentActionCinematicOffsetSeconds = cinematicTimeSoFar + cinematicTime;
            var before = combatActionList.m_actionList.Count;
            var action = new QueuedCombatAction { SpellCaster = by, SelectedTarget = target, Spell = spell, SpellTemplate = template };
            var time = !step.DispelImmune && ConsumeDispell(by, spell.m_magicSchoolID)
                ? HandleFizzleAction(action, combatActionList)
                : HandleSuccessfulAction(action, combatActionList, alreadyPaid: true);

            // The game takes a card's pips from its rank, so the copy it plays has the rank taken off: no pips are lost.
            var rank = template.m_spellRank?.m_spellRank ?? 0;
            for (var i = before; i < combatActionList.m_actionList.Count; i++) {
                var done = combatActionList.m_actionList[i];
                if (rank > 0 && ReferenceEquals(done.m_spell, spell)) {
                    done.m_spell = spell with { m_regularAdjust = spell.m_regularAdjust - rank };
                }

                if (step.Silent) {
                    done.m_showCast = false;
                }
            }

            cinematicTime += step.Silent ? Math.Min(time, 1.0f) : time;

            // The cheat's line shows over the boss just before its spell.
            if (lines.Count > 0 && before < combatActionList.m_actionList.Count) {
                cinematicTime += InsertCheatLines(combatActionList, before, combatActionList.m_actionList[before], lines, boss.SlotIndex);
                lines.Clear();
            }
        }

        return cinematicTime;
    }

    private static (Spell Spell, SpellTemplate Template) CheatSpell(string name, uint id) {
        var spell = id != 0 ? SpellFactory.GetSpell(id) : null;
        if (spell is null && !string.IsNullOrWhiteSpace(name)) {
            spell = SpellFactory.Exists(name) ? SpellFactory.GetSpell(name) : null;
        }

        return spell is not null && CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is SpellTemplate template
            ? (spell, template) : (null, null);
    }

    // CastBy: someone else on its side casts (a creature of a template, an ally, the weakest or strongest).
    private CombatDuelSubCircle CheatCaster(CombatDuelSubCircle boss, BossCheats.CheatAction step) {
        var allies = Standing(c => c != boss && c.OccupiedTeam == boss.OccupiedTeam).ToList();

        return (step.By ?? "").ToLowerInvariant() switch {
            "creature" => allies.FirstOrDefault(c => CheatTemplateOf(c) == step.Creature),
            "weakestally" => allies.OrderBy(HealthPercent).FirstOrDefault(),
            "strongestally" => allies.OrderByDescending(c => c.ParticipantGameStats.m_currentHitpoints).FirstOrDefault(),
            "self" or "" => step.Creature > 0 ? allies.FirstOrDefault(c => CheatTemplateOf(c) == step.Creature) : boss,
            _ => allies.Count == 0 ? null : allies[System.Random.Shared.Next(allies.Count)],
        };
    }

    /// <summary>Who a step goes on. An empty aim follows the spell: itself or its side for a helping spell, else a player.</summary>
    private List<CombatDuelSubCircle> CheatTargets(CombatDuelSubCircle boss, string aim, SpellTemplate template,
                                                   CombatDuelSubCircle trigger, CombatDuelSubCircle receiver) {
        var enemies = Standing(c => c.OccupiedTeam != boss.OccupiedTeam).ToList();
        var allies = Standing(c => c != boss && c.OccupiedTeam == boss.OccupiedTeam).ToList();
        CombatDuelSubCircle RandomOf(List<CombatDuelSubCircle> list) => list.Count == 0 ? null : list[System.Random.Shared.Next(list.Count)];
        List<CombatDuelSubCircle> One(CombatDuelSubCircle c) => c is null ? [] : [c];

        switch ((aim ?? "").Trim().ToLowerInvariant()) {
            case "self" or "owner":
                return [boss];
            case "caster" or "attacker" or "matched":
                return One(trigger is { IsAlive: true } ? trigger : null);
            case "receiver":
                return One(receiver is { IsAlive: true } ? receiver : null);
            case "enemy":
                return One(RandomOf(enemies));
            case "firstenemy":
                return One(enemies.FirstOrDefault());
            case "allenemies":
                return enemies;
            case "weakestenemy":
                return One(enemies.OrderBy(HealthPercent).FirstOrDefault());
            case "mostpips":
                return One(enemies.OrderByDescending(c => CheatPipCount(c, "")).FirstOrDefault());
            case "ally":
                return One(RandomOf(allies));
            case "allallies":
                return [boss, .. allies];
            case "weakestally":
                return One(allies.Append(boss).OrderBy(HealthPercent).FirstOrDefault());
            case "strongestally":
                return One(allies.Append(boss).OrderByDescending(c => c.ParticipantGameStats.m_currentHitpoints).FirstOrDefault());
            case "last":
                return _lastCheatTargets.Where(c => c.IsAlive).ToList();
            case "marked":
                return [.. _cheatMarks.Where(m => m.Value.Count > 0 && m.Key.IsAlive).Select(m => m.Key)];
        }

        // As the spell aims: a helping spell on itself, anything else on the player who set it off, else a random one.
        var helps = template is not null && (template.m_effects ?? []).SelectMany(CombatActionResolver.AllEffects)
            .Where(e => e is not null).All(e => e.m_effectTarget is kEffectTarget.kSelf or kEffectTarget.kFriendlySingle
                or kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce or kEffectTarget.kFriendlySingleNotMe
                or kEffectTarget.kMultiTargetFriendly or kEffectTarget.kGlobal or kEffectTarget.kInvalidTarget);
        if (helps) {
            return [boss];
        }

        return One(trigger is { IsAlive: true } && trigger.OccupiedTeam != boss.OccupiedTeam ? trigger : RandomOf(enemies));
    }

    // ---------------------------------------------------------------------
    // Lines, health, pips, charms
    // ---------------------------------------------------------------------

    /// <summary>The locale key of a line: the key itself, or the game's line that best matches the words.</summary>
    private static string CheatLineKey(string line) {
        line = (line ?? "").Trim();
        if (line.Contains('_') && !line.Contains(' ') && Locale.GetEnglishName(line) is { Length: > 0 }) {
            return line;
        }

        return Locale.Search(line, 1) is { Count: > 0 } found ? found[0].Key : null;
    }

    // The game shows an action carrying a message (m_stringKeyMessage) as the boss's line and does not cast its spell,
    // so each line goes on its own copy of a spell action, cast by the boss so the line shows over it.
    private float InsertCheatLines(CombatActionListObj combatActionList, int index, CombatAction source, List<string> keys, int slot) {
        combatActionList.m_actionList.InsertRange(index, keys.Select(key => source with {
            m_spellCaster = slot,
            m_stringKeyMessage = key,
            m_showCast = false,
        }));

        return keys.Count * CHEAT_LINE_SECONDS;
    }

    // Lines with no spell of the cheat to go with: after the last spell of the round so far, else in chat.
    private float ShowCheatLines(List<string> keys, CombatDuelSubCircle boss, CombatActionListObj combatActionList) {
        if (keys.Count == 0) {
            return 0;
        }

        var last = combatActionList.m_actionList.FindLastIndex(a => a.m_spell is not null);
        float seconds;
        if (last >= 0) {
            seconds = InsertCheatLines(combatActionList, last + 1, combatActionList.m_actionList[last], keys, boss.SlotIndex);
        }
        else {
            foreach (var key in keys) {
                TellCheatPlayers($"{CheaterName(boss)}: {Locale.GetEnglishName(key)}");
            }

            seconds = 0;
        }

        keys.Clear();

        return seconds;
    }

    private void TellCheatPlayers(string text) {
        foreach (var player in _subCircles.Where(c => c is { Occupied: true } && c.IsHumanPlayer)) {
            player.ParticipantActor?.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });
        }
    }

    // SetHealth: the boss's most and current health become this (a second form).
    private void SetCheaterHealth(CombatDuelSubCircle boss, int health) {
        if (boss.IsHumanPlayer) {
            return;
        }

        var stats = boss.ParticipantGameStats;
        stats.m_baseHitpoints = health;
        stats.m_currentHitpoints = health;
        if (boss.CombatParticipant is not null) {
            boss.CombatParticipant.m_maxPlayerHealth = health;
            boss.CombatParticipant.m_playerHealth = health;
        }

        Logger.Information("Duel {0} | Slot {1} | Boss health set to {2}.", Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, health));
    }

    // A creature's health as the game is told it (a wizard's goes through UpdateHealth).
    private static void SyncCheatHealth(CombatDuelSubCircle who) {
        if (who._wizard is null && who.CombatParticipant is not null) {
            who.CombatParticipant.m_playerHealth = who.ParticipantGameStats.m_currentHitpoints;
        }
    }

    private static void ChangeCheatPips(CombatDuelSubCircle who, string kind, int amount) {
        if (who.CombatParticipant?.m_pipCount is not { } pips || amount == 0) {
            return;
        }

        static byte Clamp(int n) => (byte) Math.Clamp(n, 0, 14);
        switch ((kind ?? "").ToLowerInvariant()) {
            case "power":
                pips.m_powerPips = Clamp(pips.m_powerPips + amount);
                break;
            case "shadow":
                pips.m_shadowPips = Clamp(pips.m_shadowPips + amount);
                break;
            default:
                pips.m_genericPips = Clamp(pips.m_genericPips + amount);
                break;
        }
    }

    private static readonly HashSet<kSpellEffects> s_cheatCharmEffects = [
        kSpellEffects.kModifyOutgoingDamage, kSpellEffects.kModifyOutgoingDamageFlat, kSpellEffects.kModifyOutgoingHeal,
        kSpellEffects.kModifyOutgoingHealFlat, kSpellEffects.kModifyAccuracy, kSpellEffects.kCritBoost,
        kSpellEffects.kModifyOutgoingArmorPiercing, kSpellEffects.kDispel,
    ];

    private static readonly HashSet<kSpellEffects> s_cheatWardEffects = [
        kSpellEffects.kModifyIncomingDamage, kSpellEffects.kModifyIncomingDamageFlat, kSpellEffects.kAbsorbDamage,
        kSpellEffects.kModifyIncomingHeal, kSpellEffects.kModifyIncomingHealFlat, kSpellEffects.kModifyIncomingDamageType,
        kSpellEffects.kCritBlock, kSpellEffects.kModifyIncomingArmorPiercing,
    ];

    // RemoveCharms / RemoveWards: Kind Good, Bad or All (""), newest first, Count of them (0 = all).
    private static void RemoveCheatHanging(CombatDuelSubCircle who, HashSet<kSpellEffects> kinds, string kind, int count) {
        var hanging = who._hangingEffects;
        if (hanging is null) {
            return;
        }

        static bool Good(SpellEffect e) => e.m_disposition switch {
            kHangingDisposition.kBeneficial => true,
            kHangingDisposition.kHarmful => false,
            _ => e.m_effectType is kSpellEffects.kModifyIncomingDamage or kSpellEffects.kModifyIncomingDamageFlat
                ? e.m_effectParam < 0 : e.m_effectParam > 0,
        };

        var removed = 0;
        for (var i = hanging.Count - 1; i >= 0 && (count <= 0 || removed < count); i--) {
            var e = hanging[i];
            if (e is null || !kinds.Contains(e.m_effectType)) {
                continue;
            }

            var keep = (kind ?? "").ToLowerInvariant() switch {
                "good" => !Good(e),
                "bad" => Good(e),
                _ => false,
            };
            if (!keep) {
                hanging.RemoveAt(i);
                removed++;
            }
        }
    }

}
