package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"image"
	"image/color"
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

func TestCompleteRemarkReplaysWithoutRollingBackNewerModel(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	h, _ := newAPI(path, "*")
	s := httptest.NewServer(h)
	defer s.Close()
	photo, _ := photoBody(t)
	body := strings.TrimSuffix(photo, "}") + `,"reportVersion":2,"model":` + modelJSON + `}`
	_, created := request(t, s.URL, "POST", "/api/reports", body, "creation")
	var original Issue
	json.Unmarshal(created["data"], &original)
	endpoint := "/api/issues/" + original.ID
	replacement := `{"reportVersion":2,"position":{"x":4,"y":5,"z":6},"snapshot":` + snapshotField(t, photo) + `,"model":` + strings.Replace(modelJSON, "0.25", "0.3", 1) + `}`
	res, result := request(t, s.URL, "PATCH", endpoint+"/position", replacement, "remark-one")
	if res.StatusCode != 200 {
		t.Fatalf("complete replacement: %d %s", res.StatusCode, result)
	}
	var changed Issue
	json.Unmarshal(result["data"], &changed)
	if changed.ID != original.ID || changed.Title != original.Title || changed.CreatedAt != original.CreatedAt || changed.ModelURL == "" || changed.Position != (Position{4, 5, 6}) {
		t.Fatalf("mixed issue: %+v", changed)
	}
	newer := strings.Replace(replacement, "0.3", "0.4", 1)
	request(t, s.URL, "PATCH", endpoint+"/position", newer, "remark-two")
	request(t, s.URL, "PATCH", endpoint+"/content", `{"title":"Manager edit","description":"retained","priority":"low"}`)
	request(t, s.URL, "PATCH", endpoint+"/status", `{"status":"resolved"}`)
	s.Close()
	h, _ = newAPI(path, "*")
	s = httptest.NewServer(h)
	defer s.Close()
	res, result = request(t, s.URL, "PATCH", endpoint+"/position", replacement, "remark-one")
	if res.StatusCode != 200 {
		t.Fatal(res.StatusCode)
	}
	if string(result["replacementCurrent"]) != "false" {
		t.Fatal("superseded scene claimed to be current")
	}
	json.Unmarshal(result["data"], &changed)
	if changed.Title != "Manager edit" || changed.Status != "resolved" {
		t.Fatal("replay stale unrelated fields")
	}
	response, err := http.Get(s.URL + endpoint + "/model")
	if err != nil {
		t.Fatal(err)
	}
	data, _ := io.ReadAll(response.Body)
	response.Body.Close()
	if !strings.Contains(string(data), `"x":0.4`) {
		t.Fatalf("old replay replaced newer model: %s", data)
	}
	_, replayCreate := request(t, s.URL, "POST", "/api/reports", body, "creation")
	var replayed Issue
	json.Unmarshal(replayCreate["data"], &replayed)
	if replayed.SceneRevision != changed.SceneRevision || replayed.Position != changed.Position {
		t.Fatal("creation replay mixes old position with current attachments")
	}
	res, _ = request(t, s.URL, "PATCH", endpoint+"/position", newer, "remark-one")
	if res.StatusCode != 409 {
		t.Fatal("same key changed body accepted")
	}
	_, list := request(t, s.URL, "GET", "/api/issues", "")
	var issues []Issue
	json.Unmarshal(list["data"], &issues)
	if len(issues) != 1 {
		t.Fatal("remark created issue")
	}
}

func snapshotField(t *testing.T, body string) string {
	t.Helper()
	var fields map[string]json.RawMessage
	if err := json.Unmarshal([]byte(body), &fields); err != nil {
		t.Fatal(err)
	}
	return string(fields["snapshot"])
}

func TestConcurrentCompleteRemarksKeepOneScene(t *testing.T) {
	h, _ := newAPI(filepath.Join(t.TempDir(), "issues.json"), "*")
	s := httptest.NewServer(h)
	defer s.Close()
	photo, oldPixels := photoBody(t)
	_, created := request(t, s.URL, "POST", "/api/reports", strings.TrimSuffix(photo, "}")+`,"reportVersion":2,"model":`+modelJSON+`}`, "create")
	var issue Issue
	json.Unmarshal(created["data"], &issue)
	img := image.NewRGBA(image.Rect(0, 0, 32, 32))
	img.Set(1, 1, color.RGBA{0, 0, 255, 255})
	var buffer bytes.Buffer
	png.Encode(&buffer, img)
	bodies := []string{
		`{"reportVersion":2,"position":{"x":1,"y":1,"z":1},"snapshot":` + snapshotField(t, photo) + `,"model":` + strings.Replace(modelJSON, "0.25", "0.3", 1) + `}`,
		`{"reportVersion":2,"position":{"x":2,"y":2,"z":2},"snapshot":"` + base64.StdEncoding.EncodeToString(buffer.Bytes()) + `","model":` + strings.Replace(modelJSON, "0.25", "0.4", 1) + `}`,
	}
	var wg sync.WaitGroup
	start := make(chan struct{})
	for i := 0; i < 8; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			<-start
			key := "first"
			if i%2 == 1 {
				key = "second"
			}
			res, _ := request(t, s.URL, "PATCH", "/api/issues/"+issue.ID+"/position", bodies[i%2], key)
			if res.StatusCode != 200 {
				t.Errorf("replay %d", res.StatusCode)
			}
		}(i)
	}
	close(start)
	wg.Wait()
	_, detail := request(t, s.URL, "GET", "/api/issues/"+issue.ID, "")
	json.Unmarshal(detail["data"], &issue)
	response, _ := http.Get(s.URL + issue.ModelURL)
	model, _ := io.ReadAll(response.Body)
	response.Body.Close()
	response, _ = http.Get(s.URL + issue.SnapshotURL)
	pixels, _ := io.ReadAll(response.Body)
	response.Body.Close()
	if issue.Position == (Position{1, 1, 1}) {
		if !strings.Contains(string(model), `"x":0.3`) || !bytes.Equal(pixels, oldPixels) {
			t.Fatal("first scene mixed")
		}
	} else if issue.Position == (Position{2, 2, 2}) {
		if !strings.Contains(string(model), `"x":0.4`) || !bytes.Equal(pixels, buffer.Bytes()) {
			t.Fatal("second scene mixed")
		}
	} else {
		t.Fatal("no coherent winner")
	}
}

func TestCompleteRemarkFailureRetainsOldSceneAndSuccessRetiresOldBytes(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "issues.json")
	h, _ := newAPI(path, "*")
	s := httptest.NewServer(h)
	defer s.Close()
	photo, oldPixels := photoBody(t)
	body := strings.TrimSuffix(photo, "}") + `,"reportVersion":2,"model":` + modelJSON + `}`
	_, created := request(t, s.URL, "POST", "/api/reports", body, "original")
	var original Issue
	json.Unmarshal(created["data"], &original)
	endpoint := "/api/issues/" + original.ID
	img := image.NewRGBA(image.Rect(0, 0, 48, 32))
	img.Set(2, 2, color.RGBA{0, 255, 0, 255})
	var buf bytes.Buffer
	png.Encode(&buf, img)
	replacement := `{"reportVersion":2,"position":{"x":7,"y":8,"z":9},"snapshot":"` + base64.StdEncoding.EncodeToString(buf.Bytes()) + `","model":` + strings.Replace(modelJSON, "0.25", "0.4", 1) + `}`
	for _, bad := range []string{strings.Replace(replacement, `"reportVersion":2`, `"reportVersion":3`, 1), strings.Replace(replacement, `[0,1,2]`, `[0,1,5]`, 1), `{"position":{"x":7,"y":8,"z":9}}`, strings.Replace(replacement, `"snapshot":"`, `"snapshot":"broken`, 1)} {
		res, _ := request(t, s.URL, "PATCH", endpoint+"/position", bad, "invalid")
		if res.StatusCode != 400 {
			t.Fatal("incomplete scene accepted")
		}
	}
	os.Rename(path, path+".original")
	os.Mkdir(path, 0700)
	res, _ := request(t, s.URL, "PATCH", endpoint+"/position", replacement, "replace")
	if res.StatusCode != 500 {
		t.Fatal("failed write acknowledged")
	}
	_, detail := request(t, s.URL, "GET", endpoint, "")
	if string(detail["data"]) != string(created["data"]) {
		t.Fatal("failed write changed metadata")
	}
	response, _ := http.Get(s.URL + original.SnapshotURL)
	actual, _ := io.ReadAll(response.Body)
	response.Body.Close()
	if !bytes.Equal(actual, oldPixels) {
		t.Fatal("failed write lost photo")
	}
	response, _ = http.Get(s.URL + original.ModelURL)
	actual, _ = io.ReadAll(response.Body)
	response.Body.Close()
	if !strings.Contains(string(actual), `"x":0.25`) {
		t.Fatal("failed write lost model")
	}
	os.Remove(path)
	os.Rename(path+".original", path)
	var wg sync.WaitGroup
	for suffix, b := range map[string]string{"/position": replacement, "/status": `{"status":"resolved"}`, "/content": `{"title":"Concurrent","description":"keep","priority":"low"}`} {
		wg.Add(1)
		go func(suffix, b string) {
			defer wg.Done()
			res, _ := request(t, s.URL, "PATCH", endpoint+suffix, b, "replace")
			if res.StatusCode != 200 {
				t.Errorf("concurrent %s: %d", suffix, res.StatusCode)
			}
		}(suffix, b)
	}
	wg.Wait()
	_, detail = request(t, s.URL, "GET", endpoint, "")
	var changed Issue
	json.Unmarshal(detail["data"], &changed)
	if changed.Status != "resolved" || changed.Title != "Concurrent" || changed.Position != (Position{7, 8, 9}) {
		t.Fatal("lost concurrent field")
	}
	data, _ := os.ReadFile(path)
	if bytes.Contains(data, []byte(base64.StdEncoding.EncodeToString(oldPixels))) || bytes.Contains(data, []byte(`"x":0.25`)) {
		t.Fatal("old attachment retained in durable snapshot")
	}
	files, _ := os.ReadDir(dir)
	if len(files) != 1 {
		t.Fatal("old or temporary attachment snapshot retained")
	}
	s.Close()
	h, _ = newAPI(path, "*")
	s = httptest.NewServer(h)
	defer s.Close()
	response, _ = http.Get(s.URL + original.SnapshotURL)
	actual, _ = io.ReadAll(response.Body)
	response.Body.Close()
	if !bytes.Equal(actual, buf.Bytes()) {
		t.Fatal("restart lost new photo")
	}
	res, _ = request(t, s.URL, "PATCH", endpoint+"/position", replacement, "replace")
	if res.StatusCode != 200 {
		t.Fatal("receipt lost during content/status save")
	}
}
