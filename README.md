# Rogue Chess V2

A small S&box C# turn-based tactics game about positioning, army composition, Scrap control, and temporary card effects.

## Files

- `Code/RogueChess/RogueChessTypes.cs` contains teams, units, cards, positions, and card metadata.
- `Code/RogueChess/RogueChessGameComponent.cs` owns board state, army choices, turns, resources, card play, movement, attacks, Hacker disables, win checks, and the simple AI.
- `Code/RogueChess/RogueChessPanel.razor` renders the Army Builder, clickable board, hands, status, and control buttons.
- `Code/RogueChess/RogueChessPanel.razor.scss` provides the tactical UI styling.
- `Version 2 docs/` contains the Rogue Chess V2 design documents.

## Add It To A Scene

1. Open this folder as a S&box addon/project.
2. Open `Assets/scenes/minimal.scene`.
3. Press play.

If your scene already has a UI root, you can disable `UseEmbeddedPanel`, add `RogueChessPanel` manually, and assign its `Game` parameter to the `RogueChessGameComponent`.

## Version 2 Rules

- Each player starts with exactly 8 units: 1 Commander plus 7 freely chosen units.
- The selectable non-Commander units are Buddy, Shooter, Tank, and Hacker.
- There are no army composition restrictions beyond the required Commander.
- A turn allows one unit action, one optional card, then End Turn.
- Unit actions are move, attack, or a special ability.
- The Hacker may disable one adjacent enemy instead of attacking.
- A disabled unit cannot act during its next turn, may still be attacked, and then recovers.
- Destroying the enemy Commander wins the game.
- Cards remain temporary and do not create permanent upgrades.

## Test Vs AI Mode

1. The first screen is the `Rogue Chess V2` Army Builder.
2. Select a unit card from the Unit Pool, then click an empty army slot.
3. Fill all 7 open slots beside the locked Commander.
4. Click `Start Game` to enter the match in the default `PVE` mode.
5. Blue acts first and Red is controlled by the computer using a default legal V2 army.
6. Click a friendly unit, then click a highlighted move tile or highlighted enemy target.
7. Select a ready Hacker and click `Disable` to highlight adjacent enemies that can be disabled.
8. Click a card in the current player's hand, then click a highlighted card target.
9. Use `End Turn`; Red will run a legal simple AI turn.
10. Kill the enemy Commander to win.

## Rules Notes

- The board is an 8x8 symmetrical grid.
- Scrap Tiles are the center board squares. A unit standing on one at the start of its team's turn gives +1 extra Scrap.
- Buddy moves farther than the other units, so it is strong for reaching Scrap Tiles.
- Shooter attacks up to 3 tiles in a straight orthogonal line. Other units block the shot.
- Tank has high health and low mobility, making it strong for holding territory.
- Hacker is fragile but can deny one adjacent enemy action on that enemy's next turn.

## Known Limitations

- The AI is intentionally simple and deterministic.
- Card draw uses a fixed repeating order instead of shuffle/randomness.
- There is no networking, save/load, custom Tank/Hacker bitmap art, or matchmaking.
