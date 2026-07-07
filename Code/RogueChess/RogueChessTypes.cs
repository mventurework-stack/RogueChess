using System;
using System.Collections.Generic;

namespace StrategyGame;

public enum RogueChessTeam
{
	Blue,
	Red
}

public enum RogueChessMode
{
	PlayerVsComputer,
	PlayerVsPlayer,
	ComputerVsComputer
}

public enum RogueChessOnlineRole
{
	Offline,
	BluePlayer,
	RedPlayer,
	Spectator
}

public enum UnitType
{
	Commander,
	Buddy,
	Shooter,
	Tank,
	Hacker
}

// Which sliding directions a unit may move along.
public enum MoveDirs
{
	Orthogonal,
	Diagonal,
	All
}

public enum CardType
{
	Guard,
	Push,
	Focus,
	Sprint,
	BuildBuddy,
	Repair,
	Reboot
}

public readonly record struct GridPos( int X, int Y )
{
	public static readonly GridPos[] CardinalDirections =
	{
		new( 1, 0 ),
		new( -1, 0 ),
		new( 0, 1 ),
		new( 0, -1 )
	};

	public static readonly GridPos[] DiagonalDirections =
	{
		new( 1, 1 ),
		new( 1, -1 ),
		new( -1, 1 ),
		new( -1, -1 )
	};

	public static readonly GridPos[] AllDirections =
	{
		new( 1, 0 ),
		new( -1, 0 ),
		new( 0, 1 ),
		new( 0, -1 ),
		new( 1, 1 ),
		new( 1, -1 ),
		new( -1, 1 ),
		new( -1, -1 )
	};

	public bool IsInsideBoard => X >= 0 && X < RogueChessGameComponent.BoardSize && Y >= 0 && Y < RogueChessGameComponent.BoardSize;

	public int ManhattanDistance( GridPos other )
	{
		return Math.Abs( X - other.X ) + Math.Abs( Y - other.Y );
	}

	// Chebyshev (chessboard) distance: max(|dx|,|dy|). Used for omnidirectional attack range so a
	// diagonal tile at distance N counts as N, not 2N (which Manhattan would give).
	public int ChebyshevDistance( GridPos other )
	{
		return Math.Max( Math.Abs( X - other.X ), Math.Abs( Y - other.Y ) );
	}

	public GridPos Offset( GridPos direction )
	{
		return new GridPos( X + direction.X, Y + direction.Y );
	}
}

public sealed class UnitData
{
	public int Id { get; }
	public RogueChessTeam Team { get; }
	public UnitType Type { get; }
	public GridPos Position { get; set; }
	public int Health { get; set; }
	public int MaxHealth { get; }
	public int MoveRange { get; }        // sliding move distance
	public int AttackRange { get; }      // maximum attack distance; 0 = cannot attack
	public MoveDirs MoveDirs { get; }    // allowed sliding directions
	public int Damage { get; }
	public int Shield { get; set; }
	public int FocusDamageBonus { get; set; }
	public int SprintMoveBonus { get; set; }
	public int DisabledTurns { get; set; }
	public bool IsDisabledThisTurn { get; set; }
	public int? LastDisabledByUnitId { get; set; }
	public int LastDisabledOnTurn { get; set; } = -1;
	public bool CanActThisTurn { get; set; } = true;

	public UnitData( int id, RogueChessTeam team, UnitType type, GridPos position )
	{
		Id = id;
		Team = team;
		Type = type;
		Position = position;

		// Heroes ruleset: directional sliding movement + per-unit attack geometry. Flat 1 damage.
		switch ( type )
		{
			case UnitType.Commander:
				MaxHealth = 3;
				MoveRange = 2;
				MoveDirs = MoveDirs.All;
				AttackRange = 3;
				Damage = 1;
				break;
			case UnitType.Buddy:
				MaxHealth = 2;
				MoveRange = 2;
				MoveDirs = MoveDirs.All;
				AttackRange = 1;
				Damage = 1;
				break;
			case UnitType.Shooter:
				MaxHealth = 2;
				MoveRange = 3; // Option A: diagonal slide up to 3. Option B (bent path) handled in movement gen.
				MoveDirs = MoveDirs.Diagonal;
				AttackRange = 3;
				Damage = 1;
				break;
			case UnitType.Tank:
				MaxHealth = 3;
				MoveRange = 2;
				MoveDirs = MoveDirs.Orthogonal;
				AttackRange = 2;
				Damage = 1;
				break;
			case UnitType.Hacker:
				MaxHealth = 1;
				MoveRange = 6;
				MoveDirs = MoveDirs.All;
				AttackRange = 0;
				Damage = 1;
				break;
		}

		Health = MaxHealth;
	}

	public GridPos[] MoveDirectionSet => MoveDirs switch
	{
		MoveDirs.Orthogonal => GridPos.CardinalDirections,
		MoveDirs.Diagonal => GridPos.DiagonalDirections,
		_ => GridPos.AllDirections
	};

	public int CurrentMoveRange => MoveRange + SprintMoveBonus;
	public int CurrentDamage => Damage + FocusDamageBonus;
	public string ShortName => Type switch
	{
		UnitType.Commander => "CMD",
		UnitType.Buddy => "BUD",
		UnitType.Shooter => "SHT",
		UnitType.Tank => "TNK",
		UnitType.Hacker => "HCK",
		_ => "???"
	};
}

public readonly record struct CardData( CardType Type, string Name, int Cost, string RulesText )
{
	public static readonly IReadOnlyDictionary<CardType, CardData> All = new Dictionary<CardType, CardData>
	{
		[CardType.Guard] = new( CardType.Guard, "Guard", 1, "Choose one friendly unit. It gains +1 Shield until your next turn. Shield blocks damage before Health." ),
		[CardType.Push] = new( CardType.Push, "Push", 1, "Choose one ready friendly unit, then choose an adjacent enemy. Push that enemy 1 tile directly away if the space is empty." ),
		[CardType.Focus] = new( CardType.Focus, "Focus", 2, "Choose one friendly unit. Its next attack this turn deals +1 extra damage. Does not protect the unit." ),
		[CardType.Sprint] = new( CardType.Sprint, "Sprint", 2, "Choose one friendly unit before it moves. It gets +1 move range this turn only. It still cannot attack after moving." ),
		[CardType.BuildBuddy] = new( CardType.BuildBuddy, "Build Buddy", 3, "Choose an empty tile next to your Commander. Summon a Buddy there. The new Buddy can act next turn." ),
		[CardType.Repair] = new( CardType.Repair, "Repair", 2, "Choose one damaged friendly unit. Restore 1 Health, up to its maximum Health. Does not add Shield." ),
		[CardType.Reboot] = new( CardType.Reboot, "Reboot", 1, "Choose one disabled friendly unit. Immediately clear its disabled state so it can move and attack normally this turn." )
	};

	public static readonly IReadOnlyDictionary<CardType, CardVisualData> Visuals = new Dictionary<CardType, CardVisualData>
	{
		[CardType.Guard] = new( "Defense", "guard-art", "SHD", "Tiny shield, excellent timing.", "COMMON" ),
		[CardType.Push] = new( "Tactic", "push-art", ">>", "Personal space is a battle strategy.", "COMMON" ),
		[CardType.Focus] = new( "Attack", "focus-art", "FOC", "One good hit, carefully planned.", "RARE" ),
		[CardType.Sprint] = new( "Movement", "sprint-art", "RUN", "Little legs. Big plans.", "COMMON" ),
		[CardType.BuildBuddy] = new( "Summon", "build-art", "BUD", "Backup has entered the board.", "RARE" ),
		[CardType.Repair] = new( "Support", "repair-art", "FIX", "Duct tape, courage, and one more turn.", "COMMON" ),
		[CardType.Reboot] = new( "Support", "reboot-art", "RBT", "Wakes a frozen friend right back up.", "COMMON" )
	};
}

public readonly record struct CardVisualData( string TypeLabel, string ArtClass, string ArtGlyph, string FlavorText, string RarityText );
