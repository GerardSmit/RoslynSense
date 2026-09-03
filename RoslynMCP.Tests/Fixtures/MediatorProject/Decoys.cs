using MediatorFixture.Orders;

namespace MediatorFixture;

/// <summary>
/// Things that look like a dispatch and are not. None of them may appear in any result.
/// </summary>
public sealed class Decoys
{
    private readonly Transport _transport = new();

    /// <summary>A Send on something that is not a mediator.</summary>
    public void SendBytes(byte[] payload) => _transport.Send(payload);

    /// <summary>GetOrderQuery is only mentioned here, in a comment.</summary>
    public string Mentioned() => "GetOrderQuery";

    /// <summary>The request type named, but constructed rather than dispatched.</summary>
    public GetOrderQuery Build(int id) => new(id);

    private sealed class Transport
    {
        public void Send(byte[] payload)
        {
            _ = payload;
        }
    }
}
