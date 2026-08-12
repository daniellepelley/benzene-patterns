provider "aws" {
  region = var.region
}

data "aws_caller_identity" "current" {}
data "aws_region" "current" {}

locals {
  name = "${var.name_prefix}-${var.language}"

  base_tags = merge(
    {
      "benzene:pattern"  = "real-time-risk"
      "benzene:language" = var.language
      "ManagedBy"        = "terraform"
    },
    var.tags,
  )

  # Environment variables every service Lambda receives. Identical shape across languages - each port
  # reads TRADES_TABLE_NAME / MARKET_DATA_STREAM_NAME / EVENT_BUS_NAME from its own config the same way
  # the docker-compose local stacks read DYNAMODB_SERVICE_URL. On real AWS there is no service-URL
  # override: the SDK's default credential/endpoint chain (the Lambda's execution role) is used.
  common_env = {
    TRADES_TABLE_NAME = aws_dynamodb_table.trades.name
    BENZENE_LANGUAGE  = var.language
  }
}
