using System.Diagnostics;

namespace CacheCleaner;

internal sealed class ScanDiagnostics : IDisposable
{
    private static readonly AsyncLocal<ScanDiagnostics?> Active = new();
    private readonly ScanDiagnostics? _previous;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly Dictionary<string, StageMetric> _stages = new();
    private readonly HashSet<string> _queries = new(StringComparer.OrdinalIgnoreCase);
    private string _stage = "Other";
    private long _stageStart;
    public List<StageMetric> Stages => _stages.Values.ToList();
    public int RepeatedSizeQueries { get; private set; }

    public sealed class StageMetric
    {
        public string Name { get; init; } = "";
        public double ElapsedMs { get; set; }
        public int SizeQueries { get; set; }
        public double SizeMs { get; set; }
        public int Commands { get; set; }
        public double CommandMs { get; set; }
    }

    private ScanDiagnostics() { _previous = Active.Value; Active.Value = this; }
    public static ScanDiagnostics Begin() => new();
    public static void NextStage(string name)
    {
        var current = Active.Value;
        if (current == null) return;
        current.EndStage();
        current._stage = name;
        current._stageStart = current._watch.ElapsedTicks;
    }
    private StageMetric Metric() => _stages.TryGetValue(_stage, out var metric) ? metric
        : _stages[_stage] = new StageMetric { Name = _stage };
    private void EndStage() => Metric().ElapsedMs +=
        (_watch.ElapsedTicks - _stageStart) * 1000d / Stopwatch.Frequency;

    public static IDisposable? SizeQuery(string path, string pattern, SearchOption option)
    {
        var current = Active.Value;
        if (current == null) return null;
        if (!current._queries.Add($"{path}\n{pattern}\n{option}")) current.RepeatedSizeQueries++;
        var metric = current.Metric();
        metric.SizeQueries++;
        return new Timing(ms => metric.SizeMs += ms);
    }
    public static IDisposable? Command()
    {
        var current = Active.Value;
        if (current == null) return null;
        var metric = current.Metric();
        metric.Commands++;
        return new Timing(ms => metric.CommandMs += ms);
    }
    private sealed class Timing(Action<double> record) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose() => record(Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
    }
    public void Dispose() { EndStage(); Active.Value = _previous; }
}
