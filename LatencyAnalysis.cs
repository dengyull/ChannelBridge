using System.Numerics;

namespace ChannelBridge;

public sealed record LatencyTrial(double? Milliseconds, double Confidence, string Error = "");
public sealed class LatencyRow
{
    public string Role { get; set; } = "";
    public List<LatencyTrial> Initial { get; set; } = new();
    public List<LatencyTrial> Verified { get; set; } = new();
    public int CompensationMs { get; set; }
    public double? Median => LatencyAnalysis.Median(Initial);
    public double? Mean => LatencyAnalysis.Mean(Initial);
    public double? Spread => LatencyAnalysis.Spread(Initial);
    public double? VerifiedMedian => LatencyAnalysis.Median(Verified);
    public double? VerifiedMean => LatencyAnalysis.Mean(Verified);
}
public sealed record CalibrationCommand(string Id, string RunId, DateTime ExpiresUtc, string MicrophoneId, string Revision, SurroundProfile? Profile, bool Cancel = false);
public sealed record CalibrationReport(string Id, bool Success, bool Applied, string Message, List<LatencyRow> Rows, double? MeanSpreadMs = null, double? MedianSpreadMs = null);
public sealed record SignalMatch(double Seconds, double Confidence);

public static class LatencyAnalysis
{
    public const int Rate = 12000;
    public const double ToleranceMs = 10;
    public const double AlignmentToleranceMs = 5;
    public static double? Median(IReadOnlyList<LatencyTrial> trials) => Complete(trials) ? trials.Select(t => t.Milliseconds!.Value).Order().ElementAt(1) : null;
    public static double? Mean(IReadOnlyList<LatencyTrial> trials) => Complete(trials) ? trials.Average(t => t.Milliseconds!.Value) : null;
    public static double? Spread(IReadOnlyList<LatencyTrial> trials) => Complete(trials) ? trials.Max(t => t.Milliseconds!.Value) - trials.Min(t => t.Milliseconds!.Value) : null;
    static bool Complete(IReadOnlyList<LatencyTrial> trials) => trials.Count == 3 && trials.All(t => t.Milliseconds is double d && double.IsFinite(d) && d >= 0 && d <= 2000 && t.Error.Length == 0);
    public static bool Stable(IReadOnlyList<LatencyTrial> trials) => Complete(trials) && Spread(trials) <= ToleranceMs && Math.Abs(Mean(trials)!.Value - Median(trials)!.Value) <= ToleranceMs / 2;
    public static void Plan(List<LatencyRow> rows)
    {
        if (rows.Count < 2 || rows.Any(r => !Stable(r.Initial))) throw new InvalidOperationException("初测未通过稳定性验证：每只音箱必须有三次有效结果，极差 ≤ 10 ms，平均值与中位数差 ≤ 5 ms。请降低环境噪声或检查麦克风后重测。");
        double slowest = rows.Max(r => r.Median!.Value);
        foreach (var row in rows)
        {
            row.CompensationMs = (int)Math.Round(slowest - row.Median!.Value, MidpointRounding.AwayFromZero);
            if (row.CompensationMs > 500) throw new InvalidOperationException("需要的补偿超过 500 ms，未应用。请检查异常设备。");
        }
    }
    public static (double MeanSpread, double MedianSpread) Verify(List<LatencyRow> rows)
    {
        if (rows.Count < 2 || rows.Any(r => !Stable(r.Verified))) throw new InvalidOperationException("补偿后的三次复测不稳定，未保存补偿。");
        double means = rows.Max(r => r.VerifiedMean!.Value) - rows.Min(r => r.VerifiedMean!.Value);
        double medians = rows.Max(r => r.VerifiedMedian!.Value) - rows.Min(r => r.VerifiedMedian!.Value);
        if (means > AlignmentToleranceMs || medians > AlignmentToleranceMs) throw new InvalidOperationException($"复测未对齐：平均延迟极差 {means:0.00} ms，中位数极差 {medians:0.00} ms，要求均 ≤ 5 ms。未保存补偿。");
        return (means, medians);
    }
    public static double Duration(bool bass) => bass ? .30 : .12;
    public static float Chirp(double t, bool bass)
    {
        double duration = Duration(bass); if (t < 0 || t >= duration) return 0;
        double f0 = bass ? 40 : 500, f1 = bass ? 140 : 4500;
        double phase = 2 * Math.PI * (f0 * t + (f1 - f0) * t * t / (2 * duration));
        return (float)(Math.Sin(phase) * .5 * (1 - Math.Cos(2 * Math.PI * t / duration)));
    }
    public static float[] Resample(float[] input, int rate)
    {
        var output = new float[(int)((long)input.Length * Rate / rate)];
        for (int i = 0; i < output.Length; i++)
        {
            double position = (double)i * rate / Rate; int a = (int)position;
            output[i] = input[a] + (input[Math.Min(a + 1, input.Length - 1)] - input[a]) * (float)(position - a);
        }
        return output;
    }
    public static SignalMatch Detect(float[] input, bool bass, bool reference, CancellationToken cancellation = default)
    {
        var template = Enumerable.Range(0, (int)(Duration(bass) * Rate)).Select(i => (double)Chirp((double)i / Rate, bass)).ToArray();
        double mean = template.Average(); for (int i = 0; i < template.Length; i++) template[i] -= mean;
        if (input.Length < template.Length + Rate / 5 || input.Any(x => !float.IsFinite(x))) throw new IOException("录音过短或数据无效。");
        if (!reference && input.Count(x => Math.Abs(x) >= .98) > input.Length / 1000) throw new IOException("麦克风削波，请降低输入增益。");
        int n = 1; while (n < input.Length + template.Length) n <<= 1;
        var a = new Complex[n]; var b = new Complex[n];
        for (int i = 0; i < input.Length; i++) a[i] = input[i];
        for (int i = 0; i < template.Length; i++) b[i] = template[template.Length - i - 1];
        Fft(a, false, cancellation); Fft(b, false, cancellation);
        for (int i = 0; i < n; i++) a[i] *= b[i]; Fft(a, true, cancellation);
        double energy = template.Sum(x => x * x), floor = input.Take(Rate / 8).Select(x => (double)x * x).Average();
        var sum = new double[input.Length + 1]; var squares = new double[input.Length + 1];
        for (int i = 0; i < input.Length; i++) { sum[i + 1] = sum[i] + input[i]; squares[i + 1] = squares[i] + input[i] * (double)input[i]; }
        var scores = new double[input.Length - template.Length + 1];
        double best = 0; int index = -1;
        for (int i = Rate / 8; i < scores.Length; i++)
        {
            double s = sum[i + template.Length] - sum[i];
            double e = squares[i + template.Length] - squares[i] - s * s / template.Length;
            if (e / template.Length < Math.Max(1e-9, floor * (reference ? 1.1 : 6.25))) continue;
            double score = Math.Abs(a[i + template.Length - 1].Real) / Math.Sqrt(Math.Max(1e-30, e * energy));
            scores[i] = score; if (score > best) { best = score; index = i; }
        }
        if (index < 0 || best < (reference ? .75 : .25)) throw new IOException($"未可靠检测到测试声（匹配度 {best:0.00}），请检查音量、麦克风位置及环境噪声。");
        int exclusion = (int)(Rate * (bass ? .04 : .012));
        double second = scores.Where((_, i) => Math.Abs(i - index) > exclusion).DefaultIfEmpty().Max();
        if (second > best * .90) throw new IOException("检测到多个接近的到达峰，反射或干扰过强；未采用不确定结果。");
        return new SignalMatch((double)index / Rate, Math.Min(1, best));
    }
    static void Fft(Complex[] x, bool inverse, CancellationToken cancellation)
    {
        int n = x.Length;
        for (int i = 1, j = 0; i < n; i++) { int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) (x[i], x[j]) = (x[j], x[i]); }
        for (int length = 2; length <= n; length <<= 1)
        {
            cancellation.ThrowIfCancellationRequested();
            var step = Complex.FromPolarCoordinates(1, (inverse ? 2 : -2) * Math.PI / length);
            for (int i = 0; i < n; i += length)
            {
                Complex w = Complex.One;
                for (int j = 0; j < length / 2; j++) { var u = x[i + j]; var v = x[i + j + length / 2] * w; x[i + j] = u + v; x[i + j + length / 2] = u - v; w *= step; }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) x[i] /= n;
    }
}
