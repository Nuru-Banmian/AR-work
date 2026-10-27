package main

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"time"
)

type Receiver struct {
	ID        string            `json:"id"`
	Name      string            `json:"name"`
	Protocol  int               `json:"protocol"`
	Port      int               `json:"port"`
	Addresses []ReceiverAddress `json:"addresses"`
}

// Identity belongs to the data directory, independent of host name or service address.
func loadReceiver(path, name string) (Receiver, error) {
	var stored Receiver
	data, err := os.ReadFile(path)
	if err == nil {
		if err = json.Unmarshal(data, &stored); err != nil || len(stored.ID) != 32 {
			return stored, errors.New("invalid receiver identity; preserve identity file")
		}
	} else if errors.Is(err, os.ErrNotExist) {
		if err = os.MkdirAll(filepath.Dir(path), 0700); err != nil {
			return stored, err
		}
		bytes := make([]byte, 16)
		if _, err = rand.Read(bytes); err != nil {
			return stored, err
		}
		stored.ID = hex.EncodeToString(bytes)
		// Exclusive creation prevents two starting processes from inventing different identities.
		file, createErr := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
		if errors.Is(createErr, os.ErrExist) {
			return loadReceiver(path, name)
		}
		if createErr != nil {
			return stored, createErr
		}
		err = json.NewEncoder(file).Encode(stored)
		if err == nil {
			err = file.Sync()
		}
		closeErr := file.Close()
		if err != nil {
			return stored, err
		}
		if closeErr != nil {
			return stored, closeErr
		}
	} else {
		return stored, err
	}
	stored.Name = name
	stored.Protocol = 1
	return stored, nil
}
func receiverHandler(next http.Handler, receiver Receiver) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-ARWork-Receiver", receiver.ID)
		// Guard before reading any request bytes. Older clients without this header remain supported.
		if target := r.Header.Get("X-ARWork-Receiver"); target != "" && target != receiver.ID {
			fail(w, 409, "receiver_mismatch", "Receiver identity changed; request was not accepted")
			return
		}
		if r.URL.Path == "/api/receiver" {
			if r.Method != "GET" {
				fail(w, 405, "method_not_allowed", "Use GET")
				return
			}
			current := receiver
			current.Addresses = phoneAddresses(current.Port)
			respond(w, 200, map[string]any{"data": current})
			return
		}
		next.ServeHTTP(w, r)
	})
}
func listenRange(host string, start, count int) (net.Listener, error) {
	for port := start; port < start+count && port <= 65535; port++ {
		listener, err := net.Listen("tcp", net.JoinHostPort(host, strconv.Itoa(port)))
		if err == nil {
			return listener, nil
		}
	}
	return nil, fmt.Errorf("no available API port in %d..%d", start, start+count-1)
}

type ReceiverAddress struct {
	Interface string `json:"interface"`
	Address   string `json:"address"`
}

func phoneAddresses(port int) []ReceiverAddress {
	result := []ReceiverAddress{}
	interfaces, err := net.Interfaces()
	if err != nil {
		return result
	}
	for _, device := range interfaces {
		if device.Flags&net.FlagUp == 0 || device.Flags&net.FlagLoopback != 0 {
			continue
		}
		addresses, err := device.Addrs()
		if err != nil {
			continue
		}
		for _, address := range addresses {
			ip, _, err := net.ParseCIDR(address.String())
			if err != nil || ip.To4() == nil || ip.IsLoopback() || ip.IsLinkLocalUnicast() {
				continue
			}
			result = append(result, ReceiverAddress{Interface: device.Name, Address: "http://" + net.JoinHostPort(ip.String(), strconv.Itoa(port))})
		}
	}
	return result
}

func runReceiver() error {
	dataPath := env("ARWORK_DATA", "data/issues.json")
	handler, err := newAPI(dataPath, env("ARWORK_ORIGINS", "http://localhost:5176,http://127.0.0.1:5176"))
	if err != nil {
		return err
	}
	name, _ := os.Hostname()
	receiver, err := loadReceiver(filepath.Join(filepath.Dir(dataPath), "receiver.json"), env("ARWORK_NAME", name))
	if err != nil {
		return err
	}
	host, portText, err := net.SplitHostPort(env("ARWORK_ADDR", ":18088"))
	if err != nil {
		return err
	}
	port, err := strconv.Atoi(portText)
	if err != nil || port < 1 || port > 65535 {
		return errors.New("invalid API port")
	}
	listener, err := listenRange(host, port, 10)
	if err != nil {
		return err
	}
	defer listener.Close()
	receiver.Port = listener.Addr().(*net.TCPAddr).Port
	receiver.Addresses = phoneAddresses(receiver.Port)
	// Launcher reads a unique per-run readiness file only after the API socket is owned.
	if ready := os.Getenv("ARWORK_READY"); ready != "" {
		bytes, _ := json.Marshal(receiver)
		if err := os.WriteFile(ready+".tmp", bytes, 0600); err != nil {
			return err
		}
		if err := os.Rename(ready+".tmp", ready); err != nil {
			return err
		}
	}
	server := &http.Server{Handler: receiverHandler(handler, receiver), ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 20 * time.Second, WriteTimeout: 20 * time.Second, IdleTimeout: 60 * time.Second}
	log.Printf("AR Work receiver %s listening on %s", receiver.ID, listener.Addr())
	return server.Serve(listener)
}
