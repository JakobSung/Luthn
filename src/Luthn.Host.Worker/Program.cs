using Luthn.Core.Persistence;
using Luthn.Host.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLuthnPersistence(builder.Configuration);
builder.Services.AddSafeProjectionSyncFoundation();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
