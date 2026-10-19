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

    /// <summary>This run's random seed: it picks the creatures on the run's streets.</summary>
    public ulong Seed { get; set; }

    /// <summary>The current tier, starting at 1.</summary>
    public int Tier { get; set; } = 1;

    public RoguelikePhase Phase { get; set; } = RoguelikePhase.Street;

    /// <summary>Seconds spent on the current street. Only counts while the wizard is online.</summary>
    public int StreetSeconds { get; set; }

    /// <summary>The highest tier whose street quests were handed out, so each tier's quests come once.</summary>
    public int QuestsGrantedTier { get; set; }

    /// <summary>The highest tier whose boss quests were handed out, so each tier's come once.</summary>
    public int BossQuestsGrantedTier { get; set; }

    /// <summary>
    /// True after the wizard fled a battle and was sent to the hub; the next zone they arrive in sends them
    /// back to their run. Saved, because a zone change starts a new session.
    /// </summary>
    public bool ReturnPending { get; set; }

    /// <summary>
    /// How many times the run's street creatures were rerolled: every login onto the street rolls new ones.
    /// </summary>
    public int Rerolls { get; set; }

    /// <summary>The stat potions drunk on this run: the total each stat was raised by, by stat name.</summary>
    public Dictionary<string, int> Boosts { get; set; } = [];

    /// <summary>True once the run's free mount was handed out.</summary>
    public bool MountGiven { get; set; }

    /// <summary>Creatures defeated during the whole run.</summary>
    public int Kills { get; set; }

    /// <summary>Bosses defeated during the whole run.</summary>
    public int BossesDefeated { get; set; }

}
