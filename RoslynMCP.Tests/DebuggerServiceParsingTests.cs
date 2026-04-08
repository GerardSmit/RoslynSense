using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Tests for DebuggerService MI protocol parsing and formatting methods.
/// These verify the machine-parseable output from netcoredbg is correctly interpreted.
/// </summary>
public class DebuggerServiceParsingTests
{
    // === ExtractMiField ===

    [Fact]
    public void ExtractMiField_SimpleField()
    {
        var result = DebuggerService.ExtractMiField(@"name=""hello""", "name");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void ExtractMiField_FieldWithEscapedQuotes()
    {
        var result = DebuggerService.ExtractMiField(@"value=""say \""hi\""""", "value");
        Assert.Equal(@"say ""hi""", result);
    }

    [Fact]
    public void ExtractMiField_FieldWithEscapedBackslash()
    {
        var result = DebuggerService.ExtractMiField(@"path=""C:\\Users\\test""", "path");
        Assert.Equal(@"C:\Users\test", result);
    }

    [Fact]
    public void ExtractMiField_MissingFieldReturnsNull()
    {
        var result = DebuggerService.ExtractMiField(@"name=""hello""", "value");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractMiField_MultipleFields()
    {
        var text = @"name=""x"",value=""42""";
        Assert.Equal("x", DebuggerService.ExtractMiField(text, "name"));
        Assert.Equal("42", DebuggerService.ExtractMiField(text, "value"));
    }

    [Fact]
    public void ExtractMiField_EmptyValue()
    {
        var result = DebuggerService.ExtractMiField(@"file=""""", "file");
        Assert.Equal("", result);
    }

    // === ExtractQuotedString ===

    [Fact]
    public void ExtractQuotedString_ValidString()
    {
        var result = DebuggerService.ExtractQuotedString(@"""hello world""");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void ExtractQuotedString_WithEscapes()
    {
        // ExtractMiField strips the escape character, so \n becomes n
        // The full unescape (e.g. \n → newline) is done by UnescapeMiString separately
        var result = DebuggerService.ExtractQuotedString(@"""line1\nline2""");
        Assert.Equal("line1nline2", result);
    }

    [Fact]
    public void ExtractQuotedString_NotQuoted()
    {
        var result = DebuggerService.ExtractQuotedString("hello");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractQuotedString_TooShort()
    {
        var result = DebuggerService.ExtractQuotedString("x");
        Assert.Null(result);
    }

    // === EscapeMiString / UnescapeMiString ===

    [Fact]
    public void EscapeMiString_EscapesQuotesAndBackslashes()
    {
        var result = DebuggerService.EscapeMiString(@"say ""hello"" at C:\path");
        Assert.Equal(@"say \""hello\"" at C:\\path", result);
    }

    [Fact]
    public void UnescapeMiString_UnescapesQuotesBackslashesNewlines()
    {
        var result = DebuggerService.UnescapeMiString(@"say \""hello\""\nC:\\path");
        Assert.Equal("say \"hello\"\nC:\\path", result);
    }

    [Fact]
    public void EscapeAndUnescape_RoundTrip()
    {
        var original = @"test ""value"" with\backslash";
        var escaped = DebuggerService.EscapeMiString(original);
        var unescaped = DebuggerService.UnescapeMiString(escaped);
        Assert.Equal(original, unescaped);
    }

    // === ExtractError ===

    [Fact]
    public void ExtractError_FromErrorResponse()
    {
        var result = DebuggerService.ExtractError(@"^error,msg=""No symbol table""");
        Assert.Equal("No symbol table", result);
    }

    [Fact]
    public void ExtractError_NoMsgField()
    {
        var result = DebuggerService.ExtractError("^error");
        Assert.Equal("Unknown error", result);
    }

    // === ParseStoppedFrame ===

    [Fact]
    public void ParseStoppedFrame_BreakpointHit()
    {
        var line = @"*stopped,reason=""breakpoint-hit"",disp=""keep"",bkptno=""1"",frame={func=""MyClass.MyMethod"",args=[],file=""Test.cs"",fullname=""/src/Test.cs"",line=""42""},thread-id=""1""";
        var frame = DebuggerService.ParseStoppedFrame(line);

        Assert.Equal("breakpoint-hit", frame.Reason);
        Assert.Equal("MyClass.MyMethod", frame.Function);
        Assert.Equal("/src/Test.cs", frame.FilePath);
        Assert.Equal(42, frame.Line);
        Assert.Equal(1, frame.BreakpointNumber);
    }

    [Fact]
    public void ParseStoppedFrame_StepComplete()
    {
        var line = @"*stopped,reason=""end-stepping-range"",frame={func=""Program.Main"",args=[],file=""Program.cs"",fullname=""D:\\src\\Program.cs"",line=""10""},thread-id=""1""";
        var frame = DebuggerService.ParseStoppedFrame(line);

        Assert.Equal("end-stepping-range", frame.Reason);
        Assert.Equal("Program.Main", frame.Function);
        Assert.Equal(@"D:\src\Program.cs", frame.FilePath);
        Assert.Equal(10, frame.Line);
    }

    [Fact]
    public void ParseStoppedFrame_MissingFieldsDefaultGracefully()
    {
        var frame = DebuggerService.ParseStoppedFrame("*stopped");

        Assert.Equal("unknown", frame.Reason);
        Assert.Equal("unknown", frame.Function);
        Assert.Equal("", frame.FilePath);
        Assert.Equal(0, frame.Line);
        Assert.Equal(0, frame.BreakpointNumber);
    }

    // === FormatLocals ===

    [Fact]
    public void FormatLocals_ParsesVariables()
    {
        var response = @"^done,locals=[{name=""x"",value=""42""},{name=""name"",value=""hello""}]";
        var result = DebuggerService.FormatLocals(response);

        Assert.Contains("x = 42", result);
        Assert.Contains("name = hello", result);
    }

    [Fact]
    public void FormatLocals_ParsesVariablesFormat()
    {
        // netcoredbg's -stack-list-variables returns variables=[] instead of locals=[]
        var response = @"^done,variables=[{name=""a"",value=""3""},{name=""b"",value=""5""}]";
        var result = DebuggerService.FormatLocals(response);

        Assert.Contains("a = 3", result);
        Assert.Contains("b = 5", result);
    }

    [Fact]
    public void FormatLocals_EmptyLocals()
    {
        var response = @"^done,locals=[]";
        var result = DebuggerService.FormatLocals(response);

        Assert.Contains("Local Variables", result);
    }

    [Fact]
    public void FormatLocals_ErrorResponse()
    {
        var result = DebuggerService.FormatLocals(@"^error,msg=""Thread is not stopped""");
        Assert.StartsWith("Error:", result);
        Assert.Contains("Thread is not stopped", result);
    }

    [Fact]
    public void FormatLocals_NoLocalsField()
    {
        var result = DebuggerService.FormatLocals("^done");
        Assert.Equal("No local variables.", result);
    }

    // === FormatStackTrace ===

    [Fact]
    public void FormatStackTrace_ParsesFrames()
    {
        var response = @"^done,stack=[frame={level=""0"",func=""Test.Method"",file=""Test.cs"",line=""10""},frame={level=""1"",func=""Program.Main"",file=""Program.cs"",line=""5""}]";
        var result = DebuggerService.FormatStackTrace(response);

        Assert.Contains("#0 Test.Method at Test.cs:10", result);
        Assert.Contains("#1 Program.Main at Program.cs:5", result);
    }

    [Fact]
    public void FormatStackTrace_FrameWithoutFile()
    {
        var response = @"^done,stack=[frame={level=""0"",func=""NativeMethod""}]";
        var result = DebuggerService.FormatStackTrace(response);

        Assert.Contains("#0 NativeMethod", result);
        Assert.DoesNotContain(" at ", result);
    }

    [Fact]
    public void FormatStackTrace_ErrorResponse()
    {
        var result = DebuggerService.FormatStackTrace(@"^error,msg=""No frames""");
        Assert.StartsWith("Error:", result);
    }

    // === ProcessMiOutput (token-prefix stripping) ===

    [Fact]
    public async Task ProcessMiOutput_TokenPrefixedDoneCompletesWaiter()
    {
        using var service = new DebuggerService();
        // Simulate a response waiter
        var tcs = new TaskCompletionSource<string>();
        typeof(DebuggerService)
            .GetField("_responseWaiter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(service, tcs);

        service.ProcessMiOutput(@"5^done,value=""42""");

        Assert.True(tcs.Task.IsCompleted);
        Assert.StartsWith("^done", await tcs.Task);
    }

    [Fact]
    public async Task ProcessMiOutput_TokenPrefixedErrorCompletesWaiter()
    {
        using var service = new DebuggerService();
        var tcs = new TaskCompletionSource<string>();
        typeof(DebuggerService)
            .GetField("_responseWaiter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(service, tcs);

        service.ProcessMiOutput(@"12^error,msg=""No symbol""");

        Assert.True(tcs.Task.IsCompleted);
        Assert.StartsWith("^error", await tcs.Task);
    }

    [Fact]
    public async Task ProcessMiOutput_NoTokenPrefixStillWorks()
    {
        using var service = new DebuggerService();
        var tcs = new TaskCompletionSource<string>();
        typeof(DebuggerService)
            .GetField("_responseWaiter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(service, tcs);

        service.ProcessMiOutput(@"^done,locals=[]");

        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal("^done,locals=[]", await tcs.Task);
    }

    [Fact]
    public void ProcessMiOutput_StoppedSetsStateAndFrame()
    {
        using var service = new DebuggerService();
        var tcs = new TaskCompletionSource<string>();
        typeof(DebuggerService)
            .GetField("_responseWaiter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(service, tcs);

        service.ProcessMiOutput(@"*stopped,reason=""breakpoint-hit"",bkptno=""1"",frame={func=""Test.Run"",file=""Test.cs"",fullname=""/src/Test.cs"",line=""15""},thread-id=""1""");

        Assert.Equal(DebuggerService.DebugState.Stopped, service.State);
        Assert.NotNull(service.CurrentFrame);
        Assert.Equal("breakpoint-hit", service.CurrentFrame.Reason);
        Assert.Equal("Test.Run", service.CurrentFrame.Function);
        Assert.Equal(15, service.CurrentFrame.Line);
    }

    [Fact]
    public void ProcessMiOutput_RunningSetsState()
    {
        using var service = new DebuggerService();

        service.ProcessMiOutput("*running,thread-id=\"all\"");

        Assert.Equal(DebuggerService.DebugState.Running, service.State);
    }

    [Fact]
    public void ProcessMiOutput_ConsoleOutputCaptured()
    {
        using var service = new DebuggerService();

        service.ProcessMiOutput(@"~""Hello from test\n""");

        // Console output is stored internally — verify via raw output buffer
        // The raw buffer at least should contain the line
        var rawField = typeof(DebuggerService)
            .GetField("_rawOutputBuffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var raw = rawField.GetValue(service)!.ToString()!;
        Assert.Contains("Hello from test", raw);
    }

    [Fact]
    public void ProcessMiOutput_TokenPrefixedConsoleOutputCaptured()
    {
        using var service = new DebuggerService();

        service.ProcessMiOutput(@"5~""Debug output\n""");

        var consoleField = typeof(DebuggerService)
            .GetField("_consoleOutput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var console = (List<string>)consoleField.GetValue(service)!;
        Assert.Single(console);
        Assert.Contains("Debug output", console[0]);
    }

    [Fact]
    public void ProcessMiOutput_GdbPromptIgnored()
    {
        using var service = new DebuggerService();

        // Should not throw or change state
        service.ProcessMiOutput("(gdb)");

        Assert.Equal(DebuggerService.DebugState.NotStarted, service.State);
    }

    // === FormatLocals with escaped values ===

    [Fact]
    public void FormatLocals_UnescapesValues()
    {
        var response = @"^done,locals=[{name=""msg"",value=""\""hello world\""""}]";
        var result = DebuggerService.FormatLocals(response);

        Assert.Contains(@"msg = ""hello world""", result);
    }

    // === FormatStackTrace with full paths ===

    [Fact]
    public void FormatStackTrace_ShowsFileNameOnly()
    {
        var response = @"^done,stack=[frame={level=""0"",func=""MyApp.Service.DoWork"",file=""D:\\code\\src\\Service.cs"",line=""99""}]";
        var result = DebuggerService.FormatStackTrace(response);

        Assert.Contains("Service.cs:99", result);
        // Should not contain full path
        Assert.DoesNotContain("D:\\code", result);
    }

    // === State Machine & Session Management ===

    [Fact]
    public void InitialState_IsNotStarted()
    {
        using var service = new DebuggerService();

        Assert.Equal(DebuggerService.DebugState.NotStarted, service.State);
        Assert.Null(service.CurrentFrame);
        Assert.Empty(service.Breakpoints);
    }

    [Fact]
    public void GetStatus_WhenNotStarted_ShowsNotStarted()
    {
        using var service = new DebuggerService();

        var status = service.GetStatus();

        Assert.Contains("NotStarted", status);
    }

    [Fact]
    public void GetStatus_WhenStopped_ShowsBreakpointsAndFrame()
    {
        using var service = new DebuggerService();

        // Simulate a stopped state with breakpoint and frame
        service.ProcessMiOutput(@"*stopped,reason=""breakpoint-hit"",bkptno=""1"",frame={func=""Calculator.Add"",file=""Calc.cs"",fullname=""C:\\src\\Calc.cs"",line=""10""},thread-id=""1""");

        // Manually add a breakpoint entry
        service.Breakpoints.GetType(); // Verify it's accessible
        var bpField = typeof(DebuggerService)
            .GetField("_breakpoints", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var breakpoints = (System.Collections.Concurrent.ConcurrentDictionary<int, DebuggerService.BreakpointInfo>)bpField.GetValue(service)!;
        breakpoints[1] = new DebuggerService.BreakpointInfo(1, "C:\\src\\Calc.cs", 10);

        var status = service.GetStatus();

        Assert.Contains("Stopped", status);
        Assert.Contains("#1", status);
        Assert.Contains("Calc.cs:10", status);
        Assert.Contains("Calculator.Add", status);
    }

    [Fact]
    public void Stop_ResetsState()
    {
        using var service = new DebuggerService();

        // Simulate stopped state
        service.ProcessMiOutput(@"*stopped,reason=""breakpoint-hit"",bkptno=""1"",frame={func=""Test"",file=""t.cs"",fullname=""t.cs"",line=""5""},thread-id=""1""");
        Assert.Equal(DebuggerService.DebugState.Stopped, service.State);

        var result = service.Stop();

        Assert.Contains("stopped", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DebuggerService.DebugState.NotStarted, service.State);
        Assert.Null(service.CurrentFrame);
        Assert.Empty(service.Breakpoints);
    }

    [Fact]
    public void Stop_WhenNotStarted_StillReturnsStoppedMessage()
    {
        using var service = new DebuggerService();

        var result = service.Stop();

        // DebuggerService.Stop() always succeeds (cleanup is idempotent)
        Assert.Contains("stopped", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetBreakpoint_WhenNotStarted_ReturnsError()
    {
        using var service = new DebuggerService();

        var result = await service.SetBreakpointAsync("test.cs", 10);

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task RemoveBreakpoint_WhenNotStarted_ReturnsError()
    {
        using var service = new DebuggerService();

        var result = await service.RemoveBreakpointAsync(1);

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task Continue_WhenNotStarted_ReturnsError()
    {
        using var service = new DebuggerService();

        var result = await service.ContinueAsync();

        Assert.Contains("No active debug session", result);
    }

    [Fact]
    public async Task StepIn_WhenNotStopped_ReturnsError()
    {
        using var service = new DebuggerService();

        var result = await service.StepInAsync();

        Assert.Contains("not stopped", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StepOver_WhenNotStopped_ReturnsError()
    {
        using var service = new DebuggerService();

        var result = await service.StepOverAsync();

        Assert.Contains("not stopped", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StepOut_WhenNotStopped_ReturnsError()
    {
        using var service = new DebuggerService();

        var result = await service.StepOutAsync();

        Assert.Contains("not stopped", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_WhenNotStopped_ReturnsError()
    {
        using var service = new DebuggerService();

        var result = await service.EvaluateAsync("1 + 1");

        Assert.Contains("not stopped", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLocals_WhenNotStopped_ReturnsError()
    {
        using var service = new DebuggerService();

        var result = await service.GetLocalsAsync();

        Assert.Contains("not stopped", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStackTrace_WhenNotStopped_ReturnsError()
    {
        using var service = new DebuggerService();

        var result = await service.GetStackTraceAsync();

        Assert.Contains("not stopped", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartTestSession_WithNonExistentProject_ReturnsError()
    {
        using var service = new DebuggerService();

        var result = await service.StartTestSessionAsync("/nonexistent/project.csproj", null);

        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartTestSession_WhenAlreadyActive_ReturnsError()
    {
        using var service = new DebuggerService();

        // Simulate an active session by setting state
        var stateField = typeof(DebuggerService)
            .GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        stateField.SetValue(service, DebuggerService.DebugState.Stopped);

        var result = await service.StartTestSessionAsync("some.csproj", null);

        Assert.Contains("already active", result, StringComparison.OrdinalIgnoreCase);
    }

    // === StoppedFrame record tests ===

    [Fact]
    public void StoppedFrame_RecordEquality()
    {
        var frame1 = new DebuggerService.StoppedFrame("breakpoint-hit", "Main", "app.cs", 10, 1);
        var frame2 = new DebuggerService.StoppedFrame("breakpoint-hit", "Main", "app.cs", 10, 1);

        Assert.Equal(frame1, frame2);
    }

    [Fact]
    public void BreakpointInfo_RecordEquality()
    {
        var bp1 = new DebuggerService.BreakpointInfo(1, "test.cs", 5);
        var bp2 = new DebuggerService.BreakpointInfo(1, "test.cs", 5);

        Assert.Equal(bp1, bp2);
    }

    // === State transitions via ProcessMiOutput ===

    [Fact]
    public void ProcessMiOutput_ExitSetsResponseWaiter()
    {
        using var service = new DebuggerService();
        var tcs = new TaskCompletionSource<string>();
        typeof(DebuggerService)
            .GetField("_responseWaiter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(service, tcs);

        service.ProcessMiOutput("^exit");

        Assert.True(tcs.Task.IsCompleted);
    }

    [Fact]
    public void ProcessMiOutput_MultipleStoppedEvents_UpdatesFrame()
    {
        using var service = new DebuggerService();

        service.ProcessMiOutput(@"*stopped,reason=""breakpoint-hit"",bkptno=""1"",frame={func=""A"",file=""a.cs"",fullname=""a.cs"",line=""10""},thread-id=""1""");
        Assert.Equal("A", service.CurrentFrame!.Function);
        Assert.Equal(10, service.CurrentFrame.Line);

        // Simulate another stop (e.g., after step)
        var tcs = new TaskCompletionSource<string>();
        typeof(DebuggerService)
            .GetField("_responseWaiter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(service, tcs);

        service.ProcessMiOutput(@"*stopped,reason=""end-stepping-range"",frame={func=""B"",file=""b.cs"",fullname=""b.cs"",line=""20""},thread-id=""1""");
        Assert.Equal("B", service.CurrentFrame!.Function);
        Assert.Equal(20, service.CurrentFrame.Line);
        Assert.Equal("end-stepping-range", service.CurrentFrame.Reason);
    }

    [Fact]
    public void ProcessMiOutput_ExitedStoppedSetsExitedState()
    {
        using var service = new DebuggerService();

        service.ProcessMiOutput(@"*stopped,reason=""exited"",exit-code=""0""");

        Assert.Equal(DebuggerService.DebugState.Exited, service.State);
    }

    [Fact]
    public void ProcessMiOutput_ExitedNormallySetsExitedState()
    {
        using var service = new DebuggerService();

        service.ProcessMiOutput(@"*stopped,reason=""exited-normally""");

        Assert.Equal(DebuggerService.DebugState.Exited, service.State);
    }

    [Fact]
    public void ProcessMiOutput_BreakpointHitStoppedSetsStoppedState()
    {
        using var service = new DebuggerService();

        service.ProcessMiOutput(@"*stopped,reason=""breakpoint-hit"",bkptno=""1"",frame={func=""Test"",file=""t.cs"",fullname=""t.cs"",line=""5""},thread-id=""1""");

        Assert.Equal(DebuggerService.DebugState.Stopped, service.State);
    }

    [Fact]
    public void ProcessMiOutput_BreakpointDoneAutoTracksBreakpoint()
    {
        using var service = new DebuggerService();
        var tcs = new TaskCompletionSource<string>();
        typeof(DebuggerService)
            .GetField("_responseWaiter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(service, tcs);

        service.ProcessMiOutput(@"^done,bkpt={number=""1"",type=""breakpoint"",disp=""keep"",enabled=""y"",func="""",file=""Test.cs"",fullname=""D:\\src\\Test.cs"",line=""42""}");

        Assert.Single(service.Breakpoints);
        Assert.True(service.Breakpoints.ContainsKey(1));
        Assert.Equal(@"D:\src\Test.cs", service.Breakpoints[1].FilePath);
        Assert.Equal(42, service.Breakpoints[1].Line);
    }

    [Fact]
    public void ProcessMiOutput_MultipleBreakpointsAutoTracked()
    {
        using var service = new DebuggerService();

        service.ProcessMiOutput(@"^done,bkpt={number=""1"",type=""breakpoint"",disp=""keep"",enabled=""y"",file=""a.cs"",fullname=""/src/a.cs"",line=""10""}");
        service.ProcessMiOutput(@"^done,bkpt={number=""2"",type=""breakpoint"",disp=""keep"",enabled=""y"",file=""b.cs"",fullname=""/src/b.cs"",line=""20""}");

        Assert.Equal(2, service.Breakpoints.Count);
        Assert.Equal("/src/a.cs", service.Breakpoints[1].FilePath);
        Assert.Equal("/src/b.cs", service.Breakpoints[2].FilePath);
    }
}
