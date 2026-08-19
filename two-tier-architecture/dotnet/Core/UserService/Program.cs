using Benzene.HostedService;
using Benzene.Patterns.TwoTier.Core.UserService;

// The whole entry point. StartUp declares HTTP as a transport alongside any other this service might
// grow, so this file does not change when that happens.
await BenzeneHost.RunAsync<StartUp>(args);
