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
 * INTERACT VENDOR 
 * ========================================================================
 * 
 * PURPOSE:
 * Manages vendor interaction mechanics for NPCs, handling shop inventory 
 * and player shopping interactions in the game world.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * Dynamically resolves vendor inventory from World database.
 * 
 * TODO:
 * - Investigate CSR test shop configuration
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractVendorComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "WizShoppingService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.Shopping;
    public string StateName       => "Shop"; // Forbids the player from moving.
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_ShopOptionEquipment";

    private const string DefaultShopTitle = "KrocNPC_00000013";

    private List<GID> _inventory;
    private string _shopTitle = "Unknown Shop";
    private int _shopType = 0;
    // Which shop window the game opens: 0 the equipment (robe) shop, 2 reagents, 4 jewels, 5 pet snacks.
    private int _furnitureShop = 0;
    private bool _csrTestShop = false;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        // Attach if the template is an NPC and has an inventory in Dragon database,
        // or if the template is a vendor as per game client data.
        => template is GameObjectTemplate goTemplate
        && goTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate)
        && (NpcInventoryCollection.TryGetNpcInventory(goTemplate.m_templateID, out _)
        || WorldVendorLocations.IsVendor(goTemplate.m_templateID));

    public override void OnStart() {
        // The shop's own title and type come from its template, so a vendor with no stock still says what it sells.
        var shoppingBehavior = Entity.Template?.m_behaviors?.OfType<WizShoppingBehaviorTemplate>().FirstOrDefault();
        if (shoppingBehavior != null) {
            if (!string.IsNullOrWhiteSpace(shoppingBehavior.m_shopTitle)) {
                _shopTitle = shoppingBehavior.m_shopTitle;
            }
            _shopType = shoppingBehavior.m_shopType;
            _furnitureShop = shoppingBehavior.m_furnitureShop;
            _csrTestShop = shoppingBehavior.m_CSRTestShop;
        }

        if (!NpcInventoryCollection.TryGetNpcInventory(Entity.ActiveGameObject.m_templateID, out var inventory)) {
            Logger.Error("Failed to get vendor inventory for NPC {0}",
                Logger.Args(Entity.ActiveGameObject.m_templateID));
            _inventory = [];

            return;
        }

        _inventory = inventory.Inventory ?? [];

        if (!string.IsNullOrWhiteSpace(inventory.ShopTitle)) {
            _shopTitle = inventory.ShopTitle;
        }
    }

    // A vendor that sells jewels (Katherine RockHammer) opens the game's jewel shop window: the equipment shop window
    // has no tab for jewels, so they would not show there.
    private bool IsJewelVendor
        => Imlight.CoreLib.Game.Runs.RoguelikeStock.IsJewelVendor(Entity.ActiveGameObject.m_templateID, _shopTitle, _inventory);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => IsJewelVendor
            ? [
                new JewelShopOption {
                    m_displayKey = "GUI_ShopOptionJewel",
                    m_iconKey = NpcIcon,
                    m_serviceName = ServiceName,
                }
            ]
            : [
                new EquipmentShopOption {
                    m_displayKey = DisplayKey,
                    m_iconKey = NpcIcon,
                    m_serviceName = ServiceName,
                }
            ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        SendShopOfferings(playerActor, playerCharacter);
        SendPlayerIntoWizbang(playerObject.m_globalID);
        SendPlayerIntoState(playerObject.m_globalID);
    }

    /// <summary>
    /// True when this vendor offers the item to the wizard. A roguelike run gives each wizard their own stock.
    /// </summary>
    public bool HasItem(GID itemGID, Wizard wizard = null)
        => StockFor(wizard).Any(x => x.MParts.TemplateId == itemGID.MParts.TemplateId);

    private List<GID> StockFor(Wizard wizard)
        => Imlight.CoreLib.Game.Runs.RoguelikeStock.ShopsActiveFor(wizard)
            ? Imlight.CoreLib.Game.Runs.RoguelikeStock.ShopFor(wizard, Entity.ActiveGameObject.m_templateID, _inventory, _shopTitle)
            : _inventory;

    private void SendShopOfferings(IActorRef playerActor, Wizard wizard) {
        var shopOffering = new WizShopOffering() {
            m_sellModifier = 0.05f,
            m_shopTitle = string.IsNullOrWhiteSpace(_shopTitle) || _shopTitle == "Unknown Shop" ? DefaultShopTitle : _shopTitle,
            m_shopList = StockFor(wizard),
            // Changes the type of currency that is used
            // 0 - Gold
            // 1 - PvP tickets
            m_shopType = _shopType,
            // A jewel vendor opens the jewel shop window (4); the robe shop window has no tab for jewels.
            m_furnitureShop = IsJewelVendor ? 4 : _furnitureShop,
            // todo: figure this out for QA
            m_CSRTestShop = _csrTestShop,
        };

        // Serialize the offerings and send them to the player.
        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );
        if (!serializer.Serialize(shopOffering, 4, out var serializedShopList)) {
            Logger.Error("Failed to serialize shop offering for NPC {0}",
                Logger.Args(Entity.ActiveGameObject.m_templateID));

            return;
        }

        var shopListMsg = new WIZARD_12_PROTOCOL.MSG_SHOPLIST() {
            GlobalID = Entity.ActiveGameObject.m_globalID.Full,
            Data = serializedShopList
        };
        playerActor.Tell(shopListMsg);
    }

    private void SendPlayerIntoWizbang(ulong playerObjID) {
        // Create the wiz bang message, and wrap it in a broadcast message.
        var wizBangMsg = new GAME_5_PROTOCOL.MSG_WIZBANG {
            WizBangID = (uint) WizBang,
            GameObjectID = playerObjID
        };
        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = wizBangMsg,
            Selfless = false,
        };

        Entity.ZoneRef.Tell(broadcastMsg);
    }

    private void SendPlayerIntoState(ulong playerObjID) {
        // Create the change state message, and wrap it in a broadcast message.
        var changeStateMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            State = StringHash.Compute(StateName),
            GameObjectID = playerObjID
        };
        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = changeStateMsg,
            Selfless = false,
        };

        Entity.ZoneRef.Tell(broadcastMsg);
    }

}
