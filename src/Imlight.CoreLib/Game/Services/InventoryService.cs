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
 * INVENTORY SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player inventory interactions, including item management, 
 * quick selling, and special item handling.
 * 
 * USAGE EXAMPLE:
 * Internal service handling various inventory-related messages within 
 * the game server session.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty, Joji
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Services;

internal class InventoryService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new InventoryService(parentActor));

    #region Destroy/Feed Inventoryitem

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_TRASHINVENTORYITEM))]
    private void ReceiveTrashInventoryItem(GAME_5_PROTOCOL.MSG_TRASHINVENTORYITEM message) {
        var wizard = GetActiveWizard();

        // Check if this is an equipped item (e.g. an active elixir).
        var slot = wizard.EquipmentBehavior.SlotList?.FirstOrDefault(s => s.ItemId.Value == message.GlobalID);
        if (slot is not null || wizard.EquipmentBehavior.HasItemEquipped(message.GlobalID)) {
            var item = wizard.EquipmentBehavior.GetItem(message.GlobalID);
            var template = ItemHelper.GetItemTemplate(item);

            if (template is null) {
                var dbItem = WizardItemCollection.GetItem(wizard.CharId, message.GlobalID);
                if (dbItem is not null) {
                    template = ItemHelper.GetItemTemplate(dbItem);
                }
            }

            if (slot is null) {
                Logger.Error("Tried to unequip item with global id {0} that does not exist in player equipment.",
                    Logger.Args(message.GlobalID)); 
                return;
            }

            if (template is null) {
                Logger.Error("Tried to unequip item with global id {0} but could not find its template.",
                    Logger.Args(message.GlobalID));
                return;
            }

            var slotName = slot.SlotType.ToString();
            var removedEffects = CharacterEffectHelper.RemoveEffectsFromWizard(wizard, template);
            wizard.EquipmentBehavior.UnequipItem(message.GlobalID);
            WizardItemCollection.RemoveItem(wizard.CharId, message.GlobalID);
            WizardCollection.UpdateCharacterItems(wizard);

            // For elixirs, send MSG_EQUIPMENTBEHAVIOR_UNEQUIPITEM so the client's ElixirGUIManager
            // receives the "EQUIPMENT_UnequipItem" event and destroys the HUD timer icon.
            if (slot.SlotType == EquipmentSlotType.Elixir) {
                SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPMENTBEHAVIOR_UNEQUIPITEM {
                    GlobalID = wizard.GameObjectID,
                    ItemID = message.GlobalID
                });
            }
            else {
                SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM {
                    ItemID = message.GlobalID,
                    SlotName = slotName,
                    IsEquip = 0
                });
            }

            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
                GlobalID = wizard.GameObjectID,
                ItemID = message.GlobalID
            });

            if (removedEffects is not null) {
                foreach (var effect in removedEffects) {
                    SendToSocket(new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
                        GameObjectID = wizard.GameObjectID,
                        EffectNameID = effect.m_effectNameID,
                        InternalID = effect.m_internalID
                    });
                }
            }

            if (template.m_behaviors?.OfType<ElixirBehaviorTemplate>().FirstOrDefault() is { } elixirTemplate) {
                PlaySound(elixirTemplate.m_unequipSound);
            }

            // Sync health and mana display, clamping current values to the new maximums.
            var clientStats = wizard.GameStats.GetClientTypeAlternative();
            if (wizard.GameStats.m_currentHitpoints > clientStats.m_baseHitpoints) {
                wizard.GameStats.m_currentHitpoints = clientStats.m_baseHitpoints;
            }

            if (wizard.GameStats.m_currentMana > clientStats.m_baseMana) {
                wizard.GameStats.m_currentMana = clientStats.m_baseMana;
            }

            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH {
                CharacterID = wizard.GameObjectID,
                NewHealth = wizard.GameStats.m_currentHitpoints,
                NewHealthMax = clientStats.m_baseHitpoints,
                DisplayDiff = 1
            });

            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEMANA {
                Mana = wizard.GameStats.m_currentMana,
                MaxMana = clientStats.m_baseMana,
                DisplayDiff = 1
            });

            return;
        }

        wizard.RemoveItemFromInventory(message.GlobalID);
        SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM() {
            GlobalID = wizard.GameObjectID,
            ItemID = message.GlobalID
        });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_FEEDINVENTORYITEM))]
    private void ReceiveFeedInventoryItem(GAME_5_PROTOCOL.MSG_FEEDINVENTORYITEM message) {
        SendToSocket(new GAME_5_PROTOCOL.MSG_FEEDINVENTORYITEM() {
            FedObjectID = message.FedObjectID,
            PetID = message.PetID,
        });
    }

    #endregion

    #region Quicksell from Inventory

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REQUESTQUICKSELL))]
    private void ReceiveRequestQuickSell(WIZARD_12_PROTOCOL.MSG_REQUESTQUICKSELL message) {
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_REQUESTQUICKSELL() {
            FromTemplateID = message.FromTemplateID,
            Section = message.Section,
            SellModifier = message.SellModifier + 0.05f, // (?) Live server uses ~0.05f.
        });
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_QUICKSELLREQUEST))]
    private void ReceiveQuickSellRequest(WIZARD2_53_PROTOCOL.MSG_QUICKSELLREQUEST message) {
        // Non-versionable: the client compact-encodes this Data blob, same as every other
        // hand-defined message's embedded ObjectProperty payload (CombatDuelComponent, etc.).
        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );

        var wizard = GetActiveWizard();
        int goldSum = 0;

        if (!serializer.Deserialize<QuickSellItemList>(message.Data, 4, out var quickSellItemList)
            || quickSellItemList.m_quickSellItemList is null) {
            Logger.Log.Error("Failed to deserialize quicksell item list.");

            return;
        }

        // Remove items from inventory and equipment, tally up gold sum.
        foreach (QuickSellItem quickSellItem in quickSellItemList.m_quickSellItemList) {
            var item = wizard.InventoryBehavior.GetItem(quickSellItem.m_sellItemGID);
            if (item is not null) {
                var template = (WizItemTemplate) CoreObjectFactory.GetCoreTemplate(item.m_templateID);

                wizard.RemoveItemFromInventory(item.m_globalID);

                // Some items (snack, reagents) are stackable.
                for (int i = 0; i < quickSellItem.m_quantity; i++) {
                    SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM() {
                        GlobalID = wizard.GameObjectID,
                        ItemID = quickSellItem.m_sellItemGID
                    });

                    var value = (int) Math.Ceiling(template.m_baseCost * 0.05f);
                    if (template.m_numPrimaryColors != 1 && template.m_numSecondaryColors != 0) {
                        value = (int) Math.Ceiling(value * 1.2275f); // Dyed items are more expensive.
                    }

                    goldSum += value;
                }

                continue;
            }

            // Treasure cards aren't in InventoryBehavior: the spellbook tracks them by
            // spell hash (see SpellBookService's treasure handlers), not a GID.
            var sellItemHash = (uint) (ulong) quickSellItem.m_sellItemGID;
            var treasureCardTemplateId = SpellFactory.GetTemplateIdByHash(sellItemHash);
            if (treasureCardTemplateId != 0
                && CoreObjectFactory.GetCoreTemplate(treasureCardTemplateId) is SpellTemplate spellTemplate) {
                for (int i = 0; i < quickSellItem.m_quantity; i++) {
                    if (!wizard.SpellbookBehavior.RemoveTreasureCard(treasureCardTemplateId)) {
                        break;
                    }

                    WizardCollection.RemoveTreasureCard(wizard, treasureCardTemplateId);

                    SendToSocket(new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMBOOK() {
                        SpellID = (int) sellItemHash,
                        EnchantmentID = 0
                    });

                    goldSum += (int) Math.Ceiling(spellTemplate.m_baseCost * 0.05f);
                }

                continue;
            }

            Logger.Log.Warning("Could not resolve quicksell item {0}; nothing was sold for it.",
                Logger.Args(quickSellItem.m_sellItemGID));
        }

        // Update player with their new gold balance.
        wizard.AddGold(goldSum);
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD() {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch,
        });

        // End quicksell process with empty message.
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_QUICKSELLREQUEST());
    }

    #endregion

    #region Jewels

    private static readonly CoreObjectSerializer s_itemSerializer = new(behaviors: SerializerFlags.None);

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_EQUIPJEWELREQUEST))]
    private void ReceiveEquipJewelRequest(WIZARD2_53_PROTOCOL.MSG_EQUIPJEWELREQUEST message) {
        var wizard = GetActiveWizard();
        Logger.Information("Socket request: item {0}, jewel {1}, socket {2}.",
            Logger.Args(message.ItemGID, message.JewelGID, message.SocketNumber));

        try {
            var item = FindOwnedItem(wizard, message.ItemGID);
            var jewel = wizard.InventoryBehavior.GetItem(message.JewelGID);
            if (item is null || jewel is null) {
                InformGameClient("That item or jewel is not in your backpack.");

                return;
            }

            var problem = JewelSockets.CheckSocket(wizard, item, ItemHelper.GetItemTemplate(jewel), message.SocketNumber);
            if (problem is not null) {
                InformGameClient(problem);

                return;
            }

            JewelSockets.Socket(item, message.SocketNumber, (uint) jewel.m_templateID);
            WizardItemCollection.SaveItemBehaviors(item);

            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_EQUIPJEWELTOITEM {
                GlobalID = wizard.GameObjectID,
                ItemGID = message.ItemGID,
                SocketNumber = message.SocketNumber,
                JewelGID = message.JewelGID,
            });

            // The jewel is part of the item now.
            wizard.RemoveItemFromInventory(jewel.m_globalID);
            WizardItemCollection.RemoveItem(jewel);
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
                GlobalID = wizard.GameObjectID,
                ItemID = jewel.m_globalID,
            });

            EquipmentExtras.Refresh(wizard);

            // The jewel's effects go to the stats page right away.
            foreach (var effectMessage in EquipmentExtras.TakeCardEffectMessages(wizard)) {
                SendToSocket(effectMessage);
            }

            RefreshPetMaxStats(wizard, item);
            Logger.Information("{0} socketed jewel {1} into item {2}, socket {3}.",
                Logger.Args(wizard.CharId, jewel.m_templateID, item.m_templateID, message.SocketNumber));
        }
        catch (Exception e) {
            Logger.Error("Socketing a jewel failed: {0}", Logger.Args(e));
            InformGameClient("Socketing that jewel ran into a problem. Please send the server log.");
        }
    }

    // A pet jewel's stat talent raises the pet's maximum stats while it is in; the pet window gets the new numbers.
    private void RefreshPetMaxStats(Wizard wizard, WizClientObjectItem item) {
        if (Imlight.CoreLib.Game.Pet.PetProgress.GetBehavior(item) is null) {
            return;
        }

        Imlight.CoreLib.Game.Pet.PetProgress.EnsureInitialized(item);
        Imlight.CoreLib.Game.Pet.PetCare.Finish(wizard, item, null, SendToSocket);
    }

    // [Jewels] KeepShatteredJewels: a jewel taken out goes back to the backpack instead of shattering.
    private static readonly bool KeepShatteredJewels = ConfigurationManager.GetValue("Jewels.KeepShatteredJewels", false);

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_UNSOCKETJEWELREQUEST))]
    private void ReceiveUnsocketJewelRequest(WIZARD2_53_PROTOCOL.MSG_UNSOCKETJEWELREQUEST message) {
        var wizard = GetActiveWizard();
        Logger.Information("Unsocket request: item {0}, socket {1}.", Logger.Args(message.ItemGID, message.SocketNumber));

        try {
            var item = FindOwnedItem(wizard, message.ItemGID);
            if (item is null) {
                InformGameClient("That item is not yours.");

                return;
            }

            // A No Shatter jewel goes back to the backpack; any other jewel shatters on the way out unless
            // [Jewels] KeepShatteredJewels is on (Justin, 2026-10-20).
            var jewelTemplateId = JewelSockets.Unsocket(item, message.SocketNumber);
            var noShatter = JewelSockets.IsNoShatter(CoreObjectFactory.GetCoreTemplate(jewelTemplateId) as WizItemTemplate);
            if (jewelTemplateId == 0) {
                InformGameClient("That socket is empty.");

                return;
            }

            WizardItemCollection.SaveItemBehaviors(item);
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UNEQUIPSOCKETEDJEWEL {
                GlobalID = wizard.GameObjectID,
                ItemGID = message.ItemGID,
                SocketNumber = message.SocketNumber,
                SocketDeleted = 0,
            });

            Logger.Information("Jewel {0} taken out of item {1}, socket {2}: {3}.", Logger.Args(jewelTemplateId, item.m_templateID,
                message.SocketNumber, noShatter ? "No Shatter, back to the backpack" : KeepShatteredJewels ? "kept (KeepShatteredJewels)" : "shattered"));
            if (noShatter || KeepShatteredJewels) {
                if (wizard.AddItemToInventory(jewelTemplateId, out var jewel)) {
                    SendInventoryAdd(wizard, jewel);
                }
                else {
                    InformGameClient("Your backpack is full, so the jewel could not come back.");
                }
            }

            EquipmentExtras.Refresh(wizard);

            // The jewel's effects go to the stats page right away.
            foreach (var effectMessage in EquipmentExtras.TakeCardEffectMessages(wizard)) {
                SendToSocket(effectMessage);
            }

            RefreshPetMaxStats(wizard, item);
        }
        catch (Exception e) {
            Logger.Error("Taking out a jewel failed: {0}", Logger.Args(e));
            InformGameClient("Taking out that jewel ran into a problem. Please send the server log.");
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_UNLOCKSOCKETS))]
    private void ReceiveUnlockSockets(WIZARD2_53_PROTOCOL.MSG_UNLOCKSOCKETS message) {
        var wizard = GetActiveWizard();
        Logger.Information("Unlock sockets: item {0}, cost choice {1}.", Logger.Args(message.ItemGID, message.CostChoice));

        // Sockets are free here.
        var item = FindOwnedItem(wizard, message.ItemGID);
        var state = item is null ? null : JewelSockets.GetState(item, create: true);
        if (state is not null) {
            state.m_publicJewelInfo.m_bSocketsLocked = false;
            WizardItemCollection.SaveItemBehaviors(item);
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UNLOCKSOCKETSCONFIRM {
            Failure = (sbyte) (state is null ? 1 : 0),
            WebFailure = 0,
            Credits = wizard.Account?.Crowns ?? wizard.Crowns,
        });

        if (state is not null) {
            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_JEWELSOCKETLOCKUPDATE {
                GlobalID = wizard.GameObjectID,
                ItemGID = message.ItemGID,
                Locked = 0,
            });
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_JEWELSOCKETUNLOCKCOST))]
    private void ReceiveJewelSocketUnlockCost(WIZARD2_53_PROTOCOL.MSG_JEWELSOCKETUNLOCKCOST message) {
        // Logged so the next log shows the format. Sockets are unlocked for free when asked.
        Logger.Information("Socket unlock cost asked: {0}", Logger.Args(message.Data));
    }

    private static WizClientObjectItem FindOwnedItem(Wizard wizard, ulong globalId)
        => wizard.InventoryBehavior.GetItem(globalId)
            ?? wizard.EquipmentBehavior?.EquippedItems?.FirstOrDefault(i => i.m_globalID == globalId);

    private void SendInventoryAdd(Wizard wizard, WizClientObjectItem item) {
        if (!s_itemSerializer.Serialize(item, (uint) (PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit), out var data)) {
            Logger.Error("Could not serialize item {0} for the backpack.", Logger.Args(item.m_globalID));

            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
            GlobalID = wizard.GameObjectID,
            SerializedItem = data,
        });
    }

    #endregion

    #region Snacks

    [MessageHandler(typeof(PET_9_PROTOCOL.MSG_PETSNACKREMOVEREQUEST))]
    private void ReceivePetSnackRemoveRequest(PET_9_PROTOCOL.MSG_PETSNACKREMOVEREQUEST message) {
        var wizard = GetActiveWizard();

        var hasSnack = wizard.PetSnackBehavior.HasSnackID(message.GlobalID);
        if (!hasSnack) {
            Logger.Log.Debug("Tried to remove snack with global id {0} that does not exist in player snack bag.",
                Logger.Args(message.GlobalID));

            return;
        }

        wizard.RemoveSnack(message.GlobalID, out var updatedSnack);

        if (updatedSnack.m_quantity > 0) {
            SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE() {
                GlobalID = wizard.GameObjectID,
                ItemID = updatedSnack.m_globalID,
                Quantity = updatedSnack.m_quantity
            });

            return;
        }

        SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKREMOVE() {
            GlobalID = wizard.GameObjectID,
            ItemID = updatedSnack.m_globalID,
        });
    }

    #endregion

    #region Reagents

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REAGENTREMOVEREQUEST))]
    private void ReceiveReagentRemoveRequest(WIZARD_12_PROTOCOL.MSG_REAGENTREMOVEREQUEST message) {
        var wizard = GetActiveWizard();

        var hasReagent = wizard.AlchemyBehavior.HasReageant(message.GlobalID);
        if (!hasReagent) {
            Logger.Log.Debug("Tried to remove reagent with global id {0} that does not exist in player reagent bag.",
                Logger.Args(message.GlobalID));

            return;
        }

        var reagent = wizard.AlchemyBehavior.GetReagent(message.GlobalID);
        wizard.RemoveReagent(reagent.m_globalID, out var updatedReagent);

        if (updatedReagent.m_quantity > 0) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE() {
                GlobalID = wizard.GameObjectID,
                ItemID = updatedReagent.m_globalID,
                Quantity = updatedReagent.m_quantity
            });

            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTREMOVE() {
            GlobalID = wizard.GameObjectID,
            ItemID = updatedReagent.m_globalID,
        });
    }

    #endregion

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PLAYERWIZBANG))]
    private void ReceivePlayerWizbang(WIZARD_12_PROTOCOL.MSG_PLAYERWIZBANG message) {
        var wizard = GetActiveWizard();

        switch (message.StateName) {
            case "SpellbookWizbang":
                ZoneBroadcast(new GAME_5_PROTOCOL.MSG_WIZBANG() {
                    GameObjectID = wizard.GameObjectID,
                    WizBangID = StringHash.Compute("Registrar")
                }, false);
                break;
            default:
                ZoneBroadcast(new GAME_5_PROTOCOL.MSG_WIZBANG() {
                    GameObjectID = wizard.GameObjectID,
                    WizBangID = 0
                }, false);
                break;
        }
    }

}
