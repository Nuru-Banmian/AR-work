package main

import (
	"bytes"
	"encoding/base64"
	"errors"
	"image"
	_ "image/jpeg"
	_ "image/png"
	"net/http"
	"strings"
)

// Photos and metadata share one atomic file replacement; no visible half-report.
func validateSnapshot(encoded string, required bool) ([]byte, error) {
	if encoded == "" {
		if required {
			return nil, errors.New("Confirmed snapshot is required")
		}
		return nil, nil
	}
	if len(encoded) > 8*1024*1024 {
		return nil, errors.New("Snapshot must be at most 6 MiB")
	}
	data, err := base64.StdEncoding.Strict().DecodeString(encoded)
	if err != nil || len(data) > 6*1024*1024 {
		return nil, errors.New("Snapshot must be base64 JPEG or PNG, at most 6 MiB")
	}
	config, format, err := image.DecodeConfig(bytes.NewReader(data))
	if err != nil || (format != "jpeg" && format != "png") || config.Width < 16 || config.Height < 16 || config.Width > 8192 || config.Height > 8192 || int64(config.Width)*int64(config.Height) > 24000000 {
		return nil, errors.New("Snapshot must be JPEG/PNG, 16–8192 pixels per side, at most 24 megapixels")
	}
	if _, _, err = image.Decode(bytes.NewReader(data)); err != nil {
		return nil, errors.New("Snapshot pixels are incomplete or corrupt")
	}
	return data, nil
}

func cloneMedia(original map[string][]byte) map[string][]byte {
	next := make(map[string][]byte, len(original)+1)
	for id, data := range original {
		next[id] = data
	}
	return next
}

func (a *api) readSnapshot(w http.ResponseWriter, r *http.Request) {
	if r.Method != "GET" {
		fail(w, 405, "method_not_allowed", "Use GET")
		return
	}
	id := strings.TrimSuffix(strings.TrimPrefix(r.URL.Path, "/api/issues/"), "/snapshot")
	a.mu.Lock()
	if a.deleted[id] {
		a.mu.Unlock()
		deletedIssue(w, id)
		return
	}
	data := a.media[id]
	a.mu.Unlock()
	if len(data) == 0 {
		fail(w, 404, "snapshot_not_found", "No snapshot available for this issue")
		return
	}
	w.Header().Set("Content-Type", http.DetectContentType(data))
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(200)
	_, _ = w.Write(data)
}
