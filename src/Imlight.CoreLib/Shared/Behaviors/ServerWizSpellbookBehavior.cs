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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Imlight.CoreLib.Game.Spells;
using Imlight.Common;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Shared.Behaviors;

[Serializable]
public class ServerWizSpellbookBehavior : ServerSpellbookBehavior {

    [JsonIgnore] public new bool NoTransfer { get; set; } = false;

    [JsonIgnore] public MagicSchool PrimarySchool { get; set; }
    [JsonIgnore] public int GenericMaxRank { get; set; }
    [JsonIgnore] public int SchoolMaxRank { get; set; }
    [JsonIgnore] public int GenericMaxInstances { get; set; }
    [JsonIgnore] public int SchoolMaxInstances { get; set; }
    [JsonIgnore] public int MaxSpells { get; set; }
    [JsonIgnore] public int MaxTreasureCards { get; set; }

    // One entry per crossed out copy: gear can give the same card more than once, and with a set only one of two
    // crossed out copies stayed crossed out after a zone change (Niduus, 2026-10-19).
    public Dictionary<ulong, List<uint>> ExcludedItemSpellIds { get; set; } = [];

    public void InitializeProperties(DeckBehaviorTemplate deckTemplate) {
        SetPropertiesFromDeckTemplate(deckTemplate);
    }

    public void InitializeSpells(DeckBehavior deckBehavior) {
        if (deckBehavior is null) {
            return;
        }

        base.SpellList = deckBehavior.m_spellList;
    }

    public bool EquipDeck(WizItemTemplate template, DeckBehavior deckBehavior) {
        if (template is null) {
            return false;
        }

        // Search for a deck behavior template within the item template.
        foreach (var behaviorTemplate in template.m_behaviors) {
            if (behaviorTemplate is not DeckBehaviorTemplate deckBehaviorTemplate) {
                continue;
            }

            // We've found what we're looking for. Set the deck behavior properties.
            SetPropertiesFromDeckTemplate(deckBehaviorTemplate);
            base.SpellList = deckBehavior.m_spellList;

            return true;
        }

        return false;
    }

    public bool AddSpellToDeck(uint spellTemplateId) {
        base.SpellList ??= new List<SpellData>();

        // Get the spells template; we'll need it for the magic school ID.
        var spellTemplate = SpellFactory.GetSpell(spellTemplateId);
        if (spellTemplate is null) {
            Logger.Debug("Failed to create spell from template {0}.", Logger.Args(spellTemplateId));
            
            return false;
        }

        // Treasure cards have their own slot limit and skip the per-spell copy cap.
        var isTreasure = spellTemplate.m_treasureCard;
        if (isTreasure) {
            if (CountDeckCards(true) >= MaxTreasureCards) {
                Logger.Debug("The deck already has the maximum amount of allowed treasure cards.");

                return false;
            }
        }
        else if (CountDeckCards(false) >= MaxSpells) {
            Logger.Debug("The deck already has the maximum amount of allowed spells.");

            return false;
        }

        // Create a new SpellData for this spell, if one doesn't already exist.
        var spellData = SpellList.Find(x => x.m_templateID == spellTemplateId);
        if (spellData is null) {
            // If the spell doesn't exist in the deck, we'll want to add it.
            spellData = new SpellData {
                m_templateID = spellTemplateId,
                m_quantity = 1
            };
            SpellList.Add(spellData);
        }
        else {
            // Otherwise, we'll want to increase the quantity so long as the number of max instances hasn't been reached.
            if (!isTreasure) {
                var spellSchool = (MagicSchool) spellTemplate.m_magicSchoolID;
                var maxInstances = spellSchool == PrimarySchool ? SchoolMaxInstances : GenericMaxInstances;
                if (spellData.m_quantity >= maxInstances) {
                    Logger.Debug("The deck already has the maximum amount of allowed instances of spell {0}.", Logger.Args(spellTemplateId));
                    
                    return false;
                }
            }

            spellData.m_quantity++;
        }

        return true;
    }

    public bool RemoveSpellFromDeck(uint spellTemplateId) {
        if (SpellList is null) {
            return false;
        }

        var spellData = SpellList.Find(x => x.m_templateID == spellTemplateId);
        if (spellData is null) {
            Logger.Debug("The deck does not contain spell {0}.", Logger.Args(spellTemplateId));
            return false;
        }

        // Decrease the quantity, if we can. Otherwise, remove the spell data.
        if (spellData.m_quantity - 1 <= 0) {
            SpellList.Remove(spellData);
        }
        else {
            spellData.m_quantity--;
        }

        return true;
    }

    /// <summary>
    /// Adds or removes a spell template ID from a deck item's exclusion list.
    /// </summary>
    /// <param name="deckId">The global ID of the deck item.</param>
    /// <param name="spellTemplateId">The spell template ID to exclude/include.</param>
    /// <param name="exclude">True to exclude, false to include (un-exclude).</param>
    public void SetItemSpellExclusion(ulong deckId, uint spellTemplateId, bool exclude, int copies) {
        if (exclude) {
            if (!ExcludedItemSpellIds.TryGetValue(deckId, out var list)) {
                list = [];
                ExcludedItemSpellIds[deckId] = list;
            }

            if (list.Count(id => id == spellTemplateId) < Math.Max(copies, 1)) {
                list.Add(spellTemplateId);
            }
        }
        else {
            if (ExcludedItemSpellIds.TryGetValue(deckId, out var list)) {
                list.Remove(spellTemplateId);
                if (list.Count == 0) {
                    ExcludedItemSpellIds.Remove(deckId);
                }
            }
        }
    }

    /// <summary>
    /// How many copies of the given spell template are crossed out for the given deck item.
    /// </summary>
    public int ExcludedItemSpellCount(ulong deckId, uint spellTemplateId)
        => ExcludedItemSpellIds.TryGetValue(deckId, out var list)
            ? list.Count(id => id == spellTemplateId)
            : 0;

    public new ClientSpellbookBehavior GetClientBehaviorInstance() {
        var spellIdList = new List<SpellIDTracker>();
        foreach (var templateId in LearnedSpellTemplateIds) {
            spellIdList.Add(new SpellIDTracker {
                m_isRetired = false,
                m_spellID = templateId,
            });
        }

        return new ClientSpellbookBehavior {
            m_spellIDList = spellIdList
        };
    }

    private int CountDeckCards(bool treasure) {
        var count = 0;
        foreach (var spellData in SpellList) {
            var spell = SpellFactory.GetSpell(spellData.m_templateID);
            if (spell is not null && spell.m_treasureCard == treasure) {
                count += (int) spellData.m_quantity;
            }
        }

        return count;
    }

    private void SetPropertiesFromDeckTemplate(DeckBehaviorTemplate template) {
        // Set the deck behavior properties.
        // Try to parse the string school as a MagicSchool enum.
        MagicSchool school;
        if (string.IsNullOrEmpty(template.m_primarySchoolName)
            || !Enum.TryParse(template.m_primarySchoolName, true, out school)) {
            school = MagicSchool.None;
        }
        this.PrimarySchool = school;
        this.GenericMaxRank = template.m_genericMaxRank;
        this.SchoolMaxRank = template.m_schoolMaxRank;
        this.GenericMaxInstances = template.m_genericMaxInstances;
        this.SchoolMaxInstances = template.m_schoolMaxInstances;
        this.MaxSpells = template.m_maxSpells;
        this.MaxTreasureCards = template.m_maxTreasureCards;
    }
    
}
