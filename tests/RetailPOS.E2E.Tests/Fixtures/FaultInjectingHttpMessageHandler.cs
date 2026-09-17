using System.Net;

namespace RetailPOS.E2E.Tests.Fixtures;

/// <summary>
/// Delegating HTTP message handler capable of injecting deterministic network faults
/// (pre-flight request drops, post-commit response drops, status code overrides, and latency)
/// without relying on flaky thread sleeps.
/// </summary>
public sealed class FaultInjectingHttpMessageHandler : DelegatingHandler
{
    private readonly object _lock = new();

    public int TotalRequests { get; private set; }
    public int DroppedRequests { get; private set; }
    public int InjectedFaultCount { get; private set; }

    public bool DropNextRequest { get; set; }
    public bool DropNextResponse { get; set; }
    public HttpStatusCode? FailNextWithStatusCode { get; set; }
    public TimeSpan? InjectedDelay { get; set; }
    public Func<HttpRequestMessage, bool>? FaultFilter { get; set; }

    public FaultInjectingHttpMessageHandler()
    {
    }

    public FaultInjectingHttpMessageHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            TotalRequests++;
        }

        var shouldApplyFault = FaultFilter == null || FaultFilter(request);

        if (shouldApplyFault)
        {
            // 1. Injected Delay
            if (InjectedDelay.HasValue && InjectedDelay.Value > TimeSpan.Zero)
            {
                await Task.Delay(InjectedDelay.Value, cancellationToken);
            }

            // 2. Pre-flight Request Drop
            if (DropNextRequest)
            {
                lock (_lock)
                {
                    DropNextRequest = false;
                    DroppedRequests++;
                    InjectedFaultCount++;
                }

                throw new HttpRequestException(
                    "Simulated network drop: connection abruptly severed before request reached server.",
                    null,
                    HttpStatusCode.RequestTimeout);
            }

            // 3. Status Code Override (e.g., 503 or 500)
            if (FailNextWithStatusCode.HasValue)
            {
                var code = FailNextWithStatusCode.Value;
                lock (_lock)
                {
                    FailNextWithStatusCode = null;
                    InjectedFaultCount++;
                }

                return new HttpResponseMessage(code)
                {
                    RequestMessage = request,
                    Content = new StringContent($"{{\"success\":false,\"errorCode\":\"INJECTED_FAULT_{code}\",\"message\":\"Simulated fault injection\"}}")
                };
            }

            // 4. Post-commit Response Drop:
            // Send request through inner handler so the server executes and commits the transaction,
            // then simulate network partition before the response reaches the client.
            if (DropNextResponse)
            {
                lock (_lock)
                {
                    DropNextResponse = false;
                    DroppedRequests++;
                    InjectedFaultCount++;
                }

                // Execute on server
                _ = await base.SendAsync(request, cancellationToken);

                // Sever connection on return
                throw new HttpRequestException(
                    "Simulated network drop: server committed transaction but connection severed before response returned to client.",
                    null,
                    HttpStatusCode.GatewayTimeout);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    public void Reset()
    {
        lock (_lock)
        {
            DropNextRequest = false;
            DropNextResponse = false;
            FailNextWithStatusCode = null;
            InjectedDelay = null;
            FaultFilter = null;
        }
    }

    public void ResetCounters()
    {
        lock (_lock)
        {
            TotalRequests = 0;
            DroppedRequests = 0;
            InjectedFaultCount = 0;
        }
    }
}
