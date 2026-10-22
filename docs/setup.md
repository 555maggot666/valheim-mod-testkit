# Setup: headless server and headless client

## Server (free, no Steam account)

The dedicated server is a separate Steam app (**896660**) that does **not** require owning the game:

```bash
mkdir -p ~/valheim && cd ~/valheim
~/steamcmd/steamcmd.sh +force_install_dir ~/valheim \
  +login anonymous +app_update 896660 validate +quit
```

RAM: ~1.5–2 GB. A world is generated on first start; a scenario can therefore test a fresh world and a
restart (the second run proves that persisted state is reloaded).

Deploy BepInEx (the pack from Thunderstore works for the server too):

```bash
# BepInExPack_Valheim contains: BepInEx/, doorstop_config.ini, .doorstop_version, doorstop_libs/,
# start_server_bepinex.sh
cp -a BepInExPack_Valheim/. ~/valheim/
```

Then `scripts/run-suite.sh` handles the rest: it copies your plugin directory into
`~/valheim/BepInEx/plugins/testkit-run/`, starts the server, waits for
`BepInEx/config/valheim-testkit/report.json` and returns its exit code.

## Client (needs one game license — yours)

> **Partially verified.** A client install (app 892970, ~3.7 GB) boots on this machine under Xvfb + llvmpipe and
> BepInEx loads the mod, the kit and the suite (`3 loaded, 0 skipped, 0 failed`). What is *not* proven here: joining
> a world. The client log shows `InvalidOperationException: Steamworks is not initialized` six times because no
> Steam client is running, and Valheim's `ZNet` is Steam networking — a client without Steam can boot but never
> join or host. Run the client suite with Steam logged in (your desktop is the realistic place).

Only needed for client-side behaviour (popups, hud, "does the plugin load in the client build"). Use a
real Steam login; do **not** copy a game between accounts.

```bash
~/steamcmd/steamcmd.sh +force_install_dir ~/valheim-client \
  +login <your-account> +app_update 892970 validate +quit
```

### Running it without a desktop

Valheim is a Unity game: the client needs a rendering device even when nobody watches. With no GPU, Mesa's
software Vulkan (`lavapipe`) is enough to boot and load a world — slow (1–5 fps, world load in minutes) but
sufficient, because suites assert on state, not on frames.

```bash
sudo apt install xvfb mesa-vulkan-drivers
Xvfb :99 -screen 0 1280x720x24 &
export DISPLAY=:99
# llvmpipe is the only ICD here; the loader picks it up on its own. Confirm with:
#   vulkaninfo --summary | grep deviceName    -> llvmpipe (LLVM ..., 256 bits)
cd ~/valheim-client
./start_game_bepinex.sh -screen-width 1280 -screen-height 720 -screen-fullscreen 0
```

Check the GPU with `vulkaninfo --summary` (expect `llvmpipe`). Put the same plugin set into
`~/valheim-client/BepInEx/plugins/` and let a suite with `RequiresClient => true` run: it can call
`Screen.Capture("name")` to save a PNG of the render output, which works under Xvfb because it renders the
camera into a texture instead of photographing a window.

### Running the client suite

`scripts/run-client-suite.sh` does the deploy/start/wait cycle for a client, like `run-suite.sh` does for a
server. It starts Xvfb itself when the display is not up yet.

```bash
./scripts/run-client-suite.sh --client-dir ~/valheim-client \
                              --plugin-dir ./out/plugins \
                              --connect 127.0.0.1:2464 \
                              [--password <pw>] [--timeout 600]
```

`--connect` is passed to the game as `+connect <ip:port>`; `--password` as `-password <pw>`. A Steam client
logged in as the owning account is still required (see above).

### Two clients on one machine?

Not worth it: one Steam account cannot be logged in twice, and each client wants 4–6 GB RAM. When a scenario
needs "another player", use the synthetic peer (`RpcSynth.Deliver`) — it exercises the same handler a real peer
would. Keep exactly one real two-player session per release for the visual check.
