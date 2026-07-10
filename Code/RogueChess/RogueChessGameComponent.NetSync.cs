using Sandbox;
using System.Collections.Generic;
using System.Linq;

namespace StrategyGame;

public sealed partial class RogueChessGameComponent
{
	// Serializable snapshot of a single unit's networked state.
	public sealed class NetUnit
	{
		public int Id { get; set; }
		public RogueChessTeam Team { get; set; }
		public UnitType Type { get; set; }
		public int X { get; set; }
		public int Y { get; set; }
		public int Health { get; set; }
		public int Shield { get; set; }
		public int FocusDamageBonus { get; set; }
		public int SprintMoveBonus { get; set; }
		public int DisabledTurns { get; set; }
		public bool IsDisabledThisTurn { get; set; }
		public bool CanActThisTurn { get; set; }
	}

	// Full authoritative game state the host broadcasts to clients.
	public sealed class NetState
	{
		public List<NetUnit> Units { get; set; } = new();
		public List<CardType> BlueHand { get; set; } = new();
		public List<CardType> RedHand { get; set; } = new();
		// Army-builder slot contents (null = empty slot) so each player sees their own picks and the
		// opponent's readiness while configuring.
		public List<UnitType?> BlueArmy { get; set; } = new();
		public List<UnitType?> RedArmy { get; set; } = new();
		public bool BlueArmyReady { get; set; }
		public bool RedArmyReady { get; set; }
		public int BlueScrap { get; set; }
		public int RedScrap { get; set; }
		public RogueChessTeam CurrentTeam { get; set; }
		public int TurnNumber { get; set; }
		public int MovesUsedThisTurn { get; set; }
		public bool AttackUsedThisTurn { get; set; }
		public bool MatchStarted { get; set; }
		public bool IsDraw { get; set; }
		public RogueChessTeam? Winner { get; set; }
		public string WinReason { get; set; }
		public string StatusMessage { get; set; }
	}

	// Host authority: serialize the live game state and push it to every client.
	// Never runs on clients, and never during minimax simulation (isSimulating snapshots
	// the board hundreds of times and would flood the network with intermediate states).
	void BroadcastStateIfHost()
	{
		if ( !OnlineSessionActive || !Networking.IsActive || !Networking.IsHost )
			return;

		if ( isSimulating )
			return;

		var state = new NetState
		{
			Units = units.Select( unit => new NetUnit
			{
				Id = unit.Id,
				Team = unit.Team,
				Type = unit.Type,
				X = unit.Position.X,
				Y = unit.Position.Y,
				Health = unit.Health,
				Shield = unit.Shield,
				FocusDamageBonus = unit.FocusDamageBonus,
				SprintMoveBonus = unit.SprintMoveBonus,
				DisabledTurns = unit.DisabledTurns,
				IsDisabledThisTurn = unit.IsDisabledThisTurn,
				CanActThisTurn = unit.CanActThisTurn
			} ).ToList(),
			BlueHand = blueHand.ToList(),
			RedHand = redHand.ToList(),
			BlueArmy = blueArmyChoices.ToList(),
			RedArmy = redArmyChoices.ToList(),
			BlueArmyReady = BlueArmyReady,
			RedArmyReady = RedArmyReady,
			BlueScrap = BlueScrap,
			RedScrap = RedScrap,
			CurrentTeam = CurrentTeam,
			TurnNumber = TurnNumber,
			MovesUsedThisTurn = MovesUsedThisTurn,
			AttackUsedThisTurn = AttackUsedThisTurn,
			MatchStarted = MatchStarted,
			IsDraw = IsDraw,
			Winner = Winner,
			WinReason = WinReason,
			StatusMessage = StatusMessage
		};

		BroadcastGameState( Json.Serialize( state ) );
	}

	// Applied on clients only: the host is authoritative and ignores its own broadcast.
	[Rpc.Broadcast]
	void BroadcastGameState( string json )
	{
		if ( Networking.IsHost )
			return;

		var state = Json.Deserialize<NetState>( json );
		if ( state is null )
			return;

		units.Clear();
		foreach ( var netUnit in state.Units )
		{
			var unit = new UnitData( netUnit.Id, netUnit.Team, netUnit.Type, new GridPos( netUnit.X, netUnit.Y ) )
			{
				Health = netUnit.Health,
				Shield = netUnit.Shield,
				FocusDamageBonus = netUnit.FocusDamageBonus,
				SprintMoveBonus = netUnit.SprintMoveBonus,
				DisabledTurns = netUnit.DisabledTurns,
				IsDisabledThisTurn = netUnit.IsDisabledThisTurn,
				CanActThisTurn = netUnit.CanActThisTurn
			};

			units.Add( unit );
		}

		blueHand.Clear();
		blueHand.AddRange( state.BlueHand );
		redHand.Clear();
		redHand.AddRange( state.RedHand );

		blueArmyChoices.Clear();
		blueArmyChoices.AddRange( state.BlueArmy );
		redArmyChoices.Clear();
		redArmyChoices.AddRange( state.RedArmy );
		BlueArmyReady = state.BlueArmyReady;
		RedArmyReady = state.RedArmyReady;

		BlueScrap = state.BlueScrap;
		RedScrap = state.RedScrap;
		CurrentTeam = state.CurrentTeam;
		TurnNumber = state.TurnNumber;
		MovesUsedThisTurn = state.MovesUsedThisTurn;
		AttackUsedThisTurn = state.AttackUsedThisTurn;
		MatchStarted = state.MatchStarted;
		IsDraw = state.IsDraw;
		Winner = state.Winner;
		WinReason = state.WinReason;
		StatusMessage = state.StatusMessage;

		UiVersion++;
		uiPanel?.StateHasChanged();
	}
}
