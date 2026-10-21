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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// Pet stat maximums raised with `.pet stats` (Justin, 2026-10-19: Strength 220 to 260). Kept in pet-max-stats.txt
/// next to the server, one "pet id, stat, maximum" per line, so they stay after a restart. A raised maximum is a
/// floor: talents that give more still give more.
/// </summary>
internal static class PetMaxRaises {

    private static readonly PetStatFile s_file = new("pet-max-stats.txt");

    /// <summary>
    /// The raised maximum of a pet's stat, or 0 if it was not raised.
    /// </summary>
    internal static int Get(ulong petId, string stat) => s_file.Get(petId, stat);

    internal static void Set(ulong petId, string stat, int maximum) => s_file.Set(petId, stat, maximum);

}

/// <summary>
/// How much 0.10.17 added to each of a pet's stats for its selfish talents, so it can be taken back: selfish talents
/// raise only the maximum (Justin, 2026-10-20). Kept in pet-selfish-stats.txt next to the server.
/// </summary>
internal static class PetSelfishStats {

    private static readonly PetStatFile s_file = new("pet-selfish-stats.txt");

    internal static int Get(ulong petId, string stat) => s_file.Get(petId, stat);

    internal static void Set(ulong petId, string stat, int added) => s_file.Set(petId, stat, added);

}

/// <summary>
/// A number per pet and stat, saved as "pet id, stat, number" lines in a file next to the server.
/// </summary>
internal sealed class PetStatFile(string file) {

    private readonly object _lock = new();
    private Dictionary<(ulong Pet, string Stat), int> _values;

    internal int Get(ulong petId, string stat) {
        lock (_lock) {
            return Load().TryGetValue((petId, Key(stat)), out var value) ? value : 0;
        }
    }

    internal void Set(ulong petId, string stat, int value) {
        lock (_lock) {
            if (Load().TryGetValue((petId, Key(stat)), out var old) && old == value) {
                return;
            }

            _values[(petId, Key(stat))] = value;
            try {
                File.WriteAllLines(file, _values.Select(entry => $"{entry.Key.Pet} {entry.Key.Stat} {entry.Value}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Logger.Warning("Could not save {0}: {1}", Logger.Args(file, ex.Message));
            }
        }
    }

    private static string Key(string stat) => (stat ?? "").Trim().ToLowerInvariant();

    private Dictionary<(ulong, string), int> Load() {
        if (_values is not null) {
            return _values;
        }

        _values = [];
        try {
            if (File.Exists(file)) {
                foreach (var line in File.ReadAllLines(file)) {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 3 && ulong.TryParse(parts[0], out var pet) && int.TryParse(parts[2], out var value)) {
                        _values[(pet, Key(parts[1]))] = value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Could not read {0}: {1}", Logger.Args(file, ex.Message));
        }

        return _values;
    }

}
