using Sandbox;
using Sandbox.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrategyGame;

public enum AiDifficulty
{
	Beginner,
	Intermediate,
	Hard
}

/// <summary>
/// Main controller for the Scrap Chess Buddies prototype.
/// Scene setup: create an empty GameObject, attach this component, and leave
/// UseEmbeddedPanel enabled to spawn the UI through a ScreenPanel automatically.
/// </summary>
[Title( "Rogue Chess Game" ), Category( "Prototype" ), Icon( "grid_on" )]
public sealed class RogueChessGameComponent : Component
{
	public const int BoardSize = 8;
	public const int ArmySlotCount = 8;
	public const int CommanderArmySlotIndex = 3;
	const int SelectableArmySlotCount = ArmySlotCount - 1;
	const int HandLimit = 5;
	const int StalemateTurnLimit = 30;
	const float HitEffectDuration = 1.0f;
	const float DeathEffectDuration = 1.0f;
	const float UnitSoundVolume = 0.45f;
	const float BackgroundSoundVolume = 0.45f;
	const string UnitHitSoundPath = "sounds/roguechess/hit.sound";
	const string UnitDeathSoundPath = "sounds/roguechess/death.sound";
	const string UnitMoveSoundPath = "sounds/roguechess/movement.sound";
	const string BackgroundSoundPath = "sounds/roguechess/background.sound";

	static readonly GridPos[] ResourceTiles =
	{
		new( 2, 2 ),
		new( 5, 2 ),
		new( 2, 5 ),
		new( 5, 5 )
	};

	static readonly CardType[] DeckOrder =
	{
		CardType.Guard,
		CardType.Push,
		CardType.Focus,
		CardType.Sprint,
		CardType.BuildBuddy,
		CardType.Repair
	};

	static readonly UnitType[] SelectableArmyTypes =
	{
		UnitType.Buddy,
		UnitType.Shooter,
		UnitType.Tank,
		UnitType.Hacker
	};

	static readonly UnitType[] DefaultArmyChoices =
	{
		UnitType.Buddy,
		UnitType.Shooter,
		UnitType.Tank,
		UnitType.Commander,
		UnitType.Hacker,
		UnitType.Buddy,
		UnitType.Shooter,
		UnitType.Tank
	};

	static readonly GridPos[] BlueArmyPositions =
	{
		new( 0, 7 ),
		new( 1, 7 ),
		new( 2, 7 ),
		new( 3, 7 ),
		new( 4, 7 ),
		new( 5, 7 ),
		new( 6, 7 ),
		new( 7, 7 )
	};

	static readonly GridPos[] RedArmyPositions =
	{
		new( 0, 0 ),
		new( 1, 0 ),
		new( 2, 0 ),
		new( 3, 0 ),
		new( 4, 0 ),
		new( 5, 0 ),
		new( 6, 0 ),
		new( 7, 0 )
	};

	static List<UnitType?> CreateEmptyArmyChoices()
	{
		var choices = Enumerable.Repeat<UnitType?>( null, ArmySlotCount ).ToList();
		choices[CommanderArmySlotIndex] = UnitType.Commander;
		return choices;
	}

	[Property] public bool UseEmbeddedPanel { get; set; } = true;
	[Property] public SoundEvent UnitHitSound { get; set; }
	[Property] public SoundEvent UnitDeathSound { get; set; }
	[Property] public SoundEvent UnitMoveSound { get; set; }
	[Property] public SoundEvent BackgroundSound { get; set; }

	public RogueChessMode Mode { get; private set; } = RogueChessMode.PlayerVsComputer;
	public AiDifficulty Difficulty { get; private set; } = AiDifficulty.Intermediate;
	public RogueChessTeam CurrentTeam { get; private set; }
	public RogueChessTeam? Winner { get; private set; }
	public bool IsDraw { get; private set; }
	public bool IsGameOver => Winner is not null || IsDraw;
	public int BlueScrap { get; private set; }
	public int RedScrap { get; private set; }
	public bool UnitActionSpent { get; private set; }
	public bool CardPlayed { get; private set; }
	public int SelectedUnitId { get; private set; } = -1;
	public int SelectedCardIndex { get; private set; } = -1;
	public bool SpecialActionSelected { get; private set; }
	public bool MatchStarted { get; private set; }
	public UnitType SelectedArmyBuilderUnit { get; private set; } = UnitType.Buddy;
	public int UiVersion { get; private set; }
	public string StatusMessage { get; private set; } = "";
	public int TurnNumber { get; private set; }

	public IReadOnlyList<UnitData> Units => units;
	public IReadOnlyList<CardType> BlueHand => blueHand;
	public IReadOnlyList<CardType> RedHand => redHand;
	public IReadOnlyList<UnitType?> BlueArmyChoices => blueArmyChoices;
	public IReadOnlyList<UnitType?> RedArmyChoices => redArmyChoices;
	public IReadOnlyList<UnitType> UnitPoolTypes => SelectableArmyTypes;
	public int BlueArmyFilledSlots => blueArmyChoices.Count( type => type.HasValue );
	public bool IsBlueArmyComplete => BlueArmyFilledSlots == ArmySlotCount;

	readonly List<UnitData> units = new();
	readonly List<CardType> blueHand = new();
	readonly List<CardType> redHand = new();
	readonly List<UnitType?> blueArmyChoices = CreateEmptyArmyChoices();
	readonly List<UnitType?> redArmyChoices = DefaultArmyChoices.Select( type => (UnitType?)type ).ToList();
	readonly List<BoardEffect> boardEffects = new();
	readonly List<DyingUnitVisual> dyingUnitVisuals = new();

	int nextUnitId = 1;
	int blueDeckIndex;
	int redDeckIndex;
	bool isRunningAi;
	string lastAiAction = "";
	float nextAiActionTime;
	float nextBackgroundSoundRetryTime;
	int turnsSinceLastAttack;
	RogueChessTeam pvcHardTeam;
	readonly Dictionary<int, GridPos> previousTileByUnit = new();
	SoundEvent backgroundSoundEvent;
	SoundHandle backgroundSoundHandle;
	ScreenPanel screenPanel;
	RogueChessPanel uiPanel;

	protected override void OnAwake()
	{
		// Preload the background track so the first Play has no asset-load stall.
		backgroundSoundEvent = BackgroundSound ?? TryLoadSoundEvent( BackgroundSoundPath );
	}

	protected override void OnStart()
	{
		StartBackgroundSound();
		PrepareArmyBuilder();

		if ( UseEmbeddedPanel )
		{
			CreateEmbeddedPanel();
		}
	}

	protected override void OnDestroy()
	{
		StopBackgroundSound();
		uiPanel?.Delete();
		uiPanel = null;
	}

	protected override void OnUpdate()
	{
		UpdateBoardEffects();
		UpdateDyingUnitVisuals();
		EnsureBackgroundSound();

		if ( MatchStarted && IsCurrentAiTurn() && Time.Now >= nextAiActionTime )
			RunAiTurn();
	}

	void CreateEmbeddedPanel()
	{
		screenPanel = GameObject.GetComponent<ScreenPanel>() ?? GameObject.AddComponent<ScreenPanel>();
		screenPanel.AutoScreenScale = true;

		uiPanel = screenPanel.GetPanel().AddChild<RogueChessPanel>();
		uiPanel.Game = this;
		MarkDirty();
	}

	public void RestartMatch()
	{
		if ( !IsBlueArmyComplete )
		{
			StatusMessage = $"Choose {SelectableArmySlotCount} units to join your Commander before starting the match.";
			MarkDirty();
			return;
		}

		MatchStarted = true;
		units.Clear();
		blueHand.Clear();
		redHand.Clear();
		boardEffects.Clear();
		dyingUnitVisuals.Clear();

		nextUnitId = 1;
		blueDeckIndex = 0;
		redDeckIndex = 0;
		BlueScrap = 0;
		RedScrap = 0;
		Winner = null;
		TurnNumber = 0;
		IsDraw = false;
		turnsSinceLastAttack = 0;
		previousTileByUnit.Clear();
		AssignPvcDifficulties();

		AddStartingArmy( RogueChessTeam.Blue );
		AddStartingArmy( RogueChessTeam.Red );

		StartTurn( RogueChessTeam.Blue );
	}

	void PrepareArmyBuilder()
	{
		MatchStarted = false;
		units.Clear();
		blueHand.Clear();
		redHand.Clear();
		boardEffects.Clear();
		dyingUnitVisuals.Clear();
		ClearSelection();
		StatusMessage = $"Choose {SelectableArmySlotCount} units to join your Commander.";
		MarkDirty();
	}

	void AddStartingArmy( RogueChessTeam team )
	{
		var positions = team == RogueChessTeam.Blue ? BlueArmyPositions : RedArmyPositions;
		var choices = GetArmyChoices( team );

		for ( var i = 0; i < ArmySlotCount; i++ )
		{
			AddUnit( team, choices[i] ?? DefaultArmyChoices[i], positions[i] );
		}
	}

	public void StartMatchFromArmyBuilder()
	{
		RestartMatch();
	}

	public void SelectArmyBuilderUnit( UnitType unitType )
	{
		if ( unitType == UnitType.Commander )
			return;

		SelectedArmyBuilderUnit = unitType;
		StatusMessage = $"{unitType} selected for army slots.";
		MarkDirty();
	}

	public void SetBlueArmySlot( int index )
	{
		if ( MatchStarted || index < 0 || index >= ArmySlotCount || index == CommanderArmySlotIndex )
			return;

		if ( blueArmyChoices[index] == SelectedArmyBuilderUnit )
		{
			ClearBlueArmySlot( index );
			return;
		}

		var previous = blueArmyChoices[index];
		blueArmyChoices[index] = SelectedArmyBuilderUnit;
		StatusMessage = previous.HasValue
			? $"Army slot {index + 1} replaced with {SelectedArmyBuilderUnit}."
			: $"Army slot {index + 1} set to {SelectedArmyBuilderUnit}.";
		MarkDirty();
	}

	public void ClearBlueArmySlot( int index )
	{
		if ( MatchStarted || index < 0 || index >= ArmySlotCount || index == CommanderArmySlotIndex || !blueArmyChoices[index].HasValue )
			return;

		var removed = blueArmyChoices[index].Value;
		blueArmyChoices[index] = null;
		StatusMessage = $"Removed {removed} from army slot {index + 1}.";
		MarkDirty();
	}

	public void SetMode( RogueChessMode mode )
	{
		Mode = mode;

		if ( Mode == RogueChessMode.ComputerVsComputer )
			AssignPvcDifficulties();

		ClearSelection();

		StatusMessage = Mode switch
		{
			RogueChessMode.PlayerVsComputer => "Mode: Player vs Computer. Blue is human and Red is computer.",
			RogueChessMode.PlayerVsPlayer => "Mode: Player vs Player. Both sides are controlled by people.",
			RogueChessMode.ComputerVsComputer => "Mode: Computer vs Computer. Both sides will play automatically.",
			_ => StatusMessage
		};

		ScheduleAiIfNeeded();
		MarkDirty();
	}

	public void SetDifficulty( AiDifficulty difficulty )
	{
		Difficulty = difficulty;
		ClearSelection();
		StatusMessage = $"Difficulty set to {difficulty}.";
		MarkDirty();
	}

	public void CycleArmySlot( RogueChessTeam team, int index )
	{
		if ( MatchStarted || IsCurrentAiTurn() || index < 0 || index >= ArmySlotCount || index == CommanderArmySlotIndex )
			return;

		var choices = GetArmyChoices( team );
		var currentType = choices[index] ?? SelectableArmyTypes[^1];
		var currentIndex = Array.IndexOf( SelectableArmyTypes, currentType );
		choices[index] = SelectableArmyTypes[(currentIndex + 1) % SelectableArmyTypes.Length];
		StatusMessage = $"{TeamName( team )} army slot {index + 1} set to {choices[index]}. Restart Match to deploy this army.";
		MarkDirty();
	}

	public string GetArmySummary( RogueChessTeam team )
	{
		var choices = GetArmyChoices( team );
		return string.Join( ", ", choices.Select( type => type?.ToString() ?? "Empty" ) );
	}

	public void HandleTurnButton()
	{
		if ( Mode == RogueChessMode.ComputerVsComputer )
		{
			StopPvcGame();
			return;
		}

		EndTurn();
	}

	public void StopPvcGame()
	{
		if ( Mode != RogueChessMode.ComputerVsComputer )
			return;

		Mode = RogueChessMode.PlayerVsComputer;
		ClearSelection();
		StatusMessage = "PVC game stopped.";
		MarkDirty();
	}

	public void EndTurn()
	{
		if ( !MatchStarted || IsGameOver || IsCurrentAiTurn() )
			return;

		AdvanceTurn( true );
	}

	public void ClickTile( int x, int y )
	{
		if ( !MatchStarted || IsGameOver || IsCurrentAiTurn() )
			return;

		var pos = new GridPos( x, y );

		if ( SelectedCardIndex >= 0 )
		{
			var hand = GetHand( CurrentTeam );
			var selectedCard = SelectedCardIndex < hand.Count ? hand[SelectedCardIndex] : (CardType?)null;
			var cardSource = GetUnitAt( pos );
			if ( selectedCard == CardType.Push && cardSource is not null && cardSource.Team == CurrentTeam )
			{
				SelectedUnitId = cardSource.Id;
				StatusMessage = cardSource.CanActThisTurn
					? $"{TeamName( CurrentTeam )} selected {cardSource.Type} as the Push source. Now choose an adjacent enemy."
					: $"{TeamName( CurrentTeam )} {cardSource.Type} cannot be the Push source until next turn.";
				MarkDirty();
				return;
			}

			if ( TryPlaySelectedCardAt( pos ) )
				return;

			StatusMessage = "That card cannot target this tile.";
			MarkDirty();
			return;
		}

		var unit = GetUnitAt( pos );
		if ( unit is not null && unit.Team == CurrentTeam )
		{
			SelectedUnitId = unit.Id;
			SpecialActionSelected = false;
			StatusMessage = unit.CanActThisTurn
				? $"{TeamName( CurrentTeam )} selected {unit.Type}."
				: $"{TeamName( CurrentTeam )} {unit.Type} was just built and can act next turn.";
			MarkDirty();
			return;
		}

		var selected = GetSelectedUnit();
		if ( selected is null || UnitActionSpent )
		{
			ClearSelection();
			StatusMessage = UnitActionSpent ? "Unit action already spent this turn." : "Select one of your units first.";
			MarkDirty();
			return;
		}

		if ( SpecialActionSelected )
		{
			if ( unit is not null && unit.Team != CurrentTeam && TryUseSpecialAbility( selected, unit ) )
				return;

			StatusMessage = "No legal special ability for that tile.";
			MarkDirty();
			return;
		}

		if ( unit is null && IsLegalMove( selected, pos ) )
		{
			MoveUnit( selected, pos );
			return;
		}

		if ( unit is not null && unit.Team != CurrentTeam && IsLegalAttack( selected, unit ) )
		{
			AttackUnit( selected, unit );
			return;
		}

		StatusMessage = "No legal action for that tile.";
		MarkDirty();
	}

	public void SelectCard( RogueChessTeam team, int index )
	{
		if ( !MatchStarted || IsGameOver || IsCurrentAiTurn() )
			return;

		if ( team != CurrentTeam )
		{
			StatusMessage = "Only the current player's hand can be played.";
			MarkDirty();
			return;
		}

		var hand = GetHand( team );
		if ( index < 0 || index >= hand.Count )
			return;

		if ( CardPlayed )
		{
			StatusMessage = "Only one card can be played each turn.";
			MarkDirty();
			return;
		}

		var card = hand[index];
		if ( SelectedCardIndex == index )
		{
			CancelSelectedCard();
			return;
		}

		if ( GetScrap( team ) < CardData.All[card].Cost )
		{
			StatusMessage = "Not enough Scrap for that card.";
			MarkDirty();
			return;
		}

		SelectedCardIndex = index;
		SpecialActionSelected = false;
		StatusMessage = card == CardType.Push
			? "Selected Push. First select one of your ready units, then choose an adjacent enemy to push away."
			: $"Selected {CardData.All[card].Name}; choose a valid target.";
		MarkDirty();
	}

	public void ToggleSpecialAction()
	{
		if ( !MatchStarted || IsGameOver || IsCurrentAiTurn() || UnitActionSpent )
			return;

		var selected = GetSelectedUnit();
		if ( selected is null || selected.Team != CurrentTeam || selected.Type != UnitType.Hacker || !selected.CanActThisTurn )
		{
			StatusMessage = "Select a ready Hacker to use a special ability.";
			MarkDirty();
			return;
		}

		SelectedCardIndex = -1;
		SpecialActionSelected = !SpecialActionSelected;
		StatusMessage = SpecialActionSelected
			? "Hacker special selected. Choose an adjacent enemy to disable for its next turn."
			: "Hacker special cancelled.";
		MarkDirty();
	}

	public void CancelSelectedCard()
	{
		if ( SelectedCardIndex < 0 )
			return;

		SelectedCardIndex = -1;
		StatusMessage = "Card selection cancelled.";
		MarkDirty();
	}

	public UnitData GetUnitAt( int x, int y )
	{
		return GetUnitAt( new GridPos( x, y ) );
	}

	public UnitData GetSelectedUnit()
	{
		return units.FirstOrDefault( unit => unit.Id == SelectedUnitId );
	}

	public bool IsResourceTile( int x, int y )
	{
		return ResourceTiles.Contains( new GridPos( x, y ) );
	}

	public string GetTileClass( int x, int y )
	{
		var pos = new GridPos( x, y );
		var classes = new List<string> { "tile", (x + y) % 2 == 0 ? "light" : "dark" };
		var unit = GetUnitAt( pos );

		if ( IsResourceTile( x, y ) )
			classes.Add( "resource" );

		if ( unit is not null )
		{
			classes.Add( "occupied" );
			classes.Add( unit.Team == RogueChessTeam.Blue ? "blue-unit" : "red-unit" );
			if ( unit.Id == SelectedUnitId )
				classes.Add( "selected" );
		}

		if ( SelectedCardIndex >= 0 && IsValidSelectedCardTarget( pos ) )
		{
			classes.Add( "card-target" );
		}
		else if ( SpecialActionSelected && unit is not null && unit.Team != CurrentTeam && IsLegalSpecialAbilityTarget( GetSelectedUnit(), unit ) )
		{
			classes.Add( "legal-special" );
		}
		else
		{
			var selected = GetSelectedUnit();
			if ( selected is not null && !UnitActionSpent )
			{
				if ( unit is null && IsLegalMove( selected, pos ) )
					classes.Add( "legal-move" );

				if ( unit is not null && unit.Team != CurrentTeam && IsLegalAttack( selected, unit ) )
					classes.Add( "legal-attack" );
			}
		}

		return string.Join( " ", classes );
	}

	public string GetTileCueText( int x, int y )
	{
		var pos = new GridPos( x, y );
		var unit = GetUnitAt( pos );

		if ( SelectedCardIndex >= 0 && IsValidSelectedCardTarget( pos ) )
			return "CARD";

		var selected = GetSelectedUnit();
		if ( selected is null || UnitActionSpent )
			return "";

		if ( SpecialActionSelected && unit is not null && unit.Team != CurrentTeam && IsLegalSpecialAbilityTarget( selected, unit ) )
			return "DISABLE";

		if ( unit is null && IsLegalMove( selected, pos ) )
			return "MOVE";

		if ( unit is not null && unit.Team != CurrentTeam && IsLegalAttack( selected, unit ) )
			return "ATTACK";

		return "";
	}

	public string GetHitEffectClass( int x, int y, string baseClass )
	{
		var pos = new GridPos( x, y );
		foreach ( var effect in boardEffects )
		{
			if ( effect.Position != pos || effect.EffectType != "hit" )
				continue;

			var progress = Math.Clamp( (Time.Now - effect.StartTime) / HitEffectDuration, 0f, 1f );
			var phaseClass = progress < 0.18f ? "hit-pop" : progress < 0.68f ? "hit-hold" : "hit-fade";
			return $"{baseClass} {phaseClass}";
		}

		return "";
	}

	public string GetDyingUnitTokenClass( int x, int y )
	{
		foreach ( var visual in dyingUnitVisuals )
		{
			if ( visual.Position != new GridPos( x, y ) )
				continue;

			var progress = Math.Clamp( (Time.Now - visual.StartTime) / DeathEffectDuration, 0f, 1f );
			var phaseClass = progress < 0.2f ? "death-pop" : progress < 0.72f ? "death-hold" : "death-fade";
			return $"dying-unit-token {phaseClass} {GetPieceImageClass( visual.Team, visual.UnitType )}";
		}

		return "";
	}

	public string GetDeathSplatClass( int x, int y, string baseClass )
	{
		foreach ( var visual in dyingUnitVisuals )
		{
			if ( visual.Position != new GridPos( x, y ) )
				continue;

			var progress = Math.Clamp( (Time.Now - visual.StartTime) / DeathEffectDuration, 0f, 1f );
			var phaseClass = progress < 0.2f ? "death-pop" : progress < 0.72f ? "death-hold" : "death-fade";
			return $"{baseClass} {phaseClass}";
		}

		return "";
	}

	public string GetPieceImageClass( RogueChessTeam team, UnitType type )
	{
		if ( team == RogueChessTeam.Blue )
		{
			return type switch
			{
				UnitType.Commander => "piece-commander-blue",
				UnitType.Buddy => "piece-buddy-blue",
				UnitType.Shooter => "piece-shooter-blue",
				UnitType.Tank => "piece-tank-blue",
				UnitType.Hacker => "piece-hacker-blue",
				_ => ""
			};
		}

		return type switch
		{
			UnitType.Commander => "piece-commander-red",
			UnitType.Buddy => "piece-buddy-red",
			UnitType.Shooter => "piece-shooter-red",
			UnitType.Tank => "piece-tank-red",
			UnitType.Hacker => "piece-hacker-red",
			_ => ""
		};
	}

	public string GetCardClass( RogueChessTeam team, int index )
	{
		var classes = new List<string> { "card" };
		var hand = GetHand( team );

		if ( team == CurrentTeam )
			classes.Add( "current" );
		else
			classes.Add( "open-info" );

		if ( index == SelectedCardIndex && team == CurrentTeam )
			classes.Add( "selected" );

		if ( CardPlayed || index < 0 || index >= hand.Count || GetScrap( team ) < CardData.All[hand[index]].Cost || team != CurrentTeam || IsComputerControlled( team ) )
			classes.Add( "disabled" );

		return string.Join( " ", classes );
	}

	public string TeamName( RogueChessTeam team )
	{
		return team == RogueChessTeam.Blue ? "Blue" : "Red";
	}

	public string GetUnitRole( UnitType unitType )
	{
		return unitType switch
		{
			UnitType.Commander => "Leader Unit",
			UnitType.Buddy => "Fast Scout",
			UnitType.Shooter => "Long-Range Support",
			UnitType.Tank => "Durable Frontline",
			UnitType.Hacker => "Battlefield Control",
			_ => ""
		};
	}

	public string GetUnitDescription( UnitType unitType )
	{
		return unitType switch
		{
			UnitType.Commander => "Losing your Commander means you lose the battle.",
			UnitType.Buddy => "Reaches Scrap quickly, flanks enemies, and screens allies.",
			UnitType.Shooter => "Pressures lanes from range but cannot shoot through units.",
			UnitType.Tank => "Holds territory and protects fragile allies with high health.",
			UnitType.Hacker => "May disable one adjacent enemy instead of attacking.",
			_ => ""
		};
	}

	public UnitData CreatePreviewUnit( UnitType unitType )
	{
		return new UnitData( 0, RogueChessTeam.Blue, unitType, default );
	}

	public int GetScrap( RogueChessTeam team )
	{
		return team == RogueChessTeam.Blue ? BlueScrap : RedScrap;
	}

	public bool IsCurrentAiTurn()
	{
		return MatchStarted && IsComputerControlled( CurrentTeam ) && !IsGameOver;
	}

	public bool IsComputerControlled( RogueChessTeam team )
	{
		return Mode switch
		{
			RogueChessMode.PlayerVsComputer => team == RogueChessTeam.Red,
			RogueChessMode.ComputerVsComputer => true,
			_ => false
		};
	}

	public bool IsHumanControlled( RogueChessTeam team )
	{
		return !IsComputerControlled( team );
	}

	void AdvanceTurn( bool allowAi )
	{
		ClearEndOfTurnBonuses( CurrentTeam );
		ClearSelection();
		StartTurn( OtherTeam( CurrentTeam ) );

		if ( allowAi && IsCurrentAiTurn() )
		{
			ScheduleAiIfNeeded();
		}
	}

	void StartTurn( RogueChessTeam team )
	{
		CurrentTeam = team;
		UnitActionSpent = false;
		CardPlayed = false;
		SelectedCardIndex = -1;
		SelectedUnitId = -1;
		TurnNumber++;
		turnsSinceLastAttack++;

		if ( turnsSinceLastAttack >= StalemateTurnLimit )
		{
			IsDraw = true;
			StatusMessage = "Stalemate — no attacks for many turns. The match is a draw. Press Restart Match to play again.";
			MarkDirty();
			return;
		}

		foreach ( var unit in units.Where( unit => unit.Team == team ) )
		{
			if ( unit.DisabledTurns > 0 )
			{
				unit.CanActThisTurn = false;
				unit.IsDisabledThisTurn = true;
				unit.DisabledTurns--;
			}
			else
			{
				unit.CanActThisTurn = true;
				unit.IsDisabledThisTurn = false;
			}

			unit.Shield = 0;
			unit.FocusDamageBonus = 0;
			unit.SprintMoveBonus = 0;
		}

		var scrapGain = 1 + units.Count( unit => unit.Team == team && ResourceTiles.Contains( unit.Position ) );
		if ( DifficultyFor( team ) == AiDifficulty.Hard && IsComputerControlled( team ) )
			scrapGain += 1;
		SetScrap( team, GetScrap( team ) + scrapGain );
		DrawCard( team );

		StatusMessage = $"{TeamName( team )} starts turn and gains {scrapGain} Scrap.";
		ScheduleAiIfNeeded();
		MarkDirty();
	}

	void ScheduleAiIfNeeded()
	{
		if ( IsCurrentAiTurn() )
			nextAiActionTime = Time.Now + AiActionDelay;
	}

	// Computer-vs-Computer plays at a steady, watchable pace; otherwise speed scales with difficulty.
	float AiActionDelay
	{
		get
		{
			if ( Mode == RogueChessMode.ComputerVsComputer )
				return 1.0f;

			return ActiveDifficulty switch
			{
				AiDifficulty.Intermediate => 0.5f,
				AiDifficulty.Hard => 0.3f,
				_ => 0.65f
			};
		}
	}

	// Intermediate and Hard attack any enemy in range before collecting Scrap.
	bool AiAttacksBeforeResources => ActiveDifficulty != AiDifficulty.Beginner;

	// Intermediate and Hard reinforce whenever they can afford it.
	bool AiBuildsEagerly => ActiveDifficulty != AiDifficulty.Beginner;

	// In Computer-vs-Computer each side is randomly Intermediate or Hard; otherwise the chosen Difficulty applies.
	public AiDifficulty DifficultyFor( RogueChessTeam team )
	{
		if ( Mode == RogueChessMode.ComputerVsComputer )
			return team == pvcHardTeam ? AiDifficulty.Hard : AiDifficulty.Intermediate;

		return Difficulty;
	}

	AiDifficulty ActiveDifficulty => DifficultyFor( CurrentTeam );

	void AssignPvcDifficulties()
	{
		pvcHardTeam = Random.Shared.Next( 2 ) == 0 ? RogueChessTeam.Blue : RogueChessTeam.Red;
	}

	void DrawCard( RogueChessTeam team )
	{
		var hand = GetHand( team );
		if ( hand.Count >= HandLimit )
			return;

		var deckIndex = team == RogueChessTeam.Blue ? blueDeckIndex : redDeckIndex;
		hand.Add( DeckOrder[deckIndex % DeckOrder.Length] );
		deckIndex++;

		if ( team == RogueChessTeam.Blue )
			blueDeckIndex = deckIndex;
		else
			redDeckIndex = deckIndex;
	}

	UnitData AddUnit( RogueChessTeam team, UnitType type, GridPos position, bool canActThisTurn = true )
	{
		var unit = new UnitData( nextUnitId++, team, type, position )
		{
			CanActThisTurn = canActThisTurn
		};

		units.Add( unit );
		return unit;
	}

	void MoveUnit( UnitData unit, GridPos destination )
	{
		var from = unit.Position;
		previousTileByUnit[unit.Id] = from;
		unit.Position = destination;
		unit.SprintMoveBonus = 0;
		UnitActionSpent = true;
		ClearSelection();
		PlayUnitMoveSound();
		StatusMessage = $"{TeamName( unit.Team )} {unit.Type} moved.";
		if ( isRunningAi )
			lastAiAction = $"{TeamName( unit.Team )} computer moved {unit.Type} from {FormatPos( from )} to {FormatPos( destination )}.";

		MarkDirty();
	}

	void AttackUnit( UnitData attacker, UnitData defender )
	{
		turnsSinceLastAttack = 0;
		var damage = attacker.CurrentDamage;
		var shieldAbsorb = Math.Min( defender.Shield, damage );
		defender.Shield -= shieldAbsorb;
		damage -= shieldAbsorb;
		defender.Health -= damage;

		attacker.FocusDamageBonus = 0;
		attacker.SprintMoveBonus = 0;
		UnitActionSpent = true;
		ClearSelection();

		if ( defender.Health <= 0 )
		{
			PlayUnitDeathSound();
			AddDyingUnitVisual( defender );
			units.Remove( defender );

			if ( defender.Type == UnitType.Commander )
			{
				Winner = attacker.Team;
				StatusMessage = $"{TeamName( attacker.Team )} wins! The enemy Commander is down.";
			}
			else
			{
				StatusMessage = $"{TeamName( attacker.Team )} {attacker.Type} defeated {defender.Type}.";
			}
		}
		else
		{
			PlayUnitHitSound();
			AddHitEffect( defender.Position );
			StatusMessage = $"{TeamName( attacker.Team )} {attacker.Type} attacked {defender.Type}.";
		}

		if ( isRunningAi )
		{
			lastAiAction = defender.Health <= 0
				? $"{TeamName( attacker.Team )} computer attacked and defeated {TeamName( defender.Team )} {defender.Type}."
				: $"{TeamName( attacker.Team )} computer attacked {TeamName( defender.Team )} {defender.Type}.";
		}

		MarkDirty();
	}

	bool TryUseSpecialAbility( UnitData user, UnitData target )
	{
		if ( !IsLegalSpecialAbilityTarget( user, target ) )
			return false;

		target.DisabledTurns = 1;
		UnitActionSpent = true;
		ClearSelection();
		StatusMessage = $"{TeamName( user.Team )} Hacker disabled {TeamName( target.Team )} {target.Type}.";
		if ( isRunningAi )
			lastAiAction = $"{TeamName( user.Team )} computer disabled {TeamName( target.Team )} {target.Type}.";

		MarkDirty();
		return true;
	}

	void AddHitEffect( GridPos pos )
	{
		boardEffects.RemoveAll( effect => effect.Position == pos && effect.EffectType == "hit" );
		boardEffects.Add( new BoardEffect( pos, "hit", Time.Now, Time.Now + HitEffectDuration ) );
	}

	void AddDyingUnitVisual( UnitData unit )
	{
		dyingUnitVisuals.RemoveAll( visual => visual.Position == unit.Position );
		dyingUnitVisuals.Add( new DyingUnitVisual( unit.Position, unit.Type, unit.Team, Time.Now, Time.Now + DeathEffectDuration ) );
	}

	void UpdateBoardEffects()
	{
		if ( boardEffects.Count == 0 )
			return;

		boardEffects.RemoveAll( effect => Time.Now >= effect.EndTime );
		MarkDirty();
	}

	void UpdateDyingUnitVisuals()
	{
		if ( dyingUnitVisuals.Count == 0 )
			return;

		dyingUnitVisuals.RemoveAll( visual => Time.Now >= visual.EndTime );
		MarkDirty();
	}

	void PlayUnitHitSound()
	{
		PlayUiSound( UnitHitSound, UnitHitSoundPath, UnitSoundVolume );
	}

	void PlayUnitDeathSound()
	{
		PlayUiSound( UnitDeathSound, UnitDeathSoundPath, UnitSoundVolume );
	}

	void PlayUnitMoveSound()
	{
		PlayUiSound( UnitMoveSound, UnitMoveSoundPath, UnitSoundVolume );
	}

	void PlayUiSound( SoundEvent soundEvent, string fallbackPath, float volume )
	{
		if ( soundEvent is not null && TryPlaySoundEvent( soundEvent, volume ) )
			return;

		soundEvent = TryLoadSoundEvent( fallbackPath );
		if ( soundEvent is not null )
			TryPlaySoundEvent( soundEvent, volume );
	}

	bool TryPlaySoundEvent( SoundEvent soundEvent, float volume = UnitSoundVolume )
	{
		try
		{
			var handle = Sound.Play( soundEvent );
			handle.Volume = volume;
			handle.SpacialBlend = 0f;
			return true;
		}
		catch
		{
			return false;
		}
	}

	void StartBackgroundSound()
	{
		// Play immediately on game start, bypassing the retry throttle
		// (the throttle field can survive editor hot-reloads).
		nextBackgroundSoundRetryTime = 0f;
		backgroundSoundHandle = null;
		EnsureBackgroundSound();
	}

	void EnsureBackgroundSound()
	{
		if ( backgroundSoundHandle is { IsValid: true, IsPlaying: true, Finished: false } )
			return;

		if ( Time.Now < nextBackgroundSoundRetryTime )
			return;

		nextBackgroundSoundRetryTime = Time.Now + 2f;
		backgroundSoundEvent ??= BackgroundSound ?? TryLoadSoundEvent( BackgroundSoundPath );
		if ( backgroundSoundEvent is null )
			return;

		try
		{
			backgroundSoundHandle = Sound.Play( backgroundSoundEvent );
			backgroundSoundHandle.Volume = BackgroundSoundVolume;
			backgroundSoundHandle.SpacialBlend = 0f;
		}
		catch
		{
			backgroundSoundHandle = null;
		}
	}

	void StopBackgroundSound()
	{
		if ( backgroundSoundHandle is { IsValid: true, IsStopped: false } )
			backgroundSoundHandle.Stop();

		backgroundSoundHandle = null;
	}

	SoundEvent TryLoadSoundEvent( string path )
	{
		try
		{
			return ResourceLibrary.Get<SoundEvent>( path );
		}
		catch
		{
			return null;
		}
	}

	bool TryPlaySelectedCardAt( GridPos pos )
	{
		var hand = GetHand( CurrentTeam );
		if ( SelectedCardIndex < 0 || SelectedCardIndex >= hand.Count || CardPlayed )
			return false;

		var card = hand[SelectedCardIndex];
		if ( !TryPlayCardAt( CurrentTeam, card, pos ) )
			return false;

		hand.RemoveAt( SelectedCardIndex );
		CardPlayed = true;
		SelectedCardIndex = -1;
		CheckCommanderVictory();
		MarkDirty();
		return true;
	}

	bool TryPlayCardAt( RogueChessTeam team, CardType card, GridPos pos )
	{
		var data = CardData.All[card];
		if ( GetScrap( team ) < data.Cost )
			return false;

		if ( !ApplyCardEffect( team, card, pos ) )
			return false;

		SetScrap( team, GetScrap( team ) - data.Cost );
		StatusMessage = $"{TeamName( team )} played {data.Name}.";
		return true;
	}

	bool ApplyCardEffect( RogueChessTeam team, CardType card, GridPos pos )
	{
		var target = GetUnitAt( pos );

		switch ( card )
		{
			case CardType.Guard:
				if ( target is null || target.Team != team )
					return false;

				target.Shield += 1;
				return true;

			case CardType.Push:
				if ( target is null || target.Team == team )
					return false;

				if ( !TryGetPushDestination( team, target, out var destination ) )
					return false;

				target.Position = destination;
				return true;

			case CardType.Focus:
				if ( UnitActionSpent || target is null || target.Team != team )
					return false;

				target.FocusDamageBonus = 1;
				return true;

			case CardType.Sprint:
				if ( UnitActionSpent || target is null || target.Team != team )
					return false;

				target.SprintMoveBonus = 1;
				return true;

			case CardType.BuildBuddy:
				if ( GetUnitAt( pos ) is not null || !pos.IsInsideBoard )
					return false;

				var commander = GetCommander( team );
				if ( commander is null || commander.Position.ManhattanDistance( pos ) != 1 )
					return false;

				AddUnit( team, UnitType.Buddy, pos, false );
				return true;

			case CardType.Repair:
				if ( target is null || target.Team != team || target.Health >= target.MaxHealth )
					return false;

				target.Health = Math.Min( target.MaxHealth, target.Health + 1 );
				return true;
		}

		return false;
	}

	bool IsValidSelectedCardTarget( GridPos pos )
	{
		var hand = GetHand( CurrentTeam );
		if ( SelectedCardIndex < 0 || SelectedCardIndex >= hand.Count )
			return false;

		return IsValidCardTarget( CurrentTeam, hand[SelectedCardIndex], pos );
	}

	bool IsValidCardTarget( RogueChessTeam team, CardType card, GridPos pos )
	{
		var target = GetUnitAt( pos );
		var commander = GetCommander( team );

		return card switch
		{
			CardType.Guard => target is not null && target.Team == team,
			CardType.Push => target is not null && target.Team != team && TryGetPushDestination( team, target, out _ ),
			CardType.Focus => !UnitActionSpent && target is not null && target.Team == team,
			CardType.Sprint => !UnitActionSpent && target is not null && target.Team == team,
			CardType.BuildBuddy => target is null && pos.IsInsideBoard && commander is not null && commander.Position.ManhattanDistance( pos ) == 1,
			CardType.Repair => target is not null && target.Team == team && target.Health < target.MaxHealth,
			_ => false
		};
	}

	bool TryGetPushDestination( RogueChessTeam team, UnitData enemy, out GridPos destination )
	{
		var selected = GetSelectedUnit();
		if ( selected is not null && selected.Team == team && selected.CanActThisTurn && TryGetPushDestinationFromSource( selected, enemy, out destination ) )
			return true;

		destination = default;
		return false;
	}

	bool TryGetPushDestinationFromSource( UnitData source, UnitData enemy, out GridPos destination )
	{
		destination = default;

		if ( source.Position.ManhattanDistance( enemy.Position ) != 1 )
			return false;

		var direction = new GridPos( enemy.Position.X - source.Position.X, enemy.Position.Y - source.Position.Y );
		var pushed = enemy.Position.Offset( direction );
		if ( !pushed.IsInsideBoard || GetUnitAt( pushed ) is not null )
			return false;

		destination = pushed;
		return true;
	}

	bool IsLegalMove( UnitData unit, GridPos destination )
	{
		return GetLegalMoves( unit ).Contains( destination );
	}

	bool IsLegalAttack( UnitData attacker, UnitData defender )
	{
		return attacker.Team == CurrentTeam
			&& attacker.CanActThisTurn
			&& defender.Team != attacker.Team
			&& attacker.Position.ManhattanDistance( defender.Position ) <= attacker.AttackRange
			&& HasLineOfSight( attacker.Position, defender.Position );
	}

	bool IsLegalSpecialAbilityTarget( UnitData user, UnitData target )
	{
		return user is not null
			&& target is not null
			&& user.Team == CurrentTeam
			&& user.Type == UnitType.Hacker
			&& user.CanActThisTurn
			&& !UnitActionSpent
			&& target.Team != user.Team
			&& user.Position.ManhattanDistance( target.Position ) == 1;
	}

	List<GridPos> GetLegalMoves( UnitData unit )
	{
		var legal = new List<GridPos>();
		if ( unit.Team != CurrentTeam || UnitActionSpent || !unit.CanActThisTurn )
			return legal;

		var visited = new HashSet<GridPos> { unit.Position };
		var queue = new Queue<(GridPos Position, int Distance)>();
		queue.Enqueue( (unit.Position, 0) );

		while ( queue.Count > 0 )
		{
			var current = queue.Dequeue();
			if ( current.Distance >= unit.CurrentMoveRange )
				continue;

			foreach ( var direction in GridPos.CardinalDirections )
			{
				var next = current.Position.Offset( direction );
				if ( !next.IsInsideBoard || visited.Contains( next ) || GetUnitAt( next ) is not null )
					continue;

				visited.Add( next );
				legal.Add( next );
				queue.Enqueue( (next, current.Distance + 1) );
			}
		}

		return legal;
	}

	bool HasLineOfSight( GridPos from, GridPos to )
	{
		var distance = from.ManhattanDistance( to );
		if ( distance <= 1 )
			return true;

		if ( from.X != to.X && from.Y != to.Y )
			return false;

		var stepX = Math.Sign( to.X - from.X );
		var stepY = Math.Sign( to.Y - from.Y );
		var current = from.Offset( new GridPos( stepX, stepY ) );

		while ( current != to )
		{
			if ( GetUnitAt( current ) is not null )
				return false;

			current = current.Offset( new GridPos( stepX, stepY ) );
		}

		return true;
	}

	void RunAiTurn()
	{
		if ( !IsCurrentAiTurn() )
			return;

		var team = CurrentTeam;
		isRunningAi = true;
		lastAiAction = $"{TeamName( team )} computer had no legal move.";

		try
		{
			TryAiPlayCard( team );
			TryAiUnitAction( team );
		}
		finally
		{
			isRunningAi = false;
		}

		if ( !IsGameOver )
		{
			AdvanceTurn( false );
			StatusMessage = $"{lastAiAction} {TeamName( CurrentTeam )} turn starts.";
			MarkDirty();
		}
	}

	void TryAiPlayCard( RogueChessTeam team )
	{
		if ( CardPlayed )
			return;

		if ( TryAiPlayFocus( team ) ) return;
		if ( TryAiPlayGuard( team ) ) return;
		if ( TryAiPlayRepair( team ) ) return;
		if ( TryAiBuildBuddy( team ) ) return;
		TryAiPlaySprint( team );
	}

	bool AiPlayCard( RogueChessTeam team, CardType card, GridPos pos )
	{
		if ( CardPlayed )
			return false;

		var hand = GetHand( team );
		var index = hand.IndexOf( card );
		if ( index < 0 || GetScrap( team ) < CardData.All[card].Cost )
			return false;

		SelectedCardIndex = index;
		if ( TryPlaySelectedCardAt( pos ) )
			return true;

		SelectedCardIndex = -1;
		return false;
	}

	bool TryAiPlayFocus( RogueChessTeam team )
	{
		var enemyCommander = GetCommander( OtherTeam( team ) );
		if ( enemyCommander is null )
			return false;

		var attacker = units.FirstOrDefault( unit => unit.Team == team && IsLegalAttack( unit, enemyCommander ) );
		if ( attacker is null )
			return false;

		if ( AiPlayCard( team, CardType.Focus, attacker.Position ) )
		{
			lastAiAction = $"{TeamName( team )} computer focused {attacker.Type} for a stronger attack.";
			return true;
		}

		return false;
	}

	bool TryAiPlayGuard( RogueChessTeam team )
	{
		var commander = GetCommander( team );
		if ( commander is null || !IsUnitUnderThreat( commander ) )
			return false;

		if ( AiPlayCard( team, CardType.Guard, commander.Position ) )
		{
			lastAiAction = $"{TeamName( team )} computer guarded its Commander.";
			return true;
		}

		return false;
	}

	bool TryAiPlayRepair( RogueChessTeam team )
	{
		var commander = GetCommander( team );
		var target = ( commander is not null && commander.Health < commander.MaxHealth )
			? commander
			: units.Where( unit => unit.Team == team && unit.Health < unit.MaxHealth )
				.OrderBy( unit => unit.Health )
				.FirstOrDefault();
		if ( target is null )
			return false;

		if ( AiPlayCard( team, CardType.Repair, target.Position ) )
		{
			lastAiAction = $"{TeamName( team )} computer repaired {target.Type}.";
			return true;
		}

		return false;
	}

	bool TryAiBuildBuddy( RogueChessTeam team )
	{
		var commander = GetCommander( team );
		if ( commander is null )
			return false;

		var enemyTeam = OtherTeam( team );
		var shouldBuild = AiBuildsEagerly || units.Count( unit => unit.Team == team ) < units.Count( unit => unit.Team == enemyTeam ) || TurnNumber % 3 == 0;
		if ( !shouldBuild )
			return false;

		foreach ( var direction in GridPos.CardinalDirections )
		{
			var pos = commander.Position.Offset( direction );
			if ( pos.IsInsideBoard && GetUnitAt( pos ) is null && AiPlayCard( team, CardType.BuildBuddy, pos ) )
			{
				lastAiAction = $"{TeamName( team )} computer built a Buddy at {FormatPos( pos )}.";
				return true;
			}
		}

		return false;
	}

	bool TryAiPlaySprint( RogueChessTeam team )
	{
		var enemyCommander = GetCommander( OtherTeam( team ) );
		if ( enemyCommander is null )
			return false;

		if ( units.Any( unit => unit.Team == team && units.Any( e => e.Team != team && IsLegalAttack( unit, e ) ) ) )
			return false;

		var mover = units
			.Where( unit => unit.Team == team && GetAiProgressMoves( unit, enemyCommander.Position ).Count > 0 )
			.OrderBy( unit => unit.Position.ManhattanDistance( enemyCommander.Position ) )
			.FirstOrDefault();
		if ( mover is null )
			return false;

		if ( AiPlayCard( team, CardType.Sprint, mover.Position ) )
		{
			lastAiAction = $"{TeamName( team )} computer sprinted {mover.Type}.";
			return true;
		}

		return false;
	}

	bool IsUnitUnderThreat( UnitData unit )
	{
		return units.Any( enemy => enemy.Team != unit.Team
			&& IsUnitUnderThreatFrom( unit, enemy ) );
	}

	bool IsUnitUnderThreatFrom( UnitData unit, UnitData enemy )
	{
		return enemy.Position.ManhattanDistance( unit.Position ) <= enemy.AttackRange
			&& HasLineOfSight( enemy.Position, unit.Position );
	}

	void TryAiUnitAction( RogueChessTeam team )
	{
		var enemyTeam = OtherTeam( team );
		var enemyCommander = GetCommander( enemyTeam );
		if ( enemyCommander is null )
			return;

		// Always go for the enemy Commander or a guaranteed kill first.
		foreach ( var attacker in units.Where( unit => unit.Team == team ).ToList() )
		{
			if ( IsLegalAttack( attacker, enemyCommander ) )
			{
				AttackUnit( attacker, enemyCommander );
				return;
			}
		}

		foreach ( var attacker in units.Where( unit => unit.Team == team ).ToList() )
		{
			var killTarget = units.FirstOrDefault( unit => unit.Team == enemyTeam && IsLegalAttack( attacker, unit ) && unit.Health <= attacker.CurrentDamage );
			if ( killTarget is not null )
			{
				AttackUnit( attacker, killTarget );
				return;
			}
		}

		if ( TryAiDisableCommanderThreat( team, enemyTeam ) )
			return;

		if ( AiAttacksBeforeResources )
		{
			if ( TryAiAttackAnyEnemy( team, enemyTeam ) ) return;
			if ( TryAiCollectResource( team, enemyCommander ) ) return;
		}
		else
		{
			if ( TryAiCollectResource( team, enemyCommander ) ) return;
			if ( TryAiAttackAnyEnemy( team, enemyTeam ) ) return;
		}

		if ( TryAiDisableAnyEnemy( team, enemyTeam ) )
			return;

		var bestMove = units
			.Where( unit => unit.Team == team )
			.SelectMany( unit => GetAiProgressMoves( unit, enemyCommander.Position ).Select( pos => new { Unit = unit, Pos = pos, Distance = pos.ManhattanDistance( enemyCommander.Position ) } ) )
			.OrderBy( move => move.Distance )
			.FirstOrDefault();

		if ( bestMove is not null )
			MoveUnit( bestMove.Unit, bestMove.Pos );
	}

	bool TryAiDisableCommanderThreat( RogueChessTeam team, RogueChessTeam enemyTeam )
	{
		var commander = GetCommander( team );
		if ( commander is null )
			return false;

		foreach ( var hacker in units.Where( unit => unit.Team == team && unit.Type == UnitType.Hacker ).ToList() )
		{
			var threat = units.FirstOrDefault( unit => unit.Team == enemyTeam && IsUnitUnderThreatFrom( commander, unit ) && IsLegalSpecialAbilityTarget( hacker, unit ) );
			if ( threat is not null )
				return TryUseSpecialAbility( hacker, threat );
		}

		return false;
	}

	bool TryAiDisableAnyEnemy( RogueChessTeam team, RogueChessTeam enemyTeam )
	{
		foreach ( var hacker in units.Where( unit => unit.Team == team && unit.Type == UnitType.Hacker ).ToList() )
		{
			var target = units
				.Where( unit => unit.Team == enemyTeam && IsLegalSpecialAbilityTarget( hacker, unit ) )
				.OrderByDescending( unit => unit.Type == UnitType.Commander )
				.ThenBy( unit => unit.Health )
				.FirstOrDefault();
			if ( target is not null )
				return TryUseSpecialAbility( hacker, target );
		}

		return false;
	}

	bool TryAiAttackAnyEnemy( RogueChessTeam team, RogueChessTeam enemyTeam )
	{
		foreach ( var attacker in units.Where( unit => unit.Team == team ).ToList() )
		{
			var target = units.FirstOrDefault( unit => unit.Team == enemyTeam && IsLegalAttack( attacker, unit ) );
			if ( target is not null )
			{
				AttackUnit( attacker, target );
				return true;
			}
		}

		return false;
	}

	bool TryAiCollectResource( RogueChessTeam team, UnitData enemyCommander )
	{
		foreach ( var unit in units.Where( unit => unit.Team == team ).ToList() )
		{
			var resourceMove = GetAiProgressMoves( unit, enemyCommander.Position )
				.Where( pos => ResourceTiles.Contains( pos ) )
				.OrderBy( pos => pos.ManhattanDistance( enemyCommander.Position ) )
				.FirstOrDefault();
			if ( ResourceTiles.Contains( resourceMove ) )
			{
				MoveUnit( unit, resourceMove );
				return true;
			}
		}

		return false;
	}

	List<GridPos> GetAiProgressMoves( UnitData unit, GridPos enemyTarget )
	{
		var currentDistance = unit.Position.ManhattanDistance( enemyTarget );
		var hasPrevious = previousTileByUnit.TryGetValue( unit.Id, out var previous );

		return GetLegalMoves( unit )
			.Where( pos => !hasPrevious || pos != previous )
			.Where( pos => pos.ManhattanDistance( enemyTarget ) < currentDistance )
			.ToList();
	}

	void CheckCommanderVictory()
	{
		var blueCommander = GetCommander( RogueChessTeam.Blue );
		var redCommander = GetCommander( RogueChessTeam.Red );

		if ( blueCommander is not null && blueCommander.Health <= 0 )
			Winner = RogueChessTeam.Red;

		if ( redCommander is not null && redCommander.Health <= 0 )
			Winner = RogueChessTeam.Blue;
	}

	void ClearEndOfTurnBonuses( RogueChessTeam team )
	{
		foreach ( var unit in units.Where( unit => unit.Team == team ) )
		{
			unit.FocusDamageBonus = 0;
			unit.SprintMoveBonus = 0;
		}
	}

	void ClearSelection()
	{
		SelectedUnitId = -1;
		SelectedCardIndex = -1;
		SpecialActionSelected = false;
	}

	void SetScrap( RogueChessTeam team, int value )
	{
		if ( team == RogueChessTeam.Blue )
			BlueScrap = value;
		else
			RedScrap = value;
	}

	List<CardType> GetHand( RogueChessTeam team )
	{
		return team == RogueChessTeam.Blue ? blueHand : redHand;
	}

	List<UnitType?> GetArmyChoices( RogueChessTeam team )
	{
		return team == RogueChessTeam.Blue ? blueArmyChoices : redArmyChoices;
	}

	UnitData GetUnitAt( GridPos pos )
	{
		return units.FirstOrDefault( unit => unit.Position == pos );
	}

	UnitData GetCommander( RogueChessTeam team )
	{
		return units.FirstOrDefault( unit => unit.Team == team && unit.Type == UnitType.Commander );
	}

	RogueChessTeam OtherTeam( RogueChessTeam team )
	{
		return team == RogueChessTeam.Blue ? RogueChessTeam.Red : RogueChessTeam.Blue;
	}

	string FormatPos( GridPos pos )
	{
		return $"({pos.X},{pos.Y})";
	}

	void MarkDirty()
	{
		UiVersion++;
		uiPanel?.StateHasChanged();
	}

	readonly record struct BoardEffect( GridPos Position, string EffectType, float StartTime, float EndTime );
	readonly record struct DyingUnitVisual( GridPos Position, UnitType UnitType, RogueChessTeam Team, float StartTime, float EndTime );
}
