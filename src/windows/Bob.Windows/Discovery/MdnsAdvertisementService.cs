using System.Runtime.InteropServices;
using Bob.Windows.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bob.Windows.Discovery;

internal sealed class MdnsAdvertisementService : IHostedService
{
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(2);
    private static readonly DnsServiceNative.RegisterCompleteCallback NativeCallback =
        OnNativeCompletion;
    private static readonly nint NativeCallbackPointer =
        Marshal.GetFunctionPointerForDelegate(NativeCallback);

    private readonly object _gate = new();
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<MdnsAdvertisementService> _logger;
    private readonly MdnsServiceDescriptor _descriptor;
    private readonly TaskCompletionSource _shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenRegistration _applicationStartedRegistration;
    private CancellationTokenRegistration _applicationStoppingRegistration;
    private NativeState _state;
    private nint _serviceInstance;
    private nint _request;
    private nint _callbackContext;
    private bool _stopRequested;

    public MdnsAdvertisementService(
        IHostApplicationLifetime applicationLifetime,
        ServerIdentity identity,
        ILogger<MdnsAdvertisementService> logger)
    {
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _descriptor = MdnsServiceDescriptor.Create(identity.ServerId, Environment.MachineName);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            _logger.LogWarning("mDNS advertisement requires Windows 10 or newer.");
            CompleteWithoutNativeResources();
            return Task.CompletedTask;
        }

        // Kestrel is ready when ApplicationStarted is signaled, so the service is
        // never advertised before its HTTPS endpoint can accept connections.
        _applicationStoppingRegistration =
            _applicationLifetime.ApplicationStopping.Register(RequestStop);
        _applicationStartedRegistration =
            _applicationLifetime.ApplicationStarted.Register(BeginRegistration);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _applicationStartedRegistration.Dispose();
        _applicationStoppingRegistration.Dispose();
        RequestStop();

        Task shutdownTask;
        lock (_gate)
        {
            shutdownTask = _shutdownCompletion.Task;
        }

        try
        {
            await shutdownTask.WaitAsync(ShutdownWait, cancellationToken);
        }
        catch (TimeoutException)
        {
            LogBoundedShutdownWarning();
        }
        catch (OperationCanceledException)
        {
            LogBoundedShutdownWarning();
        }
    }

    private void RequestStop()
    {
        lock (_gate)
        {
            _stopRequested = true;

            switch (_state)
            {
                case NativeState.Idle:
                    CompleteLocked();
                    break;
                case NativeState.Registered:
                    BeginDeregistrationLocked();
                    break;
            }
        }
    }

    private void BeginRegistration()
    {
        lock (_gate)
        {
            if (_state != NativeState.Idle || _stopRequested)
            {
                if (_stopRequested && _state == NativeState.Idle)
                {
                    CompleteLocked();
                }

                return;
            }

            try
            {
                var keys = _descriptor.Properties.Select(property => property.Key).ToArray();
                var values = _descriptor.Properties.Select(property => property.Value).ToArray();
                using var nativeKeys = new NativeUtf16StringArray(keys);
                using var nativeValues = new NativeUtf16StringArray(values);

                _serviceInstance = DnsServiceNative.DnsServiceConstructInstance(
                    _descriptor.ServiceName,
                    _descriptor.HostName,
                    nint.Zero,
                    nint.Zero,
                    _descriptor.Port,
                    priority: 0,
                    weight: 0,
                    (uint)_descriptor.Properties.Count,
                    nativeKeys.Pointer,
                    nativeValues.Pointer);

                if (_serviceInstance == nint.Zero)
                {
                    var lastError = Marshal.GetLastWin32Error();
                    _logger.LogWarning(
                        "mDNS service instance construction failed (Win32 error {Error}).",
                        lastError);
                    CompleteLocked();
                    return;
                }

                _callbackContext = AllocateCallbackContext(CallbackOperation.Register);
                _request = Marshal.AllocHGlobal(Marshal.SizeOf<DnsServiceNative.RegisterRequest>());
                WriteRequestLocked(_callbackContext);
                _state = NativeState.RegistrationPending;

                var status = DnsServiceNative.DnsServiceRegister(_request, nint.Zero);
                if (status != DnsServiceNative.DnsRequestPending)
                {
                    _logger.LogWarning(
                        "mDNS registration was rejected immediately (DNS status {Status}, Win32 error {Error}).",
                        status,
                        Marshal.GetLastWin32Error());
                    ReleaseCallbackContextWithoutCallbackLocked();
                    CompleteLocked();
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "mDNS registration could not be started.");
                ReleaseCallbackContextWithoutCallbackLocked();
                CompleteLocked();
            }
        }
    }

    private void BeginDeregistrationLocked()
    {
        if (_state != NativeState.Registered)
        {
            return;
        }

        try
        {
            _callbackContext = AllocateCallbackContext(CallbackOperation.Deregister);
            WriteRequestLocked(_callbackContext);
            _state = NativeState.DeregistrationPending;

            var status = DnsServiceNative.DnsServiceDeRegister(_request, nint.Zero);
            if (status != DnsServiceNative.DnsRequestPending)
            {
                _logger.LogWarning(
                    "mDNS deregistration was rejected immediately (DNS status {Status}, Win32 error {Error}).",
                    status,
                    Marshal.GetLastWin32Error());
                ReleaseCallbackContextWithoutCallbackLocked();
                CompleteLocked();
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "mDNS deregistration could not be started.");
            ReleaseCallbackContextWithoutCallbackLocked();
            CompleteLocked();
        }
    }

    private void WriteRequestLocked(nint callbackContext)
    {
        var request = new DnsServiceNative.RegisterRequest
        {
            Version = DnsServiceNative.QueryRequestVersion1,
            InterfaceIndex = 0,
            ServiceInstance = _serviceInstance,
            RegisterCompletionCallback = NativeCallbackPointer,
            QueryContext = callbackContext,
            Credentials = nint.Zero,
            UnicastEnabled = 0
        };

        Marshal.StructureToPtr(request, _request, fDeleteOld: false);
    }

    private nint AllocateCallbackContext(CallbackOperation operation)
    {
        var handle = GCHandle.Alloc(new CallbackContext(this, operation));
        return GCHandle.ToIntPtr(handle);
    }

    private static void OnNativeCompletion(
        uint status,
        nint queryContext,
        nint callbackInstance)
    {
        GCHandle contextHandle = default;
        CallbackContext? context = null;
        var freeCallbackInstance = callbackInstance != nint.Zero;

        try
        {
            if (queryContext == nint.Zero)
            {
                return;
            }

            contextHandle = GCHandle.FromIntPtr(queryContext);
            context = contextHandle.Target as CallbackContext;
            if (context is null)
            {
                return;
            }

            freeCallbackInstance = context.Owner.IsSeparateCallbackInstance(callbackInstance);
            context.Owner.HandleNativeCompletion(
                context.Operation,
                status,
                queryContext);
        }
        catch (Exception exception)
        {
            // Exceptions must never cross the unmanaged callback boundary.
            context?.Owner.LogUnexpectedCallbackFailure(exception);
        }
        finally
        {
            if (freeCallbackInstance)
            {
                TryFreeServiceInstance(callbackInstance);
            }

            if (contextHandle.IsAllocated)
            {
                contextHandle.Free();
            }
        }
    }

    private bool IsSeparateCallbackInstance(nint callbackInstance)
    {
        lock (_gate)
        {
            return callbackInstance != nint.Zero && callbackInstance != _serviceInstance;
        }
    }

    private void HandleNativeCompletion(
        CallbackOperation operation,
        uint status,
        nint queryContext)
    {
        lock (_gate)
        {
            if (_callbackContext == queryContext)
            {
                // The callback owns and frees this GCHandle after this method returns.
                _callbackContext = nint.Zero;
            }

            if (operation == CallbackOperation.Register)
            {
                HandleRegistrationCompletionLocked(status);
            }
            else
            {
                HandleDeregistrationCompletionLocked(status);
            }
        }
    }

    private void HandleRegistrationCompletionLocked(uint status)
    {
        if (_state != NativeState.RegistrationPending)
        {
            _logger.LogWarning(
                "Ignored an mDNS registration callback in unexpected state {State}.",
                _state);
            return;
        }

        if (status != DnsServiceNative.ErrorSuccess)
        {
            _logger.LogWarning(
                "mDNS registration failed asynchronously (DNS status {Status}).",
                status);
            CompleteLocked();
            return;
        }

        _state = NativeState.Registered;
        _logger.LogInformation(
            "Advertising {ServiceName} on port {Port} over mDNS.",
            _descriptor.ServiceName,
            _descriptor.Port);

        if (_stopRequested)
        {
            BeginDeregistrationLocked();
        }
    }

    private void HandleDeregistrationCompletionLocked(uint status)
    {
        if (_state != NativeState.DeregistrationPending)
        {
            _logger.LogWarning(
                "Received an mDNS deregistration callback in unexpected state {State}.",
                _state);
        }
        else if (status != DnsServiceNative.ErrorSuccess)
        {
            _logger.LogWarning(
                "mDNS deregistration completed with DNS status {Status}.",
                status);
        }
        else
        {
            _logger.LogInformation("Stopped advertising {ServiceName}.", _descriptor.ServiceName);
        }

        CompleteLocked();
    }

    private void CompleteWithoutNativeResources()
    {
        lock (_gate)
        {
            CompleteLocked();
        }
    }

    private void CompleteLocked()
    {
        if (_state == NativeState.Completed)
        {
            return;
        }

        if (_request != nint.Zero)
        {
            Marshal.FreeHGlobal(_request);
            _request = nint.Zero;
        }

        if (_serviceInstance != nint.Zero)
        {
            DnsServiceNative.DnsServiceFreeInstance(_serviceInstance);
            _serviceInstance = nint.Zero;
        }

        _state = NativeState.Completed;
        _shutdownCompletion.TrySetResult();
    }

    private void ReleaseCallbackContextWithoutCallbackLocked()
    {
        if (_callbackContext == nint.Zero)
        {
            return;
        }

        var handle = GCHandle.FromIntPtr(_callbackContext);
        _callbackContext = nint.Zero;
        if (handle.IsAllocated)
        {
            handle.Free();
        }
    }

    private void LogBoundedShutdownWarning()
    {
        _logger.LogWarning(
            "Timed out waiting for mDNS shutdown. Native state is retained until its callback or process exit to avoid an unsafe early free.");
    }

    private void LogUnexpectedCallbackFailure(Exception exception)
    {
        _logger.LogWarning(exception, "Unexpected failure in the native mDNS callback.");
    }

    private static void TryFreeServiceInstance(nint serviceInstance)
    {
        if (serviceInstance == nint.Zero)
        {
            return;
        }

        try
        {
            DnsServiceNative.DnsServiceFreeInstance(serviceInstance);
        }
        catch
        {
            // Native callbacks cannot surface managed failures.
        }
    }

    private sealed record CallbackContext(
        MdnsAdvertisementService Owner,
        CallbackOperation Operation);

    private enum CallbackOperation
    {
        Register,
        Deregister
    }

    private enum NativeState
    {
        Idle,
        RegistrationPending,
        Registered,
        DeregistrationPending,
        Completed
    }
}
