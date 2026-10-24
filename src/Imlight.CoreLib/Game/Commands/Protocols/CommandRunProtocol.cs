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

using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Roguelike run commands, for every player. The work is done by the player's RoguelikeService.
///   .run status        Tier, time left and kills.
///   .run next          Leave the shops and start the next tier.
///   .run train         After a boss, go to the spell trainers (Ravenwood).
///   .run return        Go back to the run's current street, boss or shop zone.
///   .run here          Show the current zone's full name.
///   .run music [off|on] Another random track, or run music off/on for you.
///   .run jewel [shape] Get a random jewel or pin, of a shape if named (staff only).
///   .run pet [age]     Get a run pet, random or of an age (staff only).
///   .run pwn [giant]   Learn the Pwn developer spell (staff only, temporary, for testing).
///   .run goto &lt;tier&gt; [street|boss|shop]  Jump to a tier's phase, for testing (staff only).
///   .run zones &lt;text&gt;  List zone names containing the text (staff only), for filling in [Roguelike].
/// </summary>
internal class CommandRunProtocol : CommandProtocol {

    internal override string Group { get; set; } = "run";

    [Command("status")]
    [Alias("s")]
    private void StatusCommand() => Send("status");

    [Command("next")]
    private void NextCommand() => Send("next");

    [Command("train")]
    private void TrainCommand() => Send("train");

    [Command("return")]
    private void ReturnCommand() => Send("return");

    [Command("stats")]
    private void StatsCommand() => Send("stats");

    [Command("here")]
    private void HereCommand() => Send("here");

    [Command("timerui")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void TimerUiCommand([Remainder] string layout) => Send("timerui", layout);

    [Command("music")]
    private void MusicCommand([Remainder] string onOrOff = "") => Send("music", onOrOff.Trim().ToLowerInvariant() switch {
        "off" => "off",
        "on" => "on",
        _ => "",
    });

    [Command("jewel")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void JewelCommand([Remainder] string shape = "") => Send("jewel", shape);

    [Command("pet")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void PetCommand([Remainder] string age = "") => Send("pet", age);

    // TEMPORARY test command: teaches the Pwn developer spell. Remove once testing is done.
    [Command("pwn")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void PwnCommand([Remainder] string which = "") => Send("pwn", which);

    [Command("goto")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void GotoCommand([Remainder] string where) => Send("goto", where);

    [Command("zones")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void ZonesCommand([Remainder] string filter) => Send("zones", filter);

    private void Send(string action, string argument = "")
        => Context.SessionActor.Tell(new SERVICE_101_PROTOCOL.MSG_ROGUELIKE_COMMAND {
            Action = action,
            Argument = argument,
        }, null);

}
