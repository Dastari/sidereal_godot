# Sidereal Godot operating contract

This repository is the native frontend only. The authoritative SpacetimeDB module, database,
and existing browser game remain in Dastari/sidereal_spacetime. The owner authorized this
native evaluation and separate public repository on 2026-10-04.

- Read the Sidereal wiki before architectural work. The current evaluation, design, operational
  runbook, and next gates live at https://wiki.sidereal.dastari.net/Architecture/Godot%20Frontend%20Evaluation.
  Update documentation through the sidereal-wiki MCP with expected_sha. Do not add local design,
  plan, handoff, or ADR Markdown files. README.md is an entry point; the wiki holds project documents.
  UI components/theme/input rules: https://wiki.sidereal.dastari.net/Architecture/Godot%20UI%20Framework.
  Model briefs, owner references, scale/pivots/sockets and claims:
  https://wiki.sidereal.dastari.net/Art/Godot%20Frontend%20Asset%20Tasks.
  The existing Opus 5.5 lane owns replacement characters/armor; coordinate its manifest rather than
  duplicating it. Preserve authored surfaces per the 2026-10-02 trial, while retaining logical grid,
  occupancy and authority. Interim scene assets and silhouettes are not final owner-approved art.
- Clients send intent; server reducers validate and own transforms, inventory, damage, and control.
  Subscribe only to actor-filtered views. Ownership alone grants no piloting authority.
- Keep stable character UUIDs and existing Dastari issuer/client identity. Native login uses the
  existing public game client with authorization code, PKCE S256, browser login, and exact loopback
  callback. Never store credentials or replace original provider tokens with SDK-issued tickets.
  Windows defaults and integration tests use the Sidereal Tailscale hostname, never the server's
  LAN address or loopback: live :8447, isolated test :8448, downloads :8446. Auth stays on
  auth.dastari.net. Test data must remain on the explicit isolated fixture, never the live world.
- Spatial values remain doubles in metres; subtract world origin before conversion to renderer
  floats. World XY maps to renderer X/-Z. Replicated construction coordinates are 1/32 metre units.
- Use Godot 4.7.2 .NET, .NET SDK 8.0.425, and SpacetimeDB.ClientSDK 2.10.0. Bindings are generated
  by the official CLI from the pinned backend schema; do not implement a custom wire protocol.
- Linux tools and downloads use python3 scripts/dev.py godot-*. Setup and export are reproducible.
  Never publish a backend from this repository or start/stop the existing game's services.
  Smoke tests require the exact deployed module artifact and SHA-256 on the isolated test listener.
- Keep .tools, .runtime, output, .godot, bin, obj, tokens, accounts, and database data outside git.
  The repository is public. Inspect staged files before publishing anything.
- Fetch and inspect branches/PRs before substantive work; use a feature branch and deliver via a
  GitHub PR. Do not push directly to main or merge without an explicit owner instruction.
- Validate native locked restore/build, focused console tests, and Godot import/export as relevant.
  Review visual changes in actual Godot. Backend authority changes also require its existing checks
  and isolated smoke. Distinguish implemented, scaffold, planned, and owner-approved in the wiki.
- When working alongside Sidereal agents on this server, use Agent Mail project key
  /root/sidereal_godot and reserve narrow paths before editing; follow the wiki's Agent Mail runbook.
