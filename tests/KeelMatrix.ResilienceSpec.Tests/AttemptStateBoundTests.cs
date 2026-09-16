using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class AttemptStateBoundTests
{
    [Fact]
    public void TheRecordedTimelineBoundMatchesTheScriptStepBound()
    {
        Assert.Equal(HttpFaultScript.MaximumSteps, HttpAttemptReport.MaximumRecordedAttempts);
    }

    [Fact]
    public async Task ARunThatReachesExactlyTheBoundIsStillEvaluated()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Always(HttpFault.NetworkError()));
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryEveryExceptionHandler(HttpFaultScript.MaximumSteps - 1));
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        var report = scenario.Report;
        Assert.False(report.IsOverflowed);
        Assert.Equal(HttpFaultScript.MaximumSteps, report.AttemptCount);
        Assert.Equal(HttpFaultScript.MaximumSteps, report.Attempts.Count);
        Assert.Same(report, report.ShouldHaveAttempts(HttpFaultScript.MaximumSteps));
        Assert.Equal(ResilienceResultKind.DownstreamError, result.Kind);
    }

    [Fact]
    public async Task AClientThatRetriesHarnessFailuresOverflowsTheBoundedTimeline()
    {
        const int maximumRetries = 1500;

        using var scenario = new ResilienceScenario(HttpFaultScript.Always(HttpFault.NetworkError()));
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryEveryExceptionHandler(maximumRetries));
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        var report = scenario.Report;
        var served = maximumRetries + 1;

        // The run itself is unchanged: the client under test still sees the scripted chain, so the harness never
        // injects a failure of its own.
        Assert.Equal(ResilienceResultKind.ScriptExhausted, result.Kind);

        // The bound is enforced, and the served total stays exact instead of being silently truncated.
        Assert.True(report.IsOverflowed);
        Assert.Equal(served, report.AttemptCount);
        Assert.Equal(HttpAttemptReport.MaximumRecordedAttempts, report.Attempts.Count);
        Assert.Equal(HttpAttemptReport.MaximumRecordedAttempts, report.Attempts[report.Attempts.Count - 1].Ordinal);

        // The truncation is visible in the timeline itself, so it is never silent.
        var timeline = report.DescribeTimeline();
        Assert.Contains("timeline capped:", timeline, StringComparison.Ordinal);
        Assert.Contains(
            $"{served - HttpAttemptReport.MaximumRecordedAttempts} further attempt(s)",
            timeline,
            StringComparison.Ordinal);
        Assert.StartsWith(
            $"#{HttpAttemptReport.MaximumRecordedAttempts} ",
            report.Timeline[report.Attempts.Count - 1],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertionsOverAnOverflowedTimelineFailLoudlyInsteadOfCountingPartially()
    {
        const int maximumRetries = 1500;

        using var scenario = new ResilienceScenario(HttpFaultScript.Always(HttpFault.NetworkError()));
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryEveryExceptionHandler(maximumRetries));
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        var report = scenario.Report;
        var served = maximumRetries + 1;

        var exact = Assert.Throws<AttemptStateOverflowException>(() => report.ShouldHaveAttempts(served));
        Assert.Contains($"served {served} attempt(s)", exact.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"{HttpAttemptReport.MaximumRecordedAttempts} attempts (HttpAttemptReport.MaximumRecordedAttempts)",
            exact.Message,
            StringComparison.Ordinal);
        Assert.Contains("ScriptExhaustedException", exact.Message, StringComparison.Ordinal);

        // An assertion that would otherwise describe a truncated timeline as acceptable must not pass either.
        Assert.Throws<AttemptStateOverflowException>(() => report.ShouldHaveAtMostAttempts(served + 500));
        Assert.Throws<AttemptStateOverflowException>(() => report.ShouldNotHaveRetried(HttpMethod.Get));
        Assert.Throws<AttemptStateOverflowException>(() => report.ShouldHaveMethodSequence(HttpMethod.Get));
    }
}
