using System;
using System.Collections.Generic;
using System.Linq;

namespace StrategyGame
{
	public sealed partial class RogueChessGameComponent
	{
		// ===================== EVALUATION (v1.1 weights) =====================
		public double Eval( RogueChessTeam me )
		{
			var opp = OtherTeam( me );
			var myCmd = GetCommander( me );
			var oppCmd = GetCommander( opp );

			bool myLost = myCmd is null || myCmd.Health <= 0 || units.Count( u => u.Team == me && u.Type != UnitType.Commander ) == 0;
			bool oppLost = oppCmd is null || oppCmd.Health <= 0 || units.Count( u => u.Team == opp && u.Type != UnitType.Commander ) == 0;
			if ( oppLost && !myLost ) return 100000;
			if ( myLost ) return -100000;

			double myHp = units.Where( u => u.Team == me ).Sum( u => u.Health );
			double oppHp = units.Where( u => u.Team == opp ).Sum( u => u.Health );

			int oppThreatMyCmd = ThreatCountAgainst( me );  // opponent units threatening MY commander
			int myThreatOppCmd = ThreatCountAgainst( opp ); // my units threatening OPP commander

			int myMob = MobilityFor( me );
			int oppMob = MobilityFor( opp );

			int myDisabled = units.Count( u => u.Team == me && u.DisabledTurns > 0 );
			int oppDisabled = units.Count( u => u.Team == opp && u.DisabledTurns > 0 );

			// Light card-economy tiebreaker (small, additive): prefer more Scrap and a fuller hand.
			int myScrap = GetScrap( me ), oppScrap = GetScrap( opp );
			int myHand = ( me == RogueChessTeam.Blue ? blueHand : redHand ).Count;
			int oppHand = ( opp == RogueChessTeam.Blue ? blueHand : redHand ).Count;

			// Threat term — CORRECTED sign (the only version going forward): reward threatening the enemy
			// Commander, penalize your own Commander being threatened. The original spec text had this
			// inverted (it rewarded being threatened); validation proved that made the AI play badly, so the
			// inverted/literal path has been removed and must not be reintroduced.
			return 1.0 * ( myHp - oppHp )
				+ 4.0 * ( myThreatOppCmd - oppThreatMyCmd )
				+ 0.15 * ( myMob - oppMob )
				+ 2.5 * ( oppDisabled - myDisabled )
				+ 0.15 * ( myScrap - oppScrap )
				+ 0.15 * ( myHand - oppHand );
		}

		// Count enemy units that (from their current tile) have commanderTeam's Commander within attack range.
		int ThreatCountAgainst( RogueChessTeam commanderTeam )
		{
			var cmd = GetCommander( commanderTeam );
			if ( cmd is null )
				return 0;
			return units.Count( e => e.Team != commanderTeam && e.AttackRange > 0
				&& e.Position.ChebyshevDistance( cmd.Position ) <= e.AttackRange );
		}

		// Turn-phase-agnostic mobility proxy for the "legal action count" term: sliding-move destinations
		// (per direction, stopping before blockers) + attackable enemies, for every unit of the team.
		int MobilityFor( RogueChessTeam team )
		{
			int count = 0;
			foreach ( var u in units.Where( x => x.Team == team ) )
			{
				// Shared movement geometry (includes the Shooter's bent Option B), turn-phase agnostic.
				count += ComputeSlidingDestinations( u ).Count;
				if ( u.AttackRange > 0 )
					count += units.Count( e => e.Team != team && u.Position.ChebyshevDistance( e.Position ) <= u.AttackRange );
			}
			return count;
		}

		// ===================== CANDIDATE FULL-TURNS =====================
		// A candidate turn is a closure that applies a full team-turn (attack + reserve moves) to live state.
		// Reserve movement is delegated to the rule-based AiMovePhase (the hybrid part); the SEARCH decides
		// which attack to make (or to hold), and the opponent (min node) picks its best reply.
		List<(string tag, Action play)> CandidateTurns( RogueChessTeam team )
		{
			// Build the searched attack/move BODIES first, then wrap each with the rule-based card windows.
			var bodies = new List<(string tag, Action body)>();

			// Attack options this turn (fresh turn -> CanUnitAttack holds). Ordered: hit Commander first, then lowest-HP target.
			var attacks = GenerateLegalAttackActions( team )
				.Select( a => (a, def: GetUnitById( a.TargetId )) )
				.Where( x => x.def is not null )
				.OrderByDescending( x => x.def.Type == UnitType.Commander )
				.ThenBy( x => x.def.Health )
				.Take( 6 )
				.ToList();

			foreach ( var (a, def) in attacks )
			{
				int atkId = a.UnitId, defId = a.TargetId;
				bodies.Add( ($"atk u{atkId}->u{defId}", () =>
				{
					AttackUnit( GetUnitById( atkId ), GetUnitById( defId ) );
					AiMovePhase( team );
				}
				) );
			}

			bodies.Add( ("rule", () => { AiAttackPhase( team ); AiMovePhase( team ); }) );
			bodies.Add( ("advance", () => { AiMovePhase( team ); }) );
			bodies.Add( ("hold", () => { }) );

			// HYBRID card delegation: each candidate turn also runs the rule-based TryAiPlayCard in the
			// before window and the after window — cards are played deterministically alongside the searched
			// move/attack, adding no branching (same pattern as delegating reserve moves to AiMovePhase).
			var list = new List<(string, Action)>();
			foreach ( var (tag, body) in bodies )
			{
				var b = body;
				list.Add( (tag, () =>
				{
					TryAiPlayCard( team );                 // card window before the move phase
					b();                                    // searched attack + rule-based reserve moves
					if ( !IsGameOver ) TryAiPlayCard( team ); // card window after all actions
				}
				) );
			}

			return list;
		}

		// ===================== MINIMAX + ALPHA-BETA (full-turn granularity) =====================
		// Instrumentation.
		public static long MinimaxNodes;

		double Minimax( int plies, double alpha, double beta, RogueChessTeam me )
		{
			MinimaxNodes++;
			if ( IsGameOver || plies <= 0 )
				return Eval( me );

			var toMove = CurrentTeam;
			bool maximizing = toMove == me;
			double best = maximizing ? double.NegativeInfinity : double.PositiveInfinity;

			foreach ( var (_, play) in CandidateTurns( toMove ) )
			{
				var snap = Snapshot();
				play();
				if ( !IsGameOver )
					AdvanceTurn( false ); // end this side's turn -> opponent's StartTurn (fresh counters)
				double v = Minimax( plies - 1, alpha, beta, me );
				Restore( snap );

				if ( maximizing )
				{
					if ( v > best ) best = v;
					if ( best > alpha ) alpha = best;
				}
				else
				{
					if ( v < best ) best = v;
					if ( best < beta ) beta = best;
				}
				if ( alpha >= beta )
					break; // cutoff
			}

			return best;
		}

		// Choose the best full-turn for the current side and return the closure that applies it.
		Action ChooseBestTurn( int plies, RogueChessTeam me )
		{
			double alpha = double.NegativeInfinity, beta = double.PositiveInfinity, best = double.NegativeInfinity;
			var cands = CandidateTurns( me );
			Action bestPlay = cands.Count > 0 ? cands[0].play : () => { };

			foreach ( var (_, play) in cands )
			{
				var snap = Snapshot();
				play();
				if ( !IsGameOver )
					AdvanceTurn( false );
				double v = Minimax( plies - 1, alpha, beta, me );
				Restore( snap );

				if ( v > best ) { best = v; bestPlay = play; }
				if ( best > alpha ) alpha = best;
			}
			return bestPlay;
		}

		// Play one real minimax turn for the current side (applies chosen turn + advances), like RunAiTurn.
		public void RunMinimaxTurn( int plies )
		{
			var me = CurrentTeam;
			var play = ChooseBestTurn( plies, me );
			play();
			if ( !IsGameOver )
				AdvanceTurn( false );
		}

		// Count this team's non-Hacker units left in an "unanswerable" threat (an enemy can hit them,
		// they cannot hit that enemy back). Used to measure blunder avoidance.
		public int UnanswerablyExposedUnits( RogueChessTeam team )
		{
			int n = 0;
			foreach ( var u in units.Where( x => x.Team == team && x.Type != UnitType.Hacker ) )
			{
				bool exposed = units.Any( e => e.Team != team && e.AttackRange > 0
					&& e.Position.ChebyshevDistance( u.Position ) <= e.AttackRange
					&& !( u.AttackRange > 0 && u.Position.ChebyshevDistance( e.Position ) <= u.AttackRange ) );
				if ( exposed ) n++;
			}
			return n;
		}
	}
}
