# The service Lambdas. Note what is NOT here: no `runtime`, no `handler`, no `filename`. Every function
# is `package_type = "Image"` and takes an opaque `image_uri`. THIS is what makes the stack shared:
# swapping the .NET image for the Go image is a variable change, not a Terraform change. The Dockerfile
# that produces each image is the only per-language artifact.

# --- Trade Ledger (event sourcing) - HTTP command handler behind API Gateway ------------------------
resource "aws_cloudwatch_log_group" "trade_ledger" {
  name              = "/aws/lambda/${local.name}-trade-ledger"
  retention_in_days = var.log_retention_days
  tags              = local.base_tags
}

resource "aws_lambda_function" "trade_ledger" {
  function_name = "${local.name}-trade-ledger"
  role          = aws_iam_role.trade_ledger.arn
  package_type  = "Image"
  image_uri     = var.service_images["trade-ledger"]
  memory_size   = var.lambda_memory_mb
  timeout       = var.lambda_timeout_seconds

  environment {
    variables = local.common_env
  }

  depends_on = [aws_cloudwatch_log_group.trade_ledger]
  tags       = local.base_tags
}

# --- Risk Read Models (CQRS) - DynamoDB-stream projector + HTTP query handler in one function --------
# Every language ships this as a SINGLE function. .NET / TS / Python multiplex the stream trigger and
# the HTTP query natively; Go, though one-trigger-per-binary at the framework level, multiplexes them
# on the event shape inside one binary too (real-time-risk/go/cmd/lambda-risk-read-models). So this one
# function - with both the stream event-source mapping AND the API route pointing at it - is uniform
# across all four ports; no Go-specific two-function variant is needed (see PARITY-FINDINGS.md §3.4 and
# the Go/Python PARITY-NOTES on the in-memory-read-model caveat).
resource "aws_cloudwatch_log_group" "risk_read_models" {
  name              = "/aws/lambda/${local.name}-risk-read-models"
  retention_in_days = var.log_retention_days
  tags              = local.base_tags
}

resource "aws_lambda_function" "risk_read_models" {
  function_name = "${local.name}-risk-read-models"
  role          = aws_iam_role.risk_read_models.arn
  package_type  = "Image"
  image_uri     = var.service_images["risk-read-models"]
  memory_size   = var.lambda_memory_mb
  timeout       = var.lambda_timeout_seconds

  environment {
    variables = local.common_env
  }

  depends_on = [aws_cloudwatch_log_group.risk_read_models]
  tags       = local.base_tags
}

# CDC: the ledger table's stream drives the projection. TRIM_HORIZON + idempotent (book, version) apply
# means a cold start replays safely; ReportBatchItemFailures gives per-record retry. Filtered to INSERT
# because the ledger is append-only - MODIFY/REMOVE never happen, but the filter makes that explicit
# and cheap.
resource "aws_lambda_event_source_mapping" "ledger_to_projection" {
  event_source_arn                   = aws_dynamodb_table.trades.stream_arn
  function_name                      = aws_lambda_function.risk_read_models.arn
  starting_position                  = "TRIM_HORIZON"
  batch_size                         = 100
  maximum_batching_window_in_seconds = 1
  function_response_types            = ["ReportBatchItemFailures"]

  filter_criteria {
    filter {
      pattern = jsonencode({ eventName = ["INSERT"] })
    }
  }
}
