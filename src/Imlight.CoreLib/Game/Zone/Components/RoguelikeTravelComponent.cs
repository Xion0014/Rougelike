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
 * Buttons for moving around a roguelike run between tiers, in place of the .run commands.
 *
 * The Rate My Stitch tome in the Shopping District ([Roguelike] TravelBooks) offers, after a boss, a trip to the
 * spell trainers in Ravenwood and the start of the next street. Mr. Lincoln, who teaches nothing on a run, offers
 * the way back to the Shopping District. Each choice shows like a world gate's destination, named after the zone
 * it leads to, and does what .run train, .run next and .run return do.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Runs;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class RoguelikeTravelComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "RoguelikeTravel";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => null;

    private bool IsBook => Entity.Template is GameObjectTemplate template
                           && RoguelikeConfig.TravelBooks.Contains(template.m_templateID);

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => RoguelikeConfig.Enabled
           && template is GameObjectTemplate goTemplate
           && (RoguelikeConfig.TravelBooks.Contains(goTemplate.m_templateID)
               || InteractTrainerComponent.TestTrainerKindOf(goTemplate) == InteractTrainerComponent.TestTrainerKind.Enchantments);

    // What each offered option does, in order, for this wizard.
    private List<string> ActionsFor(Wizard wizard) {
        if (wizard?.Roguelike is not { Phase: RoguelikePhase.Shop }) {
            return [];
        }

        return IsBook ? ["train", "next"] : ["return"];
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard wizard)
        => ActionsFor(wizard)
            .Select(action => (ServiceOptionBase) new ZoneGateOption {
                m_displayKey = DestinationKey(wizard, action),
                m_serviceName = ServiceName,
            })
            .ToList();

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var actions = ActionsFor(playerCharacter);
        if (serviceOptionIndex >= actions.Count) {
            return;
        }

        playerActor.Tell(new SERVICE_101_PROTOCOL.MSG_ROGUELIKE_COMMAND { Action = actions[(int) serviceOptionIndex] });
    }

    /// <summary>The name shown for an option: the name of the zone it leads to.</summary>
    private static string DestinationKey(Wizard wizard, string action) {
        var zonePath = action switch {
            "train" => RoguelikeConfig.ResolveZone(RoguelikeConfig.TrainZone),
            "next" => RoguelikeConfig.ResolveZone(RoguelikeConfig.GetTier(wizard.Roguelike.Tier + 1)?.Street),
            _ => RoguelikeConfig.ResolveZone(RoguelikeConfig.ShopZone),
        };

        return string.IsNullOrEmpty(zonePath)
            ? action
            : ZoneLoader.DisplayNameKey(zonePath) is { Length: > 0 } key ? key : zonePath;
    }

}
