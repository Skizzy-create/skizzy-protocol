# skizzy-protocol: specification draft 0.1

**Wire format and contract API** for making Kerbal Space Program 1.12.x and its mods controllable by AI agents and other programs.

| | |
|---|---|
| Status | **Draft 0.1**, unstable; anything may change before 1.0 |
| Date | 2026-11-07 |
| Companion | [`PLAN.md`](PLAN.md): architecture, rationale and roadmap |
| Licence | Spec text CC-BY 4.0. Code listings in §12.1 and §12.4 are CC0 1.0. |

The key words MUST, MUST NOT, SHOULD, SHOULD NOT and MAY are used as in RFC 2119.

"skizzy-protocol" is the working name, with "Kerbal Uplink" proposed (see PLAN §2). Only the handshake identifier (§5.1) carries the name. A host MUST accept both `"skizzy-protocol"` and any alias it advertises.

---

## 1. Roles

| Role | Description |
|---|---|
| **Host** | The in-game plugin (`Skizzy.Host.dll`). It owns the socket, the catalog, the permission engine, the audit log and the main-thread dispatcher. There is exactly one per KSP process. |
| **Provider** | A component that contributes capabilities to the host's catalog. It can be built in (`ksp.*`, `host.*`), Tier 0 (reflection), Tier 1 (annotations), Tier 2 (duck-typed attributes in a mod) or Tier 3 (a contract DLL or the JSON-delegate ABI). |
| **Client** | Anything that connects to the host over the wire: an MCP bridge, a script, a dashboard, a test runner. |
| **Bridge** | A client that re-exposes the host through another protocol, such as MCP (§14). |

## 2. Concepts

- **Capability.** A named, typed operation, for example `bda.start_competition`. It has an input schema, an optional output schema, a safety level, scene availability, thread affinity, and flags (`longRunning`, `idempotent`).
- **Resource.** Read-only data addressed by URI or URI template, for example `ksp://vessel/{handle}`.
- **Stream.** A named, sampled data source delivered as `stream.data` notifications, for example `bda.engagement`.
- **Event.** A named, discrete occurrence delivered as `event` notifications and kept in a ring buffer, for example `bda.vessel_killed`.
- **Operation.** The handle (`op:N`) for a long-running capability call.
- **Catalog.** The set of providers, capabilities, resources, streams and events, with a monotonically increasing `revision`.
- **Handle.** An opaque string that references a game object (§9).

---

## 3. Transport

### 3.1 Endpoint
- WebSocket (RFC 6455) at `ws://127.0.0.1:<port>/skizzy`. The default port is **47549**.
- A host MUST bind only to loopback (`127.0.0.1`, optionally `::1`). Binding to other interfaces is out of scope for 0.x.
- Subprotocol: a client SHOULD offer `Sec-WebSocket-Protocol: skizzy.v0`. A host MUST select `skizzy.v0` if offered.

### 3.2 Authentication and request hygiene
1. The host generates a token of at least 256 bits of CSPRNG output, base64url-encoded. It stores the token at `GameData/Skizzy/PluginData/auth.token`, readable by the local user.
2. A client MUST present the token in the upgrade request as `Authorization: Bearer <token>`.
3. If a client cannot set headers, it MAY instead send the token as `params.token` in `session.hello`. The host MUST then accept no other method before a successful `session.hello`, and MUST close the connection after one failed attempt.
4. The host MUST reject (HTTP 403) any upgrade request that carries an `Origin` header not in its configured allow-list. The allow-list is empty by default, which blocks web pages from reaching the socket.
5. The host MUST reject any upgrade whose `Host` header is not `127.0.0.1:<port>`, `localhost:<port>` or `[::1]:<port>`. This defeats DNS rebinding.
6. Token comparison MUST be constant-time.

### 3.3 Framing and limits
- Each WebSocket text frame (or reassembled message) contains exactly **one** JSON-RPC 2.0 object. JSON-RPC batch arrays are not supported in 0.x, and the host returns `-32600`.
- Encoding is UTF-8. Hosts advertise their limits in `session.hello` and MUST enforce them. Defaults:

| Limit | Default |
|---|---|
| `maxMessageBytes` | 4 194 304 |
| `maxJsonDepth` | 64 |
| `maxConnections` | 4 |
| `maxInflight` (per session) | 32 |
| `maxSubscriptions` (per session) | 64 |
| `idleTimeoutSec` | 600 |

- Hosts SHOULD send WebSocket pings every 30 s. Clients MUST answer with pongs (standard behaviour of WebSocket libraries).

---

## 4. Message envelope

All messages are JSON-RPC 2.0. Requests carry `id`. Notifications carry no `id`; both directions may send them, though in 0.x only the host sends notifications.

**Common request option** `params.opts`, which is optional on every request:

| Key | Type | Meaning |
|---|---|---|
| `timeoutMs` | integer | Wall-clock cap for the request. Long operations still return `accepted` immediately. |
| `waitMs` | integer | For `longRunning` capabilities: wait up to this long for completion before returning `accepted`. Default 0. |
| `idempotencyKey` | string | The host returns the cached result of an earlier call with the same key (in the same session, for 10 minutes). |
| `dryRun` | boolean | Validate and run the permission check, but do not execute. Returns `{status:"dry_run", wouldRequire:{...}}`. |
| `reason` | string | Human-readable reason shown in confirmation prompts. Labelled as client-supplied. |

**Result metadata.** Every successful result object MUST include `meta`:

```json
"meta": { "ut": 204511.32, "fixedFrame": 9123345, "scene": "FLIGHT", "paused": false,
          "warp": { "mode": "none", "rate": 1 }, "catalogRevision": 42 }
```

`warp.mode` is one of `none`, `physics` or `rails`.

---

## 5. Session lifecycle

```
connect (WS upgrade + auth) ─► session.hello ─► catalog.get ─► { cap.call | stream.* | events.* | op.* }* ─► session.bye / close
                                    ▲                                    │
                                    └──────── catalog.changed ◄──────────┘
```

### 5.1 `session.hello` (MUST be the first request)

Request:

```json
{"jsonrpc":"2.0","id":1,"method":"session.hello","params":{
  "protocol":"skizzy-protocol",
  "versions":["0.1"],
  "client":{"name":"skizzy-mcp","version":"0.1.0","kind":"mcp-bridge"},
  "features":["ops","streams","events","events.poll","confirm.client"]
}}
```

`client.kind` is one of `mcp-bridge`, `direct`, `test-runner` or `dashboard`.

Response:

```json
{"jsonrpc":"2.0","id":1,"result":{
  "protocol":"skizzy-protocol", "aliases":["uplink"], "version":"0.1",
  "session":"s-7f3a2c",
  "host":{"name":"Skizzy.Host","version":"0.1.0","ksp":"1.12.5","kspBuild":"03190"},
  "features":["ops","streams","events","events.poll","confirm.game","confirm.client"],
  "limits":{"maxMessageBytes":4194304,"maxInflight":32,"maxSubscriptions":64,"maxConnections":4},
  "catalogRevision":42,
  "grants":{"summary":[{"mod":"Squad","read":"allow"},{"mod":"BDArmory","read":"allow","control":"allow","cheat":"allow","destructive":"ask"}]},
  "meta":{"ut":204511.32,"fixedFrame":9123345,"scene":"FLIGHT","paused":false,"warp":{"mode":"none","rate":1},"catalogRevision":42}
}}
```

**Negotiation rules:**
- The host picks the highest version in `params.versions` that it supports. If there is none, it returns `VERSION_UNSUPPORTED` with `data.supported`.
- `features` in the response is the **intersection** of what the client requested and what the host supports. Unknown features MUST be ignored.

**Feature registry (0.1):**

| Feature | Meaning |
|---|---|
| `ops` | Long-running operations (§6.4) |
| `streams` | Stream subscriptions (§6.5) |
| `events` | Event subscriptions (§6.6) |
| `events.poll` | Cursor polling of the event ring buffer |
| `confirm.game` | The host can ask the human in game |
| `confirm.client` | The client can collect human confirmation (for example MCP elicitation) and return a confirmation token (§8.4) |
| `deepReflection` | `reflect.*` methods exist (they are still permission-gated) |
| `lockstep` | `sim.hold`, `sim.step` and `sim.release` (reserved, M6) |

### 5.2 `session.bye`
This request carries no params. The host cancels the session's subscriptions. Operations keep running (§6.4).

---

## 6. Core methods

### 6.1 Host

| Method | Params | Result |
|---|---|---|
| `host.stats` | — | `{budgetMs, usedMsP50, usedMsP99, queueDepth, inflight, sessions, worstCapabilities:[{name, ms}], streamsDegraded}` |
| `host.state` | — | `{scene, paused, warp, activeVessel, competition?}`. The same object is pushed as the `host.state` notification on change. |

### 6.2 Catalog

| Method | Params | Result |
|---|---|---|
| `catalog.get` | `{since?: revision, include?: ["capabilities","resources","streams","events","providers"], provider?, safety?, onlyAllowed?: bool}` | `{revision, providers[], capabilities[], resources[], streams[], events[], removed?: [names]}`. With `since`, the host MAY return a delta. |
| `catalog.search` | `{query, limit?: 20}` | Matching descriptors ranked by name and description relevance (used for progressive disclosure) |
| `catalog.describe` | `{name}` | A single full descriptor |

Notification `catalog.changed`: `{revision, added:[names], removed:[names], changed:[names]}`. Triggers include mod providers registering, annotation reloads, permission changes (because the `permission` field changes), and dynamic enums changing.

### 6.3 Calling capabilities

`cap.call` takes `{name, args, opts?}`, where `name` MAY carry an explicit version as `name@N`.

| Outcome | Result |
|---|---|
| Synchronous | `{status:"ok", value, meta}` |
| Long-running | `{status:"accepted", op:"op:N", meta}` |
| Dry run | `{status:"dry_run", wouldRequire:{level, policy}, meta}` |
| Error | JSON-RPC `error` (§10) |

The host processes a `cap.call` in this order (normative):
1. Envelope and size checks.
2. Resolve the capability. Unknown names return `-32601`.
3. Validate `args` against `inputSchema`. Failures return `INVALID_PARAMS` with JSON-pointer `data.errors`.
4. Scene availability. Failures return `UNAVAILABLE_IN_SCENE`.
5. **Permission evaluation** (§8). The result is `deny` (`PERMISSION_DENIED`), `ask` (confirmation, §8.4) or `allow`.
6. Rate limit. Failures return `RATE_LIMITED`.
7. Preconditions (`requires`). Failures return `PRECONDITION_FAILED`.
8. Enqueue by affinity (`main`/`fixed`), or execute directly (`snapshot`/`any`).
9. Execute. Provider exceptions become `PROVIDER_ERROR`, or the provider's own error kind.
10. Validate the result against `outputSchema` (debug builds only; production logs mismatches).
11. Audit (every call, including denials).

### 6.4 Operations

| Method | Params | Result |
|---|---|---|
| `op.get` | `{op}` | `{op, capability, status: running\|ok\|error\|cancelled, progress?, message?, value?, error?, startedUT, endedUT?}` |
| `op.wait` | `{op, timeoutMs}` | The same as `op.get`, returned when the operation finishes or the timeout passes. This blocks a network thread, never the game. |
| `op.cancel` | `{op, reason?}` | `{op, status}`. Cancellation is cooperative; providers check `isCancelled`. |
| `op.list` | `{session?: "self"\|"all"}` | Running and recent operations (recent ones are kept for 10 minutes) |

Notifications: `op.progress {op, progress (0..1 or null), message}` and `op.done {op, status, value?, error?}`.

**Rules:**
- Operations are owned by the host, not the session. A client MAY re-attach after reconnecting using `op.get`.
- Operations bound to a scene are cancelled on scene change with `error.kind = "CANCELLED", data.reason = "scene_change"`.
- Timeouts declared by providers are in **UT seconds**, with a host wall-clock cap.

### 6.5 Streams

`stream.subscribe` takes:

```json
{ "source": { "stream": "bda.engagement" } | { "query": <ksp.query selector> } | { "resource": "ksp://vessel/v:123" },
  "mode": "sampled" | "onChange",
  "rateHz": 4,                 // sampled: target rate in game time; host may lower it
  "minIntervalMs": 200,        // onChange: coalescing window
  "deadband": { "default": 0.001, "fields": { "floodedDisplay": 0.01 } },
  "fields": ["…"] }
```

The result is `{sub:"sub:N", effectiveRateHz}`. The host pushes `stream.data`:

```json
{"jsonrpc":"2.0","method":"stream.data","params":{"sub":"sub:3","seq":118,"ut":204520.10,
  "kind":"delta","data":{"v:3281937421":{"m:88123:4:MissileFire":{"multiTargetNum":64}}},
  "degraded":false}}
```

`kind` is `full` on the first message and after a resync, and `delta` otherwise. `stream.unsubscribe {sub}` and `stream.list` are also defined.

**Rules:**
- Sampling happens in `FixedUpdate`, within the host's stream budget.
- If the host cannot meet the requested rate it MUST lower `effectiveRateHz` and set `degraded:true`.
- While paused, the host sends one frame with `paused:true` and then nothing until physics resumes.

### 6.6 Events

| Method | Params | Result |
|---|---|---|
| `events.subscribe` | `{names:["bda.*","ksp.vessel.*"], fromCursor?}` | `{sub, cursor}` |
| `events.unsubscribe` | `{sub}` | — |
| `events.poll` | `{cursor, limit?: 256, names?}` | `{events[], nextCursor, resyncRequired: bool}` |

The ring buffer holds at least 2048 events. `resyncRequired` is true when `cursor` has fallen out of the buffer.

Notification:

```json
{"jsonrpc":"2.0","method":"event","params":{"cursor":5531,"name":"bda.vessel_killed","ut":204600.5,
  "data":{"vessel":"Iowa_2","handle":"v:1188","by":"Kirov_1","cause":"Missiles","aliveState":"CleanKill"}}}
```

### 6.7 Permissions (read-only plus requests)

| Method | Params | Result |
|---|---|---|
| `permissions.get` | `{mod?, capability?}` | The effective policy for this session, including the matched rule |
| `permissions.request` | `{mod, level, capability?, reason}` | Shows an in-game prompt to the human. Returns `{status: granted\|denied\|timeout, scope: once\|session\|persistent}`. |

There is **no** method that sets permissions. A host MUST NOT offer one over the wire. Notification `permissions.changed {mods:[...]}` is followed by `catalog.changed`.

### 6.8 Deep reflection (feature `deepReflection`; gated)

| Method | Params | Level |
|---|---|---|
| `reflect.get` | `{target: handle \| {type, static:true}, path: "member.member[0]", maxDepth?: 3}` | `deepRead` flag per mod |
| `reflect.set` | `{target, path, value}` | `unsafe` |
| `reflect.call` | `{target, method, args[], argTypes?[]}` | `unsafe` |

Hosts MUST cap result size, break cycles, and audit the full path.

---

## 7. Catalog data model

### 7.1 Provider descriptor

```json
{ "id": "bdarmory", "title": "BDArmory", "version": "1.13.1", "tier": 2,
  "mod": "BDArmory", "assembly": "BDArmory", "authority": 100,
  "description": "Weapons, AI pilots, competitions and scoring.",
  "protocolMin": "0.1", "status": "active" }
```

`tier` is 0–3. `status` is `active`, `shadowed` (a higher-authority provider took over its names) or `error`.

### 7.2 Capability descriptor

| Field | Type | Req. | Notes |
|---|---|---|---|
| `name` | string | ✓ | `^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$`, lowercase with dots. The first segment is the provider namespace (§13). |
| `version` | integer | ✓ | Starts at 1. A breaking change publishes a new version alongside the old one. |
| `provider` | string | ✓ | |
| `title` | string | | |
| `description` | string | ✓ | Written for an LLM: what it does, when to use it, side effects, units |
| `safety` | enum | ✓ | `read`, `control`, `destructive`, `cheat` or `unsafe` (§8.1) |
| `scenes` | string[] | ✓ | `GameScenes` names, or `["ANY"]` |
| `affinity` | enum | ✓ | `main`, `fixed`, `snapshot` or `any` |
| `longRunning` | bool | | Default false |
| `idempotent` | bool | | Default false |
| `requires` | string[] | | Preconditions: `unpacked`, `unpaused`, `activeVessel`, `competitionIdle`, … |
| `inputSchema` | JSON Schema 2020-12 | ✓ | Type `object`. Handle-typed properties use `"x-skz-handle": "vessel"\|"part"\|"module"`. |
| `outputSchema` | JSON Schema 2020-12 | | |
| `examples` | array | | `[{args, description}]` |
| `permission` | object | ✓ (host-filled) | `{state: allow\|ask\|deny, source}` for *this session* |
| `availability` | object | ✓ (host-filled) | `{available: bool, reason?}` for the current scene and state |
| `x-skz-*` | any | | Extension metadata such as units, step and labels |

### 7.3 Resource, stream and event descriptors

```json
{ "uri": "ksp://vessel/{handle}", "template": true, "title": "Vessel", "mimeType": "application/json",
  "description": "Vessel summary with parts and modules (depth 1).", "safety": "read", "provider": "ksp" }
{ "stream": "bda.engagement", "provider": "bdarmory", "defaultHz": 2, "maxHz": 20, "safety": "read",
  "description": "...", "schema": { … } }
{ "event": "bda.vessel_killed", "provider": "bdarmory", "safety": "read", "maxRateHz": 10,
  "description": "...", "schema": { … } }
```

---

## 8. Safety levels and permissions (normative)

### 8.1 Levels

| Level | Definition |
|---|---|
| `read` | MUST NOT change game state, files, or settings |
| `control` | Changes state in ways a player could in normal play, and reversibly |
| `destructive` | Irreversible changes, or loss of state (firing weapons, staging, removing vessels, loading saves) |
| `cheat` | Effects impossible in normal play (spawning, teleporting, editing resources or HP, infinite-ammo-type settings) |
| `unsafe` | Can break the game or act outside it (deep reflection write or call, file writes outside sandboxes) |

A capability MUST declare the **highest** level of any effect it can have. Levels are independent grants, not a hierarchy.

### 8.2 Evaluation
A host MUST evaluate policy on every call, in the host, before execution. The order:
1. capability rule
2. part-pack rule (if the capability targets a part or module)
3. mod rule
4. global default

At equal specificity, `deny` beats `ask`, which beats `allow`. The result is then clamped by any client cap. Session grants from in-game prompts sit above file rules. The default global policy MUST be `deny`, except for stock (`Squad`) `read`.

### 8.3 Unsafe
`unsafe` MUST NOT be grantable through any wire method or client-side confirmation. It may only be granted through the host's local configuration or UI.

### 8.4 Confirmation (`ask`)
- With `confirm.game`: the host shows an in-game prompt and holds the call, on a network thread, until the human decides or the timeout (default 60 s) passes. A timeout is a deny. Approval scopes are `once`, `session` or `persistent`.
- With `confirm.client`, and only when host policy allows `confirmChannel = client|either`:
  1. The host returns `CONFIRMATION_REQUIRED` with `data:{confirmId, summary, level}`.
  2. The client collects human approval (for example MCP elicitation).
  3. The client retries `cap.call` with `opts.confirmation: {confirmId, approved: true}`.

  A `confirmId` is single-use, bound to the session and to a hash of `args`, and expires after 120 s.

### 8.5 Audit
The host MUST append one record per request (allowed or not) with:
- the UTC time, UT, session and client name
- the method and capability, and the truncated args
- the decision and the matched rule
- who confirmed it (game, client or n/a)
- the status, error kind and duration

---

## 9. Handles and value encoding

| Kind | Format | Notes |
|---|---|---|
| Vessel | `v:<persistentId>` | `Vessel.persistentId` (uint) |
| Part | `p:<persistentId>` | `Part.persistentId` |
| Module | `m:<partPersistentId>:<moduleIndex>:<ModuleClassName>` | The host MUST verify the class name on use and return `NOT_FOUND {reason:"stale"}` if it does not match |
| Operation, subscription, confirmation | `op:N`, `sub:N`, `cf:N` | |

Capabilities whose schema marks a property `x-skz-handle: vessel` MUST also accept an exact vessel name. If the name is ambiguous, they return `INVALID_PARAMS` with `data.candidates`.

**Value encoding:**

| Value | Encoding |
|---|---|
| Vectors | arrays `[x,y,z]` with `x-skz-frame` |
| Positions | `{"body":"Kerbin","lat":-0.15,"lon":-74.2,"alt":12.3}`, or relative `{"ref":"v:123","north":…,"east":…,"up":…}`. Raw Unity world coordinates MUST NOT be emitted. |
| Time | `ut` (seconds of universal time, double). Wall-clock values are ISO 8601 UTC strings, with the suffix `Utc` in key names. |
| Enums | strings (names) |
| NaN and ±Infinity | `null`, with sibling `"<key>$nonFinite":"NaN"\|"+Inf"\|"-Inf"` |
| Localized text | resolved strings. The raw `#LOC` key MAY appear in `x-skz-locKey`. |

---

## 10. Errors

Errors use the JSON-RPC `error` object, with `data.kind` as the stable machine-readable identifier. Clients SHOULD switch on `data.kind`, not on `code`.

| code | `data.kind` | retryable | Meaning and typical `data` |
|---|---|---|---|
| -32700 | `PARSE_ERROR` | no | Invalid JSON |
| -32600 | `INVALID_REQUEST` | no | Not JSON-RPC 2.0, a batch, or a call before `session.hello` |
| -32601 | `METHOD_NOT_FOUND` | no | Unknown method or capability: `{name, didYouMean?}` |
| -32602 | `INVALID_PARAMS` | no | Schema failure: `{errors:[{pointer, message}], candidates?}` |
| -32603 | `INTERNAL_ERROR` | maybe | A host bug (logged) |
| -32001 | `UNAUTHENTICATED` | no | Bad or missing token. The connection is closed. |
| -32002 | `PERMISSION_DENIED` | no | `{capability, mod, required, policy, source, hint}` |
| -32003 | `CONFIRMATION_REQUIRED` | yes | `{confirmId, summary, level, expiresInMs}` |
| -32004 | `UNAVAILABLE_IN_SCENE` | yes (later) | `{scene, required[], reason?: "transition"}` |
| -32005 | `NOT_FOUND` | no | Unknown or stale handle: `{handle, reason: "unknown"\|"stale"\|"destroyed"}` |
| -32006 | `BUSY` | yes | Queue full: `{retryAfterMs}` |
| -32007 | `TIMEOUT` | yes | `{timeoutMs, op?}` |
| -32008 | `CONFLICT` | no | State conflict, for example a competition already running or guard mode on: `{detail}` |
| -32009 | `PRECONDITION_FAILED` | maybe | `{reason: "paused"\|"vessel_packed"\|"bda_api_changed"\|..., detail}` |
| -32010 | `PROVIDER_ERROR` | maybe | An exception in mod code: `{provider, exceptionType, message}`. No stack trace unless the host is in debug mode. |
| -32011 | `CANCELLED` | no | `{reason: "client"\|"scene_change"\|"kill_switch"\|"timeout"}` |
| -32012 | `RATE_LIMITED` | yes | `{retryAfterMs, limit}` |
| -32013 | `VERSION_UNSUPPORTED` | no | `{supported:["0.1"]}` |

Every error's `data` SHOULD include `hint`: one sentence telling an LLM what to do next.

---

## 11. Versioning

- **Protocol** is `MAJOR.MINOR`.
  - In 0.x, any change may break compatibility; clients pin exact versions.
  - From 1.0, MINOR is additive only (new methods, optional fields, new features), and MAJOR is breaking.
  - Hosts SHOULD support the previous MAJOR for 12 months.
- **Capabilities** carry an integer `version`.
  - A breaking change to a capability's input or output MUST publish `name@N+1`, and SHOULD keep `name@N` for one host minor release.
  - `cap.call` without `@N` resolves to the highest version.
- **Providers** use semver in `version`. `protocolMin` declares the oldest protocol they understand.
- **The contract assembly** (`Skizzy.Contract.dll`) MUST keep `AssemblyVersion` at `MAJOR.0.0.0`. Only `AssemblyFileVersion` changes, so mods compiled against any 1.x keep binding.
- **The JSON-delegate ABI** reports `AbiVersion` (integer). Hosts MUST support all ABI versions from 1 up to their current one.

---

## 12. Contract API (C#)

### 12.1 Tier 2: `SkizzyAttributes.cs` (CC0; copy it and change the namespace)

```csharp
// SkizzyAttributes.cs — CC0 1.0 Universal. Copy into your mod and change the namespace.
// The skizzy-protocol host discovers these attributes BY CLASS NAME via CustomAttributeData,
// so your mod needs no reference to any Skizzy assembly. If no host is installed they do nothing.
using System;

namespace YourMod.SkizzyIntegration
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    internal sealed class SkizzyProviderAttribute : Attribute
    {
        public SkizzyProviderAttribute(string id) { Id = id; }
        public string Id { get; }                      // lowercase, e.g. "hullbreach"; becomes the namespace
        public string Title { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }            // default: assembly informational/file version
        public int Authority { get; set; }             // higher wins when two providers define the same capability
        public string ProtocolMin { get; set; } = "0.1";
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class SkizzyToolAttribute : Attribute
    {
        public SkizzyToolAttribute(string name) { Name = name; }
        public string Name { get; }                    // "<providerId>.<verb_noun>", e.g. "hullbreach.get_flood_state"
        public string Title { get; set; }
        public string Description { get; set; }        // write for an LLM: purpose, side effects, units
        public string Safety { get; set; } = "control";// read | control | destructive | cheat | unsafe
        public string Scenes { get; set; } = "ANY";    // comma list of GameScenes names, e.g. "FLIGHT,EDITOR"
        public string Affinity { get; set; } = "main"; // main | fixed | any
        public string Requires { get; set; }           // comma list: unpacked, unpaused, activeVessel, ...
        public bool LongRunning { get; set; }          // implied when the method returns IEnumerator
        public bool Idempotent { get; set; }
        public bool ReturnsJson { get; set; }          // method returns a raw JSON string
        public int Version { get; set; } = 1;
        public string OutputSchemaJson { get; set; }   // optional JSON Schema for the result
    }

    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    internal sealed class SkizzyParamAttribute : Attribute
    {
        public string Description { get; set; }
        public double Minimum { get; set; } = double.NaN;
        public double Maximum { get; set; } = double.NaN;
        public object Default { get; set; }
        public string Units { get; set; }              // "m", "m/s", "t", "deg", "s", "fraction"
        public string Enum { get; set; }               // comma list for string params
        public string SchemaJson { get; set; }         // full JSON Schema override for this parameter
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false)]
    internal sealed class SkizzyResourceAttribute : Attribute
    {
        public SkizzyResourceAttribute(string uriTemplate) { UriTemplate = uriTemplate; }
        public string UriTemplate { get; }             // e.g. "hullbreach://vessel/{vessel}"
        public string Description { get; set; }
        public double SampleHz { get; set; }           // > 0: may also be offered as a sampled stream
    }

    [AttributeUsage(AttributeTargets.Event | AttributeTargets.Field, AllowMultiple = false)]
    internal sealed class SkizzyEventAttribute : Attribute
    {
        public SkizzyEventAttribute(string name) { Name = name; }
        public string Name { get; }                    // e.g. "hullbreach.foundered"
        public string Description { get; set; }
        public double MaxRateHz { get; set; } = 10;
    }
}
```

### 12.2 Duck-typing rules (normative for hosts)

1. **Discovery.** For every assembly in `AssemblyLoader.loadedAssemblies`, read custom attribute data on types, methods, properties, events and parameters through `CustomAttributeData`. An attribute matches if its type's **simple name** equals one of the five names above. Namespace and visibility do not matter.
2. **Provider classes** MUST be one of:
   - `static`
   - a type with a public static `Instance` property or field (a KSPAddon singleton)
   - a `PartModule` or `VesselModule` subclass. Tools on these are *instance-bound*: the host adds a required `module` or `vessel` handle parameter and invokes the tool on each resolved instance.
3. **Tool methods** MUST be public or internal, and MUST NOT be generic or overloaded under the same tool name. Violations are reported in the provider's `status: error` with a reason.
4. **Parameter types:**
   - JSON scalars, enums (by name), nullables (optional)
   - `T[]` and `List<T>` of supported types
   - `Dictionary<string, object>`
   - `Vector3d` and `Vector3` (from `[x,y,z]`)
   - `Vessel`, `Part` or any `PartModule` subclass, resolved from a handle or name. The schema gets `x-skz-handle`.
5. **Injected parameters,** matched by name and type, are not part of the schema:
   - `Action<double,string> progress`
   - `Func<bool> isCancelled`
   - `Func<double> ut`
6. **Defaults.** C# optional parameter defaults become schema `default`s. `[SkizzyParam(Default=…)]` overrides them.
7. **Return types:**

| Return type | Result |
|---|---|
| `void` | `null` |
| Scalar, enum, POCO, `IDictionary`, `IEnumerable` | Serialized from public fields and properties, depth 6, cycles broken |
| `string` with `ReturnsJson = true` | Raw JSON |
| `IEnumerator` | **Long-running.** Each step runs on the host dispatcher's coroutine. A yielded object with members `progress` (number) and/or `message` (string) emits `op.progress`. Unity `YieldInstruction`s (`WaitForFixedUpdate`, `WaitForSeconds`) are honoured. The last other yielded value is the result. |

8. **Errors.** Any exception becomes `PROVIDER_ERROR`. If the exception has a string property `SkizzyErrorKind` matching a `data.kind` in §10, the host uses that kind and the exception message.
9. **Events.** A `static event` or a static delegate field tagged `[SkizzyEvent]` is subscribed by the host using a dynamically built delegate of the event's type. The arguments are serialized as `data` (one argument: the value; several: `{arg0, arg1, …}` or the parameter names when available).

### 12.3 Tier 3a: `Skizzy.Contract.dll` (MIT; hard reference)

```csharp
// API outline: signatures only (bodies omitted). Targets net48; references mscorlib, System,
// Assembly-CSharp and UnityEngine.CoreModule only.
namespace Skizzy.Protocol
{
    public enum Safety   { Read, Control, Destructive, Cheat, Unsafe }
    public enum Affinity { Main, Fixed, Snapshot, Any }
    public enum ErrorKind { InvalidParams, PermissionDenied, UnavailableInScene, NotFound, Busy, Timeout,
                            Conflict, PreconditionFailed, ProviderError, Cancelled, RateLimited }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class SkizzyProviderAttribute : Attribute { /* same members as §12.1 */ }

    /// Implemented by Tier-3a providers. The host instantiates the class (parameterless ctor or
    /// static Instance) once per game session and calls Register on the main thread.
    public interface ISkizzyProvider { void Register(ISkizzyRegistry registry); }

    public interface ISkizzyRegistry
    {
        string ProtocolVersion { get; }
        IToolBuilder     Tool(string name);
        IResourceBuilder Resource(string uriTemplate);
        IStreamBuilder   Stream(string name, StreamOptions options);
        IEventSink       Event(string name, string description = null, string schemaJson = null);
        void Unregister(string name);
        void NotifyCatalogChanged();                     // e.g. a dynamic enum changed
    }

    public interface IToolBuilder
    {
        IToolBuilder Describe(string description, string title = null);
        IToolBuilder Safety(Safety level);
        IToolBuilder Scenes(params GameScenes[] scenes);
        IToolBuilder Affinity(Affinity affinity);
        IToolBuilder Requires(params string[] preconditions);
        IToolBuilder Idempotent(bool value = true);
        IToolBuilder LongRunning(bool value = true);
        IToolBuilder Version(int version);
        IToolBuilder Param(string name, Schema schema, string description, bool required = true);
        IToolBuilder InputSchema(string json);           // replaces Param-built schema
        IToolBuilder OutputSchema(string json);
        IToolBuilder Handler(Func<IToolContext, IDictionary<string, object>, object> handler);
        IToolBuilder Coroutine(Func<IToolContext, IDictionary<string, object>, IEnumerator> routine);
    }

    public interface IToolContext
    {
        ClientInfo Caller { get; }                       // name, kind, session id
        bool IsCancelled { get; }
        double UT { get; }
        void Progress(double? fraction, string message = null);
        Vessel ResolveVessel(object handleOrName);       // throws SkizzyException(NotFound)
        Part ResolvePart(object handle);
        T ResolveModule<T>(object handle) where T : PartModule;
        string HandleOf(Vessel v);
        string HandleOf(Part p);
        string HandleOf(PartModule m);
    }

    public interface IResourceBuilder { IResourceBuilder Describe(string d); IResourceBuilder Reader(Func<IToolContext, IDictionary<string,string>, object> read); }
    public interface IStreamBuilder   { IStreamBuilder Describe(string d); IStreamBuilder Sampler(Func<object> sampleInFixedUpdate); IStreamWriter Push(); }
    public interface IStreamWriter    { void Publish(object payload); }
    public interface IEventSink       { void Emit(object payload); }

    public sealed class StreamOptions { public double DefaultHz = 2, MaxHz = 20; public string SchemaJson; }
    public sealed class ClientInfo    { public string Name, Kind, Session; }

    public sealed class Schema
    {
        public static Schema Number(double? min = null, double? max = null, double? def = null, string units = null);
        public static Schema Integer(long? min = null, long? max = null, long? def = null);
        public static Schema Boolean(bool? def = null);
        public static Schema String(string def = null, string pattern = null);
        public static Schema Enum(IEnumerable<string> values, string def = null);
        public static Schema Handle(string kind);        // "vessel" | "part" | "module"
        public static Schema Array(Schema items, int? minItems = null, int? maxItems = null);
        public static Schema Raw(string jsonSchema);
    }

    public sealed class SkizzyException : Exception
    {
        public SkizzyException(ErrorKind kind, string message, object data = null);
        public ErrorKind Kind { get; }
        public object Detail { get; }                    // serialized into error.data
    }
}
```

Arguments arrive as `IDictionary<string, object>` holding JSON-like values (`double`, `long`, `bool`, `string`, `List<object>`, `Dictionary<string,object>`, `null`), so the contract needs no JSON library. Return values are serialized by the host as in §12.2 rule 7. Hard-dependent mods SHOULD add `[assembly: KSPAssemblyDependency("Skizzy.Contract", 1, 0)]` and MUST NOT ship `Skizzy.Contract.dll` themselves.

### 12.4 Tier 3b: JSON-delegate ABI (no reference; `SkizzyLink.cs`, CC0)

The host exposes a static entry point inside `Skizzy.Host.dll`:

```csharp
namespace Skizzy.Host.Abi
{
    public static class SkizzyAbi
    {
        public static int AbiVersion => 1;

        /// manifestJson: {"provider":{...§7.1 minus host fields...},"capabilities":[...§7.2 minus host fields...],
        ///                "streams":[...],"events":[...],"resources":[...]}
        /// invoke(name, requestJson) -> responseJson, called on the main thread per capability affinity.
        ///   request:  {"callId":"c-91","args":{...},"ut":204511.3,"caller":{"name":"skizzy-mcp","kind":"mcp-bridge"}}
        ///   response: {"ok":<value>} | {"error":{"kind":"CONFLICT","message":"..."}} | {"pending":{"progress":0.2,"message":"..."}}
        ///   If "pending" is returned, the host calls invoke("$poll", {"callId":"c-91"}) once per frame until ok/error,
        ///   and invoke("$cancel", {"callId":"c-91"}) on cancellation.
        /// attachEmitter(emit): host hands the provider emit(name, payloadJson) for events and pushed streams.
        /// Returns {"ok":{"registrationId":"r-3"}} or {"error":{...}}.
        public static string RegisterProvider(string manifestJson,
                                              Func<string, string, string> invoke,
                                              Action<Action<string, string>> attachEmitter);

        public static string UpdateManifest(string registrationId, string manifestJson); // → catalog.changed
    }
}
```

`SkizzyLink.cs` (CC0, about 120 lines, shipped in `src/Skizzy.Contract.Source/`) works as follows:
- It finds `Skizzy.Host.Abi.SkizzyAbi` by scanning `AssemblyLoader.loadedAssemblies` for an assembly named `Skizzy.Host`.
- It checks `AbiVersion`, builds the delegates, and includes a 60-line JSON writer and parser so the mod does not need a JSON library.
- If the host is absent it returns `false`, and the mod carries on normally.
- A mod calls `SkizzyLink.TryRegister(manifestJson, Invoke)` from a `KSPAddon` (Startup.Instantly or MainMenu).

### 12.5 Tier 1: `SKIZZY_ANNOTATIONS` (ConfigNode; MM-patchable)

The files live anywhere under GameData. The host ships its packs in `GameData/Skizzy/Annotations/`. They are read after ModuleManager has run (from `GameDatabase`), so they can be added and patched with `@SKIZZY_ANNOTATIONS[...]`.

```
SKIZZY_ANNOTATIONS
{
    mod = HullBreach                    // owning mod (assembly or GameData folder)
    modVersion = 1.*                    // optional; ignored with a warning when it does not match
    provider = hullbreach               // capability namespace for any declared tools

    MODULE
    {
        name = ModuleHullBreach
        description = Per-part flooding model. Breached hull parts take on water; enough water founders the ship.
        FIELD { name = crushDepth      units = m         description = Depth at which this part implodes.  safety = control }
        FIELD { name = floodedDisplay  units = fraction  description = Fraction of this compartment that is flooded. }
        FIELD { name = inflowDisplay   units = t/s       description = Current water inflow. }
        FIELD { name = armorBaseline   expose = false }
        EVENT { name = ToggleTestBreach  safety = cheat  description = Force a 75% test breach on this part (debug). }
    }

    VESSEL_MODULE
    {
        name = HullBreachVesselModule
        PROPERTY { name = WaterTonnes         units = t    description = Water aboard the whole vessel. }
        PROPERTY { name = CapacityTonnes      units = t }
        PROPERTY { name = InflowTonnesPerSec  units = t/s }
        PROPERTY { name = Foundered           description = True once the vessel has foundered (sinking). }
    }
}

SKIZZY_ANNOTATIONS
{
    mod = BDArmory
    provider = bdarmory
    STATIC
    {
        type = BDArmory.Settings.BDArmorySettings
        memberAttribute = BDAPersistentSettingsField   // expose every static tagged with this attribute
        access = readwrite
        safety = control
        capability = bda.settings                      // generates bda.settings.get / bda.settings.set
        OVERRIDE { match = INFINITE_*               safety = cheat }
        OVERRIDE { match = GRAVITY_*                safety = cheat }
        OVERRIDE { match = REMOTE_ORCHESTRATION_*   expose = false }
    }
    METHOD
    {
        capability = bda.competition.start_now
        type = BDArmory.Competition.BDACompetitionMode
        instance = static:Instance
        name = StartCompetitionNow
        safety = control
        scenes = FLIGHT
        description = Skip the "waiting for teams to get in position" phase of a starting competition.
        PARAM { name = delay  type = float  default = 0  units = s  description = Delay before forcing the start. }
    }
}
```

**Grammar summary:**

| Node | Purpose | Keys / sub-nodes |
|---|---|---|
| `MODULE` / `VESSEL_MODULE` | Annotate a module type | `name`, `description`. Sub-nodes `FIELD`, `EVENT`, `ACTION` and `PROPERTY`, each with `name`, `description`, `units`, `safety`, `expose`, `writable`, `alias`. `PROPERTY` exposes a C# property read-only unless `writable = true`, which requires `safety = cheat` or higher. |
| `STATIC` | Expose static members of a type | `type`, `memberAttribute` or `members` (comma list or glob), `access`, `safety`, `capability`, `OVERRIDE` |
| `METHOD` | Declare a composite tool over a public method | `capability`, `type`, `instance` (`static:<member>` or `module`/`vessel`), `name`, `safety`, `scenes`, `description`, `PARAM` (`name`, `type`, `default`, `min`, `max`, `units`, `description`) |

---

## 13. Namespaces

| Prefix | Owner |
|---|---|
| `session.`, `host.`, `catalog.`, `cap.`, `op.`, `stream.`, `events.`, `permissions.`, `reflect.`, `sim.` | Core protocol methods (§5–6) |
| `ksp.` | The host's built-in stock provider (vessels, parts, modules, action groups, staging, time, scene, logs, config) |
| `<providerId>.` | Mod providers, for example `bda.`/`bdarmory.` (BDArmory) or `hullbreach.`. A provider id must match `^[a-z][a-z0-9_]{1,31}$`. |
| `x_<anything>.` | Experimental or private. Never shown to MCP clients unless explicitly enabled. |

BDArmory capabilities use the short alias `bda.` because it is the de-facto community abbreviation. The provider id is `bdarmory`, and the host maps `bda.` to it. When two providers claim the same capability name, the one with the higher `authority` wins and the other is marked `shadowed`.

---

## 14. MCP projection (normative for bridges)

A conforming MCP bridge:

1. **Speaks MCP 2026-07-28.** It implements `server/discover`, accepts `_meta` protocol versions, and SHOULD also negotiate 2025-11-25 for older clients.
2. **Exposes each allowed capability as a tool.**
   - The name is the capability name with `.` replaced by `_`. If the result exceeds 64 characters it is truncated to 56 and suffixed `_` + a 7-character hash. The resulting name MUST match `^[a-zA-Z0-9_-]{1,64}$`.
   - The original name is in `_meta["skizzy/capability"]`.
   - `inputSchema` and `outputSchema` are copied. Results return `structuredContent` (the `value`) plus one text block summarising it in at most about 400 characters.
3. **Sets annotations:**
   - `readOnlyHint = (safety == read)`
   - `destructiveHint = safety ∈ {destructive, cheat, unsafe}`
   - `idempotentHint = idempotent`
   - `openWorldHint = false`
4. **Hides** capabilities whose `permission.state == deny`, unless configured to show them. Capabilities in state `ask` are listed with "(requires your confirmation)" appended to the description.
5. **Keeps lists deterministic,** sorted by name. It sets `ttlMs` (default 60000) and `cacheScope: "private"`. On `catalog.changed` it emits `notifications/tools/list_changed` and/or `notifications/resources/list_changed` on `subscriptions/listen` streams that opted in.
6. **Maps long operations:**
   - If the client negotiated the `io.modelcontextprotocol/tasks` extension, an operation becomes a task.
   - Otherwise the bridge calls with `opts.waitMs` (default 30000), relays `op.progress` as `notifications/progress` when the request carried a `progressToken`, and on timeout returns `{op, status:"running"}`.
   - It MUST offer `skizzy_wait {op, timeoutMs}` and `skizzy_cancel {op}` tools.
7. **Maps `CONFIRMATION_REQUIRED`:**
   - If `confirm.client` was negotiated and the client supports elicitation, the bridge returns an MRTR `InputRequiredResult` with a form elicitation built from `data.summary`. On retry it forwards `opts.confirmation`.
   - Otherwise it waits for the in-game decision, or returns a tool error with a hint to ask the user.
8. **Maps errors.** Capability errors become tool results with `isError: true` and `structuredContent: {kind, message, hint, ...data}`. Protocol and transport failures become MCP errors.
9. **Maps resources** one-to-one, including templates. `resourceSubscriptions` on `subscriptions/listen` map to host streams, throttled so that `notifications/resources/updated` is sent at most once per second per URI.
10. **Treats game text as data.** Game-sourced strings (vessel names, craft descriptions, log lines) MUST NOT be placed into tool `description` fields.
11. **Offers toolsets** (`core`, `bda`, `bda-advanced`, `universal-write`, `deep`, `+<providerId>`) and the progressive-disclosure pair `skizzy_search_capabilities` / `skizzy_call`, so the default tool count stays at about 40.

---

## 15. Conformance checklist (host)

- [ ] Binds to loopback only. Rejects a missing or bad token, a non-allow-listed `Origin`, and a foreign `Host`.
- [ ] `session.hello` is required first. Version and feature negotiation follow §5.1.
- [ ] Never touches Unity or KSP objects off the main thread (verified with debug assertions).
- [ ] Every `cap.call` follows the order in §6.3. Every request is audited.
- [ ] The default policy is deny except Squad `read`. No wire method can grant permissions. `unsafe` cannot be granted remotely.
- [ ] Handles are invalidated on scene change. A stale module handle returns `NOT_FOUND {reason:"stale"}`.
- [ ] Operations report timeouts in UT, survive client disconnects, and are cancelled on scene change.
- [ ] Streams degrade (and report it) instead of exceeding the budget. Paused state is signalled.
- [ ] The event ring buffer holds at least 2048 events, with `resyncRequired`.
- [ ] Positions never use raw world coordinates. NaN and Infinity are encoded per §9.
- [ ] Tier 0 writes follow the PAW-equivalent path and read the value back.
- [ ] Tier 2 discovery works when the attribute type is in the mod's own namespace and is `internal`.
- [ ] `SkizzyAbi.AbiVersion` 1 works with `SkizzyLink.cs`.

The conformance suite in `spec/conformance/` drives these checks over the wire, against a running KSP plus a test provider mod.

---

## 16. Examples

### 16.1 Session transcript (abridged)

```json
→ {"jsonrpc":"2.0","id":1,"method":"session.hello","params":{"protocol":"skizzy-protocol","versions":["0.1"],"client":{"name":"notebook","version":"0.0.1","kind":"direct"},"features":["ops","streams","events"]}}
← {"jsonrpc":"2.0","id":1,"result":{"protocol":"skizzy-protocol","version":"0.1","session":"s-91","features":["ops","streams","events"],"catalogRevision":42,"meta":{"ut":204511.32,"scene":"FLIGHT","paused":false,"warp":{"mode":"none","rate":1},"fixedFrame":9123345,"catalogRevision":42}}}

→ {"jsonrpc":"2.0","id":2,"method":"cap.call","params":{"name":"ksp.vessels.list","args":{"loaded":true}}}
← {"jsonrpc":"2.0","id":2,"result":{"status":"ok","value":[{"handle":"v:3281937421","name":"Kirov_1","situation":"SPLASHED","position":{"body":"Kerbin","lat":-0.1501,"lon":-74.2003,"alt":0.4},"parts":214,"packed":false}],"meta":{...}}}

→ {"jsonrpc":"2.0","id":3,"method":"cap.call","params":{"name":"bda.configure_weapon_manager","args":{"vessel":"Kirov_1","multiTargetNum":64,"maxMissilesOnTarget":4}}}
← {"jsonrpc":"2.0","id":3,"result":{"status":"ok","value":{"applied":{"multiTargetNum":{"old":1,"new":64},"maxMissilesOnTarget":{"old":1,"new":4}}},"meta":{...}}}

→ {"jsonrpc":"2.0","id":4,"method":"stream.subscribe","params":{"source":{"stream":"bda.engagement"},"mode":"sampled","rateHz":4}}
← {"jsonrpc":"2.0","id":4,"result":{"sub":"sub:1","effectiveRateHz":4}}

→ {"jsonrpc":"2.0","id":5,"method":"cap.call","params":{"name":"bda.start_competition","args":{"distance":8000,"type":"FFA","tag":"mt64"},"opts":{"waitMs":20000}}}
← {"jsonrpc":"2.0","method":"op.progress","params":{"op":"op:7","progress":null,"message":"Competition: Waiting for teams to get in position."}}
← {"jsonrpc":"2.0","id":5,"result":{"status":"ok","value":{"competitionId":215637211,"tag":"mt64","startUT":204560.0},"meta":{...}}}
← {"jsonrpc":"2.0","method":"stream.data","params":{"sub":"sub:1","seq":1,"ut":204560.25,"kind":"full","data":{"Kirov_1":{"guard":true,"targets":["Iowa_1","Iowa_2","Fletcher_1"],"missilesAway":{"Iowa_1":4,"Iowa_2":3}}}}}
← {"jsonrpc":"2.0","method":"event","params":{"cursor":5531,"name":"bda.vessel_killed","ut":204600.5,"data":{"vessel":"Iowa_2","by":"Kirov_1","cause":"Missiles"}}}
```

### 16.2 Minimal Python direct client

```python
import asyncio, itertools, json, pathlib
import websockets  # pip install websockets (v14+)

KSP = pathlib.Path(r"D:\Epic Games\KerbalSpaceProgram\English")
TOKEN = (KSP / "GameData/Skizzy/PluginData/auth.token").read_text().strip()

async def main():
    ids = itertools.count(1)
    async with websockets.connect("ws://127.0.0.1:47549/skizzy", subprotocols=["skizzy.v0"],
                                  additional_headers={"Authorization": f"Bearer {TOKEN}"}) as ws:
        async def call(method, params=None):
            i = next(ids)
            await ws.send(json.dumps({"jsonrpc": "2.0", "id": i, "method": method, "params": params or {}}))
            while True:                       # notifications may arrive interleaved
                msg = json.loads(await ws.recv())
                if msg.get("id") == i:
                    return msg.get("result", msg.get("error"))
        print(await call("session.hello", {"protocol": "skizzy-protocol", "versions": ["0.1"],
                                           "client": {"name": "notebook", "version": "0.0.1", "kind": "direct"}}))
        print(await call("cap.call", {"name": "bda.get_scores", "args": {"format": "summary"}}))

asyncio.run(main())
```

---

## 17. Open items for draft 0.2
- Whether to add a binary or MessagePack encoding for high-rate streams, or to keep JSON only and measure first.
- `sim.hold`, `sim.step` and `sim.release` semantics, and how they interact with BDArmory AI coroutines.
- Whether resources should support ranged or paged reads for large configs and logs.
- Signed provider manifests, so a host could warn when an annotation pack claims a mod it does not belong to.
- Whether `permissions.request` may ask for `persistent` scope, or only `session` scope.
