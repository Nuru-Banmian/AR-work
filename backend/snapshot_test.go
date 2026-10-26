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
	"testing"
)

func photoBody(t *testing.T) (string, []byte) {
	t.Helper()
	img := image.NewRGBA(image.Rect(0, 0, 32, 48))
	img.Set(1, 1, color.RGBA{255, 0, 0, 255})
	var b bytes.Buffer
	if err := png.Encode(&b, img); err != nil {
		t.Fatal(err)
	}
	body := `{"title":"Snapshot report","description":"entrance","priority":"high","position":{"x":1,"y":2,"z":3},"snapshot":"` + base64.StdEncoding.EncodeToString(b.Bytes()) + `"}`
	return body, b.Bytes()
}

func TestSnapshotReportSurvivesRestartAndReplay(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	s := httptest.NewServer(handler)
	body, pixels := photoBody(t)
	res, result := request(t, s.URL, "POST", "/api/reports", body, "photo-key")
	if res.StatusCode != 201 {
		t.Fatalf("create snapshot: %d %s", res.StatusCode, result)
	}
	var issue struct {
		ID          string `json:"id"`
		SnapshotURL string `json:"snapshotUrl"`
	}
	json.Unmarshal(result["data"], &issue)
	if issue.SnapshotURL != "/api/issues/"+issue.ID+"/snapshot" {
		t.Fatalf("missing association: %+v", issue)
	}
	s.Close()
	handler, err = newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	s = httptest.NewServer(handler)
	defer s.Close()
	res, replay := request(t, s.URL, "POST", "/api/reports", body, "photo-key")
	if res.StatusCode != 200 || string(replay["data"]) != string(result["data"]) {
		t.Fatal("replay changed report")
	}
	photo, err := http.Get(s.URL + issue.SnapshotURL)
	if err != nil {
		t.Fatal(err)
	}
	defer photo.Body.Close()
	actual, _ := io.ReadAll(photo.Body)
	if photo.StatusCode != 200 || !bytes.Equal(actual, pixels) || photo.Header.Get("Content-Type") != "image/png" {
		t.Fatal("snapshot lost or changed")
	}
	_, list := request(t, s.URL, "GET", "/api/issues", "")
	var issues []json.RawMessage
	json.Unmarshal(list["data"], &issues)
	if len(issues) != 1 {
		t.Fatal("duplicate report")
	}
}

func TestRequiredSnapshotAndAtomicReplacement(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	s := httptest.NewServer(handler)
	defer s.Close()
	for _, body := range []string{reportBody, strings.TrimSuffix(reportBody, "}") + `,"snapshot":"bm90LWFuLWltYWdl"}`} {
		res, _ := request(t, s.URL, "POST", "/api/reports", body, "retry")
		if res.StatusCode != 400 {
			t.Fatalf("invalid image accepted: %d", res.StatusCode)
		}
	}
	body, _ := photoBody(t)
	res, created := request(t, s.URL, "POST", "/api/reports", body, "retry")
	if res.StatusCode != 201 {
		t.Fatal("failed validation reserved key")
	}
	var issue struct {
		ID string `json:"id"`
	}
	json.Unmarshal(created["data"], &issue)
	endpoint := "/api/issues/" + issue.ID
	res, _ = request(t, s.URL, "PATCH", endpoint+"/position", `{"position":{"x":9,"y":8,"z":7}}`)
	if res.StatusCode != 400 {
		t.Fatal("photo position updated without replacement snapshot")
	}
	var payload map[string]any
	json.Unmarshal([]byte(body), &payload)
	replacement := `{"position":{"x":9,"y":8,"z":7},"snapshot":"` + payload["snapshot"].(string) + `"}`
	if err := os.Rename(path, path+".backup"); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(path, 0700); err != nil {
		t.Fatal(err)
	}
	res, _ = request(t, s.URL, "PATCH", endpoint+"/position", replacement)
	if res.StatusCode != 500 {
		t.Fatalf("storage failure hidden: %d", res.StatusCode)
	}
	_, unchanged := request(t, s.URL, "GET", endpoint, "")
	if string(unchanged["data"]) != string(created["data"]) {
		t.Fatal("failed replacement mutated report")
	}
	os.Remove(path)
	os.Rename(path+".backup", path)
	res, _ = request(t, s.URL, "PATCH", endpoint+"/position", replacement)
	if res.StatusCode != 200 {
		t.Fatalf("valid replacement rejected: %d", res.StatusCode)
	}
	res, _ = request(t, s.URL, "PATCH", endpoint+"/content", `{"title":"PC edit","description":"new","priority":"low","snapshot":"fake"}`)
	if res.StatusCode != 400 {
		t.Fatal("content endpoint accepted snapshot")
	}
	request(t, s.URL, "PATCH", endpoint+"/content", `{"title":"PC edit","description":"new","priority":"low"}`)
	request(t, s.URL, "PATCH", endpoint+"/status", `{"status":"resolved"}`)
	_, latest := request(t, s.URL, "GET", endpoint, "")
	var actual struct {
		Title, Status, SnapshotURL string
		Position                   Position
	}
	json.Unmarshal(latest["data"], &actual)
	if actual.Title != "PC edit" || actual.Status != "resolved" || actual.Position != (Position{9, 8, 7}) || actual.SnapshotURL == "" {
		t.Fatalf("fields overwritten: %+v", actual)
	}
}

func TestSnapshotValidationAndMissingMedia(t *testing.T) {
	handler, err := newAPI(filepath.Join(t.TempDir(), "issues.json"), "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	s := httptest.NewServer(handler)
	defer s.Close()
	valid, _ := photoBody(t)
	var base map[string]any
	json.Unmarshal([]byte(valid), &base)
	for name, photo := range map[string]string{"bad base64": "!", "not image": base64.StdEncoding.EncodeToString([]byte("not an image")), "too large": strings.Repeat("A", 8*1024*1024+4)} {
		t.Run(name, func(t *testing.T) {
			base["snapshot"] = photo
			body, _ := json.Marshal(base)
			res, _ := request(t, s.URL, "POST", "/api/reports", string(body))
			if res.StatusCode != 400 {
				t.Fatalf("invalid snapshot: %d", res.StatusCode)
			}
		})
	}
	var tiny bytes.Buffer
	png.Encode(&tiny, image.NewRGBA(image.Rect(0, 0, 1, 1)))
	base["snapshot"] = base64.StdEncoding.EncodeToString(tiny.Bytes())
	body, _ := json.Marshal(base)
	res, _ := request(t, s.URL, "POST", "/api/reports", string(body))
	if res.StatusCode != 400 {
		t.Fatal("tiny image accepted")
	}
	_, created := request(t, s.URL, "POST", "/api/issues", reportBody)
	var issue Issue
	json.Unmarshal(created["data"], &issue)
	if issue.SnapshotURL != "" {
		t.Fatal("invented legacy photo")
	}
	res, _ = request(t, s.URL, "GET", "/api/issues/"+issue.ID+"/snapshot", "")
	if res.StatusCode != 404 {
		t.Fatal("missing image not explicit")
	}
}

func TestFailedPhotoCreationCanRetryWithoutDuplicate(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "http://localhost:5173")
	if err != nil {
		t.Fatal(err)
	}
	s := httptest.NewServer(handler)
	defer s.Close()
	body, _ := photoBody(t)
	if err := os.Mkdir(path, 0700); err != nil {
		t.Fatal(err)
	}
	res, _ := request(t, s.URL, "POST", "/api/reports", body, "failure-key")
	if res.StatusCode != 500 {
		t.Fatal("storage failure hidden")
	}
	_, list := request(t, s.URL, "GET", "/api/issues", "")
	if string(list["data"]) != "[]" {
		t.Fatal("half report visible")
	}
	os.Remove(path)
	res, created := request(t, s.URL, "POST", "/api/reports", body, "failure-key")
	if res.StatusCode != 201 {
		t.Fatal("failed key reserved")
	}
	res, replayed := request(t, s.URL, "POST", "/api/reports", body, "failure-key")
	if res.StatusCode != 200 || string(created["data"]) != string(replayed["data"]) {
		t.Fatal("retry changed report")
	}
	changed := strings.Replace(body, "entrance", "different", 1)
	res, _ = request(t, s.URL, "POST", "/api/reports", changed, "failure-key")
	if res.StatusCode != 409 {
		t.Fatal("changed frozen request accepted")
	}
}
