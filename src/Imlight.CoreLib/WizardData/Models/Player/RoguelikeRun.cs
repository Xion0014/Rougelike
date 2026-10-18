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

namespace Imlight.CoreLib.WizardData.Models.Player;

/// <summary>
/// Where a wizard is in a roguelike run.
/// </summary>
public enum RoguelikePhase {

    /// <summary>Fighting on the tier's street while the timer runs.</summary>
    Street,

    /// <summary>The timer ran out; the tier's boss must be beaten.</summary>
    Boss,

    /// <summary>The boss is beaten; the wizard is shopping before the next tier.</summary>
    Shop,

    /// <summary>Every tier is cleared.</summary>
    Complete,

}

/// <summary>
/// A wizard's roguelike run, saved with the character. See the [Roguelike] section of Imlight.ini.
/// </summary>
[Serializable]
public class RoguelikeRun {

    /// <summary>The current tier, starting at 1.</summary>
    public int Tier { get; set; } = 1;

    public RoguelikePhase Phase { get; set; } = RoguelikePhase.Street;

    /// <summary>Seconds spent on the current street. Only counts while the wizard is online.</summary>
    public int StreetSeconds { get; set; }

    /// <summary>The highest tier whose street quests were handed out, so each tier's quests come once.</summary>
    public int QuestsGrantedTier { get; set; }

    /// <summary>Creatures defeated during the whole run.</summary>
    public int Kills { get; set; }

    /// <summary>Bosses defeated during the whole run.</summary>
    public int BossesDefeated { get; set; }

}
