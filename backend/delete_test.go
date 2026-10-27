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

func TestDeleteRetiresCompleteSceneAndReplayAfterRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	h, _ := newAPI(path, "*")
	s := httptest.NewServer(h)
	photo, _ := photoBody(t)
	body := strings.TrimSuffix(photo, "}") + `,"reportVersion":2,"model":` + modelJSON + `}`
	_, created := request(t, s.URL, "POST", "/api/reports", body, "create-deleted")
	var issue Issue
	json.Unmarshal(created["data"], &issue)
	endpoint := "/api/issues/" + issue.ID
	remark := `{"reportVersion":2,"position":{"x":7,"y":8,"z":9},"snapshot":` + snapshotField(t, photo) + `,"model":` + modelJSON + `}`
	request(t, s.URL, "PATCH", endpoint+"/position", remark, "remark-deleted")
	res, _ := request(t, s.URL, "DELETE", endpoint, `{"title":`+mustJSON(issue.Title)+`}`)
	if res.StatusCode != 200 {
		t.Fatalf("delete: %d", res.StatusCode)
	}
	s.Close()
	h, err := newAPI(path, "*")
	if err != nil {
		t.Fatal(err)
	}
	s = httptest.NewServer(h)
	defer s.Close()
	for _, suffix := range []string{"", "/snapshot", "/model"} {
		res, _ = request(t, s.URL, "GET", endpoint+suffix, "")
		if res.StatusCode != 410 {
			t.Fatalf("deleted %s: %d", suffix, res.StatusCode)
		}
	}
	for _, retry := range []struct{ method, path, body, key string }{
		{"POST", "/api/reports", body, "create-deleted"},
		{"PATCH", endpoint + "/position", remark, "remark-deleted"},
		{"PATCH", endpoint + "/position", remark, "new-remark"},
		{"PATCH", endpoint + "/status", `{"status":"resolved"}`, ""},
	} {
		res, _ = request(t, s.URL, retry.method, retry.path, retry.body, retry.key)
		if res.StatusCode != 410 {
			t.Fatalf("replay %s: %d", retry.path, res.StatusCode)
		}
	}
	_, list := request(t, s.URL, "GET", "/api/issues", "")
	if !strings.Contains(string(list["deletedIds"]), issue.ID) {
		t.Fatal("full query omitted authoritative deleted identity")
	}
	if string(list["data"]) != "[]" {
		t.Fatal("deleted issue returned")
	}
	// A later unrelated mutation must preserve tombstones while keeping other issues usable.
	_, other := request(t, s.URL, "POST", "/api/reports", body, "unrelated-create")
	var kept Issue
	json.Unmarshal(other["data"], &kept)
	if kept.ID == issue.ID || kept.ModelURL == "" {
		t.Fatal("unrelated creation lost")
	}
	res, _ = request(t, s.URL, "POST", "/api/reports", body, "create-deleted")
	if res.StatusCode != 410 {
		t.Fatal("unrelated mutation dropped tombstone")
	}
	request(t, s.URL, "DELETE", "/api/issues/"+kept.ID, `{"title":`+mustJSON(kept.Title)+`}`)
	data, _ := os.ReadFile(path)
	for _, forbidden := range []string{"snapshotUrl", "modelUrl", `"vertices"`, snapshotField(t, photo), issue.Title} {
		if strings.Contains(string(data), forbidden) {
			t.Fatalf("retained deleted data: %s", forbidden)
		}
	}
}

func mustJSON(value string) string { data, _ := json.Marshal(value); return string(data) }

func TestDeleteFailureAndConcurrentReplay(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	h, _ := newAPI(path, "*")
	s := httptest.NewServer(h)
	defer s.Close()
	photo, _ := photoBody(t)
	body := strings.TrimSuffix(photo, "}") + `,"reportVersion":2,"model":` + modelJSON + `}`
	_, created := request(t, s.URL, "POST", "/api/reports", body, "delete-race")
	var issue Issue
	json.Unmarshal(created["data"], &issue)
	endpoint := "/api/issues/" + issue.ID
	res, _ := request(t, s.URL, "DELETE", endpoint, `{"title":"wrong"}`)
	if res.StatusCode != 409 {
		t.Fatal("missing title confirmation")
	}
	os.Rename(path, path+".original")
	os.Mkdir(path, 0700)
	res, _ = request(t, s.URL, "DELETE", endpoint, `{"title":`+mustJSON(issue.Title)+`}`)
	if res.StatusCode != 500 {
		t.Fatal("delete failure acknowledged")
	}
	_, detail := request(t, s.URL, "GET", endpoint, "")
	if string(detail["data"]) != string(created["data"]) {
		t.Fatal("failed delete changed issue")
	}
	for _, url := range []string{issue.ModelURL, issue.SnapshotURL} {
		response, err := s.Client().Get(s.URL + url)
		if err != nil {
			t.Fatal(err)
		}
		response.Body.Close()
		if response.StatusCode != 200 {
			t.Fatal("failed delete lost attachment")
		}
	}
	os.Remove(path)
	os.Rename(path+".original", path)
	old, _ := os.ReadFile(path)
	os.WriteFile(filepath.Join(filepath.Dir(path), ".issues-12345.tmp"), old, 0600)
	var wg sync.WaitGroup
	for i := 0; i < 12; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			method, url, b, key := "DELETE", endpoint, `{"title":`+mustJSON(issue.Title)+`}`, ""
			if i%4 == 1 {
				method, url, b, key = "POST", "/api/reports", body, "delete-race"
			}
			if i%4 == 2 {
				method, url, b = "PATCH", endpoint+"/status", `{"status":"resolved"}`
			}
			if i%4 == 3 {
				method, url, b, key = "PATCH", endpoint+"/position", `{"reportVersion":2,"position":{"x":7,"y":8,"z":9},"snapshot":`+snapshotField(t, photo)+`,"model":`+modelJSON+`}`, "race-remark"
			}
			response, _ := request(t, s.URL, method, url, b, key)
			if response.StatusCode != 200 && response.StatusCode != 410 {
				t.Errorf("race %d", response.StatusCode)
			}
		}(i)
	}
	wg.Wait()
	if _, err := os.Stat(filepath.Join(filepath.Dir(path), ".issues-12345.tmp")); !os.IsNotExist(err) {
		t.Fatal("old temporary retained")
	}
	// Even a legacy crash temporary appearing beside a deleted-only dataset is recognized on restart.
	os.WriteFile(filepath.Join(filepath.Dir(path), ".issues-67890.tmp"), old, 0600)
	s.Close()
	if _, err := newAPI(path, "*"); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(filepath.Dir(path), ".issues-67890.tmp")); !os.IsNotExist(err) {
		t.Fatal("deleted-only restart retained legacy attachment temporary")
	}
}
