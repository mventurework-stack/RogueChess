# Rogue Chess V2 -- Front Page Army Builder

## Purpose

The Army Builder is the first screen shown when Rogue Chess V2 launches.

Its purpose is to let the Blue player choose the seven non-Commander units that join the locked Commander in the starting army.

## Visual Layout

The screen uses a dark sci-fi tactical interface with blue neon accents.

The layout is divided into three areas:

- Left panel: Rogue Chess title, Build Your Army subtitle, short instruction text, Start Game, Settings, and How To Play.
- Center panel: Your Army header, eight army slots, and the Unit Pool.
- Right panel: selected unit details and army rules.

## Slot Rules

The army has exactly eight slots.

- Slot 1 is always the Commander.
- The Commander slot is filled automatically.
- The Commander slot is locked.
- Slots 2 through 8 begin empty.
- Slots 2 through 8 accept Buddy, Shooter, Tank, or Hacker.

## Commander Lock Rule

The Commander cannot be removed or replaced.

Every legal army contains exactly one Commander.

## Unit Pool

The Unit Pool contains:

- Buddy
- Shooter
- Tank
- Hacker

The player selects a unit card, then clicks an empty army slot to place that unit.

There are no composition restrictions. Any mix of the four unit types is legal, including seven copies of the same unit type.

## Start Game Validation

The Start Game button is disabled until all eight army slots are filled.

The army is complete when it contains:

- 1 locked Commander
- 7 chosen non-Commander units

## Match Integration

When Start Game is pressed, the selected Blue army is passed into the match setup.

The match then spawns:

- Blue Commander plus the seven selected Blue units.
- Red Commander plus a default legal V2 army.

The Army Builder does not change the core turn system, card rules, Scrap system, or Commander destruction victory condition.
