// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Net.Http.Headers;
using CiDebugMcp.Engine;
using Xunit;

namespace CiDebugMcp.Tests;

/// <summary>
/// Concurrency and lifecycle tests for <see cref="AdoCiProvider"/>'s
/// auth-resolution path. The class uses a constructor-injected
/// <c>Func&lt;AuthResult?&gt;</c> seam so these tests can drive
/// <c>EnsureAuth</c> deterministically without launching <c>az</c> /
/// <c>git credential</c> subprocesses or depending on ambient environment.
/// </summary>
public class AdoCiProviderEnsureAuthTests
{
    private static AdoCiProvider CreateProvider(Func<AuthResult?> resolver)
    {
        return new AdoCiProvider(
            "https://dev.azure.com/test-org",
            "test-project",
            new LogCache(),
            originalHost: null,
            authResolver: resolver);
    }

    [Fact]
    public void EnsureAuth_FirstCall_PopulatesAuthorizationHeader()
    {
        var provider = CreateProvider(() => new AuthResult("Basic", "dGVzdA=="));

        Assert.Null(provider.AuthorizationHeader);

        provider.EnsureAuth();

        Assert.NotNull(provider.AuthorizationHeader);
        Assert.Equal("Basic", provider.AuthorizationHeader!.Scheme);
        Assert.Equal("dGVzdA==", provider.AuthorizationHeader.Parameter);
    }

    [Fact]
    public void EnsureAuth_SecondCall_DoesNotInvokeResolverAgain()
    {
        var calls = 0;
        var provider = CreateProvider(() =>
        {
            Interlocked.Increment(ref calls);
            return new AuthResult("Bearer", "token-1");
        });

        provider.EnsureAuth();
        provider.EnsureAuth();
        provider.EnsureAuth();

        Assert.Equal(1, calls);
        Assert.Equal("token-1", provider.AuthorizationHeader!.Parameter);
    }

    [Fact]
    public void EnsureAuth_NoCredentials_DoesNotThrowAndMarksResolved()
    {
        var calls = 0;
        var provider = CreateProvider(() =>
        {
            Interlocked.Increment(ref calls);
            return null;
        });

        // Must complete without throwing even when the resolver returns null
        // (the production behavior is to log "no ADO authentication found"
        // and leave the header unset, then mark _authResolved = true so the
        // failed lookup is not retried on every subsequent API call).
        provider.EnsureAuth();
        provider.EnsureAuth();

        Assert.Null(provider.AuthorizationHeader);
        Assert.Equal(1, calls);
    }

    /// <summary>
    /// Pins the publication invariant: under concurrent <c>WarmAuth</c> +
    /// sync <c>EnsureAuth</c>, the resolver is invoked at most once AND
    /// every caller that returns from <c>EnsureAuth</c> observes the
    /// Authorization header in place at the moment of return — not just
    /// eventually. A caller that reads <c>_authResolved == true</c> on the
    /// outer fast-path check must not be able to fall straight through to
    /// its first HTTP call with an empty Authorization header.
    ///
    /// This test uses a gated resolver (<see cref="ManualResetEventSlim"/>)
    /// so the race window is deterministic rather than scheduler-dependent.
    /// Thread A enters the resolver and blocks; threads B..N then race on
    /// the outer fast-path while A is still inside the lock with the header
    /// not yet written. Once released, A finishes the publication and all
    /// observations must show the populated header.
    ///
    /// Moving <c>_authResolved = true</c> above the header assignment in
    /// <see cref="AdoCiProvider.EnsureAuth"/> makes the assertion fail with
    /// <c>Assert.NotNull() Failure: Value is null</c>, confirming the test
    /// pins the documented "mark resolved only after the header is in
    /// place" invariant.
    /// </summary>
    [Fact]
    public async Task EnsureAuth_ConcurrentCallers_ResolverInvokedExactlyOnce_AllSeeHeader()
    {
        var calls = 0;
        using var resolverEntered = new ManualResetEventSlim(initialState: false);
        using var releaseResolver = new ManualResetEventSlim(initialState: false);
        const string token = "deterministic-token";

        var provider = CreateProvider(() =>
        {
            Interlocked.Increment(ref calls);
            resolverEntered.Set();
            // Hold the writer inside the lock with the header not yet
            // written until the racing readers have launched.
            releaseResolver.Wait(TimeSpan.FromSeconds(30));
            return new AuthResult("Bearer", token);
        });

        // Writer: WarmAuth queues EnsureAuth on the thread pool. This is the
        // canonical production scenario: WarmAuth fires-and-forgets while
        // the first real API call comes in.
        provider.WarmAuth();

        // Wait until the writer is inside the resolver (i.e. holding the
        // _authLock with the header not yet written). Adversarial reverts
        // that flip _authResolved before this point are what we want to
        // catch.
        Assert.True(resolverEntered.Wait(TimeSpan.FromSeconds(10)),
            "Writer never entered the resolver — WarmAuth scheduling failed");

        const int readers = 8;
        using var startReaders = new Barrier(readers);
        var observations = new AuthenticationHeaderValue?[readers];
        var readerTasks = Enumerable.Range(0, readers).Select(i => Task.Run(() =>
        {
            // Align the readers so they all hit the fast-path at once while
            // the writer is still blocked inside the resolver.
            startReaders.SignalAndWait();
            provider.EnsureAuth();
            // Capture header immediately. If the publication invariant is
            // broken, this can be null even though EnsureAuth returned.
            observations[i] = provider.AuthorizationHeader;
        })).ToArray();

        // Give readers time to reach the fast-path check (no good signal
        // for "all readers are at outer if check" — best we can do is a
        // short delay).
        await Task.Delay(50);

        // Release the writer; readers now either return via the outer
        // fast-path (if _authResolved became visible) or queue on _authLock
        // and return via the inner double-check. Either way, every reader
        // must observe a populated header.
        releaseResolver.Set();

        await Task.WhenAll(readerTasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, calls);
        for (var i = 0; i < readers; i++)
        {
            Assert.NotNull(observations[i]);
            Assert.Equal(token, observations[i]!.Parameter);
        }
    }

    [Fact]
    public void ResetAuth_ClearsHeaderAndAllowsResolutionWithNewCredentials()
    {
        var current = new AuthResult("Bearer", "token-1");
        var calls = 0;
        var provider = CreateProvider(() =>
        {
            Interlocked.Increment(ref calls);
            return current;
        });

        provider.EnsureAuth();
        Assert.Equal("token-1", provider.AuthorizationHeader!.Parameter);
        Assert.Equal(1, calls);

        // Simulate the elicitation-retry flow: user re-authenticated, so the
        // engine clears cached state. Next EnsureAuth must re-invoke the
        // resolver and pick up the new credential.
        current = new AuthResult("Bearer", "token-2");
        provider.ResetAuth();
        Assert.Null(provider.AuthorizationHeader);

        provider.EnsureAuth();
        Assert.Equal("token-2", provider.AuthorizationHeader!.Parameter);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task WarmAuth_DoesNotThrow_AndEventuallyPopulatesHeader()
    {
        var provider = CreateProvider(() => new AuthResult("Basic", "from-warm"));

        provider.WarmAuth(); // fire-and-forget Task.Run(EnsureAuth)

        // WarmAuth is async; spin for up to 5s for the background task to
        // populate the header. Production code's first real API call would
        // do the same wait implicitly via its own EnsureAuth() entry under
        // _authLock, but the test does it explicitly.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (provider.AuthorizationHeader == null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.NotNull(provider.AuthorizationHeader);
        Assert.Equal("from-warm", provider.AuthorizationHeader!.Parameter);
    }
}
