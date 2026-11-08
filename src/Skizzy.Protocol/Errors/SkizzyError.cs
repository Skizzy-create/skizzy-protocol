using System;
using Skizzy.Protocol.Json;

namespace Skizzy.Protocol.Errors
{
    /// <summary>The unified error kinds of 03-protocol.md §9. Codes never change for a kind.</summary>
    public enum ErrorKind
    {
        ParseError,
        InvalidRequest,
        MethodNotFound,
        InvalidParams,
        InternalError,
        Unauthenticated,
        PermissionDenied,
        ConfirmationRequired,
        UnavailableInScene,
        NotFound,
        Busy,
        Timeout,
        Conflict,
        PreconditionFailed,
        ProviderError,
        Cancelled,
        RateLimited,
        VersionUnsupported,
        LeaseHeld,
        RoeViolation,
        StaleDecision,
        BudgetExhausted,
        SlowConsumer,
        NotInView,
        ExecutorUnavailable,
        KillSwitchActive,
        DuplicateName,
    }

    public static class ErrorKinds
    {
        private struct Info
        {
            public int Code;
            public string Name;
            public string Retryable;
            public string DefaultHint;
            public Info(int code, string name, string retryable, string hint) { Code = code; Name = name; Retryable = retryable; DefaultHint = hint; }
        }

        private static readonly Info[] Table =
        {
            new Info(-32700, "PARSE_ERROR", "no", "Send one valid UTF-8 JSON-RPC 2.0 object per text frame."),
            new Info(-32600, "INVALID_REQUEST", "no", "Send a single JSON-RPC 2.0 request object; call session.hello first."),
            new Info(-32601, "METHOD_NOT_FOUND", "no", "Use catalog.search to find the right capability name."),
            new Info(-32602, "INVALID_PARAMS", "no", "Fix the arguments listed in data.errors; catalog.describe shows the schema."),
            new Info(-32603, "INTERNAL_ERROR", "later", "A host bug; retry later and report the trace id if it repeats."),
            new Info(-32001, "UNAUTHENTICATED", "no", "Read the token from GameData/SkizzyProtocol/PluginData/auth.token and send it as 'Authorization: Bearer <token>'."),
            new Info(-32002, "PERMISSION_DENIED", "no", "The user's permission settings forbid this; ask the user to change them in the in-game Permissions window."),
            new Info(-32003, "CONFIRMATION_REQUIRED", "yes", "Ask the user to approve the prompt in game, then retry."),
            new Info(-32004, "UNAVAILABLE_IN_SCENE", "later", "Switch KSP to one of the scenes in data.required and retry."),
            new Info(-32005, "NOT_FOUND", "no", "List current objects again (ksp.vessels.list) and use a fresh handle."),
            new Info(-32006, "BUSY", "yes", "Retry after data.retryAfterMs."),
            new Info(-32007, "TIMEOUT", "yes", "Retry, or raise opts.timeoutMs."),
            new Info(-32008, "CONFLICT", "no", "The game state conflicts with this call; read data.detail and change the state first."),
            new Info(-32009, "PRECONDITION_FAILED", "later", "A precondition in data.reason is not met; fix it and retry."),
            new Info(-32010, "PROVIDER_ERROR", "later", "Game or mod code threw; check ksp.log.tail and retry later."),
            new Info(-32011, "CANCELLED", "no", "The call was cancelled; start it again if still needed."),
            new Info(-32012, "RATE_LIMITED", "yes", "Wait data.retryAfterMs before calling again."),
            new Info(-32013, "VERSION_UNSUPPORTED", "no", "Request one of the versions in data.supported."),
            new Info(-32020, "LEASE_HELD", "later", "Another actor holds this control channel; wait or ask it to release."),
            new Info(-32021, "ROE_VIOLATION", "no", "The rules of engagement forbid this action."),
            new Info(-32022, "STALE_DECISION", "yes", "Re-read the picture and issue the order again with the new seq."),
            new Info(-32023, "BUDGET_EXHAUSTED", "no", "The spending cap was reached."),
            new Info(-32024, "SLOW_CONSUMER", "no", "Read messages faster or subscribe to fewer streams."),
            new Info(-32025, "NOT_IN_VIEW", "no", "The object is not visible to your team under fog of war."),
            new Info(-32026, "EXECUTOR_UNAVAILABLE", "no", "No executor exists for this order type on this install."),
            new Info(-32027, "KILL_SWITCH_ACTIVE", "later", "The user engaged the kill switch; only read calls work until they release it in game."),
            new Info(-32028, "DUPLICATE_NAME", "no", "Choose another name or use the suggestion."),
        };

        public static int Code(ErrorKind k) { return Table[(int)k].Code; }
        public static string Name(ErrorKind k) { return Table[(int)k].Name; }
        public static string Retryable(ErrorKind k) { return Table[(int)k].Retryable; }
        public static string DefaultHint(ErrorKind k) { return Table[(int)k].DefaultHint; }

        public static bool TryParse(string name, out ErrorKind kind)
        {
            for (int i = 0; i < Table.Length; i++)
            {
                if (Table[i].Name == name) { kind = (ErrorKind)i; return true; }
            }
            kind = ErrorKind.InternalError;
            return false;
        }

        public static bool TryFromCode(int code, out ErrorKind kind)
        {
            for (int i = 0; i < Table.Length; i++)
            {
                if (Table[i].Code == code) { kind = (ErrorKind)i; return true; }
            }
            kind = ErrorKind.InternalError;
            return false;
        }

        public static int Count => Table.Length;
    }

    /// <summary>
    /// A protocol error: JSON-RPC <c>error</c> with <c>data.kind</c>, optional <c>data.reason</c>, and always
    /// <c>data.hint</c> and <c>data.retryable</c> (03 §9). Thrown by handlers; converted at the router.
    /// </summary>
    public sealed class SkizzyError : Exception
    {
        public ErrorKind Kind { get; }
        public string Reason { get; }
        public string Hint { get; }
        /// <summary>Extra members merged into <c>data</c> (handle, retryAfterMs, errors, ...).</summary>
        public JsonValue Extra { get; }

        public SkizzyError(ErrorKind kind, string message, string reason = null, string hint = null, JsonValue extra = null)
            : base(message ?? ErrorKinds.Name(kind))
        {
            Kind = kind;
            Reason = reason;
            Hint = string.IsNullOrEmpty(hint) ? ErrorKinds.DefaultHint(kind) : hint;
            Extra = extra;
        }

        public int Code => ErrorKinds.Code(Kind);

        public JsonValue ToErrorObject()
        {
            var data = JsonValue.Obj();
            data.Set("kind", ErrorKinds.Name(Kind));
            if (Reason != null) data.Set("reason", Reason);
            data.Set("hint", Hint);
            data.Set("retryable", ErrorKinds.Retryable(Kind));
            if (Extra != null && Extra.IsObject)
            {
                for (int i = 0; i < Extra.Count; i++)
                {
                    string k = Extra.KeyAt(i);
                    if (k == "kind" || k == "hint" || k == "retryable" || k == "reason") continue;
                    data.Set(k, Extra.ValueAt(i));
                }
            }
            return JsonValue.Obj().Set("code", Code).Set("message", Message).Set("data", data);
        }

        /// <summary>The first line the MCP bridge shows: "KIND: reason — hint" (03 §9).</summary>
        public string ToOneLine()
        {
            return ErrorKinds.Name(Kind) + (Reason != null ? ": " + Reason : "") + " - " + Message + (Hint != null ? " Hint: " + Hint : "");
        }

        public static SkizzyError FromErrorObject(JsonValue err)
        {
            if (err == null) return new SkizzyError(ErrorKind.InternalError, "empty error");
            var data = err.GetObj("data");
            ErrorKind kind = ErrorKind.InternalError;
            string kname = data?.GetString("kind");
            if (kname == null || !ErrorKinds.TryParse(kname, out kind)) ErrorKinds.TryFromCode(err.GetInt("code", -32603), out kind);
            return new SkizzyError(kind, err.GetString("message", kname), data?.GetString("reason"), data?.GetString("hint"), data);
        }

        // ---- factories (one per common case so messages stay consistent) ----
        public static SkizzyError InvalidParams(string message, JsonValue errors = null, string hint = null)
        {
            var extra = JsonValue.Obj();
            if (errors != null) extra.Set("errors", errors);
            return new SkizzyError(ErrorKind.InvalidParams, message, null, hint, extra);
        }

        public static SkizzyError InvalidParam(string pointer, string message, string hint = null)
        {
            var errs = JsonValue.Arr().Add(JsonValue.Obj().Set("pointer", pointer).Set("message", message));
            return InvalidParams(message, errs, hint);
        }

        public static SkizzyError NotFound(string handle, string reason, string hint = null)
        {
            string msg = reason == "stale" ? "Handle " + handle + " is stale (from before a scene change or reload)"
                : reason == "destroyed" ? "Object " + handle + " was destroyed"
                : "No object " + handle;
            return new SkizzyError(ErrorKind.NotFound, msg, reason, hint, JsonValue.Obj().Set("handle", handle));
        }

        public static SkizzyError Precondition(string reason, string message, string hint = null, JsonValue extra = null)
        {
            return new SkizzyError(ErrorKind.PreconditionFailed, message, reason, hint, extra);
        }

        public static SkizzyError Unavailable(string scene, string[] required, string reason)
        {
            var extra = JsonValue.Obj().Set("scene", scene).Set("required", JsonValue.ArrOf(required));
            return new SkizzyError(ErrorKind.UnavailableInScene,
                "Not available in scene " + scene + (reason == "transition" ? " (scene load in progress)" : ""),
                reason, reason == "transition" ? "Wait for the scene to finish loading and retry." : "Switch KSP to " + string.Join(" or ", required) + " and retry.", extra);
        }

        public static SkizzyError Internal(string message, string traceId)
        {
            return new SkizzyError(ErrorKind.InternalError, message, null, null, JsonValue.Obj().Set("traceId", traceId));
        }

        public static SkizzyError RateLimited(long retryAfterMs, string limit)
        {
            return new SkizzyError(ErrorKind.RateLimited, "Rate limit " + limit + " exceeded", null,
                "Wait " + retryAfterMs + " ms before calling again.", JsonValue.Obj().Set("retryAfterMs", retryAfterMs).Set("limit", limit));
        }

        public static SkizzyError Busy(long retryAfterMs, string why)
        {
            return new SkizzyError(ErrorKind.Busy, why, null, "Retry after " + retryAfterMs + " ms.", JsonValue.Obj().Set("retryAfterMs", retryAfterMs));
        }

        public static SkizzyError Cancelled(string reason, string message)
        {
            return new SkizzyError(ErrorKind.Cancelled, message, reason);
        }
    }
}
