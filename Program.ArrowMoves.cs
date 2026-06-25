using ChessKit;
using Chess;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using static ChessKit.FenText;

// Shared arrow/move-resolution helpers used by the live overlay and the analysis board's arrow rendering.
partial class Program
{
    private static bool TryResolveLegalArrowMove(
        string fen,
        char expectedMovingSide,
        int originalFromFile,
        int originalFromRank,
        int originalToFile,
        int originalToRank,
        int remappedFromFile,
        int remappedFromRank,
        int remappedToFile,
        int remappedToRank,
        out int resolvedFromFile,
        out int resolvedFromRank,
        out int resolvedToFile,
        out int resolvedToRank)
    {
        resolvedFromFile = remappedFromFile;
        resolvedFromRank = remappedFromRank;
        resolvedToFile = remappedToFile;
        resolvedToRank = remappedToRank;

        if (IsLegalArrowMoveForSideToMove(fen, expectedMovingSide, remappedFromFile, remappedFromRank, remappedToFile, remappedToRank))
            return true;

        if (IsLegalArrowMoveForSideToMove(fen, expectedMovingSide, originalFromFile, originalFromRank, originalToFile, originalToRank))
        {
            resolvedFromFile = originalFromFile;
            resolvedFromRank = originalFromRank;
            resolvedToFile = originalToFile;
            resolvedToRank = originalToRank;
            return true;
        }

        int rotatedFromFile = 7 - originalFromFile;
        int rotatedFromRank = 7 - originalFromRank;
        int rotatedToFile = 7 - originalToFile;
        int rotatedToRank = 7 - originalToRank;

        if (IsLegalArrowMoveForSideToMove(fen, expectedMovingSide, rotatedFromFile, rotatedFromRank, rotatedToFile, rotatedToRank))
        {
            resolvedFromFile = rotatedFromFile;
            resolvedFromRank = rotatedFromRank;
            resolvedToFile = rotatedToFile;
            resolvedToRank = rotatedToRank;
            return true;
        }

        return false;
    }

    private static bool TryResolveRenderableExternalArrowMove(
        string fen,
        char expectedMovingSide,
        int originalFromFile,
        int originalFromRank,
        int originalToFile,
        int originalToRank,
        int remappedFromFile,
        int remappedFromRank,
        int remappedToFile,
        int remappedToRank,
        out int resolvedFromFile,
        out int resolvedFromRank,
        out int resolvedToFile,
        out int resolvedToRank)
    {
        resolvedFromFile = remappedFromFile;
        resolvedFromRank = remappedFromRank;
        resolvedToFile = remappedToFile;
        resolvedToRank = remappedToRank;

        if (IsRenderableExternalArrowMoveForSide(fen, expectedMovingSide, remappedFromFile, remappedFromRank, remappedToFile, remappedToRank))
            return true;

        if (IsRenderableExternalArrowMoveForSide(fen, expectedMovingSide, originalFromFile, originalFromRank, originalToFile, originalToRank))
        {
            resolvedFromFile = originalFromFile;
            resolvedFromRank = originalFromRank;
            resolvedToFile = originalToFile;
            resolvedToRank = originalToRank;
            return true;
        }

        int rotatedFromFile = 7 - originalFromFile;
        int rotatedFromRank = 7 - originalFromRank;
        int rotatedToFile = 7 - originalToFile;
        int rotatedToRank = 7 - originalToRank;

        if (IsRenderableExternalArrowMoveForSide(fen, expectedMovingSide, rotatedFromFile, rotatedFromRank, rotatedToFile, rotatedToRank))
        {
            resolvedFromFile = rotatedFromFile;
            resolvedFromRank = rotatedFromRank;
            resolvedToFile = rotatedToFile;
            resolvedToRank = rotatedToRank;
            return true;
        }

        return false;
    }

    private static bool TryResolveStrictExternalArrowMove(
        string fen,
        char expectedMovingSide,
        int fromFile,
        int fromRank,
        int toFile,
        int toRank,
        out int resolvedFromFile,
        out int resolvedFromRank,
        out int resolvedToFile,
        out int resolvedToRank)
    {
        resolvedFromFile = fromFile;
        resolvedFromRank = fromRank;
        resolvedToFile = toFile;
        resolvedToRank = toRank;

        return IsLegalArrowMoveForSideToMove(
            FenWithSideToMove(fen, expectedMovingSide),
            expectedMovingSide,
            fromFile,
            fromRank,
            toFile,
            toRank);
    }

    private static string FenWithSideToMove(string fen, char sideToMove)
    {
        if (string.IsNullOrWhiteSpace(fen))
            return fen;

        var parts = fen.Split(' ');
        if (parts.Length < 2)
            return fen;

        parts[1] = sideToMove == 'b' ? "b" : "w";
        return string.Join(" ", parts);
    }

    private static bool TryResolveLegalAnalysisBoardArrowMove(
        string fen,
        char expectedMovingSide,
        int fromFile,
        int fromRank,
        int toFile,
        int toRank,
        out int resolvedFromFile,
        out int resolvedFromRank,
        out int resolvedToFile,
        out int resolvedToRank)
    {
        resolvedFromFile = fromFile;
        resolvedFromRank = fromRank;
        resolvedToFile = toFile;
        resolvedToRank = toRank;

        return IsLegalAnalysisBoardArrowMoveForSide(fen, expectedMovingSide, fromFile, fromRank, toFile, toRank);
    }

    private static bool IsLegalAnalysisBoardArrowMoveForSide(string fen, char expectedMovingSide, int fromFile, int fromRank, int toFile, int toRank)
    {
        if (fromFile is < 0 or > 7 || fromRank is < 0 or > 7 || toFile is < 0 or > 7 || toRank is < 0 or > 7)
            return false;

        try
        {
            char sideToMove = GetSideToMove(fen) ?? expectedMovingSide;
            if (sideToMove != expectedMovingSide)
                return false;

            var board = ChessBoard.LoadFromFen(fen, AutoEndgameRules.All);
            var side = expectedMovingSide == 'b' ? PieceColor.Black : PieceColor.White;
            var origin = new Position((short)fromFile, (short)fromRank);
            var target = new Position((short)toFile, (short)toRank);
            var piece = board[origin];
            if (piece == null || piece.Color != side)
                return false;

            return board.Moves(origin, false, false).Any(m => m.NewPosition == target);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLegalArrowMoveForSideToMove(string fen, char expectedMovingSide, int fromFile, int fromRank, int toFile, int toRank)
    {
        if (fromFile is < 0 or > 7 || fromRank is < 0 or > 7 || toFile is < 0 or > 7 || toRank is < 0 or > 7)
            return false;

        try
        {
            var board = ChessBoard.LoadFromFen(fen, AutoEndgameRules.All);
            var sideToMove = expectedMovingSide == 'b' ? PieceColor.Black : PieceColor.White;
            var origin = new Position((short)fromFile, (short)fromRank);
            var target = new Position((short)toFile, (short)toRank);
            var piece = board[origin];
            if (piece == null || piece.Color != sideToMove)
                return false;
            return board.Moves(origin, false, false).Any(m => m.NewPosition == target);
        }
        catch
        {
            return IsRenderableExternalArrowMoveForSide(fen, expectedMovingSide, fromFile, fromRank, toFile, toRank);
        }
    }

    private static bool IsRenderableExternalArrowMoveForSide(string fen, char expectedMovingSide, int fromFile, int fromRank, int toFile, int toRank)
    {
        string boardPosition = GetBoardPosition(fen);
        if (string.IsNullOrWhiteSpace(boardPosition))
            return false;

        var pieces = ParseBoard(boardPosition);
        string fromSquare = ToAlgebraicSquare(fromFile, fromRank);
        string toSquare = ToAlgebraicSquare(toFile, toRank);

        if (!pieces.TryGetValue(fromSquare, out char originPiece))
            return false;

        bool originMatchesSide =
            expectedMovingSide == 'w' ? char.IsUpper(originPiece) : char.IsLower(originPiece);

        if (!originMatchesSide)
            return false;

        char lowerPiece = char.ToLowerInvariant(originPiece);
        int fileDelta = toFile - fromFile;
        int rankDelta = toRank - fromRank;
        int absFileDelta = Math.Abs(fileDelta);
        int absRankDelta = Math.Abs(rankDelta);

        if (pieces.TryGetValue(toSquare, out char targetPiece))
        {
            bool targetSameSide =
                expectedMovingSide == 'w' ? char.IsUpper(targetPiece) : char.IsLower(targetPiece);

            if (targetSameSide)
                return false;
        }
        else if (lowerPiece == 'p' && fileDelta != 0)
        {
            return false;
        }

        if (fromFile == toFile && fromRank == toRank)
            return false;

        return lowerPiece switch
        {
            'p' => IsPseudoLegalPawnArrow(expectedMovingSide, fromRank, fileDelta, rankDelta, pieces.ContainsKey(toSquare)),
            'n' => (absFileDelta == 1 && absRankDelta == 2) || (absFileDelta == 2 && absRankDelta == 1),
            'b' => absFileDelta == absRankDelta && IsPathClear(pieces, fromFile, fromRank, toFile, toRank),
            'r' => (fileDelta == 0 || rankDelta == 0) && IsPathClear(pieces, fromFile, fromRank, toFile, toRank),
            'q' => ((absFileDelta == absRankDelta) || fileDelta == 0 || rankDelta == 0) &&
                   IsPathClear(pieces, fromFile, fromRank, toFile, toRank),
            'k' => absFileDelta <= 1 && absRankDelta <= 1,
            _ => false
        };
    }

    private static string ToAlgebraicSquare(int file, int rank)
    {
        if (file is < 0 or > 7 || rank is < 0 or > 7)
            return "";

        return $"{(char)('a' + file)}{rank + 1}";
    }

    private static bool IsPseudoLegalPawnArrow(char expectedMovingSide, int fromRank, int fileDelta, int rankDelta, bool targetOccupied)
    {
        int forward = expectedMovingSide == 'b' ? -1 : 1;
        int startRank = expectedMovingSide == 'b' ? 6 : 1;

        if (fileDelta == 0)
        {
            if (targetOccupied)
                return false;

            return rankDelta == forward ||
                   (fromRank == startRank && rankDelta == 2 * forward);
        }

        return Math.Abs(fileDelta) == 1 &&
               rankDelta == forward &&
               targetOccupied;
    }

    private static bool IsPathClear(Dictionary<string, char> pieces, int fromFile, int fromRank, int toFile, int toRank)
    {
        int fileStep = Math.Sign(toFile - fromFile);
        int rankStep = Math.Sign(toRank - fromRank);
        int file = fromFile + fileStep;
        int rank = fromRank + rankStep;

        while (file != toFile || rank != toRank)
        {
            if (pieces.ContainsKey(ToAlgebraicSquare(file, rank)))
                return false;

            file += fileStep;
            rank += rankStep;
        }

        return true;
    }

    private static string GetBoardPosition(string fen)
    {
        var parts = fen.Split(' ');
        return parts.Length > 0 ? parts[0] : fen;
    }

    private static string ToUciMove(Move move)
    {
        string text = PositionToSquare(move.OriginalPosition) + PositionToSquare(move.NewPosition);
        if (move.IsPromotion && move.Promotion != null)
            text += char.ToLowerInvariant(move.Promotion.Type.AsChar);
        return text;
    }

    private static string PositionToSquare(Position position)
    {
        char file = (char)('a' + position.X);
        char rank = (char)('1' + position.Y);
        return $"{file}{rank}";
    }

}
