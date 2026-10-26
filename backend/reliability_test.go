package main

import (
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
)

func TestFailedMutationsKeepIssueAndKeyUnchanged(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	_, created := request(t, server.URL, "POST", "/api/issues", reportBody, "original")
	var original Issue
	json.Unmarshal(created["data"], &original)
	if err := os.Rename(path, path+".backup"); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(path, 0700); err != nil {
		t.Fatal(err)
	}
	res, _ := request(t, server.URL, "PATCH", "/api/issues/"+original.ID+"/status", `{"status":"resolved"}`)
	if res.StatusCode != 500 {
		t.Fatalf("status failure hidden: %d", res.StatusCode)
	}
	_, result := request(t, server.URL, "GET", "/api/issues/"+original.ID, "")
	if string(result["data"]) != string(created["data"]) {
		t.Fatalf("failed mutation changed issue: %s", result)
	}
	res, _ = request(t, server.URL, "POST", "/api/issues", reportBody, "failed-key")
	if res.StatusCode != 500 {
		t.Fatalf("creation failure hidden: %d", res.StatusCode)
	}
	if err := os.Remove(path); err != nil {
		t.Fatal(err)
	}
	if err := os.Rename(path+".backup", path); err != nil {
		t.Fatal(err)
	}
	res, _ = request(t, server.URL, "POST", "/api/issues", reportBody, "failed-key")
	if res.StatusCode != 201 {
		t.Fatalf("failed creation reserved key: %d", res.StatusCode)
	}
}

func TestLegacyDataAndNormalizedReplay(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	legacy := `[{"id":"legacy","title":"Old issue","priority":"low","status":"resolved","position":{"x":0,"y":0,"z":0},"createdAt":"2026-10-01T00:00:00Z","updatedAt":"2026-10-01T00:00:00Z"}]`
	if err := os.WriteFile(path, []byte(legacy), 0600); err != nil {
		t.Fatal(err)
	}
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	_, created := request(t, server.URL, "POST", "/api/issues", reportBody, "normalized")
	res, replay := request(t, server.URL, "POST", "/api/issues", `{"position":{"z":2.0,"y":1,"x":-0.0},"priority":"high","description":"Entrance","title":"Wall crack"}`, "normalized")
	if res.StatusCode != 200 || string(replay["data"]) != string(created["data"]) {
		t.Fatalf("normalized request did not replay: %d %s", res.StatusCode, replay)
	}
	server.Close()
	handler, err = newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server = httptest.NewServer(handler)
	defer server.Close()
	res, result := request(t, server.URL, "GET", "/api/issues/legacy", "")
	var issue Issue
	json.Unmarshal(result["data"], &issue)
	if res.StatusCode != 200 || issue.Title != "Old issue" || issue.Status != "resolved" {
		t.Fatalf("legacy issue lost: %s", result)
	}
}

const reportBody = `{"title":" Wall crack ","description":"Entrance","priority":"high","position":{"x":0,"y":1,"z":2}}`

func TestLostResponseRetryAfterRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	// Forward the real request, let storage finish, then close the connection without a response.
	drop := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		forward, _ := http.NewRequest("POST", server.URL+"/api/issues", r.Body)
		forward.Header = r.Header.Clone()
		res, err := http.DefaultClient.Do(forward)
		if err != nil {
			t.Error(err)
			return
		}
		io.Copy(io.Discard, res.Body)
		res.Body.Close()
		if res.StatusCode != 201 {
			t.Errorf("forward create: %d", res.StatusCode)
		}
		conn, _, err := w.(http.Hijacker).Hijack()
		if err != nil {
			t.Error(err)
			return
		}
		conn.Close()
	}))
	defer drop.Close()
	lost, _ := http.NewRequest("POST", drop.URL, strings.NewReader(reportBody))
	lost.Header.Set("Idempotency-Key", "lost-response")
	response, err := http.DefaultClient.Do(lost)
	if err == nil {
		response.Body.Close()
		t.Fatal("expected lost response")
	}
	_, listedBefore := request(t, server.URL, "GET", "/api/issues", "")
	var before []Issue
	json.Unmarshal(listedBefore["data"], &before)
	if len(before) != 1 {
		t.Fatal("forwarded request was not saved")
	}
	server.Close()
	handler, err = newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server = httptest.NewServer(handler)
	defer server.Close()
	res, replay := request(t, server.URL, "POST", "/api/issues", reportBody, "lost-response")
	var replayed Issue
	json.Unmarshal(replay["data"], &replayed)
	if res.StatusCode != 200 || before[0] != replayed {
		t.Fatalf("replay changed result: %d %s", res.StatusCode, replay)
	}
	res, _ = request(t, server.URL, "POST", "/api/issues", `{"title":"Different","description":"Entrance","priority":"high","position":{"x":0,"y":1,"z":2}}`, "lost-response")
	if res.StatusCode != 409 {
		t.Fatalf("different content must conflict: %d", res.StatusCode)
	}
	_, listed := request(t, server.URL, "GET", "/api/issues", "")
	var issues []Issue
	json.Unmarshal(listed["data"], &issues)
	if len(issues) != 1 {
		t.Fatalf("retry duplicated report: %s", listed)
	}
}

func TestConcurrentSameKeyCreatesOneIssue(t *testing.T) {
	handler, err := newAPI(filepath.Join(t.TempDir(), "issues.json"), "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	var group sync.WaitGroup
	ids := make(chan string, 16)
	for i := 0; i < 16; i++ {
		group.Add(1)
		go func() {
			defer group.Done()
			res, result := request(t, server.URL, "POST", "/api/issues", reportBody, "concurrent")
			if res.StatusCode != 201 && res.StatusCode != 200 {
				t.Errorf("create: %d", res.StatusCode)
				return
			}
			var issue Issue
			json.Unmarshal(result["data"], &issue)
			ids <- issue.ID
		}()
	}
	group.Wait()
	close(ids)
	first := ""
	for id := range ids {
		if first == "" {
			first = id
		}
		if id != first {
			t.Fatal("concurrent duplicate")
		}
	}
	_, result := request(t, server.URL, "GET", "/api/issues", "")
	var issues []Issue
	json.Unmarshal(result["data"], &issues)
	if len(issues) != 1 {
		t.Fatalf("expected one issue: %s", result)
	}
}

func TestStatusTransitionsAndDetailsSurviveRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	_, result := request(t, server.URL, "POST", "/api/issues", reportBody, "status-report")
	var original Issue
	json.Unmarshal(result["data"], &original)
	for _, status := range []string{"resolved", "open", "in_progress", "resolved", "in_progress"} {
		res, changed := request(t, server.URL, "PATCH", "/api/issues/"+original.ID+"/status", `{"status":"`+status+`"}`)
		var issue Issue
		json.Unmarshal(changed["data"], &issue)
		if res.StatusCode != 200 || issue.Status != status || issue.ID != original.ID || issue.Title != original.Title || issue.Position != original.Position || issue.CreatedAt != original.CreatedAt || issue.UpdatedAt.Before(original.UpdatedAt) {
			t.Fatalf("transition: %d %s", res.StatusCode, changed)
		}
	}
	for _, body := range []string{`{"status":"bad"}`, `{"status":null}`, `{"status":"open","title":"overwrite"}`, `{"status":"open"} {}`} {
		res, _ := request(t, server.URL, "PATCH", "/api/issues/"+original.ID+"/status", body)
		if res.StatusCode != 400 {
			t.Fatalf("invalid status: %d", res.StatusCode)
		}
	}
	res, _ := request(t, server.URL, "PATCH", "/api/issues/missing/status", `{"status":"open"}`)
	if res.StatusCode != 404 {
		t.Fatalf("unknown issue: %d", res.StatusCode)
	}
	server.Close()
	handler, err = newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server = httptest.NewServer(handler)
	defer server.Close()
	res, result = request(t, server.URL, "GET", "/api/issues/"+original.ID, "")
	var issue Issue
	json.Unmarshal(result["data"], &issue)
	if res.StatusCode != 200 || issue.Status != "in_progress" {
		t.Fatalf("restart lost status: %d %s", res.StatusCode, result)
	}
	res, result = request(t, server.URL, "POST", "/api/issues", reportBody, "status-report")
	var replay Issue
	json.Unmarshal(result["data"], &replay)
	if res.StatusCode != 200 || replay != original {
		t.Fatalf("replay must retain original receipt: %d %s", res.StatusCode, result)
	}
}
