# Valheim Mod TestKit

Run **real** Valheim code in a **real** game process — dedicated server or client — from a test suite, without
a second Steam account and without clicking through the game.

Modders usually test by hand: build, start a server, join with two clients, look at the screen. That misses
regressions and cannot run unattended. This kit keeps the part that must be real (the Unity runtime) and
automates everything around it.

```
scenario (C#)  ──►  ValheimTestKit plugin  ──►  live game process (server or client)
                          │                              │
                          ├── RpcSynth: fake peer        ├── real ZRoutedRpc handlers
                          ├── Reflect: probe mod state   ├── real ZNet / Player / Hud
                          └── report.json  ◄─────────────┘
```

## What it can and cannot prove

| Question | Covered by |
| --- | --- |
| Wire format, chunking, decoding, dedupe | suite in the live server |
| Server receive path, ledger persistence, restart stability | suite in the live server (`RpcSynth.Deliver` with a fake sender) |
| Client apply path, popup queue, hud guards | suite in a live **client** (`RequiresClient`) |
| Does the plugin load in the client build | client run, log line |
| Icon, position, timing, "feels right" | **only** a real session with two humans |

It does not fake gameplay: it calls the handlers the game itself calls, and reads the state the game itself
uses. It also does not replace a real two-player smoke test — it makes one unnecessary for every regression
that is not visual.

## Layout

```
src/ValheimTestKit/        the plugin: suite discovery, report, RpcSynth, Reflect, Screen
scripts/run-suite.sh       deploy plugins into a server dir, start it, wait for the report
examples/                  a minimal suite you can copy
docs/setup.md              getting a headless server and a headless client
```

## Quick start (server side)

```bash
# 1. build the kit
VH_MANAGED_DIR=~/valheim/valheim_server_Data/Managed \
BEPINEX_CORE_DIR=~/valheim/BepInEx/core \
dotnet build src/ValheimTestKit -c Release

# 2. write a suite (see examples/MinimalSuite.cs), build it as its own plugin DLL

# 3. run it
./scripts/run-suite.sh --server-dir ~/valheim \
                       --plugin-dir ./out/plugins \
                       --world testkit --port 2464
```

Exit code 0 means zero failures. The report lands in
`<server>/BepInEx/config/valheim-testkit/report.json`.

## Writing a suite

```csharp
[TestKitSuite("mymod")]
public sealed class MySuite : TestSuite
{
    public override IEnumerator Run()
    {
        yield return Scenario("ledger-dedupe", () =>
        {
            AssertEqual(0, Something.Dedupe(...), "duplicates");
        });

        // drive a mod's RPC handler as if a remote player had sent it
        yield return Scenario("remote-unlock", () =>
        {
            ZPackage pkg = BuildPayload();
            Assert(RpcSynth.Deliver("MYMOD_New", RpcSynth.Rewind(pkg), sender: 987654321L), "no handler");
        });
    }
}
```

The suite assembly must declare **some** `[BepInPlugin]` class, otherwise BepInEx never loads it. A stub is
enough — see `examples/SuiteLoader.cs`.

## Rules that keep this honest

- Suites assert on **game state**, not on log text.
- `RpcSynth.Deliver` bypasses the network, not the handler: sender ids are yours to choose, so "another player"
  is one integer.
- Anything that needs a human eye belongs in a manual checklist, not in a passing test.
- The kit is test-only: it is never shipped in a mod package.
