using Microsoft.AspNetCore.SignalR;
using TicTacToeBlazor.Models;
using TicTacToeBlazor.Services;

namespace TicTacToeBlazor.Hubs
{
    public class GameHub : Hub
    {
        private readonly GameStateService _gameState;

        // Inject ILogger for better logging practices later if desired
        // private readonly ILogger<GameHub> _logger;
        // public GameHub(GameStateService gameState, ILogger<GameHub> logger)
        public GameHub(GameStateService gameState)
        {
            _gameState = gameState;
            // _logger = logger;
        }

        public override async Task OnConnectedAsync()
        {
            Console.WriteLine($"Client connected: {Context.ConnectionId}");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string connectionId = Context.ConnectionId;
            Console.WriteLine($"Client disconnected: {connectionId} | Exception: {exception?.Message ?? "None"}");

            var game = _gameState.GetGameByConnection(connectionId);
            string? remainingPlayerConnectionId = null;
            if (game != null)
            {
                if (game.Player1?.ConnectionId == connectionId && game.Player2 != null) { remainingPlayerConnectionId = game.Player2.ConnectionId; }
                else if (game.Player2?.ConnectionId == connectionId && game.Player1 != null) { remainingPlayerConnectionId = game.Player1.ConnectionId; }
                Console.WriteLine($"Disconnecting player was in game {game.GameId}. Potential opponent connection: {remainingPlayerConnectionId ?? "None"}");
            }
            else { Console.WriteLine($"Disconnecting player {connectionId} was not found in an active game map."); }

            var (disconnectedPlayer, gameAborted) = await _gameState.PlayerDisconnected(connectionId);

            if (disconnectedPlayer != null && gameAborted && !string.IsNullOrEmpty(remainingPlayerConnectionId))
            {
                Console.WriteLine($"Notifying remaining player ({remainingPlayerConnectionId}) about disconnect of {disconnectedPlayer.Name}.");
                try { await Clients.Client(remainingPlayerConnectionId).SendAsync("OpponentDisconnected", $"{disconnectedPlayer.Name} has disconnected. The game is aborted."); }
                catch (Exception notifyEx) { Console.WriteLine($"Error notifying opponent {remainingPlayerConnectionId}: {notifyEx.Message}"); }
            }
            else if (disconnectedPlayer != null && gameAborted) { Console.WriteLine($"Game was aborted for player {disconnectedPlayer.Name}, but no remaining opponent connection ID was found/valid to notify."); }

            await base.OnDisconnectedAsync(exception);
        }


        public async Task FindGame(string playerName)
        {
            string connectionId = Context.ConnectionId;
            Console.WriteLine($"DEBUG: GameHub.FindGame received call for player: {playerName}, Connection: {connectionId}");

            PlayerInfo? playerInfo = null;
            try
            {
                Console.WriteLine($"DEBUG: GameHub.FindGame - Calling PlayerConnected for {playerName} ({connectionId})...");
                playerInfo = await _gameState.PlayerConnected(connectionId, playerName);
                Console.WriteLine($"DEBUG: GameHub.FindGame - PlayerConnected returned: {(playerInfo == null ? "NULL (Name In Use?)" : playerInfo.Name)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"!!! EXCEPTION during _gameState.PlayerConnected: {ex}");
                await Clients.Caller.SendAsync("Error", "Server error during connection setup.");
                return;
            }


            if (playerInfo == null)
            {
                Console.WriteLine($"DEBUG: GameHub.FindGame - PlayerInfo null, sending NameInUse to {connectionId}.");
                await Clients.Caller.SendAsync("NameInUse");
                return;
            }

            Console.WriteLine($"DEBUG: GameHub.FindGame - Sending UpdateState(Waiting) to {connectionId}.");
            await Clients.Caller.SendAsync("UpdateState", "Waiting");

            (GameInfo? game, PlayerInfo? player1, PlayerInfo? player2) matchResult = (null, null, null);
            try
            {
                Console.WriteLine($"DEBUG: GameHub.FindGame - Calling TryMatchmake for {connectionId}...");
                matchResult = _gameState.TryMatchmake(connectionId);
                Console.WriteLine($"DEBUG: GameHub.FindGame - TryMatchmake returned: Game? {matchResult.game != null}, P1? {matchResult.player1?.Name ?? "N/A"}, P2? {matchResult.player2?.Name ?? "N/A"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"!!! EXCEPTION during _gameState.TryMatchmake: {ex}");
                await Clients.Caller.SendAsync("Error", "Server error during matchmaking.");
                // Consider cleaning up player if matchmaking failed badly?
                // await _gameState.PlayerDisconnected(connectionId); // Or specific cleanup logic
                return;
            }

            // Destructure result *after* try-catch
            var (game, player1, player2) = matchResult;

            if (game != null && player1 != null && player2 != null)
            {
                Console.WriteLine($"DEBUG: GameHub.FindGame - Game Found! Notifying players {player1.Name} ({player1.ConnectionId}) and {player2.Name} ({player2.ConnectionId}).");
                try
                {
                    // Send GameFound notifications
                    await Clients.Client(player1.ConnectionId).SendAsync("GameFound", game.GameId, player1.Name, player2.Name, player1.Symbol, true);
                    await Clients.Client(player2.ConnectionId).SendAsync("GameFound", game.GameId, player1.Name, player2.Name, player2.Symbol, false);
                    Console.WriteLine($"DEBUG: GameHub.FindGame - GameFound notifications sent.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"!!! EXCEPTION sending GameFound notification: {ex}");
                    // This is tricky - game exists but clients might not know.
                    // Might need to abort/clean up the game state.
                }
            }
            else if (player1 != null) // Check if still waiting (TryMatchmake returns player1 even if no game)
            {
                Console.WriteLine($"DEBUG: GameHub.FindGame - Still waiting for {player1.Name} ({connectionId}).");
            }
            else
            {
                // This case should ideally not happen if PlayerConnected succeeded
                Console.WriteLine($"DEBUG: GameHub.FindGame - TryMatchmake returned unexpected nulls for player {playerName} ({connectionId}).");
            }
        }

        public async Task SetBoardSize(string gameId, int size)
        {
            string connectionId = Context.ConnectionId;
            Console.WriteLine($"Received request to set board size for game {gameId} to {size}x{size} from {connectionId}");
            GameInfo? game = null;

            try
            {
                game = _gameState.GetGameById(gameId);

                if (game != null && game.Player1?.ConnectionId == connectionId && game.Status == GameStatus.SettingBoardSize)
                {
                    await _gameState.SetBoardSize(gameId, size);

                    game = _gameState.GetGameById(gameId); // Re-fetch potentially updated state

                    if (game == null) { Console.WriteLine($"Error: Game {gameId} became null after SetBoardSize service call."); await Clients.Caller.SendAsync("Error", "Game state lost after setting size."); return; }

                    if (game.Status == GameStatus.Player1Turn)
                    {
                        Console.WriteLine($"Board size set. Notifying players of game {gameId}.");
                        if (game.Player1 != null && game.Player2 != null)
                        { await Clients.Clients(game.Player1.ConnectionId, game.Player2.ConnectionId).SendAsync("GameStarted", game.GameId, size, game.Player1.Name); }
                        else { Console.WriteLine($"Error: Player 1 ({game.Player1?.ConnectionId}) or Player 2 ({game.Player2?.ConnectionId}) is null when trying to notify for game start {gameId}"); if (game.Player1 != null) { await Clients.Client(game.Player1.ConnectionId).SendAsync("Error", "Opponent data missing or disconnected, cannot start game."); } }
                    }
                    else if (game.Status == GameStatus.Aborted)
                    { Console.WriteLine($"Game {gameId} was aborted during board size setting (likely opponent disconnect)."); await Clients.Caller.SendAsync("Error", "Opponent disconnected during game setup. Game aborted."); }
                    else { Console.WriteLine($"SetBoardSize: Service call completed but game {gameId} status is unexpected: {game.Status}."); await Clients.Caller.SendAsync("Error", "Failed to set board size or game state changed unexpectedly."); }
                }
                else
                {
                    string reason = game == null ? "Game not found" : game.Player1?.ConnectionId != connectionId ? "Not Player 1" : $"Wrong state ({game?.Status})";
                    Console.WriteLine($"Failed to set board size for game {gameId}. Reason: {reason}.");
                    if (game != null && (game.Player1?.ConnectionId == connectionId || game.Player2?.ConnectionId == connectionId)) { await Clients.Caller.SendAsync("Error", "Cannot set board size now."); }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"!!! ERROR during SetBoardSize Hub method for game {gameId}: {ex}");
                await Clients.Caller.SendAsync("Error", "An unexpected server error occurred while starting the game.");
            }
        }

        public async Task MakeMove(string gameId, int row, int col)
        {
            string connectionId = Context.ConnectionId;
            Console.WriteLine($"Received move ({row},{col}) for game {gameId} from {connectionId}");
            GameInfo? game = null; // Defined here to be accessible later

            try // Add try-catch around service call
            {
                var (success, message, newStatus) = await _gameState.MakeMove(connectionId, row, col);
                game = _gameState.GetGameById(gameId); // Get state *after* move attempt

                if (game == null) { Console.WriteLine($"Error: Game {gameId} not found after MakeMove call."); await Clients.Caller.SendAsync("Error", "Game not found after move."); return; }

                string? p1ConnId = game.Player1?.ConnectionId;
                string? p2ConnId = game.Player2?.ConnectionId;
                var clientsToNotify = new List<string>();
                if (!string.IsNullOrEmpty(p1ConnId)) clientsToNotify.Add(p1ConnId);
                if (!string.IsNullOrEmpty(p2ConnId)) clientsToNotify.Add(p2ConnId);

                if (success)
                {
                    Console.WriteLine($"Move successful in game {gameId}. New status: {newStatus}. Notifying players.");
                    string nextPlayerName = (newStatus == GameStatus.Player1Turn) ? game.Player1?.Name ?? "P1" : (newStatus == GameStatus.Player2Turn) ? game.Player2?.Name ?? "P2" : "";

                    if (clientsToNotify.Any()) { await Clients.Clients(clientsToNotify).SendAsync("ReceiveMove", row, col, game.Board?[row, col], newStatus, nextPlayerName); }

                    if (newStatus == GameStatus.Player1Win || newStatus == GameStatus.Player2Win || newStatus == GameStatus.Draw)
                    {
                        string winnerName = (newStatus == GameStatus.Player1Win) ? game.Player1?.Name ?? "P1" : (newStatus == GameStatus.Player2Win) ? game.Player2?.Name ?? "P2" : "";
                        if (clientsToNotify.Any()) { await Clients.Clients(clientsToNotify).SendAsync("GameOver", newStatus, winnerName); }
                    }
                }
                else { Console.WriteLine($"Invalid move in game {gameId}: {message}"); await Clients.Caller.SendAsync("Error", message); }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"!!! ERROR during MakeMove Hub method for game {gameId}: {ex}");
                // Inform caller, but avoid sending error if game doesn't exist anymore
                if (game != null) { await Clients.Caller.SendAsync("Error", "An unexpected server error occurred while making your move."); }
            }
        }

        // No chat version - comment out or remove SendChatMessage if not needed
        // public async Task SendChatMessage(string gameId, string message)
        // {
        //      // ... chat logic ...
        // }
    }
}