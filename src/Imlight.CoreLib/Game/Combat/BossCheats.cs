/*
 * Ported from Niduus/Imlight (quality-assurance branch): the cheat file format and loader. Our engine
 * (CombatResolver.BossCheats.cs) runs the blocks listed there as supported; others are logged and skipped.
 *
 * Boss cheats (Niduus, 2026-10-21), read from spiraldb-extra/BossCheats/*.json, one file per boss template.
 *
 * A cheat is a move a boss makes outside its own turn: a spell cast for free, a line shown over the boss, or both.
 * The game files hold the cheat spells (Spells/G14-CombatTrigger-Spells, AZ-CombatTrigger-Spells and others) and the
 * cheat lines, but not when a boss uses them; that lives on the real game's servers, so each boss's file says it.
 *
 * When:
 *   BattleStart   the boss's first round (a boss added with .joinme starts its own count).
 *   RoundStart    the start of a round: Round = the boss's Nth round, Every = every Nth round, neither = each round,
 *                 both = round Round, then every Every rounds after it (Round 3, Every 5: rounds 3, 8, 13...).
 *                 NoAllies: only when no creature is left standing on the boss's side.
 *   RoundEnd      the end of a round, after everyone's move, counted the same way.
 *   HealthBelow   the boss's health falls under Percent of its most, or under Health (hit points) when that is set.
 *   PlayerCasts   a player's spell lands. School, Kind (the card's type: Damage, Heal, Ward, Charm, Aura, Global,
 *                 Steal, Manipulation, Shadow...) and Spells (card names) narrow it. OnBoss: only a spell aimed at
 *                 the boss. SingleTarget: only a spell that hits one target. NotOnCaster: only a spell aimed at
 *                 someone other than the one who cast it (a blade on another wizard; any When that looks at a spell).
 *                 NotCategory: only a spell that is none of these kinds (commas between; the Sphinx's wrong answer).
 *   RoundEnd with Pips (PipsOf, PipsKind as for When Pips): only if those pips are still there at the end of the round.
 *   PlayerPasses  a player passes.
 * Target: Self, Caster (the player who set it off), Receiver (whoever that player's spell was aimed at), Enemy (a random
 * player), FirstEnemy, AllEnemies, or empty to pick as the spell aims. Line: words from the game's own line (as .ca
 * finds it) or its locale key. Once: only the first time. Chance: percent, 100 if left out.
 * Phase: the cheat works only in that phase of the fight (0 or left out = every phase; a boss starts in phase 1).
 * Rounds count from the start of the phase. SetPhase moves the boss on to that phase from the next round.
 * SetHealth: the boss's most and current health become this, even after it was defeated (a second form).
 * Defeat: the boss is defeated after the cheat, if its spell did not do it.
 * Spawn: a creature template that comes into the battle on the boss's side (a second creature with its own model, such
 * as a minion). It takes a free enemy circle and fights from the next round with its own cheat file, counting its
 * rounds from its first round; the boss is defeated (with Defeat) and the line shows over it, and Cast and SetPhase are
 * not used. With no circle free, SetHealth (if set) brings the boss back in its own circle instead, as if there were
 * no Spawn and no Defeat. Drops stay with the boss's own template (NpcDropTable).
 * Silent: the cheat's spell goes off without the casting animation (its line still shows).
 * A boss polymorphed into another creature for the rest of the battle (a second form in its own circle, such as Haunted
 * Malistaire through "Afterlife - Malistaire 02") uses that creature's cheat file from then on, if it has one, starting
 * again at phase 1 from the next round (CombatPolymorph.cs).
 * Casts: several spells instead of Cast, each { "Spell": name, "SpellID": id (wins over the name), "Silent": true,
 *   "DispelImmune": false }, cast in order, or one of them at random with Random: true. A cheat's spell never fizzles
 *   and works through stun. A Dispel of the spell's school on the boss stops it as it stops a player's spell.
 *   DispelImmune (true if left out) is the old dispel-proof: on a cheat with no DispelThen it now means the spell is
 *   cast again until it gets through, as "DispelThen": "Retry", "RetryUntilCast": true (Niduus, 2026-10-23). The app
 *   writes false for its casts and uses the "if dispelled" block instead.
 * Passive: a cheat with When "Passive" has no trigger and never fires; it is always true (Niduus, 2026-10-24).
 *   Passive "Circle", Circle n (1-4): it takes circle n of its side whenever it comes into a battle (the battle's
 *   start, walking in, a late join or a summon); circle 1 is the first of its side as the game numbers them. If
 *   someone already stands there, it takes the free circle of its side nearest to circle n. Players too (PlayerCheats).
 * The app's Bosses tab changes, turns off and adds cheats in BossOverrides/<template id>.json, laid over this file
 * (BossOverrides.CheatsOf); this file itself is never changed by it.
 *
 * Custom cheats (Niduus, 2026-10-22: "Custom Bosses so cheats can have their conditions and such able to be modified and
 * created"), made as When -> If -> Do blocks in the app, add to the fields above:
 *   When         also BossHit (a spell from the other side lands on the boss and hurts it: School, Kind, Category and
 *                Spells narrow it), MinionDefeated (a creature on the boss's side is defeated), BossDefeated (the boss itself is defeated; spell Ifs look at the spell that did it) and Pips (at the start
 *                of a round, the boss, or with PipsOf "Enemy" any player, has at least Pips pips; a power pip is one).
 *   Category     for PlayerCasts and BossHit: what the card does, worked out from its effects: Damage, Heal, Shield,
 *                Trap, Blade, Weakness, Aura, Global, Minion, Stun, Steal, Dispel, Polymorph.
 *   CasterSchool only when the player who set it off is of this school.
 *   HealthAbove, HealthUnder   only while the boss's health is above / under this percent of its most.
 *   MaxTimes     at most this many times a battle (0 = no limit). Cooldown: at least this many rounds between two.
 *   DispelThen   "Retry": a cheat spell a Dispel on the boss stops is cast again, DispelRetries more times, or until it gets through with RetryUntilCast (20 tries at most).
 *   Actions      what it does, in order, after the fields above (Cast, Line, SetPhase... count as the first actions):
 *     { "Do": "Cast", "Spell"/"SpellID", "Target", "Silent", "DispelImmune" }   a free spell.
 *     { "Do": "Polymorph", "Spell", "Target" }   a polymorph spell. One that changes the boss for good (Afterlife -
 *                                           Malistaire 02, a kPolymorph) goes on the boss itself unless Target says
 *                                           otherwise; one that attacks in another shape (Polymorph - Dragon NA: he
 *                                           turns into the Black Dragon, hits a player and turns back, all in its
 *                                           cinematic) aims as Target or the spell says. Each is logged as
 *                                           "... polymorph with <spell>: ...".
 *     { "Do": "Interrupt" }                 the player's spell that set it off fizzles. A cheat with it (PlayerCasts
 *                                           or BossHit) goes off before that spell instead of after it.
 *     { "Do": "RemoveCharms"/"RemoveWards", "Kind": "Good"/"Bad"/"All", "Count" (0 = all), "Target" }
 *     { "Do": "AddEffects", "Spell", "Target" }   the charms and wards of a spell go on, with no casting.
 *     { "Do": "Heal", "Amount", "Percent" (Amount is percent of the most health), "Target" (Self if left out) }
 *     { "Do": "Minion", "Creature", "Custom" }    a creature (or a custom of the Customs tab) joins the boss's side.
 *     { "Do": "Form", "Creature", "Custom", "ReplaceBoss" }   a second form, as Spawn (ReplaceBoss as Defeat).
 *     { "Do": "SetHealth", "Amount" }  { "Do": "SetPhase", "Phase" }  { "Do": "Defeat" }
 *     { "Do": "Say", "Line" }               a game line over the boss; "Text": words sent to every player's chat.
 *     { "Do": "Pips", "Amount" (below 0 takes), "Kind": "Normal"/"Power"/"Shadow", "Target" (Self if left out) }
 *   An action with "Off": true is skipped. Target also takes Attacker (as Caster), WeakestEnemy, Ally (a random one
 *   on the boss's side) and AllAllies.
 *
 * More (Niduus, 2026-10-22: "Get more expansive with cheats"; CombatResolver.CheatSteps.cs). Old files read as before:
 *   Spells + AnyVersion   a plain name is every version of that spell ("Scald" is also "Scald (Treasure Card)",
 *                "Scald - Item Card", "Scald - Mob"...; SpellNameFamily.cs). Without AnyVersion, exact names as before.
 *                Cast, Polymorph and AddEffects take a plain name too: a Spell that is no spell's own name casts the
 *                plainest spell of that family. RemoveCharms / RemoveWards take a Spell: only those from that spell.
 *   Category     may hold several kinds with commas ("Charm, Ward"); also Charm (any blade, weakness or other charm),
 *                Ward (any shield, trap or absorb), Shadow, Enchantment and Treasure.
 *   CostMin, CostMax   the pip cost of the spell (CostMax 0 = no top; an X spell costs 0).
 *   When         also PlayerHeals, PlayerCharms, PlayerWards, PlayerAuras, PlayerGlobals, PlayerMinions, PlayerShadows,
 *                PlayerDamages (PlayerCasts for that kind of card), PlayerCrits (a player's spell was a critical hit),
 *                PlayerDefeated (a player falls; Receiver is that player), PlayerHealthBelow (a player's health first
 *                drops under Percent, or Health; once each player; Caster is that player), PlayerFlees (a player ran;
 *                answered at the start of the next round) and FromRound (every round, or every Every rounds, from
 *                round Round on: an enrage).
 *   PlayersMin, PlayersMax   only while this many players stand. FromRound, ToRound: only in these battle rounds.
 *   HasAllies    only while someone else on its side stands.
 *   Actions also:
 *     { "Do": "Reflect" }                   the player's spell fizzles and the boss casts it back (as Interrupt, before).
 *     { "Do": "StealPips", "Amount", "Kind", "Target" (Caster if left out) }   taken from them, given to the boss.
 *     { "Do": "RemoveAuras", "Target" (AllEnemies) }
 *     { "Do": "AddCharm", "Kind": Blade/Weakness/Shield/Trap/Absorb, "Amount" (percent; hit points for Absorb),
 *       "School" ("" = all), "Target" (Self, or Caster for a weakness or trap) }   used up as a real one. Kind DoT or
 *       HoT: Amount in all, over Rounds rounds (3 if 0). The game draws a charm or ward only from a spell it sees
 *       cast, so a real one of that kind and school is cast (Shadowblade for a Shadow blade; Balanceblade, Hex,
 *       Tower Shield, Weakness or Spirit Armor for all schools) and its effect is then given this size on the server.
 *       "Spell"/"SpellID": cast that spell instead, and the effect keeps its id (two of one spell count once on a hit,
 *       as real ones do). The game shows the real spell's own size; the server deals with this one. DoT and HoT are
 *       not drawn. "Look": the cast animation, Sun, Shadow, Moon, Star or one of the seven schools ("" = normal). The
 *       game plays the casting of the cast spell's own school, so a real spell of that school and kind is cast
 *       (Sun: Sun Blade Sun on itself, else Dragonblade TC-01; Shadow: Shadowblade, Shadow Trap, Shadow Shield...). A
 *       kind and look the game has no spell for (a Sun trap) is cast normally. With "Spell", that spell's school decides.
 *       Kind Global (more damage of School, "" = all), HealGlobal (more healing) or GloomGlobal (less healing, as Doom
 *       and Gloom): a battlefield bubble of Amount percent that takes the current one's place, as a real one does. Its
 *       school's real bubble (Wyldfire, Balefrost, Darkwind, Blood Moon, Circle of Thorns, Deadzone, Balance of Power,
 *       ShadowGlobal; Power Play for all; Sanctuary; Doom and Gloom) is cast so the game draws it, then given this size
 *       and school on the server. The lookalikes are listed in CheatLookalikes.cs.
 *     { "Do": "Announce", "Text", "Style": Box/Chat/Both (Box), "Who": ""/Players }   a server announcement (as the
 *       Manager's Announcement card sends) to the wizards in this battle only. {boss}, {player} and {round} in Text are
 *       filled in. Unlike Say (a game line over the boss, or chat with its name in front), it reads as from the server.
 *     { "Do": "IfCleared", "Count" (1), "Amount" (1), "Each" }   the next Count steps only if the removal steps before
 *       it (RemoveEffects, RemoveCharms, RemoveWards, RemoveAuras) took off at least Amount effects; with Each, once for
 *       each one taken off. "Clear DoT for +55% Storm blade": RemoveEffects Kind DoT Count 1 Target Receiver, then
 *       IfCleared, then AddCharm Blade 55 Storm Target Self.
 *     { "Do": "Boost", "Kind": Damage/Resist/Heal/Accuracy, "Amount" (percent, below 0 lowers), "School", "Rounds"
 *       (0 = the rest of the battle), "Target" (Self) }   an aura-like effect that is never used up (enrage: Boost
 *       Damage 10 on a FromRound cheat; it stacks). A new aura cast on that target takes it off, as auras do.
 *     { "Do": "Immune", "School" ("" = all), "Rounds", "Target" (Self) }   a -100% ward of that school that is never
 *       used up (pierce still gets through, as against any shield).
 *     { "Do": "Stun", "Rounds" (1), "Target" (Caster) }   as a stun spell does, with no animation.
 *     { "Do": "HealthTo", "Amount" (percent of the most health), "Target" (Self) }   never below 1.
 *     { "Do": "SwapHealth", "Target" (Caster) }   the boss and the target swap their share of health.
 *     { "Do": "PickOne", "Count" (0 = all the rest), "Weights": "2, 1, 1" }   of the next Count actions only one goes
 *       off, picked at random by Weights (1 each if left out).
 * Each cheat made or changed in the app (and each cheat of a custom) writes "Custom cheat | ..." to the log as it goes off.
 * Player cheats (PlayerCheats.cs) use these same cheats for the wizard played on the server's own PC.
 *
 * For players and creatures alike (Niduus, 2026-10-23: "IMPLEMENT RULE. ALL CHEATS are to be compatible for PLAYERS and
 * CREATURES"): every When, If and Do works in a boss's file and in the player cheats, through one set of code
 * (CombatResolver.BossCheats.cs). "It" or "the boss" is whoever the cheats are of; "a player" is someone on the other
 * side (for a boss a wizard, for a wizard any creature there).
 *   When         also ICast (its own spell, just before it lands), ICrit (its own critical hit), IPass (it passes),
 *                EnemyCasts (a spell from the other side, just before it lands), EnemyJoins (someone joins the other side
 *                and its first round is after Round of the battle; Minions: a summoned minion counts too; "the
 *                attacker" is the newcomer) and AllyHealthBelow (someone else on its side first drops under Percent, or
 *                Health; once each; "the attacker" is that one), SideFizzles (a spell of its own or of someone on
 *                its side fizzles; "the attacker" is the one whose spell fizzled; Spells, School, Category narrow it).
 *                For a player's fizzle use PlayerCasts with Fizzle "Fizzled". MyHealthBelow is HealthBelow, as the player cheats
 *                first named it.
 *   Creature, NotCreature   only if the one the When is about (the one that fell for MinionDefeated / PlayerDefeated,
 *                else the one whose move, health or coming in set it off) is of this creature template (NotCreature:
 *                is not).
 *   Actions also:
 *     { "Do": "FreeCast", ... }             the player cheats' free cast (PlayerCheats.cs), for a boss too.
 *     { "Do": "Wait", "Rounds" }            the steps after it go off at the start of the round Rounds rounds later, if
 *                                           it still stands (Rounds 4: a minion falls in round 3, they go in round 7).
 *   Target also: Summoner (who summoned this minion, while it stands; else no one), LastAttacker (the last one on the other side whose
 *   spell hurt it; no one when that one has fallen), WeakestAlly (the lowest share of health on its side), StrongestAlly
 *   (the most health on its side), and the player cheats' me, p1-p4 (its own side's circles), e1-e4 and all.
 *   Say's Text in a player cheat goes to that wizard's own chat. SetHealth on a wizard sets only their health (their most
 *   health is their own).
 *
 * The gauntlets (Niduus, 2026-10-24: Wizard404's cheats for every housing gauntlet; players and creatures alike):
 *   Passive "Minions"   Brings (creature ids), BringsCount of BringsRandom: it comes into a battle with them on its
 *                side. A battle started against it gathers them as a ZoneObjects encounter does (the zone's own ones
 *                first, then summoned); anyone else who has it (a wizard, a boss that walks in) gets them summoned.
 *                With the If "how many players" (PlayersMin / PlayersMax) it is a cheat of its own: its creatures come
 *                too once that many players are in the battle, while it is in its first round (Wizard404's player
 *                counts, such as Dr. Neaux's extra minion for 3 wizards). A creature may have several.
 *   When AllyHit       a spell from the other side hurts someone else on its side (School, Category narrow it);
 *                "Hit" is that ally, the attacker the one who cast it.
 *   HasWho, HasKind, HasSpell, HasNot   only if Caster (default), Receiver or Self has a charm, ward, aura or over-time of
 *                HasKind (Shield, Trap, Blade, Weakness, Charm, Ward, Aura, DoT, HoT; several with commas) or from the
 *                spell HasSpell; HasNot: only if none.
 *   AlliesUp, AlliesDown   only while each of these creatures (ids) stands on its side, and none of these does.
 *   AlliesMin, AlliesMax   only while this many others stand on its side.
 *   Mark, MarkWho, NotMarked   only if MarkWho (Caster, Receiver, Self, AnyEnemy) has (NotMarked: has not) the mark.
 *   MultiTarget  only a spell that hits a whole side. NoAllies now counts for every When, not only a round's.
 *   Category also DoT and HoT (damage or healing over rounds).
 *   Actions also:
 *     { "Do": "CastBy", "By": Creature (with "Creature": its id) / Ally / WeakestAlly / StrongestAlly / Summoner,
 *       "Spell"/"SpellID", "Target", "Silent" }   someone else on its side casts it, free; nothing if no one stands.
 *     { "Do": "Mark", "Mark", "Target" (Caster) }  { "Do": "Unmark", "Mark", "Target" (Marked) }   a named mark, for the
 *                                           If above; it shows nothing in the game.
 *     { "Do": "InTurn", "Count", "Group" }   of the next Count choices of Group steps (1), the first goes off the first
 *                                           time the cheat does, the next the next time, and round again. PickOne takes
 *                                           Group too.
 *     { "Do": "OnlySchool", "School", "Count", "Target" (Last) }   the next Count steps only if the target is of School.
 *     FirstTurn Side "Swap": the side that went first goes second.
 *   Target also Last (whoever the step before went on; a step on itself does not count), Hit (the ally hit, for
 *   AllyHit) and Marked.
 *   A SetPhase back to a phase lets its HealthBelow cheats go off again. Line takes a locale key
 *   (CombatMessages_00000676) as well as words.
 *
 * Every creature's cheats (Niduus, 2026-10-24: "Take all the boss cheats and implement them"; Wizard404's notes for
 * each creature, made first from the blocks above, then from these few, for players and creatures alike):
 *   HasCount     the If "has" needs at least this many such effects (0 or 1 = one): Coatl answers a fourth blade.
 *   HasWho       also AnyEnemy (someone on the other side has it; HasNot: no one there) and AnyAlly (someone on its own
 *                side, itself too); the one found is the target Matched.
 *   PipsKind     for When Pips: "Shadow" counts only shadow pips (Vigilant Adapa's Constrain for a shadow pip).
 *   CreatureWho  "Receiver": for a When about a spell, Creature / NotCreature looks at whoever the spell was aimed at (a
 *                jinx on a Sea Lion), not its caster.
 *   CostMax      -1: only a spell of 0 pips (an X spell or a 0-pip card).
 *   Actions also:
 *     { "Do": "RemoveEffects", "Kind" (Shield, Trap, Blade, Weakness, Charm, Ward, Aura, DoT, HoT; commas; empty = all),
 *       "Count" (0 = all), "Spell", "Target" (Self) }   its hanging effects of that kind come off, newest first; protected
 *                                           ones stay (Personal Daemon's "Stack Overflow").
 *   Target also Owner (whose cheat it is: for a CastBy, the one whose file it is, not the caster), Matched (whom the If
 *   "has" found with AnyEnemy / AnyAlly, else the player who set it off, such as the one with the pips), Unmarked (each
 *   player without the step's or cheat's mark: Mother Sunn's Ra for a wizard who cast no damage) and MostPips (the
 *   player with the most pips).
 *
 * File settings: ShadowPips is how many shadow pips the boss can hold (0 or left out = the [Battle] settings), and
 * ShadowPipChance the percent a round to gain one when its stats give none (25 if left out).
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Combat;

internal static class BossCheats {

    internal sealed class BossCheatFile {
        public ulong TemplateID { get; set; }
        public string Name { get; set; } = "";
        public string Notes { get; set; } = "";
        public int ShadowPips { get; set; }
        public int ShadowPipChance { get; set; } = 25;
        public List<BossCheat> Cheats { get; set; } = [];
    }

    internal sealed class BossCheat {
        public string When { get; set; } = "";
        public int Round { get; set; }
        public int Every { get; set; }
        public int Percent { get; set; }
        public string School { get; set; } = "";
        public string Kind { get; set; } = "";
        public List<string> Spells { get; set; } = [];
        public string Cast { get; set; } = "";
        public string Target { get; set; } = "";
        public string Line { get; set; } = "";
        public bool Once { get; set; }
        public int Chance { get; set; } = 100;
        public string Notes { get; set; } = "";
        public int Health { get; set; }
        public bool NoAllies { get; set; }
        public bool OnBoss { get; set; }
        public bool SingleTarget { get; set; }
        public int Phase { get; set; }
        public int SetPhase { get; set; }
        public int SetHealth { get; set; }
        public bool Defeat { get; set; }
        public uint Spawn { get; set; }
        public bool Silent { get; set; }
        public List<CheatCast> Casts { get; set; } = [];
        public bool Random { get; set; }
        public string Category { get; set; } = "";
        public string CasterSchool { get; set; } = "";
        public int Pips { get; set; }
        public string PipsOf { get; set; } = "";
        public int HealthAbove { get; set; }
        public int HealthUnder { get; set; }
        public int MaxTimes { get; set; }
        public int Cooldown { get; set; }
        public List<CheatAction> Actions { get; set; } = [];
        // Spells is a list of plain names, each standing for every version of that spell (SpellNameFamily).
        public bool AnyVersion { get; set; }
        // The pip cost of the spell that sets it off: at least CostMin, at most CostMax (0 = no top).
        public int CostMin { get; set; }
        public int CostMax { get; set; }
        // Only while this many players are standing (0 = no limit).
        public int PlayersMin { get; set; }
        public int PlayersMax { get; set; }
        // Only from / up to this round of the battle (0 = no limit).
        public int FromRound { get; set; }
        public int ToRound { get; set; }
        // Only while someone else on its side is still standing (the other way round from NoAllies).
        public bool HasAllies { get; set; }
        // For a player-cast When: "" or "Landed" only a spell that lands sets it off, "Fizzled" only one that fizzles,
        // "Any" either (Niduus, 2026-10-22).
        public string Fizzle { get; set; } = "";
        // "If dispelled": with DispelThen "Retry", a Dispel on the boss can stop the cheat's spells, and each one that
        // fizzles is cast again up to DispelRetries more times, or until it gets through with RetryUntilCast
        // (Niduus, 2026-10-22).
        public string DispelThen { get; set; } = "";
        public int DispelRetries { get; set; }
        public bool RetryUntilCast { get; set; }
        // An added cheat turned off in the app (BossOverrides.CheatsOf).
        public bool Off { get; set; }
        // Only if the one the When is about is (or with NotCreature, is not) of this creature template (Niduus, 2026-10-23).
        public uint Creature { get; set; }
        public bool NotCreature { get; set; }
        // EnemyJoins: a summoned minion counts too.
        public bool Minions { get; set; }
        // When "Passive": always true, with no trigger. Passive "Circle": it always takes circle Circle (1-4) of its side
        // when it comes into a battle (Niduus, 2026-10-24: "make Fairy Queens assign for 4th spot a passive block").
        public string Passive { get; set; } = "";
        public int Circle { get; set; }
        // Passive "Minions": it comes into a battle with these creatures (template ids) on its side, and BringsCount of
        // BringsRandom picked at random (Niduus, 2026-10-24: the gauntlets' bosses and their minions).
        public List<string> Brings { get; set; } = [];
        public List<string> BringsRandom { get; set; } = [];
        public int BringsCount { get; set; }
        // "Has": only if HasWho (Caster, Receiver or Self) has a charm, ward, aura or over-time of HasKind (Shield, Trap,
        // Blade, Weakness, Charm, Ward, Aura, DoT, HoT; empty for any), or one from the spell HasSpell (any version); with
        // HasNot, only if it has none.
        public string HasWho { get; set; } = "";
        public string HasKind { get; set; } = "";
        public string HasSpell { get; set; } = "";
        public bool HasNot { get; set; }
        // HasCount: at least this many such effects (0 or 1 = one). HasWho also AnyEnemy (someone on the other side has
        // them; with HasNot, no one there) and AnyAlly (someone on its own side, itself too); the target "Matched" is the
        // one found (Niduus, 2026-10-24: Wizard404's cheats for every creature).
        public int HasCount { get; set; }
        // When "Pips": PipsKind "Shadow" counts only shadow pips (empty: all pips, a power pip as one).
        public string PipsKind { get; set; } = "";
        // For a When about a spell: CreatureWho "Receiver" makes Creature / NotCreature look at whoever the spell was
        // aimed at (the one hit) instead of its caster.
        public string CreatureWho { get; set; } = "";
        // Only while each of AlliesUp (template ids) stands on its side, and none of AlliesDown does.
        public List<string> AlliesUp { get; set; } = [];
        public List<string> AlliesDown { get; set; } = [];
        // Only while this many others stand on its side (AlliesMax 0 = no top; both 0 = no limit, -1 in AlliesMax = none).
        public int AlliesMin { get; set; }
        public int AlliesMax { get; set; }
        // "Marked": only if MarkWho (Caster, Receiver, Self, or AnyEnemy: someone on the other side) has the mark Mark
        // (a Mark step puts it on); with NotMarked, only if not (AnyEnemy: no one on the other side has it).
        public string Mark { get; set; } = "";
        public string MarkWho { get; set; } = "";
        public bool NotMarked { get; set; }
        // Only a spell that hits a whole side (the other way round from SingleTarget).
        public bool MultiTarget { get; set; }
        // Only a spell aimed at someone other than its caster (Kormak Shadowclaw: a charm on another wizard).
        public bool NotOnCaster { get; set; }
        // Only a spell that is none of these kinds ("Blade" or "Charm, Ward"): the Sphinx's wrong answer to a riddle.
        public string NotCategory { get; set; } = "";

        /// <summary>A cheat made or changed in the app (BossOverrides.CheatsOf) or a custom's: its firing is logged.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal bool Custom { get; set; }

        /// <summary>Whether it stops the player's spell, so it goes off before that spell.</summary>
        internal bool Interrupts => (Actions ?? []).Any(a => a is not null && !a.Off && (a.Is("Interrupt") || a.Is("Reflect")));

        /// <summary>
        /// Everything it does, in order: the old fields as actions (a second form, health, phase, line, spells, defeat),
        /// then Actions. Random keeps one of the spells.
        /// </summary>
        internal List<CheatAction> Steps() {
            var steps = new List<CheatAction>();
            if (Spawn > 0) {
                steps.Add(new CheatAction { Do = "Form", Creature = Spawn, ReplaceBoss = Defeat });
            }

            if (SetHealth > 0) {
                steps.Add(new CheatAction { Do = "SetHealth", Amount = SetHealth });
            }

            if (SetPhase > 0) {
                steps.Add(new CheatAction { Do = "SetPhase", Phase = SetPhase });
            }

            if (!string.IsNullOrWhiteSpace(Line)) {
                steps.Add(new CheatAction { Do = "Say", Line = Line });
            }

            steps.AddRange(CastList(all: true).Select(c => new CheatAction {
                Do = "Cast", Spell = c.Spell, SpellID = c.SpellID, Silent = c.Silent, DispelImmune = c.DispelImmune, Target = c.Target ?? "",
            }));
            if (Defeat && Spawn == 0) {
                steps.Add(new CheatAction { Do = "Defeat" });
            }

            steps.AddRange((Actions ?? []).Where(a => a is not null && !a.Off).Select(a => Silent && a.IsCast ? a with { Silent = true } : a));
            var casts = steps.Where(s => s.IsCast).ToList();
            if (Random && casts.Count > 1) {
                var keep = casts[System.Random.Shared.Next(casts.Count)];
                steps.RemoveAll(s => s.IsCast && s != keep);
            }

            return steps;
        }

        internal bool Is(string when) => When.Equals(when, StringComparison.OrdinalIgnoreCase);

        /// <summary>The spells to cast this time: Casts, or Cast as a list of one; the cheat's Silent counts for each.</summary>
        internal List<CheatCast> CastList(bool all = false) {
            List<CheatCast> casts = [.. (Casts ?? []).Where(c => c is not null && (c.SpellID != 0 || !string.IsNullOrWhiteSpace(c.Spell)))
                .Select(c => Silent ? c with { Silent = true } : c)];
            if (casts.Count == 0 && !string.IsNullOrWhiteSpace(Cast)) {
                casts.Add(new CheatCast { Spell = Cast, Silent = Silent });
            }

            return !all && Random && casts.Count > 1 ? [casts[System.Random.Shared.Next(casts.Count)]] : casts;
        }
    }

    /// <summary>One spell of a cheat's Casts.</summary>
    internal sealed record CheatCast {
        public string Spell { get; set; } = "";
        public uint SpellID { get; set; }
        public bool Silent { get; set; }
        public bool DispelImmune { get; set; } = true;
        public string Target { get; set; } = "";
    }

    /// <summary>One thing a cheat does (its Actions); see the top of this file.</summary>
    internal sealed record CheatAction {
        public string Do { get; set; } = "";
        public string Spell { get; set; } = "";
        public uint SpellID { get; set; }
        public string Target { get; set; } = "";
        public bool Silent { get; set; }
        public bool DispelImmune { get; set; } = true;
        public int Amount { get; set; }
        public bool Percent { get; set; }
        public string Kind { get; set; } = "";
        public int Count { get; set; }
        public uint Creature { get; set; }
        public int Custom { get; set; }
        public bool ReplaceBoss { get; set; }
        public int Phase { get; set; }
        public string Line { get; set; } = "";
        public string Text { get; set; } = "";
        public bool Off { get; set; }
        // For Boost, Immune and Stun: how many rounds (0 = the rest of the battle, or 1 for Stun); for Wait, how many to wait.
        public int Rounds { get; set; }
        // For Boost, Immune and AddCharm: the school ("" or All = every school).
        public string School { get; set; } = "";
        // For PickOne: the chance of each of the next actions, such as "2, 1, 1" (1 each if left out).
        public string Weights { get; set; } = "";
        // Player cheats' FreeCast: how many times, when (First, Last, Before, After, Now), and with an enchantment.
        public int Times { get; set; }
        // For IfCleared: once for each effect taken off, not just once.
        public bool Each { get; set; }
        public string Timing { get; set; } = "";
        public bool Enchant { get; set; }
        // For Announce: Box (default), Chat or Both; and who gets it ("" every wizard in the battle, Players).
        public string Style { get; set; } = "";
        public string Who { get; set; } = "";
        // For AddCharm: the cast animation of the spell cast for it ("" normal, Sun, Shadow, Moon, Star, or a school).
        public string Look { get; set; } = "";
        // For FirstTurn: Us (the cheat's own side acts first) or Them (the other side).
        public string Side { get; set; } = "";
        // The enchantments picked for an enchanted FreeCast, put on in this order (none: the best one that fits).
        public List<string> Enchants { get; set; } = [];
        // For Mark and Unmark: the mark's name. For CastBy: who casts (Creature: one of Creature's template on its side;
        // Ally, WeakestAlly, StrongestAlly, Summoner). For PickOne and InTurn: how many steps each choice is (1).
        public string Mark { get; set; } = "";
        public string By { get; set; } = "";
        public int Group { get; set; }

        internal bool Is(string what) => Do.Equals(what, StringComparison.OrdinalIgnoreCase);

        internal bool IsCast => Is("Cast") || Is("Polymorph");
    }

    // [BossCheats] Enabled (Niduus's) and our [Roguelike] BossCheats (the launcher's Settings switch); both on by default.
    internal static readonly bool Enabled = ConfigurationManager.GetValue("BossCheats.Enabled", true) && ConfigurationManager.GetValue("Roguelike.BossCheats", true);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Lazy<Dictionary<ulong, BossCheatFile>> s_bosses = new(Load);

    /// <summary>
    /// The cheats of a boss template, or none. For a custom's key (Customs.cs): its creature's file with the custom's
    /// own changes laid over it.
    /// </summary>
    internal static List<BossCheat> For(ulong templateId)
        => Enabled && s_bosses.Value.TryGetValue(templateId, out var boss) ? [.. boss.Cheats.Where(c => !c.Off)] : s_none;

    /// <summary>Whether a creature template has a cheat file.</summary>
    internal static bool Has(ulong templateId) => Enabled && s_bosses.Value.ContainsKey(templateId);

    /// <summary>
    /// The circle (1-4 of its side) a passive "Circle" cheat says a combatant always takes, or 0. The last one that is
    /// on wins.
    /// </summary>
    internal static int CircleOf(IEnumerable<BossCheat> cheats)
        => cheats?.LastOrDefault(c => c is { Off: false, Circle: >= 1 and <= 4 } && c.Is("Passive")
            && c.Passive.Equals("Circle", StringComparison.OrdinalIgnoreCase))?.Circle ?? 0;

    /// <summary>The template ids of a list of ids written as words ("1668859").</summary>
    internal static List<uint> Ids(IEnumerable<string> words)
        => [.. (words ?? []).Select(w => uint.TryParse((w ?? "").Trim(), out var id) ? id : 0).Where(id => id > 0)];

    /// <summary>A passive "Minions" cheat that is on: the creatures a combatant comes into a battle with, or null.</summary>
    /// <remarks>One with a players count (PlayersMin / PlayersMax) is not this one: see <see cref="MinionsForPlayersOf"/>.</remarks>
    internal static BossCheat MinionsOf(IEnumerable<BossCheat> cheats)
        => cheats?.LastOrDefault(c => IsMinions(c) && c.PlayersMin <= 0 && c.PlayersMax <= 0);

    /// <summary>The passive "Minions" cheats that are on and wait for a number of players (PlayersMin / PlayersMax).</summary>
    internal static List<BossCheat> MinionsForPlayersOf(IEnumerable<BossCheat> cheats)
        => [.. (cheats ?? []).Where(c => IsMinions(c) && (c.PlayersMin > 0 || c.PlayersMax > 0))];

    /// <summary>Whether this many players fit a cheat's PlayersMin / PlayersMax (0 is no limit).</summary>
    internal static bool PlayersFit(BossCheat cheat, int players)
        => players >= cheat.PlayersMin && (cheat.PlayersMax <= 0 || players <= cheat.PlayersMax);

    private static bool IsMinions(BossCheat c)
        => c is { Off: false } && c.Is("Passive") && c.Passive.Equals("Minions", StringComparison.OrdinalIgnoreCase)
           && (Ids(c.Brings).Count > 0 || Ids(c.BringsRandom).Count > 0);

    /// <summary>The creatures a passive "Minions" cheat brings this time: all of Brings, then BringsCount of BringsRandom.</summary>
    internal static List<uint> BroughtBy(BossCheat cheat) {
        if (cheat is null) {
            return [];
        }

        var random = Ids(cheat.BringsRandom).OrderBy(_ => System.Random.Shared.Next()).Take(Math.Max(0, cheat.BringsCount));

        return [.. Ids(cheat.Brings).Concat(random)];
    }

    // The cheats of a boss with no file, the same list each time so BossOverrides.CheatsOf keeps its own laid over it.
    private static readonly List<BossCheat> s_none = [];

    /// <summary>How many shadow pips a boss template can hold and its chance a round to gain one, or (0, 0).</summary>
    internal static (int Most, int Chance) ShadowPipsOf(ulong templateId)
        => s_bosses.Value.TryGetValue(templateId, out var boss) && boss.ShadowPips > 0
            ? (boss.ShadowPips, Math.Clamp(boss.ShadowPipChance, 0, 100))
            : (0, 0);

    internal static string NameOf(ulong templateId) {
        return s_bosses.Value.TryGetValue(templateId, out var boss) && boss.Name.Length > 0 ? boss.Name : templateId.ToString();
    }

    private static Dictionary<ulong, BossCheatFile> Load() {
        var bosses = new Dictionary<ulong, BossCheatFile>();
        var dir = Path.Combine(SpiralDB.ExtraPath, "BossCheats");
        Logger.Information("Reading boss cheats from {0}.", Logger.Args(Path.GetFullPath(dir)));
        if (!Directory.Exists(dir)) {
            return bosses;
        }

        foreach (var file in Directory.GetFiles(dir, "*.json")) {
            try {
                var boss = JsonSerializer.Deserialize<BossCheatFile>(File.ReadAllText(file), s_options);
                if (boss is null || boss.TemplateID == 0) {
                    continue;
                }

                boss.Cheats = [.. (boss.Cheats ?? []).Where(c => c is not null)];
                bosses[boss.TemplateID] = boss;
            }
            catch (Exception e) {
                Logger.Warning("Boss cheats {0} could not be read: {1}", Logger.Args(Path.GetFileName(file), e.Message));
            }
        }

        Logger.Information("Boss cheats loaded for {0} bosses.", Logger.Args(bosses.Count));

        return bosses;
    }

}
