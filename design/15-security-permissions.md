# 15 Security and permissions

The permission model of PLAN v0.2 §6 stands (levels, policy evaluation order, `ask` flow, presets, ConfigNode file, audit, kill switch). This file lists what changes, what is added, and the threat model for a multi-actor system.

## 1. Levels and defaults (D11)

| Level | Default (preset **Battle Lab**, applied on first run for the local user) |
|---|---|
| `read` | allow |
| `control` | allow (flight, editor, camera, AI/WM params, orders, leases, recorder, time scale, **[R]** vessel rename, staging of non-weapon stages) |
| `destructive` | **ask** (**[R2]** surrendering a whole side (`22-communications-diplomacy.md` §10) unless the scenario sets `agentsMaySurrender`, manual weapon fire, staging a stage that fires a weapon or strands crew (resolver in `06-flight-control.md` §2), remove vessels, overwrite craft, save/load game) — the user can switch individual capabilities to allow (e.g. `bda.fire_*` for the test range) |
| `cheat` | allow for `bdarmory` spawn/settings, `ksp.vessel.place`, **[R2]** `ksp.vessel.spawn_craft`/`spawn_formation` (any count, spawn anywhere, `06-flight-control.md` §9.1), `sim.respawn.*`, `sim.*`, library staging; ask for `INFINITE_*`/gravity hacks |
| `unsafe` | deny (deep reflection set/call, writes outside sandboxes); `reflect.get` per mod via the in-game UI only |

Areas: all on except `deep`. Presets `Observer`, `Test Director`, `Full Combat`, `Battle Lab` (new default). Nothing over the wire changes areas or permissions; `permissions.request` only shows a prompt.

## 2. Actors and per-role clamps — [ADDED]

**[R2] Roles follow the pass 2 command model** (`09-c2-multi-agent.md` §0–1): one commander per side (a human or one agent), advisors, delegates spawned by the commander's AI client, scripts and behaviours. Pass 1's `unit` and `group` roles are gone with the built-in agent layers. Because several actors can share one bridge (a commander and its subagents on one MCP connection), the client-level cap (`CLIENT` rule) is not enough, and the permission file has `ROLE` rules:

```
ROLE { name = commander levels = read, control, destructive, cheat   god = per-scenario   maxPrecedence = 800   bulkOps = ask   seat = true }
ROLE { name = advisor   levels = read, control   god = false   channels = none   maxPrecedence = 0     comms = team, direct }
ROLE { name = delegate  levels = read, control   god = false   channels = per-delegation   maxPrecedence = 700   scope = cluster   comms = team, direct }
ROLE { name = observer  levels = read            god = per-session   maxPrecedence = 0 }
ROLE { name = script    levels = read, control   god = per-session  channels = steer, formation  maxPrecedence = 500 }
ROLE { name = behaviour levels = read, control   god = false  channels = steer, weapons, targeting, sensors, ai_params, formation  maxPrecedence = 400 }
ROLE { name = harness   levels = read, control, cheat   god = true  channels = *  maxPrecedence = 500 }
ROLE { name = human     levels = *   god = true   maxPrecedence = 1000 }
```

- **Commander.** Only the seat holder may change stance, surrender, open the diplomatic channel, broadcast, create delegations and change side doctrine. An agent with the `commander` role that does not hold the seat acts as an `advisor`.
- **Delegate.** Its channels and cluster come from its delegation. Every call outside the cluster is `PERMISSION_DENIED`/`out_of_scope`. Delegation tokens are 128-bit, shown once, stored hashed, revocable, never readable through any capability or recording, and expire with the delegation.
- **Advisor.** Read access, the team channel and proposals only. It holds no leases.
- **Commander god view.** `god = per-scenario` means fog by default for agent commanders. The scenario or the permissions window can grant god view to the local user's own agent. Agent-vs-agent scenarios keep fog.

**[R]** Pass 1.0 called the field `preemptMax` and set the script cap at 400, which made the documented "doctrine promotes a trusted script" impossible. The field is now `maxPrecedence` everywhere.

- **Effective precedence** is `min(doctrine or default band, ROLE maxPrecedence)`, and a client can only lower it.
- **The effective permission decision** is `min(file policy, CLIENT cap, ROLE cap)`.
- **God view** (`tac.god`) is a per-role flag, so delegates and advisors cannot read through fog even though the data is `read` level.
- **Actor identities** are asserted by the bridge: it knows which MCP session, header or `as` token is which actor. Scripts declare their own identity. The host trusts the loopback connection holder but still clamps by role and client caps, so a mis-declared role cannot exceed what the file allows for that client. A delegate identity always requires its token.

## 3. Bulk and two-key operations — [ADDED]

`bda.remove_vessels all`, `sim.scenario.load` with `killEverythingFirst`, `ksp.game.load`, deleting library versions and recordings require either an in-game confirmation **or** a second actor's co-sign (`opts.cosign: {actor, token}`, e.g. the batch runner plus the human who started it), configurable per capability (`twoKey = game|cosign|either`). Defaults: `game` for the human's interactive sessions, `cosign` for unattended batch runs that were started from the in-game window ("arm batch mode for 8 hours").

## 4. Socket security (unchanged, restated)

Loopback only; 256-bit token in `PluginData/auth.token`, constant-time compare; `Origin` header rejected unless allow-listed (browser attack); `Host` check (DNS rebinding); per-session and per-actor rate limits; message/depth/queue caps; idle timeout; slow-consumer disconnect. No LAN binding in v1: a second human on another PC uses an SSH tunnel to the loopback port (documented); LAN/TLS is P3 and needs a per-client certificate design.

## 5. Prompt injection and untrusted text

- Vessel names, craft descriptions, log lines, map annotations and agent-written notes are **data**: never placed into tool descriptions or system prompts by the bridge; compact text escapes control characters; names are truncated to 48 chars.
- **[R2]** Team messages and reports travel as `comms` messages with the authenticated sender; the bridge returns them as data in tool results, never in tool descriptions or prompts.
- **[R2] Messages from other sides are adversarial by design** (negotiation, threats, bluffs, psyops; `22-communications-diplomacy.md` §3, §10). They are delivered marked `untrusted:true` with a fixed provenance line. In agent-vs-agent scenarios the scenario runner and `skizzy doctor` warn when an agent commander also has non-Skizzy tools (files, shell); the docs show how to run commanders with only the Skizzy MCP tools or in a sandbox. Risk R31.
- Permissions are host-side, so an injected instruction cannot exceed the role's grants; destructive actions still hit `ask` or two-key.
- **[R2]** The blast radius of a confused or manipulated agent is bounded by its ROLE (a delegate only touches its cluster), host-side `ask` levels and the kill switch; the optional API-key example adds turn and dollar caps (`09-c2-multi-agent.md` §8.5).

## 6. Safeguards in play

- Kill switch (hotkey, toolbar, map, API) with the semantics in `06-flight-control.md` §10.
- ROE guards and zones evaluated in-game, not by the LLM (`09-c2-multi-agent.md` §2.3); `requireIff` default on under fog.
- Friendly-fire check before manual fire (`CheckSafeToFireGuns`, missile path vs friendlies within `minRangeToFriendlyM`); the check is advisory for BDA's own guard mode (BDA already has its own) and blocking for Skizzy-issued fire.
- Lease TTLs and fallbacks make every automation fail to BDA default behaviour, never to a frozen stick.
- Path sandbox: `Skizzy/`, `AutoSpawn/`, `saves/<profile>/Ships/`, `GameData/SkizzyProtocol/PluginData/`, read-only `GameData/BDArmory/Logs/` and `KSP.log`; file names restricted; no symlink following.
- In-game C# behaviour plug-ins are trusted code: documented, loaded only from the Behaviours folder, and listed with hashes in `skizzy doctor`.

## 7. Audit

Every call (allowed or not), every lease change, every order transition, **[R2]** every seat change, delegation, stance change, violation and comms message (with true sender), and client notes appended through `rec.append` is in the audit JSONL with actor, trace id and `basedOnSeq`. The map's events panel shows it live; `skizzy://audit/recent` reads it (own session unless `commander`/`human`).

## 8. Security checklist (release gate)

Browser connection refused (Origin); DNS rebinding refused (Host); token-less and bad-token connections closed; role clamps enforced in a test where a `delegate` actor requests god view and `cheat` and acts outside its cluster (**[R2]**); a delegation token is refused after revocation and is never readable through any capability, log or recording; a spoofed broadcast callsign never changes the authenticated sender on team or diplomatic channels; path traversal rejected; 10 k-case fuzz corpus without a host exception; token never readable through any capability or recording; two-key enforced for `remove_vessels all` in batch mode; kill switch releases all Skizzy control within one physics step while BDA guard stays up, and on the active vessel the BDA pilot is off so the human has the stick (**[R]**); the kill switch can be released only from the in-game UI or by a `human` actor.
