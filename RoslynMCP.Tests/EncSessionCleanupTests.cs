using System.Reflection;
using Microsoft.CodeAnalysis.EditAndContinue;
using RoslynMCP.Services.HotReload;
using Xunit;

namespace RoslynMCP.Tests;

public class EncSessionCleanupTests
{
    public class EncProxy : DispatchProxy
    {
        public int Discards, Ends;
        public bool FailDiscard, FailEnd;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "DiscardSolutionUpdate")
            {
                Discards++;
                if (FailDiscard) throw new InvalidOperationException("discard failed");
            }
            else if (method.Name == "EndDebuggingSession")
            {
                Ends++;
                if (FailEnd) throw new InvalidOperationException("end failed");
            }
            else throw new NotSupportedException(method.Name);
            return null;
        }
    }

    [Fact]
    public void DiscardFailureStillEndsRoslynSessionAndDoesNotEndTwice()
    {
        var (session, proxy) = CreatePending();
        proxy.FailDiscard = true;
        Assert.Throws<InvalidOperationException>(session.EndSession);
        Assert.Equal(1, proxy.Ends);
        session.EndSession();
        Assert.Equal(1, proxy.Ends);
    }

    [Fact]
    public void EndFailureCanBeRetriedWithoutDiscardingSuccessfulUpdateTwice()
    {
        var (session, proxy) = CreatePending();
        proxy.FailEnd = true;
        Assert.Throws<InvalidOperationException>(session.EndSession);
        proxy.FailEnd = false;
        session.EndSession();
        session.EndSession();
        Assert.Equal(1, proxy.Discards);
        Assert.Equal(2, proxy.Ends);
    }

    private static (EncEditSession, EncProxy) CreatePending()
    {
        var service = DispatchProxy.Create<IEditAndContinueService, EncProxy>();
        var session = (EncEditSession)typeof(EncEditSession)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
            .Invoke([service, default(DebuggingSessionId), new EncPdbDocumentPaths()]);
        typeof(EncEditSession).GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session, true);
        return (session, (EncProxy)(object)service);
    }
}
