# One HTTP API fronting the request/response services. Same two routes the local docker-compose slice
# exposes (POST /trades, GET /books/{book}/positions) - the black-box contract a shared test suite
# asserts against is therefore identical whether you hit the compose stack or this API.
resource "aws_apigatewayv2_api" "http" {
  name          = "${local.name}-api"
  protocol_type = "HTTP"
  tags          = local.base_tags
}

resource "aws_apigatewayv2_stage" "default" {
  api_id      = aws_apigatewayv2_api.http.id
  name        = "$default"
  auto_deploy = true
  tags        = local.base_tags

  access_log_settings {
    destination_arn = aws_cloudwatch_log_group.api.arn
    format = jsonencode({
      requestId      = "$context.requestId"
      httpMethod     = "$context.httpMethod"
      routeKey       = "$context.routeKey"
      status         = "$context.status"
      responseLen    = "$context.responseLength"
      integrationErr = "$context.integrationErrorMessage"
    })
  }
}

resource "aws_cloudwatch_log_group" "api" {
  name              = "/aws/apigateway/${local.name}"
  retention_in_days = var.log_retention_days
  tags              = local.base_tags
}

# POST /trades -> Trade Ledger
resource "aws_apigatewayv2_integration" "trade_ledger" {
  api_id                 = aws_apigatewayv2_api.http.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.trade_ledger.invoke_arn
  payload_format_version = "2.0"
}

resource "aws_apigatewayv2_route" "post_trades" {
  api_id    = aws_apigatewayv2_api.http.id
  route_key = "POST /trades"
  target    = "integrations/${aws_apigatewayv2_integration.trade_ledger.id}"
}

resource "aws_lambda_permission" "apigw_trade_ledger" {
  statement_id  = "AllowApiGwInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.trade_ledger.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.http.execution_arn}/*/*"
}

# GET /books/{book}/positions -> Risk Read Models
resource "aws_apigatewayv2_integration" "risk_read_models" {
  api_id                 = aws_apigatewayv2_api.http.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.risk_read_models.invoke_arn
  payload_format_version = "2.0"
}

resource "aws_apigatewayv2_route" "get_positions" {
  api_id    = aws_apigatewayv2_api.http.id
  route_key = "GET /books/{book}/positions"
  target    = "integrations/${aws_apigatewayv2_integration.risk_read_models.id}"
}

resource "aws_lambda_permission" "apigw_risk_read_models" {
  statement_id  = "AllowApiGwInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.risk_read_models.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.http.execution_arn}/*/*"
}
