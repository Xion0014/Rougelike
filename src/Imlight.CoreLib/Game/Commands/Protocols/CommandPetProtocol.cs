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
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Pet commands for the pet you are wearing: `.pet info`, `.pet feed`, `.pet xp`, `.pet level`, `.pet talent`,
/// `.pet derby`, `.pet stats`.
/// </summary>
internal class CommandPetProtocol : CommandProtocol {

    internal override string Group { get; set; } = "pet";

    [Command("info")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void InfoCommand() {
        if (WornPet() is { } pet) {
            PetProgress.EnsureInitialized(pet);
            InformSenderClient(PetProgress.Describe(pet));
        }
    }

    [Command("feed")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void FeedCommand([Remainder] string snack) => Feed(snack, all: false);

    [Command("feedall")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void FeedAllCommand([Remainder] string snack) => Feed(snack, all: true);

    [Command("xp")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void XpCommand(string amount) {
        if (!uint.TryParse(amount, out var xp) || xp == 0) {
            InformSenderClient("Type an amount of pet XP, such as .pet xp 500.");

            return;
        }

        if (WornPet() is not { } pet) {
            return;
        }

        var growth = PetProgress.AddXp(pet, xp);
        PetCare.Finish(Context.Character, pet, growth, Send);
        InformSenderClient(PetCare.Summary(pet, growth));
    }

    [Command("level")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LevelCommand(string level) {
        var target = PetProgress.ParseLevel(level);
        if (target is null) {
            InformSenderClient("Type a level from 1 to 7 or its name: Baby, Teen, Adult, Ancient, Epic, Mega or Ultra.");

            return;
        }

        if (WornPet() is not { } pet) {
            return;
        }

        var growth = PetProgress.SetLevel(pet, target.Value);
        PetCare.Finish(Context.Character, pet, growth, Send);
        InformSenderClient($"Your pet is now {PetProgress.LevelName(target.Value)}."
            + (growth.NewTalents.Count + growth.NewDerbyTalents.Count > 0
                ? $" New talents: {string.Join(", ", growth.NewTalents.Concat(growth.NewDerbyTalents).Select(PetProgress.TalentLabel))}."
                : "")
            + " Relog to see it in the pet window.");
    }

    [Command("talent")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void TalentCommand([Remainder] string name) {
        if (TryPoolCommand(name, derby: false)) {
            return;
        }

        var talent = FindTalent(name);
        if (talent is null || WornPet() is not { } pet) {
            return;
        }

        if (!PetProgress.AddTalent(pet, talent)) {
            InformSenderClient("Your pet already has that talent.");

            return;
        }

        PetCare.Finish(Context.Character, pet, null, Send);
        InformSenderClient($"Your pet now has {PetProgress.TalentLabel(talent)}. Relog to see it in the pet window.");
    }

    // `.pet derby get|claim|unclaim`, the same as `.pet talent` for the derby talents (Justin, 2026-10-19).
    [Command("derby")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void DerbyCommand([Remainder] string text) {
        if (!TryPoolCommand(text, derby: true)) {
            InformSenderClient("Type .pet derby get <slot 0-9> <talent>, .pet derby claim <slot> or .pet derby unclaim <slot>.");
        }
    }

    // `.pet stats <Strength|Intellect|Agility|Will|Power|Happiness> <amount|max>`.
    [Command("stats")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void StatsCommand([Remainder] string text) {
        var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 2) {
            InformSenderClient("Type .pet stats <Strength, Intellect, Agility, Will, Power or Happiness> <amount or max>.");

            return;
        }

        if (WornPet() is not { } pet) {
            return;
        }

        var problem = PetProgress.SetStat(pet, words[0], words[1], out var shownAs);
        if (problem is not null) {
            InformSenderClient(problem);

            return;
        }

        PetCare.Finish(Context.Character, pet, null, Send);
        InformSenderClient($"Your pet's {shownAs}.");
    }

    /// <summary>
    /// `get <slot> <talent>` puts a talent into a slot (0 at the top of the pet window, 9 at the bottom) without
    /// showing it; `claim <slot>` shows it, at most five; `unclaim <slot>` hides it again.
    /// </summary>
    /// <returns>False if the text is not one of these, so the caller can treat it another way.</returns>
    private bool TryPoolCommand(string text, bool derby) {
        var words = (text ?? "").Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var action = words.Length > 0 ? words[0].ToLowerInvariant() : "";
        if (action is not ("get" or "claim" or "unclaim")) {
            return false;
        }

        var command = derby ? ".pet derby" : ".pet talent";
        if (words.Length < 2 || !int.TryParse(words[1], out var slot) || slot < 0 || slot > 9) {
            InformSenderClient(action == "get"
                ? $"Type {command} get <slot 0-9> <talent name>. Slot 0 is the top of the pet window, 9 the bottom."
                : $"Type {command} {action} <slot 0-9>.");

            return true;
        }

        if (WornPet() is not { } pet) {
            return true;
        }

        string problem;
        PetTalentBaseTemplate talent;
        if (action == "get") {
            if (words.Length < 3) {
                InformSenderClient($"Type {command} get {slot} <talent name>.");

                return true;
            }

            talent = FindTalent(words[2], derby);
            if (talent is null) {
                return true;
            }

            problem = PetProgress.PutInPool(pet, derby, slot, talent);
        }
        else {
            problem = PetProgress.Claim(pet, derby, slot, action == "claim", out talent);
        }

        if (problem is not null) {
            InformSenderClient(problem);

            return true;
        }

        // From now on the pet's shown talents stay as picked instead of filling up with its age.
        Imlight.CoreLib.Game.Combat.BattleModes.HandPickedPetTalents.Set(pet.m_globalID.Full, true);
        PetCare.Finish(Context.Character, pet, null, Send);
        InformSenderClient(action switch {
            "get" => $"Slot {slot} now holds {PetProgress.TalentLabel(talent)}, not claimed yet. Claim it with {command} claim {slot}.",
            "claim" => $"Claimed {PetProgress.TalentLabel(talent)} in slot {slot}.",
            _ => $"Unclaimed {PetProgress.TalentLabel(talent)} in slot {slot}.",
        });

        return true;
    }

    [Command("untalent")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void UntalentCommand([Remainder] string name) {
        var talent = FindTalent(name);
        if (talent is null || WornPet() is not { } pet) {
            return;
        }

        if (!PetProgress.RemoveTalent(pet, talent)) {
            InformSenderClient("Your pet does not have that talent.");

            return;
        }

        PetCare.Finish(Context.Character, pet, null, Send);
        InformSenderClient($"Took {PetProgress.TalentLabel(talent)} off your pet. Relog to see it in the pet window.");
    }

    [Command("maxstats")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void MaxStatsCommand() {
        if (WornPet() is not { } pet) {
            return;
        }

        PetProgress.EnsureInitialized(pet);
        PetProgress.MaxStats(pet);
        PetCare.Finish(Context.Character, pet, null, Send);
        InformSenderClient("Your pet's stats are at their maximum. Relog to see it in the pet window.");
    }

    private void Feed(string snackText, bool all) {
        if (WornPet() is not { } pet) {
            return;
        }

        var wizard = Context.Character;
        var snacks = wizard.PetSnackBehavior.Snacks ?? [];
        var text = snackText?.Trim() ?? "";
        var snack = ulong.TryParse(text, out var id)
            ? snacks.FirstOrDefault(s => s.m_templateID == id || s.m_globalID == id)
            : snacks.FirstOrDefault(s => SnackName(s).Equals(text, StringComparison.OrdinalIgnoreCase))
                ?? snacks.FirstOrDefault(s => SnackName(s).Contains(text, StringComparison.OrdinalIgnoreCase));
        if (snack is null) {
            InformSenderClient(snacks.Count == 0
                ? "Your snack bag is empty. Add snacks with .mod addsnack <id>."
                : $"No snack like that in your bag. You have: {string.Join(", ", snacks.Select(s => $"{SnackName(s)} x{s.m_quantity}"))}.");

            return;
        }

        var problem = PetCare.Feed(wizard, pet, snack.m_templateID, all ? snack.m_quantity : 1, Send, out var growth);
        InformSenderClient(problem ?? PetCare.Summary(pet, growth));
    }

    private WizClientObjectItem WornPet() {
        var pet = PetCare.GetWornPet(Context.Character);
        if (PetProgress.GetBehavior(pet) is null) {
            InformSenderClient("Wear a pet first.");

            return null;
        }

        return pet;
    }

    private PetTalentBaseTemplate FindTalent(string name, bool? derby = null) {
        var text = name?.Trim() ?? "";
        var talents = PetData.AllTalents.Where(t => derby is null || (t is PetDerbyTalentTemplate) == derby).ToList();
        var talent = talents.FirstOrDefault(t => string.Equals(t.m_talentName, text, StringComparison.OrdinalIgnoreCase))
            ?? talents.FirstOrDefault(t => string.Equals(Locale.GetEnglishName(t.m_displayName), text, StringComparison.OrdinalIgnoreCase));
        if (talent is null) {
            var close = talents
                .Where(t => ((string) t.m_talentName).Contains(text, StringComparison.OrdinalIgnoreCase)
                    || Locale.GetEnglishName(t.m_displayName).Contains(text, StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .Select(PetProgress.TalentLabel)
                .ToList();
            InformSenderClient(close.Count > 0
                ? $"Which talent? {string.Join(", ", close)}."
                : "No talent with that name. Try a name like Pain-Giver or Talent-Damage-All02.");
        }

        return talent;
    }

    private static string SnackName(ClientPetSnackItem snack) {
        var template = CoreObjectFactory.GetCoreTemplate(snack.m_templateID) as WizItemTemplate;
        var shown = template is null ? "" : Locale.GetEnglishName(template.m_displayName);

        return string.IsNullOrEmpty(shown) ? (template?.m_objectName ?? snack.m_templateID.ToString()) : shown;
    }

    private void Send(IMessage message) => Context.SessionActor.Tell(message, ActorRefs.NoSender);

}
