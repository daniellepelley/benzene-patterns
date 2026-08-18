using Benzene.AspNet.Core;
using Benzene.Patterns.Cqrs.Write.TenantService;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
builder.UseBenzene<StartUp>();
var app = builder.Build();
app.UseBenzene();
app.Run();
