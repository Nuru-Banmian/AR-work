# Issue-owned scanned model upload (#24)

New phone reports use `POST /api/reports` with `reportVersion: 2`, required JPEG/PNG `snapshot` base64, ordinary text/world `position`, and required `model`. Legacy requests without reportVersion and legacy records remain readable and replayable without a model. Do not upgrade frozen old request bodies in place.

`model` version 1 contains flat `vertices` (metres, xyz), matching RGB byte `colors`, triangle `triangles` indices, and `marker: {position:{x,y,z}, normal:{x,y,z}}`. Coordinates are right-handed model-local: reflect Unity local Z, reverse each triangle's winding, transform selected world point/normal through the scan surface's world-to-local matrix before reflection. Unit outward normal supplies marker direction; the triangle-cone tip is at marker position, base 2.5 cm along the normal. Vertex colors describe captured surface colors, not photo textures. Unknown/absent surfaces remain holes.

The selected scan is copied at successful surface placement, then copied into the capture/report draft. Saving a new report writes the entire immutable body (model/photo/text) via the existing atomic durable queue before network work. No shared model-library path is referenced. New reports are frozen immediately; legacy editable pending reports retain their old behavior. Queue UI reports real identity-verification and transfer/receipt stages without inventing percentage progress. Completion requires both snapshotUrl and modelUrl in the receipt for version 2. Lost receipts/failures retain the same body/key; process restart preserves it.

The complete request budget is 32 MiB UTF-8, checked before phone enqueue and bounded by the server reader. Model limits: 90,000 vertices, 160,000 triangles, matching colors 0..255, finite local coordinates within +/-10 m, valid indices, complete explicit marker coordinates, unit normal, tip within 5 mm of an observed triangle. Null numbers, missing coordinates and invalid versions are rejected. Photos retain their existing 6 MiB / 24 megapixel limits.

The existing atomic file snapshot now has an optional `models` map keyed by issue ID. Issue metadata, photo, model and original idempotent receipt are committed by one fsync + replacement; only then update memory, notify SSE and respond. `Issue.modelUrl` is `/api/issues/{id}/model`; GET returns that model JSON (no-store). List/detail only contain the URL, not the mesh. Same-key content changes, including model changes, return 409. Old fingerprints remain unchanged for absent models. Each issue has its own model entry.

PC defaults to the existing photo and large-photo/retry flow. Switching to 3D dynamically imports the viewer and fetches the model. It supports captured color, cone visibility, rotate, right-drag pan, wheel zoom and reset. Request abort, instance isolation, controls/geometry/material/renderer disposal and WebGL context release happen on unmount/revision change. Legacy records show `旧记录无模型`. There is no PC model library.

## Validation (2026-10-27)

- Go full tests and vet passed. HTTP cases cover complete create, restart/receipt replay, changed-model conflict, concurrent replay, invalid/null/missing coordinates, failure during atomic save with no list visibility, and a >12 MiB supported maximum-dimension mesh plus near-limit real PNG request.
- Unity ModelQueueChecks passed: transformed known local point/normal, handedness/winding, independent photo/model after scanning mutation, restart, frozen editing rejection and failure replay. ReceiverQueueChecks real HTTP passed, including incomplete model receipt rejection and full receipt success after queue reconstruction. Navigation 50 checks passed.
- Playwright browser passed: same fixture issue ID `45dfab5ce6d01009aeb3c73b9a6ff720` has both photo/model URLs, photo default/large-photo, model rotate/pan/zoom/reset/color/marker controls, 503 model failure/retry, photo failure/retry, canvas removal when returning to photo, and legacy no-model notice. This is an isolated known-geometry fixture, not a real scan acceptance claim.
- Android IL2CPP ARM64 build passed; APK ZIP/7 native libraries/16 KiB alignment passed. APK 28,094,348 bytes, SHA256 `faeb3b83980613dba3be2abef9d80d29fea26a2fe5feff9d5cb58133076b63bd`.
- ADB lists no device. Real scan/photo/model/marker correspondence on the same issue ID, physical marker fit, flight-mode acquisition, app restart and LAN retransmission are **not verified**. #24 stays open until those checks pass.

## Handoff to #25

Use the same versioned model DTO, local `FreezeIssueModel()` and `modelUrl` contract. Re-mark must atomically replace photo + model + local marker and preserve identity/content/status, clearing old attachments only after durable success. Current legacy position PATCH is not yet the new replacement protocol. Extend the shared snapshot transaction with `Models`; every mutation must preserve it. Deletion must remove both media/model entries and scrub attachment-bearing receipt results, retaining only minimal tombstone/dedup data so creates/re-marks cannot revive deleted issues. Do not delete the independent phone SurfaceScanStore library.

Necessary previously untracked scan foundations were preserved in separate commit `564acfc`; original unrelated README/AI_LOG and XR simulation artifacts were left untouched. No push/merge or parent closure was performed.
