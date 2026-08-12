# Forward infra for services 4-5 (Market-Data Aggregator, Valuation, Risk Coordinator). Gated OFF by
# default because no port ships these on published packages yet (PARITY-FINDINGS.md §2). Kept here, in
# the SAME shared stack, so that when a language does implement them the deploy is still just "publish
# more images + flip a flag" - never "write new Terraform per language". Every resource stays
# container-image / language-opaque, exactly like the always-on slice.

# --- Market-Data Aggregator (stream processing) -----------------------------------------------------
resource "aws_kinesis_stream" "market_data" {
  count            = var.enable_market_data ? 1 : 0
  name             = "${local.name}-market-data"
  shard_count      = 2
  retention_period = 24
  tags             = local.base_tags
}

# Choreography bus: Market-Data emits bar:closed, Valuation reacts and emits position:revalued, Risk
# Read Models projects both. One custom EventBridge bus carries these topics.
resource "aws_cloudwatch_event_bus" "choreography" {
  count = var.enable_market_data ? 1 : 0
  name  = "${local.name}-choreography"
  tags  = local.base_tags
}

resource "aws_iam_role" "market_data" {
  count              = var.enable_market_data ? 1 : 0
  name               = "${local.name}-market-data"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
  tags               = local.base_tags
}

resource "aws_iam_role_policy_attachment" "market_data_basic" {
  count      = var.enable_market_data ? 1 : 0
  role       = aws_iam_role.market_data[0].name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

data "aws_iam_policy_document" "market_data" {
  count = var.enable_market_data ? 1 : 0
  statement {
    sid       = "ConsumeMarketDataStream"
    actions   = ["kinesis:DescribeStream", "kinesis:DescribeStreamSummary", "kinesis:GetRecords", "kinesis:GetShardIterator", "kinesis:ListShards"]
    resources = [aws_kinesis_stream.market_data[0].arn]
  }
  statement {
    sid       = "EmitBarClosed"
    actions   = ["events:PutEvents"]
    resources = [aws_cloudwatch_event_bus.choreography[0].arn]
  }
}

resource "aws_iam_role_policy" "market_data" {
  count  = var.enable_market_data ? 1 : 0
  name   = "market-data"
  role   = aws_iam_role.market_data[0].id
  policy = data.aws_iam_policy_document.market_data[0].json
}

resource "aws_cloudwatch_log_group" "market_data" {
  count             = var.enable_market_data ? 1 : 0
  name              = "/aws/lambda/${local.name}-market-data-aggregator"
  retention_in_days = var.log_retention_days
  tags              = local.base_tags
}

resource "aws_lambda_function" "market_data" {
  count         = var.enable_market_data ? 1 : 0
  function_name = "${local.name}-market-data-aggregator"
  role          = aws_iam_role.market_data[0].arn
  package_type  = "Image"
  image_uri     = var.service_images["market-data-aggregator"]
  memory_size   = var.lambda_memory_mb
  timeout       = var.lambda_timeout_seconds

  environment {
    variables = merge(local.common_env, {
      MARKET_DATA_STREAM_NAME = aws_kinesis_stream.market_data[0].name
      EVENT_BUS_NAME          = aws_cloudwatch_event_bus.choreography[0].name
    })
  }

  depends_on = [aws_cloudwatch_log_group.market_data]
  tags       = local.base_tags
}

resource "aws_lambda_event_source_mapping" "kinesis_to_aggregator" {
  count                              = var.enable_market_data ? 1 : 0
  event_source_arn                   = aws_kinesis_stream.market_data[0].arn
  function_name                      = aws_lambda_function.market_data[0].arn
  starting_position                  = "TRIM_HORIZON"
  batch_size                         = 500
  maximum_batching_window_in_seconds = 5
  parallelization_factor             = 1
  function_response_types            = ["ReportBatchItemFailures"]
}

# --- Valuation Service (choreography) ---------------------------------------------------------------
resource "aws_iam_role" "valuation" {
  count              = var.enable_market_data ? 1 : 0
  name               = "${local.name}-valuation"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
  tags               = local.base_tags
}

resource "aws_iam_role_policy_attachment" "valuation_basic" {
  count      = var.enable_market_data ? 1 : 0
  role       = aws_iam_role.valuation[0].name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

data "aws_iam_policy_document" "valuation" {
  count = var.enable_market_data ? 1 : 0
  statement {
    sid       = "EmitPositionRevalued"
    actions   = ["events:PutEvents"]
    resources = [aws_cloudwatch_event_bus.choreography[0].arn]
  }
}

resource "aws_iam_role_policy" "valuation" {
  count  = var.enable_market_data ? 1 : 0
  name   = "valuation"
  role   = aws_iam_role.valuation[0].id
  policy = data.aws_iam_policy_document.valuation[0].json
}

resource "aws_cloudwatch_log_group" "valuation" {
  count             = var.enable_market_data ? 1 : 0
  name              = "/aws/lambda/${local.name}-valuation-service"
  retention_in_days = var.log_retention_days
  tags              = local.base_tags
}

resource "aws_lambda_function" "valuation" {
  count         = var.enable_market_data ? 1 : 0
  function_name = "${local.name}-valuation-service"
  role          = aws_iam_role.valuation[0].arn
  package_type  = "Image"
  image_uri     = var.service_images["valuation-service"]
  memory_size   = var.lambda_memory_mb
  timeout       = var.lambda_timeout_seconds

  environment {
    variables = merge(local.common_env, {
      EVENT_BUS_NAME = aws_cloudwatch_event_bus.choreography[0].name
    })
  }

  depends_on = [aws_cloudwatch_log_group.valuation]
  tags       = local.base_tags
}

# bar:closed -> Valuation Service
resource "aws_cloudwatch_event_rule" "bar_closed" {
  count          = var.enable_market_data ? 1 : 0
  name           = "${local.name}-bar-closed"
  event_bus_name = aws_cloudwatch_event_bus.choreography[0].name
  event_pattern  = jsonencode({ "detail-type" = ["bar:closed"] })
  tags           = local.base_tags
}

resource "aws_cloudwatch_event_target" "bar_closed_valuation" {
  count          = var.enable_market_data ? 1 : 0
  rule           = aws_cloudwatch_event_rule.bar_closed[0].name
  event_bus_name = aws_cloudwatch_event_bus.choreography[0].name
  arn            = aws_lambda_function.valuation[0].arn
}

resource "aws_lambda_permission" "bar_closed_valuation" {
  count         = var.enable_market_data ? 1 : 0
  statement_id  = "AllowEventBridgeInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.valuation[0].function_name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.bar_closed[0].arn
}

# --- Risk Coordinator (map-reduce) ------------------------------------------------------------------
# Fires on an end-of-day schedule, scatters risk:shard across worker invocations, folds the partials.
resource "aws_iam_role" "risk_coordinator" {
  count              = var.enable_risk_coordinator ? 1 : 0
  name               = "${local.name}-risk-coordinator"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
  tags               = local.base_tags
}

resource "aws_iam_role_policy_attachment" "risk_coordinator_basic" {
  count      = var.enable_risk_coordinator ? 1 : 0
  role       = aws_iam_role.risk_coordinator[0].name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

# The coordinator invokes itself (or a worker alias) fan-out style - Lambda-to-Lambda invoke.
data "aws_iam_policy_document" "risk_coordinator" {
  count = var.enable_risk_coordinator ? 1 : 0
  statement {
    sid       = "ScatterToWorkers"
    actions   = ["lambda:InvokeFunction"]
    resources = ["arn:aws:lambda:${data.aws_region.current.name}:${data.aws_caller_identity.current.account_id}:function:${local.name}-risk-coordinator*"]
  }
}

resource "aws_iam_role_policy" "risk_coordinator" {
  count  = var.enable_risk_coordinator ? 1 : 0
  name   = "risk-coordinator"
  role   = aws_iam_role.risk_coordinator[0].id
  policy = data.aws_iam_policy_document.risk_coordinator[0].json
}

resource "aws_cloudwatch_log_group" "risk_coordinator" {
  count             = var.enable_risk_coordinator ? 1 : 0
  name              = "/aws/lambda/${local.name}-risk-coordinator"
  retention_in_days = var.log_retention_days
  tags              = local.base_tags
}

resource "aws_lambda_function" "risk_coordinator" {
  count         = var.enable_risk_coordinator ? 1 : 0
  function_name = "${local.name}-risk-coordinator"
  role          = aws_iam_role.risk_coordinator[0].arn
  package_type  = "Image"
  image_uri     = var.service_images["risk-coordinator"]
  memory_size   = var.lambda_memory_mb
  timeout       = 300

  environment {
    variables = local.common_env
  }

  depends_on = [aws_cloudwatch_log_group.risk_coordinator]
  tags       = local.base_tags
}

resource "aws_cloudwatch_event_rule" "end_of_day" {
  count               = var.enable_risk_coordinator ? 1 : 0
  name                = "${local.name}-end-of-day"
  schedule_expression = "cron(0 22 * * ? *)"
  tags                = local.base_tags
}

resource "aws_cloudwatch_event_target" "end_of_day_coordinator" {
  count = var.enable_risk_coordinator ? 1 : 0
  rule  = aws_cloudwatch_event_rule.end_of_day[0].name
  arn   = aws_lambda_function.risk_coordinator[0].arn
}

resource "aws_lambda_permission" "end_of_day_coordinator" {
  count         = var.enable_risk_coordinator ? 1 : 0
  statement_id  = "AllowScheduleInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.risk_coordinator[0].function_name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.end_of_day[0].arn
}
