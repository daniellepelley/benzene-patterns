# One execution role per service, least-privilege. The role SHAPES are language-agnostic: a .NET
# Lambda and a Go Lambda projecting the same stream need exactly the same permissions, because the
# permission is about the AWS resource, not the runtime.

data "aws_iam_policy_document" "lambda_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

# --- Trade Ledger: writes events to the table, reads a stream's current version before appending ---
resource "aws_iam_role" "trade_ledger" {
  name               = "${local.name}-trade-ledger"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
  tags               = local.base_tags
}

resource "aws_iam_role_policy_attachment" "trade_ledger_basic" {
  role       = aws_iam_role.trade_ledger.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

data "aws_iam_policy_document" "trade_ledger" {
  statement {
    sid       = "LedgerTableReadWrite"
    actions   = ["dynamodb:PutItem", "dynamodb:GetItem", "dynamodb:Query", "dynamodb:TransactWriteItems"]
    resources = [aws_dynamodb_table.trades.arn]
  }
}

resource "aws_iam_role_policy" "trade_ledger" {
  name   = "ledger-table"
  role   = aws_iam_role.trade_ledger.id
  policy = data.aws_iam_policy_document.trade_ledger.json
}

# --- Risk Read Models: reads the ledger stream (CDC), serves queries from its own projection ---
resource "aws_iam_role" "risk_read_models" {
  name               = "${local.name}-risk-read-models"
  assume_role_policy = data.aws_iam_policy_document.lambda_assume.json
  tags               = local.base_tags
}

resource "aws_iam_role_policy_attachment" "risk_read_models_basic" {
  role       = aws_iam_role.risk_read_models.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

data "aws_iam_policy_document" "risk_read_models" {
  statement {
    sid = "ConsumeLedgerStream"
    actions = [
      "dynamodb:DescribeStream",
      "dynamodb:GetRecords",
      "dynamodb:GetShardIterator",
      "dynamodb:ListStreams",
    ]
    resources = [aws_dynamodb_table.trades.stream_arn]
  }
}

resource "aws_iam_role_policy" "risk_read_models" {
  name   = "ledger-stream"
  role   = aws_iam_role.risk_read_models.id
  policy = data.aws_iam_policy_document.risk_read_models.json
}
