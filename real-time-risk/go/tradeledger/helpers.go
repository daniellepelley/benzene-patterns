package tradeledger

import (
	"encoding/json"
	"errors"

	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/eventstore"
)

// defaultMarshal is the production event serializer (encoding/json.Marshal), wired through the
// jsonMarshal seam so a test could substitute a failing marshaler to exercise the error path.
func defaultMarshal(v any) ([]byte, error) { return json.Marshal(v) }

// isConcurrency reports whether err is (or wraps) the store's optimistic-concurrency conflict.
func isConcurrency(err error) bool { return errors.Is(err, eventstore.ErrConcurrency) }
