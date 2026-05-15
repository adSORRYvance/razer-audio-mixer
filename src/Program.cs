using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazerMixerBridge;
using RazerMixerBridge.Audio;
using RazerMixerBridge.Config;
using RazerMixerBridge.Hid;

string configPath = args.Length > 0
    ? args[0]
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "razer-mixer-bridge", "config.toml");

BridgeConfig config = File.Exists(configPath)
    ? ConfigLoader.Load(configPath)
    : new BridgeConfig();

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddSingleton(config)
    .AddSingleton<HidDeviceWatcher>()
    .AddSingleton<PulseAudioClient>()
    .AddSingleton<FaderRouter>()
    .AddHostedService<Bridge>();

builder.Services.AddLogging(l =>
{
    l.AddConsole();
    l.SetMinimumLevel(LogLevel.Information);
});

// Enable sd_notify + systemd journal logging when running under systemd.
builder.Services.AddSystemd();

var host = builder.Build();
await host.RunAsync();
