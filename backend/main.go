package main

import (
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
	mu       sync.Mutex
	path     string
	issues   []Issue
	origins  map[string]bool
	requests map[string]creation
}

// The original result is retained even when the issue's status changes later.
type creation struct {
	Fingerprint string `json:"fingerprint"`
	Result      Issue  `json:"result"`
}

type snapshot struct {
	Issues   []Issue             `json:"issues"`
	Requests map[string]creation `json:"requests"`
}

func newAPI(path, allowedOrigins string) (http.Handler, error) {
	a := &api{path: path, issues: []Issue{}, origins: map[string]bool{}, requests: map[string]creation{}}
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
	case "/health":
		if r.Method != "GET" {
			fail(w, 405, "method_not_allowed", "Use GET")
			return
		}
		respond(w, 200, map[string]any{"data": map[string]string{"status": "ok"}})
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
	parts := strings.Split(strings.TrimPrefix(r.URL.Path, "/api/issues/"), "/")
	detail := len(parts) == 1 && parts[0] != ""
	status := len(parts) == 2 && parts[0] != "" && parts[1] == "status"
	if !detail && !status {
		fail(w, 404, "not_found", "Endpoint not found")
		return
	}
	if (detail && r.Method != "GET") || (status && r.Method != "PATCH") {
		fail(w, 405, "method_not_allowed", "Use GET for details or PATCH for status")
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
	a.mu.Lock()
	defer a.mu.Unlock()
	for index, issue := range a.issues {
		if issue.ID != parts[0] {
			continue
		}
		if status {
			issue.Status = input.Status
			issue.UpdatedAt = time.Now().UTC()
			next := append([]Issue{}, a.issues...)
			next[index] = issue
			if err := saveIssues(a.path, snapshot{next, a.requests}); err != nil {
				log.Printf("save status: %v", err)
				fail(w, 500, "storage_error", "Could not save status; data was not changed")
				return
			}
			a.issues = next
		}
		respond(w, 200, map[string]any{"data": issue})
		return
	}
	fail(w, 404, "not_found", "Issue not found")
}

func (a *api) create(w http.ResponseWriter, r *http.Request) {
	var input struct {
		Title       string                      `json:"title"`
		Description string                      `json:"description"`
		Priority    string                      `json:"priority"`
		Position    *struct{ X, Y, Z *float64 } `json:"position"`
	}
	decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 64*1024))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&input); err != nil {
		fail(w, 400, "invalid_argument", "Body must be valid JSON with title, description, priority and position{x,y,z}")
		return
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		fail(w, 400, "invalid_argument", "Body must contain exactly one JSON object")
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
	id := make([]byte, 16)
	if _, err := rand.Read(id); err != nil {
		fail(w, 500, "internal_error", "Could not create issue")
		return
	}
	now := time.Now().UTC()
	issue := Issue{ID: hex.EncodeToString(id), Title: input.Title, Description: input.Description, Priority: input.Priority,
		Status: "open", Position: Position{*input.Position.X, *input.Position.Y, *input.Position.Z}, CreatedAt: now, UpdatedAt: now}
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
	}{issue.Title, issue.Description, issue.Priority, issue.Position})
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
	if err := saveIssues(a.path, snapshot{next, nextRequests}); err != nil {
		log.Printf("save issues: %v", err)
		fail(w, 500, "storage_error", "Could not save issue; data was not changed")
		return
	}
	a.issues = next
	a.requests = nextRequests
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
	handler, err := newAPI(env("ARWORK_DATA", "data/issues.json"), env("ARWORK_ORIGINS", "http://localhost:5173,http://127.0.0.1:5173"))
	if err != nil {
		log.Fatalf("Cannot open issue data: %v", err)
	}
	server := &http.Server{Addr: env("ARWORK_ADDR", ":8080"), Handler: handler, ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 20 * time.Second, WriteTimeout: 20 * time.Second, IdleTimeout: 60 * time.Second}
	log.Printf("AR Work listening on %s", server.Addr)
	log.Fatal(server.ListenAndServe())
}
