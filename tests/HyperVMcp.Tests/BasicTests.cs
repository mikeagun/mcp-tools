// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using HyperVMcp.Engine;
using HyperVMcp.Tools;
using McpSharp;
using Xunit;

namespace HyperVMcp.Tests;

public class TypesTests
{
    [Fact]
    public void CommandJob_AddOutput_MaintainsBuffer()
    {
        var job = new CommandJob("cmd-1", "s-1", "test", outputMode: "full");

        job.AddOutput("line1");
        job.AddOutput("line2");
        job.AddOutput("line3");
        job.AddOutput("line4");

        var snapshot = job.GetSnapshot(10);
        Assert.Contains("line1", snapshot.Output);
        Assert.Contains("line4", snapshot.Output);
        Assert.Equal(4, snapshot.TotalOutputLines);
    }

    [Fact]
    public void CommandJob_TailMode_DropsOldLines()
    {
        // OutputBuffer tail mode keeps last TailModeMaxLines (1000),
        // but we can test the concept by adding more than that.
        var job = new CommandJob("cmd-1", "s-1", "test", outputMode: "tail");
        for (var i = 0; i < 1005; i++)
            job.AddOutput($"line-{i}");

        var snapshot = job.GetSnapshot(10);
        Assert.Contains("line-1004", snapshot.Output);
        Assert.DoesNotContain("line-0", snapshot.Output);
        Assert.Equal(1005, snapshot.TotalOutputLines);
        Assert.Equal(1000, snapshot.RetainedOutputLines);
    }

    [Fact]
    public void CommandJob_Complete_SetsStatus()
    {
        var job = new CommandJob("cmd-1", "s-1", "test");
        Assert.Equal(CommandStatus.Running, job.Status);

        job.Complete(0);
        Assert.Equal(CommandStatus.Completed, job.Status);
        Assert.Equal(0, job.ExitCode);
    }

    [Fact]
    public void CommandJob_CompleteFailed_SetsFailedStatus()
    {
        var job = new CommandJob("cmd-1", "s-1", "test");

        job.Complete(1);
        Assert.Equal(CommandStatus.Failed, job.Status);
        Assert.Equal(1, job.ExitCode);
    }

    [Fact]
    public void CommandJob_Fail_SetsErrorAndStatus()
    {
        var job = new CommandJob("cmd-1", "s-1", "test");

        job.Fail("something broke");
        Assert.Equal(CommandStatus.Failed, job.Status);

        var snapshot = job.GetSnapshot();
        Assert.Contains("something broke", snapshot.Errors);
    }

    [Fact]
    public void CommandJob_WaitForNews_ReturnsOnComplete()
    {
        var job = new CommandJob("cmd-1", "s-1", "test");

        // Complete in background after a short delay.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            job.Complete(0);
        });

        var got = job.WaitForNews(5000);
        Assert.True(got);
        Assert.Equal(CommandStatus.Completed, job.Status);
    }

    [Fact]
    public void CommandJob_WaitForNews_TimesOut()
    {
        var job = new CommandJob("cmd-1", "s-1", "test");
        var got = job.WaitForNews(50);
        Assert.False(got);
        Assert.Equal(CommandStatus.Running, job.Status);
    }

    [Fact]
    public void CommandJob_GetSnapshot_TailLines()
    {
        var job = new CommandJob("cmd-1", "s-1", "test");
        for (int i = 0; i < 50; i++)
            job.AddOutput($"line-{i}");

        var snapshot = job.GetSnapshot(tailLines: 5);
        Assert.Contains("line-49", snapshot.Output);
        Assert.Contains("line-45", snapshot.Output);
        Assert.DoesNotContain("line-44", snapshot.Output);
        Assert.Equal(50, snapshot.TotalOutputLines);
    }

    [Fact]
    public void CommandJob_Dispose_DoesNotThrow()
    {
        var job = new CommandJob("cmd-1", "s-1", "test");
        job.AddOutput("test");
        job.Dispose();
        job.Dispose(); // Double-dispose should be safe.
    }

    [Fact]
    public void CommandJob_TimedOut_SetsStatus()
    {
        var job = new CommandJob("cmd-1", "s-1", "test");
        job.TimedOut();
        Assert.Equal(CommandStatus.TimedOut, job.Status);
    }

    [Fact]
    public void CommandJob_Cancel_SetsStatus()
    {
        var job = new CommandJob("cmd-1", "s-1", "test");
        job.Cancel();
        Assert.Equal(CommandStatus.Cancelled, job.Status);
    }

    [Fact]
    public void CommandJob_GetSnapshot_WithSinceLine()
    {
        var job = new CommandJob("cmd-1", "s-1", "test");
        for (var i = 1; i <= 10; i++)
            job.AddOutput($"line-{i}");

        var snapshot = job.GetSnapshot(tailLines: 100, sinceLine: 5);
        Assert.Contains("line-6", snapshot.Output);
        Assert.Contains("line-10", snapshot.Output);
        Assert.DoesNotContain("line-5", snapshot.Output);
    }

    [Fact]
    public void CommandJob_GetSnapshot_ExcludeOutput()
    {
        var job = new CommandJob("cmd-1", "s-1", "test");
        job.AddOutput("some output");

        var snapshot = job.GetSnapshot(includeOutput: false);
        Assert.Null(snapshot.Output);
    }
}

public class OutputBufferTests
{
    [Fact]
    public void FullMode_EnforcesCap_DropsOldestLines()
    {
        var buf = new OutputBuffer("full");
        var total = OutputBuffer.FullModeMaxLines + 5;
        for (var i = 0; i < total; i++)
            buf.AddLine($"line-{i}");

        Assert.Equal(total, buf.TotalLinesReceived);
        Assert.Equal(OutputBuffer.FullModeMaxLines, buf.RetainedLineCount);
        Assert.True(buf.WasTruncated);
        Assert.Equal(6, buf.FirstAvailableLine);
    }

    [Fact]
    public void Search_FindsMatches_WithContext()
    {
        var buf = new OutputBuffer();
        buf.AddLine("aaa");
        buf.AddLine("bbb MATCH");
        buf.AddLine("ccc");
        buf.AddLine("ddd");
        buf.AddLine("eee MATCH");
        buf.AddLine("fff");

        var result = buf.Search("MATCH", contextLines: 1);
        Assert.Equal(2, result.TotalMatches);
        Assert.Equal(2, result.Matches.Count);
        Assert.Equal(2, result.Matches[0].Line);
        Assert.Equal("bbb MATCH", result.Matches[0].Text);
        Assert.Equal(5, result.Matches[1].Line);
        Assert.Contains("1: aaa", result.Matches[0].Context);
        Assert.Contains("3: ccc", result.Matches[0].Context);
    }

    [Fact]
    public void Search_InvalidRegex_ThrowsInvalidOperationException()
    {
        var buf = new OutputBuffer();
        buf.AddLine("test");
        Assert.Throws<InvalidOperationException>(() => buf.Search("["));
    }

    [Fact]
    public void Search_FreedBuffer_ReturnsFreedResult()
    {
        var buf = new OutputBuffer();
        buf.AddLine("test");
        buf.Free();

        var result = buf.Search("test");
        Assert.True(result.Freed);
        Assert.Empty(result.Matches);
        Assert.Equal(0, result.TotalMatches);
    }

    [Fact]
    public void Search_Pagination_SkipAndMax()
    {
        var buf = new OutputBuffer();
        for (var i = 0; i < 10; i++)
            buf.AddLine($"match-{i}");

        var result = buf.Search("match", contextLines: 0, maxResults: 3, skip: 2);
        Assert.Equal(10, result.TotalMatches);
        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(3, result.Matches[0].Line);
        Assert.Equal("match-2", result.Matches[0].Text);
    }

    [Fact]
    public void GetTailSince_NewLines_ReturnsOnlyNew()
    {
        var buf = new OutputBuffer();
        for (var i = 1; i <= 10; i++)
            buf.AddLine($"line-{i}");

        var slice = buf.GetTailSince(5, maxLines: 100);
        Assert.Equal(5, slice.Lines.Count);
        Assert.Equal("line-6", slice.Lines[0]);
        Assert.Equal("line-10", slice.Lines[4]);
        Assert.Equal(6, slice.FromLine);
        Assert.Equal(10, slice.ToLine);
        Assert.Equal(5, slice.SinceLine);
    }

    [Fact]
    public void GetTailSince_NoNewLines_ReturnsEmpty()
    {
        var buf = new OutputBuffer();
        for (var i = 1; i <= 10; i++)
            buf.AddLine($"line-{i}");

        var slice = buf.GetTailSince(10, maxLines: 100);
        Assert.Empty(slice.Lines);
    }

    [Fact]
    public void GetTailSince_MoreNewThanMax_ReturnsTailOfNew()
    {
        var buf = new OutputBuffer();
        for (var i = 1; i <= 20; i++)
            buf.AddLine($"line-{i}");

        var slice = buf.GetTailSince(5, maxLines: 3);
        Assert.Equal(3, slice.Lines.Count);
        Assert.Equal("line-18", slice.Lines[0]);
        Assert.Equal("line-20", slice.Lines[2]);
        Assert.Equal(12, slice.SkippedLines);
    }

    [Fact]
    public void GetLines_ReturnsRange()
    {
        var buf = new OutputBuffer();
        for (var i = 1; i <= 20; i++)
            buf.AddLine($"line-{i}");

        var slice = buf.GetLines(5, 10);
        Assert.Equal(6, slice.Lines.Count);
        Assert.Equal("line-5", slice.Lines[0]);
        Assert.Equal("line-10", slice.Lines[5]);
        Assert.Equal(5, slice.FromLine);
        Assert.Equal(10, slice.ToLine);
    }

    [Fact]
    public void GetLines_OutOfRange_ClampsToAvailable()
    {
        var buf = new OutputBuffer();
        for (var i = 1; i <= 10; i++)
            buf.AddLine($"line-{i}");

        var slice = buf.GetLines(-5, 100);
        Assert.Equal(10, slice.Lines.Count);
        Assert.Equal(1, slice.FromLine);
        Assert.Equal(10, slice.ToLine);
    }

    [Fact]
    public void GetLines_FreedBuffer_ReturnsFreedSlice()
    {
        var buf = new OutputBuffer();
        buf.AddLine("test");
        buf.Free();

        var slice = buf.GetLines(1, 10);
        Assert.True(slice.Freed);
        Assert.Empty(slice.Lines);
    }

    [Fact]
    public void FindFirstMatch_ReturnsLineNumber()
    {
        var buf = new OutputBuffer();
        for (var i = 1; i <= 10; i++)
            buf.AddLine($"line-{i}");

        Assert.Equal(5, buf.FindFirstMatch("line-5"));
    }

    [Fact]
    public void FindFirstMatch_NoMatch_ReturnsNull()
    {
        var buf = new OutputBuffer();
        buf.AddLine("aaa");
        buf.AddLine("bbb");

        Assert.Null(buf.FindFirstMatch("zzz"));
    }

    [Fact]
    public void Free_ReturnsCount_SecondCallReturnsZero()
    {
        var buf = new OutputBuffer();
        for (var i = 0; i < 10; i++)
            buf.AddLine($"line-{i}");

        Assert.Equal(10, buf.Free());
        Assert.Equal(0, buf.Free());
    }

    [Fact]
    public void Free_AddLineAfterFree_Ignored()
    {
        var buf = new OutputBuffer();
        for (var i = 0; i < 10; i++)
            buf.AddLine($"line-{i}");
        buf.Free();

        var totalBefore = buf.TotalLinesReceived;
        buf.AddLine("should be ignored");
        Assert.Equal(totalBefore, buf.TotalLinesReceived);
    }

    [Fact]
    public void GetAroundLine_CentersCorrectly()
    {
        var buf = new OutputBuffer();
        for (var i = 1; i <= 100; i++)
            buf.AddLine($"line-{i}");

        var slice = buf.GetAroundLine(50, maxLines: 10);
        Assert.Equal(10, slice.Lines.Count);
        Assert.Equal(45, slice.FromLine);
        Assert.Equal(54, slice.ToLine);
        Assert.Contains("line-50", slice.Lines);
    }
}

public class PsUtilsTests
{
    [Fact]
    public void PsEscape_QuotesDoubled()
    {
        Assert.Equal("it''s", PsUtils.PsEscape("it's"));
    }

    [Fact]
    public void PsEscape_NewlinesStripped()
    {
        Assert.Equal("ab", PsUtils.PsEscape("a\r\n\0b"));
    }

    [Fact]
    public void PsEscape_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", PsUtils.PsEscape(""));
    }

    [Fact]
    public void PsEscape_NoSpecialChars_Unchanged()
    {
        Assert.Equal("hello", PsUtils.PsEscape("hello"));
    }

    [Fact]
    public void ValidateName_EmptyThrows()
    {
        Assert.Throws<ArgumentException>(() => PsUtils.ValidateName("", "test"));
    }

    [Fact]
    public void ValidateName_WhitespaceThrows()
    {
        Assert.Throws<ArgumentException>(() => PsUtils.ValidateName("  ", "test"));
    }

    [Fact]
    public void ValidateName_ControlCharThrows()
    {
        Assert.Throws<ArgumentException>(() => PsUtils.ValidateName("vm\t1", "test"));
    }

    [Fact]
    public void ValidateName_ValidNameReturnsValue()
    {
        Assert.Equal("my-vm-01", PsUtils.ValidateName("my-vm-01", "test"));
    }

    [Fact]
    public void ValidateName_SpecialShellCharsAllowed()
    {
        Assert.Equal("vm;test", PsUtils.ValidateName("vm;test", "test"));
    }
}

public class ToolRegistrationTests
{
    [Fact]
    public void AllToolsRegisterWithoutError()
    {
        var server = new McpServer("test");
        var backend = new ElevatedBackend();
        var credStore = new CredentialStore();
        var sessionMgr = new SessionManager(credStore, backend);
        var vmMgr = new VmManager(backend);
        var cmdRunner = new CommandRunner(sessionMgr);
        var ftMgr = new FileTransferManager(sessionMgr, cmdRunner);

        // Should not throw.
        HyperVMcp.Tools.ToolRegistration.RegisterAll(server, sessionMgr, vmMgr, cmdRunner, ftMgr);

        // Verify tools are listed.
        var result = server.Dispatch("tools/list", null);
        var tools = result?["tools"]?.AsArray();
        Assert.NotNull(tools);
        Assert.True(tools.Count >= 24, $"Expected at least 24 tools, got {tools.Count}");

        // Verify key tools exist.
        var toolNames = tools.Select(t => t?["name"]?.GetValue<string>()).ToHashSet();
        Assert.Contains("connect_vm", toolNames);
        Assert.Contains("list_vms", toolNames);
        Assert.Contains("invoke_command", toolNames);
        Assert.Contains("copy_to_vm", toolNames);
        Assert.Contains("get_vm_info", toolNames);
        Assert.Contains("cancel_command", toolNames);
        Assert.Contains("run_script", toolNames);
        Assert.Contains("kill_process", toolNames);
        Assert.Contains("set_env", toolNames);
        Assert.Contains("search_command_output", toolNames);
        Assert.Contains("get_command_output", toolNames);

        // Verify removed tools are gone.
        Assert.DoesNotContain("deploy_build", toolNames);
        Assert.DoesNotContain("run_test", toolNames);
        Assert.DoesNotContain("start_trace", toolNames);
        Assert.DoesNotContain("collect_diagnostics", toolNames);
        Assert.DoesNotContain("install_msi", toolNames);
        Assert.DoesNotContain("replace_driver", toolNames);
        Assert.DoesNotContain("get_test_status", toolNames);

        // Cleanup.
        cmdRunner.Dispose();
        sessionMgr.Dispose();
    }

    [Fact]
    public void ServerInitializeReturnsCapabilities()
    {
        var server = new McpServer("hyperv-mcp-test", "1.0.0");
        var result = server.Dispatch("initialize", null);

        Assert.NotNull(result);
        Assert.Equal("2025-06-18", result["protocolVersion"]?.GetValue<string>());
        Assert.Equal("hyperv-mcp-test", result["serverInfo"]?["name"]?.GetValue<string>());
    }
}

/// <summary>
/// Tests for CommandTools.SnapshotToJson — .NET type detection and hint generation.
/// </summary>
public class SnapshotToJsonTests
{
    private static CommandSnapshot MakeSnapshot(
        string output, CommandStatus status = CommandStatus.Completed, int exitCode = 0, int totalLines = 1)
    {
        return new CommandSnapshot
        {
            CommandId = "cmd-1",
            SessionId = "s-1",
            Command = "test",
            Status = status,
            ExitCode = exitCode,
            Output = output,
            TotalOutputLines = totalLines,
        };
    }

    [Fact]
    public void SingleDotNetTypeName_TriggersHint()
    {
        var snapshot = MakeSnapshot("Microsoft.PowerShell.Commands.GenericMeasureInfo");
        var json = HyperVMcp.Tools.CommandTools.SnapshotToJson(snapshot);
        Assert.Contains(".NET type", json["hint"]?.GetValue<string>() ?? "");
    }

    [Fact]
    public void MultiLineFormatObjects_TriggersHint()
    {
        // Format-List produces multiple .NET format object type names.
        var output = string.Join("\n",
            "Microsoft.PowerShell.Commands.Internal.Format.FormatStartData",
            "Microsoft.PowerShell.Commands.Internal.Format.GroupStartData",
            "Microsoft.PowerShell.Commands.Internal.Format.FormatEntryData",
            "Microsoft.PowerShell.Commands.Internal.Format.GroupEndData",
            "Microsoft.PowerShell.Commands.Internal.Format.FormatEndData");
        var snapshot = MakeSnapshot(output, totalLines: 5);
        var json = HyperVMcp.Tools.CommandTools.SnapshotToJson(snapshot);
        Assert.Contains(".NET type", json["hint"]?.GetValue<string>() ?? "");
    }

    [Fact]
    public void NormalOutput_NoHint()
    {
        var snapshot = MakeSnapshot("Hello world\nLine 2", totalLines: 2);
        var json = HyperVMcp.Tools.CommandTools.SnapshotToJson(snapshot);
        Assert.Null(json["hint"]);
    }

    [Fact]
    public void RunningCommand_NoTypeHint()
    {
        // Running commands should show the polling hint, not a type hint.
        var snapshot = MakeSnapshot(
            "Microsoft.PowerShell.Commands.GenericMeasureInfo",
            status: CommandStatus.Running, exitCode: 0);
        var json = HyperVMcp.Tools.CommandTools.SnapshotToJson(snapshot);
        Assert.Contains("Poll with", json["hint"]?.GetValue<string>() ?? "");
    }
}
