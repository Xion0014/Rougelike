# Achievements

`achievements.json` lists what there is to earn, in the order the launcher shows it. Each entry:

| Field | |
|---|---|
| `Id` | Never change it once released: earned achievements are stored by it. |
| `Name`, `Description` | Shown in game when earned and on the launcher's Achievements page. |
| `Hidden` | `true`: the launcher shows "???" until someone earns it. |
| `When` | What earns it (below), with `Tier`, `Amount` and `Counter`. |

**When**

- `TierCleared`: beat tier `Tier`'s boss (`Tier` 0 or left out: any tier).
- `TierClearedNoDeath`: beat a tier's boss (`Tier`, or any) without dying since the previous tier's boss.
- `TierClearedDieDelete`: beat tier `Tier`'s boss with Die = Delete on.
- `BossInRounds`: beat a tier's boss (`Tier`, or any) in `Amount` rounds or fewer.
- `Counter`: a count reaches `Amount`. `Counter` is one of `BattlesWon`, `Kills`, `BossesBeaten`, `Deaths`, `Flees`,
  `JewelsFound`, `PetsFound`, `SpellsLearned`.
- `Level`: reach level `Amount`.
- `Gold`: hold `Amount` gold at once.
- `PetAge`: find a pet in battle of age `Amount` or older (1 Baby, 2 Teen, 3 Adult, 4 Ancient, 5 Epic, 6 Mega, 7 Ultra).

Progress is kept per wizard in `Server/Achievements/progress.json` (counts and the date each one was earned), which the
launcher reads. `.run achievements` lists a wizard's in game. `[Roguelike] Achievements = false` turns them off.
