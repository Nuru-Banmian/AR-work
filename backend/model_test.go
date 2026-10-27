package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"image"
	"image/png"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
)

const modelJSON = `{"version":1,"vertices":[0,0,0,1,0,0,0,1,0],"colors":[255,0,0,0,255,0,0,0,255],"triangles":[0,1,2],"marker":{"position":{"x":0.25,"y":0.25,"z":0},"normal":{"x":0,"y":0,"z":1}}}`

func TestModelCompleteRestartReplay(t *testing.T) {
	path := filepath.Join(t.TempDir(), "data.json")
	h, _ := newAPI(path, "*")
	s := httptest.NewServer(h)
	photo, _ := photoBody(t)
	body := strings.TrimSuffix(photo, "}") + `,"reportVersion":2,"model":` + modelJSON + `}`
	res, result := request(t, s.URL, "POST", "/api/reports", body, "model-key")
	if res.StatusCode != 201 {
		t.Fatalf("%d %s", res.StatusCode, result)
	}
	var issue struct {
		ID       string `json:"id"`
		ModelURL string `json:"modelUrl"`
	}
	json.Unmarshal(result["data"], &issue)
	if issue.ModelURL != "/api/issues/"+issue.ID+"/model" {
		t.Fatal("no model association")
	}
	s.Close()
	h, _ = newAPI(path, "*")
	s = httptest.NewServer(h)
	defer s.Close()
	res, replay := request(t, s.URL, "POST", "/api/reports", body, "model-key")
	if res.StatusCode != 200 || string(replay["data"]) != string(result["data"]) {
		t.Fatal("lost replay")
	}
	response, getErr := http.Get(s.URL + issue.ModelURL)
	if getErr != nil {
		t.Fatal(getErr)
	}
	defer response.Body.Close()
	data, _ := io.ReadAll(response.Body)
	if response.StatusCode != 200 || !strings.Contains(string(data), `"triangles":[0,1,2]`) {
		t.Fatalf("lost model: %s", data)
	}
	changed := strings.Replace(body, "0.25", "0.3", 1)
	res, _ = request(t, s.URL, "POST", "/api/reports", changed, "model-key")
	if res.StatusCode != 409 {
		t.Fatal("model not fingerprinted")
	}
}
func TestInvalidModelsRemainInvisible(t *testing.T) {
	h, _ := newAPI(filepath.Join(t.TempDir(), "issues.json"), "*")
	s := httptest.NewServer(h)
	defer s.Close()
	photo, _ := photoBody(t)
	invalid := []string{`null`, strings.Replace(modelJSON, `[0,1,2]`, `[0,1,3]`, 1), strings.Replace(modelJSON, `"version":1`, `"version":3`, 1), strings.Replace(modelJSON, `"z":1`, `"z":0`, 1), strings.Replace(modelJSON, `0.25`, `9`, 1), strings.Replace(modelJSON, `255,0,0,0,255,0,0,0,255`, `256,0,0`, 1)}
	for _, model := range invalid {
		body := strings.TrimSuffix(photo, "}") + `,"reportVersion":2,"model":` + model + `}`
		res, _ := request(t, s.URL, "POST", "/api/reports", body, "invalid")
		if res.StatusCode != 400 {
			t.Fatalf("invalid model accepted: %s", model)
		}
	}
	_, list := request(t, s.URL, "GET", "/api/issues", "")
	if string(list["data"]) != "[]" {
		t.Fatal("incomplete report visible")
	}
}
func TestModelStorageFailureAndConcurrentReplay(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "data.json")
	h, _ := newAPI(path, "*")
	s := httptest.NewServer(h)
	defer s.Close()
	photo, _ := photoBody(t)
	body := strings.TrimSuffix(photo, "}") + `,"reportVersion":2,"model":` + modelJSON + `}`
	// Rename onto an existing directory fails on every platform, through the real filesystem boundary.
	if err := os.Mkdir(path, 0700); err != nil {
		t.Fatal(err)
	}
	res, _ := request(t, s.URL, "POST", "/api/reports", body, "atomic-model")
	if res.StatusCode != 500 {
		t.Fatal("failed persistence acknowledged")
	}
	_, list := request(t, s.URL, "GET", "/api/issues", "")
	if string(list["data"]) != "[]" {
		t.Fatal("failed save visible")
	}
	os.Remove(path)
	var wg sync.WaitGroup
	for i := 0; i < 6; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			res, _ := request(t, s.URL, "POST", "/api/reports", body, "atomic-model")
			if res.StatusCode != 201 && res.StatusCode != 200 {
				t.Errorf("replay: %d", res.StatusCode)
			}
		}()
	}
	wg.Wait()
	_, list = request(t, s.URL, "GET", "/api/issues", "")
	var issues []Issue
	json.Unmarshal(list["data"], &issues)
	if len(issues) != 1 || issues[0].ModelURL == "" || issues[0].SnapshotURL == "" {
		t.Fatal("partial/duplicate model report")
	}
}
func TestLargeCompleteModelWithinSupportedBudget(t *testing.T) {
	var m ScanModel
	json.Unmarshal([]byte(modelJSON), &m)
	for len(m.Vertices) < 270000 {
		m.Vertices = append(m.Vertices, 0.1234567890123456, 0.2345678901234567, 0.3456789012345678)
		m.Colors = append(m.Colors, 123, 234, 245)
	}
	for len(m.Triangles) < 480000 {
		m.Triangles = append(m.Triangles, 0, 1, 2)
	}
	img := image.NewRGBA(image.Rect(0, 0, 1800, 1100))
	var seed uint32 = 1
	for i := 0; i < len(img.Pix); i += 4 {
		for j := 0; j < 3; j++ {
			seed = 1664525*seed + 1013904223
			img.Pix[i+j] = byte(seed >> 24)
		}
		img.Pix[i+3] = 255
	}
	var photo bytes.Buffer
	if err := png.Encode(&photo, img); err != nil {
		t.Fatal(err)
	}
	if photo.Len() > 6*1024*1024 {
		t.Fatal("fixture photo exceeds budget")
	}
	body, _ := json.Marshal(map[string]any{"reportVersion": 2, "model": m, "snapshot": base64.StdEncoding.EncodeToString(photo.Bytes()), "title": "large real dimensions", "priority": "high", "position": Position{}})
	if len(body) <= 12*1024*1024 || len(body) > 32*1024*1024 {
		t.Fatalf("fixture size %d", len(body))
	}
	h, _ := newAPI(filepath.Join(t.TempDir(), "data.json"), "*")
	s := httptest.NewServer(h)
	defer s.Close()
	res, result := request(t, s.URL, "POST", "/api/reports", string(body), "large-model")
	if res.StatusCode != 201 {
		t.Fatalf("legal full request rejected: %d %s", res.StatusCode, result)
	}
}
func TestIncompleteModelNumbersRejected(t *testing.T) {
	h, _ := newAPI(filepath.Join(t.TempDir(), "data.json"), "*")
	s := httptest.NewServer(h)
	defer s.Close()
	photo, _ := photoBody(t)
	for _, model := range []string{strings.Replace(modelJSON, `"position":{"x":0.25,"y":0.25,"z":0},`, "", 1), strings.Replace(modelJSON, `[0,0,0,1`, `[null,0,0,1`, 1), strings.Replace(modelJSON, `[0,1,2]`, `[null,1,2]`, 1), strings.Replace(modelJSON, `[255,0,0`, `[255,null,0`, 1), strings.Replace(modelJSON, `"z":0}`, `"z":null}`, 1)} {
		body := strings.TrimSuffix(photo, "}") + `,"reportVersion":2,"model":` + model + `}`
		res, _ := request(t, s.URL, "POST", "/api/reports", body)
		if res.StatusCode != 400 {
			t.Fatalf("incomplete model accepted: %s", model)
		}
	}
}
