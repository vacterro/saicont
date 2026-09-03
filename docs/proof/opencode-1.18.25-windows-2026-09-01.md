# OpenCode transport proof — Windows / ConPTY

Captured on 2026-09-01 from the installed `opencode` 1.18.25 in
`V:\___VAC\__K\__CODE\_AI_STUFF_AGENTIC\_SAIPAL`.

Command under test:

```text
opencode --hostname 127.0.0.1 --port 41961 .
```

The process displayed the OpenCode TUI in the PowerShell PTY. The endpoint
was reachable independently of the TUI screen buffer.

## Verified routes

```text
GET /global/health                 200 {"healthy":true,"version":"1.18.25"}
GET /doc                            200 OpenAPI 3.1 document
GET /event                          200 SSE stream (project-scoped)
GET /global/event                   200 SSE stream (global)
GET /session                        200 session list
GET /session/status                 200 object keyed by session ID
GET /session/:id                    200 session detail
GET /session/:id/message            200 message list
POST /session/:id/message           200 synchronous message response
```

`/session` without `directory` returned many historical sessions. The SAIPAL
directory had multiple plausible sessions, so choosing the newest session is
not safe. The proof session was explicitly created with
`POST /session?directory=V%3A%5C...%5CSAIPAL` and was bound to:

```text
session = ses_fa406a114ffe5haUlwT8x1AgRG
directory = V:\___VAC\__K\__CODE\_AI_STUFF_AGENTIC\_SAIPAL
agent = build
provider = sairoute
model = SAICRAN
```

## Actual message proof

The following real API request returned HTTP 200 and an assistant message in
the same exact session. The response contained `role=assistant`,
`sessionID=ses_fa406a114ffe5haUlwT8x1AgRG`, `modelID=SAICRAN`,
`providerID=sairoute`, and text `PROOF_OK`.

```json
{"agent":"build","model":{"providerID":"sairoute","modelID":"SAICRAN"},"parts":[{"type":"text","text":"Reply with exactly PROOF_OK and nothing else."}]}
```

The complete response is available in the terminal capture for this proof;
the stable identifiers and lifecycle samples below are copied verbatim from
the live SSE stream.

## Actual SSE samples

OpenCode 1.18.25 emits `data:` JSON whose useful event is nested under
`payload`; project-scoped events also carry `directory` and `project`.

```text
data: {"payload":{"id":"evt_05bf92401001xhzdl8HfvpcmLK","type":"server.connected","properties":{}}}
data: {"directory":"V:\\___VAC\\__K\\__CODE\\_AI_STUFF_AGENTIC\\_SAIPAL","project":"global","payload":{"id":"evt_05bf95eec001KFiG0ixtPVyUee","type":"session.created","properties":{"sessionID":"ses_fa406a114ffe5haUlwT8x1AgRG"}}}
data: {"directory":"V:\\___VAC\\__K\\__CODE\\_AI_STUFF_AGENTIC\\_SAIPAL","project":"global","payload":{"id":"evt_05bf9b4e10016TxNQzDL56mwBG","type":"session.status","properties":{"sessionID":"ses_fa406a114ffe5haUlwT8x1AgRG","status":{"type":"busy"}}}}
data: {"directory":"V:\\___VAC\\__K\\__CODE\\_AI_STUFF_AGENTIC\\_SAIPAL","project":"global","payload":{"id":"evt_05bf9e89c001LIqv2U58ymuSU6","type":"session.status","properties":{"sessionID":"ses_fa406a114ffe5haUlwT8x1AgRG","status":{"type":"idle"}}}}
data: {"directory":"V:\\___VAC\\__K\\__CODE\\_AI_STUFF_AGENTIC\\_SAIPAL","project":"global","payload":{"id":"evt_05bf9e89c002lwR90aBJykwXO7","type":"session.idle","properties":{"sessionID":"ses_fa406a114ffe5haUlwT8x1AgRG"}}}
```

The live stream also emitted `message.updated`, `message.part.delta`,
`message.part.updated`, `session.updated`, and `sync` frames. `session.status`
was observed as `busy` then `idle`; the response completed with `finish=stop`.

## Error evidence

The same installed server's persisted message API contains real
`internal_error` observations from an existing SAIPAL session:

```json
{"session":"ses_fa9340514ffe4SpHcpHCap14La","message":"msg_057c072f6001SFgGXkmQy9AMqE","role":"assistant","error":{"name":"UnknownError","data":{"message":"{\"message\":\"An internal error occurred. Please try again later.\",\"code\":\"internal_error\"}"}}}
```

This is persisted message evidence, not an invented screen-buffer string.
Live error/retry induction remains a required acceptance item; the transport
must treat `session.error`, `status.type=retry`, malformed events, and an SSE
disconnect without reconciliation as fail-closed.

## Live SAICONT observation

With a real TUI still running on port 41961, a second proof session was
created and sent a deliberately invalid model through the real API. OpenCode
returned HTTP 500. The independently running SAICONT dry-run watcher then
logged the exact project/session binding and the API-derived failure:

```text
session = ses_fa3f4695dffeqJUQf3tRQ8vIrl
HTTP POST /session/ses_fa3f4695dffeqJUQf3tRQ8vIrl/message -> 500
SAICONT: trigger=True ready=False busy=False failure=MODEL_NOT_FOUND
```

The watcher did not read the TUI/ConPTY buffer and did not send `cc` because
the observed failure was permanent and the session was not send-safe. This
proves live API detection and fail-closed behavior; it is not yet a proof of
automatic transient-error continuation.

The direct project-scoped `/event` capture for that run contained the real
sequence `session.status=busy`, `session.error`, `session.status=idle`, and
`session.idle`; those raw frames are stored in the adjacent NDJSON evidence
file. The error payload was `UnknownError` with `Model not found...`, so it is
intentionally classified as permanent `MODEL_NOT_FOUND`, not mislabeled as a
transient retry.

## Exact-session `cc` commit proof

An isolated proof target (`initialDelaySeconds=10`, trigger `PROOF_OK`) was run
against the same fixed endpoint. SAICONT observed the exact session after the
real API message completed, entered `send=pending`, and issued the API request
without console input. The persisted message list then contained:

```text
user      msg_05c1be24b001Yn2Uua6XIYqdcD  cc
assistant msg_05c1be255001oWUtq0P20YGpsA    sairoute/SAICRAN
```

The preceding `PROOF_OK` assistant response and both continuation records used
the same session `ses_fa3e4eb70ffeGcfQqP3v11tkAf`; no model/provider switch was
observed. The provider remained busy longer than the bounded watcher operation,
so this deliberately does not claim post-generation success: a pending
operation is not accepted merely because a request was started. This is live
evidence of exact-session commit and context preservation, not a substitute for
the still-open transient/internal-error acceptance row.
