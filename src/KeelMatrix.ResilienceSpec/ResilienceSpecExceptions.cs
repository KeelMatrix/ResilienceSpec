namespace KeelMatrix.ResilienceSpec;

/// <summary>
/// Represents the failure raised when the scripted downstream is called more times than the script provides steps.
/// </summary>
/// <remarks>
/// This is a harness problem rather than a resilience problem: either the client under test retried more times
/// than the script covers, or the script does not describe the expected attempt count.
/// </remarks>
public sealed class ScriptExhaustedException : InvalidOperationException
{
    internal ScriptExhaustedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Represents the failure raised when an assertion cannot judge a run because the scripted downstream served more
/// attempts than <see cref="HttpAttemptReport.MaximumRecordedAttempts"/> and the recorded timeline is incomplete.
/// </summary>
/// <remarks>
/// Per-attempt state is bounded so that a client under test cannot grow attempt records without limit inside the
/// test process. The bound is therefore never bypassed silently: the served attempt count stays exact and is
/// reported, while assertions over attempt state fail with this exception instead of evaluating a truncated
/// timeline.
/// </remarks>
public sealed class AttemptStateOverflowException : InvalidOperationException
{
    internal AttemptStateOverflowException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Represents the failure raised when one script is consumed by more than one in-flight request.
/// </summary>
/// <remarks>
/// A script has deterministic single-consumer semantics by default. Use
/// <see cref="ScriptConcurrency.AllowConcurrent"/> only when the scenario under test really is concurrent, and
/// use one scenario per test case otherwise.
/// </remarks>
public sealed class ConcurrentScriptUseException : InvalidOperationException
{
    internal ConcurrentScriptUseException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Represents the failure raised when a deterministic timing capability is requested without a controllable clock.
/// </summary>
/// <remarks>
/// Timing behaviour is only ever asserted against an injected <see cref="TimeProvider"/> whose advance operation
/// the scenario owns. The package never falls back to wall-clock sleeps or elapsed-time tolerances.
/// </remarks>
public sealed class MissingTimeProviderException : InvalidOperationException
{
    internal MissingTimeProviderException(string message)
        : base(message)
    {
    }
}

/// <summary>Represents the failure raised when a resilience expectation does not hold for the observed timeline.</summary>
public sealed class ResilienceAssertionException : Exception
{
    internal ResilienceAssertionException(string message)
        : base(message)
    {
    }
}
