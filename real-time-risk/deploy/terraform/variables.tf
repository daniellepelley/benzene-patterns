# The whole point of this stack: it is IDENTICAL for every Benzene language port. The only thing that
# differs between a .NET deploy and a Go deploy is which container image each service's Lambda runs -
# and that is passed in as a variable (`service_images`), never hard-coded here. "Only the compilation
# changes" is enforced structurally: there is no `runtime`, no `handler`, no language name anywhere in
# the resources, because container-image Lambdas are opaque to all three.

variable "region" {
  description = "AWS region to deploy into."
  type        = string
  default     = "eu-west-1"
}

variable "language" {
  description = <<-EOT
    Which language port this deployment is for: one of dotnet | typescript | python | go. Used ONLY
    for resource naming/tagging so all four ports can coexist in one AWS account as isolated but
    byte-for-byte-identically-shaped stacks. It changes no resource's shape - it is a label, exactly
    mirroring the fact that the language changes nothing but the compiled artifact.
  EOT
  type        = string

  validation {
    condition     = contains(["dotnet", "typescript", "python", "go"], var.language)
    error_message = "language must be one of: dotnet, typescript, python, go."
  }
}

variable "name_prefix" {
  description = "Prefix for all resource names. Combined with `language` -> e.g. `rtr-dotnet-trades`."
  type        = string
  default     = "rtr"
}

variable "service_images" {
  description = <<-EOT
    Map of service name -> ECR image URI (with tag or digest) for that service's Lambda. This is the
    ONE per-language input. Each language builds the same service from the same Dockerfile shape and
    pushes an image; the image URI is all Terraform ever sees. Keys are the logical service names:
    "trade-ledger", "risk-read-models", and (when enabled) "market-data-aggregator",
    "valuation-service", "risk-coordinator". The Pricing Service (gRPC) is intentionally NOT here - it
    is not Lambda-deployable in any port (see PARITY-FINDINGS.md), so it lives outside this module.
  EOT
  type        = map(string)
}

variable "enable_market_data" {
  description = "Deploy the Market-Data Aggregator (Kinesis) + Valuation Service. Off until a port ships them."
  type        = bool
  default     = false
}

variable "enable_risk_coordinator" {
  description = "Deploy the Risk Coordinator (map-reduce over Lambda). Off until a port ships it."
  type        = bool
  default     = false
}

variable "lambda_memory_mb" {
  description = "Memory (MB) for the service Lambdas. Applied identically to every language."
  type        = number
  default     = 512
}

variable "lambda_timeout_seconds" {
  description = "Timeout (s) for the service Lambdas."
  type        = number
  default     = 30
}

variable "log_retention_days" {
  description = "CloudWatch Logs retention for every service's log group."
  type        = number
  default     = 14
}

variable "tags" {
  description = "Extra tags merged onto every taggable resource."
  type        = map(string)
  default     = {}
}
