using System.Runtime.InteropServices;

namespace Bob.Windows.Discovery;

internal static class DnsServiceNative
{
    internal const uint ErrorSuccess = 0;
    internal const uint DnsRequestPending = 9506;
    internal const uint QueryRequestVersion1 = 1;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void RegisterCompleteCallback(
        uint status,
        nint queryContext,
        nint serviceInstance);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RegisterRequest
    {
        internal uint Version;
        internal uint InterfaceIndex;
        internal nint ServiceInstance;
        internal nint RegisterCompletionCallback;
        internal nint QueryContext;
        internal nint Credentials;
        internal int UnicastEnabled;
    }

    [DllImport(
        "dnsapi.dll",
        CharSet = CharSet.Unicode,
        CallingConvention = CallingConvention.Winapi,
        ExactSpelling = true,
        SetLastError = true)]
    internal static extern nint DnsServiceConstructInstance(
        string serviceName,
        string hostName,
        nint ip4Address,
        nint ip6Address,
        ushort port,
        ushort priority,
        ushort weight,
        uint propertiesCount,
        nint keys,
        nint values);

    [DllImport(
        "dnsapi.dll",
        CallingConvention = CallingConvention.Winapi,
        ExactSpelling = true,
        SetLastError = true)]
    internal static extern uint DnsServiceRegister(nint request, nint cancel);

    [DllImport(
        "dnsapi.dll",
        CallingConvention = CallingConvention.Winapi,
        ExactSpelling = true,
        SetLastError = true)]
    internal static extern uint DnsServiceDeRegister(nint request, nint cancel);

    [DllImport(
        "dnsapi.dll",
        CallingConvention = CallingConvention.Winapi,
        ExactSpelling = true)]
    internal static extern void DnsServiceFreeInstance(nint serviceInstance);
}

internal sealed class NativeUtf16StringArray : IDisposable
{
    private readonly nint[] _strings;

    internal NativeUtf16StringArray(IReadOnlyList<string> values)
    {
        _strings = new nint[values.Count];
        Pointer = values.Count == 0
            ? nint.Zero
            : Marshal.AllocHGlobal(checked(values.Count * nint.Size));

        try
        {
            for (var index = 0; index < values.Count; index++)
            {
                _strings[index] = Marshal.StringToHGlobalUni(values[index]);
                Marshal.WriteIntPtr(Pointer, index * nint.Size, _strings[index]);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal nint Pointer { get; private set; }

    public void Dispose()
    {
        for (var index = 0; index < _strings.Length; index++)
        {
            var value = _strings[index];
            if (value != nint.Zero)
            {
                Marshal.FreeHGlobal(value);
                _strings[index] = nint.Zero;
            }
        }

        if (Pointer != nint.Zero)
        {
            Marshal.FreeHGlobal(Pointer);
            Pointer = nint.Zero;
        }
    }
}
