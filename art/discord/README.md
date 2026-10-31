# Discord Rich Presence art

Upload these in the Discord Developer Portal (application 1566185912138989618) under **Rich Presence → Art Assets**,
with exactly these names (the file name without `.png`):

| Asset | Shown as |
|---|---|
| `logo` | the large image: the spiral and sword of the launcher icon |
| `arc1` | the small image during arc 1 (Wizard City to Dragonspyre), embers |
| `arc2` | the small image during arc 2 (Celestia to Morganthe), frost |
| `arc3` | the small image during arc 3 (Polaris to Empyrea), violet |

All are 1024 × 1024. `make.js` draws them again (`node make.js` next to `Cinzel-Bold.ttf`, with Playwright).
