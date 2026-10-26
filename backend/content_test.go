package main

import (
	"encoding/json"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestContentEditPreservesManagerStatusAndSurvivesRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	_, created := request(t, server.URL, "POST", "/api/issues", reportBody, "content-edit")
	var original Issue
	if err := json.Unmarshal(created["data"], &original); err != nil {
		t.Fatal(err)
	}
	// The inspector opened the issue before the manager changed its status.
	request(t, server.URL, "GET", "/api/issues/"+original.ID, "")
	res, _ := request(t, server.URL, "PATCH", "/api/issues/"+original.ID+"/status", `{"status":"resolved"}`)
	if res.StatusCode != 200 {
		t.Fatal(res.StatusCode)
	}
	res, result := request(t, server.URL, "PATCH", "/api/issues/"+original.ID+"/content", `{"title":"  Repaired pipe  ","description":"Checked by inspector","priority":"low"}`)
	if res.StatusCode != 200 {
		t.Fatalf("edit: %d %s", res.StatusCode, result)
	}
	var changed Issue
	if err := json.Unmarshal(result["data"], &changed); err != nil {
		t.Fatal(err)
	}
	if changed.Title != "Repaired pipe" || changed.Description != "Checked by inspector" || changed.Priority != "low" || changed.Status != "resolved" || changed.ID != original.ID || changed.Position != original.Position || changed.CreatedAt != original.CreatedAt || !changed.UpdatedAt.After(original.UpdatedAt) {
		t.Fatalf("content edit overwrote other fields: %+v", changed)
	}
	server.Close()
	handler, err = newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	server = httptest.NewServer(handler)
	defer server.Close()
	_, detail := request(t, server.URL, "GET", "/api/issues/"+original.ID, "")
	if string(detail["data"]) != string(result["data"]) {
		t.Fatal("restart lost content")
	}
	_, list := request(t, server.URL, "GET", "/api/issues", "")
	var issues []Issue
	if err := json.Unmarshal(list["data"], &issues); err != nil {
		t.Fatal(err)
	}
	if len(issues) != 1 || issues[0] != changed {
		t.Fatalf("list not current: %s", list)
	}
	_, replay := request(t, server.URL, "POST", "/api/issues", reportBody, "content-edit")
	if string(replay["data"]) != string(created["data"]) {
		t.Fatal("edit changed original creation receipt")
	}
}

func TestContentErrorsKeepOriginalAndAllowRetry(t *testing.T) {
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
	endpoint := "/api/issues/" + original.ID + "/content"
	valid := `{"title":"Updated","description":"","priority":"medium"}`
	for _, body := range []string{
		`{"title":" ","description":"","priority":"low"}`,
		`{"title":"Updated","description":"","priority":"urgent"}`,
		`{"title":"Updated","priority":"low"}`,
		`{"title":null,"description":"","priority":"low"}`,
		`{"title":"Updated","description":null,"priority":"low"}`,
		`{"title":"Updated","description":"","priority":null}`,
		`null`, `{`, valid + ` {}`, strings.Replace(valid, `"medium"`, `"medium","status":"open"`, 1),
		strings.Replace(valid, `"medium"`, `"medium","position":{"x":9,"y":9,"z":9}`, 1),
		strings.Replace(valid, `"Updated"`, `"`+strings.Repeat("x", 65536)+`"`, 1),
	} {
		res, result := request(t, server.URL, "PATCH", endpoint, body)
		if res.StatusCode != 400 || !strings.Contains(string(result["error"]), "invalid_argument") {
			t.Fatalf("invalid edit accepted: %d %s", res.StatusCode, result)
		}
	}
	res, result := request(t, server.URL, "PATCH", "/api/issues/missing/content", valid)
	if res.StatusCode != 404 || !strings.Contains(string(result["error"]), "not_found") {
		t.Fatalf("unknown ID: %d %s", res.StatusCode, result)
	}
	if err := os.Rename(path, path+".backup"); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(path, 0700); err != nil {
		t.Fatal(err)
	}
	res, result = request(t, server.URL, "PATCH", endpoint, valid)
	if res.StatusCode != 500 || !strings.Contains(string(result["error"]), "storage_error") {
		t.Fatalf("storage failure: %d %s", res.StatusCode, result)
	}
	_, detail := request(t, server.URL, "GET", "/api/issues/"+original.ID, "")
	if string(detail["data"]) != string(created["data"]) {
		t.Fatal("failed edit changed issue")
	}
	if err := os.Remove(path); err != nil {
		t.Fatal(err)
	}
	if err := os.Rename(path+".backup", path); err != nil {
		t.Fatal(err)
	}
	res, result = request(t, server.URL, "PATCH", endpoint, valid)
	if res.StatusCode != 200 {
		t.Fatalf("retry failed: %d %s", res.StatusCode, result)
	}
}
