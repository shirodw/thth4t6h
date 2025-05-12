using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using TicTacToeBlazor.Data;
using TicTacToeBlazor.Models;
using System.Threading.Tasks; // Ensure Task is available for async methods

namespace TicTacToeBlazor.Services
{
    public class GameStateService
    {
        private readonly ConcurrentDictionary<string, PlayerInfo> _waitingPlayers = new();
        private readonly ConcurrentDictionary<string, GameInfo> _activeGames = new();
        private readonly ConcurrentDictionary<string, string> _playerConnections = new();
        private readonly object _matchmakingLock = new object();
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        // Constructor should NOT be async
        public GameStateService(IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _dbContextFactory = dbContextFactory;
        }

        public async Task<PlayerInfo?> PlayerConnected(string connectionId, string name)
        {
            Console.WriteLine($"DEBUG: PlayerConnected - Entry. Name: '{name}', Connection: {connectionId}");
            var trimmedName = name.Trim();
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                Console.WriteLine("PlayerConnected Error: Player name is empty or whitespace.");
                return null;
            }

            bool nameExists = false;
            try
            {
                nameExists = _waitingPlayers.Values.Any(p => p.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) { Console.WriteLine($"!!! EXCEPTION checking waiting players: {ex}"); return null; }

            if (nameExists)
            {
                Console.WriteLine($"PlayerConnected Info: Name '{trimmedName}' is already waiting.");
                return null;
            }

            var player = new PlayerInfo { ConnectionId = connectionId, Name = trimmedName };
            if (!_waitingPlayers.TryAdd(connectionId, player))
            {
                Console.WriteLine($"PlayerConnected Error: Failed to add player {trimmedName} ({connectionId}) to waiting dictionary. Already exists?");
                _waitingPlayers.TryGetValue(connectionId, out player);
                return player;
            }

            Console.WriteLine($"PlayerConnected Success: Added {trimmedName} ({connectionId}). Waiting: {_waitingPlayers.Count}");
            return player;
        }

        public (GameInfo? game, PlayerInfo? player1, PlayerInfo? player2) TryMatchmake(string connectionId)
        {
            lock (_matchmakingLock)
            {
                Console.WriteLine($"DEBUG: TryMatchmake - Entered lock. Waiting: {_waitingPlayers.Count}. Caller Connection: {connectionId}");

                if (!_waitingPlayers.TryGetValue(connectionId, out var newPlayer))
                {
                    Console.WriteLine($"TryMatchmake Error: Caller {connectionId} not found in waiting players.");
                    return (null, null, null);
                }

                if (_waitingPlayers.Count < 2)
                {
                    Console.WriteLine($"TryMatchmake Info: Only {newPlayer.Name} is waiting. Count: {_waitingPlayers.Count}");
                    return (null, newPlayer, null);
                }

                PlayerInfo? opponent = null;
                try
                {
                    opponent = _waitingPlayers.FirstOrDefault(kvp => kvp.Key != connectionId).Value;
                }
                catch (Exception ex) { Console.WriteLine($"!!! EXCEPTION finding opponent: {ex}"); return (null, newPlayer, null); }

                if (opponent != null)
                {
                    Console.WriteLine($"TryMatchmake: Found opponent {opponent.Name} ({opponent.ConnectionId}) for {newPlayer.Name} ({newPlayer.ConnectionId})");

                    bool removedNew = _waitingPlayers.TryRemove(newPlayer.ConnectionId, out _);
                    bool removedOpponent = _waitingPlayers.TryRemove(opponent.ConnectionId, out _);
                    Console.WriteLine($"TryMatchmake: Removal status - NewPlayer({removedNew}), Opponent({removedOpponent})");

                    newPlayer.Symbol = "X";
                    opponent.Symbol = "O";

                    var game = new GameInfo
                    {
                        Player1 = newPlayer,
                        Player2 = opponent,
                        Status = GameStatus.SettingBoardSize,
                        StartTime = DateTime.UtcNow
                    };

                    _activeGames[game.GameId] = game;
                    _playerConnections[newPlayer.ConnectionId] = game.GameId;
                    _playerConnections[opponent.ConnectionId] = game.GameId;

                    Console.WriteLine($"TryMatchmake Success: Game started: {game.GameId} between {newPlayer.Name} (X) and {opponent.Name} (O)");
                    return (game, newPlayer, opponent);
                }
                else
                {
                    Console.WriteLine($"TryMatchmake: No opponent found for {newPlayer.Name}. Waiting count: {_waitingPlayers.Count}");
                    return (null, newPlayer, null);
                }
            }
        }

        public GameInfo? GetGameByConnection(string connectionId)
        {
            if (_playerConnections.TryGetValue(connectionId, out var gameId)) { _activeGames.TryGetValue(gameId, out var game); return game; }
            return null;
        }
        public GameInfo? GetGameById(string gameId) { _activeGames.TryGetValue(gameId, out var game); return game; }


        public async Task SetBoardSize(string gameId, int size)
        {
            Console.WriteLine($"DEBUG: SetBoardSize - Entry. GameID: {gameId}, Size: {size}");
            if (_activeGames.TryGetValue(gameId, out var game))
            {
                // --- More robust checks ---
                if (game.Status != GameStatus.SettingBoardSize) { Console.WriteLine($"SetBoardSize Error: Game {gameId} not in SettingBoardSize state (Actual: {game.Status})."); return; }
                if (size < 3 || size > 10) { Console.WriteLine($"SetBoardSize Error: Invalid size {size} requested for game {gameId}."); return; }
                if (game.Player1 == null || game.Player2 == null)
                {
                    Console.WriteLine($"SetBoardSize Error: Player1 ({game.Player1?.Name ?? "NULL"}) or Player2 ({game.Player2?.Name ?? "NULL"}) is null for game {gameId}. Aborting setup.");
                    game.Status = GameStatus.Aborted; return;
                }
                // --- Checks End ---

                game.BoardSize = size;
                game.Board = new string?[size, size];
                game.Status = GameStatus.Player1Turn;

                try
                {
                    Console.WriteLine($"DEBUG: SetBoardSize - Attempting DB operations for game {gameId}...");
                    using var dbContext = await _dbContextFactory.CreateDbContextAsync();
                    var dbPlayer1 = await GetOrCreateDbPlayer(dbContext, game.Player1.Name);
                    var dbPlayer2 = await GetOrCreateDbPlayer(dbContext, game.Player2.Name);
                    game.Player1.DbPlayerId = dbPlayer1.Id;
                    game.Player2.DbPlayerId = dbPlayer2.Id;
                    var dbGame = new Game { Player1Id = dbPlayer1.Id, Player2Id = dbPlayer2.Id, StartDate = game.StartTime, BoardSize = size };
                    dbContext.Games.Add(dbGame);
                    await dbContext.SaveChangesAsync();
                    game.DbGameId = dbGame.Id;
                    Console.WriteLine($"SetBoardSize Success: DB Game {dbGame.Id} created/updated for Game {gameId}. Board size {size}. Player 1's turn.");
                }
                catch (Exception dbEx)
                {
                    Console.WriteLine($"!!! DATABASE ERROR setting board size for game {gameId}: {dbEx}");
                    game.Status = GameStatus.Aborted; game.DbGameId = null;
                    throw;
                }
            }
            else { Console.WriteLine($"SetBoardSize Error: Game with ID {gameId} not found in active games."); }
        }

        private async Task<Player> GetOrCreateDbPlayer(ApplicationDbContext dbContext, string playerName)
        {
            var trimmedName = playerName.Trim();
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                Console.WriteLine("GetOrCreateDbPlayer Error: Attempted with empty/whitespace name.");
                throw new ArgumentException("Player name cannot be empty.", nameof(playerName));
            }

            Console.WriteLine($"DEBUG: GetOrCreateDbPlayer - Looking for player '{trimmedName}'");
            var player = await dbContext.Players.FirstOrDefaultAsync(p => p.Name == trimmedName);
            if (player == null)
            {
                Console.WriteLine($"DEBUG: GetOrCreateDbPlayer - Player '{trimmedName}' not found, creating new.");
                player = new Player { Name = trimmedName };
                dbContext.Players.Add(player);
                await dbContext.SaveChangesAsync();
                Console.WriteLine($"DEBUG: GetOrCreateDbPlayer - Created player '{trimmedName}' with ID {player.Id}.");
            }
            else
            {
                Console.WriteLine($"DEBUG: GetOrCreateDbPlayer - Found player '{trimmedName}' with ID {player.Id}.");
            }
            return player;
        }

        public async Task<(bool success, string message, GameStatus newStatus)> MakeMove(string connectionId, int row, int col)
        {
            Console.WriteLine($"DEBUG: MakeMove - Entry. Conn: {connectionId}, Move: ({row},{col})");
            if (!_playerConnections.TryGetValue(connectionId, out var gameId) || !_activeGames.TryGetValue(gameId, out var game))
            { Console.WriteLine($"MakeMove Error: Game not found for Conn {connectionId}."); return (false, "Game not found.", GameStatus.Aborted); }

            if (game.Status != GameStatus.Player1Turn && game.Status != GameStatus.Player2Turn)
            { Console.WriteLine($"MakeMove Error: Game {gameId} not active (State: {game.Status})."); return (false, "Game is not currently active.", game.Status); }

            PlayerInfo? player = null;
            if (game.Status == GameStatus.Player1Turn && game.Player1?.ConnectionId == connectionId) player = game.Player1;
            else if (game.Status == GameStatus.Player2Turn && game.Player2?.ConnectionId == connectionId) player = game.Player2;

            if (player == null) { Console.WriteLine($"MakeMove Error: Not player's turn or player invalid. Game: {gameId}, Conn: {connectionId}, State: {game.Status}"); return (false, "It's not your turn or player invalid.", game.Status); }

            if (row < 0 || row >= game.BoardSize || col < 0 || col >= game.BoardSize || game.Board == null || game.Board[row, col] != null)
            { Console.WriteLine($"MakeMove Error: Invalid move ({row},{col}) for board size {game.BoardSize} or cell taken. Game: {gameId}"); return (false, "Invalid move.", game.Status); }

            game.Board[row, col] = player.Symbol;
            Console.WriteLine($"DEBUG: MakeMove - Board updated for Game {gameId}: ({row},{col}) = {player.Symbol}");

            try { await RecordTurn(game.DbGameId!.Value, player.DbPlayerId!.Value, row, col); }
            catch (Exception dbEx) { Console.WriteLine($"!!! DB Error recording turn for Game {gameId}: {dbEx}"); }

            var winnerSymbol = CheckWin(game.Board, game.BoardSize);
            if (winnerSymbol != null)
            {
                game.Status = (winnerSymbol == game.Player1?.Symbol) ? GameStatus.Player1Win : GameStatus.Player2Win;
                Console.WriteLine($"DEBUG: MakeMove - Win detected for {winnerSymbol} in Game {gameId}. Status: {game.Status}");
                await EndGame(game.DbGameId!.Value, game.Status == GameStatus.Player1Win ? game.Player1?.DbPlayerId : game.Player2?.DbPlayerId);
                return (true, $"{player.Name} wins!", game.Status);
            }
            else if (IsBoardFull(game.Board, game.BoardSize))
            {
                game.Status = GameStatus.Draw;
                Console.WriteLine($"DEBUG: MakeMove - Draw detected for Game {gameId}. Status: {game.Status}");
                await EndGame(game.DbGameId!.Value, null);
                return (true, "It's a draw!", game.Status);
            }
            else
            {
                game.Status = (game.Status == GameStatus.Player1Turn) ? GameStatus.Player2Turn : GameStatus.Player1Turn;
                Console.WriteLine($"DEBUG: MakeMove - Turn switched for Game {gameId}. New Status: {game.Status}");
                return (true, "Move successful.", game.Status);
            }
        }

        private async Task RecordTurn(int dbGameId, int dbPlayerId, int row, int col)
        {
            using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var turn = new Turn { GameId = dbGameId, PlayerId = dbPlayerId, CoordX = row, CoordY = col, Timestamp = DateTime.UtcNow };
            dbContext.Turns.Add(turn);
            await dbContext.SaveChangesAsync();
            Console.WriteLine($"DEBUG: RecordTurn - Saved turn for Game {dbGameId}, Player {dbPlayerId} at ({row},{col}).");
        }

        private async Task EndGame(int dbGameId, int? winnerDbId) // winnerDbId is correctly nullable
        {
            Console.WriteLine($"DEBUG: EndGame - Attempting to end Game {dbGameId} in DB. WinnerID: {winnerDbId?.ToString() ?? "None"}");
            using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var dbGame = await dbContext.Games.FindAsync(dbGameId); // Can return null

            // Null check is correctly placed here
            if (dbGame != null && !dbGame.EndDate.HasValue)
            {
                // Assignment is fine: nullable int? = nullable int?
                dbGame.WinnerId = winnerDbId;
                dbGame.EndDate = DateTime.UtcNow;
                await dbContext.SaveChangesAsync();
                Console.WriteLine($"DEBUG: EndGame - DB Game {dbGameId} marked as ended.");
            }
            else if (dbGame == null) { Console.WriteLine($"EndGame Warning: DB Game {dbGameId} not found."); }
            else { Console.WriteLine($"EndGame Info: DB Game {dbGameId} already has an EndDate."); }
        }

        // No chat version - leave empty or remove if desired
        // public void AddChatMessage(string gameId, string playerName, string message) { }

        public async Task<(PlayerInfo? disconnectedPlayer, bool gameAborted)> PlayerDisconnected(string connectionId)
        {
            Console.WriteLine($"DEBUG: PlayerDisconnected - Entry. Conn: {connectionId}");
            bool gameWasAborted = false;

            if (_waitingPlayers.TryRemove(connectionId, out var waitingPlayer))
            { Console.WriteLine($"PlayerDisconnected: Removed waiting player {waitingPlayer.Name} ({connectionId})"); return (waitingPlayer, false); }

            if (_playerConnections.TryGetValue(connectionId, out var gameId) && _activeGames.TryGetValue(gameId, out var game))
            {
                _playerConnections.TryRemove(connectionId, out _);
                Console.WriteLine($"PlayerDisconnected: Player disconnected from game {gameId}: Connection {connectionId}");
                PlayerInfo? disconnectedPlayer = (game.Player1?.ConnectionId == connectionId) ? game.Player1 : game.Player2;

                if (game.Status != GameStatus.Player1Win && game.Status != GameStatus.Player2Win && game.Status != GameStatus.Draw && game.Status != GameStatus.Aborted)
                {
                    game.Status = GameStatus.Aborted; gameWasAborted = true;
                    Console.WriteLine($"PlayerDisconnected: Game {gameId} aborted due to disconnect.");
                    if (game.DbGameId.HasValue) { await EndGame(game.DbGameId.Value, null); }
                }

                if (game.Player1?.ConnectionId == connectionId) game.Player1 = null;
                if (game.Player2?.ConnectionId == connectionId) game.Player2 = null;
                Console.WriteLine($"PlayerDisconnected: Cleared player ref in Game {gameId} for Conn {connectionId}. P1: {game.Player1?.Name ?? "NULL"}, P2: {game.Player2?.Name ?? "NULL"}");

                if (game.Player1 == null && game.Player2 == null) { _activeGames.TryRemove(gameId, out _); Console.WriteLine($"PlayerDisconnected: Removed Game {gameId} from active games (both players gone)."); }

                return (disconnectedPlayer, gameWasAborted);
            }

            Console.WriteLine($"PlayerDisconnected: Conn {connectionId} not found in waiting or active game map.");
            return (null, false);
        }


        // --- Game Logic Helpers (Restored) ---
        private string? CheckWin(string?[,] board, int boardSize)
        {
            string? winner;
            // Rows
            for (int i = 0; i < boardSize; i++) { winner = CheckLine(board, boardSize, i, 0, 0, 1); if (winner != null) return winner; }
            // Columns
            for (int j = 0; j < boardSize; j++) { winner = CheckLine(board, boardSize, 0, j, 1, 0); if (winner != null) return winner; }
            // Diagonals
            winner = CheckLine(board, boardSize, 0, 0, 1, 1); if (winner != null) return winner;
            winner = CheckLine(board, boardSize, 0, boardSize - 1, 1, -1); if (winner != null) return winner;
            return null; // No winner found
        }

        private string? CheckLine(string?[,] board, int boardSize, int startRow, int startCol, int dRow, int dCol)
        {
            // Check bounds for starting position (should be valid if called correctly)
            if (startRow < 0 || startRow >= boardSize || startCol < 0 || startCol >= boardSize) return null;

            string? first = board[startRow, startCol];
            if (first == null) return null;

            for (int i = 1; i < boardSize; i++)
            {
                int checkRow = startRow + i * dRow;
                int checkCol = startCol + i * dCol;
                // Check bounds for subsequent cells
                if (checkRow < 0 || checkRow >= boardSize || checkCol < 0 || checkCol >= boardSize || board[checkRow, checkCol] != first)
                {
                    return null;
                }
            }
            return first;
        }

        private bool IsBoardFull(string?[,] board, int boardSize)
        {
            for (int i = 0; i < boardSize; i++)
            {
                for (int j = 0; j < boardSize; j++)
                {
                    if (board[i, j] == null)
                    {
                        return false; // Found an empty cell
                    }
                }
            }
            return true; // No empty cells found
        }
        // --- End Game Logic Helpers ---

    }
}