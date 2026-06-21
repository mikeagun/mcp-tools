// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using McpSharp.Policy;
using Xunit;

namespace McpSharp.Tests;

/// <summary>
/// The two-prompt persistence follow-up must honor the configured
/// elicitation timeout instead of blocking indefinitely when the client never
/// responds to the second prompt. The scope prompt is answered with an option whose
/// Persistence is null (triggering PromptForPersistence); the persistence prompt is
/// left unanswered to force the timeout path.
/// </summary>
public class PolicyDispatchTimeoutTests
{
    [Fact]
    public void PromptForPersistence_HonorsTimeout_WhenSecondPromptUnanswered()
    {
        // s-1 = scope prompt answer (accept the null-persistence option);
        // after that, the input stream blocks so the s-2 persistence prompt times out.
        var s1 = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject
            {
                ["action"] = "accept",
                ["content"] = new JsonObject { ["action"] = "Allow it" },
            },
        }.ToJsonString();

        // Framing-detection dummy + the s-1 response, then the stream blocks forever.
        var preloaded = Encoding.UTF8.GetBytes("{\"_\":0}\n" + s1 + "\n");
        var input = new OneShotThenBlockStream(preloaded);
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        // Lock NDJSON framing by consuming the dummy line on the main thread.
        transport.ReadMessage();

        var server = new McpServer("test");
        server.Transport = transport;
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject { ["elicitation"] = new JsonObject() },
        });
        server.RegisterTool(new ToolInfo
        {
            Name = "do_it",
            Description = "test",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = _ => new JsonObject { ["ok"] = true },
        });

        var policy = new PolicyEngine(new PolicyConfig(), new ConfirmClassifier(), new MatchAllMatcher());
        var optionGen = new NullPersistenceOptionGenerator();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "do_it",
                ["arguments"] = new JsonObject(),
            },
            server, policy, optionGen,
            elicitationTimeoutSeconds: 1);
        sw.Stop();

        // It must have returned (not blocked forever) within a small bound of the timeout.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"Dispatch did not return promptly: {sw.Elapsed}");

        // The persistence prompt timed out, so the request is denied (deny-safe).
        Assert.True(result!["isError"]?.GetValue<bool>());

        // The timeout path fired for the SECOND prompt specifically: a
        // notifications/cancelled referencing s-2 was written to the wire.
        output.Position = 0;
        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("notifications/cancelled", wire);
        Assert.Contains("s-2", wire);

        input.Unblock();
    }

    // -- Test stubs -----------------------------------------------------------

    private sealed class ConfirmClassifier : IToolClassifier
    {
        public PolicyDecision Classify(string toolName, JsonObject args) => PolicyDecision.Confirm;
    }

    private sealed class MatchAllMatcher : IRuleMatcher
    {
        public bool Matches(ApprovalRule rule, string toolName, JsonObject args) => true;
    }

    private sealed class NullPersistenceOptionGenerator : IOptionGenerator
    {
        public List<ElicitationOption> Generate(string toolName, JsonObject args, PolicyEvaluation evaluation)
            => new()
            {
                new ElicitationOption
                {
                    Label = "Allow it",
                    Polarity = ApprovalPolarity.Allow,
                    Persistence = null, // forces the two-prompt persistence follow-up
                    Rule = new ApprovalRule { Tools = [toolName] },
                },
            };
    }

    /// <summary>
    /// A read-only stream that serves a preloaded buffer once, then blocks on
    /// further reads (simulating a client that never sends the second response)
    /// until <see cref="Unblock"/> is called.
    /// </summary>
    private sealed class OneShotThenBlockStream : Stream
    {
        private readonly byte[] _data;
        private int _pos;
        private readonly ManualResetEventSlim _gate = new(false);

        public OneShotThenBlockStream(byte[] data) => _data = data;

        public void Unblock() => _gate.Set();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos < _data.Length)
            {
                int n = Math.Min(count, _data.Length - _pos);
                Array.Copy(_data, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }
            // Block until explicitly unblocked, then report EOF.
            _gate.Wait();
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _gate.Set();
            _gate.Dispose();
            base.Dispose(disposing);
        }
    }
}
