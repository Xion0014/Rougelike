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
 * CROWNSHOP SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Handles the functionality of the crown shop
 * 
 * 
 * USAGE EXAMPLE:
 *
 * 
 * NOTE:
 * 
 * 
 * TODO:
 * - Populating with correct data
 * - Removing from wishlist (& Wishlist privacy)
 * - Daily Spiral quests
 * 
 * Created by: Phill030
 * Version: KALI 1.0
 * Last Updated: 15.10.2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.CrownShop;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Packs;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Misc;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class CrownShopService(SessionActor sessionActor) : MessageService(sessionActor) {

    private static readonly Random s_random = new();
    private static readonly CoreObjectSerializer s_coreObjectSerializer = new(behaviors: SerializerFlags.None);

    private static ulong _randomUlong() {
        Span<byte> bytes = stackalloc byte[8];
        s_random.NextBytes(bytes);
        return BitConverter.ToUInt64(bytes);
    }

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new CrownShopService(parentActor));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_SEGDATA_REQUEST))]
    private void ReceiveCrownShopSegDataRequest(WIZARD_12_PROTOCOL.MSG_PCS_SEGDATA_REQUEST message) {
        var wizard = GetActiveWizard();

        // On a roguelike run the Shop button shows the wizard's stats instead: crowns are not used on a run, and
        // the game's own stats page leaves the run's stat potions out. Not answering keeps the shop closed.
        if (Imlight.CoreLib.Game.Runs.RoguelikeConfig.ShopButtonShowsStats
            && Imlight.CoreLib.Game.Runs.RoguelikeBoosts.OnRun(wizard)) {
            Logger.Information("Crown shop opened by {0} on a run; showing their stats instead.", Logger.Args(wizard.CharId));
            InformGameClient(Imlight.CoreLib.Game.Runs.RoguelikeBoosts.DescribeStats(wizard), isImportant: true);

            // Turn the shop down, so the game does not wait on an empty shop window.
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_SEGDATA_RESPONSE { Success = 0 });

            return;
        }
        var schoolStr = wizard.MagicSchoolBehavior.MagicSchool.ToString();
        var schoolCode = schoolStr.Length >= 2 ? schoolStr[..2] : schoolStr;

        var segmentationInputData = new SegmentationInputData() {
            m_bIsValidSegmentationData = true,
            m_playerLevel = wizard.MagicSchoolBehavior.Level,
            m_playerSchoolOfFocus = schoolCode,
            m_accountNDaysAged = (int) (DateTime.Now - wizard.Account.CreationTime).TotalDays,
            m_accountNDaysSinceLastLogin = 0,
            m_accountNDaysSinceLastPurchase = 0,
            m_accountNDaysLastCrownsPurchase = 0,
            m_accountIsMember = 0,
            m_accountIsCSR = wizard.Account.AuthLevel > AuthLevel.None ? 1 : 0, // todo: Change this back before prod!
            m_accountNCrownsSpent = 0,
            m_accountNCrownsInWallet = wizard.Account.Crowns,
            m_accountNDaysSinceItemPurchased = [],
            m_numOfParticularItemInInventory = [],
            m_numItemsOfCategoryInInventory = [],
            m_playerHasBadge = [], // Used for calculating Requirements
            m_accountNHighestWorld = 0
        };

        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );
        var propertyFlags = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!serializer.Serialize(segmentationInputData, propertyFlags, out var serializedData)) {
            Logger.Error("Failed to serialize SegmentationInputData");
            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_SEGDATA_RESPONSE {
            Success = 1,
            Data = serializedData
        });

        // We need to call this to "sync" the CrownShop Crown-Balance, else it just says 0
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_CROWNBALANCE {
            Failure = 0,
            TotalCrowns = wizard.Account.Crowns,
            CharacterID = wizard.CharId,
            CacheBalanceForCSSegmentation = 1
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_LIST_REQUEST))]
    private void ReceiveCrownShopListRequest(WIZARD_12_PROTOCOL.MSG_PCS_LIST_REQUEST message) {
        uint nextUpdateId = message.UpdateID == 0 ? 1 : message.UpdateID + 1;
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_LIST_RESPONSE {
            Data = CrownShopHandler.GetCrownShopData(),
            Updates = "",
            UpdateID = nextUpdateId,
            Error = 0,
        });
    }

    // Is this saleID still valid and actively running?
    // Does this saleID grant the price the client is asking to lock
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_PRICE_LOCK_REQUEST))]
    private void ReceivePriceLockReq(WIZARD_12_PROTOCOL.MSG_PCS_PRICE_LOCK_REQUEST message) {
        Logger.Information("Received MSG_PCS_PRICE_LOCK_REQUEST for item {0}", Logger.Args(message.Item));

        if (message.SaleID != 0) {
            Logger.Warning("SaleID {0} is not supported. Ignoring.", Logger.Args(message.SaleID));
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PRICE_LOCK_RESPONSE {
                Error = 1,
            });
            return;
        }

        if (!CrownShopHandler.TryGetCrownShopItem(message.Item, out var item)) {
            Logger.Warning("Item {0} not found in CrownShop.", Logger.Args(message.Item));
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PRICE_LOCK_RESPONSE {
                Error = 1,
            });
            return;
        }

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PRICE_LOCK_RESPONSE {
            CostCrowns = item.m_crownsCost,
            CostGold = item.m_goldCost,
            CostTickets = 0,
            Error = 0,
            Item = message.Item
        });
    }

    // The client requests to buy [N amount] of this item (and possibly gift it to another player),
    // which will be removed(?) from the list of things they can buy (if enabled).
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST))]
    private void ReceivePurchaseRequest(WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST message) {
        var wizard = GetActiveWizard();

        if (message.Count < 1 || message.Count > CrownShopHandler.s_maxBuyCount || message.SaleID != 0) {
            Logger.Warning("Rejected purchase request for item {0}.", Logger.Args(message.Item));

            SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
                Error = 1,
            });
            return;
        }

        // Authoritative cost lookup from catalog
        if (!CrownShopHandler.TryGetCrownShopItem(message.Item, out var catalogItem)) {
            Logger.Warning("Item {0} not found in CrownShop.", Logger.Args(message.Item));
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
                Error = 1,
            });
            return;
        }

        int amountToPay;
        if (message.Type == 0) {
            // User is buying with gold
            amountToPay = message.Count * catalogItem.m_goldCost;
            if (wizard.GameStats.m_currentGold < amountToPay) {
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
                    Error = 1,
                });
                return;
            }

            wizard.RemoveGold(amountToPay);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                Gold = wizard.GameStats.m_currentGold,
                MaxGold = wizard.GameStats.m_baseGoldPouch
            });
        }
        else if (message.Type == 1) {
            // User is buying with crowns
            amountToPay = message.Count * catalogItem.m_crownsCost;
            if (wizard.Account.Crowns < amountToPay) {
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
                    Error = 1,
                });
                return;
            }

            wizard.Account.SetCrowns(wizard.Account.Crowns - amountToPay);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
                Item = message.Item,
                Error = 0,
                Cost = amountToPay,
                Count = message.Count,
                Gifted = (byte) (message.Recipient == 0 ? 0 : 1),
                Type = message.Type
            });

            // Sync crowns
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_CROWNBALANCE {
                Failure = 0,
                TotalCrowns = wizard.Account.Crowns,
                CharacterID = wizard.CharId,
                CacheBalanceForCSSegmentation = 1
            });
        }
        else {
            throw new InvalidOperationException($"Invalid currency provided: {message.Type}");
        }

        var itemTemplate = CoreObjectFactory.GetCoreTemplate(message.Item);
        if (message.Recipient == 0) {
            switch (itemTemplate) {
                case BoosterPackTemplate:
                    for (uint i = 0; i < message.Count; i++) {
                        PackManager.OpenPack(SessionActor.ActorRef, wizard, message.Item);
                    }
                    return;
                case GoldAmountTemplate goldItem:
                    wizard.AddGold(goldItem.m_goldAmount);
                    SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                        Gold = wizard.GameStats.m_currentGold,
                        MaxGold = wizard.GameStats.m_baseGoldPouch
                    });
                    return;
                case LunariAmountTemplate lunariTemplate:
                    wizard.AddLunari(lunariTemplate.m_lunariAmount);
                    SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATEEVENTCURRENCY1 {
                        EventCurrency1 = wizard.GameStats.m_currentEventCurrency1,
                        MaxEventCurrency1 = wizard.GameStats.m_baseEventCurrency1Pouch
                    });
                    return;
                case ReagentItemTemplate reagentTemplate:
                    if (!wizard.AddReagent(reagentTemplate.m_templateID, out ClientReagentItem reagentObj)) {
                        Logger.Warning("Could not add reagent to inventory.");

                        SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
                            Error = 1,
                        });
                        return;
                    }

                    if (!s_coreObjectSerializer.Serialize(reagentObj, 27, out var serializedReagentObj)) {
                        Logger.Warning("Failed to serialize core object.");

                        return;
                    }

                    SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTADD {
                        GlobalID = wizard.GameObjectID,
                        Data = serializedReagentObj
                    });

                    SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
                        ItemGlobalID = reagentObj.m_globalID,
                        ItemTemplateID = (uint) reagentObj.m_templateID,
                        ItemLocation = 1
                    });

                    SendToSocket(new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE {
                        GlobalID = wizard.GameObjectID,
                        ItemID = reagentObj.m_globalID,
                        Quantity = reagentObj.m_quantity
                    });
                    return;
                case PetSnackItemTemplate:
                    for (uint i = 0; i < message.Count; i++) {
                        if (!wizard.AddSnack(message.Item, out ClientPetSnackItem snackObj)) {
                            Logger.Warning("Could not add snack to the snack bag.");

                            SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
                                Error = 1,
                            });
                            return;
                        }

                        if (!s_coreObjectSerializer.Serialize(snackObj, 24, out var serializedSnack)) {
                            Logger.Warning("Failed to serialize core object.");
                            return;
                        }

                        SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKADD {
                            GlobalID = wizard.GameObjectID,
                            Data = serializedSnack
                        });

                        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
                            ItemGlobalID = snackObj.m_globalID,
                            ItemTemplateID = (uint) snackObj.m_templateID,
                            ItemLocation = 1
                        });

                        SendToSocket(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE {
                            GlobalID = wizard.GameObjectID,
                            ItemID = snackObj.m_globalID,
                            Quantity = snackObj.m_quantity
                        });
                    }
                    return;
                case TourneyTokensAmountTemplate tourneyToken:
                    // TODO
                    return;
                case WizItemTemplate wizItemTemplate:
                    // Check if the item is an Elixir.
                    var elixirBehavior = wizItemTemplate.m_behaviors?.OfType<ElixirBehaviorTemplate>().FirstOrDefault();
                    if (elixirBehavior is not null && message.PurchaseElixirEquipNow > 0) {
                        if (TryEquipElixir(wizard, message.Item, wizItemTemplate, elixirBehavior)) {
                            return;
                        }
                        // If it couldn't be equipped (already 3 active, duplicate type, etc.), fall back to inventory.
                    }

                    var levelUpElixir = wizItemTemplate.m_behaviors?.OfType<LevelUpElixirBehaviorTemplate>().FirstOrDefault();
                    if (levelUpElixir is not null && message.PurchaseElixirEquipNow > 0) {
                        return;
                    }

                    var worldElixir = wizItemTemplate.m_behaviors?.OfType<WorldElixirBehaviorTemplate>().FirstOrDefault();
                    if (worldElixir is not null && message.PurchaseElixirEquipNow > 0) {
                        return;
                    }

                    // Check if the item is emote or teleport effect
                    var customEmote = itemTemplate.m_behaviors?.OfType<CustomEmoteBehaviorTemplate>().FirstOrDefault();
                    if (customEmote is not null && customEmote.m_bitFieldNumber >= 0) {
                        if (customEmote.m_emoteType == CustomEmoteType.CE_Teleport) {
                            wizard.UnlockCustomTeleportEffect(customEmote.m_bitFieldNumber);
                        }
                        else {
                            wizard.UnlockCustomEmote(customEmote.m_bitFieldNumber);
                        }
                    }

                    // Pets need a hatched pet object with talents and stats, which AddItemToInventory does not build.
                    if (PetFactory.IsPetTemplate((uint) message.Item)) {
                        GivePets(wizard, message);
                        return;
                    }

                    // todo: serialize the item only once
                    for (uint i = 0; i < message.Count; i++) {
                        if (!wizard.AddItemToInventory(message.Item, out WizClientObjectItem itemCoreObject)) {
                            Logger.Warning("Could not add item to inventory.");

                            SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
                                Error = 1,
                            });
                            return;
                        }

                        if (!s_coreObjectSerializer.Serialize(itemCoreObject, 24, out var serializedItem)) {
                            Logger.Warning("Failed to serialize core object.");
                            return;
                        }

                        SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
                            GlobalID = wizard.GameObjectID,
                            SerializedItem = serializedItem
                        });

                        // This adds the item to 'REVIEW MY ORDER'
                        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
                            ItemGlobalID = itemCoreObject.m_globalID,
                            ItemLocation = 1,
                            ItemTemplateID = (uint) message.Item
                        });
                    }
                    return;
                default:
                    Logger.Warning($"Unhandled TemplateID: {message.Item}");
                    break;
            }
        }
        else {
            var targetCharacter = WizardCollection.GetCharacterUnloaded(message.Recipient);
            if (targetCharacter is null) {
                Logger.Error("Recipient with CharacterId {0} was not found.", Logger.Args(message.Recipient));
                return;
            }

            ulong recipientAccountId = targetCharacter.AccountId;
            var targetAccount = AccountCollection.GetAccount(recipientAccountId);
            if (targetAccount is null) {
                Logger.Error("Account {0} for character was not found.", Logger.Args(recipientAccountId));
                return;
            }

            var giftRedemption = new CrownShopGiftRedemption {
                m_giverAccountID = wizard.AccountId,
                m_itemCount = message.Count,
                m_itemId = message.Item,
                m_itemTransactionId = Guid.NewGuid().ToString("N"),
            };

            var giftSerializer = new ObjectSerializer(Versionable: true, Behaviors: SerializerFlags.None);
            if (!giftSerializer.Serialize(giftRedemption, 31, out var serializedItem)) {
                Logger.Error("Failed to serialize CrownShopGiftRedemption.");
                return;
            }

            var mail = new Mail {
                m_recipientId = targetCharacter.AccountId,
                m_senderId = (GID) wizard.CharId,
                m_messageData = System.Text.Encoding.Latin1.GetString((byte[]) serializedItem),
                m_expireDuration = 0,
                m_messageType = 2,
                m_timeStamp = (ulong) DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                m_mailId = _randomUlong(),
            };

            targetAccount.AddMail(mail);

            // Notify the player, if they're online
            if (TryGetOnlinePlayer(message.Recipient, out OnlinePlayer onlinePlayer)) {
                Context.ActorSelection(onlinePlayer.ActorPath).Tell(new SERVICE_101_PROTOCOL.MSG_DELIVER_GIFT {
                    Mail = mail
                });
            }
        }
    }

    private void GivePets(Wizard wizard, WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_REQUEST message) {
        var templateId = (uint) message.Item;
        for (uint i = 0; i < Math.Max(message.Count, 1u); i++) {
            var pet = PetFactory.CreateHatchedPet(wizard.CharId, templateId);
            if (pet is not null && message.PetName != 0 && WizardNameBank.IsValidPetName(message.PetName)) {
                PetFactory.TrySetPetName(pet, message.PetName);
            }

            if (pet is null || !wizard.AddPetToInventory(pet)) {
                Logger.Warning("Could not add crown shop pet {0} to the inventory of {1}.",
                    Logger.Args(templateId, wizard.CharId));

                SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
                    Error = 1,
                });
                return;
            }

            if (!ItemSync.SendAdded(SessionActor.ActorRef, wizard, pet)) {
                wizard.RemoveItemFromInventory(pet.m_globalID);
                WizardItemCollection.RemoveItem(wizard.CharId, pet.m_globalID);

                SendToSocket(new WIZARD_12_PROTOCOL.MSG_PCS_PURCHASE_RESPONSE {
                    Error = 1,
                });
                return;
            }

            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
                ItemGlobalID = pet.m_globalID,
                ItemLocation = 1,
                ItemTemplateID = templateId
            });
        }
    }

    private bool TryEquipElixir(Wizard wizard,
                                ulong templateId,
                                WizItemTemplate itemTemplate,
                                ElixirBehaviorTemplate elixirTemplate) {
        uint durationSeconds = 0;
        if (!string.IsNullOrEmpty(elixirTemplate.m_expireTime)) {
            _ = uint.TryParse((string) elixirTemplate.m_expireTime, out durationSeconds);
        }

        // Case 1: Instant refill (duration == 0).
        if (durationSeconds == 0) {
            if (elixirTemplate.m_equipActionList?.m_results?.Count > 0) {
                ResultDispatcher.ExecuteResults(
                    Context,
                    elixirTemplate.m_equipActionList,
                    SessionActor.ActorRef,
                    wizard.GetInitializedGameObject());
            }

            PlaySound(elixirTemplate.m_equipSound);
            return true;
        }

        // Case 2: Timed elixir (duration > 0).
        var equipBehavior = wizard.EquipmentBehavior;
        if (!equipBehavior.CanEquipElixir(elixirTemplate)) {
            return false; // Can't equip right now; caller falls back to inventory.
        }

        var elixirItem = (WizClientObjectItem) CoreObjectFactory.FinalizeCoreObject(templateId);
        elixirItem.m_characterId = (GID) wizard.CharId;
        CoreObjectFactory.InitializeCoreObjectBehaviors(elixirItem, templateId);

        if (CoreObjectFactory.FindBehaviorInstance<ClientElixirBehavior>(elixirItem, out var clientElixir)) {
            clientElixir.m_expireTime = durationSeconds;
            clientElixir.m_statsApplied = true;
        }

        if (!equipBehavior.EquipElixir(elixirItem, durationSeconds)) {
            return false;
        }

        var addedEffects = CharacterEffectHelper.AddEffectsToWizard(wizard, itemTemplate);

        WizardItemCollection.AddItem(elixirItem);
        WizardCollection.UpdateCharacterItems(wizard);

        if (s_coreObjectSerializer.Serialize(elixirItem, 24, out var serializedItem)) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
                GlobalID = wizard.GameObjectID,
                SerializedItem = serializedItem
            });
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = elixirItem.m_globalID,
            ItemLocation = 1,
            ItemTemplateID = (uint) templateId
        });

        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM {
            ItemID = elixirItem.m_globalID,
            SlotName = "Elixir",
            IsEquip = 1
        });

        SendAddEffects(wizard, addedEffects);

        // Start HUD countdown timer and enable HUD icon.
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER {
            GlobalID = elixirItem.m_globalID,
            TimerTime = durationSeconds
        });

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE {
            parentID = elixirItem.m_globalID,
            EffectEnabled = 1
        });

        PlaySound(elixirTemplate.m_equipSound);

        // Update health and mana display with new maximums.
        var clientStats = wizard.GameStats.GetClientTypeAlternative();
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

        return true;
    }

    private void SendAddEffects(Wizard wizard, List<GameEffectBase> effects) {
        if (effects is null || effects.Count == 0) {
            return;
        }

        foreach (var effect in effects) {
            if (!s_coreObjectSerializer.Serialize(effect, PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var effectData)) {
                continue;
            }

            SendToSocket(new GAME_5_PROTOCOL.MSG_ADDEFFECT {
                GameObjectID = wizard.GameObjectID,
                EffectData = effectData
            });
        }
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_UPDATEUSERWISHLIST))]
    private void ReceiveWishlistUpdate(WIZARD_12_PROTOCOL.MSG_PCS_UPDATEUSERWISHLIST message) {

    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_CrownShopLogging))]
    private void ReceiveCrownShopLogging(WIZARD2_53_PROTOCOL.MSG_CrownShopLogging message) {
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_CrownShopLogging {
            Enabled = 0
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_PCS_PATCH))]
    private void ReceivePCsPatch(WIZARD_12_PROTOCOL.MSG_PCS_PATCH message) {
        Logger.Warning("What is MSG_PCS_PATCH for? This is called when someone opens player gifts");
    }

}
