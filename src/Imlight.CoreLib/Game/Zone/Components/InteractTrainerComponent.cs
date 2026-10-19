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
 * INTERACT TRAINER
 * ========================================================================
 * 
 * PURPOSE:
 * Manages spell training interactions for NPCs, dynamically generating 
 * spell training options based on player characteristics and spell requirements.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * Dynamically resolves spell training options from NPC spell inventory.
 * Considers player's magic school, level, and existing spellbook.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Randomization;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractTrainerComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "WizTrainingService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => "GUI_NPCInteractText";
    public WizBangs WizBang       => WizBangs.Training;
    public string StateName       => "Shop";
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_ShopOptionEquipment";

    public List<NPCSpellEntry> SpellInventory { get; private set; } = [];
    private List<ServiceOptionBase> _serviceOptionBases = [];

    public static bool ShouldAttachToEntity(CoreTemplate template) 
        // Attach if the template is an NPC and has a spell inventory in Dragon database.
        => template is GameObjectTemplate goTemplate 
        && goTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate) 
        && (NpcSpellInventoryCollection.TryGetNpcInventory(goTemplate.m_templateID, out _)
            || TestTrainerKindOf(goTemplate) is not null);

    /// <summary>
    /// A test trainer teaches a whole kind of spell for free, at any level. In Ravenwood: Mr. Lincoln every
    /// enchantment, Monstrologist Burke every Extract spell, Mellori the Shadow spells and Merle Ambrose the spells only
    /// monsters and bosses cast.
    /// </summary>
    public bool IsTestTrainer { get; private set; }

    internal enum TestTrainerKind { Enchantments, Extracts, Shadow, Boss }

    /// <summary>
    /// Finds which test trainer an NPC is: Mr. Lincoln, or in Ravenwood Monstrologist Burke (WC-RAV-NPC13), Mellori
    /// (WC-RAV-NPC12) or Merle Ambrose (WC-RAV-NPC14). (Abner in the Commons only shows one kind of service, so he
    /// keeps the treasure card shop.)
    /// </summary>
    /// <returns>The kind of spells the NPC teaches, or null for any other NPC.</returns>
    internal static TestTrainerKind? TestTrainerKindOf(GameObjectTemplate template) {
        var objectName = template.m_objectName.ToString();
        switch (objectName) {
            case "WC-RAV-NPC13":
                return TestTrainerKind.Extracts;
            case "WC-RAV-NPC12":
                return TestTrainerKind.Shadow;
            case "WC-RAV-NPC14":
                return TestTrainerKind.Boss;
        }

        var names = $"{objectName} {Imlight.CoreLib.Shared.Resources.Locale.GetEnglishName(template.m_displayName.ToString())}";

        return names.Contains("Lincoln", System.StringComparison.OrdinalIgnoreCase) ? TestTrainerKind.Enchantments : null;
    }

    // The Shadow spells players learn (template names): Shadow Shrike, Sentinel, Seraph and Trickster, the Dark Fiend,
    // Dark Nova and Dark Shepherd creatures, the Shadow Shield, Shadow Trap, Shadowblade and Shadowspear cards, and
    // Justiceblade. They head Mellori's list.
    private static readonly HashSet<string> s_vanillaShadowSpells = [
        "Shadow Self DPS", "Shadow Self Tank", "Shadow Self Heal", "Shadow Self Crit",
        "Shadow Creature Fiend", "Shadow Creature Big Bomb", "Shadow Creature Healer",
        "Shadow Shield", "Shadow Trap", "Shadowblade", "Shadowspear", "Balanceblade Shadowblade",
    ];

    // Spell folders that hold monster and boss fight spells.
    private static readonly string[] s_bossFolders = [
        "Fight Specific/", "Mobs/", "CombatTrigger", "Gauntlet", "DisposableDungeon",
    ];

    // Folders of spells that are not for battle or that only work in one place.
    private static readonly string[] s_skippedFolders = [
        "Test/", "Gardening", "Fishing", "Cantrip", "Castle", "WhirlyBurly", "Battleground", "Sherlock", "Rhoshambo",
        "Sigil", "PetPowers", "Tournament", "MonsterMagic", "Minion Specific",
    ];

    private static readonly HashSet<string> s_combatSchools = new(System.StringComparer.OrdinalIgnoreCase) {
        "Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Star", "Sun", "Moon", "Shadow",
    };

    private static readonly System.Lazy<Dictionary<TestTrainerKind, List<NPCSpellEntry>>> s_testSpells
        = new(BuildTestSpells, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    // The Extract spells (Extract Undead and the rest) are templates named CollectEssence<Creature>; the ones with a
    // number after the name are what an enchanted card turns into.
    private static bool IsExtractSpell(string name)
        => name.StartsWith("CollectEssence", System.StringComparison.Ordinal) && !name.Contains(' ');

    private static bool IsBossSpell(string path, string name)
        => s_bossFolders.Any(folder => path.Contains(folder, System.StringComparison.OrdinalIgnoreCase))
            || System.Text.RegularExpressions.Regex.IsMatch(name, @"\bMOB\b|- Mob\b|\bBoss\b|RAID");

    private static bool IsShadowSpell(SpellTemplate spell, string name)
        => (string) spell.m_sMagicSchoolName == "Shadow" || name.Contains("Shadow", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds every test trainer's list at once, so a spell is taught by one trainer only and each card name shows
    /// once (the many copies the game has of a spell share its name). Lists are sorted by name.
    /// </summary>
    private static Dictionary<TestTrainerKind, List<NPCSpellEntry>> BuildTestSpells() {
        var templateIds = new Dictionary<string, uint>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            templateIds.TryAdd(location.m_filename, location.m_id);
        }

        // One candidate per spell template that can be learned and cast in battle.
        var candidates = new List<(TestTrainerKind Kind, string Shown, int Rank, uint TemplateId, string Name)>();
        var normalNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var normalOtherSchoolNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (path, spell) in Imlight.CoreLib.Game.Spells.SpellFactory.AllSpellTemplates()) {
            string name = spell.m_name;
            string displayKey = spell.m_displayName;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(displayKey) || name.EndsWith(" TC")
                || !s_combatSchools.Contains((string) spell.m_sMagicSchoolName ?? "")
                || s_skippedFolders.Any(folder => path.Contains(folder, System.StringComparison.OrdinalIgnoreCase))
                || !templateIds.TryGetValue(path, out var templateId)) {
                continue;
            }

            var shown = Imlight.CoreLib.Shared.Resources.Locale.GetEnglishName(displayKey);
            if (string.IsNullOrWhiteSpace(shown)) {
                shown = displayKey;
            }

            // Internal helper spells: no type, placeholder names, tests and scripted defeats.
            string typeName = spell.m_sTypeName;
            if (string.IsNullOrEmpty(typeName) || typeName == "None" || !shown.Any(char.IsUpper)
                || shown.Contains("Test", System.StringComparison.OrdinalIgnoreCase) || shown.Contains("zappy")
                || name.Contains("Defeat", System.StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var isBoss = IsBossSpell(path, name);
            var isEnchantment = Imlight.CoreLib.Game.Combat.CombatEnchantments.IsEnchantment(spell) || (string) spell.m_sTypeName == "Enchantment";
            if (!isBoss && !spell.m_Treasure && !name.Contains(" - ")) {
                normalNames.Add(shown);
                if (!IsShadowSpell(spell, name)) {
                    normalOtherSchoolNames.Add(shown);
                }
            }

            // Every copy of an Extract card belongs to Burke; the enchanted copies ("CollectEssenceGobbler 100")
            // count as enchantments, so they are left out instead of landing on Mr. Lincoln.
            var isExtractCopy = name.StartsWith("CollectEssence", System.StringComparison.OrdinalIgnoreCase)
                || shown.StartsWith("Extract ", System.StringComparison.OrdinalIgnoreCase);
            if (isExtractCopy && !IsExtractSpell(name)) {
                continue;
            }

            TestTrainerKind? kind = isExtractCopy ? TestTrainerKind.Extracts
                : isEnchantment ? TestTrainerKind.Enchantments
                : s_vanillaShadowSpells.Contains(name) || (!isBoss && !spell.m_Treasure && IsShadowSpell(spell, name)) ? TestTrainerKind.Shadow
                : isBoss && !spell.m_Treasure ? TestTrainerKind.Boss
                : null;
            if (kind is null) {
                continue;
            }

            // The plainest copy of a spell wins: a listed Shadow spell, then the one that is not a treasure card,
            // has no " - " variant suffix and has the shortest name.
            var rank = (s_vanillaShadowSpells.Contains(name) ? 0 : 1000) + (spell.m_Treasure ? 500 : 0)
                + (name.Contains(" - ") ? 200 : 0) + name.Length;
            candidates.Add((kind.Value, shown, rank, templateId, name));
        }

        var result = new Dictionary<TestTrainerKind, List<NPCSpellEntry>>();
        var taken = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var kind in new[] { TestTrainerKind.Extracts, TestTrainerKind.Enchantments, TestTrainerKind.Shadow, TestTrainerKind.Boss }) {
            var picks = candidates
                .Where(c => c.Kind == kind)
                // Merle only teaches what players can't learn anywhere else.
                .Where(c => kind != TestTrainerKind.Boss || !normalNames.Contains(c.Shown))
                // Shadow copies of other schools' spells (a Shadow Blizzard) would look like the normal card.
                .Where(c => kind != TestTrainerKind.Shadow || s_vanillaShadowSpells.Contains(c.Name) || !normalOtherSchoolNames.Contains(c.Shown))
                .GroupBy(c => c.Shown, System.StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(c => c.Rank).First())
                .Where(c => taken.Add(c.Shown))
                .OrderBy(c => kind == TestTrainerKind.Shadow && s_vanillaShadowSpells.Contains(c.Name) ? 0 : 1)
                .ThenBy(c => c.Shown, System.StringComparer.OrdinalIgnoreCase);

            result[kind] = [.. picks.Select(c => new NPCSpellEntry { TemplateID = c.TemplateId, RequiredSpellID = 0, Level = 1 })];
            Logger.Information("Test trainer teaches {0} {1} spells.", Logger.Args(result[kind].Count, kind));
        }

        return result;
    }

    public override void OnStart() {
        if (Entity.Template is GameObjectTemplate testTrainer && TestTrainerKindOf(testTrainer) is { } kind) {
            IsTestTrainer = true;
            SpellInventory = s_testSpells.Value[kind];
            InitializeServiceBases();

            return;
        }

        // Search Dragon database for our spell inventory.
        if (    Entity.Template is not GameObjectTemplate goTemplate
            || !NpcSpellInventoryCollection.TryGetNpcInventory(goTemplate.m_templateID, out var spellInventory)) {
            Logger.Error("{0} on entity {1} has no spell inventory.", 
                Logger.Args(ServiceName, Entity.ActiveGameObject.m_debugName));

            return;
        }

        SpellInventory = spellInventory.Spells;
        InitializeServiceBases();
    }

    // About six pages of the training window; the game disconnects when an NPC offers hundreds.
    private const int TEST_TRAINER_MAX_OPTIONS = 50;

    // Where each wizard's current section starts in the list of spells they don't know yet, by character id.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, int> _sectionStart = new();

    /// <summary>
    /// The spells this trainer offers a wizard, as indexes into <see cref="SpellInventory"/>. The game disconnects
    /// when an NPC offers hundreds of options at once, so a test trainer offers one section of up to 50 spells the
    /// wizard doesn't know yet; walking away and back shows the next section.
    /// </summary>
    private IEnumerable<int> OfferedIndexes(Wizard wizard) {
        if (!IsTestTrainer && Imlight.CoreLib.Game.Runs.RoguelikeStock.TrainersActiveFor(wizard)) {
            // A roguelike run offers a short list of random spells.
            return Enumerable.Range(0, System.Math.Min(_serviceOptionBases.Count, Imlight.CoreLib.Game.Runs.RoguelikeStock.TrainerSpellCount));
        }

        if (!IsTestTrainer) {
            return Enumerable.Range(0, _serviceOptionBases.Count);
        }

        var unknown = UnknownIndexes(wizard);
        var start = _sectionStart.GetValueOrDefault(wizard.CharId);
        if (start >= unknown.Count) {
            start = 0;
        }

        return unknown.Skip(start).Take(TEST_TRAINER_MAX_OPTIONS);
    }

    private List<int> UnknownIndexes(Wizard wizard)
        => [.. Enumerable.Range(0, _serviceOptionBases.Count)
            .Where(i => !wizard.SpellbookBehavior.HasSpell((uint) SpellInventory[i].TemplateID))];

    /// <summary>
    /// Moves a wizard on to the next section of a test trainer's list, back to the first after the last. The first
    /// visit shows the first section.
    /// </summary>
    internal void NextSection(Wizard wizard) {
        if (!IsTestTrainer || wizard is null) {
            return;
        }

        var unknownCount = UnknownIndexes(wizard).Count;
        _sectionStart.AddOrUpdate(wizard.CharId, 0, (_, start) => {
            var next = start + TEST_TRAINER_MAX_OPTIONS;

            return next >= unknownCount ? 0 : next;
        });
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard wizard) {
        if (wizard is null) {
            return IsTestTrainer ? _serviceOptionBases.Take(TEST_TRAINER_MAX_OPTIONS) : _serviceOptionBases;
        }

        // We've initialized a list of service option bases in InitializeServiceBases.
        // However, these options can change depending on how far the player has progressed.
        // We must now edit each service option to reflect the player's status.
        List<ServiceOptionBase> newServiceOptions = [];

        foreach (var i in OfferedIndexes(wizard)) {
            // In randomizer mode the wizard learns a swapped spell, so the trainer offers that one instead.
            // A roguelike run offers its own random spells.
            var trainerSpellId = (uint) SpellInventory[i].TemplateID;
            var (spellId, offeredLevel, isRunSpell) = SpellFor(wizard, i);
            if (CoreObjectFactory.GetCoreTemplate(spellId) is not SpellTemplate spellTemplate) {
                continue;
            }

            var hasSpell = wizard.SpellbookBehavior.HasSpell(spellId);
            if (hasSpell) {
                continue;
            }

            // Copy the shared option so one wizard's view never leaks into another's.
            var newBase = (WizTrainingOption) _serviceOptionBases[i] with { };
            if (spellId != trainerSpellId) {
                newBase.m_spellName = spellTemplate.m_name;
                newBase.m_displayKey = spellTemplate.m_displayName;
            }

            // Training costs nothing for the wizard's own school (or for everyone with FreeTraining), else one point.
            newBase.m_trainingCost = CostFor(wizard, spellTemplate);

            // NoLevelRequirements lets any level train any spell.
            var requiredLevel = offeredLevel;
            newBase.m_requiredLevel = requiredLevel;

            // Check if wizard has enough training points to train the spell, and if the
            // wizard is high enough level to train the spell.
            if (wizard.MagicSchoolBehavior.TrainingPoints < newBase.m_trainingCost
                    || wizard.MagicSchoolBehavior.Level < requiredLevel) {
                newBase.m_bCanTrain = false;
                newBase.m_failedRequirement = null;

                newServiceOptions.Add(newBase);
                continue;
            }

            // If the spell has no required spell, it can be trained, else check if the wizard has the required spell.
            // A run's random spells have no prerequisites.
            var originalRequiredId = isRunSpell ? 0 : RequiredSpellFor(wizard, SpellInventory[i]);
            if (originalRequiredId == 0) {
                newBase.m_bCanTrain = true;
                newBase.m_failedRequirement = null;
                newBase.m_requirements = null;
            }
            else {
                // The required spell is swapped the same way, so a randomized chain still lines up.
                var requiredSpellId = Randomizer.MapSpell(wizard, (uint) originalRequiredId);
                if (CoreObjectFactory.GetCoreTemplate(requiredSpellId) is not SpellTemplate reqSpellTemplate) {
                    // A requirement that can't be found doesn't block training.
                    newBase.m_bCanTrain = true;
                    newBase.m_failedRequirement = null;
                    newBase.m_requirements = null;
                    newServiceOptions.Add(newBase);
                    continue;
                }

                if (requiredSpellId != SpellInventory[i].RequiredSpellID) {
                    newBase.m_requirements = HasSpellRequirement(reqSpellTemplate);
                }

                var hasReqSpell = wizard.SpellbookBehavior.HasSpell(requiredSpellId);

                if (!hasReqSpell) {
                    newBase.m_bCanTrain = false;
                    newBase.m_failedRequirement = new ReqHasSpell() {
                        m_applyNOT = false,
                        m_operator = Operator.ROP_AND,
                        m_spellName = reqSpellTemplate.m_name
                    };
                }
                else {
                    newBase.m_bCanTrain = true;
                    newBase.m_failedRequirement = null;
                }
            }

            newServiceOptions.Add(newBase);
        }

        return newServiceOptions;
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) { }

    /// <summary>
    /// The spell this wizard is offered at <paramref name="index"/>, the level it needs, and whether it is a
    /// roguelike run's random pick. Used both to list the spells and to teach one, so the two always agree.
    /// </summary>
    internal (uint SpellId, int Level, bool IsRunSpell) SpellFor(Wizard wizard, int index) {
        var entry = SpellInventory[index];
        if (!IsTestTrainer && Imlight.CoreLib.Game.Runs.RoguelikeStock.TrainersActiveFor(wizard)) {
            // Past the run's short list there is nothing to train (spell 0 is not a spell).
            return Imlight.CoreLib.Game.Runs.RoguelikeStock.TrainerSpellFor(
                       wizard, Entity.ActiveGameObject.m_templateID, index, SpellInventory.Count) is { } pick
                ? (pick.SpellId, Randomizer.NoLevelRequirements ? 1 : pick.Level, true)
                : (0u, int.MaxValue, true);
        }

        var spellId = IsTestTrainer ? (uint) entry.TemplateID : Randomizer.MapSpell(wizard, (uint) entry.TemplateID);

        return (spellId, Randomizer.NoLevelRequirements ? 1 : entry.Level, false);
    }

    /// <summary>
    /// How many training points a spell costs this wizard: none for their own school or with FreeTraining, else one.
    /// </summary>
    internal int CostFor(Wizard wizard, SpellTemplate spellTemplate)
        => IsTestTrainer ? 0 : TrainingCost(wizard, spellTemplate);

    internal static int TrainingCost(Wizard wizard, SpellTemplate spellTemplate) {
        if (Randomizer.FreeTraining) {
            return 0;
        }

        string school = spellTemplate.m_sMagicSchoolName;

        return school == wizard.MagicSchoolBehavior.MagicSchool.ToString() ? 0 : 1;
    }

    /// <summary>
    /// The spell (before randomizer swaps) a wizard must know to train this entry, or 0 for none.
    /// NoSpellRequirements removes them all. RandomizeSpellRequirements gives each spell that has one a
    /// requirement picked from this trainer's lower level spells, so every chain can still be finished.
    /// </summary>
    private ulong RequiredSpellFor(Wizard wizard, NPCSpellEntry entry) {
        if (Randomizer.NoSpellRequirements || entry.RequiredSpellID == 0) {
            return 0;
        }

        if (!Randomizer.RandomizeSpellRequirements) {
            return entry.RequiredSpellID;
        }

        var lower = SpellInventory
            .Where(other => other.Level < entry.Level && other.TemplateID != entry.TemplateID)
            .Select(other => other.TemplateID)
            .OrderBy(id => id)
            .ToArray();

        return lower.Length == 0 ? 0 : lower[Randomizer.PickIndex(wizard, "requirements", entry.TemplateID, lower.Length)];
    }

    private void InitializeServiceBases() {
        // Foreach spell in the inventory, create a service option. This method serves to initialize defaults.
        // When a player interacts with this object, we'll go back and edit each service option
        // depending on the player's current level, school, and existing spell inventory.
        _serviceOptionBases = [];

        foreach (var spell in SpellInventory) {
            var template = CoreObjectFactory.GetCoreTemplate(spell.TemplateID);
            if (template is null || template is not SpellTemplate spellTemplate) {
                Logger.Error("Trainer {0} has an invalid spell template with ID {1}",
                    Logger.Args(Entity.ActiveGameObject.m_templateID, spell.TemplateID));

                continue;
            }

            var spellOption = new WizTrainingOption {
                m_iconKey = WizBang.ToString(),
                m_serviceName = ServiceName,

                m_spellName = spellTemplate.m_name,
                m_displayKey = spellTemplate.m_displayName,
                m_requiredLevel = spell.Level,
                m_trainingIndex = SpellInventory.IndexOf(spell),
                m_trainingCost = spellTemplate.m_trainingCost,
            };

            if (spell.RequiredSpellID != 0) {
                var reqTemplate = CoreObjectFactory.GetCoreTemplate(spell.RequiredSpellID);
                if (reqTemplate is null || reqTemplate is not SpellTemplate reqSpellTemplate) {
                    Logger.Error("Trainer {0} has an invalid required spell template with ID {1}",
                        Logger.Args(Entity.ActiveGameObject.m_templateID, spell.RequiredSpellID));

                    continue;
                }

                spellOption.m_requirements = HasSpellRequirement(reqSpellTemplate);
            }
            else {
                spellOption.m_requirements = null;
            }

            _serviceOptionBases.Add(spellOption);
        }
    }

    private static RequirementList HasSpellRequirement(SpellTemplate requiredSpell)
        => new() {
            m_applyNOT = false,
            m_operator = Operator.ROP_AND,
            m_requirements = new List<Requirement>() {
                new ReqHasSpell() {
                    m_applyNOT = false,
                    m_operator = Operator.ROP_AND,
                    m_spellName = requiredSpell.m_name,
                }
            }
        };

}