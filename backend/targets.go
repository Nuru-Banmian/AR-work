package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"image"
	_ "image/jpeg"
	_ "image/png"
	"io"
	"math"
	"net/http"
	"regexp"
	"strings"
)

// Versions are immutable: replacement atomically retires all earlier versions.
type ReferenceTarget struct {
	ID           string  `json:"id"`
	Version      int     `json:"version"`
	Name         string  `json:"name"`
	Location     string  `json:"location"`
	WidthMeters  float64 `json:"widthMeters"`
	HeightMeters float64 `json:"heightMeters"`
	ImageBase64  string  `json:"imageBase64"`
	ImageSHA256  string  `json:"imageSha256"`
	Active       bool    `json:"active"`
}

var referenceID = regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)

func (a *api) registeredReference(ref *Reference) bool {
	if ref == nil {
		return true
	} // Legacy API requests remain supported.
	for _, target := range a.targets {
		if target.ID == ref.CardID && target.Version == ref.Version && target.Active && math.Abs(target.WidthMeters-ref.WidthMeters) < 1e-6 && math.Abs(target.HeightMeters-ref.HeightMeters) < 1e-6 {
			return true
		}
	}
	return false
}

func (a *api) references(w http.ResponseWriter, r *http.Request) {
	if r.Method == "GET" {
		a.mu.Lock()
		targets := append([]ReferenceTarget{}, a.targets...)
		a.mu.Unlock()
		respond(w, 200, map[string]any{"data": targets})
		return
	}
	if r.Method != "POST" {
		fail(w, 405, "method_not_allowed", "Use GET or POST")
		return
	}
	var input struct {
		ID           string  `json:"id"`
		Version      int     `json:"version"`
		Name         string  `json:"name"`
		Location     string  `json:"location"`
		WidthMeters  float64 `json:"widthMeters"`
		HeightMeters float64 `json:"heightMeters"`
		ImageBase64  string  `json:"imageBase64"`
	}
	decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 768*1024))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&input); err != nil {
		fail(w, 400, "invalid_argument", "Invalid reference JSON or body exceeds 768 KiB")
		return
	}
	if decoder.Decode(new(any)) != io.EOF {
		fail(w, 400, "invalid_argument", "Expected one reference object")
		return
	}
	input.Name = strings.TrimSpace(input.Name)
	input.Location = strings.TrimSpace(input.Location)
	if !referenceID.MatchString(input.ID) || input.Version < 1 || input.Version > 10000 || input.Name == "" || len(input.Name) > 200 || input.Location == "" || len(input.Location) > 500 || input.WidthMeters < 0.05 || input.WidthMeters > 5 || input.HeightMeters < 0.05 || input.HeightMeters > 5 {
		fail(w, 400, "invalid_argument", "Reference requires id, version, name, location and measured dimensions between 0.05 and 5 metres")
		return
	}
	data, err := base64.StdEncoding.Strict().DecodeString(input.ImageBase64)
	if err != nil || len(data) > 512*1024 {
		fail(w, 400, "invalid_image", "JPEG/PNG must be base64 encoded and at most 512 KiB")
		return
	}
	config, format, err := image.DecodeConfig(bytes.NewReader(data))
	if err != nil || (format != "jpeg" && format != "png") || config.Width < 256 || config.Height < 256 || config.Width > 2048 || config.Height > 2048 {
		fail(w, 400, "invalid_image", "JPEG/PNG dimensions must be 256–2048 pixels per edge")
		return
	}
	if _, _, err = image.Decode(bytes.NewReader(data)); err != nil {
		fail(w, 400, "invalid_image", "Image pixels cannot be decoded")
		return
	}
	if math.Abs((float64(config.Width)/float64(config.Height))/(input.WidthMeters/input.HeightMeters)-1) > 0.015 {
		fail(w, 400, "invalid_image", "Image must be cropped to the measured rectangular target, preserving its aspect ratio")
		return
	}
	hash := sha256.Sum256(data)
	target := ReferenceTarget{input.ID, input.Version, input.Name, input.Location, input.WidthMeters, input.HeightMeters, input.ImageBase64, hex.EncodeToString(hash[:]), true}
	a.mu.Lock()
	defer a.mu.Unlock()
	latest := 0
	for _, old := range a.targets {
		if old.ID == target.ID {
			if old.Version > latest {
				latest = old.Version
			}
			if old.Version == target.Version {
				compare := old
				compare.Active = true
				if compare != target {
					fail(w, 409, "reference_conflict", "A reference version is immutable; register the next version")
					return
				}
				respond(w, 200, map[string]any{"data": old})
				return
			}
		} else if old.Active && old.ImageSHA256 == target.ImageSHA256 {
			fail(w, 409, "duplicate_reference", "This image is already assigned to another reference")
			return
		}
	}
	if target.Version != latest+1 {
		fail(w, 409, "reference_conflict", "Register the next sequential version")
		return
	}
	if len(a.targets) >= 32 {
		fail(w, 409, "reference_limit", "At most 32 reference versions are supported")
		return
	}
	next := append([]ReferenceTarget{}, a.targets...)
	for i := range next {
		if next[i].ID == target.ID {
			next[i].Active = false
		}
	}
	next = append(next, target)
	if err := saveIssues(a.path, snapshot{Issues: a.issues, Requests: a.requests, References: next}); err != nil {
		fail(w, 500, "storage_error", "Reference was not changed; retry the same request")
		return
	}
	a.targets = next
	a.notifySaved()
	respond(w, 201, map[string]any{"data": target})
}
