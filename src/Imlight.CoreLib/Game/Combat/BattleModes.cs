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
using System.IO;
using System.Linq;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Battle modes a wizard turns on with a command. Each is kept in a file next to the server, one character id per
/// line, so it stays on after a restart.
/// </summary>
internal sealed class BattleModes(string file) {

    /// <summary>`.harmony on`: every battle starts with all 7 pips as power pips and full shadow pips.</summary>
    internal static readonly BattleModes Harmony = new("harmony.txt");

    /// <summary>`.lightnshadow on`: every round tops power pips, shadow pips and mana back up to full.</summary>
    internal static readonly BattleModes LightAndShadow = new("lightnshadow.txt");

    /// <summary>`.allknowing on`: every battle counts as mastering every school.</summary>
    internal static readonly BattleModes AllKnowing = new("allknowing.txt");

    /// <summary>
    /// Pets (by item id, not character) whose talents were picked with `.pet talent` or `.pet derby`: their shown
    /// talents are left as set instead of being filled up to the pet's age.
    /// </summary>
    internal static readonly BattleModes HandPickedPetTalents = new("pet-talents-by-hand.txt");

    private readonly string _file = file;
    private readonly object _lock = new();
    private HashSet<ulong> _characters;

    internal bool IsOn(ulong characterId) {
        lock (_lock) {
            return Load().Contains(characterId);
        }
    }

    internal void Set(ulong characterId, bool on) {
        lock (_lock) {
            var characters = Load();
            if (on ? !characters.Add(characterId) : !characters.Remove(characterId)) {
                return;
            }

            try {
                File.WriteAllLines(_file, characters.Select(id => id.ToString()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Logger.Warning("Could not save {0}: {1}", Logger.Args(_file, ex.Message));
            }
        }
    }

    private HashSet<ulong> Load() {
        if (_characters is not null) {
            return _characters;
        }

        _characters = [];
        try {
            if (File.Exists(_file)) {
                foreach (var line in File.ReadAllLines(_file)) {
                    if (ulong.TryParse(line.Trim(), out var id)) {
                        _characters.Add(id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Could not read {0}: {1}", Logger.Args(_file, ex.Message));
        }

        return _characters;
    }

}
