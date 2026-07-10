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
		InitializeOnlineArmies();
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

	// Offline: any local action may configure the army (the single-army flow edits Blue). Online: a connection
	// may only touch its own team's army; spectators are rejected for both teams.
	bool CanConnectionConfigureArmy( Connection connection, RogueChessTeam team )
	{
		if ( !OnlineSessionActive )
			return true;

		return GetOnlineRole( connection ) switch
		{
			RogueChessOnlineRole.BluePlayer => team == RogueChessTeam.Blue,
			RogueChessOnlineRole.RedPlayer => team == RogueChessTeam.Red,
			_ => false
		};
	}

	bool RejectIfConnectionCannotConfigureArmy( Connection connection, RogueChessTeam team )
	{
		if ( CanConnectionConfigureArmy( connection, team ) )
			return false;

		StatusMessage = $"You can only configure your own team's army ({TeamName( team )} is not yours).";
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

	// Army-builder header: names the team the local player is configuring (or a read-only spectator label).
	public string GetArmyBuilderHeaderText()
	{
		if ( !OnlineSessionActive )
			return "Your Army";

		return LocalOnlineRole switch
		{
			RogueChessOnlineRole.BluePlayer => "Your Army — Blue",
			RogueChessOnlineRole.RedPlayer => "Your Army — Red",
			_ => "Match Setup (Spectating)"
		};
	}

	// Per-team ready status shown in the online builder so each player can see the opponent's progress.
	public string GetArmyReadyStatusText()
	{
		if ( !OnlineSessionActive )
			return "";

		var blue = BlueArmyReady ? "Ready" : "Building";
		var red = RedArmyReady ? "Ready" : "Building";
		return $"Blue: {blue}  |  Red: {red}";
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
		BlueArmyReady = false;
		RedArmyReady = false;
		StatusMessage = message;
		ClearSelection();
		MarkDirty();
	}

	// --- Online per-team army building + ready-up ------------------------------------------------------

	void InitializeOnlineArmies()
	{
		// Red starts empty so the Red player builds from scratch (offline Red is the AI preset). Blue keeps
		// whatever the host was assembling in the offline builder.
		ResetArmyChoicesToEmpty( RogueChessTeam.Red );
		BlueArmyReady = false;
		RedArmyReady = false;
		selectedRedArmyBuilderUnit = UnitType.Shooter;
	}

	void ResetArmyChoicesToEmpty( RogueChessTeam team )
	{
		var choices = GetArmyChoices( team );
		choices.Clear();
		choices.AddRange( CreateEmptyArmyChoices() );
	}

	void SetArmyReady( RogueChessTeam team, bool ready )
	{
		if ( team == RogueChessTeam.Blue )
			BlueArmyReady = ready;
		else
			RedArmyReady = ready;
	}

	// START GAME dispatcher. Offline redeploys immediately; online marks the caller's side ready and only
	// begins the match once both sides are ready.
	void HandleStartPressedForConnection( Connection caller )
	{
		if ( OnlineSessionActive )
			ReadyUpForConnection( caller );
		else
			RestartMatchForConnection( caller );
	}

	void ReadyUpForConnection( Connection caller )
	{
		var role = GetOnlineRole( caller );
		var team = role switch
		{
			RogueChessOnlineRole.BluePlayer => (RogueChessTeam?)RogueChessTeam.Blue,
			RogueChessOnlineRole.RedPlayer => (RogueChessTeam?)RogueChessTeam.Red,
			_ => null
		};

		if ( team is null )
		{
			StatusMessage = "Spectators cannot start the match.";
			MarkDirty();
			return;
		}

		if ( !IsArmyComplete( team.Value ) )
		{
			StatusMessage = $"{TeamName( team.Value )}: choose {HeroSlotCount} heroes to join your Commander before readying up.";
			MarkDirty();
			return;
		}

		SetArmyReady( team.Value, true );

		if ( BlueArmyReady && RedArmyReady )
		{
			StatusMessage = "Both armies ready. Starting match.";
			BeginMatch(); // both armies already built by their players; keep them as-is
			return;
		}

		var otherTeam = OtherTeam( team.Value );
		StatusMessage = $"{TeamName( team.Value )} is ready. Waiting for {TeamName( otherTeam )} to finish their army.";
		MarkDirty();
	}

	void RestartOnlineForConnection( Connection caller )
	{
		var role = GetOnlineRole( caller );
		if ( role != RogueChessOnlineRole.BluePlayer && role != RogueChessOnlineRole.RedPlayer )
		{
			StatusMessage = "Only a player can restart the match.";
			MarkDirty();
			return;
		}

		// Return both sides to army building for a fresh match.
		MatchStarted = false;
		Winner = null;
		WinReason = null;
		IsDraw = false;
		units.Clear();
		blueHand.Clear();
		redHand.Clear();
		combatLog.Clear();
		ResetArmyChoicesToEmpty( RogueChessTeam.Blue );
		ResetArmyChoicesToEmpty( RogueChessTeam.Red );
		BlueArmyReady = false;
		RedArmyReady = false;
		ClearSelection();
		StatusMessage = "Rebuilding armies. Both players pick again, then press Start Game.";
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
		HandleStartPressedForConnection( Rpc.Caller );
	}

	[Rpc.Host]
	public void RequestRestartMatch()
	{
		RestartMatchForConnection( Rpc.Caller );
	}

	[Rpc.Host]
	public void RequestSelectArmyBuilderUnit( RogueChessTeam team, UnitType unitType )
	{
		SelectArmyBuilderUnitForConnection( Rpc.Caller, team, unitType );
	}

	[Rpc.Host]
	public void RequestSetArmySlot( RogueChessTeam team, int index )
	{
		SetArmySlotForConnection( Rpc.Caller, team, index );
	}

	[Rpc.Host]
	public void RequestClearArmySlot( RogueChessTeam team, int index )
	{
		ClearArmySlotForConnection( Rpc.Caller, team, index );
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
