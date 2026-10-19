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
 * RANDOMIZER MODE
 * ========================================================================
 *
 * PURPOSE:
 * An optional server mode, configured in the [Randomizer] section of
 * Imlight.ini, that shuffles a character's school, the spells they learn,
 * the loot they get and the creatures in the world. It can also roll
 * creature decks, gear item cards and pip gain fresh every battle, and
 * teach a random spell (any combat spell in the game) after a win.
 *
 * USAGE EXAMPLE:
 * var spellId = Randomizer.MapSpell(wizard, trainerSpellId);
 * var itemId = Randomizer.MapDrop(wizard, droppedItemId);
 *
 * NOTE:
 * - Swaps stay balanced by only trading within a bucket: spells keep their
 *   type and pip cost, gear keeps its slot, school and level range, treasure
 *   cards keep their type and pip cost, creatures keep their level range.
 * - Spell and loot swaps are fixed per character (the same spell always
 *   becomes the same spell), and never map two IDs to one, so prerequisite
 *   chains at trainers still line up.
 * - The pools are built in the background on boot; a swap asked for before
 *   then waits for them, so no character ever gets an unswapped spell.
 * - The per-battle rolls (decks, item cards, pips) are truly random and
 *   differ every time, but still trade within a bucket: a spell becomes
 *   another spell of the same type and pip cost.
 *
 * TODO:
 *
 * Version: KALI 1.0
 * Last Updated: 10/18/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Randomization;

/// <summary>
/// The randomizer server mode.
/// </summary>
public static class Randomizer {

    private static readonly MagicSchool[] s_primarySchools = [
        MagicSchool.Fire, MagicSchool.Ice, MagicSchool.Storm, MagicSchool.Myth,
        MagicSchool.Life, MagicSchool.Death, MagicSchool.Balance,
    ];

    // Gear that is safe to swap. Quest items, keys and housing are left alone so quests still work.
    private static readonly HashSet<ObjectType> s_gearTypes = [
        ObjectType.OT_EQUIP_HEAD, ObjectType.OT_EQUIP_CHEST, ObjectType.OT_EQUIP_LEGS,
        ObjectType.OT_EQUIP_HANDS, ObjectType.OT_EQUIP_FINGER, ObjectType.OT_EQUIP_FEET,
        ObjectType.OT_EQUIP_EAR, ObjectType.OT_PET, ObjectType.OT_HORSE,
    ];

    internal static readonly bool Enabled
        = ConfigurationManager.GetValue("Randomizer.Enabled", false);
    // Tame mode keeps everything that battles and quests depend on: creatures, their decks and pips stay as they
    // are, so every "defeat" quest can be done and fights are as hard as normal. Schools, spells and drops still shuffle.
    internal static readonly bool Tame
        = Enabled && ConfigurationManager.GetValue("Randomizer.Tame", false);
    internal static readonly bool RandomizeSchool
        = Enabled && ConfigurationManager.GetValue("Randomizer.RandomizeSchool", true);
    internal static readonly bool RandomizeSpells
        = Enabled && ConfigurationManager.GetValue("Randomizer.RandomizeSpells", true);
    internal static readonly bool RandomizeDrops
        = Enabled && ConfigurationManager.GetValue("Randomizer.RandomizeDrops", true);
    internal static readonly bool RandomizeCreatures
        = Enabled && !Tame && ConfigurationManager.GetValue("Randomizer.RandomizeCreatures", false);
    private static readonly bool s_creaturesEverySpawn
        = ConfigurationManager.GetValue("Randomizer.CreaturesChangeEverySpawn", true);
    internal static readonly bool RandomizeCreatureDecks
        = Enabled && !Tame && ConfigurationManager.GetValue("Randomizer.RandomizeCreatureDecks", true);
    internal static readonly bool RandomizeItemCards
        = Enabled && ConfigurationManager.GetValue("Randomizer.RandomizeItemCards", true);
    internal static readonly bool RandomizePips
        = Enabled && !Tame && ConfigurationManager.GetValue("Randomizer.RandomizePips", true);
    // The [Trainers] switches work with or without randomizer mode.
    internal static readonly bool RandomizeSpellRequirements
        = ConfigurationManager.GetValue("Trainers.RandomizeSpellRequirements", false);
    internal static readonly bool NoSpellRequirements
        = ConfigurationManager.GetValue("Trainers.NoSpellRequirements", false);
    internal static readonly bool NoLevelRequirements
        = ConfigurationManager.GetValue("Trainers.NoLevelRequirements", false);
    internal static readonly bool FreeTraining
        = ConfigurationManager.GetValue("Trainers.FreeTraining", false);
    private static readonly int s_startingSpells
        = Enabled ? Math.Clamp(ConfigurationManager.GetValue("Randomizer.StartingSpells", 6), 0, 50) : 0;
    private const int STARTER_MAX_PIPS = 3;
    private static readonly int s_spellAfterBattleChance
        = Enabled ? Math.Clamp(ConfigurationManager.GetValue("Randomizer.SpellAfterBattleChance", 100), 0, 100) : 0;

    // Schools whose spells can be cast in battle. Gardening, fishing, cantrips and the like are left out.
    private static readonly HashSet<string> s_combatSchools = new(StringComparer.OrdinalIgnoreCase) {
        "Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Star", "Sun", "Moon", "Shadow",
    };

    private static readonly bool s_sameForEveryone
        = ConfigurationManager.GetValue("Randomizer.SameForEveryone", false);
    private static readonly ulong s_seed
        = SeededShuffle.Fnv1a(ConfigurationManager.GetValue("Randomizer.Seed", ""));
    private static readonly int s_levelRange
        = Math.Max(1, ConfigurationManager.GetValue("Randomizer.LevelRange", 10));

    private static readonly Lazy<SeededShuffle> s_spells = new(BuildSpellPool, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<SeededShuffle> s_drops = new(BuildDropPool, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<SeededShuffle> s_creatures = new(BuildCreaturePool, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<SeededShuffle> s_combatSpells = new(BuildCombatSpellPool, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<uint[]> s_starterSpells = new(BuildStarterSpellPool, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Builds the swap pools in the background so the first swap rarely has to wait for them.
    /// </summary>
    public static void StartBackgroundBuild() {
        if (!Enabled) {
            return;
        }

        Logger.Information("Randomizer mode is on{Tame}. School: {School}, spells: {Spells}, drops: {Drops}, creatures: {Creatures}.",
            Logger.Args(Tame ? " (tame)" : "", RandomizeSchool, RandomizeSpells, RandomizeDrops, RandomizeCreatures));
        Logger.Information("Randomizer battles. Creature decks: {Decks}, item cards: {ItemCards}, pips: {Pips}, spell after a win: {Chance}%.",
            Logger.Args(RandomizeCreatureDecks, RandomizeItemCards, RandomizePips, s_spellAfterBattleChance));

        _ = Task.Run(() => {
            try {
                if (RandomizeSpells) {
                    Logger.Information("Randomizer can swap {Count} spells.", Logger.Args(s_spells.Value.Count));
                }
                if (RandomizeDrops) {
                    Logger.Information("Randomizer can swap {Count} drops.", Logger.Args(s_drops.Value.Count));
                }
                if (RandomizeCreatures) {
                    Logger.Information("Randomizer can swap {Count} creatures.", Logger.Args(s_creatures.Value.Count));
                }
                if (RandomizeCreatureDecks || RandomizeItemCards || s_spellAfterBattleChance > 0) {
                    Logger.Information("Randomizer can use {Count} battle spells.", Logger.Args(s_combatSpells.Value.Count));
                }
            }
            catch (Exception ex) {
                Logger.Error("Randomizer could not build its pools; nothing will be swapped. {Exception}", Logger.Args(ex));
            }
        });
    }

    /// <summary>
    /// Picks the school for a new character. Returns <paramref name="chosen"/> when school randomizing is off.
    /// </summary>
    internal static MagicSchool PickSchool(MagicSchool chosen)
        => RandomizeSchool
            ? s_primarySchools[Random.Shared.Next(s_primarySchools.Length)]
            : chosen;

    /// <summary>
    /// Returns the spell a wizard really learns in place of <paramref name="spellTemplateId"/>.
    /// </summary>
    internal static uint MapSpell(Wizard wizard, uint spellTemplateId)
        => RandomizeSpells && wizard is not null
            ? (uint) s_spells.Value.Map(spellTemplateId, SeedFor(wizard, "spells"))
            : spellTemplateId;

    /// <summary>
    /// Returns the item or treasure card a wizard really gets in place of a dropped <paramref name="templateId"/>.
    /// </summary>
    internal static ulong MapDrop(Wizard wizard, ulong templateId)
        => RandomizeDrops && wizard is not null
            ? s_drops.Value.Map(templateId, SeedFor(wizard, "drops"))
            : templateId;

    /// <summary>
    /// Returns the creature that spawns in place of <paramref name="templateId"/>: a fresh pick of a similar
    /// level every spawn, or (with CreaturesChangeEverySpawn off) the same swap every time, for every player.
    /// </summary>
    internal static ulong MapCreature(ulong templateId) {
        if (!RandomizeCreatures) {
            return templateId;
        }

        return s_creaturesEverySpawn
            ? s_creatures.Value.Pick(templateId, Random.Shared)
            : s_creatures.Value.Map(templateId, SeededShuffle.Mix(s_seed ^ SeededShuffle.Fnv1a("creatures")));
    }

    /// <summary>
    /// The creature that stands in for <paramref name="templateId"/> under <paramref name="seed"/>: one of a similar
    /// level (bosses only with bosses), whether or not randomizer mode is on. Used by roguelike runs.
    /// </summary>
    internal static ulong MapCreatureSeeded(ulong templateId, ulong seed)
        => s_creatures.Value.Map(templateId, seed);

    // The creature each swapped spawn replaced, by the spawn's global id, so quests still count it as that creature.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, ulong> s_replacedCreatures = new();
    private const int MAX_REMEMBERED_CREATURES = 50_000;

    /// <summary>
    /// Remembers which creature a swapped spawn stands in for.
    /// </summary>
    /// <param name="spawnGlobalId">The global id of the spawned creature.</param>
    /// <param name="originalTemplateId">The template the spawn point asked for.</param>
    internal static void RememberReplacedCreature(ulong spawnGlobalId, ulong originalTemplateId) {
        // Spawns that never end in a won battle are never forgotten, so the list is emptied now and then.
        if (s_replacedCreatures.Count >= MAX_REMEMBERED_CREATURES) {
            s_replacedCreatures.Clear();
        }

        s_replacedCreatures[spawnGlobalId] = originalTemplateId;
    }

    /// <summary>
    /// Finds the creature a swapped spawn stands in for, and forgets it.
    /// </summary>
    /// <param name="spawnGlobalId">The global id of the defeated creature.</param>
    /// <param name="originalTemplateId">The template the spawn point asked for.</param>
    /// <returns>False if the creature was not swapped.</returns>
    internal static bool TryTakeReplacedCreature(ulong spawnGlobalId, out ulong originalTemplateId)
        => s_replacedCreatures.TryRemove(spawnGlobalId, out originalTemplateId);

    /// <summary>
    /// Returns a random spell of the same type and pip cost for a creature's deck. Different every spawn.
    /// </summary>
    internal static uint RollCreatureSpell(uint spellTemplateId)
        => RandomizeCreatureDecks ? RollSpell(spellTemplateId) : spellTemplateId;

    /// <summary>
    /// Returns a random spell of the same type and pip cost for a card a piece of gear gives. Different every battle.
    /// </summary>
    internal static uint RollItemCard(uint spellTemplateId)
        => RandomizeItemCards ? RollSpell(spellTemplateId) : spellTemplateId;

    /// <summary>
    /// How many pips a participant gains this round: usually one, sometimes none, two or three.
    /// </summary>
    internal static int RollPipGain() {
        var roll = Random.Shared.Next(100);

        return roll switch {
            < 15 => 0,
            < 65 => 1,
            < 93 => 2,
            _ => 3,
        };
    }

    /// <summary>
    /// After a win, maybe picks a random battle spell the wizard does not know yet. Any combat spell in the game
    /// can come up, including ones players normally never get. Returns null when nothing is taught this time.
    /// </summary>
    internal static (uint TemplateId, string Name)? PickSpellAfterBattle(Wizard wizard) {
        if (s_spellAfterBattleChance == 0 || wizard is null || Random.Shared.Next(100) >= s_spellAfterBattleChance) {
            return null;
        }

        var pool = s_combatSpells.Value;
        if (pool.Count == 0) {
            return null;
        }

        for (var attempt = 0; attempt < 25; attempt++) {
            var id = (uint) pool.PickAny(Random.Shared);
            if (id == 0 || wizard.SpellbookBehavior.HasSpell(id)) {
                continue;
            }

            if (CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate spell) {
                string displayKey = spell.m_displayName;
                string name = spell.m_name;
                var english = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);

                return (id, string.IsNullOrEmpty(english) ? name : english);
            }
        }

        return null;
    }

    /// <summary>
    /// Teaches a new character random battle spells (StartingSpells of them, 6 by default) that cost
    /// at most three pips, so every one can be cast early on. Called before the character is first saved.
    /// </summary>
    internal static void GiveStartingSpells(Wizard wizard) {
        if (s_startingSpells == 0 || wizard is null) {
            return;
        }

        try {
            var pool = s_starterSpells.Value;
            var picked = new HashSet<uint>();
            for (var attempt = 0; picked.Count < Math.Min(s_startingSpells, pool.Length) && attempt < s_startingSpells * 20; attempt++) {
                var id = pool[Random.Shared.Next(pool.Length)];
                if (!picked.Add(id)) {
                    continue;
                }

                if (Imlight.CoreLib.Game.Spells.SpellFactory.GetSpell(id) is { } spell) {
                    wizard.SpellbookBehavior.AddSpellToBook(spell);
                }
                else {
                    picked.Remove(id);
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Randomizer could not pick starting spells. {Exception}", Logger.Args(ex));
        }
    }

    /// <summary>
    /// When the starter kit is handed out, puts the starting spells into its deck so they can be used in the first
    /// battle, and makes sure the deck's own spells are learned (otherwise battles treat them as treasure cards).
    /// </summary>
    internal static void PutStartingSpellsInDeck(Wizard wizard, IEnumerable<WizClientObjectItem> items) {
        if (s_startingSpells == 0 || wizard is null) {
            return;
        }

        try {
            foreach (var item in items) {
                if (!CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(item, out var deck)) {
                    continue;
                }

                var inDeck = new HashSet<uint>();
                foreach (var entry in deck.m_spellList ?? []) {
                    inDeck.Add(entry.m_templateID);
                    if (CoreObjectFactory.GetCoreTemplate(entry.m_templateID) is SpellTemplate { m_Treasure: false }
                            && !wizard.SpellbookBehavior.LearnedSpellTemplateIds.Contains(entry.m_templateID)
                            && Imlight.CoreLib.Game.Spells.SpellFactory.GetSpell(entry.m_templateID) is { } deckSpell) {
                        wizard.LearnSpell(deckSpell);
                    }
                }

                foreach (var id in wizard.SpellbookBehavior.LearnedSpellTemplateIds.ToArray()) {
                    if (!inDeck.Contains(id)) {
                        wizard.AddSpellToDeck(id, item.m_globalID);
                    }
                }

                return;
            }
        }
        catch (Exception ex) {
            Logger.Error("Randomizer could not put the starting spells in the deck. {Exception}", Logger.Args(ex));
        }
    }

    private static uint RollSpell(uint spellTemplateId) {
        try {
            return (uint) s_combatSpells.Value.Pick(spellTemplateId, Random.Shared);
        }
        catch (Exception ex) {
            Logger.Warning("Randomizer could not roll a spell for {Id}. {Message}", Logger.Args(spellTemplateId, ex.Message));

            return spellTemplateId;
        }
    }

    /// <summary>
    /// A random index below <paramref name="count"/> that stays the same for this wizard and <paramref name="key"/>.
    /// </summary>
    internal static int PickIndex(Wizard wizard, string what, ulong key, int count)
        => (int) (SeededShuffle.Mix(SeedFor(wizard, what) ^ key) % (ulong) count);

    private static ulong SeedFor(Wizard wizard, string what) {
        var seed = s_seed ^ SeededShuffle.Fnv1a(what);
        if (!s_sameForEveryone) {
            seed ^= SeededShuffle.Mix(wizard.CharId);
        }

        return SeededShuffle.Mix(seed);
    }

    // ---------------------------------------------------------------------
    // Pools
    // ---------------------------------------------------------------------

    private static SeededShuffle BuildSpellPool() {
        // Only spells some trainer teaches: they are known to be learnable by players.
        var entries = new List<(ulong, string)>();
        var trainerSpellIds = SpiralDB.NpcSpellInventories.Values
            .Where(inventory => inventory?.Spells is not null)
            .SelectMany(inventory => inventory.Spells)
            .Select(entry => entry.TemplateID)
            .Distinct();

        foreach (var id in trainerSpellIds) {
            if (CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate spell && !spell.m_Treasure) {
                entries.Add((id, SpellBucket(spell)));
            }
        }

        return new SeededShuffle(entries);
    }

    private static SeededShuffle BuildDropPool() {
        // Everything any drop table can give, limited to gear and treasure cards.
        var entries = new List<(ulong, string)>();
        var dropIds = SpiralDB.DropTables.Values
            .Where(table => table?.Items is not null)
            .SelectMany(table => table.Items)
            .Select(item => ulong.TryParse(item.ItemId, out var id) ? id : 0)
            .Where(id => id != 0)
            .Distinct();

        foreach (var id in dropIds) {
            switch (CoreObjectFactory.GetCoreTemplate(id)) {
                case WizItemTemplate item when s_gearTypes.Contains(item.m_nObjectType): {
                    string school = item.m_school;
                    entries.Add((id, $"gear:{item.m_nObjectType}:{school}:{LevelBand(EquipLevel(item))}"));

                    break;
                }
                case SpellTemplate spell when spell.m_Treasure:
                    entries.Add((id, "tc:" + SpellBucket(spell)));

                    break;
            }
        }

        return new SeededShuffle(entries);
    }

    private static SeededShuffle BuildCreaturePool() {
        var entries = new List<(ulong, string)>();
        foreach (var id in Imlight.CoreLib.Game.Zone.AprilFools.CreatureList.TemplateIDs) {
            if (CoreObjectFactory.GetCoreTemplate(id) is not GameObjectTemplate template) {
                continue;
            }

            var npc = template.m_behaviors.OfType<NPCBehaviorTemplate>().FirstOrDefault();
            if (npc is null || !template.m_behaviors.OfType<DuelistBehaviorTemplate>().Any()) {
                continue;
            }

            // Bosses only swap with bosses.
            var rank = npc.m_bossMob ? "boss" : "mob";
            entries.Add((id, $"{rank}:{LevelBand(npc.m_nLevel)}"));
        }

        return new SeededShuffle(entries);
    }

    private static SeededShuffle BuildCombatSpellPool()
        => new(CombatSpells().Select(s => (s.Id, SpellBucket(s.Spell))));

    private static uint[] BuildStarterSpellPool()
        => [.. CombatSpells()
            .Where(s => s.Spell.m_spellRank is { m_xPipSpell: false } rank && rank.m_spellRank <= STARTER_MAX_PIPS)
            .Select(s => (uint) s.Id)];

    /// <summary>
    /// Every combat spell in the game files, trainer spells or not. Treasure cards are left out.
    /// </summary>
    private static List<(ulong Id, SpellTemplate Spell)> CombatSpells() {
        var idsByPath = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in CoreObjectFactory.TemplateManifest.m_serializedTemplates) {
            if (location is null) {
                continue;
            }

            string path = location.m_filename;
            if (!string.IsNullOrEmpty(path)) {
                idsByPath.TryAdd(path, location.m_id);
            }
        }

        var spells = new List<(ulong, SpellTemplate)>();
        foreach (var (path, spell) in Imlight.CoreLib.Game.Spells.SpellFactory.AllSpellTemplates()) {
            string name = spell.m_name;
            string school = spell.m_sMagicSchoolName;
            string type = spell.m_sTypeName;
            if (spell.m_Treasure || name.EndsWith(" TC") || spell.m_spellRank is null
                || string.IsNullOrEmpty(type) || string.IsNullOrEmpty(school) || !s_combatSchools.Contains(school)) {
                continue;
            }

            if (idsByPath.TryGetValue(path, out var id) && id <= uint.MaxValue) {
                spells.Add((id, spell));
            }
        }

        return spells;
    }

    private static string SpellBucket(SpellTemplate spell) {
        string type = spell.m_sTypeName;
        var rank = spell.m_spellRank;
        var pips = rank is null ? "?" : rank.m_xPipSpell ? "X" : rank.m_spellRank.ToString();

        return $"{type}:{pips}";
    }

    private static int EquipLevel(ItemTemplate item)
        => (int) (item.m_equipRequirements?.m_requirements?
            .OfType<ReqMagicLevel>()
            .Select(req => req.m_numericValue)
            .DefaultIfEmpty(0)
            .Max() ?? 0);

    private static int LevelBand(int level)
        => Math.Max(0, level) / s_levelRange;

}
