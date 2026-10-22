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
 * WOODEN CHEST COMPONENT
 * ========================================================================
 * 
 * PURPOSE:
 * This component handles the interaction logic for wooden chests in the game.
 * The gold value is randomly generated between 10 and 100. On a roguelike server a chest instead holds one
 * random reward (ChestRewards): gold, gear, a jewel, a spell or a pet.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Phill
 * Version: KALI 1.0
 * Last Updated: 10/09/2025
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class WoodenChestComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory, IWithTimers {

    private const int ANIMATION_TIME = 1;

    private static readonly uint[] s_treasureChestTemplateIDs = [1233729, 77825, 77884, 77826, 77827, 1562742, 77823, 470099];
    private static readonly uint s_openSoundTemplateID = 1374674914;
    private readonly TimeSpan _chestOpenAnimationTimeSpan = TimeSpan.FromSeconds(ANIMATION_TIME);

    public string ServiceName => "Interact";
    public string NpcIcon => "GUI/QuestButtons/Art_Quest_Chest_Wood.dds";
    public string NpcNameKey => "WizardGameObjects_00000054";
    public string NpcTextKey => "GUI_ChestInteract";
    public WizBangs WizBang => WizBangs.None;
    public string StateName => "Open";
    public string InteractWizBang => "";
    public string DisplayKey => "";

    public ITimerScheduler Timers { get; set; }

    private bool _hasInteracted = false;

    public static bool ShouldAttachToEntity(CoreTemplate template) =>
        template is GameObjectTemplate goTemplate
        && s_treasureChestTemplateIDs.Contains(goTemplate.m_templateID);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [
            new ServiceOptionBase() {
                m_displayKey = DisplayKey,
                m_forceInteract = false,
                m_iconKey = NpcIcon,
                m_serviceName = ServiceName,
                m_serviceIndex = 0
            }
         ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (_hasInteracted) {
            return;
        }

        _hasInteracted = true;

        // On a roguelike server a chest holds one random reward; elsewhere, a little gold.
        if (Imlight.CoreLib.Game.Runs.RoguelikeConfig.Enabled && playerCharacter?.Roguelike is not null) {
            GiveRunReward(playerActor, playerCharacter);
        }
        else {
            GiveGold(playerActor, playerCharacter, Random.Shared.Next(10, 101));
        }

        PlaySound(playerActor);
        TriggerChestAnimation();

        var delayedDeleteMsg = new ZONE_102_PROTOCOL.MSG_DELAYEDDELETEOBJECT();
        Timers.StartSingleTimer("deletechestobject", delayedDeleteMsg, _chestOpenAnimationTimeSpan);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_DELAYEDDELETEOBJECT))]
    private void ReceivedDelayedDeleteMessage(ZONE_102_PROTOCOL.MSG_DELAYEDDELETEOBJECT _) 
        => Entity.DeleteObject();

    private void TriggerChestAnimation() {
        var chestStates = new uint[] { StringHash.Compute(StateName), StringHash.Compute("NotInteracting") };
        for (int i = 0; i < chestStates.Length; i++) {
            var enterState = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
                Data = "",
                GameObjectID = Entity.ActiveGameObject.m_globalID,
                IgnoreIfCurrentStateIsOff = 0,
                State = chestStates[i]
            };

            var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
                Message = enterState,
                Selfless = false,
            };

            Entity.ZoneRef.Tell(broadcastMsg);
        }
    }

    private void SendLoot(IActorRef playerActor, int goldAmount) {
        var lootInfoList = new LootInfoList {
            m_loot = [],
            m_goldInfo = new GoldLootInfo {
                m_goldAmount = goldAmount,
                m_lootType = LOOT_TYPE.LOOT_TYPE_GOLD,
            },
            m_lootRarityList = new LootRarityList {
                m_loot = []
            }
        };

        var serializer = new ObjectSerializer(
            false,
            Behaviors: SerializerFlags.None
        );

        if (!serializer.Serialize(lootInfoList, 4, out var data)) {
            Logger.Error("Failed to serialize LootInfoList.");

            return;
        }

        var loot = new WIZARD_12_PROTOCOL.MSG_LOOT {
            GlobalID = Entity.ActiveGameObject.m_globalID,
            LootList = data
        };

        playerActor.Tell(loot);
    }

    private void GiveGold(IActorRef playerActor, Wizard playerCharacter, int goldAmount) {
        SendLoot(playerActor, goldAmount);

        // Adds the gold, then tells the game the wizard's new total. (This used to send the chest's amount as the
        // total, which showed a second line that set the wizard's gold to it.)
        Imlight.CoreLib.Game.DropTables.LootGranter.UpdateWizardGold(playerActor, playerCharacter, goldAmount);
    }

    // ---------------------------------------------------------------------
    // Roguelike chests
    // ---------------------------------------------------------------------

    /// <summary>
    /// [Roguelike] ChestRewards: how likely each reward of a street chest is, as "kind:weight" pairs. Kinds: gold
    /// (ChestGoldMin to ChestGoldMax, not multiplied), gear (ordinary or crown, the wizard's level range), jewel
    /// (a jewel or pin), spell (one the wizard can learn) and pet (a run pet, rolled like a dropped one).
    /// </summary>
    private static readonly (string Kind, int Weight)[] s_chestRewards = ParseWeights(
        ConfigurationManager.GetValue("Roguelike.ChestRewards", "gold:35, gear:25, jewel:20, spell:10, pet:10"));
    private static readonly int s_chestGoldMin = Math.Max(0, ConfigurationManager.GetValue("Roguelike.ChestGoldMin", 1000));
    private static readonly int s_chestGoldMax = Math.Max(s_chestGoldMin, ConfigurationManager.GetValue("Roguelike.ChestGoldMax", 10000));

    private static (string, int)[] ParseWeights(string text)
        => [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split(':', 2))
            .Where(pair => pair.Length == 2 && int.TryParse(pair[1].Trim(), out var w) && w > 0)
            .Select(pair => (pair[0].Trim().ToLowerInvariant(), int.Parse(pair[1].Trim())))];

    private void GiveRunReward(IActorRef playerActor, Wizard wizard) {
        // Whatever the roll, a reward that cannot be given (nothing fits the wizard's level) falls back to gold.
        var kind = RollKind();
        var given = kind switch {
            "gear" => GiveItem(playerActor, wizard, Imlight.CoreLib.Game.Runs.RoguelikeStock.PickChestGear(wizard)),
            "jewel" => GiveItem(playerActor, wizard, Imlight.CoreLib.Game.Runs.RoguelikeStock.PickJewel(wizard)),
            "spell" => GiveItem(playerActor, wizard, Imlight.CoreLib.Game.Runs.RoguelikeStock.RollSpellToLearn(wizard, 100)),
            "pet" => GivePet(playerActor, wizard),
            _ => false,
        };
        if (!given) {
            kind = "gold";
            GiveGold(playerActor, wizard, Random.Shared.Next(s_chestGoldMin, s_chestGoldMax + 1));
        }

        Logger.Information("[Roguelike] {0} opened a chest: {1}.", Logger.Args(wizard.CharId, kind));
    }

    private static string RollKind() {
        var total = s_chestRewards.Sum(r => r.Weight);
        if (total <= 0) {
            return "gold";
        }

        var roll = Random.Shared.Next(total);
        foreach (var (kind, weight) in s_chestRewards) {
            if (roll < weight) {
                return kind;
            }

            roll -= weight;
        }

        return "gold";
    }

    // Gear, jewels and spells go through the loot window like any drop (a spell shows "New Spell Learned").
    private static bool GiveItem(IActorRef playerActor, Wizard wizard, uint? templateId) {
        if (templateId is not { } id) {
            return false;
        }

        var loot = new Imlight.CoreLib.WizardData.Models.World.DropTableResult();
        loot.Items.Add(new Imlight.CoreLib.WizardData.Models.World.DropItemResult { ItemId = id.ToString(), Quantity = 1 });
        Imlight.CoreLib.Game.DropTables.LootGranter.GrantAndDisplay(playerActor, wizard, loot);

        return true;
    }

    private static bool GivePet(IActorRef playerActor, Wizard wizard) {
        var pet = Imlight.CoreLib.Game.Runs.RoguelikePets.Build(wizard);
        if (pet is null || !wizard.AddPetToInventory(pet)) {
            return false;
        }

        Imlight.CoreLib.Shared.Items.ItemSync.SendAdded(playerActor, wizard, pet);
        playerActor.Tell(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = pet.m_globalID,
            ItemLocation = 1,
            ItemTemplateID = (uint) pet.m_templateID,
        });

        return true;
    }

    private static void PlaySound(IActorRef playerActor) {
        var soundId = new GID(s_openSoundTemplateID);
        var playSound = new GAME_5_PROTOCOL.MSG_PLAYSOUND {
            SoundID = soundId,
            ReinteractTime = 0,
            SoundFilename = "",
            StartDelay = 0,
            PlayAtMusicVolume = 0,
        };

        playerActor.Tell(playSound);
    }

}
