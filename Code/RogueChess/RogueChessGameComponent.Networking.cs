using Sandbox;
using Sandbox.Network;
using System;
using System.Threading.Tasks;

namespace StrategyGame;

public sealed partial class RogueChessGameComponent : Component.INetworkListener
{
	const string RogueChessGameIdent = "roguechessalpha";
	const string RogueChessOnlineName = "Rogue Chess Online PVP";
	const int RogueChessOnlineMaxPlayers = 8;

	bool ShouldRouteUiActionsToHost => OnlineSessionActive && Networking.IsClient && !Networking.IsHost;
	bool IsOnlineTurnGateEnabled => OnlineSessionActive && Networking.IsActive && Mode == RogueChessMode.PlayerVsPlayer;

	public bool CanHostOnlineGame => !Networking.IsActive && !Networking.IsConnecting;
	public bool CanJoinOnlineGame => !Networking.IsActive && !Networking.IsConnecting;
	public bool CanLeaveOnlineGame => OnlineSessionActive || Networking.IsActive || Networking.IsConnecting;

	public void HostOnlineGame()
	{
		if ( !CanHostOnlineGame )
		{
			StatusMessage = "An online session is already active or connecting.";
			MarkDirty();
			return;
		}

		Mode = RogueChessMode.PlayerVsPlayer;
		OnlineSessionActive = true;
		Networking.ServerName = RogueChessOnlineName;
		Networking.SetData( "roguechess_version", "v3-phase-2" );
		Networking.CreateLobby( new LobbyConfig
		{
			Name = RogueChessOnlineName,
			MaxPlayers = RogueChessOnlineMaxPlayers,
			Privacy = LobbyPrivacy.Public,
			DestroyWhenHostLeaves = true,
			AutoSwitchToBestHost = false,
			Hidden = false
		} );

		EnsureNetworkedGameObject();
		TryAssignLocalHostAsBlue();
		StatusMessage = "Hosting online PVP. Waiting for opponent.";
		MarkDirty();
	}

	public async void JoinOnlineGame()
	{
		await JoinOnlineGameAsync();
	}

	async Task JoinOnlineGameAsync()
	{
		if ( !CanJoinOnlineGame )
		{
			StatusMessage = "An online session is already active or connecting.";
			MarkDirty();
			return;
		}

		Mode = RogueChessMode.PlayerVsPlayer;
		StatusMessage = "Looking for a Rogue Chess online game.";
		MarkDirty();

		try
		{
			var joined = await Networking.JoinBestLobby( RogueChessGameIdent );
			if ( joined )
			{
				OnlineSessionActive = true;
				StatusMessage = "Joining Rogue Chess online PVP.";
			}
			else
			{
				OnlineSessionActive = false;
				StatusMessage = "No Rogue Chess online game found. Host a game or use connect local.";
			}
		}
		catch ( Exception exception )
		{
			OnlineSessionActive = false;
			StatusMessage = $"Join failed: {exception.Message}";
		}

		MarkDirty();
	}

	public void LeaveOnlineGame()
	{
		if ( !CanLeaveOnlineGame )
		{
			StatusMessage = "Online is already offline.";
			MarkDirty();
			return;
		}

		if ( Networking.IsActive || Networking.IsConnecting )
			Networking.Disconnect();

		ClearOnlineSessionState( "Left online session." );
	}

	public void OnActive( Connection connection )
	{
		if ( connection is null )
			return;

		OnlineSessionActive = true;
		Mode = RogueChessMode.PlayerVsPlayer;
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
			OnlineSessionActive = !string.IsNullOrWhiteSpace( OnlineRedConnectionId );
			ClearSelection();
			MarkDirty();
			return;
		}

		if ( OnlineRedConnectionId == connectionId )
		{
			OnlineRedConnectionId = "";
			OnlineRedDisplayName = "";
			StatusMessage = "Red Player left the online session.";
			OnlineSessionActive = !string.IsNullOrWhiteSpace( OnlineBlueConnectionId );
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
		if ( Networking.IsConnecting )
			return "Online: Connecting";

		if ( !OnlineSessionActive || !Networking.IsActive )
			return "Online: Offline";

		if ( LocalOnlineRole == RogueChessOnlineRole.BluePlayer && string.IsNullOrWhiteSpace( OnlineRedConnectionId ) )
			return "Online: Waiting for opponent";

		return LocalOnlineRole switch
		{
			RogueChessOnlineRole.BluePlayer => "Online: You are Blue",
			RogueChessOnlineRole.RedPlayer => "Online: You are Red",
			RogueChessOnlineRole.Spectator => "Online: Spectating",
			_ => "Online: Offline"
		};
	}

	string GetOnlineRosterText()
	{
		if ( !OnlineSessionActive || !Networking.IsActive )
			return "Online: Offline";

		var blueName = string.IsNullOrWhiteSpace( OnlineBlueDisplayName ) ? "Waiting" : OnlineBlueDisplayName;
		var redName = string.IsNullOrWhiteSpace( OnlineRedDisplayName ) ? "Waiting" : OnlineRedDisplayName;
		return $"Blue: {blueName} | Red: {redName}";
	}

	void EnsureNetworkedGameObject()
	{
		GameObject.NetworkMode = NetworkMode.Object;
		GameObject.NetworkSpawn();
	}

	void TryAssignLocalHostAsBlue()
	{
		if ( !Networking.IsHost )
			return;

		var localConnection = Connection.Local;
		var connectionId = GetConnectionId( localConnection );
		if ( string.IsNullOrWhiteSpace( connectionId ) )
			return;

		OnlineBlueConnectionId = connectionId;
		OnlineBlueDisplayName = GetConnectionDisplayName( localConnection );
	}

	void ClearOnlineSessionState( string message )
	{
		OnlineSessionActive = false;
		OnlineBlueConnectionId = "";
		OnlineRedConnectionId = "";
		OnlineBlueDisplayName = "";
		OnlineRedDisplayName = "";
		StatusMessage = message;
		ClearSelection();
		MarkDirty();
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
