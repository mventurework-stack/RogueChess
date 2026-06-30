Scrap Chess - Network Foundation Patch

Replace these files in the same project locations:

- ScrapChessGameComponent.cs
- ScrapChessTypes.cs
- ScrapChessPanel.razor
- ScrapChessPanel.razor.scss

What this patch does:

- Keeps PvE as the default local mode.
- Keeps local PvP and PVC available.
- Adds/fixes online player roles and session state foundation.
- Keeps Online Blue, Online Red, Watch, and Leave Online controls available.
- Adds online action gates so only the current online role can act.
- Fixes compile-breaking issues in the previous generated file.

After replacing files, stop Play Mode, wait for compile, then press Play again.
