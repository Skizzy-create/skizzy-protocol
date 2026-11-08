# 15 Security and permissions

The permission model of PLAN v0.2 §6 stands (levels, policy evaluation order, `ask` flow, presets, ConfigNode file, audit, kill switch). This file lists what changes, what is added, and the threat model for a multi-actor system.

## 1. Levels and defaults (D11)

| Level | Default (preset **Battle Lab**, applied on first run for the local user) |
|---|---|
| `read` | allow |
| `control` | allow (flight, editor, camera, AI/WM params, orders, leases, recorder, time scale, **[R]** vessel rename, staging of non-weapon stages) |
| `destructive` | **ask** (manual weapon fire, staging a stage that fires a weapon or strands crew (resolver in `06-flight-control.md` §2), remove vessels, overwrite craft, save/load game) — the user can switch individual capabilities to allow (e.g. `bda.fire_*` for the test range) |
| `cheat` | allow for `bdarmory` spawn/settings, `ksp.vessel.place`, `sim.*`, library staging; ask for `INFINITE_*`/gravity hacks |
| `unsafe` | deny (deep reflection set/call, writes outside sandboxes); `reflect.get` per mod via the in-game UI only |

Areas: all on except `deep`. Presets `Observer`, `Test Director`, `Full Combat`, `Battle Lab` (new default). Nothing over the wire changes areas or permissions; `permissions.request` only shows a prompt.

## 2. Actors and per-role clamps — [ADDED]

Because several agents share one bridge, the client-level cap (`CLIENT` rule) is not enough. The permission file gains `ROLE` rules:

```
ROLE { name = unit      levels = read, control   god = false  channels = steer, weapons, targeting, sensors, ai_params  maxPrecedence = 600 }
ROLE { name = group     levels = read, control   god = false  channels = *   maxPrecedence = 700 }
ROLE { name = commander levels = read, control, destructive, cheat   god = true   maxPrecedence = 800   bulkOps = ask }
ROLE { name = script    levels = read, control   god = per-session  channels = steer, formation  maxPrecedence = 500 }
ROLE { name = behaviour levels = read, control   god = false  channels = steer, weapons, targeting, sensors, ai_params, formation  maxPrecedence = 400 }
ROLE { name = harness   levels = read, control, cheat   god = true  channels = *  maxPrecedence = 500 }
ROLE { name = human     levels = *   god = true   maxPrecedence = 1000 }
```

**[R]** Pass 1.0 called the field `preemptMax` and set the script cap at 400, which made the documented "doctrine promotes a trusted script above unit agents" impossible. The field is now `maxPrecedence` everywhere; scripts may be promoted up to 500 by doctrine and further only by editing this file. Effective precedence = `min(doctrine or default band, ROLE maxPrecedence)`; a client can only lower it. The effective permission decision is `min(file policy, CLIENT cap, ROLE cap)`. God view (`tac.god`) is a per-role flag, so unit agents cannot read through fog even though the data is `read` level. Actor identities are asserted by the bridge (it knows which MCP session is which agent) and by scripts (self-declared); the host trusts the loopback connection holder but still clamps by role and client caps, so a mis-declared role cannot exceed what the file allows for that client.

## 3. Bulk and two-key operations — [ADDED]

`bda.remove_vessels all`, spawning more than 50 vessels, `sim.scenario.load` with `killEverythingFirst`, `ksp.game.load`, deleting library versions and recordings require either an in-game confirmation **or** a second actor's co-sign (`opts.cosign: {actor, token}`, e.g. the batch runner plus the human who started it), configurable per capability (`twoKey = game|cosign|either`). Defaults: `game` for the human's interactive sessions, `cosign` for unattended batch runs that were started from the in-game window ("arm batch mode for 8 hours").

## 4. Socket security (unchanged, restated)

Loopback only; 256-bit token in `PluginData/auth.token`, constant-time compare; `Origin` header rejected unless allow-listed (browser attack); `Host` check (DNS rebinding); per-session and per-actor rate limits; message/depth/queue caps; idle timeout; slow-consumer disconnect. No LAN binding in v1: a second human on another PC uses an SSH tunnel to the loopback port (documented); LAN/TLS is P3 and needs a per-client certificate design.

## 5. Prompt injection and untrusted text

- Vessel names, craft descriptions, log lines, map annotations and agent-written notes are **data**: never placed into tool descriptions or system prompts by the bridge or orchestrator; compact text escapes control characters; names are truncated to 48 chars.
- Agent-to-agent messages (commander → unit) travel as orders/notes with the issuing actor id; the orchestrator renders them as quoted data inside the user turn, not as system instructions.
- Permissions are host-side, so an injected instruction cannot exceed the role's grants; destructive actions still hit `ask` or two-key.
- The orchestrator's budgets cap the blast radius of a confused agent (turns/minute, dollars).

## 6. Safeguards in play

- Kill switch (hotkey, toolbar, map, API) with the semantics in `06-flight-control.md` §10.
- ROE guards and zones evaluated in-game, not by the LLM (`09-c2-multi-agent.md` §2.3); `requireIff` default on under fog.
- Friendly-fire check before manual fire (`CheckSafeToFireGuns`, missile path vs friendlies within `minRangeToFriendlyM`); the check is advisory for BDA's own guard mode (BDA already has its own) and blocking for Skizzy-issued fire.
- Lease TTLs and fallbacks make every automation fail to BDA default behaviour, never to a frozen stick.
- Path sandbox: `Skizzy/`, `AutoSpawn/`, `saves/<profile>/Ships/`, `GameData/SkizzyProtocol/PluginData/`, read-only `GameData/BDArmory/Logs/` and `KSP.log`; file names restricted; no symlink following.
- In-game C# behaviour plug-ins are trusted code: documented, loaded only from the Behaviours folder, and listed with hashes in `skizzy doctor`.

## 7. Audit

Every call (allowed or not), every lease change, every order transition and every agent turn summary (from the orchestrator through `rec.append`) is in the audit JSONL with actor, trace id and `basedOnSeq`. The map's events panel shows it live; `skizzy://audit/recent` reads it (own session unless `commander`/`human`).

## 8. Security checklist (release gate)

Browser connection refused (Origin); DNS rebinding refused (Host); token-less and bad-token connections closed; role clamps enforced in a test where a `unit` actor requests god view and `cheat`; path traversal rejected; 10 k-case fuzz corpus without a host exception; token never readable through any capability or recording; two-key enforced for `remove_vessels all` in batch mode; kill switch releases all Skizzy control within one physics step while BDA guard stays up, and on the active vessel the BDA pilot is off so the human has the stick (**[R]**); the kill switch can be released only from the in-game UI or by a `human` actor.
