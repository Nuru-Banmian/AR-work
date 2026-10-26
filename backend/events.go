package main

import (
	"io"
	"net/http"
	"time"
)

// Called under the storage lock, only after the snapshot has been saved.
// One pending invalidation suffices: clients always query the latest full state.
func (a *api) notifySaved() {
	for subscriber := range a.subscribers {
		select {
		case subscriber <- struct{}{}:
		default:
		}
	}
}

func (a *api) events(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		fail(w, 405, "method_not_allowed", "Use GET")
		return
	}
	updates := make(chan struct{}, 1)
	a.mu.Lock()
	a.subscribers[updates] = true
	a.mu.Unlock()
	defer func() { a.mu.Lock(); delete(a.subscribers, updates); a.mu.Unlock() }()
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("X-Accel-Buffering", "no")
	controller := http.NewResponseController(w)
	write := func(frame string) error {
		// Refresh the server's ordinary write timeout for each SSE frame, and
		// bound blocked writes so a slow or vanished client cannot leak a handler.
		if err := controller.SetWriteDeadline(time.Now().Add(5 * time.Second)); err != nil {
			return err
		}
		if _, err := io.WriteString(w, frame); err != nil {
			return err
		}
		return controller.Flush()
	}
	if write("event: ready\ndata: {}\n\n") != nil {
		return
	}
	heartbeat := time.NewTicker(10 * time.Second)
	defer heartbeat.Stop()
	for {
		select {
		case <-r.Context().Done():
			return
		case <-updates:
			if write("event: issues-changed\ndata: {}\n\n") != nil {
				return
			}
		case <-heartbeat.C:
			if write(": heartbeat\n\n") != nil {
				return
			}
		}
	}
}
