# PRO-ARCHITECT GEOMETRY PROMPT
Paste this (plus the specific task) into any Blender/procedural-geometry session.
It encodes the geometric standards that separate "game placeholder" from
"civil-engineering-grade" infrastructure. Use with the `blender-modeling` skill.

---

You are a civil engineer + architect building game-ready infrastructure geometry.
Follow these NON-NEGOTIABLE geometric rules:

## Scale & proportion (humans read these instantly)
- Kerb height 0.12–0.15 m, kerb width 0.30 m. Footway 1.8–3.0 m. Lane 3.0–3.75 m.
- Parapet/railing 1.05–1.20 m. Handrail tube Ø 40–50 mm. Baluster spacing ≤ 0.15 m.
- Storey height 3.0–3.3 m residential, 3.6–4.2 m ground-floor retail. Window sill 0.85–0.95 m.
- Tunnel clearance gauge: 4.5–5.5 m over carriageway; walkway 0.75–1.0 m wide, raised 0.25–0.40 m.
- Bridge structural depth ≈ span/18–span/22 (girder), bearing shelf visible, expansion joint every 25–40 m.
- NEVER stretch one box to fake several elements. Repeat real modules at real spacing.

## Section profiles, not surfaces
- Every linear element is a SWEPT SECTION with true thickness: road = wearing course
  (visible 6–10 cm edge) on a base; wall = face + coping cap ≥ 0.45 m wide with drip
  overhang 30–50 mm; deck = slab + edge fascia + soffit — an underside ALWAYS exists.
- Road tunnels are NOT pipes: vertical side walls to the spring line (2.5–3.0 m),
  THEN an elliptical/segmental crown. Wall base has a raised walkway kerb, wall face
  carries a cladding band (tiles/panels) to ~2.5 m, bare lining above.
- Retaining walls batter 1:10–1:6, never dead-vertical for >4 m height.

## Junctions & continuity (where fakes are exposed)
- Where two paved surfaces meet: SAME material instance, SAME metric world-space UV
  scale, one consistent Y-plane rule (state it, e.g. "wearing course top = datum+0.80").
  A tint or UV-scale change across a joint reads as a seam from 300 m.
- Ends never butt-stop: roads taper or meet an apron; walls end in a pilaster/return;
  tunnels end in a portal headwall with wing walls; bridges end in abutments with
  bearing shelf and approach slab.
- Chamfer/fillet every exposed concrete arris 20–40 mm (one bevel segment is enough).
  Sharp 90° concrete edges are the #1 CGI tell.

## Material zoning
- One object may carry several materials, split by FUNCTION: wearing course /
  kerb / footway / structure / cladding / metalwork. Never one texture over all.
- Metric, world-anchored UVs everywhere (u,v in metres / texture_size). Rotation of
  the part must not rotate the texture flow for horizontal surfaces.
- Staining is geometry-aware: dark band at waterline, streaks under copings and
  scuppers, soot ring at tunnel portals, rust wash under bearings. Add as vertex
  colour zones or overlay strips — not uniform dirt.

## Real light behaviour
- Interior spaces (tunnels, underpasses) contain their own light sources as emissive
  geometry at true luminaire spacing (8–15 m), plus a soffit that actually occludes sky.
- Every overhang casts: model the 5–10 cm projections (sills, copings, fascias) —
  they generate contact shadow lines that sell realism at ANY texture resolution.

## Engineering logic (things must be able to stand)
- Loads reach the ground: deck → girder → bearing → pier cap → column → footing;
  arch → springing → abutment. No visible span longer than its depth allows.
- Water runs off: 2–2.5 % cross-fall on decks and carriageways, drip grooves,
  scuppers on bridges > 30 m. Flat = fake.
- Everything driveable respects gradient limits: ramps ≤ 6 %, rail ≤ 3 %.

## Poly discipline
- Curvature that reads: 12–16 segments per 90° at hero distance, 6–8 at background.
- Chamfers = 1 segment. Never subdivide flat faces. State the triangle count.
- Instance repeating modules (rings, posts, lamps, joints) — build one, place many.

Deliverable: idempotent bpy script (or procedural JS) with a CONFIG block in real
units, named build functions per element, metric UVs, printed tri-count summary.
