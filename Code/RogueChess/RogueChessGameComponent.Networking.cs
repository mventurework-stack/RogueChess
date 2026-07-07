using Sandbox;
using System;

namespace StrategyGame;

public sealed partial class RogueChessGameComponent : Component.INetworkListener
{
	bool ShouldRouteUiActionsToHost => OnlineSessionActive;
	bool IsOnlineTurnGateEnabled => OnlineSessionActive && Mode == RogueChessMode.PlayerVsPlayer;

	public void OnActive( Connection connection )
	{
		if ( connection is null )
			return;

		OnlineSessionActive = true;
		var connectionId = GetConnectionId( connection );
		if ( string.IsNullOrWhiteSpace( connectionId ) )
			return;

		if ( string.IsNullOrWhiteSpace( OnlineBlueConnectionId ) )
		{
			OnlineBlueConnectionId = connectionId;
			OnlineBlueDisplayName = GetConnectionDisplayName( connection );
			StatusMessage = $"{OnlineBlueDisplayName} joined as Blue Player.";
			MarkDirty();
			return;
		}

		if ( OnlineBlueConnectionId == connectionId )
		{
			OnlineBlueDisplayName = GetConnectionDisplayName( connection );
			MarkDirty();
			return;
		}

		if ( string.IsNullOrWhiteSpace( OnlineRedConnectionId ) )
		{
			OnlineRedConnectionId = connectionId;
			OnlineRedDisplayName = GetConnectionDisplayName( connection );
			StatusMessage = $"{OnlineRedDisplayName} joined as Red Player.";
			MarkDirty();
			return;
		}

		if ( OnlineRedConnectionId == connectionId )
		{
			OnlineRedDisplayName = GetConnectionDisplayName( connection );
			MarkDirty();
			return;
		}

		StatusMessage = $"{GetConnectionDisplayName( connection )} joined as Spectator.";
		MarkDirty();
	}

	public void OnDisconnected( Connection connection )
	{
		var connectionId = GetConnectionId( connection );
		if ( string.IsNullOrWhiteSpace( connectionId ) )
			return;

		if ( OnlineBlueConnectionId == connectionId )
		{
			OnlineBlueConnectionId = "";
			OnlineBlueDisplayName = "";
			StatusMessage = "Blue Player left the online session.";
			ClearSelection();
			MarkDirty();
			return;
		}

		if ( OnlineRedConnectionId == connectionId )
		{
			OnlineRedConnectionId = "";
			OnlineRedDisplayName = "";
			StatusMessage = "Red Player left the online session.";
			ClearSelection();
			MarkDirty();
		}
	}

	public RogueChessOnlineRole GetOnlineRole( Connection connection )
	{
		if ( !OnlineSessionActive )
			return RogueChessOnlineRole.Offline;

		var connectionId = GetConnectionId( connection );
		if ( string.IsNullOrWhiteSpace( connectionId ) )
			return RogueChessOnlineRole.Spectator;

		if ( connectionId == OnlineBlueConnectionId )
			return RogueChessOnlineRole.BluePlayer;

		if ( connectionId == OnlineRedConnectionId )
			return RogueChessOnlineRole.RedPlayer;

		return RogueChessOnlineRole.Spectator;
	}

	public bool CanConnectionActForTeam( Connection connection, RogueChessTeam team )
	{
		if ( !IsOnlineTurnGateEnabled )
			return true;

		var role = GetOnlineRole( connection );
		return team switch
		{
			RogueChessTeam.Blue => role == RogueChessOnlineRole.BluePlayer,
			RogueChessTeam.Red => role == RogueChessOnlineRole.RedPlayer,
			_ => false
		};
	}

	bool CanConnectionConfigureBlueArmy( Connection connection )
	{
		return !OnlineSessionActive || GetOnlineRole( connection ) == RogueChessOnlineRole.BluePlayer;
	}

	bool RejectIfConnectionCannotConfigureBlueArmy( Connection connection )
	{
		if ( CanConnectionConfigureBlueArmy( connection ) )
			return false;

		StatusMessage = "Only the Blue Player can configure the online army.";
		MarkDirty();
		return true;
	}

	bool RejectIfConnectionCannotActForTeam( Connection connection, RogueChessTeam team )
	{
		if ( CanConnectionActForTeam( connection, team ) )
			return false;

		StatusMessage = $"{GetOnlineRoleLabel( GetOnlineRole( connection ) )} cannot act for {TeamName( team )}.";
		MarkDirty();
		return true;
	}

	string GetOnlineStatusText()
	{
		if ( !OnlineSessionActive )
			return "Online: Inactive";

		return $"Online role: {GetOnlineRoleLabel( LocalOnlineRole )}";
	}

	string GetOnlineRosterText()
	{
		if ( !OnlineSessionActive )
			return "Online: Inactive";

		var blueName = string.IsNullOrWhiteSpace( OnlineBlueDisplayName ) ? "Waiting" : OnlineBlueDisplayName;
		var redName = string.IsNullOrWhiteSpace( OnlineRedDisplayName ) ? "Waiting" : OnlineRedDisplayName;
		return $"Blue: {blueName} | Red: {redName}";
	}

	static string GetConnectionId( Connection connection )
	{
		return connection?.Id.ToString() ?? "";
	}

	static string GetConnectionDisplayName( Connection connection )
	{
		return string.IsNullOrWhiteSpace( connection?.DisplayName ) ? "Player" : connection.DisplayName;
	}

	static string GetOnlineRoleLabel( RogueChessOnlineRole role )
	{
		return role switch
		{
			RogueChessOnlineRole.BluePlayer => "Blue Player",
			RogueChessOnlineRole.RedPlayer => "Red Player",
			RogueChessOnlineRole.Spectator => "Spectator",
			_ => "Offline"
		};
	}

	[Rpc.Host]
	public void RequestStartMatchFromArmyBuilder()
	{
		RestartMatchForConnection( Rpc.Caller );
	}

	[Rpc.Host]
	public void RequestRestartMatch()
	{
		RestartMatchForConnection( Rpc.Caller );
	}

	[Rpc.Host]
	public void RequestSelectArmyBuilderUnit( UnitType unitType )
	{
		SelectArmyBuilderUnitForConnection( Rpc.Caller, unitType );
	}

	[Rpc.Host]
	public void RequestSetBlueArmySlot( int index )
	{
		SetBlueArmySlotForConnection( Rpc.Caller, index );
	}

	[Rpc.Host]
	public void RequestClearBlueArmySlot( int index )
	{
		ClearBlueArmySlotForConnection( Rpc.Caller, index );
	}

	[Rpc.Host]
	public void RequestHandleTurnButton()
	{
		HandleTurnButtonForConnection( Rpc.Caller );
	}

	[Rpc.Host]
	public void RequestEndTurn()
	{
		EndTurnForConnection( Rpc.Caller );
	}

	[Rpc.Host]
	public void RequestClickTile( int x, int y )
	{
		ClickTileForConnection( Rpc.Caller, x, y );
	}

	[Rpc.Host]
	public void RequestSelectCard( RogueChessTeam team, int index )
	{
		SelectCardForConnection( Rpc.Caller, team, index );
	}

	[Rpc.Host]
	public void RequestCancelSelectedCard()
	{
		CancelSelectedCardForConnection( Rpc.Caller );
	}
}
