using Benzene.HostedService;
using Benzene.Patterns.Cqrs.Write.TenantService;

// The whole entry point. StartUp declares HTTP as a transport alongside any other this service might
// grow, so this file does not change when that happens.
//
// BenzeneHost.RunAsync is exactly Host.CreateDefaultBuilder(args).UseBenzene<StartUp>().Build()
// .RunAsync() - the explicit form is one rung down and stays available; take it (or BenzeneHost.Build,
// as transactional-outbox/OrdersService does) the moment you need the IHost in hand.
await BenzeneHost.RunAsync<StartUp>(args);
