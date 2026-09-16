using System.Globalization;

namespace ResilienceSpec.Probe.Runner;

/// <summary>Per-item outcome against the feasibility gate.</summary>
internal enum GateVerdict
{
    Pass,
    Narrow,
    Fail,
}

/// <summary>Writes probe evidence and tracks hard expectations and per-item verdicts.</summary>
internal sealed class ProbeReport
{
    private readonly List<KeyValuePair<string, string>> _verdicts = new();
    private readonly List<string> _expectationFailures = new();
    private readonly TextWriter _writer;

    public ProbeReport(TextWriter? writer = null)
    {
        _writer = writer ?? Console.Out;
    }

    public int ExpectationFailureCount => _expectationFailures.Count;

    public int ExitCode => _expectationFailures.Count == 0 ? 0 : 1;

    public void Section(string title)
    {
        _writer.WriteLine();
        _writer.WriteLine($"=== {title} ===");
    }

    public void Fact(string label, object? value)
    {
        _writer.WriteLine($"  {label}: {Format(value)}");
    }

    public void Note(string text)
    {
        _writer.WriteLine($"  note: {text}");
    }

    public void Items(string label, IEnumerable<string> values)
    {
        var items = values as IReadOnlyList<string> ?? values.ToArray();
        _writer.WriteLine($"  {label}: {items.Count.ToString(CultureInfo.InvariantCulture)} entries");
        foreach (var item in items)
        {
            _writer.WriteLine($"    {item}");
        }
    }

    /// <summary>Records a hard expectation. A failed expectation fails the probe run.</summary>
    public bool Expect(bool condition, string statement)
    {
        if (!condition)
        {
            _expectationFailures.Add(statement);
        }

        _writer.WriteLine($"  expect {statement}: {(condition ? "PASS" : "FAIL")}");
        return condition;
    }

    public void Verdict(string item, GateVerdict verdict, string detail)
    {
        _verdicts.Add(new KeyValuePair<string, string>(
            item,
            $"{verdict.ToString().ToUpperInvariant()} - {detail}"));
    }

    public void PrintSummary()
    {
        _writer.WriteLine();
        _writer.WriteLine("=== Verdicts ===");
        var overall = GateVerdict.Pass;
        var item4Verdicts = 0;
        foreach (var (item, verdict) in _verdicts)
        {
            _writer.WriteLine($"  {item}: {verdict}");
            if (item[0] != '4')
            {
                continue;
            }

            item4Verdicts++;
            if (verdict.StartsWith("FAIL", StringComparison.Ordinal))
            {
                overall = GateVerdict.Fail;
            }
            else if (verdict.StartsWith("NARROW", StringComparison.Ordinal) && overall != GateVerdict.Fail)
            {
                overall = GateVerdict.Narrow;
            }
        }

        if (item4Verdicts > 0)
        {
            _writer.WriteLine($"  overall section 26 item 4 timing verdict: {overall.ToString().ToUpperInvariant()}");
        }

        _writer.WriteLine();
        _writer.WriteLine("=== Result ===");
        if (_expectationFailures.Count == 0)
        {
            _writer.WriteLine("RESULT: PASS - every hard expectation of the probe harness held.");
            return;
        }

        _writer.WriteLine($"RESULT: FAIL - {_expectationFailures.Count.ToString(CultureInfo.InvariantCulture)} hard expectation(s) did not hold.");
        foreach (var failure in _expectationFailures)
        {
            _writer.WriteLine($"  failed: {failure}");
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "(null)",
        TimeSpan span => Format(span),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public static string Format(TimeSpan span) =>
        string.Create(CultureInfo.InvariantCulture, $"{span.TotalMilliseconds:0.###} ms");
}
