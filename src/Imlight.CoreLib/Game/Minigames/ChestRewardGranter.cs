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
 * CHEST REWARD GRANTER
 * ========================================================================
 *
 * PURPOSE:
 * Rolls and grants the loot of a silver chest the player won, and builds the loot list the minigame
 * rewards window shows for it.
 *
 * USAGE EXAMPLE:
 * var reward = ChestRewardGranter.Grant(playerActor, character, chestTemplateId, lootTables);
 *
 * NOTE:
 * The window draws at most 4 slots, and the gold always takes the second one. Only items and treasure
 * cards besides gold are drawn, so other granted loot is left out of the window's list. The gold shown
 * is what fit into the pouch.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Minigames;

internal static class ChestRewardGranter {

    private const int WINDOW_SLOTS = 4;

    internal static MinigameRewardResult NoReward()
        => new(DropTableConverter.CreateEmpty(), 0, false);

    internal static MinigameRewardResult Grant(IActorRef playerActor,
                                               CHARACTER_103_PROTOCOL.MSG_CHARACTER character,
                                               ulong chestTemplateId,
                                               IReadOnlyList<string> lootTables) {
        var wizard = character.Wizard;

        // On a roguelike run a silver chest holds the run's better rewards (its loot tables gave nothing here).
        var onRun = Imlight.CoreLib.Game.Runs.RoguelikeConfig.Enabled && wizard?.Roguelike is not null;
        var runGold = 0;
        var rolled = onRun ? RunLoot(playerActor, wizard, out runGold)
            : ChestLootRoller.Roll(lootTables, playerActor, character.WizardGameObject, wizard);
        FitGoldToPouch(rolled, wizard);

        var granted = LootGranter.Grant(playerActor, wizard, rolled);
        if (runGold > 0) {
            // Given as it is (not multiplied), like the wooden chests' gold, and shown in the window.
            runGold = Math.Min(runGold, Math.Max(0, wizard.GameStats.m_baseGoldPouch - wizard.GameStats.m_currentGold));
            LootGranter.UpdateWizardGold(playerActor, wizard, runGold);
            granted.GoldAmount = runGold;
        }
        var lootList = DropTableConverter.ToLootInfoList(granted);

        // The window draws items and treasure cards only (and the gold), so it is given only those.
        var drawn = lootList.m_loot.Where(x => x is ItemLootInfo or TreasureCardLootInfo).ToList();
        if (drawn.Count < lootList.m_loot.Count) {
            Logger.Information("Chest {Template} granted loot that the rewards window cannot show to {Wizard}",
                Logger.Args(chestTemplateId, wizard.PlayerNameBehavior.GetWizardName()));
        }

        lootList.m_loot = drawn;

        var slots = drawn.Count + (lootList.m_goldInfo is { m_goldAmount: > 0 } ? 1 : 0);
        if (slots > WINDOW_SLOTS) {
            Logger.Warning("Chest {Template} granted {Slots} rewards, the rewards window shows {WindowSlots}",
                Logger.Args(chestTemplateId, slots, WINDOW_SLOTS));
        }

        var finalPhase = MinigameRewardTiers.FinalPhaseForSlots(Math.Min(slots, WINDOW_SLOTS));

        return new MinigameRewardResult(lootList, finalPhase, slots > 0);
    }

    // [Roguelike] Silver chests: SilverChestItems rewards picked by SilverChestRewards ("kind:weight": gear, jewel,
    // spell), SilverChestGoldMin to SilverChestGoldMax gold, and a SilverChestPetChance% chance of a run pet as well.
    private static readonly (string Kind, int Weight)[] s_silverRewards = [.. (ConfigurationManager.GetValue(
            "Roguelike.SilverChestRewards", "gear:40, jewel:35, spell:25") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split(':', 2))
        .Where(pair => pair.Length == 2 && int.TryParse(pair[1].Trim(), out var w) && w > 0)
        .Select(pair => (pair[0].Trim().ToLowerInvariant(), int.Parse(pair[1].Trim())))];
    private static readonly int s_silverItems = Math.Clamp(ConfigurationManager.GetValue("Roguelike.SilverChestItems", 2), 0, 3);
    private static readonly int s_silverGoldMin = Math.Max(0, ConfigurationManager.GetValue("Roguelike.SilverChestGoldMin", 10000));
    private static readonly int s_silverGoldMax = Math.Max(s_silverGoldMin, ConfigurationManager.GetValue("Roguelike.SilverChestGoldMax", 50000));
    private static readonly int s_silverPetChance = Math.Clamp(ConfigurationManager.GetValue("Roguelike.SilverChestPetChance", 20), 0, 100);

    private static DropTableResult RunLoot(IActorRef playerActor, Wizard wizard, out int gold) {
        var loot = new DropTableResult();
        var given = new List<string>();
        for (var i = 0; i < s_silverItems; i++) {
            var kind = RollSilverKind();
            uint? id = kind switch {
                "gear" => Imlight.CoreLib.Game.Runs.RoguelikeStock.PickChestGear(wizard),
                "jewel" => Imlight.CoreLib.Game.Runs.RoguelikeStock.PickJewel(wizard),
                "spell" => Imlight.CoreLib.Game.Runs.RoguelikeStock.RollSpellToLearn(wizard, 100),
                _ => null,
            };
            id ??= Imlight.CoreLib.Game.Runs.RoguelikeStock.PickChestGear(wizard);
            if (id is { } itemId) {
                loot.Items.Add(new DropItemResult { ItemId = itemId.ToString(), Quantity = 1 });
                given.Add(kind);
            }
        }

        gold = Random.Shared.Next(s_silverGoldMin, s_silverGoldMax + 1);
        if (Random.Shared.Next(100) < s_silverPetChance
            && Imlight.CoreLib.Game.Zone.Components.WoodenChestComponent.GivePet(playerActor, wizard)) {
            given.Add("pet");
        }

        Logger.Information("[Roguelike] {0} won a silver chest: {1} and {2} gold.",
            Logger.Args(wizard.CharId, string.Join(", ", given), gold));

        return loot;
    }

    private static string RollSilverKind() {
        var total = s_silverRewards.Sum(r => r.Weight);
        var roll = total > 0 ? Random.Shared.Next(total) : 0;
        foreach (var (kind, weight) in s_silverRewards) {
            if (roll < weight) {
                return kind;
            }

            roll -= weight;
        }

        return "gear";
    }

    private static void FitGoldToPouch(DropTableResult rolled, Wizard wizard) {
        var room = Math.Max(0, wizard.GameStats.m_baseGoldPouch - wizard.GameStats.m_currentGold);
        if (rolled.GoldAmount <= room) {
            return;
        }

        if (room == 0) {
            Logger.Information("Chest gold of {Gold} dropped, the pouch of {Wizard} is full",
                Logger.Args(rolled.GoldAmount, wizard.PlayerNameBehavior.GetWizardName()));
        }

        rolled.GoldAmount = room;
    }

}
