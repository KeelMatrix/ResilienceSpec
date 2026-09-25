using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace KeelMatrix.ResilienceSpec.Tests;

public sealed class ClientCompositionTests
{
    [Fact]
    public async Task ConcurrentUseKindIsReservedForOverlappingAttemptsInsideOneLogicalCall()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Timeout()));
        using var client = Chains.CreateClient(scenario.Handler, new ConcurrentAttemptHandler());
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.ConcurrentUse)
            .ShouldHaveException<ConcurrentScriptUseException>();
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task OuterHandlersSeeOneRequestWhileTheRetryHappensInside()
    {
        var clock = Chains.CreateClock();
        var observer = new ChainObserver();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RecordingHandler(observer, "outer"),
            new RetryHandler(1, TimeSpan.FromSeconds(1), clock.TimeProvider, Chains.IsRetryableStatus),
            new RecordingHandler(observer, "inner"));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        Assert.Equal(
            [
                "outer:request",
                "inner:request",
                "inner:response 503",
                "inner:request",
                "inner:response 200",
                "outer:response 200",
            ],
            observer.Events);
        Assert.Equal(1, observer.Events.Count(entry => entry == "outer:request"));
    }

    [Fact]
    public async Task NamedClientsKeepSeparateScriptsAndReports()
    {
        var clock = Chains.CreateClock();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock.TimeProvider);

        using var orders = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock);
        using var billing = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()), clock);

        services.AddHttpClient("orders")
            .AddHttpMessageHandler(() => new RetryHandler(1, TimeSpan.FromSeconds(1), clock.TimeProvider, Chains.IsRetryableStatus))
            .UseResilienceSpecDownstream(orders);
        services.AddHttpClient("billing").UseResilienceSpecDownstream(billing);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using (var result = await orders.SendAsync(factory.CreateClient("orders"), Chains.Request(HttpMethod.Get)))
        {
            result.ShouldHaveStatus(HttpStatusCode.OK);
        }

        using (var result = await billing.SendAsync(factory.CreateClient("billing"), Chains.Request(HttpMethod.Get)))
        {
            result.ShouldHaveStatus(HttpStatusCode.OK);
        }

        orders.Report.ShouldHaveAttempts(2);
        billing.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task TypedClientsComposeThroughTheAdapter()
    {
        var clock = Chains.CreateClock();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock.TimeProvider);

        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock);

        services.AddHttpClient<OrdersClient>()
            .AddHttpMessageHandler(() => new RetryHandler(1, TimeSpan.FromSeconds(1), clock.TimeProvider, Chains.IsRetryableStatus))
            .UseResilienceSpecDownstream(scenario);

        using var provider = services.BuildServiceProvider();
        var typed = provider.GetRequiredService<OrdersClient>();

        using var result = await scenario.SendAsync(typed.Client, Chains.Request(HttpMethod.Get));

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveAttempts(2);
    }

    [Fact]
    public void AdapterFailsClearlyWhenTheControllableClockIsNotRegistered()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Success()),
            clock);
        var services = new ServiceCollection();
        services.AddHttpClient("orders").UseResilienceSpecDownstream(scenario);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var failure = Assert.Throws<MissingTimeProviderException>(() => factory.CreateClient("orders"));
        Assert.Contains("resolved TimeProvider is missing", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterRejectsARegisteredButDifferentClockInstance()
    {
        var scenarioClock = Chains.CreateClock();
        var registeredClock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Success()),
            scenarioClock);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(registeredClock.TimeProvider);
        services.AddHttpClient("orders").UseResilienceSpecDownstream(scenario);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var failure = Assert.Throws<MissingTimeProviderException>(() => factory.CreateClient("orders"));
        Assert.Contains("different instance", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterRejectsMissingArguments()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()));
        Assert.Throws<ArgumentNullException>(() => ResilienceSpecHttpClientBuilderExtensions.UseResilienceSpecDownstream(null!, scenario));

        var services = new ServiceCollection();
        var builder = services.AddHttpClient("orders");
        Assert.Throws<ArgumentNullException>(() => builder.UseResilienceSpecDownstream(null!));
    }

    [Fact]
    public async Task DirectHandlerSendBeforeLogicalCallFailsClosedWithoutConsumingAStep()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success(), HttpFault.Success()));
        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var directRequest = Chains.Request(HttpMethod.Get, "/direct-before");

        var failure = await Assert.ThrowsAsync<ScenarioConsumedException>(
            () => invoker.SendAsync(directRequest, CancellationToken.None));

        Assert.Contains("ResilienceScenario.SendAsync", failure.Message, StringComparison.Ordinal);
        scenario.Report.ShouldHaveAttempts(0);

        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get, "/supported");
        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task DirectHandlerSendAfterSettlementFailsClosedWithoutMutatingTheReport()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success(), HttpFault.Success()));
        using var client = Chains.CreateClient(scenario.Handler);
        using var request = Chains.Request(HttpMethod.Get, "/supported");
        using var result = await scenario.SendAsync(client, request);
        result.ShouldHaveStatus(HttpStatusCode.OK);

        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var directRequest = Chains.Request(HttpMethod.Get, "/direct-after");
        await Assert.ThrowsAsync<ScenarioConsumedException>(
            () => invoker.SendAsync(directRequest, CancellationToken.None));

        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task FactoryClientDirectSendRequiresTheScenarioRunner()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success(), HttpFault.Success()));
        var services = new ServiceCollection();
        services.AddHttpClient("orders").UseResilienceSpecDownstream(scenario);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

        var failure = await Assert.ThrowsAsync<ScenarioConsumedException>(
            () => client.GetAsync("https://orders.invalid/direct-factory"));

        Assert.Contains("ResilienceScenario.SendAsync", failure.Message, StringComparison.Ordinal);
        scenario.Report.ShouldHaveAttempts(0);

        using var request = Chains.Request(HttpMethod.Get, "/supported-factory");
        using var result = await scenario.SendAsync(client, request);
        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task DirectSequentialTopLevelCallsCannotAggregateIntoOneAssertionReport()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Repeat(HttpFault.Success(), 2));
        using var invoker = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);

        using var first = Chains.Request(HttpMethod.Post, "/first");
        using var second = Chains.Request(HttpMethod.Post, "/second");
        await Assert.ThrowsAsync<ScenarioConsumedException>(() => invoker.SendAsync(first, CancellationToken.None));
        await Assert.ThrowsAsync<ScenarioConsumedException>(() => invoker.SendAsync(second, CancellationToken.None));

        scenario.Report.ShouldHaveAttempts(0).ShouldNotHaveRetried(HttpMethod.Post);
    }

    [Fact]
    public async Task DirectHandlerSendDuringRetryBackoffFailsWithoutConsumingTheRetryStep()
    {
        var clock = Chains.CreateClock();
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(1)),
                HttpFault.Success()),
            clock,
            new ResilienceScenarioOptions
            {
                AdvanceStep = TimeSpan.FromSeconds(1),
                VirtualBudget = TimeSpan.FromSeconds(2),
                ObservationWindow = TimeSpan.FromMilliseconds(25),
            });
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.FromSeconds(1),
                timeProvider: clock.TimeProvider,
                shouldRetryResponse: Chains.IsRetryableStatus,
                retryStarted: retryStarted,
                retryRelease: retryRelease));
        using var direct = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var request = Chains.Request(HttpMethod.Get, "/supported");
        var run = scenario.SendAsync(client, request);

        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var directRequest = Chains.Request(HttpMethod.Get, "/direct-backoff");
        await Assert.ThrowsAsync<ScenarioConsumedException>(
            () => direct.SendAsync(directRequest, CancellationToken.None));
        scenario.Report.ShouldHaveAttempts(1);

        retryRelease.SetResult();
        using var result = await run;
        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveAttempts(2).ShouldRespectRetryAfter().ShouldHaveRetryDelay(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DirectHandlerSendAfterObservationCutoffCannotMutateTheReport()
    {
        var lateResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout(), HttpFault.Success()),
            Chains.CreateClock(),
            new ResilienceScenarioOptions
            {
                AdvanceClock = false,
                PendingObservation = TimeSpan.FromMilliseconds(20),
                CleanupTimeout = TimeSpan.FromMilliseconds(40),
            });
        using var stubborn = new IgnoreCancellationHandler(lateResponse);
        using var client = Chains.CreateClient(scenario.Handler, stubborn);
        using var request = Chains.Request(HttpMethod.Get, "/supported");

        using var result = await scenario.SendAsync(client, request);
        result.ShouldBePending();
        Assert.True(scenario.Report.IsObservationCutoff);

        using var direct = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var directRequest = Chains.Request(HttpMethod.Get, "/direct-cutoff");
        await Assert.ThrowsAsync<ScenarioConsumedException>(
            () => direct.SendAsync(directRequest, CancellationToken.None));
        scenario.Report.ShouldHaveAttempts(1);

        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        lateResponse.SetResult(response);
    }

    internal sealed class OrdersClient(HttpClient client)
    {
        internal HttpClient Client { get; } = client;
    }
}

public sealed class CancellationTests
{
    [Fact]
    public async Task CallerCancellationEndsTheRunDeterministically()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout(), HttpFault.Success()),
            clock,
            new ResilienceScenarioOptions
            {
                AdvanceClock = false,
                PendingObservation = TimeSpan.FromMilliseconds(200),
            });
        using var client = Chains.CreateClient(scenario.Handler);
        using var caller = new CancellationTokenSource();
        using var request = Chains.Request(HttpMethod.Get);

        var run = scenario.SendAsync(client, request, caller.Token);
        await caller.CancelAsync();
        using var result = await run;

        result.ShouldHaveKind(ResilienceResultKind.Canceled);
        scenario.Report.ShouldHaveAttempts(1);
        Assert.Equal(HttpAttemptOutcome.Abandoned, scenario.Report.Attempts[0].Outcome);

        using var secondRequest = Chains.Request(HttpMethod.Get, "/orders/second");
        await Assert.ThrowsAsync<ScenarioConsumedException>(() => scenario.SendAsync(client, secondRequest));

        using var direct = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var directRequest = Chains.Request(HttpMethod.Get, "/orders/direct-after-cancellation");
        await Assert.ThrowsAsync<ScenarioConsumedException>(
            () => direct.SendAsync(directRequest, CancellationToken.None));
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task AScenarioRejectsASecondSequentialCallAfterANormalResponse()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()));
        using var client = Chains.CreateClient(scenario.Handler);
        using var firstRequest = Chains.Request(HttpMethod.Get, "/orders/1");
        using var first = await scenario.SendAsync(client, firstRequest);
        first.ShouldHaveStatus(HttpStatusCode.OK);

        using var secondRequest = Chains.Request(HttpMethod.Get, "/orders/2");
        var failure = await Assert.ThrowsAsync<ScenarioConsumedException>(
            () => scenario.SendAsync(client, secondRequest));

        Assert.Contains("already served one logical request", failure.Message, StringComparison.Ordinal);
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task IndependentPostCallCannotBeMisreportedAsAPostRetry()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()));
        using var client = Chains.CreateClient(scenario.Handler);
        using var firstRequest = Chains.Request(HttpMethod.Post, "/orders/1");
        using var first = await scenario.SendAsync(client, firstRequest);
        first.ShouldHaveStatus(HttpStatusCode.OK);

        using var secondRequest = Chains.Request(HttpMethod.Post, "/orders/2");
        await Assert.ThrowsAsync<ScenarioConsumedException>(() => scenario.SendAsync(client, secondRequest));

        scenario.Report.ShouldHaveAttempts(1).ShouldNotHaveRetried(HttpMethod.Post);
    }

    [Fact]
    public async Task ASecondCallCannotSupplyRetryAfterOrDelayEvidence()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(1)),
                HttpFault.Success()),
            clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(1, TimeSpan.FromSeconds(1), clock.TimeProvider, Chains.IsRetryableStatus));
        using var firstRequest = Chains.Request(HttpMethod.Get, "/orders/1");
        using var first = await scenario.SendAsync(client, firstRequest);
        first.ShouldHaveStatus(HttpStatusCode.OK);

        using var secondRequest = Chains.Request(HttpMethod.Get, "/orders/2");
        await Assert.ThrowsAsync<ScenarioConsumedException>(() => scenario.SendAsync(client, secondRequest));

        scenario.Report.ShouldHaveAttempts(2)
            .ShouldRespectRetryAfter()
            .ShouldHaveRetryDelay(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AScenarioRejectsASecondSequentialCallAfterTimeout()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Timeout()), clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new AttemptTimeoutHandler(TimeSpan.FromSeconds(1), clock.TimeProvider));
        using var firstRequest = Chains.Request(HttpMethod.Get, "/orders/1");
        using var first = await scenario.SendAsync(client, firstRequest);
        first.ShouldHaveKind(ResilienceResultKind.Timeout);

        using var secondRequest = Chains.Request(HttpMethod.Get, "/orders/2");
        await Assert.ThrowsAsync<ScenarioConsumedException>(() => scenario.SendAsync(client, secondRequest));

        using var direct = new HttpMessageInvoker(scenario.Handler, disposeHandler: false);
        using var directRequest = Chains.Request(HttpMethod.Get, "/orders/direct-after-timeout");
        await Assert.ThrowsAsync<ScenarioConsumedException>(
            () => direct.SendAsync(directRequest, CancellationToken.None));
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task StrategyTimeoutWithoutCallerCancellationIsReportedAsTimeout()
    {
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new AttemptTimeoutHandler(TimeSpan.FromSeconds(1), clock.TimeProvider));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout).ShouldHaveException<TimeoutException>();
        scenario.Report.ShouldHaveAttempts(1).ShouldHaveAttemptDuration(1, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CallerCancellationRacingAStrategyTimeoutIsReportedAsCanceled()
    {
        var clock = Chains.CreateClock();
        using var caller = new CancellationTokenSource();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock);
        using var client = Chains.CreateClient(
            scenario.Handler,
            new AttemptTimeoutHandler(TimeSpan.FromSeconds(1), clock.TimeProvider, caller));
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request, caller.Token);

        result.ShouldHaveKind(ResilienceResultKind.Canceled);
        Assert.NotNull(result.Exception);
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task ASecondLogicalCallCannotConsumeTheScriptDuringRetryBackoff()
    {
        var clock = Chains.CreateClock();
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(
                HttpFault.Response(HttpStatusCode.ServiceUnavailable),
                HttpFault.Success()),
            clock,
            new ResilienceScenarioOptions
            {
                AdvanceStep = TimeSpan.FromSeconds(1),
                VirtualBudget = TimeSpan.FromSeconds(2),
                ObservationWindow = TimeSpan.FromMilliseconds(25),
            });
        using var client = Chains.CreateClient(
            scenario.Handler,
            new RetryHandler(
                maximumRetries: 1,
                delay: TimeSpan.FromSeconds(1),
                timeProvider: clock.TimeProvider,
                shouldRetryResponse: Chains.IsRetryableStatus,
                retryStarted: retryStarted,
                retryRelease: retryRelease));
        using var firstRequest = Chains.Request(HttpMethod.Get, "/orders/1");
        using var secondRequest = Chains.Request(HttpMethod.Get, "/orders/2");

        var firstCall = scenario.SendAsync(client, firstRequest);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var failure = await Assert.ThrowsAsync<ScenarioConsumedException>(
            () => scenario.SendAsync(client, secondRequest));
        Assert.Contains("one logical request", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, scenario.Report.AttemptCount);

        retryRelease.SetResult();
        using var result = await firstCall;
        result.ShouldHaveStatus(HttpStatusCode.OK);
        scenario.Report.ShouldHaveAttempts(2);
    }

    [Fact]
    public async Task UnrelatedFailureAfterAnAbandonedAttemptIsNotClassifiedAsTimeout()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Timeout()));
        using var client = Chains.CreateClient(scenario.Handler, new AbandonThenThrowHandler());
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.DownstreamError).ShouldHaveException<InvalidOperationException>();
        Assert.Equal(HttpAttemptOutcome.Abandoned, scenario.Report.LastAttempt!.Outcome);
    }

    [Fact]
    public async Task UnrelatedCancellationExceptionIsNotClassifiedAsStrategyTimeout()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Success()));
        using var client = Chains.CreateClient(scenario.Handler, new UnrelatedCancellationHandler());
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.DownstreamError).ShouldHaveException<OperationCanceledException>();
    }

    [Fact]
    public async Task NativeHttpClientTimeoutIsClassifiedAsTimeout()
    {
        using var scenario = new ResilienceScenario(HttpFaultScript.Sequence(HttpFault.Timeout()));
        using var client = Chains.CreateClient(scenario.Handler);
        client.Timeout = TimeSpan.FromMilliseconds(100);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldHaveKind(ResilienceResultKind.Timeout).ShouldHaveException<TimeoutException>();
        Assert.IsType<TaskCanceledException>(result.Exception);
        Assert.IsType<TimeoutException>(result.Exception!.InnerException);
    }

    [Fact]
    public async Task IgnoredCancellationCannotHangObservationCleanupAndLateResponsesAreDisposed()
    {
        var lateResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout(), HttpFault.Success()),
            clock,
            new ResilienceScenarioOptions
            {
                AdvanceClock = false,
                PendingObservation = TimeSpan.FromMilliseconds(20),
                CleanupTimeout = TimeSpan.FromMilliseconds(40),
            });
        using var stubborn = new IgnoreCancellationHandler(lateResponse);
        using var client = Chains.CreateClient(scenario.Handler, stubborn);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldBePending();
        Assert.True(scenario.Report.IsObservationCutoff);

        using var overlappingRequest = Chains.Request(HttpMethod.Get, "/orders/overlap");
        await Assert.ThrowsAsync<ScenarioConsumedException>(
            () => scenario.SendAsync(client, overlappingRequest));

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("late response"),
        };
        var content = response.Content;
        lateResponse.SetResult(response);
        await SpinWaitForAsync(() =>
        {
            try
            {
                _ = content.ReadAsStringAsync().GetAwaiter().GetResult();
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        });

        using var secondRequest = Chains.Request(HttpMethod.Get, "/orders/reused");
        await Assert.ThrowsAsync<ScenarioConsumedException>(() => scenario.SendAsync(client, secondRequest));
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task LateFaultAfterCleanupDeadlineDoesNotMakeScenarioReusable()
    {
        var releaseFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout(), HttpFault.Success()),
            clock,
            new ResilienceScenarioOptions
            {
                AdvanceClock = false,
                PendingObservation = TimeSpan.FromMilliseconds(20),
                CleanupTimeout = TimeSpan.FromMilliseconds(40),
            });
        using var lateFault = new LateFaultHandler(releaseFault, faultObserved);
        using var client = Chains.CreateClient(scenario.Handler, lateFault);
        using var request = Chains.Request(HttpMethod.Get);

        using var result = await scenario.SendAsync(client, request);

        result.ShouldBePending();
        Assert.True(scenario.Report.IsObservationCutoff);

        using var overlappingRequest = Chains.Request(HttpMethod.Get, "/orders/overlap");
        await Assert.ThrowsAsync<ScenarioConsumedException>(
            () => scenario.SendAsync(client, overlappingRequest));

        releaseFault.SetResult();
        await faultObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var secondRequest = Chains.Request(HttpMethod.Get, "/orders/reused");
        await Assert.ThrowsAsync<ScenarioConsumedException>(() => scenario.SendAsync(client, secondRequest));
        scenario.Report.ShouldHaveAttempts(1);
    }

    [Fact]
    public async Task StalledCancellationCleanupIsBounded()
    {
        var cancellationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Chains.CreateClock();
        using var scenario = new ResilienceScenario(
            HttpFaultScript.Sequence(HttpFault.Timeout()),
            clock,
            new ResilienceScenarioOptions
            {
                AdvanceClock = false,
                PendingObservation = TimeSpan.FromMilliseconds(20),
                CleanupTimeout = TimeSpan.FromMilliseconds(40),
            });
        using var stalled = new StalledCancellationHandler(cancellationStarted, releaseCancellation);
        using var client = Chains.CreateClient(scenario.Handler, stalled);
        using var request = Chains.Request(HttpMethod.Get);

        var run = scenario.SendAsync(client, request);
        await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var result = await run;

        result.ShouldBePending();
        Assert.True(scenario.Report.IsObservationCutoff);

        releaseCancellation.SetResult();
    }

    private static async Task SpinWaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The late response was not disposed by bounded cleanup observation.");
    }
}
