# Boss cheats

One JSON file per creature template (`<templateId>.json`). The files and their format are Niduus's
(github.com/Niduus/Imlight); the engine that runs them is `src/Imlight.CoreLib/Game/Combat/CombatResolver.BossCheats.cs`.
`BossCheats.cs` next to it documents every field.

A cheat is **When** (what sets it off), **If** (conditions), **Do** (`Actions`, in order):

```jsonc
{
  "TemplateID": 123456,
  "Name": "An example boss",
  "Cheats": [
    { "When": "PlayerCasts", "Category": "Heal", "Chance": 50,
      "Actions": [
        { "Do": "Say", "Line": "WizardBossFights_00000048" },
        { "Do": "Cast", "Spell": "Mana Burn", "Target": "Caster" }
      ],
      "Notes": "A wizard heals: half the time, a line and a Mana Burn on them." }
  ]
}
```

Works now (Spiral Gauntlet stage 1):

- **When**: BattleStart, RoundStart, RoundEnd, FromRound, Pips, HealthBelow, PlayerCasts (and PlayerDamages,
  PlayerHeals, PlayerCharms, PlayerWards, PlayerAuras, PlayerGlobals, PlayerShadows, PlayerMinions), PlayerPasses, BossHit.
- **If**: Round, Every, Phase, Once, Chance, MaxTimes, Cooldown, FromRound, ToRound, PlayersMin/Max, HasAllies,
  NoAllies, AlliesUp/Down/Min/Max, HealthAbove/Under, Mark/MarkWho/NotMarked, HasWho/HasKind/HasCount/HasNot,
  OnBoss, SingleTarget, MultiTarget, NotOnCaster, School, Kind, Category, NotCategory, CostMin/Max, CasterSchool,
  Spells (+ AnyVersion).
- **Do**: Cast, Polymorph, CastBy, Say, Mark, Unmark, PickOne, InTurn, SetHealth, HealthTo, Heal, Pips, SetPhase,
  Wait, Defeat, RemoveCharms, RemoveWards.
- **Target**: Self, Caster, Receiver, Enemy, FirstEnemy, AllEnemies, WeakestEnemy, MostPips, Ally, AllAllies,
  WeakestAlly, StrongestAlly, Last, Marked, or empty (as the spell aims).

A cheat using anything else is skipped whole and named once in the server log ("not supported yet").
On a run, cheats work from `[Roguelike] BossCheatsFromTier` (6) on; `BossCheats = false` turns them off.
`//` comments are allowed. Each cheat that goes off is logged ("... cheats after ...").
