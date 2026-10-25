/*
 * Pet May Cast talents, ported from Niduus's fork (Niduus, 2026-10-20), set in [PetCasting] of Imlight.ini.
 *
 * A talent such as Guardian Wall ("Maycast [Tower Shield]") casts the game's pet copy of its card ("Pet - Tower
 * Shield"), for free and on the pet's owner. Each May Cast can go off when the
 * owner or a teammate casts a spell, takes damage, is healed or passes, each with its own chance: the key named after
 * the card ("TowerShield = 10/25/5/10"), else OnCast, OnDamage, OnHeal and OnPass.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Combat;

internal static class PetMayCasts {

    /// <summary>A May Cast talent and the card it casts.</summary>
    internal sealed record MayCast(string Talent, string Key, string SpellName, uint SpellTemplateId);

    internal static readonly bool Enabled = ConfigurationManager.GetValue("PetCasting.Enabled", true);
    internal static readonly int MaxPerRound = Math.Max(0, ConfigurationManager.GetValue("PetCasting.MaxPerRound", 5));
    // [PetCasting] ShowAsPetCast: the cast is flagged as the pet's, so the game plays the pet's May Cast animation. The card
    // is put in the owner's hand first (the game skips a card it cannot find there).
    internal static readonly bool ShowAsPetCast = ConfigurationManager.GetValue("PetCasting.ShowAsPetCast", true);
    internal static readonly bool ForceSpell = ConfigurationManager.GetValue("PetCasting.ForceSpellTest", false);
    internal static readonly bool Chain = ConfigurationManager.GetValue("PetCasting.Chain", true);
    // The most seconds the next round waits for each May Cast's animation. The cinematic timings ran ~1 s long per
    // pet cast (Niduus's log, 2026-10-20: 16 casts, the round started 15 s after he saw them end).
    // 0 (default): each cast's own animation time, less that second, so long ones (Meteor Storm, Virulence) play out.
    internal static readonly float CastSeconds = Math.Max(0f, ConfigurationManager.GetValue("PetCasting.CastSeconds", 0f));

    /// <summary>How long the round waits for a May Cast whose animation takes <paramref name="spellSeconds"/>.</summary>
    internal static float WaitFor(float spellSeconds)
        => CastSeconds > 0 ? Math.Min(spellSeconds, CastSeconds) : Math.Max(Math.Min(spellSeconds, 2f), spellSeconds - 1f);
    internal static readonly int MaxChain = Math.Max(0, ConfigurationManager.GetValue("PetCasting.MaxChain", 10));
    internal static readonly bool AsItemCard = ConfigurationManager.GetValue("PetCasting.AsItemCard", false);
    // The game takes a card's pips from its template rank plus m_regularAdjust (m_pipCost is never sent), so the pet's
    // copy goes out with the rank taken off (Niduus, 2026-10-20: pets drained his pips and kept casting at 0).
    internal static readonly bool ZeroCost = ConfigurationManager.GetValue("PetCasting.ZeroCost", true);
    private static readonly int s_onCast = ConfigurationManager.GetValue("PetCasting.OnCast", 30);
    private static readonly int s_onDamage = ConfigurationManager.GetValue("PetCasting.OnDamage", 30);
    private static readonly int s_onHeal = ConfigurationManager.GetValue("PetCasting.OnHeal", 30);
    private static readonly int s_onPass = ConfigurationManager.GetValue("PetCasting.OnPass", 0);

    // Talent name to its May Cast; null for a talent that is not one or whose card was not found.
    private static readonly ConcurrentDictionary<string, MayCast> s_byTalent = new(StringComparer.OrdinalIgnoreCase);

    // English card name to spell template names, built the first time a talent's card is not found by its own name.
    private static readonly Lazy<Dictionary<string, List<string>>> s_byShownName = new(BuildShownNames);

    private static readonly Regex s_bracket = new(@"\[(.+?)\]", RegexOptions.Compiled);

    /// <summary>The May Casts of the pet a wizard wears, its jewel's talents included.</summary>
    internal static List<MayCast> ForWizard(Wizard wizard) {
        var pet = wizard?.EquipmentBehavior?.EquippedItems?.FirstOrDefault(item => PetProgress.GetBehavior(item) is not null);
        if (pet is null) {
            return [];
        }

        var behavior = PetProgress.GetBehavior(pet);
        var talents = (behavior.m_expressedTalents ?? []).Select(PetData.GetTalent)
            .Concat(JewelSockets.GetPetJewelTalents(pet).Select(PetData.GetTalent))
            .OfType<PetTalentTemplate>()
            .Where(talent => talent.m_effectList?.Any(effect => effect is ProvideCombatTriggerInfo) == true);

        return [.. talents.Select(talent => s_byTalent.GetOrAdd((string) talent.m_talentName, _ => Resolve(talent)))
            .Where(mayCast => mayCast is not null)];
    }

    /// <summary>True for a May Cast talent whose card the server can cast (a run pet may roll it).</summary>
    internal static bool IsCastable(PetTalentTemplate talent)
        => talent?.m_effectList?.Any(effect => effect is ProvideCombatTriggerInfo) == true
           && s_byTalent.GetOrAdd((string) talent.m_talentName, _ => Resolve(talent)) is not null;

    /// <summary>The chances in percent for a May Cast: when a spell is cast, damage is taken, healing is had, and a turn is passed.</summary>
    internal static (int Cast, int Damage, int Heal, int Pass) Chances(MayCast mayCast) {
        var text = ConfigurationManager.GetSetting($"PetCasting.{mayCast.Key}")?.Trim();
        if (string.IsNullOrEmpty(text)) {
            return (s_onCast, s_onDamage, s_onHeal, s_onPass);
        }

        var parts = text.Split('/', StringSplitOptions.TrimEntries);
        int Part(int index, int fallback)
            => int.TryParse(parts[Math.Min(index, parts.Length - 1)].TrimEnd('%'), out var value) ? value : fallback;

        return (Part(0, s_onCast), Part(1, s_onDamage), Part(2, s_onHeal), Part(3, s_onPass));
    }

    internal static bool Roll(int percent)
        => percent > 0 && (percent >= 100 || Random.Shared.Next(100) < percent);

    private static MayCast Resolve(PetTalentTemplate talent) {
        try {
            string talentName = talent.m_talentName;
            string descriptionKey = talent.m_description;
            var description = string.IsNullOrEmpty(descriptionKey) ? "" : Locale.GetEnglishName(descriptionKey);
            var match = s_bracket.Match(description ?? "");
            string displayKey = talent.m_displayName;
            var shownName = (string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey)).Trim().TrimEnd('.');
            var cardName = match.Success ? match.Groups[1].Value.Trim() : shownName;
            if (cardName.Length == 0) {
                cardName = talentName;
            }

            var spellName = FindCard(cardName, talent);
            var spell = spellName is null ? null : SpellFactory.GetSpell(spellName);
            if (spell is null) {
                Logger.Warning("Pet May Cast {0}: no card found for {1}.", Logger.Args(talentName, cardName));

                return null;
            }

            var mayCast = new MayCast(talentName, Key(cardName), spellName, spell.m_templateID);
            // Read once per talent when the server first needs it (not a cast): which card the talent would cast.
            Logger.Debug("Pet May Cast talent {0} would cast {1} ({2}) when it goes off; its chances: PetCasting.{3}.",
                Logger.Args(talentName, spellName, spell.m_templateID, mayCast.Key));

            return mayCast;
        }
        catch (Exception e) {
            Logger.Error("Pet May Cast {0} could not be read: {1}", Logger.Args((string) talent.m_talentName, e));

            return null;
        }
    }

    /// <summary>
    /// The card for a May Cast: the game's pet copy of it ("Pet - Tower Shield", from PetTalentsTriggeredSpells.xml;
    /// Niduus, 2026-10-20), else the card named in the talent's description, else that pet spell without its
    /// "Pet - " name, else a card shown with the description's name.
    /// </summary>
    private static string FindCard(string cardName, PetTalentTemplate talent) {
        var triggers = talent.m_effectList.OfType<ProvideCombatTriggerInfo>()
            .Select(info => (string) info.m_triggerName)
            .Append((string) talent.m_talentName)
            .SelectMany(Imlight.CoreLib.Game.Pet.PetTalentsTriggeredSpells.GetSpellNames)
            .ToList();
        if (triggers.FirstOrDefault(SpellFactory.Exists) is { } petCopy) {
            return petCopy;
        }

        if (SpellFactory.Exists(cardName)) {
            return cardName;
        }

        foreach (var petSpell in triggers) {
            var plain = Unpet(petSpell);
            if (plain != petSpell && !Unusual(plain) && SpellFactory.Exists(plain)) {
                return plain;
            }
        }

        if (s_byShownName.Value.TryGetValue(cardName, out var shown)) {
            var best = shown.Where(name => !Unusual(name))
                .OrderBy(name => name.Contains(" - ") ? (name.EndsWith(" - Amulet", StringComparison.OrdinalIgnoreCase) ? 1 : 2) : 0)
                .ThenBy(name => name.Length)
                .FirstOrDefault();
            if (best is not null) {
                return best;
            }
        }

        return triggers.FirstOrDefault(SpellFactory.Exists);
    }

    private static string Unpet(string name) {
        foreach (var prefix in new[] { "Pet - ", "PetPower-" }) {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                name = name[prefix.Length..];
            }
        }

        foreach (var suffix in new[] { " - Trigger", " - Pet", " - Amulet" }) {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                name = name[..^suffix.Length];
            }
        }

        return name.StartsWith("1P ", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;
    }

    // Copies of a card made for pets, monsters, treasure cards, items and tests.
    private static bool Unusual(string name) {
        var lower = name.ToLowerInvariant();

        return lower.StartsWith("pet") || lower.Contains("mob") || lower.Contains(" tc") || lower.Contains("test")
            || lower.Contains("item");
    }

    private static Dictionary<string, List<string>> BuildShownNames() {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, template) in SpellFactory.AllSpellTemplates()) {
            string key = template.m_displayName;
            var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);
            if (string.IsNullOrWhiteSpace(shown)) {
                continue;
            }

            if (!result.TryGetValue(shown.Trim(), out var names)) {
                result[shown.Trim()] = names = [];
            }

            names.Add(template.m_name);
        }

        return result;
    }

    /// <summary>The setting name for a card: its words joined, letters and digits only ("Tower Shield" is TowerShield).</summary>
    internal static string Key(string cardName)
        => Regex.Replace(string.Concat(cardName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..])), "[^A-Za-z0-9]", "");

}
