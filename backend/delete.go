package main

import (
	"encoding/json"
	"io"
	"net/http"
)

func deletedIssue(w http.ResponseWriter, id string) {
	respond(w, http.StatusGone, map[string]any{"error": map[string]string{"code": "issue_deleted", "message": "Issue was permanently deleted", "issueId": id}})
}

func (a *api) deleteIssue(w http.ResponseWriter, r *http.Request, id string) {
	var input struct {
		Title string `json:"title"`
	}
	decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 64*1024))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&input) != nil || decoder.Decode(new(any)) != io.EOF {
		fail(w, 400, "invalid_argument", "Confirm the current issue title")
		return
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.deleted[id] {
		respond(w, 200, map[string]any{"data": map[string]string{"id": id}})
		return
	}
	for index, issue := range a.issues {
		if issue.ID != id {
			continue
		}
		if input.Title != issue.Title {
			fail(w, 409, "title_conflict", "Issue title changed; refresh before deleting")
			return
		}
		// Retire legacy crash temporaries while the removed ID is still known.
		if err := cleanupSnapshotTemps(a.path, a.issues); err != nil {
			fail(w, 500, "storage_error", "Could not delete; original data retained")
			return
		}
		issues := append([]Issue{}, a.issues[:index]...)
		issues = append(issues, a.issues[index+1:]...)
		media, models := cloneMedia(a.media), cloneModels(a.models)
		delete(media, id)
		delete(models, id)
		deleted := make(map[string]bool, len(a.deleted)+1)
		for k, v := range a.deleted {
			deleted[k] = v
		}
		deleted[id] = true
		requests := make(map[string]creation, len(a.requests))
		for k, v := range a.requests {
			if v.Result != nil && v.Result.ID == id {
				v = creation{IssueID: id}
			}
			requests[k] = v
		}
		remarks := make(map[string]remarkReceipt, len(a.remarks))
		for k, v := range a.remarks {
			if v.IssueID != id {
				remarks[k] = v
			}
		}
		if err := saveIssues(a.path, snapshot{Issues: issues, Media: media, Models: models, Requests: requests, Remarks: remarks, Deleted: deleted}); err != nil {
			fail(w, 500, "storage_error", "Could not delete; original data retained")
			return
		}
		a.issues, a.media, a.models, a.requests, a.remarks, a.deleted = issues, media, models, requests, remarks, deleted
		a.notifySaved()
		respond(w, 200, map[string]any{"data": map[string]string{"id": id}})
		return
	}
	fail(w, 404, "not_found", "Issue not found")
}
