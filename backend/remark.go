package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"io"
	"math"
	"net/http"
	"strings"
	"time"
)

// Receipts contain no old scene or attachments. Replay acknowledges the key but
// returns the current issue, so it cannot roll back later remarks/manager edits.
type remarkReceipt struct {
	Fingerprint string `json:"fingerprint"`
	IssueID     string `json:"issueId"`
}

func (a *api) replacePosition(w http.ResponseWriter, r *http.Request) {
	parts := strings.Split(strings.TrimPrefix(r.URL.Path, "/api/issues/"), "/")
	if len(parts) != 2 || parts[0] == "" || parts[1] != "position" {
		fail(w, 404, "not_found", "Endpoint not found")
		return
	}
	if r.Method != "PATCH" {
		fail(w, 405, "method_not_allowed", "Use PATCH")
		return
	}
	var input struct {
		ReportVersion int                         `json:"reportVersion"`
		Model         *ScanModel                  `json:"model"`
		Snapshot      string                      `json:"snapshot"`
		Position      *struct{ X, Y, Z *float64 } `json:"position"`
	}
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, 32*1024*1024))
	if err != nil {
		fail(w, 400, "invalid_argument", "Request body is too large")
		return
	}
	decoder := json.NewDecoder(bytes.NewReader(body))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&input) != nil || decoder.Decode(new(any)) != io.EOF {
		fail(w, 400, "invalid_argument", "Expected position, snapshot, model and reportVersion only")
		return
	}
	if input.Snapshot == "" && len(body) > 64*1024 {
		fail(w, 400, "invalid_argument", "Request without snapshot must be at most 64 KiB")
		return
	}
	if input.Position == nil || input.Position.X == nil || input.Position.Y == nil || input.Position.Z == nil {
		fail(w, 400, "invalid_argument", "Complete finite position required")
		return
	}
	point := Position{*input.Position.X, *input.Position.Y, *input.Position.Z}
	for _, v := range []*float64{&point.X, &point.Y, &point.Z} {
		if math.IsNaN(*v) || math.IsInf(*v, 0) {
			fail(w, 400, "invalid_argument", "Finite position required")
			return
		}
		if *v == 0 {
			*v = 0
		}
	}
	if err := validateModel(input.Model, input.ReportVersion); err != nil {
		fail(w, 400, "invalid_model", err.Error())
		return
	}
	key := r.Header.Get("Idempotency-Key")
	if len(key) > 200 || (key != "" && strings.TrimSpace(key) == "") || ((input.ReportVersion == 2 || input.Model != nil) && key == "") {
		fail(w, 400, "invalid_argument", "Complete remarks require a nonblank Idempotency-Key of at most 200 bytes")
		return
	}
	canonical, _ := json.Marshal(struct {
		Position Position
		Snapshot string
		Model    *ScanModel
		Version  int
	}{point, input.Snapshot, input.Model, input.ReportVersion})
	digest := sha256.Sum256(canonical)
	fingerprint := hex.EncodeToString(digest[:])
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.deleted[parts[0]] {
		deletedIssue(w, parts[0])
		return
	}
	for index, issue := range a.issues {
		if issue.ID != parts[0] {
			continue
		}
		if previous, ok := a.remarks[key]; key != "" && ok {
			if previous.IssueID != issue.ID || previous.Fingerprint != fingerprint {
				fail(w, 409, "idempotency_conflict", "This remark key was used with different content")
			} else {
				respond(w, 200, map[string]any{"data": issue, "replacementCurrent": issue.SceneRevision == fingerprint})
			}
			return
		}
		// Legacy photo-only records remain compatible. A model-bearing issue cannot
		// be downgraded by an old client into a mixed model/position/photo scene.
		complete := input.ReportVersion == 2 || input.Model != nil || issue.ModelURL != ""
		if complete && (input.ReportVersion != 2 || input.Model == nil) {
			fail(w, 400, "invalid_model", "Complete model replacement required")
			return
		}
		photo, err := validateSnapshot(input.Snapshot, complete || issue.SnapshotURL != "")
		if err != nil {
			fail(w, 400, "invalid_snapshot", err.Error())
			return
		}
		media := cloneMedia(a.media)
		models := cloneModels(a.models)
		if len(photo) > 0 {
			media[issue.ID] = photo
			issue.SnapshotURL = "/api/issues/" + issue.ID + "/snapshot"
		}
		if input.Model != nil {
			models[issue.ID], _ = json.Marshal(input.Model)
			issue.ModelURL = "/api/issues/" + issue.ID + "/model"
		}
		issue.SceneRevision = fingerprint
		issue.Position = point
		issue.UpdatedAt = time.Now().UTC()
		issues := append([]Issue{}, a.issues...)
		issues[index] = issue
		receipts := make(map[string]remarkReceipt, len(a.remarks)+1)
		for k, v := range a.remarks {
			receipts[k] = v
		}
		if key != "" {
			receipts[key] = remarkReceipt{fingerprint, issue.ID}
		}
		// One file replacement retires old bytes, including its temporary file. No
		// attachment or previous scene is copied into the durable replay receipt.
		if err := saveIssues(a.path, snapshot{Deleted: a.deleted, Issues: issues, Requests: a.requests, Media: media, Models: models, Remarks: receipts}); err != nil {
			fail(w, 500, "storage_error", "Could not save complete replacement; original data retained")
			return
		}
		a.issues = issues
		a.media = media
		a.models = models
		a.remarks = receipts
		a.notifySaved()
		respond(w, 200, map[string]any{"data": issue, "replacementCurrent": issue.SceneRevision == fingerprint})
		return
	}
	fail(w, 404, "not_found", "Issue not found")
}
