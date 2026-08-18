using Benzene.AspNet.Core;
using Benzene.Grpc.AspNet;
using Benzene.Patterns.RealTimeRisk.PricingService;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);

// Run StartUp.GetConfiguration + StartUp.ConfigureServices, stashing StartUp for the call below.
builder.UseBenzene<StartUp>();

var app = builder.Build();

// The generated endpoint the interceptor routes through (see PricingGrpcService), plus the two
// opt-in services StartUp enabled.
app.MapGrpcService<PricingGrpcService>();
app.MapBenzeneGrpcReflectionService();
app.MapBenzeneGrpcHealthService();

// Run StartUp.Configure against the built pipeline, wiring Benzene's gRPC pipeline in.
app.UseBenzene();

app.Run();
