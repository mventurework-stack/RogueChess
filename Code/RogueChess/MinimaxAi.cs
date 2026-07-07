using System.Collections.Generic;
using System.Linq;

namespace StrategyGame
{
	// ---- Generic action representation ----
	// Only Move and Attack are generated in this pass, but the Card kind exists NOW so that search
	// code iterates a single generic action list and card actions can be added later without restructuring.
	public enum GameActionKind { Move, Attack, Card }

	public readonly struct GameAction
	{
		public readonly GameActionKind Kind;
		public readonly int UnitId;        // mover / attacker / (future) card source
		public readonly GridPos Destination; // Move target tile
		public readonly int TargetId;      // Attack: defender id
		public readonly CardType Card;     // Card: which card (future)
		public readonly GridPos TargetPos; // Card: target position (future)

		GameAction( GameActionKind kind, int unitId, GridPos destination, int targetId, CardType card, GridPos targetPos )
		{
			Kind = kind; UnitId = unitId; Destination = destination; TargetId = targetId; Card = card; TargetPos = targetPos;
		}

		public static GameAction Move( int unitId, GridPos destination ) => new( GameActionKind.Move, unitId, destination, -1, default, default );
		public static GameAction Attack( int attackerId, int targetId ) => new( GameActionKind.Attack, attackerId, default, targetId, default, default );
		// sourceUnitId carries the shove-source for Push (and is -1 for cards that need no source).
		public static GameAction CardAt( CardType card, GridPos targetPos, int sourceUnitId = -1 ) => new( GameActionKind.Card, sourceUnitId, default, -1, card, targetPos );

		public override string ToString() => Kind switch
		{
			GameActionKind.Move => $"Move(u{UnitId} -> ({Destination.X},{Destination.Y}))",
			GameActionKind.Attack => $"Attack(u{UnitId} -> u{TargetId})",
			_ => $"Card({Card} @ ({TargetPos.X},{TargetPos.Y}))"
		};
	}

	// Deep-copy of full game state for Snapshot/Restore. Includes hand/Scrap/deck fields even though
	// cards are currently disabled, so re-enabling cards won't require rebuilding this layer.
	public sealed class GameSnapshot
	{
		public struct U
		{
			public int Id; public RogueChessTeam Team; public UnitType Type; public GridPos Pos;
			public int Health, Shield, Focus, Sprint, DisabledTurns, LastDisabledOnTurn;
			public int? LastDisabledBy; public bool IsDisabledThisTurn, CanAct;
		}

		public readonly List<U> Units = new();
		public readonly List<CardType> BlueHand = new(), RedHand = new();
		public int BlueScrap, RedScrap, BlueDeckIndex, RedDeckIndex, BlueOwnTurns, RedOwnTurns;
		public RogueChessTeam CurrentTeam;
		public int TurnNumber, TurnsSinceLastAttack, NextUnitId, AttackerUnitId, MovesUsed, SelectedUnitId, SelectedCardIndex;
		public bool AttackUsed, CardBefore, CardAfter, IsDraw;
		public RogueChessTeam? Winner; public string WinReason;
		public readonly List<int> MovedUnitIds = new();
		public readonly Dictionary<int, GridPos> PrevTile = new();
	}

	public sealed partial class RogueChessGameComponent
	{
		UnitData GetUnitById( int id ) => units.FirstOrDefault( u => u.Id == id );

		// Generic single-action dispatch: apply exactly one GameAction to live state by calling the
		// existing rule methods. This is the Snapshot -> apply one action -> evaluate -> Restore hook.
		public void ApplyAction( GameAction action )
		{
			switch ( action.Kind )
			{
				case GameActionKind.Move:
					MoveUnit( GetUnitById( action.UnitId ), action.Destination );
					break;
				case GameActionKind.Attack:
					AttackUnit( GetUnitById( action.UnitId ), GetUnitById( action.TargetId ) );
					break;
				case GameActionKind.Card:
					// Cards resolve through the existing ApplyCardEffect. Push needs a selected source unit.
					if ( action.UnitId >= 0 )
						SelectedUnitId = action.UnitId;
					ApplyCardEffect( CurrentTeam, action.Card, action.TargetPos );
					break;
			}
		}

		public GameSnapshot Snapshot()
		{
			var s = new GameSnapshot();
			foreach ( var u in units )
				s.Units.Add( new GameSnapshot.U
				{
					Id = u.Id, Team = u.Team, Type = u.Type, Pos = u.Position,
					Health = u.Health, Shield = u.Shield, Focus = u.FocusDamageBonus, Sprint = u.SprintMoveBonus,
					DisabledTurns = u.DisabledTurns, IsDisabledThisTurn = u.IsDisabledThisTurn,
					LastDisabledBy = u.LastDisabledByUnitId, LastDisabledOnTurn = u.LastDisabledOnTurn, CanAct = u.CanActThisTurn
				} );

			s.BlueHand.AddRange( blueHand );
			s.RedHand.AddRange( redHand );
			s.BlueScrap = BlueScrap; s.RedScrap = RedScrap; s.BlueDeckIndex = blueDeckIndex; s.RedDeckIndex = redDeckIndex;
			s.BlueOwnTurns = blueOwnTurns; s.RedOwnTurns = redOwnTurns; // affect the half-rate draw timing
			s.CurrentTeam = CurrentTeam; s.TurnNumber = TurnNumber; s.TurnsSinceLastAttack = turnsSinceLastAttack;
			s.NextUnitId = nextUnitId; s.AttackerUnitId = attackerUnitIdThisTurn; s.MovesUsed = MovesUsedThisTurn;
			s.AttackUsed = AttackUsedThisTurn; s.CardBefore = CardPlayedBeforeAction; s.CardAfter = CardPlayedAfterAction;
			s.IsDraw = IsDraw; s.Winner = Winner; s.WinReason = WinReason;
			s.SelectedUnitId = SelectedUnitId; s.SelectedCardIndex = SelectedCardIndex;
			s.MovedUnitIds.AddRange( movedUnitIdsThisTurn );
			foreach ( var kv in previousTileByUnit ) s.PrevTile[kv.Key] = kv.Value;
			return s;
		}

		public void Restore( GameSnapshot s )
		{
			units.Clear();
			foreach ( var r in s.Units )
				units.Add( new UnitData( r.Id, r.Team, r.Type, r.Pos )
				{
					Health = r.Health, Shield = r.Shield, FocusDamageBonus = r.Focus, SprintMoveBonus = r.Sprint,
					DisabledTurns = r.DisabledTurns, IsDisabledThisTurn = r.IsDisabledThisTurn,
					LastDisabledByUnitId = r.LastDisabledBy, LastDisabledOnTurn = r.LastDisabledOnTurn, CanActThisTurn = r.CanAct
				} );

			blueHand.Clear(); blueHand.AddRange( s.BlueHand );
			redHand.Clear(); redHand.AddRange( s.RedHand );
			BlueScrap = s.BlueScrap; RedScrap = s.RedScrap; blueDeckIndex = s.BlueDeckIndex; redDeckIndex = s.RedDeckIndex;
			blueOwnTurns = s.BlueOwnTurns; redOwnTurns = s.RedOwnTurns;
			CurrentTeam = s.CurrentTeam; TurnNumber = s.TurnNumber; turnsSinceLastAttack = s.TurnsSinceLastAttack;
			nextUnitId = s.NextUnitId; attackerUnitIdThisTurn = s.AttackerUnitId; MovesUsedThisTurn = s.MovesUsed;
			AttackUsedThisTurn = s.AttackUsed; CardPlayedBeforeAction = s.CardBefore; CardPlayedAfterAction = s.CardAfter;
			IsDraw = s.IsDraw; Winner = s.Winner; WinReason = s.WinReason;
			SelectedUnitId = s.SelectedUnitId; SelectedCardIndex = s.SelectedCardIndex;
			movedUnitIdsThisTurn.Clear(); foreach ( var id in s.MovedUnitIds ) movedUnitIdsThisTurn.Add( id );
			previousTileByUnit.Clear(); foreach ( var kv in s.PrevTile ) previousTileByUnit[kv.Key] = kv.Value;
		}

		// ---- Candidate action generators ----

		// Legal MoveActions via the new sliding movement (GetLegalMoves does per-direction ray casting,
		// stopping before blockers — the heroes movement, not the old radius model).
		public List<GameAction> GenerateLegalMoveActions( RogueChessTeam team )
		{
			var list = new List<GameAction>();
			foreach ( var u in units.Where( x => x.Team == team ) )
				foreach ( var dest in GetLegalMoves( u ) )
					list.Add( GameAction.Move( u.Id, dest ) );
			return list;
		}

		// Legal AttackActions via the Chebyshev range check already implemented in IsLegalAttack.
		public List<GameAction> GenerateLegalAttackActions( RogueChessTeam team )
		{
			var list = new List<GameAction>();
			foreach ( var a in units.Where( x => x.Team == team ) )
				foreach ( var d in units.Where( x => x.Team != team ) )
					if ( IsLegalAttack( a, d ) )
						list.Add( GameAction.Attack( a.Id, d.Id ) );
			return list;
		}

		// Legal CardActions: for each distinct affordable card in hand, one action per valid target.
		// Cards are only playable on your own turn and within an open card window (CanPlayCard).
		public List<GameAction> GenerateLegalCardActions( RogueChessTeam team )
		{
			var list = new List<GameAction>();
			if ( team != CurrentTeam || !CanPlayCard )
				return list;

			var hand = team == RogueChessTeam.Blue ? blueHand : redHand;
			foreach ( var card in hand.Distinct() )
			{
				if ( GetScrap( team ) < CardData.All[card].Cost )
					continue;

				switch ( card )
				{
					case CardType.Guard:
						foreach ( var u in units.Where( x => x.Team == team ) )
							list.Add( GameAction.CardAt( CardType.Guard, u.Position ) );
						break;
					case CardType.Focus:
						foreach ( var u in units.Where( x => x.Team == team && CanUnitAttack( x ) ) )
							list.Add( GameAction.CardAt( CardType.Focus, u.Position ) );
						break;
					case CardType.Sprint:
						foreach ( var u in units.Where( x => x.Team == team && CanUnitMove( x ) ) )
							list.Add( GameAction.CardAt( CardType.Sprint, u.Position ) );
						break;
					case CardType.Reboot:
						foreach ( var u in units.Where( x => x.Team == team && ( x.DisabledTurns > 0 || x.IsDisabledThisTurn ) ) )
							list.Add( GameAction.CardAt( CardType.Reboot, u.Position ) );
						break;
					case CardType.Push:
						// One action per pushable enemy (uses the first friendly source that can shove it).
						foreach ( var enemy in units.Where( x => x.Team != team ) )
							foreach ( var src in units.Where( x => x.Team == team && x.CanActThisTurn && x.Position.ManhattanDistance( enemy.Position ) == 1 ) )
								if ( TryGetPushDestinationFromSource( src, enemy, out _ ) )
								{
									list.Add( GameAction.CardAt( CardType.Push, enemy.Position, src.Id ) );
									break;
								}
						break;
				}
			}
			return list;
		}

		// Full candidate list the search iterates over — moves + attacks + (future) cards.
		public List<GameAction> GenerateAllActions( RogueChessTeam team )
		{
			var all = GenerateLegalMoveActions( team );
			all.AddRange( GenerateLegalAttackActions( team ) );
			all.AddRange( GenerateLegalCardActions( team ) );
			return all;
		}
	}
}
