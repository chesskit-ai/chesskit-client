using System.Collections.Concurrent;
using System.Globalization;
using Chess;

namespace ChessKit
{
    // EXPERIMENT A — Predictive vision (local move inference).
    //
    // Goal: avoid the remote vision round-trip by inferring the move that was
    // played purely from (a) the previously known position and (b) which board
    // squares changed (the local pixel-diff we already compute). From a known
    // position there are only ~30 legal moves, each touching a known set of
    // squares; a clean 2-square diff usually identifies the move uniquely with
    // no server call at all.
    //
    // This file currently runs in SHADOW MODE only: it does NOT change behaviour.
    // On every confirmed external-board change it runs the inference on a
    // background thread, compares the result against the server's FEN (ground
    // truth), and logs whether local inference WOULD have been correct. Once the
    // logs show high accuracy we flip it to actually skip the round-trip.
    //
    // Nothing here touches the hot path beyond copying a small list of changed
    // squares; all work happens on a Task.Run thread and only emits log lines.
    internal static class PredictiveVision
    {
        // Default OFF. The shadow measurement did its job: it revealed the
        // codebase already does local move inference (the FAST-FEN /
        // TryApplyOptimisticChangedSquaresFen path, ~294 fires/session) and that
        // this parallel inference is redundant and perspective-fragile. Left in
        // the tree, opt-in only, for ad-hoc re-measurement: env
        // CHESSKIT_PREDICT_SHADOW=1.
        internal static bool ShadowEnabled = false;

        static PredictiveVision()
        {
            string? v = Environment.GetEnvironmentVariable("CHESSKIT_PREDICT_SHADOW");
            if (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
                ShadowEnabled = true;
        }

        // Shadow work runs on ONE dedicated background thread at below-normal
        // priority with a depth-1 queue. This is deliberately NOT the thread
        // pool: CPU-bound inference on the pool was starving the app's real
        // async work (engine/vision responses, arrow updates) under bullet load
        // and freezing the arrows. The bounded queue drops samples when busy —
        // for measurement, sampling is perfectly fine.
        private readonly struct Work
        {
            public readonly string OldFen;
            public readonly string ServerFen;
            public readonly List<(int, int)>? Squares;
            public readonly bool Flipped;
            public Work(string oldFen, string serverFen, List<(int, int)>? squares, bool flipped)
            {
                OldFen = oldFen;
                ServerFen = serverFen;
                Squares = squares;
                Flipped = flipped;
            }
        }

        private static readonly BlockingCollection<Work> _queue =
            new(new ConcurrentQueue<Work>(), boundedCapacity: 1);
        private static readonly object _workerLock = new();
        private static volatile bool _workerStarted;
        private static long _dropped;

        internal enum InferResult
        {
            Hit,            // exactly one legal move explains the diff
            Ambiguous,      // multiple legal moves explain the diff
            Promotion,      // unique from/to but promotion piece is unknown
            NoMatch,        // no single legal move explains the diff
            NoDiff,         // no changed-square information available
            BadInput        // FEN failed to parse, etc.
        }

        private sealed class Inference
        {
            public InferResult Result = InferResult.NoMatch;
            public string? InferredPlacement;   // board-placement field of the resulting FEN
            public string? MoveText;             // e.g. "e2e4" (footprint-derived, perspective-agnostic)
            public int ExactCount;               // moves whose footprint == mapped diff
            public int SubsetCount;              // moves whose footprint is a subset of the mapped diff
            public char Side;                    // 'w' / 'b' the side that matched
        }

        // ---- Hot-path entry: copy inputs and offload. -------------------------
        internal static void RunShadowAsync(
            string oldFen,
            string serverFen,
            IReadOnlyList<BoardVisionDetector.BoardDiffSquare>? changed,
            bool boardFlipped)
        {
            if (!ShadowEnabled)
                return;
            if (string.IsNullOrWhiteSpace(oldFen) || string.IsNullOrWhiteSpace(serverFen))
                return;

            // Snapshot the changed squares (caller's list/Mats may be reused).
            List<(int file, int rankTop)>? squares = null;
            if (changed != null && changed.Count > 0)
            {
                squares = new List<(int, int)>(changed.Count);
                foreach (var s in changed)
                    squares.Add((s.File, s.RankFromTop));
            }

            EnsureWorker();
            // Non-blocking: if the single worker is busy, drop this sample rather
            // than queue up CPU work that could back up under bullet load.
            if (!_queue.TryAdd(new Work(oldFen, serverFen, squares, boardFlipped)))
                Interlocked.Increment(ref _dropped);
        }

        private static void EnsureWorker()
        {
            if (_workerStarted)
                return;
            lock (_workerLock)
            {
                if (_workerStarted)
                    return;
                var t = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "PredictiveVisionShadow",
                    Priority = ThreadPriority.BelowNormal
                };
                t.Start();
                _workerStarted = true;
            }
        }

        private static void WorkerLoop()
        {
            foreach (var w in _queue.GetConsumingEnumerable())
            {
                try { RunShadow(w.OldFen, w.ServerFen, w.Squares, w.Flipped); }
                catch { /* measurement must never affect the app */ }
            }
        }

        private static void RunShadow(
            string oldFen,
            string serverFen,
            List<(int file, int rankTop)>? squares,
            bool boardFlipped)
        {
            try
            {
                string oldPlacement = Placement(oldFen);
                string serverPlacement = Placement(serverFen);
                if (string.IsNullOrEmpty(oldPlacement) || string.IsNullOrEmpty(serverPlacement))
                    return;
                if (oldPlacement == serverPlacement)
                    return; // no actual board change to explain

                char[,]? oldGrid = ParsePlacement(oldPlacement);
                char[,]? serverGrid = ParsePlacement(serverPlacement);
                if (oldGrid == null || serverGrid == null)
                    return;

                // Ground-truth move footprint: chess squares that actually changed.
                var trueFootprint = DiffSquares(oldGrid, serverGrid);

                // Diff squares mapped image->chess for the assumed perspective
                // (and the opposite, as a fallback in case the perspective bit
                // is wrong — measured, not assumed).
                var mapped = MapDiff(squares, boardFlipped);
                var mappedOpp = MapDiff(squares, !boardFlipped);

                int diffSqCount = squares?.Count ?? 0;
                bool diffCoversTrue = mapped != null && trueFootprint.All(mapped.Contains);
                int diffExtra = mapped == null ? -1 : mapped.Count(s => !trueFootprint.Contains(s));

                // Local inference (NEVER looks at serverFen).
                bool perspMismatch = false;
                Inference inf = mapped == null
                    ? new Inference { Result = InferResult.NoDiff }
                    : Infer(oldFen, oldPlacement, mapped);

                if (inf.Result != InferResult.Hit && mappedOpp != null)
                {
                    var infOpp = Infer(oldFen, oldPlacement, mappedOpp);
                    if (infOpp.Result == InferResult.Hit)
                    {
                        inf = infOpp;
                        perspMismatch = true;
                    }
                }

                bool correct = inf.Result == InferResult.Hit &&
                               string.Equals(inf.InferredPlacement, serverPlacement, StringComparison.Ordinal);

                string fromTo = string.Join(",", trueFootprint
                    .Select(SquareName)
                    .OrderBy(x => x, StringComparer.Ordinal));

                string msg =
                    $"[PREDICT] result={inf.Result} correct={correct} move={inf.MoveText ?? "-"} side={inf.Side} " +
                    $"exact={inf.ExactCount} subset={inf.SubsetCount} trueFp={trueFootprint.Count}({fromTo}) " +
                    $"diffSq={diffSqCount} covers={diffCoversTrue} extra={diffExtra} persp!={perspMismatch} flip={boardFlipped}";

                Program.PredictLog(msg);
                ArrowTimeline.Log("PREDICT_SHADOW", reason: inf.Result.ToString(),
                    count: inf.SubsetCount, extra: msg);

                RecordStats(inf.Result, correct, diffCoversTrue, perspMismatch);
            }
            catch (Exception ex)
            {
                try { ArrowTimeline.Log("PREDICT_SHADOW_ERR", extra: ex.GetType().Name + ": " + ex.Message); }
                catch { }
            }
        }

        // ---- Core inference: which legal move explains the changed squares? ----
        private static Inference Infer(string oldFen, string oldPlacement, HashSet<(int, int)> mappedChanged)
        {
            var outcome = new Inference();
            if (mappedChanged.Count == 0)
            {
                outcome.Result = InferResult.NoDiff;
                return outcome;
            }

            char[,]? oldGrid = ParsePlacement(oldPlacement);
            if (oldGrid == null)
            {
                outcome.Result = InferResult.BadInput;
                return outcome;
            }

            string[] fields = oldFen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            char primarySide = fields.Length > 1 && fields[1].Length > 0 ? char.ToLowerInvariant(fields[1][0]) : 'w';
            string castling = fields.Length > 2 ? fields[2] : "-";
            string ep = fields.Length > 3 ? fields[3] : "-";

            // Try the position's own side-to-move first, then the other side
            // (external turn detection is not always reliable).
            char[] sides = primarySide == 'b' ? new[] { 'b', 'w' } : new[] { 'w', 'b' };

            foreach (char side in sides)
            {
                // En-passant target only belongs to the real side-to-move.
                string epForSide = side == primarySide ? ep : "-";
                string fen = $"{oldPlacement} {side} {castling} {epForSide} 0 1";

                ChessBoard board;
                try { board = ChessBoard.LoadFromFen(fen, AutoEndgameRules.All); }
                catch { continue; }

                var exact = new List<(string placement, HashSet<(int, int)> fp)>();
                var subset = new List<(string placement, HashSet<(int, int)> fp)>();

                IEnumerable<Move> moves;
                try { moves = board.Moves(false, true).ToList(); }
                catch { continue; }

                foreach (var move in moves)
                {
                    string newPlacement;
                    try
                    {
                        var trial = ChessBoard.LoadFromFen(fen, AutoEndgameRules.All);
                        trial.Move(move);
                        newPlacement = Placement(trial.ToFen());
                    }
                    catch { continue; }

                    char[,]? newGrid = ParsePlacement(newPlacement);
                    if (newGrid == null)
                        continue;

                    var fp = DiffSquares(oldGrid, newGrid);
                    if (fp.Count == 0)
                        continue;

                    bool isSubset = fp.All(mappedChanged.Contains);
                    if (!isSubset)
                        continue;

                    subset.Add((newPlacement, fp));
                    if (fp.Count == mappedChanged.Count)
                        exact.Add((newPlacement, fp));
                }

                if (subset.Count == 0)
                    continue; // try the other side

                outcome.Side = side;
                outcome.ExactCount = exact.Count;
                outcome.SubsetCount = subset.Count;

                // Prefer a unique exact-set match; fall back to a unique subset.
                if (exact.Count == 1)
                {
                    Fill(outcome, oldGrid, exact[0].placement, exact[0].fp, InferResult.Hit);
                    return outcome;
                }
                if (exact.Count > 1)
                {
                    outcome.Result = IsPromotionTie(exact, oldGrid) ? InferResult.Promotion : InferResult.Ambiguous;
                    return outcome;
                }
                if (subset.Count == 1)
                {
                    Fill(outcome, oldGrid, subset[0].placement, subset[0].fp, InferResult.Hit);
                    return outcome;
                }

                outcome.Result = IsPromotionTie(subset, oldGrid) ? InferResult.Promotion : InferResult.Ambiguous;
                return outcome;
            }

            outcome.Result = InferResult.NoMatch;
            return outcome;
        }

        private static void Fill(Inference o, char[,] oldGrid, string placement, HashSet<(int, int)> fp, InferResult r)
        {
            o.Result = r;
            o.InferredPlacement = placement;
            o.MoveText = DeriveMoveText(oldGrid, placement, fp);
        }

        // Footprint-derived "from->to" so logging doesn't depend on engine
        // coordinate conventions. from = a square that emptied; to = a square
        // that gained a piece of the mover's colour.
        private static string DeriveMoveText(char[,] oldGrid, string newPlacement, HashSet<(int, int)> fp)
        {
            char[,]? newGrid = ParsePlacement(newPlacement);
            if (newGrid == null)
                return string.Join(",", fp.Select(SquareName).OrderBy(x => x, StringComparer.Ordinal));

            string? from = null, to = null;
            foreach (var (f, r) in fp)
            {
                char before = oldGrid[f, r];
                char after = newGrid[f, r];
                if (before != '.' && after == '.')
                    from = SquareName((f, r));
                else if (after != '.' && before != after)
                    to = SquareName((f, r));
            }

            if (from != null && to != null)
                return from + to;
            return string.Join(",", fp.Select(SquareName).OrderBy(x => x, StringComparer.Ordinal));
        }

        // Tie is a promotion if every candidate shares the same changed-square
        // footprint and the moved-to square is on the back rank.
        private static bool IsPromotionTie(List<(string placement, HashSet<(int, int)> fp)> ties, char[,] oldGrid)
        {
            if (ties.Count < 2)
                return false;
            var first = ties[0].fp;
            foreach (var t in ties)
            {
                if (t.fp.Count != first.Count || !t.fp.All(first.Contains))
                    return false;
            }
            // back-rank target present?
            return first.Any(sq => sq.Item2 == 0 || sq.Item2 == 7);
        }

        // ---- FEN / square helpers (board-placement field only). ---------------
        private static string Placement(string fen)
        {
            if (string.IsNullOrEmpty(fen))
                return string.Empty;
            int sp = fen.IndexOf(' ');
            return sp < 0 ? fen : fen.Substring(0, sp);
        }

        // grid[file 0-7 (a-h), rankIdx 0-7 (0=rank1)] ; '.' = empty
        private static char[,]? ParsePlacement(string placement)
        {
            string[] rows = placement.Split('/');
            if (rows.Length != 8)
                return null;

            var grid = new char[8, 8];
            for (int f = 0; f < 8; f++)
                for (int r = 0; r < 8; r++)
                    grid[f, r] = '.';

            for (int rowIdx = 0; rowIdx < 8; rowIdx++)
            {
                int rankIdx = 7 - rowIdx; // rows[0] = rank 8 -> rankIdx 7
                int file = 0;
                foreach (char c in rows[rowIdx])
                {
                    if (file > 8)
                        return null;
                    if (char.IsDigit(c))
                    {
                        int n = c - '0';
                        for (int k = 0; k < n && file < 8; k++)
                            grid[file++, rankIdx] = '.';
                    }
                    else
                    {
                        if (file >= 8)
                            return null;
                        grid[file++, rankIdx] = c;
                    }
                }
            }
            return grid;
        }

        private static HashSet<(int, int)> DiffSquares(char[,] a, char[,] b)
        {
            var set = new HashSet<(int, int)>();
            for (int f = 0; f < 8; f++)
                for (int r = 0; r < 8; r++)
                    if (a[f, r] != b[f, r])
                        set.Add((f, r));
            return set;
        }

        // Image (file 0-7 left->right, rankFromTop 0-7 top->bottom) -> chess
        // (fileIdx 0-7 a-h, rankIdx 0-7 0=rank1), honouring board orientation.
        private static HashSet<(int, int)>? MapDiff(List<(int file, int rankTop)>? squares, bool flipped)
        {
            if (squares == null)
                return null;
            var set = new HashSet<(int, int)>();
            foreach (var (f, rTop) in squares)
            {
                if (f < 0 || f > 7 || rTop < 0 || rTop > 7)
                    continue;
                int fileIdx = flipped ? 7 - f : f;
                int rankIdx = flipped ? rTop : 7 - rTop;
                set.Add((fileIdx, rankIdx));
            }
            return set;
        }

        private static string SquareName((int file, int rank) s)
        {
            return string.Create(2, s, static (span, sq) =>
            {
                span[0] = (char)('a' + sq.file);
                span[1] = (char)('1' + sq.rank);
            });
        }

        // ---- Rolling aggregate so the log carries headline numbers. ----------
        private static readonly object _statsLock = new();
        private static int _att, _hit, _correct, _ambiguous, _nomatch, _promo, _nodiff, _diffCovers, _persp;

        private static void RecordStats(InferResult r, bool correct, bool diffCovers, bool perspMismatch)
        {
            int snapshotAtt;
            string? summary = null;
            lock (_statsLock)
            {
                _att++;
                if (diffCovers) _diffCovers++;
                if (perspMismatch) _persp++;
                switch (r)
                {
                    case InferResult.Hit: _hit++; if (correct) _correct++; break;
                    case InferResult.Ambiguous: _ambiguous++; break;
                    case InferResult.Promotion: _promo++; break;
                    case InferResult.NoMatch: _nomatch++; break;
                    case InferResult.NoDiff: _nodiff++; break;
                }
                snapshotAtt = _att;

                if (_att % 10 == 0)
                {
                    double hitPct = 100.0 * _hit / _att;
                    double corrPct = 100.0 * _correct / _att;
                    double coversPct = 100.0 * _diffCovers / _att;
                    summary =
                        $"[PREDICT-STATS] n={_att} hit={_hit}({hitPct.ToString("F0", CultureInfo.InvariantCulture)}%) " +
                        $"correct={_correct}({corrPct.ToString("F0", CultureInfo.InvariantCulture)}%) " +
                        $"ambiguous={_ambiguous} nomatch={_nomatch} promo={_promo} nodiff={_nodiff} " +
                        $"diffCovers={_diffCovers}({coversPct.ToString("F0", CultureInfo.InvariantCulture)}%) perspFix={_persp} " +
                        $"dropped={Interlocked.Read(ref _dropped)}";
                }
            }

            if (summary != null)
                Program.PredictLog(summary);
        }
    }
}
