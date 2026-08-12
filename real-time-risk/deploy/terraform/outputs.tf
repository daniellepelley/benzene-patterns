output "api_base_url" {
  description = "Base URL of the HTTP API. POST {url}/trades and GET {url}/books/{book}/positions."
  value       = aws_apigatewayv2_api.http.api_endpoint
}

output "trades_table_name" {
  description = "The ledger/event-store DynamoDB table name."
  value       = aws_dynamodb_table.trades.name
}

output "trades_stream_arn" {
  description = "The ledger table's DynamoDB stream ARN (the CDC source for Risk Read Models)."
  value       = aws_dynamodb_table.trades.stream_arn
}

output "market_data_stream_name" {
  description = "Kinesis stream name for the Market-Data Aggregator (null unless enable_market_data)."
  value       = var.enable_market_data ? aws_kinesis_stream.market_data[0].name : null
}

output "language" {
  description = "Which language port this stack instance was deployed for."
  value       = var.language
}
