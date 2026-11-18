# Broker jobs and interrupted waits

[Contents](README.md#contents) · [Русская версия](broker-jobs-and-interrupted-waits.ru.md)

Local model commands and human-readable model downloads report the durable broker job ID as soon as it has been accepted. With `model pull --progress-json`, the same ID appears on stderr in the existing version-one progress shape: `phase="other"`, `status="broker_job:<id>;joined=false"` (or `joined=true`). Download byte counters keep their existing meaning.

```powershell
localai job status <job-id>
localai job wait <job-id> --timeout-seconds 1800
localai job cancel <job-id>
```

All three commands support `--json`. They target the current installation's default runtime; they do not select a different `--runtime-root` used by a targeted status or preflight command. Status and cancellation do not start or restart the shared broker. An older or unverifiable host returns `job_cancel_unsupported` rather than accepting an unsupported cancellation request.

Ctrl+C or a wait timeout stops that client's wait and preserves the job. `job wait` reattaches to **one broker job** and returns its cached result inside an untrusted-content boundary. It neither repeats inference nor resumes the original command's remaining fragments, validation or output-writing steps. A multi-step `triage`, translation or documentation build may require those caller-side steps after a job has finished; a completed broker job alone does not prove that its whole command completed.

Only the creator receives cancellation authority. CLI clients save that authority privately under the runtime's `client-handles` directory; joining a deduplicated job does not grant it or erase an existing owner record. Knowing a job ID is sufficient to inspect or wait for it, but not to cancel it. Do not include private owner records in diagnostic exports.

`Requested` confirms a cancellation request, not a terminal outcome. Check status for `Cancelled`; normal completion can win a race with cancellation. If the worker lease expires, a broker that supports cancellation honors the stored request before starting a replacement attempt. Other clients' jobs continue. Model-download error responses now distinguish `job_wait_detached`, `job_wait_timeout` and `job_cancelled`, with the canonical `jobId`. For `model pull`, stopping a wait or confirming cancellation retains exit code 4; a wait timeout uses 75, and failure to save private ownership information uses 73. The version-one download progress record shape remains compatible.

---

← [Documentation packages](documentation-packages.md) · [Contents](README.md#contents) · [Bounded navigation diagnostics](bounded-navigation-diagnostics.md) →

<!-- Generated from the README by DocumentationSiteTests. -->
