/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// A gold wisp gives gold to the first wizard who walks into it, more for higher levels.
/// </summary>
internal sealed class WispGoldComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IComponentFactory {

    private const float INTERACTION_RADIUS = 100.0f;
    private bool _collected;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && goTemplate.m_objectName.ToString().Contains("Wisp")
        && goTemplate.m_objectName.ToString().Contains("Gold");

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (_collected || playerWizard is null || !IsInRadius(playerObj, INTERACTION_RADIUS)) {
            return;
        }

        _collected = true;
        var level = Math.Max(1, playerWizard.MagicSchoolBehavior.Level);
        playerWizard.AddGold(Runs.RoguelikeConfig.ScaleGold(playerWizard, Random.Shared.Next(level * 2, level * 5 + 1) + 10));
        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = playerWizard.GameStats.m_currentGold,
            MaxGold = playerWizard.GameStats.m_baseGoldPouch
        });

        Entity.DeleteObject("WispDespawn", playerWizard.GameObjectID);
    }

}
