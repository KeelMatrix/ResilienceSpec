using System.Collections.Concurrent;
using System.Net;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class ScriptedDownstreamTests
{
    [Fact]
    public void NothingSentMeansNoAttempts()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()));

        scenario.Report.ShouldHaveAttempts(0);
        Assert.Empty(scenario.Report.Timeline);
        Assert.Null(scenario.Report.LastAttempt);
    }

    [Fact]
    public async Task SingleCallIsAnsweredInMemoryWithoutContent()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()));
        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var request = Chains.Request(HttpMethod.Get);
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content!.ReadAsStringAsync());
        Assert.Empty(response.Headers);
        scenario.Report.ShouldHaveAttempts(1).ShouldHaveMethodSequence(HttpMethod.Get);
        Assert.Equal(HttpAttemptOutcome.Response, scenario.Report.LastAttempt!.Outcome);
    }

    [Fact]
    public async Task ScriptedRetryAfterIsAttachedToTheResponseHeader()
    {
        var script = HttpFaultScript.Sequence(
            HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(2)));
        using var scenario = new ResilienceScenario(script);
        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var request = Chains.Request(HttpMethod.Get);
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(2), response.Headers.RetryAfter!.Delta);
        Assert.Equal(TimeSpan.FromSeconds(2), scenario.Report.LastAttempt!.RetryAfter);
    }

    [Fact]
    public async Task ExhaustedScriptFailsWithAnActionableDiagnostic()
    {
        var clock = Chains.CreateClock();
        var script = HttpFaultScript.Sequence(HttpFault.Response(HttpStatusCode.ServiceUnavailable));
        using var scenario = new ResilienceScenario(script, clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, clock.TimeProvider, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Get);
        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.ScriptExhausted).ShouldHaveException<ScriptExhaustedException>();
        scenario.Report.ShouldHaveAttempts(2);
        Assert.Equal(HttpAttemptOutcome.ScriptExhausted, scenario.Report.LastAttempt!.Outcome);
        Assert.Contains("provides only 1 step(s)", result.Exception!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleConsumerScriptFailsClearlyOnConcurrentUse()
    {
        var script = HttpFaultScript.Sequence(HttpFault.Timeout(), HttpFault.Success());
        using var scenario = new ResilienceScenario(script);
        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var caller = new CancellationTokenSource();
        using var first = Chains.Request(HttpMethod.Get, "/orders/1");
        using var second = Chains.Request(HttpMethod.Get, "/orders/2");

        var firstCall = invoker.SendAsync(first, caller.Token);
        var failure = await Assert.ThrowsAsync<ConcurrentScriptUseException>(
            () => invoker.SendAsync(second, CancellationToken.None));

        Assert.Contains("one scenario per logical call", failure.Message, StringComparison.Ordinal);

        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstCall);

        // The rejected caller never consumed a script step, so the timeline still describes one request.
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task LiveSnapshotsRemainSafeWhileAttemptsComplete()
    {
        var script = HttpFaultScript.Repeat(
            HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(1)),
            2);
        using var scenario = new ResilienceScenario(script);
        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var first = Chains.Request(HttpMethod.Get, "/orders/1");
        using var firstResponse = await invoker.SendAsync(first, CancellationToken.None);
        using var second = Chains.Request(HttpMethod.Get, "/orders/2");
        using var secondResponse = await invoker.SendAsync(second, CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, secondResponse.StatusCode);
        Assert.Equal(2, scenario.Report.AttemptCount);
    }

    [Fact]
    public async Task RequestContentSurvivesRetriesWithoutBeingRecorded()
    {
        var clock = Chains.CreateClock();
        var script = HttpFaultScript.Sequence(
            HttpFault.Response(HttpStatusCode.ServiceUnavailable),
            HttpFault.Success());
        using var scenario = new ResilienceScenario(script, clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.Zero, clock.TimeProvider, Chains.IsRetryableStatus));
        using var request = Chains.Request(HttpMethod.Post);
        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        Assert.Equal("{\"order\":42}", await request.Content!.ReadAsStringAsync());
        scenario.Report.ShouldHaveAttempts(2).ShouldHaveMethodSequence(HttpMethod.Post, HttpMethod.Post);
        Assert.DoesNotContain("order", scenario.Report.DescribeTimeline(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposingTheResultDisposesTheFinalResponse()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()));
        using var client = Chains.CreateClient(scenario.Handler, new ContentHandler());
        using var request = Chains.Request(HttpMethod.Get);

        var result = await scenario.SendAsync(client, request);
        var content = result.Response!.Content!;
        Assert.Equal("scripted-ok", await content.ReadAsStringAsync());

        result.Dispose();
        result.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReportIsAnImmutableSnapshot()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Repeat(HttpFault.Success(), 2));
        var before = scenario.Report;

        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var request = Chains.Request(HttpMethod.Get);
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(0, before.AttemptCount);
        Assert.Equal(1, scenario.Report.AttemptCount);
    }

    [Fact]
    public async Task AtomicCompletionPublicationSurvivesAPausedPublicationBoundary()
    {
        var publicationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seam = new AttemptPublicationSeam(point =>
        {
            if (point == AttemptPublicationPoint.AfterCompletionPublication)
            {
                publicationReached.TrySetResult();
                releasePublication.Task.GetAwaiter().GetResult();
            }
        });
        var entry = new AttemptEntry(
            1,
            HttpMethod.Get,
            HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(1)),
            startedAfter: null,
            startedAfterIsExact: false,
            publicationSeam: seam);

        var completion = Task.Run(() => entry.Complete(
            HttpAttemptOutcome.Response,
            HttpStatusCode.ServiceUnavailable,
            TimeSpan.FromSeconds(1)));
        await publicationReached.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var snapshot = entry.ToAttempt();

        releasePublication.SetResult();
        await completion;

        Assert.Equal(HttpAttemptOutcome.Response, snapshot.Outcome);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, snapshot.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), snapshot.RetryAfter);
    }

    [Fact]
    public async Task LiveSnapshotsNeverExposePartiallyPublishedResponseAttempts()
    {
        const int callCount = HttpAttemptReport.MaximumRecordedAttempts;
        var errors = new ConcurrentQueue<Exception>();
        var snapshotStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotsFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Repeat(HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(1)), callCount));
        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);

        var snapshotter = Task.Run(() =>
        {
            try
            {
                while (!snapshotsFinished.Task.IsCompleted)
                {
                    snapshotStarted.TrySetResult();
                    var report = scenario.Report;
                    foreach (var attempt in report.Attempts)
                    {
                        if (attempt.Outcome == HttpAttemptOutcome.Response &&
                            (attempt.StatusCode != HttpStatusCode.ServiceUnavailable ||
                             attempt.RetryAfter != TimeSpan.FromSeconds(1)))
                        {
                            errors.Enqueue(new InvalidOperationException(
                                "A live response attempt exposed incomplete status or retry metadata."));
                        }
                    }

                    Thread.Yield();
                }
            }
            catch (Exception exception)
            {
                errors.Enqueue(exception);
            }
        });

        await snapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var responseList = new List<HttpResponseMessage>(callCount);
        for (var index = 0; index < callCount; index++)
        {
            responseList.Add(await SendOneAsync());
        }

        using var responses = new ResponseCollection(responseList);
        snapshotsFinished.SetResult();
        await snapshotter;

        Assert.Empty(errors);
        var final = scenario.Report;
        final.ShouldHaveAttempts(callCount);
        Assert.All(final.Attempts, attempt =>
        {
            Assert.Equal(HttpAttemptOutcome.Response, attempt.Outcome);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, attempt.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), attempt.RetryAfter);
        });

        async Task<HttpResponseMessage> SendOneAsync()
        {
            using var request = Chains.Request(HttpMethod.Get);
            return await invoker.SendAsync(request, CancellationToken.None);
        }
    }
}

internal sealed class ResponseCollection : IDisposable
{
    private readonly IReadOnlyList<HttpResponseMessage> _responses;

    internal ResponseCollection(IReadOnlyList<HttpResponseMessage> responses) => _responses = responses;

    public void Dispose()
    {
        foreach (var response in _responses)
        {
            response.Dispose();
        }
    }
}
