package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
)

// A single process owns this snapshot. Scoped temporaries are ours; legacy
// .issues-<decimal>.tmp files are retired only if they contain a matching issue.
// Backups, other datasets, directories and malformed files are left alone.
func cleanupSnapshotTemps(path string, issues []Issue) error {
	entries, err := os.ReadDir(filepath.Dir(path))
	if os.IsNotExist(err) {
		return nil
	}
	if err != nil {
		return err
	}
	ids := map[string]bool{}
	for _, issue := range issues {
		ids[issue.ID] = true
	}
	scoped := ".issues-" + filepath.Base(path) + "-"
	for _, entry := range entries {
		if entry.IsDir() {
			continue
		}
		name := entry.Name()
		own := generatedTemp(name, scoped)
		if !own && !generatedTemp(name, ".issues-") {
			continue
		}
		target := filepath.Join(filepath.Dir(path), name)
		if !own {
			data, err := os.ReadFile(target)
			if err != nil {
				return err
			}
			var stored snapshot
			if json.Unmarshal(data, &stored) != nil {
				continue
			}
			for _, issue := range stored.Issues {
				if ids[issue.ID] {
					own = true
					break
				}
			}
		}
		if own {
			if err := os.Remove(target); err != nil {
				return err
			}
		}
	}
	return nil
}

func generatedTemp(name, prefix string) bool {
	if !strings.HasPrefix(name, prefix) || !strings.HasSuffix(name, ".tmp") {
		return false
	}
	number := strings.TrimSuffix(strings.TrimPrefix(name, prefix), ".tmp")
	if number == "" {
		return false
	}
	for _, r := range number {
		if r < '0' || r > '9' {
			return false
		}
	}
	return true
}
