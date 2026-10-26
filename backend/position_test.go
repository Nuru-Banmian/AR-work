package main

import (
	"encoding/json"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
)

func TestRemarkPreservesIssueAndSurvivesRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	_, created := request(t, server.URL, "POST", "/api/issues", reportBody, "remark")
	var original Issue
	if err := json.Unmarshal(created["data"], &original); err != nil {
		t.Fatal(err)
	}
	endpoint := "/api/issues/" + original.ID
	request(t, server.URL, "PATCH", endpoint+"/status", `{"status":"resolved"}`)
	res, result := request(t, server.URL, "PATCH", endpoint+"/position", `{"position":{"x":0,"y":-2.5,"z":3}}`)
	if res.StatusCode != 200 {
		t.Fatalf("remark: %d %s", res.StatusCode, result)
	}
	var changed Issue
	if err := json.Unmarshal(result["data"], &changed); err != nil {
		t.Fatal(err)
	}
	if changed.ID != original.ID || changed.CreatedAt != original.CreatedAt || changed.Title != original.Title || changed.Description != original.Description || changed.Priority != original.Priority || changed.Status != "resolved" || changed.Position != (Position{0, -2.5, 3}) || !changed.UpdatedAt.After(original.UpdatedAt) {
		t.Fatalf("remark overwrote issue: %+v", changed)
	}
	server.Close()
	handler, err = newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	restarted := httptest.NewServer(handler)
	defer restarted.Close()
	_, detail := request(t, restarted.URL, "GET", endpoint, "")
	if string(detail["data"]) != string(result["data"]) {
		t.Fatal("restart lost remark")
	}
	_, replay := request(t, restarted.URL, "POST", "/api/issues", reportBody, "remark")
	if string(replay["data"]) != string(created["data"]) {
		t.Fatal("remark changed creation receipt")
	}
}

func TestRemarkErrorsKeepOriginalAndAllowRetry(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	_, created := request(t, server.URL, "POST", "/api/issues", reportBody)
	var original Issue
	if err := json.Unmarshal(created["data"], &original); err != nil {
		t.Fatal(err)
	}
	endpoint := "/api/issues/" + original.ID
	valid := `{"position":{"x":4,"y":0,"z":-8}}`
	for _, body := range []string{
		`{}`, `null`, `{`, valid + ` {}`, `{"position":null}`, `{"position":{"x":0,"y":0}}`,
		`{"position":{"x":null,"y":0,"z":0}}`, `{"position":{"x":0,"y":null,"z":0}}`, `{"position":{"x":0,"y":0,"z":null}}`,
		`{"position":{"x":"1","y":0,"z":0}}`, `{"position":{"x":1e999,"y":0,"z":0}}`,
		`{"position":{"x":NaN,"y":0,"z":0}}`, `{"position":{"x":0,"y":0,"z":0,"extra":1}}`,
		strings.TrimSuffix(valid, "}") + `,"status":"open"}`, strings.TrimSuffix(valid, "}") + `,"title":"changed"}`,
		strings.TrimSuffix(valid, "}") + `,"id":"new"}`, strings.Repeat(" ", 65536) + valid,
	} {
		res, result := request(t, server.URL, "PATCH", endpoint+"/position", body)
		if res.StatusCode != 400 || !strings.Contains(string(result["error"]), "invalid_argument") {
			t.Fatalf("accepted %q: %d %s", body, res.StatusCode, result)
		}
	}
	res, _ := request(t, server.URL, "PATCH", "/api/issues/missing/position", valid)
	if res.StatusCode != 404 {
		t.Fatal(res.StatusCode)
	}
	res, _ = request(t, server.URL, "POST", endpoint+"/position", valid)
	if res.StatusCode != 405 {
		t.Fatal(res.StatusCode)
	}
	if err := os.Rename(path, path+".backup"); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(path, 0700); err != nil {
		t.Fatal(err)
	}
	res, result := request(t, server.URL, "PATCH", endpoint+"/position", valid)
	if res.StatusCode != 500 || !strings.Contains(string(result["error"]), "storage_error") {
		t.Fatalf("storage: %d %s", res.StatusCode, result)
	}
	_, detail := request(t, server.URL, "GET", endpoint, "")
	if string(detail["data"]) != string(created["data"]) {
		t.Fatal("failed remark changed original")
	}
	if err := os.Remove(path); err != nil {
		t.Fatal(err)
	}
	if err := os.Rename(path+".backup", path); err != nil {
		t.Fatal(err)
	}
	res, _ = request(t, server.URL, "PATCH", endpoint+"/position", valid)
	if res.StatusCode != 200 {
		t.Fatal(res.StatusCode)
	}
}

func TestConcurrentRemarkAndManagerStatusKeepBothChanges(t *testing.T) {
	handler, err := newAPI(filepath.Join(t.TempDir(), "issues.json"), "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	_, created := request(t, server.URL, "POST", "/api/issues", reportBody)
	var original Issue
	if err := json.Unmarshal(created["data"], &original); err != nil {
		t.Fatal(err)
	}
	endpoint := "/api/issues/" + original.ID
	for i := 0; i < 10; i++ {
		request(t, server.URL, "PATCH", endpoint+"/status", `{"status":"open"}`)
		request(t, server.URL, "PATCH", endpoint+"/position", `{"position":{"x":0,"y":0,"z":0}}`)
		start := make(chan struct{})
		var wg sync.WaitGroup
		for suffix, body := range map[string]string{"/status": `{"status":"in_progress"}`, "/position": `{"position":{"x":7,"y":8,"z":9}}`, "/content": `{"title":"Concurrent edit","description":"Kept","priority":"low"}`} {
			wg.Add(1)
			go func(suffix, body string) {
				defer wg.Done()
				<-start
				res, _ := request(t, server.URL, "PATCH", endpoint+suffix, body)
				if res.StatusCode != 200 {
					t.Errorf("%s: %d", suffix, res.StatusCode)
				}
			}(suffix, body)
		}
		close(start)
		wg.Wait()
		_, detail := request(t, server.URL, "GET", endpoint, "")
		var issue Issue
		if err := json.Unmarshal(detail["data"], &issue); err != nil {
			t.Fatal(err)
		}
		if issue.Status != "in_progress" || issue.Position != (Position{7, 8, 9}) || issue.Title != "Concurrent edit" || issue.Description != "Kept" || issue.Priority != "low" {
			t.Fatalf("lost concurrent change: %+v", issue)
		}
	}
}
