# Scrap Chess Buddies

A small S&box C# prototype for a 2D turn-based strategy card game.

## Files

- `Code/ScrapChess/ScrapChessTypes.cs` contains teams, units, cards, positions, and card metadata.
- `Code/ScrapChess/ScrapChessGameComponent.cs` owns board state, turns, resources, card play, movement, attacks, win checks, and the simple Red AI.
- `Code/ScrapChess/ScrapChessPanel.razor` renders the clickable board, hands, status, and control buttons.
- `Code/ScrapChess/ScrapChessPanel.razor.scss` provides the simple colored prototype styling.
- `.sbproj` marks the folder as a S&box addon with code under `Code`.

## Add It To A Scene

1. Open this folder as a S&box addon/project.
2. Open `Assets/scenes/minimal.scene`.
3. Press play.

If your scene already has a UI root, you can disable `UseEmbeddedPanel`, add `ScrapChessPanel` manually, and assign its `Game` parameter to the `ScrapChessGameComponent`.

## Test Vs AI Mode

1. Start the match in the default `Vs AI` mode.
2. Blue acts first and Red is controlled by the computer.
3. Click a friendly unit, then click a highlighted move tile or highlighted enemy target.
4. Click a card in the current player's hand, then click a highlighted card target.
5. Use `End Turn`; Red will immediately run a legal simple AI turn.
6. Kill the enemy Commander to win.

## Test Hot-Seat Mode

1. Click `Switch Hotseat`.
2. Blue and Red are both controlled by local players.
3. Take turns moving or attacking with one unit, optionally playing one card, then ending the turn.

## Rules Notes

- A unit may move or attack on its turn, not both.
- Scrap Tiles are the center board squares. A unit standing on one at the start of its team's turn gives +1 extra Scrap.
- Buddy moves farther than the other units, so it is strong for reaching Scrap Tiles.
- Shooter attacks up to 3 tiles in a straight orthogonal line. Other units block the shot.
- Build Buddy creates a Buddy beside your Commander, but that new Buddy acts next turn.

## Known Limitations

- The AI is intentionally simple and deterministic.
- Card draw uses a fixed repeating order instead of shuffle/randomness.
- There is no networking, save/load, animation, custom art, or matchmaking.
