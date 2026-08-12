module github.com/daniellepelley/benzene-patterns/real-time-risk/go

// 1.24.7 (not a bare 1.24): the benzene-go dependency declares go 1.24.7, so the module graph
// requires at least that patch. go mod tidy pins it here.
go 1.24.7

// The published Benzene Go framework, consumed at the module-proxy pseudo-version because the
// port has no semver tag yet (see PARITY-FINDINGS.md §2 / benzene-go's RELEASING.md: the v0.1.0
// tag "could not be pushed", but the module IS resolvable through proxy.golang.org at a commit
// pseudo-version). Only the zero-dependency root-module packages are used here (benzene,
// httpbinding, envelope, httpstatus, wire, benzenetest), so this one require covers all of them.
// TODO: switch to v0.1.0 once tagged.
require github.com/daniellepelley/benzene-go v0.0.0-20260811220019-7a1e0a116aa5

require (
	github.com/aws/aws-sdk-go-v2 v1.30.3
	github.com/aws/aws-sdk-go-v2/config v1.27.27
	github.com/aws/aws-sdk-go-v2/credentials v1.17.27
	github.com/aws/aws-sdk-go-v2/service/dynamodb v1.34.3
	github.com/aws/aws-sdk-go-v2/service/dynamodbstreams v1.22.3
)

require (
	github.com/aws/aws-sdk-go-v2/feature/ec2/imds v1.16.11 // indirect
	github.com/aws/aws-sdk-go-v2/internal/configsources v1.3.15 // indirect
	github.com/aws/aws-sdk-go-v2/internal/endpoints/v2 v2.6.15 // indirect
	github.com/aws/aws-sdk-go-v2/internal/ini v1.8.0 // indirect
	github.com/aws/aws-sdk-go-v2/service/internal/accept-encoding v1.11.3 // indirect
	github.com/aws/aws-sdk-go-v2/service/internal/endpoint-discovery v1.9.16 // indirect
	github.com/aws/aws-sdk-go-v2/service/internal/presigned-url v1.11.17 // indirect
	github.com/aws/aws-sdk-go-v2/service/sso v1.22.4 // indirect
	github.com/aws/aws-sdk-go-v2/service/ssooidc v1.26.4 // indirect
	github.com/aws/aws-sdk-go-v2/service/sts v1.30.3 // indirect
	github.com/aws/smithy-go v1.20.3 // indirect
	github.com/jmespath/go-jmespath v0.4.0 // indirect
)
