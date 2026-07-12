using System.Security.Authentication;
using System.Threading;
using System.Windows;
using Bob.Windows.Discovery;
using Bob.Windows.Security;
using Bob.Windows.Transport;
using Bob.Windows.UI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Bob.Windows;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\BOB.Windows.SingleInstance.v1";
    private WebApplication? _host;
    private ServerIdentity? _identity;
    private Mutex? _singleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var instanceMutex = new Mutex(
            initiallyOwned: true,
            name: SingleInstanceMutexName,
            createdNew: out var createdNew);
        if (!createdNew)
        {
            instanceMutex.Dispose();
            MessageBox.Show(
                "BOB is already running.",
                "BOB",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        _singleInstanceMutex = instanceMutex;

        try
        {
            var paths = AppPaths.ForCurrentUser();
            var identityStore = new ServerIdentityStore(paths);
            var identity = identityStore.LoadOrCreate();
            _identity = identity;

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = e.Args,
                ApplicationName = typeof(App).Assembly.FullName,
                ContentRootPath = AppContext.BaseDirectory
            });

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(BobProtocol.DefaultPort, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(https =>
                    {
                        https.ServerCertificate = identity.Certificate;
                        https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    });
                });
            });

            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton(identity);
            builder.Services.AddHostedService<MdnsAdvertisementService>();
            builder.Services.AddSingleton<SessionCoordinator>();
            builder.Services.AddSingleton<WebSocketSessionHandler>();
            builder.Services.AddSingleton<MainWindowViewModel>();
            builder.Services.AddSingleton<MainWindow>();

            _host = builder.Build();
            BobEndpoints.Map(_host);

            await _host.StartAsync();
            _host.Services.GetRequiredService<MainWindow>().Show();
        }
        catch (Exception)
        {
            MessageBox.Show(
                "BOB could not start.\n\n"
                + "Check the BOB files and make sure port 42424 is available, then try again.",
                "BOB startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                var host = _host;
                Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await host.StopAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Process exit is the final shutdown boundary.
                    }
                    finally
                    {
                        await host.DisposeAsync().ConfigureAwait(false);
                    }
                }).GetAwaiter().GetResult();
            }
        }
        finally
        {
            try
            {
                _identity?.Dispose();
            }
            finally
            {
                ReleaseSingleInstance();
                base.OnExit(e);
            }
        }
    }

    private void ReleaseSingleInstance()
    {
        var instanceMutex = Interlocked.Exchange(ref _singleInstanceMutex, null);
        if (instanceMutex is null)
        {
            return;
        }

        try
        {
            instanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Shutdown still needs to dispose the handle if ownership was already lost.
        }
        finally
        {
            instanceMutex.Dispose();
        }
    }
}
