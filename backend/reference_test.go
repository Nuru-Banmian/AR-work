package main

import (
	"encoding/json"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
)

const cardLocation = `"reference":{"cardId":"ARWORK-A","version":1,"widthMeters":0.2,"heightMeters":0.2,"relativePosition":{"x":1,"y":0.25,"z":-2}}`

func TestReferenceSurvivesRestartAndRemark(t *testing.T) {
	path := filepath.Join(t.TempDir(), "issues.json")
	handler, err := newAPI(path, "*")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	request(t, server.URL, "POST", "/api/references", targetBody("ARWORK-A", 1))
	body := strings.TrimSuffix(reportBody, "}") + `,` + cardLocation + `}`
	res, created := request(t, server.URL, "POST", "/api/issues", body, "reference-create")
	if res.StatusCode != 201 {
		t.Fatalf("create: %d %s", res.StatusCode, created)
	}
	var issue map[string]json.RawMessage
	json.Unmarshal(created["data"], &issue)
	if !strings.Contains(string(issue["reference"]), `"cardId":"ARWORK-A"`) {
		t.Fatal("reference missing")
	}
	var id string
	json.Unmarshal(issue["id"], &id)
	endpoint := "/api/issues/" + id
	request(t, server.URL, "PATCH", endpoint+"/status", `{"status":"resolved"}`)
	server.Close()
	handler, err = newAPI(path, "*")
	if err != nil {
		t.Fatal(err)
	}
	server = httptest.NewServer(handler)
	defer server.Close()
	_, detail := request(t, server.URL, "GET", endpoint, "")
	var restored map[string]json.RawMessage
	json.Unmarshal(detail["data"], &restored)
	if string(restored["reference"]) != string(issue["reference"]) {
		t.Fatal("restart lost reference")
	}
	res, _ = request(t, server.URL, "PATCH", endpoint+"/position", `{"position":{"x":5,"y":6,"z":7},`+strings.Replace(cardLocation, `"x":1`, `"x":3`, 1)+`}`)
	if res.StatusCode != 200 {
		t.Fatal("remark rejected", res.StatusCode)
	}
	_, detail = request(t, server.URL, "GET", endpoint, "")
	json.Unmarshal(detail["data"], &restored)
	if string(restored["status"]) != `"resolved"` || string(restored["title"]) != string(issue["title"]) || !strings.Contains(string(restored["reference"]), `"x":3`) {
		t.Fatal("remark did not preserve content and replace reference")
	}
	res, _ = request(t, server.URL, "POST", "/api/issues", strings.Replace(body, `"x":1`, `"x":9`, 1), "reference-create")
	if res.StatusCode != 409 {
		t.Fatal("reference missing from idempotency fingerprint", res.StatusCode)
	}
}

func TestInvalidReferenceCannotReplaceExistingLocation(t *testing.T) {
	handler, err := newAPI(filepath.Join(t.TempDir(), "issues.json"), "*")
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(handler)
	defer server.Close()
	request(t, server.URL, "POST", "/api/references", targetBody("ARWORK-A", 1))
	_, created := request(t, server.URL, "POST", "/api/issues", reportBody)
	var issue Issue
	json.Unmarshal(created["data"], &issue)
	endpoint := "/api/issues/" + issue.ID
	for _, ref := range []string{
		strings.Replace(cardLocation, "ARWORK-A", "ARWORK-B", 1),
		strings.Replace(cardLocation, `"version":1`, `"version":2`, 1),
		strings.Replace(cardLocation, "0.2", "0.3", 1),
		strings.Replace(cardLocation, `"x":1,`, ``, 1),
		strings.Replace(cardLocation, `"x":1`, `"x":null`, 1),
		strings.Replace(cardLocation, `"x":1`, `"x":101`, 1),
	} {
		body := `{"position":{"x":5,"y":6,"z":7},` + ref + `}`
		res, _ := request(t, server.URL, "PATCH", endpoint+"/position", body)
		if res.StatusCode != 400 {
			t.Fatalf("accepted invalid reference: %d %s", res.StatusCode, body)
		}
		res, _ = request(t, server.URL, "POST", "/api/issues", strings.TrimSuffix(reportBody, "}")+`,`+ref+`}`)
		if res.StatusCode != 400 {
			t.Fatalf("created invalid reference: %d", res.StatusCode)
		}
	}
	_, detail := request(t, server.URL, "GET", endpoint, "")
	if string(detail["data"]) != string(created["data"]) {
		t.Fatal("invalid reference mutated legacy issue")
	}
	res, result := request(t, server.URL, "PATCH", endpoint+"/position", `{"position":{"x":5,"y":6,"z":7},`+cardLocation+`}`)
	if res.StatusCode != 200 || !strings.Contains(string(result["data"]), `"cardId":"ARWORK-A"`) {
		t.Fatal("legacy supplementation failed")
	}
}
