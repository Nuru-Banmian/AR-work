package main

import (
	"bytes"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"log"
	"math"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

type Position struct {
	X float64 `json:"x"`
	Y float64 `json:"y"`
	Z float64 `json:"z"`
}

type Issue struct {
	SnapshotURL string    `json:"snapshotUrl,omitempty"`
	ID          string    `json:"id"`
	Title       string    `json:"title"`
	Description string    `json:"description"`
	Priority    string    `json:"priority"`
	Status      string    `json:"status"`
	Position    Position  `json:"position"`
	CreatedAt   time.Time `json:"createdAt"`
	UpdatedAt   time.Time `json:"updatedAt"`
}

type api struct {
	mu          sync.Mutex
	path        string
	media       map[string][]byte
	issues      []Issue
	origins     map[string]bool
	requests    map[string]creation
	subscribers map[chan struct{}]bool
}

// The original result is retained even when the issue's status changes later.
type creation struct {
	Fingerprint string `json:"fingerprint"`
	Result      Issue  `json:"result"`
}

type snapshot struct {
	Media    map[string][]byte   `json:"media,omitempty"`
	Issues   []Issue             `json:"issues"`
	Requests map[string]creation `json:"requests"`
}

func newAPI(path, allowedOrigins string) (http.Handler, error) {
	a := &api{media: map[string][]byte{}, path: path, issues: []Issue{}, origins: map[string]bool{}, requests: map[string]creation{}, subscribers: map[chan struct{}]bool{}}
	for _, origin := range strings.Split(allowedOrigins, ",") {
		a.origins[strings.TrimSpace(origin)] = true
	}
	data, err := os.ReadFile(path)
	if err == nil {
		// Read the legacy issue array without rewriting it until a successful mutation.
		if strings.HasPrefix(strings.TrimSpace(string(data)), "[") {
			err = json.Unmarshal(data, &a.issues)
		} else {
			var stored snapshot
			err = json.Unmarshal(data, &stored)
			a.issues = stored.Issues
			if stored.Media != nil {
				a.media = stored.Media
			}
			if stored.Requests != nil {
				a.requests = stored.Requests
			}
		}
		if err != nil {
			return nil, err
		}
		if a.issues == nil {
			return nil, errors.New("data file must contain an issue array")
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return nil, err
	}
	return a, nil
}

func respond(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}

func fail(w http.ResponseWriter, status int, code, message string) {
	respond(w, status, map[string]any{"error": map[string]string{"code": code, "message": message}})
}

func (a *api) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	w.Header().Add("Vary", "Origin")
	origin := r.Header.Get("Origin")
	if origin != "" {
		if !a.origins[origin] && !a.origins["*"] {
			fail(w, 403, "origin_not_allowed", "Browser origin is not allowed")
			return
		}
		w.Header().Set("Access-Control-Allow-Origin", origin)
		w.Header().Set("Access-Control-Allow-Methods", "GET, POST, PATCH, OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "Content-Type, Idempotency-Key")
	}
	if r.Method == http.MethodOptions {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	switch r.URL.Path {
	case "/api/events":
		a.events(w, r)
	case "/health":
		if r.Method != "GET" {
			fail(w, 405, "method_not_allowed", "Use GET")
			return
		}
		respond(w, 200, map[string]any{"data": map[string]string{"status": "ok"}})
	case "/api/reports":
		if r.Method != "POST" {
			fail(w, 405, "method_not_allowed", "Use POST")
			return
		}
		a.create(w, r)
	case "/api/issues":
		switch r.Method {
		case "GET":
			a.mu.Lock()
			issues := append([]Issue{}, a.issues...)
			a.mu.Unlock()
			sort.Slice(issues, func(i, j int) bool {
				if issues[i].CreatedAt.Equal(issues[j].CreatedAt) {
					return issues[i].ID < issues[j].ID
				}
				return issues[i].CreatedAt.After(issues[j].CreatedAt)
			})
			respond(w, 200, map[string]any{"data": issues})
		case "POST":
			a.create(w, r)
		default:
			fail(w, 405, "method_not_allowed", "Use GET or POST")
		}
	default:
		if strings.HasPrefix(r.URL.Path, "/api/issues/") {
			a.issue(w, r)
			return
		}
		fail(w, 404, "not_found", "Endpoint not found")
	}
}

func (a *api) issue(w http.ResponseWriter, r *http.Request) {
	if strings.HasSuffix(r.URL.Path, "/snapshot") {
		a.readSnapshot(w, r)
		return
	}
	parts := strings.Split(strings.TrimPrefix(r.URL.Path, "/api/issues/"), "/")
	detail := len(parts) == 1 && parts[0] != ""
	status := len(parts) == 2 && parts[0] != "" && parts[1] == "status"
	content := len(parts) == 2 && parts[0] != "" && parts[1] == "content"
	position := len(parts) == 2 && parts[0] != "" && parts[1] == "position"
	if !detail && !status && !content && !position {
		fail(w, 404, "not_found", "Endpoint not found")
		return
	}
	if (detail && r.Method != "GET") || ((status || content || position) && r.Method != "PATCH") {
		fail(w, 405, "method_not_allowed", "Use GET for details or PATCH for status/content/position")
		return
	}
	var input struct {
		Status string `json:"status"`
	}
	if status {
		decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 64*1024))
		decoder.DisallowUnknownFields()
		if err := decoder.Decode(&input); err != nil {
			fail(w, 400, "invalid_argument", "Body must contain status only")
			return
		}
		if err := decoder.Decode(new(any)); err != io.EOF {
			fail(w, 400, "invalid_argument", "Body must contain one JSON object")
			return
		}
		if input.Status != "open" && input.Status != "in_progress" && input.Status != "resolved" {
			fail(w, 400, "invalid_argument", "status must be open, in_progress or resolved")
			return
		}
	}
	var edit struct {
		Title       *string `json:"title"`
		Description *string `json:"description"`
		Priority    *string `json:"priority"`
	}
	if content {
		decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 64*1024))
		decoder.DisallowUnknownFields()
		if err := decoder.Decode(&edit); err != nil {
			fail(w, 400, "invalid_argument", "Body must contain title, description and priority only")
			return
		}
		if err := decoder.Decode(new(any)); err != io.EOF {
			fail(w, 400, "invalid_argument", "Body must contain one JSON object")
			return
		}
		if edit.Title == nil || strings.TrimSpace(*edit.Title) == "" || edit.Description == nil || edit.Priority == nil {
			fail(w, 400, "invalid_argument", "Nonblank title, description and priority are required")
			return
		}
		if *edit.Priority != "low" && *edit.Priority != "medium" && *edit.Priority != "high" {
			fail(w, 400, "invalid_argument", "priority must be low, medium or high")
			return
		}
	}
	var replacement []byte
	var location struct {
		Snapshot string                      `json:"snapshot"`
		Position *struct{ X, Y, Z *float64 } `json:"position"`
	}
	if position {
		body, readErr := io.ReadAll(http.MaxBytesReader(w, r.Body, 12*1024*1024))
		if readErr != nil {
			fail(w, 400, "invalid_argument", "Request body is too large")
			return
		}
		decoder := json.NewDecoder(bytes.NewReader(body))
		decoder.DisallowUnknownFields()
		if err := decoder.Decode(&location); err != nil {
			fail(w, 400, "invalid_argument", "Body must contain position{x,y,z} only")
			return
		}
		if err := decoder.Decode(new(any)); err != io.EOF {
			fail(w, 400, "invalid_argument", "Body must contain one JSON object")
			return
		}
		if location.Snapshot == "" && len(body) > 64*1024 {
			fail(w, 400, "invalid_argument", "Position request without snapshot must be at most 64 KiB")
			return
		}
		if location.Position == nil || location.Position.X == nil || location.Position.Y == nil || location.Position.Z == nil {
			fail(w, 400, "invalid_argument", "position.x, position.y and position.z are required finite numbers")
			return
		}
		for _, number := range []*float64{location.Position.X, location.Position.Y, location.Position.Z} {
			if math.IsNaN(*number) || math.IsInf(*number, 0) {
				fail(w, 400, "invalid_argument", "position coordinates must be finite numbers")
				return
			}
		}
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	for index, issue := range a.issues {
		if issue.ID != parts[0] {
			continue
		}
		media := a.media
		if position {
			var err error
			replacement, err = validateSnapshot(location.Snapshot, issue.SnapshotURL != "")
			if err != nil {
				fail(w, 400, "invalid_snapshot", err.Error())
				return
			}
			if len(replacement) > 0 {
				media = cloneMedia(a.media)
				media[issue.ID] = replacement
				issue.SnapshotURL = "/api/issues/" + issue.ID + "/snapshot"
			}
		}
		if status || content || position {
			if status {
				issue.Status = input.Status
			} else if position {
				issue.Position = Position{*location.Position.X, *location.Position.Y, *location.Position.Z}
			} else {
				issue.Title = strings.TrimSpace(*edit.Title)
				issue.Description = *edit.Description
				issue.Priority = *edit.Priority
			}
			issue.UpdatedAt = time.Now().UTC()
			next := append([]Issue{}, a.issues...)
			next[index] = issue
			if err := saveIssues(a.path, snapshot{Issues: next, Requests: a.requests, Media: media}); err != nil {
				log.Printf("save issue: %v", err)
				fail(w, 500, "storage_error", "Could not save issue; data was not changed")
				return
			}
			a.issues = next
			a.media = media
			a.notifySaved()
		}
		respond(w, 200, map[string]any{"data": issue})
		return
	}
	fail(w, 404, "not_found", "Issue not found")
}

func (a *api) create(w http.ResponseWriter, r *http.Request) {
	var input struct {
		Snapshot    string                      `json:"snapshot"`
		Title       string                      `json:"title"`
		Description string                      `json:"description"`
		Priority    string                      `json:"priority"`
		Position    *struct{ X, Y, Z *float64 } `json:"position"`
	}
	body, readErr := io.ReadAll(http.MaxBytesReader(w, r.Body, 12*1024*1024))
	if readErr != nil {
		fail(w, 400, "invalid_argument", "Request body is too large")
		return
	}
	decoder := json.NewDecoder(bytes.NewReader(body))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&input); err != nil {
		fail(w, 400, "invalid_argument", "Body must be valid JSON with title, description, priority and position{x,y,z}")
		return
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		fail(w, 400, "invalid_argument", "Body must contain exactly one JSON object")
		return
	}
	if input.Snapshot == "" && len(body) > 64*1024 {
		fail(w, 400, "invalid_argument", "Request without snapshot must be at most 64 KiB")
		return
	}
	input.Title = strings.TrimSpace(input.Title)
	if input.Title == "" {
		fail(w, 400, "invalid_argument", "title is required and must not be blank")
		return
	}
	if input.Priority != "low" && input.Priority != "medium" && input.Priority != "high" {
		fail(w, 400, "invalid_argument", "priority must be low, medium or high")
		return
	}
	if input.Position == nil || input.Position.X == nil || input.Position.Y == nil || input.Position.Z == nil {
		fail(w, 400, "invalid_argument", "position.x, position.y and position.z are required finite numbers")
		return
	}
	for _, number := range []*float64{input.Position.X, input.Position.Y, input.Position.Z} {
		if math.IsNaN(*number) || math.IsInf(*number, 0) {
			fail(w, 400, "invalid_argument", "position coordinates must be finite numbers")
			return
		}
	}
	photo, err := validateSnapshot(input.Snapshot, r.URL.Path == "/api/reports")
	if err != nil {
		fail(w, 400, "invalid_snapshot", err.Error())
		return
	}
	id := make([]byte, 16)
	if _, err := rand.Read(id); err != nil {
		fail(w, 500, "internal_error", "Could not create issue")
		return
	}
	now := time.Now().UTC()
	issue := Issue{ID: hex.EncodeToString(id), Title: input.Title, Description: input.Description, Priority: input.Priority,
		Status: "open", Position: Position{*input.Position.X, *input.Position.Y, *input.Position.Z}, CreatedAt: now, UpdatedAt: now}
	if len(photo) > 0 {
		issue.SnapshotURL = "/api/issues/" + issue.ID + "/snapshot"
	}
	// Signed zero is the same location and must not produce a different replay fingerprint.
	for _, coordinate := range []*float64{&issue.Position.X, &issue.Position.Y, &issue.Position.Z} {
		if *coordinate == 0 {
			*coordinate = 0
		}
	}
	canonical, _ := json.Marshal(struct {
		Title       string
		Description string
		Priority    string
		Position    Position
		Snapshot    string `json:",omitempty"`
	}{issue.Title, issue.Description, issue.Priority, issue.Position, input.Snapshot})
	digest := sha256.Sum256(canonical)
	fingerprint := hex.EncodeToString(digest[:])
	key := r.Header.Get("Idempotency-Key")
	if len(key) > 200 || (key != "" && strings.TrimSpace(key) == "") {
		fail(w, 400, "invalid_argument", "Idempotency-Key must be nonblank and at most 200 bytes")
		return
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if previous, ok := a.requests[key]; key != "" && ok {
		if previous.Fingerprint != fingerprint {
			fail(w, 409, "idempotency_conflict", "This key was already used with different content")
		} else {
			respond(w, 200, map[string]any{"data": previous.Result})
		}
		return
	}
	next := append(append([]Issue{}, a.issues...), issue)
	nextRequests := make(map[string]creation, len(a.requests)+1)
	for k, value := range a.requests {
		nextRequests[k] = value
	}
	if key != "" {
		nextRequests[key] = creation{fingerprint, issue}
	}
	nextMedia := cloneMedia(a.media)
	if len(photo) > 0 {
		nextMedia[issue.ID] = photo
	}
	if err := saveIssues(a.path, snapshot{Issues: next, Requests: nextRequests, Media: nextMedia}); err != nil {
		log.Printf("save issues: %v", err)
		fail(w, 500, "storage_error", "Could not save issue; data was not changed")
		return
	}
	a.issues = next
	a.requests = nextRequests
	a.media = nextMedia
	a.notifySaved()
	respond(w, 201, map[string]any{"data": issue})
}

// A single server process owns this file. Never replace durable data with an incomplete write.
func saveIssues(path string, stored snapshot) error {
	if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		return err
	}
	file, err := os.CreateTemp(filepath.Dir(path), ".issues-*.tmp")
	if err != nil {
		return err
	}
	defer os.Remove(file.Name())
	if err := json.NewEncoder(file).Encode(stored); err != nil {
		file.Close()
		return err
	}
	if err := file.Sync(); err != nil {
		file.Close()
		return err
	}
	if err := file.Close(); err != nil {
		return err
	}
	return os.Rename(file.Name(), path)
}

func env(key, fallback string) string {
	if value := os.Getenv(key); value != "" {
		return value
	}
	return fallback
}

func main() {
	if err := runReceiver(); err != nil {
		log.Fatal(err)
	}
}
