# C# Advanced Scripts

New user scripts use the `CSharp` calculation-job kind. Existing `Script` jobs retain the numeric DSL and remain editable. They are never automatically converted to C#. Saving still activates a revision immediately; draft/publish/rollback is not implemented.

## Trust And Deployment

**Disabled by default. Process isolation is not a hostile-code sandbox.** Only globally authorized Configure users may compile, preview, save or import C#. Input View, task-area Configure and writable-tag Control permissions are checked separately. The saved user's identity and permissions are rechecked at runtime.

The built-in Worker runs under the **same operating system identity as the server**. It does not configure a restricted token, separate service account, AppContainer, container, firewall, filesystem ACLs or Linux cgroups. Clearing the environment and changing the working directory do not prevent access to files, server databases, credentials or networks accessible to that identity.

Keep the feature off unless authors are trusted and deployment administrators have reviewed scripts and applied appropriate OS restrictions. Untrusted authors require an externally managed sandbox/launcher with a low-privilege identity, restricted ACLs and outbound network policy. `DotnetPath` can identify a protocol-compatible launcher, but this repository does not provision that sandbox automatically.

```powershell
dotnet publish src/Scada.Server/Scada.Server.csproj -c Release -o publish/scada
```

Deploy the entire directory: server, `wwwroot/`, and `ScriptWorker/` including runtimeconfig, deps and Roslyn dependencies. The Worker requires an installed .NET 10 runtime even if the server is self-contained. Worker trimming and AOT are unsupported.

```json
{
  "Scada": {
    "CSharpScripts": {
      "Enabled": false,
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
var current = context.Read(101);
var voltage = context.Read(102);
var power = current * voltage;
context.Output("power", power);
context.Output("highPower", power > 5000 ? 1 : 0);
context.Write(109, power > 5000 ? 1 : 0);
context.Log(power.ToString(CultureInfo.InvariantCulture));
```

Configure ReadTags as `101,102` and WriteTags as `109`. Read uses one captured input snapshot and rejects missing, stale, nonfinite or non-Good inputs when accessed. Maximum input age is MaxGapSec. No driver, server service or database context is passed to the Worker.

Output names must be compile-time strings, start with an ASCII letter, and contain at most 32 ASCII letters, digits or underscores. Saving creates read-only derived tags. A successful run that does not assign an output publishes Uncertain instead of retaining a stale Good value. Limits: 16384 source characters, 64 input tags, 16 writable tags, 32 outputs and 128 Ki characters per protocol message.

Host inputs/outputs are finite doubles; text, SN and arbitrary object bindings are not exposed. Now is UTC snapshot time; DeltaSeconds is elapsed time. Log allows 16 messages per execution, each at most 1024 characters.

Write records a target-value intention, not a direct PLC command. After successful execution the main service validates the manifest, values and whitelist and routes writes through existing permissions, interlocks, local/remote mode, bounds, enabled-state, rate-limit, feedback and audit controls. It rechecks C# task revision/enabled state before sending. Failure or timeout discards write intentions. Multiple physical writes are not an atomic transaction.

Each C# job has at most one in-flight execution and does not hold the common scheduler gate while executing or waiting for feedback. Legacy DSL scheduling is unchanged. Worker caches are bounded; assemblies are not loaded into the main service.

## Compile And Preview

The dialog provides C# highlighting, line numbers, Roslyn diagnostics, Compile and Preview (no commands). Preview displays outputs, write intentions and logs without saving jobs, creating derived tags, changing runtime state or invoking the SCADA control writer. Editing invalidates prior results. Leaving prompts before discarding an unsaved draft.

**Preview is not a general side-effect rollback.** C# can still attempt file, network or process APIs allowed by its OS identity. "No commands" applies to the controlled context.Write path, not arbitrary user-written network code. External OS isolation is required to prohibit those actions.

## Unknown Physical Command Results

Before submission the engine durably records a generated command ID and target value. Unknown outcomes, interruptions or crashes leave that target blocked from automatic resubmission across cycles and restart. A definite NotSent rejection may retry on a later cycle. Confirmed unchanged values are not resent.

Verify the physical device and command audit before acknowledging unknown commands. It requires Control for the task area and pending tags. Acknowledgement retains the previous requested target as handled without resending; only a later different target can submit another command. This is conservative suppression, not exactly-once physical execution. Corrupt script state is preserved and blocks execution until restored; acknowledgement cannot erase this protection.

## API And Project Packages

Authenticated endpoints under `/api/calc-jobs`:

- `GET /csharp/capabilities`: configured availability and limits.
- `POST /csharp/compile`: validate and compile a CSharp CalcJobRequest.
- `POST /csharp/preview`: validate and execute a snapshot-only preview.
- `POST /{id}/acknowledge-writes`: physically verified pending commands.

Configuration/compilation errors return 400, denied permissions 403, unavailable/busy Workers 503, and deadlines 504. Preview runtime failures return a result with success=false and empty writes. Client cancellation terminates the active Worker exchange.

Export preserves C# source verbatim and records original tag-ID bindings. Import accepts it only when destination tag bindings retain the same IDs; changed or missing bindings return an explicit error. Rebind and review source and whitelist IDs in the destination manually. Automatic semantic C# remapping is not implemented, and DSL rewriting is never applied to C# strings or comments.

## Focused Checks

```powershell
dotnet test tests/Scada.Engine.Tests/Scada.Engine.Tests.csproj --filter "FullyQualifiedName~CSharpScriptTests|FullyQualifiedName~CalcJobTests"
dotnet test tests/Scada.Server.Tests/Scada.Server.Tests.csproj --filter "FullyQualifiedName~CalcJobApiTests"
npm --prefix web run build
npm --prefix web run i18n:check
```

Use an isolated artifacts path if another server locks normal build outputs. Browser coverage in `web/tests/calc-jobs.spec.ts` requires an explicitly enabled, dedicated development server through PW_BASE_URL. Never enable C# on production solely to run tests.