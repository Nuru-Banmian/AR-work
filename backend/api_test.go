package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
)

func request(t *testing.T, base, method, path, body string, key ...string) (*http.Response, map[string]json.RawMessage) {
	t.Helper()
	req, err := http.NewRequest(method, base+path, bytes.NewBufferString(body))
	if err != nil {
		t.Fatal(err)
	}
	req.Header.Set("Content-Type", "application/json")
	if len(key) > 0 {
		req.Header.Set("Idempotency-Key", key[0])
	}
	req.Header.Set("Origin", "http://localhost:5173")
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	var result map[string]json.RawMessage
	if err := json.NewDecoder(res.Body).Decode(&result); err != nil {
		t.Fatal(err)
	}
	return res, result
}

func TestConcurrentReportsSurviveRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	var group sync.WaitGroup
	for i := 0; i < 12; i++ {
		group.Add(1)
		go func() {
			defer group.Done()
			res, _ := request(t, server.URL, "POST", "/api/issues", `{"title":"Concurrent report","priority":"medium","position":{"x":-1.5,"y":0,"z":2}}`)
			if res.StatusCode != 201 {
				t.Errorf("create: %d", res.StatusCode)
			}
		}()
	}
	group.Wait()
	server.Close()
	restarted, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	second := httptest.NewServer(restarted)
	defer second.Close()
	_, result := request(t, second.URL, "GET", "/api/issues", "")
	var issues []Issue
	if err := json.Unmarshal(result["data"], &issues); err != nil {
		t.Fatal(err)
	}
	if len(issues) != 12 {
		t.Fatalf("lost concurrent reports: %d", len(issues))
	}
	ids := map[string]bool{}
	for i, issue := range issues {
		if ids[issue.ID] {
			t.Fatal("duplicate ID")
		}
		ids[issue.ID] = true
		if issue.Position != (Position{-1.5, 0, 2}) {
			t.Fatalf("wrong position: %+v", issue.Position)
		}
		if i > 0 && issues[i-1].CreatedAt.Before(issue.CreatedAt) {
			t.Fatal("not newest first")
		}
	}
}

func TestHealthAndBrowserPreflight(t *testing.T) {
	handler, err := newAPI(filepath.Join(t.TempDir(), "issues.json"), "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	res, result := request(t, server.URL, "GET", "/health", "")
	if res.StatusCode != 200 || !strings.Contains(string(result["data"]), `"status":"ok"`) {
		t.Fatalf("unhealthy: %s", result)
	}
	req, _ := http.NewRequest("OPTIONS", server.URL+"/api/issues", nil)
	req.Header.Set("Origin", "http://localhost:5173")
	req.Header.Set("Access-Control-Request-Method", "POST")
	req.Header.Set("Access-Control-Request-Headers", "Content-Type")
	preflight, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	preflight.Body.Close()
	if preflight.StatusCode != 204 || preflight.Header.Get("Access-Control-Allow-Origin") != "http://localhost:5173" || !strings.Contains(preflight.Header.Get("Access-Control-Allow-Methods"), "POST") || !strings.Contains(preflight.Header.Get("Access-Control-Allow-Methods"), "PATCH") || !strings.Contains(preflight.Header.Get("Access-Control-Allow-Headers"), "Content-Type") || !strings.Contains(preflight.Header.Get("Access-Control-Allow-Headers"), "Idempotency-Key") {
		t.Fatalf("preflight rejected: %+v", preflight)
	}
	req.Header.Set("Origin", "http://untrusted.example")
	denied, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer denied.Body.Close()
	if denied.StatusCode != 403 || denied.Header.Get("Access-Control-Allow-Origin") != "" {
		t.Fatal("unexpected browser origin allowed")
	}
}

func TestFailedSaveDoesNotAppearInList(t *testing.T) {
	root := t.TempDir()
	path := filepath.Join(root, "blocked", "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "blocked"), []byte("not a directory"), 0600); err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	res, result := request(t, server.URL, "POST", "/api/issues", `{"title":"Leak","priority":"low","position":{"x":0,"y":0,"z":0}}`)
	if res.StatusCode != 500 || !strings.Contains(string(result["error"]), "storage_error") {
		t.Fatalf("save failure hidden: %d %s", res.StatusCode, result)
	}
	_, result = request(t, server.URL, "GET", "/api/issues", "")
	if string(result["data"]) != "[]" {
		t.Fatal("failed save appears as success")
	}
}

func TestCreateListAndRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	res, result := request(t, server.URL, "POST", "/api/issues", `{"title":"  Pipe leak  ","description":"Near entrance","priority":"high","position":{"x":0,"y":0,"z":0}}`)
	if res.StatusCode != 201 {
		t.Fatalf("status %d: %s", res.StatusCode, result)
	}
	var created Issue
	if err := json.Unmarshal(result["data"], &created); err != nil {
		t.Fatal(err)
	}
	if created.ID == "" || created.Title != "Pipe leak" || created.Status != "open" || created.Priority != "high" || created.CreatedAt.IsZero() || !created.CreatedAt.Equal(created.UpdatedAt) {
		t.Fatalf("unexpected issue: %+v", created)
	}
	if res.Header.Get("Access-Control-Allow-Origin") != "http://localhost:5173" {
		t.Fatal("missing CORS")
	}
	server.Close()
	restarted, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	second := httptest.NewServer(restarted)
	defer second.Close()
	res, result = request(t, second.URL, "GET", "/api/issues", "")
	var listed []Issue
	if err := json.Unmarshal(result["data"], &listed); err != nil {
		t.Fatal(err)
	}
	if res.StatusCode != 200 || len(listed) != 1 || listed[0] != created {
		t.Fatalf("restart lost issue: %s", result)
	}
}

func TestInvalidReportsDoNotCreateIssues(t *testing.T) {
	handler, err := newAPI(filepath.Join(t.TempDir(), "issues.json"), "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	for _, body := range []string{
		`{"title":" ","priority":"low","position":{"x":0,"y":0,"z":0}}`,
		`{"title":"Leak","priority":"urgent","position":{"x":0,"y":0,"z":0}}`,
		`{"title":"Leak","priority":"low"}`,
		`{"title":"Leak","priority":"low","position":{"x":0,"y":0}}`,
		`{"title":"Leak","priority":"low","position":{"x":null,"y":0,"z":0}}`,
		`{"title":"Leak","priority":"low","position":{"x":"zero","y":0,"z":0}}`,
		`{"title":"Leak","priority":"low","position":{"x":1e999,"y":0,"z":0}}`,
		`{"title":"Leak","priority":"low","position":{"x":0,"y":0,"z":0},"status":"bad"}`,
		`{"title":"Leak","priority":"low","position":{"x":0,"y":0,"z":0}} {}`,
	} {
		t.Run(body, func(t *testing.T) {
			res, result := request(t, server.URL, "POST", "/api/issues", body)
			var failure struct{ Code, Message string }
			if err := json.Unmarshal(result["error"], &failure); err != nil {
				t.Fatal(err)
			}
			if res.StatusCode != 400 || failure.Code != "invalid_argument" || failure.Message == "" {
				t.Fatalf("not a readable argument error: %d %s", res.StatusCode, result)
			}
		})
	}
	_, result := request(t, server.URL, "GET", "/api/issues", "")
	if string(result["data"]) != "[]" {
		t.Fatalf("invalid reports were stored: %s", result)
	}
}
