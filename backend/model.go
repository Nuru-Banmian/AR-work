package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"math"
	"net/http"
	"strings"
)

type ModelMarker struct {
	Position Position `json:"position"`
	Normal   Position `json:"normal"`
}

// Metres, right-handed coordinates: Unity local (x,y,-z), reversed triangle winding.
type ScanModel struct {
	Version   int          `json:"version"`
	Vertices  []float64    `json:"vertices"`
	Colors    []int        `json:"colors"`
	Triangles []int        `json:"triangles"`
	Marker    *ModelMarker `json:"marker"`
}

func validateModel(m *ScanModel, version int) error {
	if version != 0 && version != 2 {
		return errors.New("unsupported report version")
	}
	if m == nil {
		if version == 2 {
			return errors.New("model required for report version 2")
		}
		return nil
	}
	if m.Version != 1 || len(m.Vertices) < 9 || len(m.Vertices)%3 != 0 || len(m.Vertices) > 270000 || len(m.Colors) != len(m.Vertices) || len(m.Triangles) < 3 || len(m.Triangles)%3 != 0 || len(m.Triangles) > 480000 || m.Marker == nil {
		return errors.New("invalid model dimensions or marker")
	}
	for _, v := range m.Vertices {
		if math.IsNaN(v) || math.IsInf(v, 0) || math.Abs(v) > 10 {
			return errors.New("invalid model coordinate")
		}
	}
	for _, v := range m.Colors {
		if v < 0 || v > 255 {
			return errors.New("invalid vertex color")
		}
	}
	for i := 0; i < len(m.Triangles); i += 3 {
		a, b, c := m.Triangles[i], m.Triangles[i+1], m.Triangles[i+2]
		if a < 0 || b < 0 || c < 0 || a >= len(m.Vertices)/3 || b >= len(m.Vertices)/3 || c >= len(m.Vertices)/3 || a == b || b == c || a == c {
			return errors.New("invalid triangle index")
		}
	}
	p, n := m.Marker.Position, m.Marker.Normal
	for _, v := range []float64{p.X, p.Y, p.Z, n.X, n.Y, n.Z} {
		if math.IsNaN(v) || math.IsInf(v, 0) || math.Abs(v) > 10 {
			return errors.New("invalid marker")
		}
	}
	length := n.X*n.X + n.Y*n.Y + n.Z*n.Z
	if math.Abs(length-1) > 0.01 {
		return errors.New("marker normal must be unit length")
	}
	// Marker tip must lie on an observed triangle, within 5 mm (not just within its bounding box).
	if !markerOnSurface(m) {
		return errors.New("marker is not on observed surface")
	}
	return nil
}
func markerOnSurface(m *ScanModel) bool {
	p := m.Marker.Position
	for i := 0; i < len(m.Triangles); i += 3 {
		v := func(k int) Position {
			j := m.Triangles[i+k] * 3
			return Position{m.Vertices[j], m.Vertices[j+1], m.Vertices[j+2]}
		}
		a, b, c := v(0), v(1), v(2)
		u, w, q := sub(b, a), sub(c, a), sub(p, a)
		normal := cross(u, w)
		nn := dot(normal, normal)
		if nn < 1e-20 {
			continue
		}
		d := dot(q, normal)
		if d*d > 0.005*0.005*nn {
			continue
		}
		uu, uw, ww, qu, qw := dot(u, u), dot(u, w), dot(w, w), dot(q, u), dot(q, w)
		den := uu*ww - uw*uw
		x, y := (ww*qu-uw*qw)/den, (uu*qw-uw*qu)/den
		if x >= -0.005 && y >= -0.005 && x+y <= 1.005 {
			return true
		}
	}
	return false
}
func sub(a, b Position) Position { return Position{a.X - b.X, a.Y - b.Y, a.Z - b.Z} }
func dot(a, b Position) float64  { return a.X*b.X + a.Y*b.Y + a.Z*b.Z }
func cross(a, b Position) Position {
	return Position{a.Y*b.Z - a.Z*b.Y, a.Z*b.X - a.X*b.Z, a.X*b.Y - a.Y*b.X}
}
func cloneModels(source map[string]json.RawMessage) map[string]json.RawMessage {
	result := map[string]json.RawMessage{}
	for k, v := range source {
		result[k] = v
	}
	return result
}
func (a *api) readModel(w http.ResponseWriter, r *http.Request) {
	if r.Method != "GET" {
		fail(w, 405, "method_not_allowed", "Use GET")
		return
	}
	id := strings.TrimSuffix(strings.TrimPrefix(r.URL.Path, "/api/issues/"), "/model")
	a.mu.Lock()
	if a.deleted[id] {
		a.mu.Unlock()
		deletedIssue(w, id)
		return
	}
	data, ok := a.models[id]
	a.mu.Unlock()
	if !ok {
		fail(w, 404, "not_found", "Model not found")
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.Write(data)
}

// JSON null is not a coordinate or index. Do not let encoding/json silently
// replace missing or damaged source numbers with zero before validation.
func (m *ScanModel) UnmarshalJSON(data []byte) error {
	type plain ScanModel
	var decoded plain
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&decoded); err != nil {
		return err
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(data, &fields); err != nil {
		return err
	}
	for _, name := range []string{"vertices", "colors", "triangles"} {
		var values []json.RawMessage
		if err := json.Unmarshal(fields[name], &values); err != nil {
			return err
		}
		for _, v := range values {
			if string(v) == "null" {
				return errors.New("model numeric arrays cannot contain null")
			}
		}
	}
	var marker map[string]json.RawMessage
	if err := json.Unmarshal(fields["marker"], &marker); err != nil {
		return err
	}
	for _, name := range []string{"position", "normal"} {
		var point map[string]json.RawMessage
		if err := json.Unmarshal(marker[name], &point); err != nil {
			return err
		}
		for _, axis := range []string{"x", "y", "z"} {
			v := point[axis]
			if len(v) == 0 || string(v) == "null" {
				return errors.New("complete marker coordinates required")
			}
		}
	}
	*m = ScanModel(decoded)
	return nil
}
