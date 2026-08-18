using Benzene.AspNet.Core;
using Benzene.Patterns.ModularMonolith.Services.BillingService;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
builder.UseBenzene<StartUp>();
var app = builder.Build();
app.UseBenzene();
app.Run();
