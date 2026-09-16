namespace KeelMatrix.ResilienceSpec;

/// <summary>
/// An immutable, ordered script of downstream outcomes that a
/// <see cref="ScriptedHttpMessageHandler"/> hands out one step per attempt.
/// </summary>
/// <remarks>
/// Steps are consumed in attempt order. A script is bounded: it never contains more than
/// <see cref="MaximumSteps"/> steps, and an attempt that has no step left fails with
/// <see cref="ScriptExhaustedException"/> instead of inventing an outcome.
/// </remarks>
public sealed class HttpFaultScript
{
    /// <summary>The maximum number of steps one script may contain.</summary>
    public const int MaximumSteps = 512;

    private readonly HttpFault[] _faults;

    private HttpFaultScript(HttpFault[] faults) => _faults = faults;

    /// <summary>Gets the number of scripted steps.</summary>
    public int StepCount => _faults.Length;

    internal bool RequiresControlledClock => Array.Exists(_faults, fault => fault.Kind == HttpFaultKind.Delay);

    internal bool ContainsResponseFault => Array.Exists(_faults, fault => fault.ContainsResponseFault);

    internal bool ContainsExceptionFault => Array.Exists(_faults, fault => fault.ContainsExceptionFault);

    /// <summary>Creates a script from the given steps.</summary>
    /// <param name="faults">The steps, in attempt order. At least one step is required.</param>
    /// <returns>The script.</returns>
    public static HttpFaultScript Sequence(params HttpFault[] faults)
    {
        ArgumentNullException.ThrowIfNull(faults);

        if (faults.Length == 0)
        {
            throw new ArgumentException("A fault script must contain at least one step.", nameof(faults));
        }

        if (faults.Length > MaximumSteps)
        {
            throw new ArgumentOutOfRangeException(
                nameof(faults),
                faults.Length,
                $"A fault script must not contain more than {MaximumSteps} steps.");
        }

        var copy = new HttpFault[faults.Length];
        for (var index = 0; index < faults.Length; index++)
        {
            copy[index] = faults[index] ?? throw new ArgumentException("A fault script must not contain null steps.", nameof(faults));
        }

        return new HttpFaultScript(copy);
    }

    /// <summary>Creates a script that repeats one step for a fixed number of attempts.</summary>
    /// <param name="fault">The step to repeat.</param>
    /// <param name="count">The number of attempts the step covers, between one and <see cref="MaximumSteps"/>.</param>
    /// <returns>The script.</returns>
    public static HttpFaultScript Repeat(HttpFault fault, int count)
    {
        ArgumentNullException.ThrowIfNull(fault);

        if (count < 1 || count > MaximumSteps)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                $"A repeated script must cover between 1 and {MaximumSteps} attempts.");
        }

        var faults = new HttpFault[count];
        Array.Fill(faults, fault);
        return new HttpFaultScript(faults);
    }

    /// <summary>
    /// Creates a script that applies the same step to every attempt until the bounded step limit is reached.
    /// </summary>
    /// <param name="fault">The step to apply to every attempt.</param>
    /// <returns>The script.</returns>
    public static HttpFaultScript Always(HttpFault fault) => Repeat(fault, MaximumSteps);

    /// <summary>Describes the script without exposing request or response data.</summary>
    /// <returns>A short description such as <c>2 step(s): response 503 -&gt; response 200</c>.</returns>
    public override string ToString() =>
        $"{StepCount} step(s): {string.Join(" -> ", Array.ConvertAll(_faults, fault => fault.Describe()))}";

    /// <summary>Returns the step for one attempt ordinal, or <see langword="null"/> when the script is exhausted.</summary>
    /// <param name="ordinal">The one-based attempt ordinal.</param>
    /// <returns>The scripted step, or <see langword="null"/> when no step is left.</returns>
    internal HttpFault? StepAt(int ordinal) => ordinal >= 1 && ordinal <= _faults.Length ? _faults[ordinal - 1] : null;
}
