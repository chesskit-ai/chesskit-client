using OpenCvSharp;

namespace ChessKit
{
    internal static class LastMoveHighlightDetector
    {
        internal sealed class HighlightResult
        {
            public bool HasReliablePair { get; init; }
            public double Confidence { get; init; }
            public List<HighlightSquare> Squares { get; init; } = new();
            public List<HighlightSquare> Candidates { get; init; } = new();
        }

        internal sealed class HighlightSquare
        {
            public int File { get; init; }
            public int RankFromTop { get; init; }
            public double Score { get; init; }
            public double Coverage { get; init; }
            public double MeanDistance { get; init; }
        }

        private readonly struct LabColor
        {
            public LabColor(double l, double a, double b)
            {
                L = l;
                A = a;
                B = b;
            }

            public double L { get; }
            public double A { get; }
            public double B { get; }
        }

        private readonly struct PatchSample
        {
            public PatchSample(int file, int rankFromTop, int parity, LabColor color)
            {
                File = file;
                RankFromTop = rankFromTop;
                Parity = parity;
                Color = color;
            }

            public int File { get; }
            public int RankFromTop { get; }
            public int Parity { get; }
            public LabColor Color { get; }
        }

        private static readonly (double X, double Y)[] PatchCenters =
        {
            // Last-move highlights are visible in the square background, while
            // pieces often cover the center. Sample gutters/corners so occupied
            // destination squares still get detected.
            (0.10, 0.10), (0.22, 0.10), (0.50, 0.10), (0.78, 0.10), (0.90, 0.10),
            (0.10, 0.22),                                                   (0.90, 0.22),
            (0.10, 0.50),                                                   (0.90, 0.50),
            (0.10, 0.78),                                                   (0.90, 0.78),
            (0.10, 0.90), (0.22, 0.90), (0.50, 0.90), (0.78, 0.90), (0.90, 0.90),
            (0.18, 0.18), (0.82, 0.18), (0.18, 0.82), (0.82, 0.82)
        };

        public static bool TryDetect(Mat boardView, out HighlightResult result)
        {
            result = new HighlightResult();

            if (boardView == null || boardView.Empty() || boardView.Width < 160 || boardView.Height < 160)
                return false;

            try
            {
                using var lab = new Mat();
                if (boardView.Channels() == 1)
                {
                    using var bgr = new Mat();
                    Cv2.CvtColor(boardView, bgr, ColorConversionCodes.GRAY2BGR);
                    Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
                }
                else if (boardView.Channels() == 4)
                {
                    using var bgr = new Mat();
                    Cv2.CvtColor(boardView, bgr, ColorConversionCodes.BGRA2BGR);
                    Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
                }
                else
                {
                    Cv2.CvtColor(boardView, lab, ColorConversionCodes.BGR2Lab);
                }

                var samples = CollectPatchSamples(lab);
                if (samples.Count < 64)
                    return false;

                LabColor[] baselines =
                {
                    BuildMedianBaseline(samples.Where(s => s.Parity == 0).Select(s => s.Color).ToList()),
                    BuildMedianBaseline(samples.Where(s => s.Parity == 1).Select(s => s.Color).ToList())
                };

                var candidates = new List<HighlightSquare>();
                for (int rank = 0; rank < 8; rank++)
                {
                    for (int file = 0; file < 8; file++)
                    {
                        var squareSamples = samples
                            .Where(s => s.File == file && s.RankFromTop == rank)
                            .ToList();

                        if (squareSamples.Count == 0)
                            continue;

                        LabColor baseline = baselines[(file + rank) & 1];
                        double totalDistance = 0;
                        int shifted = 0;
                        foreach (var sample in squareSamples)
                        {
                            double distance = WeightedLabDistance(sample.Color, baseline);
                            totalDistance += distance;
                            if (distance >= 16.0)
                                shifted++;
                        }

                        double meanDistance = totalDistance / squareSamples.Count;
                        double coverage = shifted / (double)squareSamples.Count;
                        double score = meanDistance * (0.65 + coverage);

                        if (coverage >= 0.45 && meanDistance >= 11.5 && score >= 17.0)
                        {
                            candidates.Add(new HighlightSquare
                            {
                                File = file,
                                RankFromTop = rank,
                                Score = score,
                                Coverage = coverage,
                                MeanDistance = meanDistance
                            });
                        }
                    }
                }

                candidates = candidates
                    .OrderByDescending(c => c.Score)
                    .ToList();

                result = BuildResult(candidates);
                return result.HasReliablePair;
            }
            catch (Exception ex) when (ex is OpenCVException || ex is ArgumentException || ex is InvalidOperationException || ex is AccessViolationException)
            {
                result = new HighlightResult();
                return false;
            }
        }

        private static HighlightResult BuildResult(List<HighlightSquare> candidates)
        {
            if (candidates.Count < 2)
            {
                return new HighlightResult
                {
                    HasReliablePair = false,
                    Candidates = candidates
                };
            }

            HighlightSquare first = candidates[0];
            HighlightSquare second = candidates[1];
            double thirdScore = candidates.Count > 2 ? candidates[2].Score : 0;
            double pairFloor = Math.Min(first.Score, second.Score);
            double pairCoverage = (first.Coverage + second.Coverage) / 2.0;
            double clutterPenalty = thirdScore > 0 ? Math.Clamp(thirdScore / Math.Max(1, pairFloor), 0, 1) * 0.28 : 0;
            double confidence = Math.Clamp((pairFloor / 42.0) * 0.55 + pairCoverage * 0.55 - clutterPenalty, 0, 1);

            bool tooMuchClutter = candidates.Count >= 7 || (thirdScore > 0 && thirdScore > pairFloor * 0.88);
            bool reliable = !tooMuchClutter &&
                confidence >= 0.52 &&
                pairFloor >= 17.0 &&
                pairCoverage >= 0.45;

            return new HighlightResult
            {
                HasReliablePair = reliable,
                Confidence = confidence,
                Squares = reliable ? new List<HighlightSquare> { first, second } : new List<HighlightSquare>(),
                Candidates = candidates
            };
        }

        private static List<PatchSample> CollectPatchSamples(Mat lab)
        {
            var samples = new List<PatchSample>(64 * PatchCenters.Length);

            for (int rank = 0; rank < 8; rank++)
            {
                int y0 = (int)Math.Round(rank * lab.Height / 8.0);
                int y1 = (int)Math.Round((rank + 1) * lab.Height / 8.0);
                int squareH = Math.Max(1, y1 - y0);

                for (int file = 0; file < 8; file++)
                {
                    int x0 = (int)Math.Round(file * lab.Width / 8.0);
                    int x1 = (int)Math.Round((file + 1) * lab.Width / 8.0);
                    int squareW = Math.Max(1, x1 - x0);
                    int patch = Math.Max(3, Math.Min(squareW, squareH) / 12);

                    foreach (var center in PatchCenters)
                    {
                        int cx = x0 + (int)Math.Round(squareW * center.X);
                        int cy = y0 + (int)Math.Round(squareH * center.Y);
                        int px = Math.Clamp(cx - patch / 2, x0, Math.Max(x0, x1 - patch));
                        int py = Math.Clamp(cy - patch / 2, y0, Math.Max(y0, y1 - patch));
                        int pw = Math.Min(patch, x1 - px);
                        int ph = Math.Min(patch, y1 - py);
                        if (pw <= 0 || ph <= 0)
                            continue;

                        using var roi = new Mat(lab, new Rect(px, py, pw, ph));
                        Scalar mean = Cv2.Mean(roi);
                        samples.Add(new PatchSample(
                            file,
                            rank,
                            (file + rank) & 1,
                            new LabColor(mean.Val0, mean.Val1, mean.Val2)));
                    }
                }
            }

            return samples;
        }

        private static LabColor BuildMedianBaseline(List<LabColor> colors)
        {
            if (colors.Count == 0)
                return new LabColor();

            return new LabColor(
                Median(colors.Select(c => c.L).ToList()),
                Median(colors.Select(c => c.A).ToList()),
                Median(colors.Select(c => c.B).ToList()));
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0)
                return 0;

            values.Sort();
            int mid = values.Count / 2;
            return values.Count % 2 == 0
                ? (values[mid - 1] + values[mid]) / 2.0
                : values[mid];
        }

        private static double WeightedLabDistance(LabColor a, LabColor b)
        {
            double dL = (a.L - b.L) * 0.55;
            double dA = (a.A - b.A) * 1.15;
            double dB = (a.B - b.B) * 1.15;
            return Math.Sqrt(dL * dL + dA * dA + dB * dB);
        }
    }
}
