package main

import (
	"encoding/json"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"path/filepath"
	"testing"
)

func TestReceiverIdentityAndGuard(t *testing.T) {
	path := filepath.Join(t.TempDir(), "receiver.json")
	first, err := loadReceiver(path, "first")
	if err != nil {
		t.Fatal(err)
	}
	second, err := loadReceiver(path, "renamed")
	if err != nil || first.ID != second.ID || second.Name != "renamed" {
		t.Fatalf("identity did not survive rename: %+v %v", second, err)
	}
	other, _ := loadReceiver(filepath.Join(t.TempDir(), "receiver.json"), "other")
	if first.ID == other.ID {
		t.Fatal("different receivers share identity")
	}
	handler, _ := newAPI(filepath.Join(t.TempDir(), "issues.json"), "*")
	second.Port = 18089
	second.Addresses = []ReceiverAddress{{Interface: "LAN", Address: "http://192.168.1.8:18089"}}
	server := httptest.NewServer(receiverHandler(handler, second))
	defer server.Close()
	response, err := http.Get(server.URL + "/api/receiver")
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	var actual struct {
		Data Receiver `json:"data"`
	}
	if err := json.NewDecoder(response.Body).Decode(&actual); err != nil || actual.Data.ID != first.ID || actual.Data.Protocol != 1 {
		t.Fatalf("bad handshake: %+v %v", actual, err)
	}
	if actual.Data.Port != 18089 || actual.Data.Addresses == nil {
		t.Fatalf("phone connection instructions missing actual API address: %+v", actual.Data)
	}
	for _, address := range actual.Data.Addresses {
		parsed, err := url.Parse(address.Address)
		if err != nil || parsed.Scheme != "http" || parsed.Port() != "18089" || parsed.Hostname() == "127.0.0.1" {
			t.Fatalf("phone instructions use incorrect API port or loopback: %+v", address)
		}
	}
	request, _ := http.NewRequest("POST", server.URL+"/api/reports", nil)
	request.Header.Set("X-ARWork-Receiver", other.ID)
	rejected, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer rejected.Body.Close()
	if rejected.StatusCode != 409 {
		t.Fatalf("wrong target was not rejected: %d", rejected.StatusCode)
	}
	for _, target := range []string{other.ID, second.ID} {
		query, _ := http.NewRequest("GET", server.URL+"/api/issues", nil)
		query.Header.Set("X-ARWork-Receiver", target)
		result, err := http.DefaultClient.Do(query)
		if err != nil {
			t.Fatal(err)
		}
		result.Body.Close()
		if result.Header.Get("X-ARWork-Receiver") != second.ID {
			t.Fatal("query response identity missing")
		}
		if (target == second.ID && result.StatusCode != 200) || (target != second.ID && result.StatusCode != 409) {
			t.Fatal("authoritative deletion query accepted wrong receiver")
		}
	}
}
func TestListenFallbackOwnsActualPort(t *testing.T) {
	occupied, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer occupied.Close()
	port := occupied.Addr().(*net.TCPAddr).Port
	if port > 65525 {
		t.Skip("ephemeral port too high")
	}
	listener, err := listenRange("127.0.0.1", port, 10)
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	if listener.Addr().(*net.TCPAddr).Port == port {
		t.Fatal("connected occupied service")
	}
	duplicate, err := net.Listen("tcp", listener.Addr().String())
	if err == nil {
		duplicate.Close()
		t.Fatal("fallback released its port")
	}
	if l, err := listenRange("127.0.0.1", port, 1); err == nil {
		l.Close()
		t.Fatal("exhaustion should fail")
	}
}
