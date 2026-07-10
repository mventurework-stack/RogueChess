using Sandbox;
using Sandbox.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrategyGame;

public enum AiDifficulty
{
	// Beginner = the former "Hard" rule-based behavior. Normal is a placeholder that uses the same logic
	// for now; it will switch to the minimax AI once that is ported (Stage 3).
	Beginner,
	Normal
}

/// <summary>
/// Main controller for the Scrap Chess Buddies prototype.
/// Scene setup: create an empty GameObject, attach this component, and leave
/// UseEmbeddedPanel enabled to spawn the UI through a ScreenPanel automatically.
/// </summary>
[Title( "Rogue Chess Game" ), Category( "Prototype" ), Icon( "grid_on" )]
public sealed partial class RogueChessGameComponent : Component
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

	// Heroes card set = Focus, Guard, Sprint, Reboot, Push (5 cards). BuildBuddy/Repair are excluded
	// from the rotation (their enum/effect code remains but is never drawn).
	static readonly CardType[] DeckOrder =
	{
		CardType.Guard,
		CardType.Push,
		CardType.Reboot,
		CardType.Focus,
		CardType.Sprint
	};

	// The player picks HEROES only (Buddy is auto-filled), so Buddy is not in the selectable pool.
	static readonly UnitType[] SelectableArmyTypes =
	{
		UnitType.Shooter,
		UnitType.Tank,
		UnitType.Hacker
	};

	const int HeroSlotCount = 3; // player picks exactly 3 heroes; the other 4 non-Commander slots auto-fill with Buddy

	static readonly UnitType[] DefaultArmyChoices =
	{
		UnitType.Buddy,
		UnitType.Buddy,
		UnitType.Tank,
		UnitType.Commander,
		UnitType.Buddy,
		UnitType.Buddy,
		UnitType.Shooter,
		UnitType.Hacker
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

	[Sync] public RogueChessMode Mode { get; private set; } = RogueChessMode.PlayerVsComputer;
	public AiDifficulty Difficulty { get; private set; } = AiDifficulty.Beginner;
	public RogueChessTeam CurrentTeam { get; private set; }
	public RogueChessTeam? Winner { get; private set; }
	public string WinReason { get; private set; }
	public bool IsDraw { get; private set; }
	public bool IsGameOver => Winner is not null || IsDraw;
	public string TurnStatusText => IsGameOver ? "Game Over" : $"{TeamName( CurrentTeam )} Turn";
	public int BlueScrap { get; private set; }
	public int RedScrap { get; private set; }
	// Separated move-phase turn structure:
	//  - Move phase: up to MoveSlotsPerTurn DIFFERENT friendly units may each move once (reposition only).
	//  - Attack phase: exactly 1 attack, only by a unit that did NOT use a move slot this turn.
	// Moving and attacking are fully separate actions on fully separate units — no move-then-attack combo.
	public const int MoveSlotsPerTurn = 3;
	public int MovesUsedThisTurn { get; private set; }
	public bool AttackUsedThisTurn { get; private set; }
	int attackerUnitIdThisTurn = -1;
	readonly HashSet<int> movedUnitIdsThisTurn = new();

	// Legacy alias kept for UI/human code paths: "any unit action taken this turn".
	public bool UnitActionSpent => MovesUsedThisTurn > 0 || AttackUsedThisTurn;

	bool CanUnitMove( UnitData u ) =>
		u.Team == CurrentTeam && u.CanActThisTurn
		&& MovesUsedThisTurn < MoveSlotsPerTurn
		&& !movedUnitIdsThisTurn.Contains( u.Id )
		&& u.Id != attackerUnitIdThisTurn;

	bool CanUnitAttack( UnitData a ) =>
		a.Team == CurrentTeam && a.CanActThisTurn
		&& !AttackUsedThisTurn
		&& !movedUnitIdsThisTurn.Contains( a.Id );

	// Two card windows per turn: one before the move phase, one after all actions.
	public bool CardPlayedBeforeAction { get; private set; }
	public bool CardPlayedAfterAction { get; private set; }
	public bool CanPlayCard => UnitActionSpent ? !CardPlayedAfterAction : !CardPlayedBeforeAction;

	// UI helpers for the move-phase turn display.
	public bool AttackAvailableThisTurn => !AttackUsedThisTurn;

	public string GetSelectedUnitEligibility()
	{
		var unit = GetSelectedUnit();
		if ( unit is null || unit.Team != CurrentTeam )
			return "";

		if ( unit.Id == attackerUnitIdThisTurn )
			return "Attacked this turn — done";
		if ( movedUnitIdsThisTurn.Contains( unit.Id ) )
			return "Moved this turn — cannot attack";

		var canMove = CanUnitMove( unit );
		var canAttack = CanUnitAttack( unit ) && units.Any( e => e.Team != unit.Team && IsLegalAttack( unit, e ) );
		if ( canMove && canAttack )
			return "Eligible to move or attack";
		if ( canMove )
			return "Eligible to move";
		if ( canAttack )
			return "Eligible to attack";
		return "No action available this turn";
	}

	public int SelectedUnitId { get; private set; } = -1;
	public int SelectedCardIndex { get; private set; } = -1;
	public bool MatchStarted { get; private set; }
	// Blue's builder selection doubles as the offline single-army selection. Red has its own so each
	// online player can pick independently. Synced implicitly: edits round-trip through the host RPCs.
	public UnitType SelectedArmyBuilderUnit { get; private set; } = UnitType.Shooter;
	UnitType selectedRedArmyBuilderUnit = UnitType.Shooter;

	public UnitType GetSelectedArmyBuilderUnit( RogueChessTeam team )
	{
		return team == RogueChessTeam.Blue ? SelectedArmyBuilderUnit : selectedRedArmyBuilderUnit;
	}

	void SetSelectedArmyBuilderUnit( RogueChessTeam team, UnitType unitType )
	{
		if ( team == RogueChessTeam.Blue )
			SelectedArmyBuilderUnit = unitType;
		else
			selectedRedArmyBuilderUnit = unitType;
	}

	// Per-team "finished building, ready to start" flags. Only meaningful in an online session; both must be
	// true before the host begins the match. Synced to clients via NetState (see NetSync).
	public bool BlueArmyReady { get; private set; }
	public bool RedArmyReady { get; private set; }

	// The team the local player configures in the army builder: Blue offline, or the player's own online team.
	// Spectators return null (read-only builder view).
	public RogueChessTeam? LocalBuilderTeam
	{
		get
		{
			if ( !OnlineSessionActive )
				return RogueChessTeam.Blue;

			return LocalOnlineRole switch
			{
				RogueChessOnlineRole.BluePlayer => RogueChessTeam.Blue,
				RogueChessOnlineRole.RedPlayer => RogueChessTeam.Red,
				_ => null
			};
		}
	}

	public int UiVersion { get; private set; }
	public string StatusMessage { get; private set; } = "";
	public string LastAttackStatus { get; private set; } = "none";
	public int TurnNumber { get; private set; }
	[Sync] public bool OnlineSessionActive { get; private set; }
	[Sync] public string OnlineBlueConnectionId { get; private set; } = "";
	[Sync] public string OnlineRedConnectionId { get; private set; } = "";
	[Sync] public string OnlineBlueDisplayName { get; private set; } = "";
	[Sync] public string OnlineRedDisplayName { get; private set; } = "";
	public RogueChessOnlineRole LocalOnlineRole => GetOnlineRole( Connection.Local );
	public bool CanLocalPlayerActThisTurn => CanConnectionActForTeam( Connection.Local, CurrentTeam );
	public string OnlineStatusText => GetOnlineStatusText();
	public string OnlineRosterText => GetOnlineRosterText();

	public IReadOnlyList<UnitData> Units => units;
	public IReadOnlyList<CardType> BlueHand => blueHand;
	public IReadOnlyList<CardType> RedHand => redHand;
	public IReadOnlyList<string> CombatLog => combatLog;
	public IReadOnlyList<UnitType?> BlueArmyChoices => blueArmyChoices;
	public IReadOnlyList<UnitType?> RedArmyChoices => redArmyChoices;
	// Returned from a property getter (not the static field) so the pool refreshes on hot-reload —
	// static field initializers don't re-run on hot-reload, which stranded the old Buddy-in-pool list.
	public IReadOnlyList<UnitType> UnitPoolTypes => new[] { UnitType.Shooter, UnitType.Tank, UnitType.Hacker };
	public int ArmyFilledSlotsFor( RogueChessTeam team ) => GetArmyChoices( team ).Count( type => type.HasValue );
	public bool IsArmyComplete( RogueChessTeam team ) => ArmyFilledSlotsFor( team ) == ArmySlotCount;
	// Heroes = non-Commander, non-Buddy picks. The pick counter tracks this out of HeroSlotCount (3).
	public int HeroCountFor( RogueChessTeam team ) => GetArmyChoices( team ).Count( type => type is UnitType.Shooter or UnitType.Tank or UnitType.Hacker );

	// Blue-facing shorthands kept for the offline single-army builder UI.
	public int BlueArmyFilledSlots => ArmyFilledSlotsFor( RogueChessTeam.Blue );
	public bool IsBlueArmyComplete => IsArmyComplete( RogueChessTeam.Blue );
	public int HeroCount => HeroCountFor( RogueChessTeam.Blue );
	public int HeroPickTarget => HeroSlotCount;

	readonly List<UnitData> units = new();
	readonly List<CardType> blueHand = new();
	readonly List<CardType> redHand = new();
	readonly List<string> combatLog = new();
	readonly List<UnitType?> blueArmyChoices = CreateEmptyArmyChoices();
	readonly List<UnitType?> redArmyChoices = DefaultArmyChoices.Select( type => (UnitType?)type ).ToList();
	readonly List<BoardEffect> boardEffects = new();
	readonly List<DyingUnitVisual> dyingUnitVisuals = new();

	int nextUnitId = 1;
	int blueDeckIndex;
	int redDeckIndex;
	int blueOwnTurns; // per-team own-turn counter for the half-rate draw
	int redOwnTurns;
	bool isRunningAi;
	// True while the minimax is simulating candidate turns. Suppresses presentation side effects
	// (sounds, hit/death visuals) that Snapshot/Restore can't undo, so the search doesn't smear
	// hundreds of simulated attacks onto the live board.
	bool isSimulating;
	string lastAiAction = "";
	float nextAiActionTime;
	float nextBackgroundSoundRetryTime;
	int turnsSinceLastAttack;
	bool attackMessageShownThisTurn;
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
		if ( ShouldRouteUiActionsToHost )
		{
			RequestRestartMatch();
			return;
		}

		RestartMatchForConnection( Connection.Local );
	}

	void RestartMatchForConnection( Connection caller )
	{
		// Online restart re-opens army building for both players instead of instantly redeploying, so each
		// side can rebuild and ready up again.
		if ( OnlineSessionActive )
		{
			RestartOnlineForConnection( caller );
			return;
		}

		if ( !IsBlueArmyComplete )
		{
			StatusMessage = $"Choose {HeroSlotCount} heroes to join your Commander before starting the match.";
			MarkDirty();
			return;
		}

		if ( RejectIfConnectionCannotConfigureArmy( caller, RogueChessTeam.Blue ) )
			return;

		// Red army choices can survive hot-reload/play-session changes because the list is created once.
		// Reset it from DefaultArmyChoices before every match so the AI army always follows the current
		// 3-hero / 4-Buddy / 1-Commander rule.
		ResetRedArmyChoicesToDefault();

		BeginMatch();
	}

	// Shared match build used by both the offline start and the online both-ready start. Assumes the blue and
	// red army choices are already finalized: offline resets Red to the AI preset first; online keeps each
	// player's own built army.
	void BeginMatch()
	{
		MatchStarted = true;
		BlueArmyReady = false;
		RedArmyReady = false;
		units.Clear();
		blueHand.Clear();
		redHand.Clear();
		combatLog.Clear();
		boardEffects.Clear();
		dyingUnitVisuals.Clear();

		nextUnitId = 1;
		blueDeckIndex = 0;
		redDeckIndex = 0;
		blueOwnTurns = 0;
		redOwnTurns = 0;
		BlueScrap = 0;
		RedScrap = 0;
		Winner = null;
		WinReason = null;
		LastAttackStatus = "none";
		TurnNumber = 0;
		IsDraw = false;
		turnsSinceLastAttack = 0;
		previousTileByUnit.Clear();

		AddStartingArmy( RogueChessTeam.Blue );
		AddStartingArmy( RogueChessTeam.Red );

		StartTurn( RogueChessTeam.Blue );
	}

	void PrepareArmyBuilder()
	{
		MatchStarted = false;
		BlueArmyReady = false;
		RedArmyReady = false;
		units.Clear();
		blueHand.Clear();
		redHand.Clear();
		combatLog.Clear();
		boardEffects.Clear();
		dyingUnitVisuals.Clear();
		ClearSelection();
		ResetRedArmyChoicesToDefault();
		LastAttackStatus = "none";
		StatusMessage = $"Choose {HeroSlotCount} heroes to join your Commander.";
		MarkDirty();
	}


	void ResetRedArmyChoicesToDefault()
	{
		redArmyChoices.Clear();
		redArmyChoices.AddRange( DefaultArmyChoices.Select( type => (UnitType?)type ) );
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
		if ( ShouldRouteUiActionsToHost )
		{
			RequestStartMatchFromArmyBuilder();
			return;
		}

		HandleStartPressedForConnection( Connection.Local );
	}

	public void SelectArmyBuilderUnit( RogueChessTeam team, UnitType unitType )
	{
		if ( unitType == UnitType.Commander )
			return;

		if ( ShouldRouteUiActionsToHost )
		{
			// Update the local highlight immediately, then let the host apply the authoritative selection.
			SetSelectedArmyBuilderUnit( team, unitType );
			MarkDirty();
			RequestSelectArmyBuilderUnit( team, unitType );
			return;
		}

		SelectArmyBuilderUnitForConnection( Connection.Local, team, unitType );
	}

	void SelectArmyBuilderUnitForConnection( Connection caller, RogueChessTeam team, UnitType unitType )
	{
		if ( unitType == UnitType.Commander )
			return;

		if ( RejectIfConnectionCannotConfigureArmy( caller, team ) )
			return;

		SetSelectedArmyBuilderUnit( team, unitType );
		StatusMessage = $"{unitType} selected for {TeamName( team )} army slots.";
		MarkDirty();
	}

	public void SetArmySlot( RogueChessTeam team, int index )
	{
		if ( ShouldRouteUiActionsToHost )
		{
			RequestSetArmySlot( team, index );
			return;
		}

		SetArmySlotForConnection( Connection.Local, team, index );
	}

	void SetArmySlotForConnection( Connection caller, RogueChessTeam team, int index )
	{
		if ( MatchStarted || index < 0 || index >= ArmySlotCount || index == CommanderArmySlotIndex )
			return;

		if ( RejectIfConnectionCannotConfigureArmy( caller, team ) )
			return;

		var choices = GetArmyChoices( team );
		var selectedUnit = GetSelectedArmyBuilderUnit( team );
		var current = choices[index];

		// Clicking a slot that already holds the selected hero toggles it off.
		if ( current == selectedUnit )
		{
			ClearArmySlotForConnection( caller, team, index );
			return;
		}

		// Only heroes are placed manually; Buddies are auto-filled. Cap at HeroSlotCount unless this
		// click is swapping the hero already in this slot.
		bool slotHasHero = current is UnitType.Shooter or UnitType.Tank or UnitType.Hacker;
		if ( !slotHasHero && HeroCountFor( team ) >= HeroSlotCount )
		{
			StatusMessage = $"{TeamName( team )} already chose {HeroSlotCount} heroes. Remove one to change picks.";
			MarkDirty();
			return;
		}

		choices[index] = selectedUnit;
		NormalizeAutoBuddies( team );
		// Changing the army invalidates a prior ready-up for that side.
		SetArmyReady( team, false );

		StatusMessage = HeroCountFor( team ) >= HeroSlotCount
			? $"{TeamName( team )}: {HeroSlotCount} heroes chosen — remaining slots auto-filled with Buddies. Start Game unlocked."
			: $"{TeamName( team )} hero placed ({HeroCountFor( team )}/{HeroSlotCount}). Choose {HeroSlotCount - HeroCountFor( team )} more.";
		MarkDirty();
	}

	public void ClearArmySlot( RogueChessTeam team, int index )
	{
		if ( ShouldRouteUiActionsToHost )
		{
			RequestClearArmySlot( team, index );
			return;
		}

		ClearArmySlotForConnection( Connection.Local, team, index );
	}

	void ClearArmySlotForConnection( Connection caller, RogueChessTeam team, int index )
	{
		var choices = GetArmyChoices( team );
		if ( MatchStarted || index < 0 || index >= ArmySlotCount || index == CommanderArmySlotIndex || !choices[index].HasValue )
			return;

		if ( RejectIfConnectionCannotConfigureArmy( caller, team ) )
			return;

		var removed = choices[index].Value;
		// Auto-filled Buddies aren't manually removable — only heroes are.
		if ( removed == UnitType.Buddy )
			return;

		choices[index] = null;
		NormalizeAutoBuddies( team ); // drops the auto-Buddies now that we're below HeroSlotCount
		SetArmyReady( team, false );
		StatusMessage = $"{TeamName( team )} removed {removed}. Choose {HeroSlotCount - HeroCountFor( team )} more hero(es).";
		MarkDirty();
	}

	// Keep the auto-Buddy fill consistent: once all 3 heroes are placed, fill the remaining empty
	// non-Commander slots with Buddy; otherwise clear any auto-Buddies so those slots reopen for picking.
	void NormalizeAutoBuddies( RogueChessTeam team )
	{
		var choices = GetArmyChoices( team );
		bool full = HeroCountFor( team ) >= HeroSlotCount;
		for ( var i = 0; i < ArmySlotCount; i++ )
		{
			if ( i == CommanderArmySlotIndex )
				continue;

			if ( full && !choices[i].HasValue )
				choices[i] = UnitType.Buddy;
			else if ( !full && choices[i] == UnitType.Buddy )
				choices[i] = null;
		}
	}

	public void SetMode( RogueChessMode mode )
	{
		Mode = mode;

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
		if ( ShouldRouteUiActionsToHost )
		{
			RequestHandleTurnButton();
			return;
		}

		HandleTurnButtonForConnection( Connection.Local );
	}

	void HandleTurnButtonForConnection( Connection caller )
	{
		if ( Mode == RogueChessMode.ComputerVsComputer )
		{
			StopPvcGame();
			return;
		}

		EndTurnForConnection( caller );
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
		if ( ShouldRouteUiActionsToHost )
		{
			RequestEndTurn();
			return;
		}

		EndTurnForConnection( Connection.Local );
	}

	void EndTurnForConnection( Connection caller )
	{
		if ( !MatchStarted || IsGameOver || IsCurrentAiTurn() )
			return;

		if ( RejectIfConnectionCannotActForTeam( caller, CurrentTeam ) )
			return;

		AdvanceTurn( true );
	}

	public void ClickTile( int x, int y )
	{
		if ( ShouldRouteUiActionsToHost )
		{
			RequestClickTile( x, y );
			return;
		}

		ClickTileForConnection( Connection.Local, x, y );
	}

	void ClickTileForConnection( Connection caller, int x, int y )
	{
		if ( !MatchStarted || IsGameOver || IsCurrentAiTurn() )
			return;

		if ( RejectIfConnectionCannotActForTeam( caller, CurrentTeam ) )
			return;

		var pos = new GridPos( x, y );
		var unit = GetUnitAt( pos );

		if ( SelectedCardIndex >= 0 )
		{
			var hand = GetHand( CurrentTeam );
			var selectedCard = SelectedCardIndex < hand.Count ? hand[SelectedCardIndex] : (CardType?)null;
			var cardSource = unit;
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

			// If a card is selected and the player clicks a non-card target, do not trap the
			// board in card targeting mode. Cancel the card and let the normal unit click
			// logic below handle the same click when possible. This prevents the match from
			// feeling frozen after an invalid card target.
			SelectedCardIndex = -1;
		}
		if ( unit is not null && unit.Team == CurrentTeam )
		{
			SelectedUnitId = unit.Id;
			StatusMessage = unit.CanActThisTurn
				? $"{TeamName( CurrentTeam )} selected {unit.Type}."
				: $"{TeamName( CurrentTeam )} {unit.Type} was just built and can act next turn.";
			MarkDirty();
			return;
		}

		var selected = GetSelectedUnit();
		if ( selected is null )
		{
			ClearSelection();
			StatusMessage = "Select one of your units first.";
			MarkDirty();
			return;
		}

		// Move phase: up to 3 different units may each move; per-unit eligibility is enforced by IsLegalMove.
		if ( unit is null && IsLegalMove( selected, pos ) )
		{
			MoveUnit( selected, pos );
			return;
		}

		// Attack phase: exactly 1 attack, only by a unit that did not move this turn (enforced by IsLegalAttack).
		if ( unit is not null && unit.Team != CurrentTeam && IsLegalAttack( selected, unit ) )
		{
			AttackUnit( selected, unit );
			return;
		}

		StatusMessage = selected.Id == attackerUnitIdThisTurn
			? $"{selected.Type} already attacked this turn."
			: movedUnitIdsThisTurn.Contains( selected.Id )
				? $"{selected.Type} already moved this turn and cannot attack."
				: MovesUsedThisTurn >= MoveSlotsPerTurn && !AttackUsedThisTurn
					? "All 3 moves used. You may still attack with a unit that hasn't moved."
					: "No legal action for that tile.";
		MarkDirty();
	}

	public void SelectCard( RogueChessTeam team, int index )
	{
		if ( ShouldRouteUiActionsToHost )
		{
			RequestSelectCard( team, index );
			return;
		}

		SelectCardForConnection( Connection.Local, team, index );
	}

	void SelectCardForConnection( Connection caller, RogueChessTeam team, int index )
	{
		if ( !MatchStarted || IsGameOver || IsCurrentAiTurn() )
			return;

		if ( RejectIfConnectionCannotActForTeam( caller, team ) )
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

		if ( !CanPlayCard )
		{
			StatusMessage = UnitActionSpent
				? "You already played a card after your unit action this turn."
				: "You already played a card before your action. Take your unit action, then you may play one more.";
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
		StatusMessage = card == CardType.Push
			? "Selected Push. First select one of your ready units, then choose an adjacent enemy to push away."
			: $"Selected {CardData.All[card].Name}; choose a valid target.";
		MarkDirty();
	}

	public void CancelSelectedCard()
	{
		if ( ShouldRouteUiActionsToHost )
		{
			RequestCancelSelectedCard();
			return;
		}

		CancelSelectedCardForConnection( Connection.Local );
	}

	void CancelSelectedCardForConnection( Connection caller )
	{
		if ( SelectedCardIndex < 0 )
			return;

		if ( RejectIfConnectionCannotActForTeam( caller, CurrentTeam ) )
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
		else
		{
			var selected = GetSelectedUnit();
			if ( selected is not null )
			{
				// IsLegalMove / IsLegalAttack already enforce per-unit move-phase eligibility.
				if ( unit is null && IsLegalMove( selected, pos ) )
					classes.Add( "legal-move" );

				if ( unit is not null && unit.Team != CurrentTeam && IsLegalAttack( selected, unit ) )
					classes.Add( "legal-attack" );
			}
		}

		return string.Join( " ", classes );
	}

	// TEMP DEBUG: reports the selected unit's actual GridPos and the GridPos of every tile that will
	// be highlighted as a legal move, so an in-editor screenshot can be compared against the visual grid.
	public string DebugMoveInfo()
	{
		var selected = GetSelectedUnit();
		if ( selected is null )
			return "DBG: no unit selected";

		var moves = GetLegalMoves( selected )
			.OrderBy( p => p.Y ).ThenBy( p => p.X )
			.Select( p => $"({p.X},{p.Y})" );

		return $"DBG sel={selected.ShortName}({selected.Position.X},{selected.Position.Y}) moves: {string.Join( " ", moves )}";
	}

	public string GetTileCueText( int x, int y )
	{
		var pos = new GridPos( x, y );
		var unit = GetUnitAt( pos );

		if ( SelectedCardIndex >= 0 && IsValidSelectedCardTarget( pos ) )
			return "CARD";

		var selected = GetSelectedUnit();
		if ( selected is null )
			return "";

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

		if ( !CanPlayCard || index < 0 || index >= hand.Count || GetScrap( team ) < CardData.All[hand[index]].Cost || team != CurrentTeam || IsComputerControlled( team ) )
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
			UnitType.Hacker => "Automatically disables every adjacent enemy on arrival for their next turn (can't repeat the same target twice in a row). Can still attack normally.",
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
		MovesUsedThisTurn = 0;
		AttackUsedThisTurn = false;
		attackMessageShownThisTurn = false;
		attackerUnitIdThisTurn = -1;
		movedUnitIdsThisTurn.Clear();
		CardPlayedBeforeAction = false;
		CardPlayedAfterAction = false;
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
		if ( IsComputerControlled( team ) )
			scrapGain += 1;
		SetScrap( team, GetScrap( team ) + scrapGain );

		// Half-rate draw: each team draws only every OTHER of its own turns (deterministic, no luck).
		var ownTurns = team == RogueChessTeam.Blue ? ++blueOwnTurns : ++redOwnTurns;
		if ( ownTurns % 2 == 1 )
			DrawCard( team );

		StatusMessage = $"{TeamName( team )} turn. Gained {scrapGain} Scrap.";
		ScheduleAiIfNeeded();
		MarkDirty();
	}

	void ScheduleAiIfNeeded()
	{
		// StartTurn calls this, and StartTurn runs hundreds of times inside the minimax search.
		// nextAiActionTime is not part of Snapshot/Restore, so scheduling during a simulated turn
		// would corrupt the real AI-turn clock. Never touch the clock while simulating.
		if ( isSimulating )
			return;

		if ( IsCurrentAiTurn() )
			nextAiActionTime = Time.Now + AiActionDelay;
	}

	// Computer-vs-Computer plays at a steady, watchable pace; otherwise a single responsive rule-based pace.
	float AiActionDelay => Mode == RogueChessMode.ComputerVsComputer ? 1.0f : 0.3f;

	// Both current tiers use the former "Hard" rule-based behavior: attack before collecting Scrap, and
	// reinforce eagerly. (Normal will diverge once the minimax AI is ported.)
	bool AiAttacksBeforeResources => true;
	bool AiBuildsEagerly => true;

	// Two tiers only; the chosen Difficulty always applies (no per-side randomization).
	public AiDifficulty DifficultyFor( RogueChessTeam team )
	{
		return Difficulty;
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
		MovesUsedThisTurn++;
		movedUnitIdsThisTurn.Add( unit.Id );
		ClearSelection();
		ApplyHackerDisable( unit );
		PlayUnitMoveSound();
		StatusMessage = $"{TeamName( unit.Team )} {unit.Type} moved.";
		if ( isRunningAi )
			lastAiAction = $"{TeamName( unit.Team )} computer moved {unit.Type} from {FormatPos( from )} to {FormatPos( destination )}.";

		MarkDirty();
	}

	// Rider on a Hacker's move: disable every adjacent enemy for its next turn.
	// Skips a target this same Hacker disabled on its immediately preceding action (no parking on one unit).
	void ApplyHackerDisable( UnitData hacker )
	{
		if ( hacker.Type != UnitType.Hacker )
			return;

		foreach ( var direction in GridPos.CardinalDirections )
		{
			var target = GetUnitAt( hacker.Position.Offset( direction ) );
			if ( target is null || target.Team == hacker.Team )
				continue;

			// TurnNumber increments once per team-turn, so a team's previous turn is TurnNumber - 2.
			if ( target.LastDisabledByUnitId == hacker.Id && target.LastDisabledOnTurn == TurnNumber - 2 )
				continue;

			target.DisabledTurns = 1;
			target.LastDisabledByUnitId = hacker.Id;
			target.LastDisabledOnTurn = TurnNumber;

			AppendCombatMessage( $"{FormatUnitAtPosition( hacker )} disabled {FormatUnitAtPosition( target )}." );
		}
	}

	void AttackUnit( UnitData attacker, UnitData defender )
	{
		turnsSinceLastAttack = 0;
		if ( !isSimulating )
			LastAttackStatus = FormatAttackStatus( attacker, defender );

		var damage = attacker.CurrentDamage;
		var shieldAbsorb = Math.Min( defender.Shield, damage );
		defender.Shield -= shieldAbsorb;
		damage -= shieldAbsorb;
		defender.Health -= damage;
		var targetHp = Math.Max( defender.Health, 0 );

		AppendCombatMessage( $"{FormatUnitAtPosition( attacker )} hit {FormatUnitAtPosition( defender )} for {damage} damage. {FormatUnitAtPosition( defender )} has {targetHp}/{defender.MaxHealth} HP.", true );

		attacker.FocusDamageBonus = 0;
		attacker.SprintMoveBonus = 0;
		AttackUsedThisTurn = true;
		attackerUnitIdThisTurn = attacker.Id;
		ClearSelection();

		if ( defender.Health <= 0 )
		{
			PlayUnitDeathSound();
			AddDyingUnitVisual( defender );
			units.Remove( defender );

			if ( defender.Type == UnitType.Commander )
			{
				AppendCombatMessage( $"{FormatUnitAtPosition( defender )} destroyed. {TeamName( attacker.Team )} wins.", true );
				SetWinner( attacker.Team, "knockout", $"{TeamName( attacker.Team )} wins! The enemy Commander is down." );
			}
			else
			{
				AppendCombatMessage( $"{FormatUnitAtPosition( attacker )} destroyed {FormatUnitAtPosition( defender )}.", true );
				StatusMessage = $"{TeamName( attacker.Team )} {attacker.Type} defeated {defender.Type}.";
			}

			// Second win condition: reducing a side to just its (living) Commander wins for the other side.
			CheckEliminationVictory();
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

	string FormatAttackStatus( UnitData attacker, UnitData defender )
	{
		return $"{FormatUnitAtPosition( attacker )} attack {FormatUnitAtPosition( defender )}";
	}

	string FormatUnitAtPosition( UnitData unit )
	{
		return $"{TeamName( unit.Team )} {unit.Type}{FormatPos( unit.Position )}";
	}

	void AppendCombatMessage( string message, bool isAttackMessage = false )
	{
		if ( isSimulating )
			return;

		if ( attackMessageShownThisTurn && !isAttackMessage )
			return;

		if ( isAttackMessage )
			attackMessageShownThisTurn = true;

		combatLog.Clear();
		combatLog.Add( message );
	}

	void AddHitEffect( GridPos pos )
	{
		if ( isSimulating )
			return; // don't smear simulated-attack visuals onto the live board

		boardEffects.RemoveAll( effect => effect.Position == pos && effect.EffectType == "hit" );
		boardEffects.Add( new BoardEffect( pos, "hit", Time.Now, Time.Now + HitEffectDuration ) );
	}

	void AddDyingUnitVisual( UnitData unit )
	{
		if ( isSimulating )
			return; // don't smear simulated-death visuals onto the live board

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
		if ( isSimulating )
			return; // no audio while the minimax is simulating candidate turns

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
		if ( SelectedCardIndex < 0 || SelectedCardIndex >= hand.Count || !CanPlayCard )
			return false;

		var card = hand[SelectedCardIndex];
		// Capture which window this play consumes before the effect runs (the action flag is unchanged by cards).
		var afterAction = UnitActionSpent;
		if ( !TryPlayCardAt( CurrentTeam, card, pos ) )
			return false;

		hand.RemoveAt( SelectedCardIndex );
		if ( afterAction )
			CardPlayedAfterAction = true;
		else
			CardPlayedBeforeAction = true;
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
				// Buffs the next attack, so it must go on a unit still eligible to attack this turn (a non-mover).
				if ( target is null || target.Team != team || !CanUnitAttack( target ) )
					return false;

				target.FocusDamageBonus = 1;
				return true;

			case CardType.Sprint:
				// Buffs a move, so it must go on a unit still eligible to move this turn.
				if ( target is null || target.Team != team || !CanUnitMove( target ) )
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

			case CardType.Reboot:
				if ( target is null || target.Team != team || !( target.DisabledTurns > 0 || target.IsDisabledThisTurn ) )
					return false;

				target.DisabledTurns = 0;
				target.IsDisabledThisTurn = false;
				target.CanActThisTurn = true;
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
			CardType.Focus => target is not null && target.Team == team && CanUnitAttack( target ),
			CardType.Sprint => target is not null && target.Team == team && CanUnitMove( target ),
			CardType.BuildBuddy => target is null && pos.IsInsideBoard && commander is not null && commander.Position.ManhattanDistance( pos ) == 1,
			CardType.Repair => target is not null && target.Team == team && target.Health < target.MaxHealth,
			CardType.Reboot => target is not null && target.Team == team && ( target.DisabledTurns > 0 || target.IsDisabledThisTurn ),
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
		// Still gated to a unit that did NOT move this turn (one attack per turn).
		return CanUnitAttack( attacker )
			&& IsInAttackRangeWithLineOfSight( attacker, defender );
	}

	bool IsInAttackRangeWithLineOfSight( UnitData attacker, UnitData defender )
	{
		return defender.Team != attacker.Team
			&& HasAttackPath( attacker, attacker.Position, defender.Position );
	}

	bool HasAttackPath( UnitData attacker, GridPos from, GridPos to )
	{
		if ( attacker.AttackRange <= 0 )
			return false;

		var deltaX = to.X - from.X;
		var deltaY = to.Y - from.Y;
		var absX = Math.Abs( deltaX );
		var absY = Math.Abs( deltaY );
		var distance = Math.Max( absX, absY );

		if ( distance < 1 || distance > attacker.AttackRange )
			return false;

		if ( attacker.Type == UnitType.Tank )
		{
			var isOrthogonal = deltaX == 0 || deltaY == 0;
			return isOrthogonal && HasLineOfSight( from, to );
		}

		if ( attacker.Type == UnitType.Shooter )
		{
			var isDiagonal = absX == absY;
			return isDiagonal && HasLineOfSight( from, to );
		}

		return HasLineOfSight( from, to );
	}

	// Chess-style sliding movement, gated by turn eligibility. Delegates the tile geometry to
	// ComputeSlidingDestinations so movement rules stay in one place (used by legality + AI).
	List<GridPos> GetLegalMoves( UnitData unit )
	{
		if ( !CanUnitMove( unit ) )
			return new List<GridPos>();

		return ComputeSlidingDestinations( unit );
	}

	// Pure movement geometry (turn-phase agnostic): sliding along the unit's allowed directions up to its
	// move distance, stopping BEFORE the first occupied tile. Plus the Shooter's bent Option B path.
	List<GridPos> ComputeSlidingDestinations( UnitData unit )
	{
		var dests = new HashSet<GridPos>();

		// Option A / standard slide along the unit's direction set.
		foreach ( var direction in unit.MoveDirectionSet )
		{
			var cursor = unit.Position;
			for ( var step = 1; step <= unit.CurrentMoveRange; step++ )
			{
				cursor = cursor.Offset( direction );
				if ( !cursor.IsInsideBoard || GetUnitAt( cursor ) is not null )
					break;
				dests.Add( cursor );
			}
		}

		// Option B (Shooter only): one orthogonal step onto an EMPTY tile, then an optional diagonal
		// slide of up to 2 (+ Sprint bonus) more tiles from there. Breaks the diagonal color-lock.
		if ( unit.Type == UnitType.Shooter )
		{
			foreach ( var odir in GridPos.CardinalDirections )
			{
				var mid = unit.Position.Offset( odir );
				if ( !mid.IsInsideBoard || GetUnitAt( mid ) is not null )
					continue; // ortho step must land empty; otherwise this bent option is unavailable

				dests.Add( mid ); // may stop right after the single orthogonal step

				var bentDiag = 2 + unit.SprintMoveBonus;
				foreach ( var ddir in GridPos.DiagonalDirections )
				{
					var cursor = mid;
					for ( var step = 1; step <= bentDiag; step++ )
					{
						cursor = cursor.Offset( ddir );
						if ( !cursor.IsInsideBoard || GetUnitAt( cursor ) is not null )
							break;
						dests.Add( cursor );
					}
				}
			}
		}

		dests.Remove( unit.Position );
		return dests.ToList();
	}

	bool HasLineOfSight( GridPos from, GridPos to )
	{
		var deltaX = to.X - from.X;
		var deltaY = to.Y - from.Y;
		var absX = Math.Abs( deltaX );
		var absY = Math.Abs( deltaY );
		var distance = Math.Max( absX, absY );

		if ( distance <= 1 )
			return true;

		// Attacks may only trace along a real straight board line:
		// horizontal, vertical, or exact diagonal. This is especially important for Shooters:
		// a diagonal shot from (1,0) to (4,3) must check the blockers at (2,1) and (3,2).
		var isStraightLine = deltaX == 0 || deltaY == 0 || absX == absY;
		if ( !isStraightLine )
			return false;

		var step = new GridPos( Math.Sign( deltaX ), Math.Sign( deltaY ) );
		var current = from.Offset( step );

		// Every intermediate tile must be empty. The target tile is intentionally excluded here,
		// because it is allowed to contain the enemy being attacked.
		for ( var i = 1; i < distance; i++ )
		{
			if ( GetUnitAt( current ) is not null )
				return false;

			current = current.Offset( step );
		}

		return true;
	}

	// Minimax ("Normal" difficulty) configuration + in-engine per-turn decision timing.
	public const int MinimaxPlies = 6;
	public double LastAiDecisionMs { get; private set; }
	public double WorstAiDecisionMs { get; private set; }

	void RunAiTurn()
	{
		if ( !IsCurrentAiTurn() )
			return;

		var team = CurrentTeam;
		isRunningAi = true;
		lastAiAction = $"{TeamName( team )} computer had no legal move.";

		try
		{
			if ( Difficulty == AiDifficulty.Normal )
			{
				// Smart AI: minimax + alpha-beta chooses and applies the full turn (attack/hold + reserve
				// moves + delegated card plays). Timed so the cost is visible in-engine.
				var sw = System.Diagnostics.Stopwatch.StartNew();
				ChooseBestTurn( MinimaxPlies, team )();
				sw.Stop();
				LastAiDecisionMs = sw.Elapsed.TotalMilliseconds;
				if ( LastAiDecisionMs > WorstAiDecisionMs )
					WorstAiDecisionMs = LastAiDecisionMs;
			}
			else
			{
				TryAiPlayCard( team );      // card window before the move phase
				AiAttackPhase( team );      // the single attack (an already-in-range unit that hasn't moved)
				if ( !IsGameOver )
					AiMovePhase( team );    // advance up to 3 different reserve/other units
				if ( !IsGameOver )
					TryAiPlayCard( team );  // card window after all actions
			}
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
		if ( !CanPlayCard )
			return;

		if ( TryAiPlayReboot( team ) ) return;

		// Defensive priority: when the Commander is under an immediate (Chebyshev) threat, shield or shove
		// that threat BEFORE spending the window on offense (Focus).
		if ( CommanderThreatened( team ) )
		{
			if ( TryAiPlayGuardCommander( team ) ) return;
			if ( TryAiPlayPush( team ) ) return;
		}

		if ( TryAiPlayFocus( team ) ) return;
		if ( TryAiPlayGuard( team ) ) return;
		// BuildBuddy/Repair are excluded from the hero card pool — no AI triggers for them.
		TryAiPlaySprint( team );
	}

	// Heroes-correct threat test: some enemy has this team's Commander within its Chebyshev attack range.
	bool CommanderThreatened( RogueChessTeam team )
	{
		var commander = GetCommander( team );
		return commander is not null && units.Any( e => IsInAttackRangeWithLineOfSight( e, commander ) );
	}

	// Guard the Commander directly when it's under an immediate threat (checked ahead of Focus).
	bool TryAiPlayGuardCommander( RogueChessTeam team )
	{
		var commander = GetCommander( team );
		if ( commander is null )
			return false;

		if ( AiPlayCard( team, CardType.Guard, commander.Position ) )
		{
			lastAiAction = $"{TeamName( team )} computer guarded its threatened Commander.";
			return true;
		}

		return false;
	}

	// If an enemy currently threatens our Commander, shove it to a tile farther from the Commander
	// using an adjacent friendly unit as the push source.
	bool TryAiPlayPush( RogueChessTeam team )
	{
		var commander = GetCommander( team );
		if ( commander is null )
			return false;

		var threats = units.Where( e => IsInAttackRangeWithLineOfSight( e, commander ) ).ToList();

		foreach ( var enemy in threats )
		{
			foreach ( var src in units.Where( u => u.Team == team && u.CanActThisTurn && u.Position.ManhattanDistance( enemy.Position ) == 1 ).ToList() )
			{
				if ( TryGetPushDestinationFromSource( src, enemy, out var dest )
					&& dest.ChebyshevDistance( commander.Position ) > enemy.Position.ChebyshevDistance( commander.Position ) )
				{
					SelectedUnitId = src.Id; // Push resolves against the selected source unit
					if ( AiPlayCard( team, CardType.Push, enemy.Position ) )
					{
						SelectedUnitId = -1;
						lastAiAction = $"{TeamName( team )} computer pushed a threat off its Commander.";
						return true;
					}
					SelectedUnitId = -1;
				}
			}
		}

		return false;
	}

	// If a friendly unit is disabled this turn, spend Reboot to free the most valuable one
	// (Commander first, otherwise highest MaxHealth type) so it can act normally again.
	bool TryAiPlayReboot( RogueChessTeam team )
	{
		var disabled = units
			.Where( unit => unit.Team == team && ( unit.IsDisabledThisTurn || unit.DisabledTurns > 0 ) )
			.ToList();
		if ( disabled.Count == 0 )
			return false;

		var target = disabled.FirstOrDefault( unit => unit.Type == UnitType.Commander )
			?? disabled.OrderByDescending( unit => unit.MaxHealth ).First();

		if ( AiPlayCard( team, CardType.Reboot, target.Position ) )
		{
			lastAiAction = $"{TeamName( team )} computer rebooted {target.Type}.";
			return true;
		}

		return false;
	}

	bool AiPlayCard( RogueChessTeam team, CardType card, GridPos pos )
	{
		if ( !CanPlayCard )
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
		return IsInAttackRangeWithLineOfSight( enemy, unit );
	}

	// Attack phase: the single attack per turn, taken by an already-in-range unit that has NOT moved
	// this turn. Priority: enemy Commander, then a guaranteed kill, then any enemy in range.
	void AiAttackPhase( RogueChessTeam team )
	{
		var enemyTeam = OtherTeam( team );
		var enemyCommander = GetCommander( enemyTeam );
		if ( enemyCommander is null )
			return;

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

		TryAiAttackAnyEnemy( team, enemyTeam );
	}

	// Move phase: advance up to MoveSlotsPerTurn DIFFERENT units, prioritizing those farthest from the
	// enemy Commander (reserves not yet engaged) so the back rank actually moves up.
	void AiMovePhase( RogueChessTeam team )
	{
		var enemyCommander = GetCommander( OtherTeam( team ) );
		if ( enemyCommander is null )
			return;

		TryAiSprintIntoRange( team ); // close the last tile into attack range if Sprint enables it

		while ( MovesUsedThisTurn < MoveSlotsPerTurn && !IsGameOver )
		{
			if ( !TryAiAdvanceOneUnit( team, enemyCommander.Position ) )
				break;
		}
	}

	// If a unit can't reach any enemy's attack tile this turn by a normal move but COULD with Sprint's
	// +1 slide (it's exactly one tile short), play Sprint and move it into range this turn.
	bool TryAiSprintIntoRange( RogueChessTeam team )
	{
		if ( !CanPlayCard || GetScrap( team ) < CardData.All[CardType.Sprint].Cost || !GetHand( team ).Contains( CardType.Sprint ) )
			return false;

		foreach ( var u in units.Where( x => x.Team == team && x.AttackRange > 0 && CanUnitMove( x ) ).ToList() )
		{
			// Already able to reach an in-range tile without Sprint? Then Sprint isn't the missing piece.
			if ( GetLegalMoves( u ).Any( dest => EnemyWithin( u, dest, team ) ) )
				continue;

			u.SprintMoveBonus = 1;
			var sprintDest = GetLegalMoves( u ).Cast<GridPos?>().FirstOrDefault( dest => EnemyWithin( u, dest.Value, team ) );
			u.SprintMoveBonus = 0;
			if ( sprintDest is null )
				continue;

			if ( AiPlayCard( team, CardType.Sprint, u.Position ) )
			{
				MoveUnit( u, sprintDest.Value ); // +1 slide now applied; lands within attack range for next turn
				lastAiAction = $"{TeamName( team )} computer sprinted {u.Type} into attack range.";
				return true;
			}
			return false;
		}

		return false;
	}

	bool EnemyWithin( UnitData attacker, GridPos from, RogueChessTeam team )
		=> units.Any( e => e.Team != team && HasAttackPath( attacker, from, e.Position ) );

	bool TryAiAdvanceOneUnit( RogueChessTeam team, GridPos enemyCommanderPos )
	{
		// For each eligible mover, find its best forward tile that is SAFE (no enemy can hit it there
		// without the unit being able to hit back), plus its best forward tile ignoring safety.
		var candidates = units
			.Where( unit => unit.Team == team && CanUnitMove( unit ) )
			.Select( unit =>
			{
				var progress = GetAiProgressMoves( unit, enemyCommanderPos )
					.OrderBy( pos => pos.ManhattanDistance( enemyCommanderPos ) )
					.ToList();
				return new
				{
					Unit = unit,
					Safe = progress.Where( pos => IsSafeDestination( unit, pos ) ).Cast<GridPos?>().FirstOrDefault(),
					Any = progress.Cast<GridPos?>().FirstOrDefault(),
					Distance = unit.Position.ManhattanDistance( enemyCommanderPos )
				};
			} )
			.Where( x => x.Any is not null )
			.ToList();

		// 1. Prefer a SAFE advance, farthest-from-front first (pull up un-engaged reserves).
		var safe = candidates.Where( x => x.Safe is not null ).OrderByDescending( x => x.Distance ).FirstOrDefault();
		if ( safe is not null )
		{
			MoveUnit( safe.Unit, safe.Safe.Value );
			return true;
		}

		// 2. No safe advance exists. Don't freeze — but keep the Commander OUT of unanswerable range:
		//    advance the farthest NON-Commander into its best (unsafe) tile instead.
		var nonCmd = candidates.Where( x => x.Unit.Type != UnitType.Commander ).OrderByDescending( x => x.Distance ).FirstOrDefault();
		if ( nonCmd is not null )
		{
			MoveUnit( nonCmd.Unit, nonCmd.Any.Value );
			return true;
		}

		// 3. Only the Commander could advance, and only into danger -> hold it back rather than walk it
		//    into a ranged threat it can't answer. Try a Hacker disable step instead.
		var enemyTeam = OtherTeam( team );
		foreach ( var hacker in units.Where( unit => unit.Team == team && unit.Type == UnitType.Hacker ).ToList() )
		{
			if ( TryMoveHackerAdjacentTo( hacker, units.Where( unit => unit.Team == enemyTeam ), enemyCommanderPos ) )
				return true;
		}

		return false;
	}

	// Single-turn positional safety: a destination is unsafe if some enemy can attack the unit there
	// while the unit could NOT retaliate against that enemy from the same tile (e.g. a range-1 unit
	// stepping into a range-3 Shooter's zone). No lookahead — just this-tile threat vs retaliation.
	bool IsSafeDestination( UnitData unit, GridPos dest )
	{
		foreach ( var enemy in units.Where( u => u.Team != unit.Team ) )
		{
			var enemyCanHit = enemy.AttackRange > 0
				&& HasAttackPath( enemy, enemy.Position, dest );
			if ( !enemyCanHit )
				continue;

			var canRetaliate = unit.AttackRange > 0
				&& HasAttackPath( unit, dest, enemy.Position );
			if ( !canRetaliate )
				return false;
		}

		return true;
	}

	// Move a Hacker onto a legal tile adjacent to one of the given targets (its disable rider fires on the move).
	bool TryMoveHackerAdjacentTo( UnitData hacker, IEnumerable<UnitData> targets, GridPos preferNear )
	{
		var targetList = targets.ToList();
		if ( targetList.Count == 0 )
			return false;

		var move = GetLegalMoves( hacker )
			.Where( pos => targetList.Any( target => pos.ManhattanDistance( target.Position ) == 1 ) )
			.OrderBy( pos => pos.ManhattanDistance( preferNear ) )
			.Cast<GridPos?>()
			.FirstOrDefault();

		if ( move is null )
			return false;

		MoveUnit( hacker, move.Value );
		return true;
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
			SetWinner( RogueChessTeam.Red, "knockout", $"{TeamName( RogueChessTeam.Red )} wins! The enemy Commander is down." );

		if ( redCommander is not null && redCommander.Health <= 0 )
			SetWinner( RogueChessTeam.Blue, "knockout", $"{TeamName( RogueChessTeam.Blue )} wins! The enemy Commander is down." );
	}

	void SetWinner( RogueChessTeam team, string reason, string message )
	{
		Winner = team;
		WinReason = reason;
		StatusMessage = message;
	}

	// Second win condition ("surrender win"): a side's Commander is alive but every other unit of
	// that side is gone -> the other side wins. Knockout (dead Commander) always takes precedence.
	void CheckEliminationVictory()
	{
		if ( Winner is not null )
			return;

		foreach ( var team in new[] { RogueChessTeam.Blue, RogueChessTeam.Red } )
		{
			var commander = GetCommander( team );
			if ( commander is null || commander.Health <= 0 )
				continue;

			if ( units.Count( unit => unit.Team == team && unit.Type != UnitType.Commander ) == 0 )
			{
				var winner = OtherTeam( team );
				SetWinner( winner, "elimination", $"{TeamName( winner )} wins! {TeamName( team )} has been wiped out." );
				return;
			}
		}
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
		BroadcastStateIfHost();
	}

	readonly record struct BoardEffect( GridPos Position, string EffectType, float StartTime, float EndTime );
	readonly record struct DyingUnitVisual( GridPos Position, UnitType UnitType, RogueChessTeam Team, float StartTime, float EndTime );
}
