namespace Benzene.Patterns.RealTimeRisk.PricingService;

/// <summary>
/// The generated service base, mapped so ASP.NET Core has a gRPC endpoint to route to - and
/// deliberately empty.
/// </summary>
/// <remarks>
/// Every method on <c>pricing.Pricing</c> is claimed by a <c>[GrpcMethod]</c> Benzene handler, and
/// <c>BenzeneInterceptor</c> intercepts the call before it reaches this type. It exists only because
/// <c>MapGrpcService&lt;T&gt;</c> needs a concrete service to bind the endpoint and the protobuf
/// descriptors to; the interceptor falls through to a base implementation only for methods no
/// handler claims, and here there are none. If a method is ever added to the .proto without a
/// matching handler, calls to it will surface as unimplemented - which is the right failure, and a
/// louder one than a silently empty response.
/// </remarks>
public class PricingGrpcService : Pricing.PricingBase
{
}
