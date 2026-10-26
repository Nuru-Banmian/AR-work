package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"image"
	"image/color"
	"image/png"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func targetBody(id string, version int) string {
	im := image.NewRGBA(image.Rect(0, 0, 256, 256))
	for y := 0; y < 256; y++ {
		for x := 0; x < 256; x++ {
			im.Set(x, y, color.RGBA{uint8(x), uint8(y), uint8(x * y), 255})
		}
	}
	var b bytes.Buffer
	png.Encode(&b, im)
	payload, _ := json.Marshal(map[string]any{"id": id, "version": version, "name": "入口标牌", "location": "东门内侧墙面", "widthMeters": 0.2, "heightMeters": 0.2, "imageBase64": base64.StdEncoding.EncodeToString(b.Bytes())})
	return string(payload)
}

func TestRegisteredTargetSurvivesRestartAndVersionReplacement(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "*")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	body := targetBody("entrance", 1)
	res, first := request(t, server.URL, "POST", "/api/references", body)
	if res.StatusCode != 201 {
		t.Fatalf("register: %d %s", res.StatusCode, first)
	}
	res, replay := request(t, server.URL, "POST", "/api/references", body)
	if res.StatusCode != 200 || string(replay["data"]) != string(first["data"]) {
		t.Fatal("retry changed immutable target")
	}
	server.Close()
	handler, err = newAPI(path, "*")
	if err != nil {
		t.Fatal(err)
	}
	server = httptest.NewServer(handler)
	defer server.Close()
	res, list := request(t, server.URL, "GET", "/api/references", "")
	if res.StatusCode != 200 || !bytes.Contains(list["data"], []byte("入口标牌")) {
		t.Fatal("restart lost target", list)
	}
	res, _ = request(t, server.URL, "POST", "/api/references", targetBody("entrance", 2))
	if res.StatusCode != 201 {
		t.Fatal("new version rejected", res.StatusCode)
	}
	_, list = request(t, server.URL, "GET", "/api/references", "")
	var targets []struct {
		Version int
		Active  bool
	}
	if err = json.Unmarshal(list["data"], &targets); err != nil {
		t.Fatal(err)
	}
	if len(targets) != 2 || targets[0].Active || !targets[1].Active {
		t.Fatal("old version not retired", targets)
	}
}

func TestRetiredReferenceCannotRelocateButOriginalRetryStillWorks(t *testing.T) {
	handler, err := newAPI(filepath.Join(t.TempDir(), "issues.json"), "*")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	request(t, server.URL, "POST", "/api/references", targetBody("ARWORK-A", 1))
	body := strings.TrimSuffix(reportBody, "}") + `,` + cardLocation + `}`
	res, created := request(t, server.URL, "POST", "/api/issues", body, "original")
	if res.StatusCode != 201 {
		t.Fatal("create failed")
	}
	var issue Issue
	if err = json.Unmarshal(created["data"], &issue); err != nil {
		t.Fatal(err)
	}
	request(t, server.URL, "POST", "/api/references", targetBody("ARWORK-A", 2))
	res, replay := request(t, server.URL, "POST", "/api/issues", body, "original")
	if res.StatusCode != 200 || string(replay["data"]) != string(created["data"]) {
		t.Fatal("retirement broke uncertain creation replay")
	}
	res, _ = request(t, server.URL, "POST", "/api/issues", body, "another")
	if res.StatusCode != 400 {
		t.Fatal("created against retired reference", res.StatusCode)
	}
	endpoint := "/api/issues/" + issue.ID
	res, _ = request(t, server.URL, "PATCH", endpoint+"/position", `{"position":{"x":4,"y":5,"z":6},`+cardLocation+`}`)
	if res.StatusCode != 400 {
		t.Fatal("relocated against retired reference")
	}
	_, detail := request(t, server.URL, "GET", endpoint, "")
	if string(detail["data"]) != string(created["data"]) {
		t.Fatal("retirement changed original issue")
	}
	res, _ = request(t, server.URL, "PATCH", endpoint+"/content", `{"title":"Preserved","description":"New content","priority":"low"}`)
	if res.StatusCode != 200 {
		t.Fatal("content edit rejected")
	}
	_, list := request(t, server.URL, "GET", "/api/references", "")
	if !bytes.Contains(list["data"], []byte(`"version":2`)) {
		t.Fatal("issue update erased targets")
	}
}

func TestInvalidTargetAndStorageFailureKeepPreviousVersion(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "*")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	first := targetBody("entry", 1)
	request(t, server.URL, "POST", "/api/references", first)
	for _, body := range []string{
		`{}`, `null`, first + `{}`, strings.Replace(first, `"widthMeters":0.2`, `"widthMeters":0`, 1),
		strings.Replace(first, `"widthMeters":0.2`, `"widthMeters":0.3`, 1),
		strings.Replace(first, `"imageBase64":"`, `"imageBase64":"!`, 1),
	} {
		res, _ := request(t, server.URL, "POST", "/api/references", body)
		if res.StatusCode != 400 {
			t.Fatal("accepted invalid target", res.StatusCode)
		}
	}
	res, _ := request(t, server.URL, "POST", "/api/references", strings.Replace(first, "入口标牌", "Changed", 1))
	if res.StatusCode != 409 {
		t.Fatal("overwrote immutable target")
	}
	res, _ = request(t, server.URL, "POST", "/api/references", targetBody("duplicate", 1))
	if res.StatusCode != 409 {
		t.Fatal("accepted same image with different identity")
	}
	if err = os.Rename(path, path+".backup"); err != nil {
		t.Fatal(err)
	}
	if err = os.Mkdir(path, 0700); err != nil {
		t.Fatal(err)
	}
	res, _ = request(t, server.URL, "POST", "/api/references", targetBody("entry", 2))
	if res.StatusCode != 500 {
		t.Fatal("storage failure reported success")
	}
	_, list := request(t, server.URL, "GET", "/api/references", "")
	var targets []struct {
		Version int
		Active  bool
	}
	json.Unmarshal(list["data"], &targets)
	if len(targets) != 1 || targets[0].Version != 1 || !targets[0].Active {
		t.Fatal("failed update retired original target")
	}
	if err = os.Remove(path); err != nil {
		t.Fatal(err)
	}
	if err = os.Rename(path+".backup", path); err != nil {
		t.Fatal(err)
	}
	res, _ = request(t, server.URL, "POST", "/api/references", targetBody("entry", 2))
	if res.StatusCode != 201 {
		t.Fatal("could not retry after storage recovery")
	}
}
