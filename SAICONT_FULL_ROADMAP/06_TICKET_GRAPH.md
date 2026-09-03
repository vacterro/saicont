# TICKET GRAPH

```text
T-131  Windows baseline proof
  |
  +--> T-132  serialized install lifecycle
  +--> T-133  observable safety teardown failures
  |
  v
T-134  evidence/accounting closeout
  |
  v
T-135  WATCH deauthorization generation
  |
  +--> T-136  immutable rule-policy swap
  |      |
  |      v
  +----> T-137  shared runtime-control generation contract
             |
             v
          T-138  GUI/TUI race parity suite
             |
             v
          T-139  health schema
             |
             +--> T-140 atomic health writer
             +--> T-141 stable decision codes
                     |
                     v
                  T-142 status.ps1 -Json
                     |
                     v
          T-143 self-test extraction
             |
          T-144 options/path extraction
             |
          T-145 fault/race seams
             |
          T-146 boundary guard
             |
             v
          T-147 adversarial soak
             |
          T-148 package script
             |
          T-149 docs truth pass
             |
             v
          T-150 v1.2.0 release/tag
```

## Recommended ticket batches for an implementation agent

Do not give the agent all tickets as one giant goal. Use these batches:

- Batch A: T-131..T-134
- Batch B: T-135..T-138
- Batch C: T-139..T-142
- Batch D: T-143..T-146
- Batch E: T-147..T-150

A batch may stop early on a real blocker. Never skip a P0 predecessor just because a later feature is more entertaining.
