using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Extreme.Net;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpWebProxy.Data;

namespace SharpWebProxy
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void Configure(IApplicationBuilder app, ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory, IOptions<SiteConfig> cfg)
        {
            var logger = loggerFactory.CreateLogger("Default");

            var sessionOptions = new SessionOptions();
            sessionOptions.Cookie.Domain = cfg.Value.UrlSuffix;
            app.UseSession(sessionOptions);

            app.Run(async context =>
            {
                var connectionFeature = context.Connection;
                logger.LogDebug($"Peer: {connectionFeature.RemoteIpAddress?.ToString()}:{connectionFeature.RemotePort}"
                                + $"{Environment.NewLine}"
                                + $"Sock: {connectionFeature.LocalIpAddress?.ToString()}:{connectionFeature.LocalPort}");
                using (var scope = scopeFactory.CreateScope())
                {
                    // alternatively resolve UserManager instead and pass that if only think you want to seed are the users     
                    var handler = scope.ServiceProvider.GetRequiredService<RequestHandler>();
                    await handler.HandleRequest(context);
                }
            });
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<SiteConfig>(Configuration.GetSection("SiteConfig"));
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite(
                    Configuration.GetConnectionString("DefaultConnection")));

            var handler = new HttpClientHandler() {UseCookies = false, AllowAutoRedirect = false};
            // var handler = new ProxyHandler(new Socks5ProxyClient("127.0.0.1", 1080)) {UseCookies = false};
            services.AddSingleton<HttpClient>(new HttpClient(handler));

            services.AddScoped<DomainNameReplacer>();
            services.AddScoped<RequestHandler>();
            services.AddDistributedMemoryCache();
            services.AddSession();
        }

        public static IWebHost BuildWebHost(string[] args)
        {
            var hostBuilder = WebHost.CreateDefaultBuilder(args)
                .ConfigureLogging((_, factory) => { factory.AddConsole().SetMinimumLevel(LogLevel.Warning); })
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    var env = hostingContext.HostingEnvironment;
                    config.AddJsonFile("appsettings.json", optional: true)
                        .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true);
                })
                .UseKestrel((context, options) =>
                {
                    if (context.HostingEnvironment.IsDevelopment())
                    {
                        ShowConfig(context.Configuration);
                    }

                    var basePort = context.Configuration.GetValue<int?>("BASE_PORT") ?? 5000;

                    options.ConfigureEndpointDefaults(opt => { opt.NoDelay = true; });

                    options.ConfigureHttpsDefaults(httpsOptions => { httpsOptions.SslProtocols = SslProtocols.Tls12; });

                    // Run callbacks on the transport thread
                    options.ApplicationSchedulingMode = SchedulingMode.Inline;

                    options.Listen(IPAddress.Loopback, basePort, listenOptions =>
                    {
                        // Uncomment the following to enable Nagle's algorithm for this endpoint.
                        //listenOptions.NoDelay = false;

                        listenOptions.UseConnectionLogging();
                    });

                    options.ListenAnyIP(basePort + 3);

                    // options.UseSystemd();

                    // The following section should be used to demo sockets
                    //options.ListenUnixSocket("/tmp/kestrel-test.sock");
                })
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseStartup<Startup>();

            if (string.Equals(Process.GetCurrentProcess().Id.ToString(),
                Environment.GetEnvironmentVariable("LISTEN_PID")))
            {
                // Use libuv if activated by systemd, since that's currently the only transport that supports being passed a socket handle.
                hostBuilder.UseLibuv(options =>
                {
                    // Uncomment the following line to change the default number of libuv threads for all endpoints.
                    // options.ThreadCount = 4;
                });
            }

            return hostBuilder.Build();
        }

        public static Task Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Console.WriteLine("Unobserved exception: {0}", e.Exception);
            };

            return BuildWebHost(args).RunAsync();
        }

        private static void ShowConfig(IConfiguration config)
        {
            foreach (var pair in config.GetChildren())
            {
                Console.WriteLine($"{pair.Path} - {pair.Value}");
                ShowConfig(pair);
            }
        }
    }
}