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
/// Represents the failure raised when a request reaches a scenario's terminal handler without the active logical-call lease.
/// </summary>
/// <remarks>
/// A request must start at <see cref="ResilienceScenario.SendAsync"/>. Direct sends through the exposed terminal handler
/// or through a client configured with the adapter fail before consuming a script step or changing the report.
/// </remarks>
public sealed class ScenarioConsumedException : InvalidOperationException
{
    internal ScenarioConsumedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Represents the failure raised when two attempts overlap inside one logical call.
/// </summary>
/// <remarks>
/// A request that bypasses the owning scenario's logical-call runner raises <see cref="ScenarioConsumedException"/>
/// instead. This exception is reserved for a genuine overlapping attempt that reached the terminal handler from the
/// active logical call.
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
