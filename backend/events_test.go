package main

import (
	"bufio"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func openEvents(t *testing.T, base string) (<-chan string, func()) {
	t.Helper()
	client := &http.Client{Timeout: 35 * time.Second}
	res, err := client.Get(base + "/api/events")
	if err != nil {
		t.Fatal(err)
	}
	if res.StatusCode != 200 || !strings.HasPrefix(res.Header.Get("Content-Type"), "text/event-stream") {
		res.Body.Close()
		t.Fatalf("events: %d %v", res.StatusCode, res.Header)
	}
	lines := make(chan string, 32)
	go func() {
		defer close(lines)
		scanner := bufio.NewScanner(res.Body)
		for scanner.Scan() {
			lines <- scanner.Text()
		}
	}()
	return lines, func() { res.Body.Close() }
}

func TestFailedWritesAndCreationReplaysDoNotNotify(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	_, created := request(t, server.URL, "POST", "/api/issues", reportBody, "once")
	var original Issue
	if err := json.Unmarshal(created["data"], &original); err != nil {
		t.Fatal(err)
	}
	lines, closeStream := openEvents(t, server.URL)
	defer closeStream()
	expectEvent(t, lines, "ready")
	request(t, server.URL, "POST", "/api/issues", reportBody, "once")
	request(t, server.URL, "PATCH", "/api/issues/"+original.ID+"/status", `{"status":"invalid"}`)
	request(t, server.URL, "PATCH", "/api/issues/missing/status", `{"status":"resolved"}`)
	if err := os.Rename(path, path+".backup"); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(path, 0700); err != nil {
		t.Fatal(err)
	}
	for suffix, body := range map[string]string{
		"status":   `{"status":"resolved"}`,
		"content":  `{"title":"Unsaved","description":"","priority":"low"}`,
		"position": `{"position":{"x":1,"y":2,"z":3}}`,
	} {
		res, _ := request(t, server.URL, "PATCH", "/api/issues/"+original.ID+"/"+suffix, body)
		if res.StatusCode != 500 {
			t.Fatal(res.StatusCode)
		}
	}
	res, _ := request(t, server.URL, "POST", "/api/issues", reportBody)
	if res.StatusCode != 500 {
		t.Fatal(res.StatusCode)
	}
	_, latest := request(t, server.URL, "GET", "/api/issues/"+original.ID, "")
	if string(latest["data"]) != string(created["data"]) {
		t.Fatal("failed write changed issue")
	}
	deadline := time.After(250 * time.Millisecond)
	for {
		select {
		case line := <-lines:
			if line == "event: issues-changed" {
				t.Fatal("unsaved/replayed mutation notified")
			}
		case <-deadline:
			return
		}
	}
}

func TestStreamsReleaseOnDisconnectAndReconnectQueriesMissedChanges(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	finished := make(chan struct{}, 4)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		handler.ServeHTTP(w, r)
		if r.URL.Path == "/api/events" {
			finished <- struct{}{}
		}
	}))
	defer server.Close()
	for _, disconnect := range []string{"client-exit", "network-loss"} {
		lines, closeStream := openEvents(t, server.URL)
		expectEvent(t, lines, "ready")
		if disconnect == "client-exit" {
			closeStream()
		} else {
			server.CloseClientConnections()
			closeStream()
		}
		select {
		case <-finished:
		case <-time.After(2 * time.Second):
			t.Fatal("handler leaked after " + disconnect)
		}
	}
	_, saved := request(t, server.URL, "POST", "/api/issues", reportBody)
	server.Close()
	reloaded, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	restarted := httptest.NewServer(reloaded)
	defer restarted.Close()
	lines, closeStream := openEvents(t, restarted.URL)
	defer closeStream()
	expectEvent(t, lines, "ready")
	var issue Issue
	if err := json.Unmarshal(saved["data"], &issue); err != nil {
		t.Fatal(err)
	}
	_, latest := request(t, restarted.URL, "GET", "/api/issues/"+issue.ID, "")
	if string(latest["data"]) != string(saved["data"]) {
		t.Fatal("reconnect lost missed change")
	}
}

func expectEvent(t *testing.T, lines <-chan string, name string) {
	t.Helper()
	timer := time.NewTimer(2 * time.Second)
	defer timer.Stop()
	for {
		select {
		case line, ok := <-lines:
			if !ok {
				t.Fatal("stream closed before " + name)
			}
			if line == "event: "+name {
				return
			}
		case <-timer.C:
			t.Fatal("missing event: " + name)
		}
	}
}

func TestSavedChangesNotifyAllClientsAndCanBeQueried(t *testing.T) {
	handler, err := newAPI(filepath.Join(t.TempDir(), "issues.json"), "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	first, closeFirst := openEvents(t, server.URL)
	defer closeFirst()
	second, closeSecond := openEvents(t, server.URL)
	defer closeSecond()
	expectEvent(t, first, "ready")
	expectEvent(t, second, "ready")
	_, result := request(t, server.URL, "POST", "/api/issues", reportBody)
	var issue Issue
	if err := json.Unmarshal(result["data"], &issue); err != nil {
		t.Fatal(err)
	}
	expectEvent(t, first, "issues-changed")
	expectEvent(t, second, "issues-changed")
	for suffix, body := range map[string]string{
		"content":  `{"title":"Updated","description":"Live","priority":"low"}`,
		"position": `{"position":{"x":7,"y":8,"z":9}}`,
		"status":   `{"status":"resolved"}`,
	} {
		res, saved := request(t, server.URL, "PATCH", "/api/issues/"+issue.ID+"/"+suffix, body)
		if res.StatusCode != 200 {
			t.Fatal(res.StatusCode)
		}
		expectEvent(t, first, "issues-changed")
		expectEvent(t, second, "issues-changed")
		_, latest := request(t, server.URL, "GET", "/api/issues/"+issue.ID, "")
		if string(latest["data"]) != string(saved["data"]) {
			t.Fatal("event preceded durable query result")
		}
	}
}

func TestStreamHeartbeatsOutliveOrdinaryWriteTimeout(t *testing.T) {
	handler, err := newAPI(filepath.Join(t.TempDir(), "issues.json"), "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewUnstartedServer(handler)
	server.Config.WriteTimeout = 3 * time.Second
	server.Start()
	defer server.Close()
	lines, closeStream := openEvents(t, server.URL)
	defer closeStream()
	expectEvent(t, lines, "ready")
	deadline := time.After(12 * time.Second)
	for {
		select {
		case line, ok := <-lines:
			if !ok {
				t.Fatal("stream ended before heartbeat")
			}
			if line == ": heartbeat" {
				request(t, server.URL, "POST", "/api/issues", reportBody)
				expectEvent(t, lines, "issues-changed")
				return
			}
		case <-deadline:
			t.Fatal("missing heartbeat")
		}
	}
}

func TestEventsRespectCORSAndMethod(t *testing.T) {
	handler, err := newAPI(filepath.Join(t.TempDir(), "issues.json"), "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	res, _ := request(t, server.URL, "POST", "/api/events", "")
	if res.StatusCode != 405 {
		t.Fatal(res.StatusCode)
	}
	for origin, expected := range map[string]int{"http://localhost:5173": 200, "http://untrusted.example": 403} {
		req, _ := http.NewRequest("GET", server.URL+"/api/events", nil)
		req.Header.Set("Origin", origin)
		client := &http.Client{Timeout: 2 * time.Second}
		res, err := client.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		res.Body.Close()
		if res.StatusCode != expected {
			t.Fatal(res.StatusCode)
		}
		if expected == 200 && res.Header.Get("Access-Control-Allow-Origin") != origin {
			t.Fatal("missing CORS")
		}
	}
}
