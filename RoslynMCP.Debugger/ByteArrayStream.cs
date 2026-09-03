using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ClrDebug;

namespace RoslynMCP.Debugger;

/// <summary>
/// A read-only COM <c>IStream</c> over bytes already in memory.
/// </summary>
/// <remarks>
/// <para>
/// The symbol reader's Edit-and-Continue entry point takes a stream and nothing else, while the
/// delta PDB arrives as a byte array — so one of the two has to give. Spilling it to a temporary
/// file and opening that would work, but it puts a disk write and a delete on the path of every
/// single edit, and leaves a file behind whenever the apply throws.
/// </para>
/// <para>
/// Only the members a reader actually calls are implemented. Everything that would mutate the
/// stream returns <c>E_NOTIMPL</c> rather than pretending: a caller that tries to write to the
/// delta is doing something this type cannot honour, and failing loudly is the only honest answer.
/// </para>
/// </remarks>
[GeneratedComClass]
internal sealed partial class ByteArrayStream : IStream
{
    private readonly byte[] _bytes;
    private long _position;

    public ByteArrayStream(byte[] bytes) => _bytes = bytes;

    public HRESULT Read(IntPtr pv, int cb, out int pcbRead)
    {
        int count = (int)Math.Min(cb < 0 ? 0 : cb, Math.Max(0, _bytes.LongLength - _position));
        if (count > 0)
        {
            Marshal.Copy(_bytes, (int)_position, pv, count);
            _position += count;
        }

        pcbRead = count;
        return HRESULT.S_OK;
    }

    public unsafe HRESULT Seek(LARGE_INTEGER dlibMove, STREAM_SEEK dwOrigin, ULARGE_INTEGER* plibNewPosition)
    {
        long origin = dwOrigin switch
        {
            STREAM_SEEK.STREAM_SEEK_SET => 0,
            STREAM_SEEK.STREAM_SEEK_CUR => _position,
            STREAM_SEEK.STREAM_SEEK_END => _bytes.LongLength,
            _ => -1,
        };
        if (origin < 0)
            return HRESULT.E_INVALIDARG;

        long target = origin + dlibMove.QuadPart;
        if (target < 0)
            return HRESULT.E_INVALIDARG;

        // Seeking past the end is legal for a stream; it simply reads nothing from there.
        _position = target;
        if (plibNewPosition is not null)
            plibNewPosition->QuadPart = target;
        return HRESULT.S_OK;
    }

    public HRESULT Stat(out STATSTG pstatstg, STATFLAG grfStatFlag)
    {
        pstatstg = new STATSTG
        {
            // The bytes have no name to report, and STATFLAG_NONAME asks us not to invent one.
            pwcsName = null!,
            type = STGTY.STGTY_STREAM,
            cbSize = new ULARGE_INTEGER { QuadPart = _bytes.LongLength },
            grfMode = 0,
        };
        return HRESULT.S_OK;
    }

    public HRESULT Write(IntPtr pv, int cb, out int pcbWritten)
    {
        pcbWritten = 0;
        return HRESULT.E_NOTIMPL;
    }

    public HRESULT SetSize(ULARGE_INTEGER libNewSize) => HRESULT.E_NOTIMPL;
    public HRESULT CopyTo(IStream pstm, ULARGE_INTEGER cb, out ULARGE_INTEGER pcbRead, out ULARGE_INTEGER pcbWritten)
    {
        pcbRead = default;
        pcbWritten = default;
        return HRESULT.E_NOTIMPL;
    }
    public HRESULT Commit(STGC grfCommitFlags) => HRESULT.S_OK;
    public HRESULT Revert() => HRESULT.S_OK;
    public HRESULT LockRegion(ULARGE_INTEGER libOffset, ULARGE_INTEGER cb, int dwLockType) => HRESULT.E_NOTIMPL;
    public HRESULT UnlockRegion(ULARGE_INTEGER libOffset, ULARGE_INTEGER cb, int dwLockType) => HRESULT.E_NOTIMPL;
    public HRESULT Clone(out IStream ppstm)
    {
        ppstm = new ByteArrayStream(_bytes) { _position = _position };
        return HRESULT.S_OK;
    }
}
