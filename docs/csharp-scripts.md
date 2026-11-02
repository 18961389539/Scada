# C# Advanced Scripts

New user scripts use the `CSharp` calculation-job kind. Existing `Script` jobs retain the numeric DSL and remain editable. They are never automatically converted to C#. Creating a script publishes it. A later save updates the draft only. Scheduled runs and screen buttons execute the published copy. Publish and rollback require Configure; a screen invoke requires Control on the task area.

## Trust And Deployment

**Enabled by default. Process isolation is not a hostile-code sandbox.** Only globally authorized Configure users may compile, preview, save or import C#. Input View, task-area Configure and writable-tag Control permissions are checked separately. The saved user's identity and permissions are rechecked at runtime.

The built-in Worker runs under the **same operating system identity as the server**. It does not configure a restricted token, separate service account, AppContainer, container, firewall, filesystem ACLs or Linux cgroups. Clearing the environment and changing the working directory do not prevent access to files, server databases, credentials or networks accessible to that identity.

Set `Scada:CSharpScripts:Enabled` to false when authors are not trusted or operating-system isolation has not been reviewed. Untrusted authors require an externally managed sandbox/launcher with a low-privilege identity, restricted ACLs and outbound network policy. `DotnetPath` can identify a protocol-compatible launcher, but this repository does not provision that sandbox automatically.

```powershell
dotnet publish src/Scada.Server/Scada.Server.csproj -c Release -o publish/scada
```

Deploy the entire directory: server, `wwwroot/`, and `ScriptWorker/` including runtimeconfig, deps and Roslyn dependencies. The Worker requires an installed .NET 10 runtime even if the server is self-contained. Worker trimming and AOT are unsupported.

```json
{
  "Scada": {
    "CSharpScripts": {
      "Enabled": true,
      "WorkerCount": 2,
      "TimeoutMs": 1000,
      "CompileTimeoutMs": 15000,
      "MemoryLimitMb": 512,
      "WorkerPath": "",
      "DotnetPath": "dotnet"
    }
  }
}
```

An empty WorkerPath resolves to `ScriptWorker/Scada.ScriptWorker.dll` beside the server. Restart after changing settings. WorkerCount is clamped to 1-4; memory to 128-2048 MiB. A slot executes one request at a time; overload returns busy after five seconds. Compilation has a separate deadline. Timeout, cancellation, crash or malformed response terminates the Worker; the next request starts a replacement. Workers recycle after 50 requests and exit with their parent.

Windows Job Objects enforce kill-on-close, one-process membership and per-process committed-memory limits. Other systems have a GC heap limit and wall-clock deadline, **not** native-memory or child-process quotas; use external cgroups/container policy. Neither platform has a CPU-rate quota. Minimal OS/runtime environment variables are forwarded, not server configuration or JWT keys; secrets readable through the inherited OS identity are not protected by this filtering.

## Script Body And Host API

Enter an async C# method body, not a class or project. System, LINQ, collections, text, JSON, globalization, threading and tasks namespaces are imported. Arbitrary NuGet references, package restore and assembly upload are not exposed. Built-in .NET APIs remain available, so the host API is not a security sandbox.

```csharp
var current = context.Read("FT101");
var sample = context.ReadSample("FT101");
if (!sample.Good) context.Log(sample.Quality);
var voltage = context.Read(102);
var power = current * voltage;
context.Output("power", power);
context.Output("highPower", power > 5000 ? 1 : 0);
context.Write(109, power > 5000 ? 1 : 0);
context.Log(power.ToString(CultureInfo.InvariantCulture));
```

Configure ReadTags as `FT101, 102` or `101,102` and WriteTags as `109`. `Read` uses one captured input snapshot and rejects missing, stale, nonfinite or non-Good inputs. `ReadSample` returns that same snapshot without rejecting it. Maximum input age is MaxGapSec.

`context.History(tag)` returns up to 60 raw points when the task declares a history window of 1–3600 seconds. The window covers only the read whitelist, at most 8 tags. `context.Alarms()` returns up to 32 active alarms in the task area at or above the task's minimum level when the alarm snapshot is enabled. Both throw when the task did not declare them. `context.HasArgument` and `context.Argument` carry the numeric value from a screen button; a scheduled run has no argument. Preview and compile return a trace of these calls, capped at 64 lines.

A script library is a named set of local functions, at most 8192 characters, with at most 64 libraries. Saving or publishing a library compiles it with an empty task body, so a syntax error is rejected before it is stored. A C# task names up to 8 of them. They are compiled in the same method, before the task body. Publishing the task freezes the published library source, so a later library edit does not change a running task until that task is published again. The editor picks read and write tags by name and lists screens that run the task, plus tag names in the script that no longer resolve. A screen button sets the numeric argument shown in the confirmation and passed as `context.Argument`.

Output names must be compile-time strings, start with an ASCII letter, and contain at most 32 ASCII letters, digits or underscores. Saving creates read-only derived tags. A successful run that does not assign an output publishes Uncertain instead of retaining a stale Good value. Limits: 16384 source characters, 64 input tags, 16 writable tags, 32 outputs and 128 Ki characters per protocol message.

Host inputs/outputs are finite doubles; text, SN and arbitrary object bindings are not exposed. Now is UTC snapshot time; DeltaSeconds is elapsed time. Log allows 16 messages per execution, each at most 1024 characters.

Write records a target-value intention, not a direct PLC command. After successful execution the main service validates the manifest, values and whitelist and routes writes through existing permissions, interlocks, local/remote mode, bounds, enabled-state, rate-limit, feedback and audit controls. It rechecks C# task revision/enabled state before sending. Failure or timeout discards write intentions. Multiple physical writes are not an atomic transaction.

Each C# job has at most one in-flight execution and does not hold the common scheduler gate while executing or waiting for feedback. Legacy DSL scheduling is unchanged. Worker caches are bounded; assemblies are not loaded into the main service.

## Compile And Preview

The dialog provides C# highlighting, line numbers, and completion for `context` members, `ReadSample` and history fields, alarm fields, read-list names inside read calls, write-list names inside write calls, referenced library functions, and insertable snippets. A tag name that is not on the matching list is underlined, with a quick fix that adds it. Roslyn diagnostics include the end of the failing token. Compile runs again shortly after typing stops. Ctrl+Enter previews and Ctrl+S saves. Preview does not submit commands. It shows the samples it read and the screen argument when one is entered; edited samples are sent again on the next preview until live values are requested. Outputs and write intentions use tag names. Editing invalidates prior results and keeps the edited samples. The editor follows the application theme. Trigger, tag lists, history and alarms sit in a collapsible section; history and alarm snapshot are shown for C# tasks. A drift alert opens a source diff, and a publication can be read before rollback. The script library uses the same editor. Leaving prompts before discarding an unsaved draft.

**Preview is not a general side-effect rollback.** C# can still attempt file, network or process APIs allowed by its OS identity. "No commands" applies to the controlled context.Write path, not arbitrary user-written network code. External OS isolation is required to prohibit those actions.

## Unknown Physical Command Results

Before submission the engine durably records a generated command ID and target value. Unknown outcomes, interruptions or crashes leave that target blocked from automatic resubmission across cycles and restart. A definite NotSent rejection may retry on a later cycle. Confirmed unchanged values are not resent.

Verify the physical device and command audit before acknowledging unknown commands. It requires Control for the task area and pending tags. Acknowledgement retains the previous requested target as handled without resending; only a later different target can submit another command. This is conservative suppression, not exactly-once physical execution. Corrupt script state is preserved and blocks execution until restored; acknowledgement cannot erase this protection.

## API And Project Packages

Authenticated endpoints under `/api/calc-jobs`:

- `GET /libraries`, `POST /libraries`, `PUT /libraries/{id}`, `POST /libraries/{id}/publish`, `DELETE /libraries/{id}`: reusable C# local functions. Creating a library publishes it.
- `GET /{id}/references`: screens that run the task, and tag names in the draft that no longer resolve.
- `GET /csharp/capabilities`: configured availability and limits.
- `POST /csharp/compile`: validate and compile a CSharp CalcJobRequest.
- `POST /csharp/preview`: validate and execute a snapshot-only preview. Optional `previewArgument` sets `context.Argument`. Optional `previewInputs` replaces live samples for those read-list tag ids. The response `inputs` lists the samples the run used.
- `POST /{id}/publish?revision=`: copy the draft into the published script.
- `POST /{id}/rollback?publicationId=&revision=`: restore one of the stored publications into the draft and the published script, including its trigger settings.
- `GET /{id}/publications`: the latest 20 publications.
- `POST /{id}/invoke?argument=`: run the published script once. Draft preview, monitor mode and mobile do not call it.
- `POST /{id}/acknowledge-writes`: physically verified pending commands.

Configuration/compilation errors return 400, denied permissions 403, unavailable/busy Workers 503, and deadlines 504. Preview runtime failures return a result with success=false and empty writes. Client cancellation terminates the active Worker exchange.

Export preserves the draft and the published C# source, and records each whitelist tag's name. Import resolves those names in the destination and rewrites `context.Read`, `context.ReadSample` and `context.Write` integer arguments in both copies. Strings, comments and name-based calls stay as written. A missing name binding is rejected. An imported script with no published revision is published after validation. DSL `#{name}` rewriting is still not applied inside C# strings or comments.

`context.Read` still fails when the sample is missing, stale, non-finite or not Good. `context.ReadSample` returns `Value`, `Quality` (`Good`, `Bad`, `Uncertain`, `CommFailure` or `Stale`) and `Timestamp` for a whitelisted tag. Names accept a unique tag name or `device/name`; tags without a device use `@name`. The saved whitelist remains numeric ids.

Script jobs can trigger on a period, on a whitelisted tag moving outside a deadband, on an alarm raise, acknowledge or recover at or above a level in the task area, or once when the node becomes active and again when the published revision changes. Saving a draft does not retrigger startup and does not change the running trigger, interval, deadband or alarm edges. The first observed value and alarms already stored before the cursor is set do not run the script. Cooldown and the published interval still limit how often a change or alarm is picked up. `context.Alarms()` uses the same area filter.

Trial licenses include every feature bit. An activated license must list `csharp-scripts` before C# tasks can be saved, compiled or run. Screen documents export `jobId` as `jobRef` and import resolves it to the destination task. Publish of a screen rejects a script action whose task is missing or unpublished. Rollback lists the stored publications and restores the selected one.

## Focused Checks

```powershell
dotnet test tests/Scada.Engine.Tests/Scada.Engine.Tests.csproj --filter "FullyQualifiedName~CSharpScriptTests|FullyQualifiedName~CalcJobTests"
dotnet test tests/Scada.Server.Tests/Scada.Server.Tests.csproj --filter "FullyQualifiedName~CalcJobApiTests"
npm --prefix web run build
npm --prefix web run i18n:check
```

Use an isolated artifacts path if another server locks normal build outputs. Browser coverage in `web/tests/calc-jobs.spec.ts` requires an explicitly enabled, dedicated development server through PW_BASE_URL. Never enable C# on production solely to run tests.