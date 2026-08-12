# The Trade Ledger's event store (event sourcing, reference doc §5) AND the CDC source that feeds the
# Risk Read Models projection. One item per event, keyed (pk = stream/book id, version = 1-based
# sequence). Attribute names match Benzene.EventSourcing.DynamoDb's DynamoDbEventStore defaults
# (pk/version) and its hard-coded payload attributes (eventType/payload/timestamp) - the non-.NET
# ports implement the same item shape by hand (see PARITY-FINDINGS.md §3.1), so this single table
# definition is genuinely shared across all four languages.
resource "aws_dynamodb_table" "trades" {
  name         = "${local.name}-trades"
  billing_mode = "PAY_PER_REQUEST"

  hash_key  = "pk"
  range_key = "version"

  attribute {
    name = "pk"
    type = "S"
  }

  attribute {
    name = "version"
    type = "N"
  }

  # The Risk Read Models projection consumes this stream (DynamoDB Streams CDC). NEW_AND_OLD_IMAGES so
  # a consumer sees the full event body on INSERT. This is the ONE piece of infra shared between the
  # two always-on services, and consuming it (topic convention "{table}:{eventName}", e.g.
  # "<name>-trades:INSERT") is real in all four ports.
  stream_enabled   = true
  stream_view_type = "NEW_AND_OLD_IMAGES"

  point_in_time_recovery {
    enabled = true
  }

  tags = local.base_tags
}
