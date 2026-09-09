# The bot-layer traps

## ⚠️ The bot layer is not held to the reference (2026-08-27)

**The user's call, and it reverses CLAUDE.md's standing "the reference is the
specification" rule for `src/Gg2.Bots/` only:**

> "The reference bot implementation should be considered incomplete. We began
> this C# port project precisely because testing and troubleshooting the GML
> implementation was too slow. Ignore the GML implementation — make the bot
> implementation here better if straightforward. However, also don't spend too
> much effort chasing perfection — just document known issues if not a
> straightforward fix. Parts of the bot implementation here will be replaced via
> an RL model in a future phase."

What it licenses, and what it does not:

- ✅ **A bot's decisions are free.** A bot is an `IInputSource` like a socket, so
  what it *presses* is this project's own. Improve it where the fix is
  straightforward, and **measure the change** — `gg2-server eval`, or
  `MatchRunner` over ten-plus seeds. ⚠️ A single match is not evidence; seed
  variance on `ctf_truefort` is large enough that one match proves nothing.
- ✅ **Document rather than chase.** A hard one goes in "Known issues" below.
- ❌ **The wire and observable simulation are NOT covered.** A bot may press
  "drop intel"; `MatchSimulation` may not decide to drop one. Anything a client
  can see is still the reference's.
- ⚠️ **A departure must say so where it lives**, naming the reference line it
  leaves and what it was measured against. `PathFollower.OffRoute` is the worked
  example. Without that a later session reads the divergence as a porting mistake
  and "restores fidelity", silently undoing it.

### Departures taken so far

| Where | What | Measured |
|---|---|---|
| `PathFollower.OffRoute` | The off-route fail counter ignores the `(-1,-1)` "no edge running" sentinel, so a real edge can accumulate the two consecutive misses the blacklist wants. `botPathKeys.gml:178-192` counts sentinel misses too, which resets the count and spends the ban on a pair `BlacklistEdge` discards — so the loop escape never reaches the edge it is for, and the reference's own comment describes the loop it then fails to break. | 10 seeds, full rounds, `ctf_truefort`: 6v6 **0.4 → 1.0** captures a match and 5% → 8% conversion; uncontested 7.3 → 7.4; the carrier loop gone from every seed that had it. |
| `ScriptedBot` carrier failsafe | A bot drops the intel after 300 ticks without 24px of headway, **or** 4,000 ticks holding it at all. The reference has no such rule. | Uncontested captures a match 5.4 → 7.3; contested play unmoved (the longest contested carry is 3,180 ticks against a 4,000 ceiling). |

### Known issues, not chased

- ⚠️ **One uncontested seed in ten still carries for ~5,100 ticks** before the
  4,000-tick ceiling resolves it — a drop and a re-grab rather than a lock, so
  the flag does come back into play. Not chased.
- ✅ **The jump branch of the follower is ported** (item 4b-ii, `c34549b`) and so
  is the rocket jump (`55692be`). The only unhandled edge kind is
  `NavEdgeType.StickyJump`, and **all 24 shipped nav graphs contain zero of them**
  (Walk 18,785, Fall 11,558, Jump 67,013, DropThrough 5, DoubleJump 2,382,
  RocketJump 16,014, StickyJump 0, MoveBox 0).
- ✅ **Team Deathmatch has a goal** — `BotObjective.Deathmatch`, a spot anchored
  on the nearest enemy. `tdm_mantic` runs a real match with eight bots moving,
  and team clumping is down rather than up from the goal-less baseline.
- ⚠️ **Conversion at 6v6 is 8%**, and nothing establishes whether that is
  *right*. There is no human baseline in this project.
- ⚠️ **7 of 528 bot-matches still freeze a bot for 900+ ticks**, down from 98 —
  see "A bot that arrives stops moving" below. Every one is a Demoman, Engineer
  or Heavy handed an objective that **cannot be reached without a jump they do
  not have**; the layer never asks whether a goal is reachable for the profile
  that has to walk it.


Every ⚠️ in this file cost a session time. One section per item of phase 7, in
the order they landed: what the item is, which reference scripts it came from,
which tests gate it, and the traps that were paid for on the way.

**This file is the distillation, not the specification.** `docs/BOTAI.md` is the
structural contract the bot layer has to satisfy and `docs/ARCHITECTURE.md` has
the seam; what is here is the part a session needs *in front of it* while it
edits `src/Gg2.Bots/`.

⚠️ **Nothing here may be re-derived.** A measurement quoted here was taken
against the running game or against a pinned fixture, and several of them are
pinned exactly in both directions — a failure there is the **re-record**, not a
broken test.

### Phase 7 item 1: the input seam, and a second place every command now lives

`Gg2.Simulation.{IInputSource, IBatchInputSource, WorldView, PlayerCommand,
PlayerCommandBuffer, ICommandSource}`, `Gg2.Server.NetworkInputSource`,
`GameServer.{GatherInputs, ServiceBots, AddBot, RemoveBot, Execute*}`.
`docs/ARCHITECTURE.md`, "Bots", has the full rules; these are the traps:

- ⚠️ **`RosterEntry.Input` is gone; there is a `Source` instead**, and a socket
  is an `IInputSource` like everything else. `NetworkInputSource` **holds** the
  last `INPUTSTATE` between packets on purpose — `processClientCommands.gml:267`
  writes `keyState` and nothing clears it, so returning `PlayerInput.None`
  between packets turns every dropped packet into a stutter.
- ⚠️ **When adding a client command, add it in three places, not one**: the
  socket switch, `PlayerCommandKind`, and `GameServer.Execute`'s dispatch — and
  put the body in a shared `Execute*` method both reach.
  `botServerActions.gml` is explicit that a bot calls "the same server-side
  scripts the network path calls"; a second copy is a bot that can do something
  a client cannot. Same family of mistake as `Timers`' missing Minigun case.
- ⚠️ **A `PlayerCommandBuffer` holds exactly `ClientCommands.PerTickLimit`**,
  and that *is* the flood cap — there is no separate counter. `Push` refuses
  rather than throwing, because a client flooding the socket is clipped, not
  killed.
- ⚠️ **`WorldView` is a `ref struct` over a `ref readonly WorldState`.** Do not
  "simplify" it to a class or to a by-value struct: the first breaks the
  no-allocation rule, the second is a kilobyte copy per bot per decision, and
  both throw away the compiler's refusal to let bot code stash it.
- **Bots are serviced in an ascending pass *after* the clients**, where the
  reference interleaves them by roster index. `ServiceClients` walks backwards
  so a disconnect cannot skip an entry, and a departure compacts the roster —
  running the bots after it is what keeps slot indices stable. Unobservable:
  commands from different players produce independent messages.
- **`AddBot` is `botAdd.gml` minus the `FULL_UPDATE` burst**, and ⚠️ its
  broadcast order is **`CHANGETEAM` then `CHANGECLASS`** — the opposite of the
  order `CompleteJoin` replays an existing roster in.
- ⚠️ **No fixture can gate any of this and none ever will.** No recording in
  this project contains a bot, and the reference's own bots write `keyState` in
  the same event a socket is read in — so the seam is invisible to a trace by
  construction. `InputSourceTests` and `WorldViewTests` are the whole gate, and
  each of their four load-bearing claims was mutation-checked.
- **All five closed-loop parity windows are unchanged by the seam** (357 /
  1,204 / 756 / 489 / 328) — expected, since the parity replayer calls
  `MatchSimulation.Tick` directly and never goes through `GameServer`, but
  checked rather than assumed: a window that moves here is a bug in the seam.

### Phase 7 item 2: the nav cache reader, and a format that is not the obvious one

`src/Gg2.Bots/` — `NavCache`, `NavGraph`, `NavTypes`. Gated by `NavCacheTests`
(built bytes) and `NavGraphFixtureTests` (all 24 real caches). The reference is
`Scripts/BotNav/`; `gg2-agent/tools/navgraph.js` documents the format.

- ✅ **The `NAV_*` and `BOT_*` constants are already ported** —
  `Gg2.Protocol.{NavConstants, BotConstants}`, pinned to `Constants.xml` by
  `ConstantsTests`. Check `Gg2.Protocol` before transcribing anything from
  `Constants.xml`. The one number genuinely not there is the 23-pixel feet
  offset, a literal in the GML; it lives on `NavGraph.FeetOffset`.
- ⚠️ **A `ds_grid_write` string is not doubles packed end to end.** It is a
  12-byte header (uint32 601, uint32 width, uint32 height) then **column-major**
  cells of **sixteen** bytes each, with the double **four** bytes in. Cell
  `(x, y)` is at `12 + 16*(x*height + y) + 4`. The obvious 8-byte reading
  produces a graph that loads without complaint and describes geometry that does
  not exist — every field is a plausible small number in the wrong place.
- ⚠️ **`navNodeFromWorld` *rounds* the feet row; it does not floor it — and
  `gg2-agent/tools/navgraph.js` still floors.** That file is right about the
  format and a revision behind the game on this line. A node's feet sit exactly
  on a row boundary but GG2 rests a character with its feet a pixel *above* the
  surface, so flooring puts a stationary bot one row up. Invisible on a wide
  floor; on a staircase the node above is also one column across, wins the
  `dy*4 + dx` tie-break, and the bot is forever one step behind its own route.
  The round is `Gml.Round`, half to even, because it is GM8's `round()`.
- ⚠️ **Both derived indices are bets on the generator's output, not on the
  reader.** `navEdgeIndex` assumes edges are sorted by from-node and
  `navNodeFromWorld` assumes nodes are sorted by row then x. Neither throws when
  the assumption breaks — the neighbour list is short, or a row walk stops
  early, and every answer downstream is quietly incomplete.
  `NavGraphFixtureTests.TheFileIsSortedTheWayBothIndicesAssume` is what notices.
  ⚠️ And the row index must be built **backwards**, so the lowest index on each
  row survives: the tie-break is a strict `<`, so a forwards build resolves ties
  to a different node.
- ⚠️ **A node's x-span is in anchor columns — the *left edge* of the body box —
  while a character's `x` is the sprite's centre.** Two cells apart. Go through
  `NavGraph.AnchorColumn`/`ColumnWorldX`; converting by hand misses a two-cell
  ledge entirely.
- ⚠️ **Gates are a per-query cost and are deliberately not baked into the
  graph**, because the answer depends on the asker's team *and* whether they are
  carrying intel. **Your own team gate is open unless you are carrying intel out
  of it**, which is why a CTF return leg is a different query from the outbound
  one. An edge asks about its own gate *and* the gate on the node it lands in.
- ⚠️ **A cache is per map *and per area***, and the mask dimensions are the
  map's own — `LevelMap.ForArea` does not narrow the terrain, only the entities.
  `cp_dirtbowl` has three graphs over one mask.
- **A miss is a quiet `false`, not an error** (`NavCache.TryLoad`): a server with
  an ungenerated map in its rotation should keep serving it to humans.
- ⚠️ **The 25 caches ship checked in at this repo's `nav/`** (gzipped), or
  wherever `GG2_NAVCACHE` points. A fresh clone with none makes
  `NavGraphFixtureTests` say so rather than failing as if the tree were missing
  (`ReferenceTree.RequireNavCache`); **188 tests fail without them.** They no
  longer live in the reference's GM8 build directory — that dependency on a
  fork having run the in-game generator is gone, which is what let the
  reference become stock upstream GG2.

### Phase 7 item 3: the scenario harness, and a world with no gamemode in it

`Gg2.Bots.{ScenarioWorld, ScenarioRunner, Scenario, Scenarios}`. Gated by
`ScenarioWorldTests` and `ScenarioDefinitionTests`. The reference is
`gg2-agent/tools/{botscenario,bot-scenarios}.js`.

- ✅ **`ScenarioWorld` is docs/BOTAI.md item 11** — "spawn two characters of
  given classes at given positions on a given map, with no gamemode running, and
  step them". It is a library type rather than a test fixture on purpose: a
  self-play evaluator wants exactly this and must not reach into a test assembly
  for it.
- ⚠️ **What makes a world gamemode-less is the zeroed limits and placing no
  objective — *not* the `GameMode` field.** Measured, by setting the mode to
  TeamDeathmatch, CaptureTheFlag and KingOfTheHill in turn and watching nothing
  change: every mode's Step reads entities the world never placed. The mode is
  set to `TeamDeathmatch` because it is the honest description of a map with no
  objective (`basicRoomSetup`'s chain ends there), not because a test can tell.
  The lines that *do* work are `TimeLimitMinutes`/`CapLimit`/`KillLimit`/
  `RespawnTime`/`TdmInvulnerabilityTicks` at 0, `Autobalance` off, and **no
  `SpawnPoints` handed to `MatchSimulation.Tick`**.
- ⚠️ **`TdmInvulnerabilityTicks` must be 0 and it is only visible at placement.**
  `WorldState.Spawn` hands a TDM character that many ticks of spawn protection;
  a non-zero value makes every combat scenario's damage wrong for its opening
  seconds and reads as a bot that cannot shoot. It decays within a couple of
  seconds, so **a test that looks after a run cannot see it**.
- ⚠️ **`RespawnTime` is only readable on the tick of the death**, for the same
  shape of reason: `Deaths.Kill` arms the timer from it and a timer armed at 150
  is back at zero 150 ticks later whether or not anybody respawned.
- ⚠️ **`ScenarioWorld.Place` spawns and then teleports, and it moves the weapon
  too.** `WorldState.Spawn` is the one transcription of
  `Character.events/Create.xml` and must not be duplicated. Moving the character
  without its weapon is silent until something measures damage — a shot leaves
  from the *weapon*, so the first shot comes from wherever it spawned.
- ⚠️ **A scenario is a travel or a hold, never both and never neither**, and the
  `Scenario` constructor throws rather than defaulting. The reference's reason:
  "a combat scenario that quietly inherited a movement goal would measure the
  wrong thing and still pass."
- ⚠️ **Coordinates are snapped downward to a nav node and the snapped position
  is what is placed** (`botNodeSnap.gml`). A raw objective coordinate usually
  does not resolve — a capture-zone marker can float sixty pixels above its
  floor — and a scenario placed in the void reads as a bot that will not move.
  ⚠️ Most scenario `y` values are already a node's exact stand position, so a
  test that asserts "the snap moved it" must assert on **x**.
- ⚠️ **The fourteen scenarios cannot pass yet, and `Scenarios` says so.** Every
  one runs a bot; the behaviour is item 4. What is gated now is the data —
  `ScenarioDefinitionTests` checks that every coordinate resolves to a real node
  and that every travel leg is reachable *for its team*, which is a second,
  independent exercise of the nav reader against coordinates chosen by asking
  the reference's own graph.
- **`ScenarioExpectations.Acquired` is carried but not evaluated.** It is
  bot-internal state and no `IInputSource` reports it; `ScenarioResult.Satisfies`
  deliberately does not check it rather than passing it silently.
- **Two synthetic scenarios live in the tests** (a held key over flat ground, a
  held trigger against a soft dummy) because the fourteen real ones need a bot
  and a harness only exercised through the thing it measures cannot tell a
  broken bot from a broken runner.

### Island nodes: a nav graph describes the terrain, not the playable map

⚠️ **Do not sample nav NODES as if they were places a match visits.** Nodes are
emitted wherever the walkmask offers standable ground with clearance —
`gg2-nav-gen` floods the whole mask, not from spawns — so a graph carries nodes
in regions no walking character can reach. Routing is immune (A* never reaches
an island over edges); anything that draws *nodes* directly is not, which is
how `gg2-rl`'s banded duel placement came to be able to seed episodes inside
`gg_debug2`'s sealed chambers with no teleport and no intent (2026-09-01, the
finding that made the moving-platform `SimulationStamp` bump unconditional).

**Measured, 2026-09-01** — island nodes per area-1 cache, where "island" means
no cell of the node's stand box is reached by a 4-connected flood over open
cells seeded at that area's spawn entities (a superset of character
reachability, so an undercount if anything; method calibrated to the
platforms lane's independent 18/70 on `gg_debug2`, node-for-node):

| map | islands | mechanism |
|---|---|---|
| `gg_debug2` | 18/70 | the two sealed chambers, by design |
| `arena_montane`, `ctf_2dfort`, `ctf_classicwell` | 0 | — |
| `ctf_avanti` | 1/324 | one stray node |
| `ctf_conflict` | **105/762** | decorative scenery, located and looked at: the red factory's rooftop strip sealed under the map's top edge (one 1,212px node at y222), the 45° faces of the tall blue-side structure (single-cell diagonal chains, y0..240 at x3612..3852 and similar), and enclosed pockets inside the rock masses at the bottom and left. Real standable terrain in sealed air pockets — no cache anomaly |
| `cp_dirtbowl` | **541/835** (a2 626/837, a3 493/837) | ⚠️ **the multi-area trap**: `ForArea` narrows entities and NOT terrain, so every area's cache carries nodes on all three stages' ground while that area's spawns reach only their own stage |

⚠️ **The multi-area row is the general trap**: on any staged map, roughly
two-thirds of every per-area graph is other stages' terrain. A node draw on
`cp_dirtbowl_a1` lands outside the played stage more often than inside it.
`tests/Gg2.Tests/MovingPlatformTests.ReachableFromSpawns` is the checked-in
flood; the census itself was a scratch diagnostic (this table is its record).

### Phase 7 item 4a: A* over the nav graph

`Gg2.Bots.{NavSearch, NavQuery}`, `NavGraph.EdgeKey`. Gated by `NavSearchTests`
(hand-built shapes) and `NavSearchFixtureTests` (all 24 shipped graphs). The
reference is `Scripts/BotNav/navFindPath.gml`, whose header is the best
documentation in the bot layer — read it before changing anything here.

⛔ **The most expensive thing in the server**: one search is 15.2 µs, 30% of a
twelve-bot tick. Breakdown, two exact-equivalence wins, four rejected
candidates, and why the heuristic is not the problem (0.914 of true cost):
`docs/ARCHITECTURE.md`, "Where a headless bot match's tick goes".
⛔ **A\* gives up optimality on purpose**: a closed node is never revisited and
the heuristic is inflated by `NavConstants.SearchWeight` (1.3) — **`Poll`
−18.9%, tick −12.0%** for routes 4% costlier on average, every route still
found (19,468/25 graphs unchanged). ⚠️ **The two halves only work together** —
inflating without the closed-set skip is SLOWER (636 pops vs 408; an
inconsistent heuristic re-opens more). Full measurement: `docs/ARCHITECTURE.md`.

⛔ **The search budget is owned by the objective layer, not the cadence.**
Twelve bots on `ctf_truefort`: **321 searches/1000 ticks**, **181 a goal
reissue** — `PathFollower.SetGoal` sets `ReplanAt = 0`, so any
`BOT_GOAL_RETARGET_DIST` move buys a full A\* next follower tick (re-plan
timer 97, lost route 43).
⛔ **Raising `BotConstants.ReplanTicks` is REJECTED, measured**: 45→90 removes
9.9% of searches for 16.5% of `ScenarioSuiteTests`' walking time (and fails
`gallery-pit-climb` outright); 45→60 is −5.1%/+10.1%; 180 removes only 22%,
since the objective layer keeps forcing re-plans. **Do not spend a session
tuning the cadence.**
⚠️ Call-site counts (278/1000, `tick >= state.ReplanAt`) aren't the timer's
own (97) — every event-driven re-plan sets `ReplanAt = 0` instead of calling
`Plan`. `BotPathState.GoalsSet` records the split; `BENCH_COMBAT` prints it
beside `Replans`.
⛔ **`BOT_GOAL_RETARGET_DIST` BUYS NOTHING EITHER**: 48→96 cuts goal reissues
14% and leaves search count unchanged (321.1→321.2); 48→144 cuts reissues 29%
for 6.4% fewer searches — a staler goal just re-plans from off/out of route
instead. ⛔ **"Fewer searches" is CLOSED as a lever**: the two throttles are
86% of the budget and buy nothing net; a smaller graph is what's left.
⚠️ **No criterion here could catch a `BOT_GOAL_RETARGET_DIST` regression**
(fixed goals throughout, `eval` moves both teams) — the knob would need to
become per-bot first.

⚠️ Reissues are progressive, not oscillation (only 5.5/1000 return within
48px of the prior goal); ⛔ `ScenarioSuiteTests` can't see this lever (one
fixed goal per leg) — needs `BotObjectiveTests`' intel run and `eval`.

⛔ **A quarter of a search is `PriorityQueue`; replacing it is REJECTED** — a
hand-written 4-ary heap matching .NET's tie-breaking exactly (425,000 pops
checksummed identical) is only 7% faster, half a percent of a tick.
✅ **`NavRouteIdentityTests` pins exact routes on all 25 graphs** — every other
nav test tolerates a different route; reversing a node's edge-expansion order
reddens only this one.

- ⚠️ **A closed node is RE-OPENED when a cheaper route arrives** — what makes
  the search optimal, not merely fast; sound only for a *consistent*
  heuristic, stronger than admissibility. ctf_truefort's blue leg: 68→60 nodes.
- ⚠️ **The gate test is on the EDGE, never the node it lands on** — an arc can
  cross a gate without landing on it. **2026-08-27, all 24 graphs: 115,757
  edges, 1,350 stamped, 148 gated nodes, zero unstamped landings**
  (`NavSearchFixtureTests.NoEdgeLandsOnAGatedNodeUnstamped` catches a
  regression). `NavGraph.EdgePassable` checked the landing node too until this
  landed, ported from `gg2-agent/tools/navgraph.js`'s deliberately
  conservative `reach`. **The game is the specification**: a bot inside a gate
  it may not pass still gets edges *out*.
- ⚠️ **Refusing a class arc is the DEFAULT** — `CanDoubleJump`/`CanRocketJump`
  default false, since the opposite routes a Heavy over an arc it can't fly.
  `CanRocketJump` is **not a class property**: weapon plus `hp >
  NAV_RJ_DAMAGE` (strictly greater — damage lands before the bot does), so a
  soldier's routes shed rocket jumps as it takes damage.
- ⚠️ **The blacklist is a set, never a cost multiplier** (would break
  admissibility); per bot, hence a parameter, not part of the graph.
- ⚠️ **`occupancy` is the only safe way to perturb a cost; per-edge jitter is
  not.** Deleted from the reference — mean-reverting jitter inflates a
  sixty-edge route's candidates by the same average, so all eight bots on
  ctf_truefort got the identical 68-node route. **Do not re-add it.**
- ⚠️ A fixture must be admissible (`NavSearchTests.AssertAdmissible`: every
  edge ≥ the straight line it covers) or "cheapest wins" passes/fails for
  unrelated reasons — two first-draft fixtures violated it.
- ⚠️ A re-open fixture must make the re-open actually fire — a cheaper route
  isn't enough; it must land on an *already closed* node (near-goal reached
  expensively/popped early, far node cheaply/popped late, goal costly enough
  the far node pops first). `NavSearch.Reopens` is asserted so this can't
  silently stop firing.
- **Two properties are equivalent-by-construction, recorded not gated**
  (mutation-checked): the goal test vs. closed test ordering, and
  reconstruct's `cameFrom`/`gScore` pairing. Kept because the reference keeps
  them.
- **`NavSearch` reuses scratch** (the reference allocates four `ds_grid`s per
  query). ⚠️ **Not thread-safe on purpose** — two frontiers sharing a
  `gScore` produce wrong, not slow, routes.

### Phase 7 item 4b-i: the path follower's spine, and the ground edges

`Gg2.Bots.{PathFollower, BotPathState}`. Gated by `PathFollowerTests` (decisions,
physics taken out) and `PathFollowerFixtureTests` (a real leg walked on
`ctf_truefort` through `ScenarioWorld`). The reference is `botPathKeys.gml`
(1,014 lines, 394 of code) and `botPathPlan.gml`.

✅ **The jump branch is ported** — item 4b-ii, below in this file.

✅ **`NavEdgeType.StickyJump` is handled** — see "The sticky-jump follower arm"
below. `FollowEdge`'s `default` arm covers nothing the generator emits but
still counts rather than throws, so a newer generator's graph degrades into
`BotPathState.UnsupportedEdgeTicks` instead of silently flying an unmodelled
arc — **do not paper over a new kind with an approximate jump**. ✅ **No
shipped graph contains a type-7 edge** (all 24 `nav/` caches), and `NavSearch`
refuses one without `NavQuery.CanStickyJump`, which the reference's own A*
cannot do.

- ⚠️ **The route index advances only while the bot is ON THE GROUND** — the
  single most expensive bug in the reference's follower: `NodeFromWorld`
  tolerates a couple of cells off-surface, so an arc's landing resolves
  ~15px before the feet arrive, handing the follower the *next* edge
  mid-air. On ctf_truefort's two stacked jumps this switched the bot onto
  the return leg at the apex, landing it back where it started, repeatedly.
- ⚠️ **Arrival needs the bot stopped, not merely close** — GG2 keeps
  horizontal speed ~10 ticks after keys release, so stopping cold coasts
  30-40px past the target. Inside the band the bot *brakes* (presses against
  its own motion), with a speed floor of 1 to prevent overshoot the other way.
- ⚠️ **Landing off the route re-plans IMMEDIATELY**, not after riding out
  `BOT_OFFPATH_TICKS`: a finished move that went wrong should not be waited
  out. **Not knowing where you are at all** is the different case and does
  get the timer.
- ⚠️ **The edge is blacklisted only on the SECOND consecutive miss** — many
  real edges are marginal and flown stochastically; banning on the first
  miss cost the reference ~250 frames on a 30-tick hop.
- ⚠️ **A failed search KEEPS the old route**, run before the old route is
  given up — the reference's "bots momentarily stop moving" report,
  structural rather than occasional. Failure parks the next search for a
  re-plan window, or every event-driven caller re-fires next tick — a full
  A* per bot per frame even for a goal unreachable from a pixel away.
- ⚠️ **A stale route never declares an arrival** — it ends where the bot was
  sent *before*; declaring one would clear `HasGoal` and drop the objective.
- ⚠️ **A fall reads its takeoff end off the edge, can't re-derive it** — "the
  column just past the run is inside the landing node" holds at both ends
  when the landing spans the whole run, and guessing wrong wedged a bot on
  ctf_avanti for ~1,100 ticks on a leg priced 545. **The plan is a straight
  vertical drop from that column**; without it a fall flies with no plan.
- ⚠️ **The moved test is half a pixel, not zero** (`good_move_contact_solid`
  is eighth-pixel precision), or a bot grinding a wall jitters in place; the
  stuck test is gated on having pressed something, or a bot waiting on target
  for a jump window reports itself stuck.
- ⚠️ **A ban already imposed is not re-armed** — runs from when first
  imposed, or repeated failures on the same edge extend the ban indefinitely.
- **The anti-thrash check lives with the planner**, catching a bot that's
  technically moving (shuffling a ledge) but getting nowhere. ⚠️ It reads the
  edge in progress, which a successful plan clears, so planning twice without
  stepping finds nothing to blame.

⚠️ **Two testing traps.** A fixture where the bot can get back to its route
makes a second consecutive off-route miss unreachable (the re-plan installs a
correct route from the landing spot, not a gate). And "walk-only way there"
is the wrong question end to end: the follower gets `NavSearch`'s route,
which costs a jump like anything else and may prefer one over legs a bot
could have walked. `PathFollowerFixtureTests.FindWalkableLeg` proposes
candidates and accepts the first whose *planned* route is steerable end to
end.

### Phase 7 item 4b-ii: the jump branch, and a budget that could not be spent

`Gg2.Bots.{JumpModel, PathFollower}`'s jump case, `BotPathState`'s per-arc
fields. Gated by `PathFollowerTests` and `PathFollowerFixtureTests`. The
reference is `botPathKeys.gml:408-1014`, `botJumpReach.gml` and
`botRunupCol.gml`.

✅ **A bot crosses `ctf_truefort` end to end.** Longest steerable leg **137
nodes**; arrives at tick **1,786** with 39 re-plans, **0 stuck fires**, 10
off-route events, **0 blacklists** (`PathFollowerFixtureTests`, pinned
exactly) — beats the reference's own 2,200-2,500 ticks on its *shorter*
`truefort-spawn-to-intel` leg.
⚠️ **A subtly-wrong-everywhere behaviour layer means check what it's reading
before tuning it** — a much worse early reading of this pin traced to a
single `(int)` cast in `NavCache` truncating the velocity every arc is flown
at (item 4f), not to the takeoff machinery it seemed to implicate.

⚠️ **RocketJump is still not flown** — `NavQuery.CanRocketJump` defaults
false; the follower counts any seen in `UnsupportedEdgeTicks`. Needs the
combat half (`botRocketAim`/`botRocketFire`) and a live Rocketlauncher.

- ⚠️ **BOT_TAKEOFF_PATIENCE (60) is unreachable at the default re-plan
  stagger, unremarked in the reference.** A successful re-plan's
  `botPathFree` sets `botEdgeFrom = -1`, and the change-of-edge test zeroes
  the takeoff budget next tick — so the *re-plan interval* is a hard ceiling
  on takeoff budget. `botPathPlan.gml:53`'s interval is `BOT_REPLAN_TICKS +
  (player mod BOT_REPLAN_TICKS)`, **45 to 89**; at stagger 0 it's 45, so 60
  is never reached and the escape hatch is dead, as it was before the
  reference even incremented the counter. `BotPathState.ReplanStagger`
  carries the offset, added not phased.
- ⚠️ **`botPathFree` does NOT reset the takeoff budget** (only the run-up) —
  the port briefly did, wrong, visible only because of the ceiling above.
- ⚠️ **The takeoff column is carried ON THE EDGE, generator-searched** —
  usually the run's end facing the landing, but where the landing surface
  also ends the run (climbing a crate) only a few cells back stays flyable;
  re-deriving it turns those edges back into fictions.
- ⚠️ **An arc is flown as a TRAJECTORY, not a speed**: target
  `takeoffX + dir × min(distance, needVx × airTicks)`, **clamped at the
  arc's own distance** so a late bot keeps pressing toward the landing
  rather than past it. Reference measurement: **12-19% of jumps landed on
  the aimed-at node before, 100% after.**
- ⚠️ **The gate's speed test is `JumpModel.Reach`, not "at the arc's
  speed"** — `along >= 0` accepts a standstill, right for **91.9%** of jump
  edges, catastrophic for the rest (**3,310/62,069 unflyable by a heavy**
  from standstill; 96% of koth_corinth's heavy routes cross one). Demanding
  full speed was tried and lost (647→868, 674→997 ticks, no more arrivals).
  ⚠️ `Reach` is **not monotonic** in start speed (3.50 clears, 3.75/4.00
  fail, 4.25 clears) since arriving ahead of schedule makes the tracker
  brake — do not bisect for a threshold.
- ⚠️ **The gate measures the gap from the takeoff COLUMN, never the bot's
  x** — the tracker's ceiling is `needVx × flightTicks` from the anchor;
  measuring from x (up to `BOT_JUMP_LEAD` cells further) asks for 22px an
  arc can only deliver 12, refusing *silently* on every approach. Credit for
  ground already covered inside the column is only ever a credit, clamped
  to one cell.
- ⚠️ **The run-up follows WALK edges through the GRAPH**, not the route or
  node columns — clamping into a one-column node (a staircase's top step)
  is a no-op, so the jump still happened from standstill. **3,310 unflyable
  → 253, koth_gallery 105 → 0.**
- ⚠️ **A run-up walks BACKWARDS along the route**, invisible to the
  forward-only advance — the locate step suppresses off-route detection
  while a run-up is live, and `PathAt` must NOT move.
- ⚠️ **The second impulse is a PRESS, not a hold**, at the tick the
  generator proved (`NAV_EDGE_REJUMP`), gated on `CanDoubleJump` even though
  the search won't offer these edges elsewhere — a route outlives its plan.
- ⚠️ **`CharacterPhysics.ControlAndFriction` is public now** since the reach
  model reads it live: Begin Step sets it from `moveStatus`, **not**
  `onground`, letting an in-flight tracker correct a slow takeoff.

⚠️ **A testing trap that cost this twice: a fixture arc must cover its own
gap** — a generator would never emit `0.5 px/tick × 20 ticks` across an 18px
gap, and the correct refusal looks like a broken test. And a control relying
on the *search* refusing an edge never reaches the follower: the double-jump
control passed with `CanDoubleJump` deleted until made to plan as a scout
and fly as something else.

### Phase 7 item 4c-i: what to shoot at, and where to point

`Gg2.Bots.{BotTargeting, BotAim}`. Gated by `BotTargetingTests` and
`BotAimTests`. The reference is `botFindTarget.gml`, `botClassRange.gml`,
`botClassMinBand.gml`, `botEnemyWithin.gml`, `botAngleDelta.gml`,
`botAimSolve.gml`, `botAimLead.gml`, `botWeaponBallistics.gml` and
`botFallPredict.gml`.

⚠️ **`botCombatUpdate` is NOT ported** — 221 lines of cadence, trigger and aim
slew, that's 4c-ii — so nothing fires yet: a bot picks a target and knows
where to point but presses no key.

- ⚠️ **Threat is scored in PIXELS, so every term is comparable**:
  `distance − 300×intel − 150×attacker − 120×medic − 60×engineer − 100×wounded
  + range×inside the minimum band`. Worth walking 300px past someone harmless
  to take the carrier; a medic is worth 120 since leaving one alive undoes
  everything else.
- ⚠️ **The minimum-band penalty is a LOCK breaker, not a range rule** — a
  soldier's 110-800px band fires throughout, but pure-distance threat always
  locks the nearest enemy and combat won't retarget while holding a valid
  one; without it, closing inside `BOT_SPLASH_SAFE` pins a soldier
  permanently. `+range` always loses to anything inside the radius — the
  property is the point, not the number.
- ⚠️ **A blocked sightline doesn't mean nothing to fight** (the "bots walk
  straight past enemies" report) — falls back to the nearest candidate
  inside `BOT_BLIND_RANGE` with `Visible` false; the short radius keeps a
  blind target across the map from being aimed at like scenery.
- ⚠️ **Sentries and the enemy generator are candidates too**, aimed at by
  the **centre of the box** vs. a character's origin. A sentry ranks
  `BOT_SENTRY_BONUS` ahead of an equidistant player (unlike a player, it
  can't be walked away from); a generator sits at `BOT_GEN_THREAT` 4,000,
  chosen only when nothing live is available.
- ⚠️ **`AngleDelta` answers -180 for exactly opposite, not +180** (range
  `[-180, 180)`) — decides which way a bot turns to face directly behind it.
- ⚠️ **The ballistics table is the bot's MODEL, not the weapon** — a
  scattergun blast is six pellets with spread; the aim model calls it one
  13 px/tick projectile falling at 0.15. **Do not "fix" it to match
  `Gg2.Simulation.Projectiles`** — tuned against play, and real numbers make
  a different bot. Speed **zero** means "do not lead" (Rifle, Medigun).
- ⚠️ **Drop is `g·t·(t+1)/2`, not `g·t²/2`** — discrete ticks give the
  triangular number, not the integral; 2.5px on a thirty-tick shot, exactly
  what gets "simplified" by reading it as calculus.
- ⚠️ **The fall model has two phases (GG2 has terminal velocity)**, clamped
  at the floor under the target — else a stationary target is predicted to
  fall through the ground. `BotAim.NoFloor` ("no map is this tall") is the
  reference's own sentinel, right over a pit.
- ⚠️ **Flight time is floored at one tick** — distance zero collapses the
  prediction to "aim at your own feet" (`point_direction` of a point to
  itself is 0), so the bot aims dead right.
- **Drift is the shooter's own speed**, subtracted from the target's, only
  for the Scattergun, Shotgun and Minigun.

⚠️ **Three testing traps, map and angle convention:**

1. **Pick the open stretch by MEASUREMENT** — (3400, 456) on ctf_truefort
   looks open and isn't (four threat-bonus cases failed, the further
   candidate behind geometry); sweeping for the widest sightline found
   **(380, 268): clear 1,000px, blocked 80px straight down**, also giving
   the blind-fallback fixture a real blocked line.
2. **Compare aim angles as SIGNED** — GM8 reports `[0, 360)`, so 20° below
   horizontal comes back as 340 and a naive `>` inverts the truth. Go
   through `BotTargeting.AngleDelta`.
3. **With `NoFloor` a STATIONARY target falls forever** — needs a floor; a
   *rising* target over a thirty-tick flight ends below its start, so a rise
   is only visible with the floor well below it. Both fixtures were wrong
   the obvious way first.

### Phase 7 item 4c-ii: the combat controller, and a bot that finally fires

`Gg2.Bots.{BotCombat, BotCombatState, BotClassKeys, BotClassProfile, BotSkill}`.
Gated by `BotCombatTests`. The reference is `botCombatUpdate.gml`,
`botClassKeys.gml`, `botClassProfile.gml`, `botSkillApply.gml`,
`botSkillLerp.gml` and `botAimSpread.gml`.

✅ **A bot now sees, acquires, aims and fires.** ⚠️ Nothing wires it to a server
yet — that is 4d.

⛔ **The combat half is at its floor.** `BotCombat.Step` is **3.2 µs of a
13.2 µs twelve-bot tick (24%)**, fully attributed: `BotClassKeys.For` 0.73
(`BotArc.Clear` 0.47 at 654 ns/call), `KeepOrDropTarget` 0.40, `SolveAim`
0.38, `LookForOne` 0.36, medic ally lookup 0.18, `Perceive` 0.05. **No
unattributed cost, no large exact-equivalence win left.** Table and three
rejected measurements: `docs/ARCHITECTURE.md`, "The bot poll BELOW the top
level". Headlines:

- ⚠️ **`BotAim.FloorUnder` (267 ns) is TWICE `BotAim.Solve` (143 ns)** — the
  ballistic solver is cheap; the expensive half is `NavGraph.SnapObjective`
  (4.0 downward `NodeFromWorld` probes/call).
- ⚠️ **`BotSuppress.Spot` never fires** in a twelve-bot `ctf_truefort` match
  (0/162,000 ticks) — anything measured about it comes from a fixture.
- ⚠️ **Reordering `BotArc.Clear`'s ten segments (likeliest blocker first)
  was costed and REJECTED** (3%) — 58% of calls are clear and walk all ten
  regardless; its rect filter already returns 0.05 rects/call, so the mask
  walk is what remains.
- ⛔ **Below ~100 ns/call a `Stopwatch` measures itself** (`GetTimestamp` 14
  ns, `Frequency` 10 MHz); most combat rows are 17-45 ns/call. **Trust rows
  in the hundreds; run the probe-off control arm** (a fifth of the poll).

**The see → acquire → aim-settled → fire chain is the whole difficulty
model.** Four gates, all off `BotSkill`:

- ⚠️ **The acquire gate holds the AIM, not just the trigger** — no turn
  toward target for `AcquireTicks`, the difference between a bot that looks
  *slow* and one that looks blind; gating only the trigger would make every
  tier turn at the same speed.
- ⚠️ **SPECIAL is gated on acquisition but NOT the fire delay** — reactive
  behaviours (a pyro's airblast, a demoman under someone's feet) can't be a
  second and a half late. **The medic is the exception**: its needle gun
  *is* the right click.
- ⚠️ **The fourth gate is not a clock** — even with every delay expired, the
  trigger waits for slew to settle within `BOT_AIM_SETTLE_DEG` × the
  class's multiplier, or a slow-turning bot sprays the wall it acquired on.
- **No filter, no smoothing** — lag, overshoot and settling fall out of
  three numbers alone: perceive interval, aim interval, turn rate.

- ⚠️ **Aim error is UNIFORM on [-1, 1], not Gaussian** (Quake III's
  `crandom()`), with two multipliers: moving-target scale, and **Quake
  III's close-range penalty** (every tier worse at point blank — an
  anti-frustration measure, without which bots are unbeatable in a brawl).
  ⚠️ It **divides** and is below 1 near the target — reading it as a
  multiplier inverts the intent.
- ⚠️ **The hold-fire roll is once per AIM INTERVAL, not per tick** — a
  withheld burst, not a stuttering trigger.
- ⚠️ **A target is kept for a memory window after the sightline breaks**
  (`Visible` false, snapshot refreshed **only while visible**) — the bot
  engages the instant the line reopens instead of re-paying acquire.
- ⚠️ **A potshot target is kept inside the same widened band that picked
  it** — else the shot is taken and the target dropped next tick, no
  potshot ever leaving the barrel.
- ⚠️ **With no target the aim follows the walk, above `BOT_REST_SPEED`**
  (GG2's own "is it really moving" floor) — `> 0` would turn on mere coasting.
- ⚠️ **`BotSkill.Lerp` is flat below 0.15/above 0.95, three UNEVEN
  segments** (0.15-0.45, 0.45-0.75, 0.75-0.95 — the last two-thirds the
  width, so the top tier gains more per skill point). Five server tiers
  land exactly on 0.15…0.95.
- ⚠️ **A medic presses SPECIAL to shoot**, a pyro holds fire beyond
  `BOT_FLAME_REACH`, a soldier refuses inside its splash-safe band, a
  scoped sniper charges **proportional to range** since the Rifle's damage
  *is* its range.
- ⚠️ **The spy's toggle is `cloak == enemyNear`** — reads as a typo, but the
  spy wants cloaked *without* company, uncloaked *with* it: presses the key
  exactly when the two agree.

**Deliberately not ported, each a single branch, each conservative when
absent** — a missing helper makes a bot shoot *less*, never wrongly:
`botFindAlly` (needles not heals), `botSuppressSpot` (aim holds last-known,
not a doorway), `botArcClear` (fires through cover it would have declined),
`botIncomingProjectile` (no airblast), `botStabWindow` (no stab),
`botMinesArmed` (no detonation).

⚠️ **A `WorldState` local costs ~160KB of stack; four in one test method is a
StackOverflowException, not a failure** — the first `BotCombatTests` draft
held four and crashed the test host. Build one per helper call.

### Phase 7 item 4d: the bot itself, and a stream that must not be the match's

`Gg2.Bots.ScriptedBot`. Gated by `ScriptedBotTests`. The reference is
`botInputUpdate.gml`.

✅ **A bot walks to a goal, acquires an enemy, fires and hurts it.** It is an
ordinary `IInputSource`, so `GameServer.AddBot` takes it and no client can tell
it from a socket.

- ⚠️ **The two halves are ORed and cannot conflict.** `BotCombat` owns ATTACK and
  SPECIAL; `PathFollower` owns LEFT / RIGHT / JUMP / DOWN. The aim comes from
  combat alone.
- ⚠️ **A bot must NOT draw from `WorldState.Random` — a rule, not a preference.**
  The match's stream is observable simulation (every seeded weapon puts its draw
  on the wire), so a bot drawing from it would make the stream's position depend
  on how many bots were thinking. `ScriptedBot.Random` is its own, seeded from
  the stagger, and **has to be part of any snapshot** (docs/BOTAI.md item 2).
  ⚠️ A first draft copied `world.State.Random` into a local and passed it by
  reference: the copy advanced, the world's did not, so **every tick drew the
  same numbers** — an aim error that never changed, a hold-fire roll that never
  varied. Silent, and it passes a determinism test.
- ⚠️ **The aim solver needs the floor under the TARGET, and a null nav graph
  means no floor.** `BotAim.FallPredict` applies gravity regardless of the
  target's vertical speed, so without the clamp a stationary target is predicted
  to keep falling and the bot aims **below its feet, into the ground**. A draft
  of `BotCombat` passed `NoFloor`, reasoning a grounded target is snapshotted
  with zero vertical speed and so cannot move — wrong, and the symptom was a
  soldier that acquired, fired, and never damaged anything.
- **Three behaviours live in `ScriptedBot` rather than in either half**, because
  only it sees both: the regroup hold, the engage hold, the back-off band. ⚠️ **All
  three are gated on being on the ground and not part-way through a validated
  arc** — stripping LEFT/RIGHT during a jump is not a pause, it is a different jump.
- ⚠️ **A held bot must have its stuck counter cleared.** The follower runs its
  stuck detector on the keys it *intends* to press, then the hold throws them
  away — so a bot standing still on purpose looks to the detector like a bot
  pressing a direction and going nowhere, and every `BOT_STUCK_TICKS` it
  blacklists one of its own spawn room's exits.
- ⚠️ **The engage hold means a bot with a distant goal STOPS walking to fight** —
  the feature, not a bug: without it a bot with a live enemy in front kept
  walking its route and firing sideways. Bounded four ways: a carrier never
  holds, a bot near its own goal keeps walking, the hold expires
  `BOT_ENGAGE_TICKS` after the target was *acquired*, and a close-in class walks
  at its target instead.

**Not ported, each conservative when absent:** `botRocketDodge` (never hops away
from an incoming rocket), `botStepSafe` (a pyro holds where the engage caught it
rather than closing), and `botObjectiveUpdate` (a goal must be given from
outside).

⚠️ **Two map facts a bot test needs are DIFFERENT points.** A clear sightline and
a standing position are not the same thing on ctf_truefort: (380, 268) has
1,000px of clear line and is in the **air** — `NodeFromWorld` answers -1, so a
follower cannot plan from it — while the floor below has a **blocked** horizontal
line at chest height, so two bots 60px apart cannot see each other. **Node 14 is
both**: 97 cells wide, world x 3,282-3,864, stand position (3570, 325), 900px of
clear sightline. Measured by sweeping every node; five test failures came from
using the other one.

⚠️ **The regroup hold's length is `random(BOT_REGROUP_TICKS)`** — uniform on
0-90 — so a test must **set** it rather than let it roll. This bot's seed happens
to draw about two ticks, and a test that let the roll stand asserts nothing most
of the time and fails occasionally.

### Phase 7 item 4e: the scenario suite runs, and eight of fourteen legs pass

`Gg2.Bots.{IScenarioSubject, ScenarioRunner, ScenarioWorld}` and
`ScenarioSuiteTests`. The reference is `gg2-agent/tools/botscenario.js`.

✅ **The fourteen scenarios are executable assertions now**, driven by
`ScriptedBot`. **Eight pass; six do not**, asserted as an exact set both ways — a
leg that starts failing is a regression, one that starts passing means the list
is a lie and its entry has to go.

⚠️ **A run is bit-for-bit reproducible**: `WorldState.Random` always starts at
zero, `ScriptedBot.Random` comes off the stagger. `gallery-pit-climb`'s `Known`
field is the reference's own account of what the alternative costs.

- ⚠️ **The runner hands over the goal, because it is what snapped it.** A subject
  never told where to go stands still, and the run reports "did not arrive" about
  a bot that was never asked — reads as a broken follower, is a broken harness.
  `IScenarioSubject` is the seam.
  ⚠️ **Not implementing it is legitimate and must NOT be an error.** Throwing was
  tried and broke the synthetic travel scenario that exercises the runner
  *without* a bot (a source holding one key needs no destination); the fact is
  reported instead (`ScenarioResult.Steered`), asserted by the suite about its
  own subjects.
- ⚠️ **`Satisfies` has to check arrival itself — it did not.** `Scenario.Budget`
  already treats a longer leg as a failure "even if it eventually arrives," so a
  travel scenario with no `expect` is still asserting something; reading arrival
  off the expectations let three legs that ended **737, 958 and 2,253px short**
  report themselves satisfied.
- ⚠️ **Acquisition is latched per tick, never read at the end.** A target clears
  when it dies, so a bot that fought and won looks the same on the last tick as
  one that never saw anything. `ScenarioExpectations.Acquired` had been carried
  unevaluated since the suite was ported, for want of this.
- ⚠️ **A scenario world places the map's GENERATORS** — the one objective it does
  place — so `soldier-shells-generator` measures nothing without it: no enemy
  characters, so the damage figure is entirely the generator's and `MinDamage`
  50 is unreachable however well the bot shoots (`Rounds.StepGenerator` only
  runs under `GameMode.Generator`, and the world is TeamDeathmatch). Measured:
  510 damage over 600 ticks against the reference's 206 and 165.
- **The subject is top tier with a fixed stagger of 1.** A scenario asks whether
  a behaviour is *possible*, and `BotSkill`'s lower tiers are deliberate
  handicaps — measuring against one measures the handicap. The stagger is what a
  server gives the first non-reserved slot, and what makes a run reproducible.

⚠️ **All fourteen now pass** — the six failures traced to `NAV_EDGE_BUCKET`, one
layer down (item 4f). The symptoms (a run-up round trip, a takeoff gate refusing
every approach, an unreachable `BOT_TAKEOFF_PATIENCE`) were all real and all
pointed at the wrong layer.

⚠️ **`ScenarioSuiteTests.Unmet` is empty and keeping it empty is the point** —
asserted as an exact set both ways, so a newly-failing leg is a regression and a
newly-passing "unmet" leg fails the build with "delete its entry".

### Phase 7 item 4f: one cast, and the fourteen scenarios all pass

`Gg2.Bots.{NavCache, NavGraph}` — `NavEdge.Bucket` is a `double` now. Gated by
`NavGraphFixtureTests.AJumpEdgesVelocityIsARealAndSurvivesTheLoad` and by the
whole scenario suite.

✅ **All fourteen behaviour scenarios pass**, and the tick counts land on the
reference's own numbers.

⚠️ **`NAV_EDGE_BUCKET` is a horizontal VELOCITY in px/tick, and it is
fractional** — not a bucket index, whatever the cache column is called:
`navJumpTakeoff` proves an arc as a constant velocity applied from tick 0 and
stores it — "leave the takeoff column, cover NAV_EDGE_BUCKET px of ground a tick,
be over the landing when the arc comes back down", `botPathKeys.gml:436`. This
port declared it `int` and truncated it on load, from item 4b-i until now.

Measured over the 24 cached graphs: **67,013 jump edges, 67,005 of them
fractional, and 23,184 — 34.6% — truncate to ZERO.** A zero velocity makes the
in-flight tracker steer for the takeoff column itself — the bot hangs still in
the air — and the takeoff gate then demands the whole gap from a reach of
nothing. `ctf_truefort` 178 → 177 carries `1.9518072289156623`, a 54px budget for
a 42px gap; read as `1` it is 27.7px and **the gate can never pass, on any
approach, for ever**.

| leg | truncated | corrected | reference |
|---|---|---|---|
| `valley-floor-to-point` | 643 | **251** | — |
| `valley-spawn-to-point` | 889 | **551** | — |
| `valley-point-to-spawn` | 538 | **456** | — |
| `valley-shaft-floor-to-point` | 478 | **161** | **161** |
| `truefort-spawn-to-intel` | never | **1,750** | 2,200-2,500 |
| `truefort-blue-spawn-exit-west` | never | **53** | 58 |
| `avanti-ramp-pocket-to-intel` | never | **311** | 352-438 |
| `gallery-pit-climb` | never | **216** | 271 |
| `oldfort-perch-reroutes` | never (479 off-route) | **1,190** (0 off-route) | — |

- ⚠️ **A behaviour layer that looks subtly wrong everywhere is a reason to check
  what it is reading, before tuning it.** The tell: the defect appeared on
  *marginal* arcs of every kind on every map — a lossy read's signature, not a
  control bug's.
- ⚠️ **`(int)` on a value read out of a `ds_grid` is a decision, not a
  formality.** Every grid cell is a double; `EdgeTicks` and `EdgeCost` were
  already read as reals and only `EdgeBucket` was cast, and the node grid has no
  real columns at all. Measured per column, so the remaining casts are
  known-safe rather than assumed.
- **The gate is two assertions plus two exact values.** No jump edge may read
  zero (the 34.6%), better than 99.9% must be fractional (which notices an
  index), and two named edges must carry their exact bits — `ctf_truefort`
  178 → 177 at `1.9518072289156623` and `koth_gallery` 86 → 58 at
  `0.6975139064871476`, the arc the reference's own takeoff-gate comment quotes
  as "0.70 px/tick over 17.2 ticks against a 12px gap: exactly 12 for exactly
  12" — corroboration this port reads the same field the reference does.
  ⚠️ **The scenario suite catches it too and that is not enough**: it is a map
  load and 6,000 ticks away from the wrong line and reads as a follower defect.
- ⚠️ **Eight of the 67,013 really do land on a whole number**, so "every one is
  fractional" is false; the proportion is what is asserted. The first draft
  claimed all of them and failed.

⚠️ **The long `ctf_truefort` leg in `PathFollowerFixtureTests` is pinned at
1,786 ticks, 39 re-plans, 0 stuck fires, 10 off-route, 0 blacklists** on a
137-node route (reference: 2,200-2,500 ticks on its own shorter one). This is
the current, load-bearing reading — see item 4b-ii.

### Phase 7 item 4g: the objective layer, and a bot that plays the game

`Gg2.Bots.{BotObjective, BotGoal, BotRoles, BotRole, BotGoalSpot, BotLos,
BotSpot}`, `ScriptedBot`'s objective cadence. Gated by `BotObjectiveTests`. The
reference is `botObjectiveUpdate.gml` (707 lines), `botRoleAssign`,
`botGoalSpot`, `botSetGoalNode`, `botEnemySpawn` and `botPointContested`.

✅ **A bot given nothing but a map crosses `ctf_truefort`, takes the enemy intel
and turns round** — 1,780 ticks, 41 re-plans, 0 stuck fires, 2 blacklists. It
reads the mode off the entities, picks the flag, resolves it to a node, plans,
flies two stacked jumps, grabs, and re-decides for the run home, and nothing is
told to it.

- ⚠️ **The mode is read off the ENTITIES, not off `WorldState.GameMode`**, even
  though this port stores it and the reference cannot: `ScenarioWorld` is
  TeamDeathmatch with nothing placed, and a CTF map whose briefcase is being
  carried has no `Intelligence` instance either. Reading the field instead would
  retask every scenario's bot mid-run.
- ⚠️ **The role split's class term is a PERIOD, not a veto.** "Engineers defend,
  scouts attack" breaks at both ends — a team of nine engineers has nobody
  attacking, and `botPopulationUpdate` rolls a class with `irandom(8)`, so it can
  produce one. Making the class choose *how often* keeps a split at every
  composition. ⚠️ The count is over the bot's **lean group** (engineer+heavy,
  scout+spy, everyone else), not the whole team: two engineers separated by four
  scouts sit at team positions 0 and 5, and neither lands on a multiple of 2.
- ⚠️ **`(place + 1) mod every`, so the first bot on a team always attacks** — a
  rule keyed on plain `place mod every` makes the lone bot on a team a defender
  with nothing to defend against.
- ⚠️ **A defender is 220px BESIDE what it guards, even at a spread of zero** —
  `if (spreadX >= 0) spreadX += BOT_DEFEND_RADIUS`. Tests asserting equality with
  the flag failed by exactly 220.
- ⚠️ **Our own flag being carried outranks fetching theirs, and every role
  chases.** Before that only defenders did, so a team that rolled none (the count
  runs down the roster and a short team can land none) left an enemy standing in
  the open with our flag and nobody interested.
- ⚠️ **A carried flag has no instance, so the whole cascade comes back empty.**
  Everyone except the carrier had no goal — a defender whose flag was stolen, an
  attacker whose team-mate beat it to the grab. Both now chase the moving carrier
  directly, returned early: a standoff band around a running player is a bot
  orbiting it, and a spread is a deliberate miss of the one thing worth
  intercepting.
- ⚠️ **A control point's goal is the MIDDLE of its capture zone.** The origin is
  the top-left corner, and arrival is only accurate to `BOT_ARRIVE_TOL`: measured
  in the reference on koth_valley, the bot climbed the whole four-jump chain,
  stopped 3px short of the zone's left edge, and captured nothing.
  ⚠️ **The zone is the only position this port has for a point** —
  `ControlPoints.Place` discards the marker's own coordinates once the zones are
  matched, where the reference ranks by the marker and then overrides with the
  zone. Named divergence; it can only reorder two nearly-equidistant points, and
  a capture reads the zone and nothing else.
- ⚠️ **A LOCKED point is still walked to.** Every point is locked for the first
  900 ticks of a KotH round, 1,800 of an Arena one; the nearest-unlocked loop
  finds nothing meanwhile and leaves the bot frozen at spawn while a human is
  already walking out.
- ⚠️ **An enemy in the zone cancels every "stand somewhere better" behaviour**
  for attacker and defender alike, read off **presence**, not the capture meter:
  presence is true the frame an enemy steps inside, the meter takes a second, and
  by the time it reads as a capture the point is a third gone.
- ⚠️ **The hold trigger is a DISTANCE, not "have I arrived".** Arrival is cleared
  by issuing a goal, so a trigger keyed to it flips off the moment it fires and
  the bot oscillates between the point and its overlook for ever at 30-tick
  intervals.
- ⚠️ **The spread never applies to a goal that has to be TOUCHED.** Measured on
  ctf_truefort: bots walked the whole map to the enemy flag, stopped 37-67px
  short of it (arrival tolerance is 8, so *precisely* stopped), and stood there —
  0-0 after a full round with both teams parked around a flag neither would
  touch.
- ⚠️ **The objective's own node is resolved first and the spread only earns its
  way in.** The snap searches *downward*, so snapping an already-offset position
  falls off the platform the intel is on — swept across the shipped graphs, a 96px
  spread at `ctf_orange` node 1 drops **636px**, most of the map. The guard
  ("within a body's height") means "same level" rather than "same surface", so a
  spread can still cross a step.
- ⚠️ **An unchanged objective is not reissued** — issuing forces an immediate
  re-plan and would fight the follower's own timer. Deleting the throttle takes
  the end-to-end leg from 41 re-plans to 58 and 10 off-route events to 19, and
  **the bot still arrives, slightly sooner** (1,780 → 1,693) — a test that only
  watches arrival passes with it gone.
- ⚠️ **`BOT_SPOT_TRIES` is invisible on any search that succeeds** — bounded and
  unbounded pay the same number of tests, because both stop at the first pass. The
  gate is a search that *cannot* succeed: 12 of ctf_truefort's nodes sit where
  nothing in a 150-4,000px band has a clear line, found by sweeping.

**Two things deliberately not ported, each conservative when absent:**
`botFindAlly` and with it the medic's follow-goal (a medic falls through to the
cascade and walks the objective like everyone else — exactly what the reference
does with a medic that finds nobody to follow), and `botPopulationUpdate`, which
fills a server with bots and is `GameServer`'s half of this.

⚠️ **One branch is UNGATED and says so in its own comment**: the spot ban
(`ScriptedBot.SpotBanUntil`) — deleting it leaves every test green. It needs a
spot that resolves and cannot be reached, which needs a nav graph with two
components, and every shipped graph is connected for the team that owns the
ground. Only a synthetic two-component graph would gate it.

⚠️ **The scenario suite does not exercise any of this and must not.** A scenario
places no objective and `ScenarioRunner` hands the bot its goal directly, so
`ScriptedBot.Objective` stays null there — the reference has the same escape
(`botGoalLocked`). ⚠️ **But `ScenarioWorld` places generators now** (item 4e), so a
bot given an objective in one *would* be retasked — why the objective layer is
opt-in rather than on by default.

### A bot that arrives stops moving, for the rest of the round

**2026-09-08.** Reported from play as "the engineer bots aren't doing anything —
standing still and holding down fire". It is not the engineer and it is not the
firing: the combat half goes on working perfectly while the bot cannot move.

**The mechanism.** Arrival frees the path and clears the goal, so an arrived bot
presses no movement key at all. `BotObjective.Decide` then re-decides on its own
cadence, hands back the same point, `TryResolve` snaps it to the node the bot is
standing on, and `BotGoalSpot.Find`'s first rank term is *walk less* — which the
node under the bot's feet scores zero on and therefore always wins. Nothing in
the loop moves it again.

⚠️ **It reads as a defender's bug and is not one.** Any bot whose goal is a
place to *stand* rather than a thing to *touch* parks the same way; defenders
show it because their objective never moves. The frozen bots were Demoman,
Engineer, Heavy, Medic, Pyro, Sniper and Soldier alike, and both roles.

⚠️ **The control-point branch was already immune, and that is why this
survived.** `held` re-tasks a defender onto ground overlooking a point it owns —
the comment there says "arrival is a STATE, not a terminus" in as many words. CTF
has no equivalent: a briefcase on its base *is* an already-held point, and nothing
was doing that job. So every KotH and CP map looked broadly healthy while every
CTF map had three to five statues in it.

#### Measured

All 22 shipped maps, two seeds each, twelve bots, 4,000 ticks, counting bots with
a 900-tick-or-longer motionless run **while the round was still running**:

| | before | after |
|---|---|---|
| bot-matches frozen 900+ ticks | **98 of 528** | **7 of 528** |
| worst run, `ctf_orange` | 3,945 ticks | 313 |
| worst run, `ctf_truefort` | 3,677 | 470 |
| worst run, `koth_valley` | 1,318 | 565 |

⛔ **Those totals were 116 and 50 until the harness was corrected.**
`HeadlessMatch.Step` returns immediately once `RoundDecided`, so the world stops
advancing and every bot reads as motionless for the rest of the tick budget — a
finished match scored as a roster of statues. All 18 of `gg_debug`'s were that:
its match decides at tick 1,355 and then sits still for 2,645 more. **A measure
of "nothing is happening" has to establish that something was supposed to be
happening**, and this one did not until it was asked to.

#### The residual, chased: three more mechanisms, and what is left

Getting from 31 to 7 took three further fixes, each found by tracing one bot
rather than by reasoning about the code. **Every intermediate hypothesis was
wrong** — an island node, a failure to resolve the bot's position to a node, a
re-post landing inside its own node — and each was cheap to refute only because
the trace was per-tick state rather than an end-of-match snapshot.

- ⚠️ **A goal that must be TOUCHED was excluded from the re-post, and that was
  too broad.** `ctf_avanti` is Invasion and carries an **1,800-tick setup
  timer**: eight of twelve bots walked to the enemy intel, arrived 3px off it,
  could not take it until setup expired, and stood still for the 1,600 ticks
  between — then moved the instant it hit zero. The exclusion existed so a bot
  would not wander off the enemy flag, but it can only ever bind a bot whose grab
  is already failing: one that can take the intel takes it on arrival and never
  parks.
- ⛔ **The spot ban turned the re-post into one attempt every 300 ticks, at the
  same unreachable node every time.** `BotGoalSpot.Find` is deterministic and
  returns its best candidate, and a re-post spot that never planned set
  `SpotBanUntil` — whose fallback is the plain objective, which for a parked bot
  is *the node it is already standing on*. Traced on `dkoth_atalia`: node 126 →
  re-post to node 60 → no route → ban → back to node 126, for 2,473 ticks.
  `ScriptedBot._repostSkip` now walks down the ranked list instead.
  ⚠️ **The ban is still needed once the whole list has been tried** — removing it
  outright cost four bots on `ctf_oldfort` that it had been rescuing, which is
  why it fires when the skip cycle wraps rather than never.
- ⚠️ **An engagement CLEARED the parked counter, so a flickering target granted a
  fresh eight seconds of standing still, indefinitely.** Bots sat motionless for
  900-1,200 ticks with `WantsToEngage` true for 4-18% of them, scattered finely
  enough that the counter never once reached its threshold. Standing to fight now
  *pauses* the count rather than resetting it; only moving resets it.

**What is left is seven, and the cause is settled below.**

#### ⛔ The last seven: an objective that can only be reached by jumping

`cp_egypt` 3, `koth_harvest` 2, `dkoth_atalia` 1, `tdm_mantic` 1. Every one is a
**Demoman, Engineer or Heavy** — never a Scout, Soldier or Pyro — and that is the
whole finding.

Measured **at the tick the freeze begins**, not at the end of the match, because
gates open and shut (they were open in all seven, so gates are not involved):

| map | bot's node | reaches | goal node | on foot | with double jump | with rocket jump |
|---|---|---|---|---|---|---|
| `cp_egypt` ×3 | 213 / 212 / 291 | 516 of 546 | 131 | **no** | **yes** | **yes** |
| `koth_harvest` | 396 | 259 of 460 | 95 | **no** | **yes** | **yes** |
| `dkoth_atalia` | 9 | 238 of 315 | 57 | no | no | no |
| `tdm_mantic` | 260 | **3 of 261** | 168 | no | no | no |

⚠️ **Six of the seven are standing somewhere richly connected** — 516 of 546
nodes on `cp_egypt` — so "the bot is stranded" is the wrong picture and was the
wrong first guess. What is unreachable is the **objective**, and for four of them
it is unreachable *only because the bot cannot jump*: the same node pair connects
for a Scout or a Soldier and not for them.

**So a grounded class can be handed an objective it can never arrive at**, and
nothing in the objective layer asks whether the goal is reachable for the profile
that has to walk it. The bot re-plans, fails, re-posts around the same
unreachable anchor, and stands there.

⚠️ **Anchoring the re-post retry on the BOT instead of the objective is the
obvious fix and is REFUTED** — it took the frozen count 7 to 8. It is left out
deliberately; do not reach for it again without a measurement.

Two of the seven are a different, smaller fault: `dkoth_atalia` node 9 → 57 and
`tdm_mantic` node 260 → 168 do not connect for *any* profile, and `tdm_mantic`'s
node 260 is a genuine one-way trap — it reaches **3 of 261** nodes.

**Not chased, and the reason is where the fix lives.** Either the graphs grow
ground routes to those objectives (`docs/NAVGEN.md`, and the generator is
`src/Gg2.Nav`), or the objective layer learns to ask "can this profile reach
that?" before committing — which `NavDistanceSet`'s per-profile tables could
answer in O(1), but only on the arm that loads them, and `--no-nav-distances` is
a different arm rather than a slower one.

#### The trap-node census, since it was measured on the way past

Nodes a grounded class can leave for less than a tenth of the graph, swept across
every shipped cache: **409 of 9,653**. The worst offenders are `ctf_conflict`
138, `ctf_oldfort` 57, `dkoth_sixties` 35, `ctf_orange` 33 and `ctf_truefort` 30.

⚠️ **A trap node is not by itself a frozen bot** — `ctf_truefort` and
`ctf_orange` carry thirty-odd each and freeze nobody, because bots rarely end up
standing on one. The census is a map of where the risk lives, not a defect list.

#### And measured on a live server, which is the measurement that matters

A Release `gg2-server --map ctf_orange --bots 10 --bot-classes Engineer`,
sampled over the control port at 1 Hz for 200 seconds, longest motionless run per
bot:

| | before | after |
|---|---|---|
| bots motionless 100s+ | **4 of 10** (197s, 168s, 165s, 134s) | **0** |
| worst run | 197s | **8s** |

And again on `ctf_avanti` with twelve bots over 90 seconds, isolating the three
residual fixes above from the first one:

| | re-post only | plus the residual fixes |
|---|---|---|
| bots motionless 45s+ | **6 of 12** (53s, 52s, 51s, 51s, 50s, 46s) | **0** |
| worst run | 53s | **18s** |

⚠️ **A default roster hides it.** The same instrument against eight bots of mixed
classes on `ctf_truefort` read a worst run of 30 seconds before the fix — bad, but
not obviously a bug from a graph. Forcing the class and the map is what turns the
report into a reproduction, and a headless sweep is what says it was never about
the engineer.

#### The fix, in three parts

- **`ScriptedBot.ParkedTicks`** counts consecutive ticks the bot spent grounded,
  not engaging, not regrouping, pressing no horizontal key **and not having
  moved**. At `BotConstants.RepostTicks` (240, eight seconds) it asks
  `BotObjective.Decide` for somewhere else.
- **`BotObjective.Decide`'s `reposting`** replaces the class's band with the hold
  band, drops the sightline requirement, and passes `BotConstants.RepostMinMove`
  (96px) as `BotGoalSpot.Find`'s new `avoidRadius`, which refuses candidates that
  near the bot *before* ranking them.
- **`ScriptedBot.ApplyHolds` clears `WantsToEngage`** when the target is gone or
  out of sight.
- **The re-post is allowed on a must-touch goal, walks the candidate list when a
  spot has no route, and is paused rather than cleared by an engagement** — the
  three below, found while chasing the residual.

⚠️ **Each of the three was individually insufficient, and the order they were
found in is worth keeping.**

- Clearing `ParkedTicks` where a goal is *issued* capped it at 30 and the re-post
  never fired once: a parked bot is re-issued the point it stands on every
  `BOT_OBJECTIVE_PERIOD`, and the `BOT_GOAL_RETARGET_DIST` throttle cannot
  suppress it, because arrival already cleared `HasGoal` and left it nothing to
  compare against. The counter has to clear on the bot *moving*.
- Keeping the class's own band and only excluding the bot's node **mostly
  missed** — 133 of 166 asks on one `ctf_truefort` match found nothing, because a
  standoff band is narrow, it demands a sightline, and `BOT_SPOT_TRIES` stops the
  search after twelve line tests. Replacing the band is what made asks and
  re-posts equal.
- The spread is not applied to a re-post. `BOT_SPREAD_MAX` is enough to put the
  goal back inside the radius the search just cleared.

#### ⚠️ `WantsToEngage` was never time-bounded, whatever its doc comment said

`ApplyHolds` writes it in exactly one place and returns early above that line
when the target is gone or not visible — so it kept the last live answer for as
long as the bot had nothing in sight, which is the rest of the round for a
defender in a quiet sector. Its own remarks claim `BOT_ENGAGE_TICKS` bounds it.

**Two things read it and both were getting a constant true**: `ParkedTicks`,
which could never start, and `HybridBot`'s `HandoverRule.MacroTask`, whose
`inRange && wantsToEngage` was reduced to `inRange` — the conjunct the rule
exists for. Only the no-target arm is cleared; the airborne return still holds
the answer from the ground the bot left, which is the right one to hand a
controller mid-arc.

#### ⚠️ `StuckTicks` is blind to this and always will be

It counts a bot **pressing a direction and going nowhere**. A statue presses
nothing, so `StuckTicks` reads 0 throughout — including for a bot holding a live
route it is making no progress along (`ctf_truefort`, a defending pyro sat on leg
7 of 8 with `HasPath` true, `StuckTicks` 0 and no key pressed for 1,629 ticks,
waiting on a descent that never came). **Any parked test keyed on the follower's
own state inherits the follower's blind spot**, which is why `ParkedTicks` is
keyed on the position instead.

#### ⛔ Adding the two constants moved `SIM_UUID`, and that was the real bug

`BotConstants` was on `SimulationStamp`'s **gating** surface, so adding
`RepostTicks` and `RepostMinMove` to it moved `SIM_UUID` and every shipped
checkpoint refused to load — **75 tests red** on the stamp mismatch, none of them
about bots standing still. They were parked on `ScriptedBot` to get past it.

**That was the wrong fix and the surface was corrected instead** — see "The
`BOT_*` block does not belong on the gating surface" below. The constants are
back in `BotConstants`, where a `BOT_*`-shaped number belongs.

#### The gate, and what it could not have caught

`MatchActuallyHappensTests.NoBotStandsStillForHalfAMinute` — three maps, two
seeds, twelve bots, 4,000 ticks, no bot motionless for 900. **Confirmed to fail
with the fix reverted** (1,318 / 3,945 / 3,677) rather than assumed to.

⚠️ **`EveryBotGoesSomewhere` was green throughout, and is not weak by
accident**: it measures the first 600 ticks and asks whether four of eight bots
moved 100px. A statue forms *after* it arrives, and four movers out of eight
passes with half the roster motionless. The lesson is the one this repo keeps
re-learning — a gate that asks "did anything happen" does not answer "did it keep
happening".

#### One test's premise moved, and it was the premise rather than the claim

`DuelEnvTests.HuntingAnUnreachableOpponentDoesNotSearchEveryDecision` asserted
`!Path.HasPath` at the end of the episode to show the opponent really was
unreachable. A parked bot now re-posts to reachable ground nearby, so it finishes
holding a perfectly good route to somewhere that is not the opponent. The claim
under test — 16 re-plans against a throttled ceiling of 21 — never moved. The
premise is now counted over the episode (67 of 200 decisions had no route), which
is also strictly stronger: an end-state snapshot can be a momentary gap.

### The `BOT_*` block does not belong on the gating surface

**2026-09-08.** `SimulationStamp` splits two digests: `Uuid` **gates** — a
mismatch refuses to load a checkpoint — and `RoutingUuid` is **reported**.
`NavConstants` moved to the reported side when the split was built.
`BotConstants` did not, and the code's own remark said why: the argument applied
to it equally, but "no measurement has been taken across a `BotConstants` edit
the way the nav edits were measured, and moving it on the strength of the
argument alone would be widening an authorisation past its evidence. Open."

**The measurement is cheap and it is decisive.**

| question | answer |
|---|---|
| does any of `src/Gg2.Simulation` read a `BOT_*` constant? | **no** — so none can change what an action does |
| does the inference path? (`ObservationWriter`, `ActionDecoder`, `PolicyNetwork`, `PolicyController`, `HybridBot`, `Handover`) | **no** |
| who does, then? | `src/Gg2.Bots`, `src/Gg2.Nav`, `HandoverRule` — 27 files |

That is verbatim the routing surface's own description: which states the policy
is asked about, and when it is handed the wheel. `BotConstants` is now on it.

⛔ **`BotConstantsReachTests` is what keeps that true, and it has to be a SOURCE
scan.** A `const` is inlined by the compiler, so a read of
`BotConstants.EngageTicks` leaves *nothing* in the IL — no member reference, and
not even an assembly reference if it is the only use. Reflection, Cecil and an
assembly-reference walk are all blind by construction. Confirmed to fire by
adding one read to `Gg2.Simulation/Deaths.cs` and watching it name the file.

#### ⚠️ Removing a source moves the digest exactly as adding a constant does

`Lines()` enumerates every literal of every source, so dropping `BotConstants`
deleted its 117 entries and took `SIM_UUID` 698913b5ea547d60 → f10afbdadc795d77.
**A re-stamp is therefore the prerequisite for the move, not an alternative to
it** — both directions cost the same one-time refusal, which is worth knowing
before treating "just leave it on the gate" as the cheaper option. It is not
cheaper; it is the same price, deferred, and paid again on every later
bot-tuning edit.

The two shipped checkpoints and the four fixtures under
`tests/Gg2.Tests/Fixtures/policy/` were **re-stamped rather than retrained**:
sixteen ASCII characters in `meta.npy`, verified member by member, every weight
byte-identical. `checkpoints/README.md` records the original stamp and the
original md5s, because the re-stamp is the operation that destroys the evidence
for its own safety.

⚠️ **Sound here for a reason that will usually be absent**: the retained surface
was provably unchanged at that moment. A stamp move caused by a *physics* change
retires those files, and re-stamping through one leaves nothing able to detect
it.

⚠️ **`gg2-rl` needed no change** — it receives the stamp over the env handshake
(`EnvProtocol.SimulationUuid`) rather than computing it, so the trainer records
whatever this build hands it.

#### ⚠️ A whole-file md5 is the wrong pin for an artefact with a provenance field

`ShippedCheckpointTests` pinned the md5 of the whole `.npz`, with the explicit
instruction that going red must **not** be answered by updating the constant:
"a checkpoint that changed has new numbers or no numbers; take the README with
it, or the pin becomes a rubber stamp."

**A re-stamp broke that pin while changing nothing the README measures**, so
following the instruction literally would have meant re-measuring numbers that
could not have moved — and not following it makes the pin a rubber stamp. The
pin was the problem, not the instruction.

It is now two pins: a digest over every member **except** `meta.npy` (what the
README's numbers were measured against, and provably unmoved by a re-stamp), and
a separate one over `meta.npy`. A provenance edit and a different set of weights
now produce different failures instead of the same one.

### Phase 7 item 4h: a server that fills itself, and the headroom number

`Gg2.Bots.{BotPopulation, BotPopulationOptions}`,
`Gg2.Server.BotPopulationService`, `ScriptedBot.SetMap`, `GameServer`'s roster
accessors and `--bots`. Gated by `BotPopulationTests`. The reference is
`botPopulationUpdate.gml`, `botPickTeam`, `botPickClass` and `botReteamAll`.

✅ **`gg2-server --bots 12 --botnav <path>` runs twelve bots that walk, fight and
play the objective.**

✅ **Phase 7's exit criterion is met: twelve bots cost 0.20ms a tick against a
33.3ms budget — 0.6% of it.** Measured over 600 steady-state ticks of a real
server on `ctf_truefort` — the population manager, twelve policies each planning
and fighting, the whole simulation, the state serialiser and the broadcast.
`BotPopulationTests.TwelveBotsRunWellUnderTheTickBudget` asserts a quarter of the
budget, because the claim is an order of magnitude rather than a digit and a CI
machine under load is not a benchmark.

⚠️ **`Gg2.Server` references `Gg2.Bots` now, and that edge is deliberate.** The
population manager is server-side in the reference — only the server has a
roster, a config file and a socket to broadcast a join on. `Gg2.Bots` stays pure
and gains nothing; docs/ARCHITECTURE.md's one hard rule (`Gg2.Simulation` must
not reference `Gg2.Server`) is untouched, and the `IInputSource` seam still means
the *simulation* never special-cases a bot.

- ⚠️ **`if(!global.botsEnabled) exit;` means DO NOTHING, not "the target is
  zero".** A bot added by hand on a server with the manager switched off must
  survive; reading it as a target removes it next tick. **Measured: it took out
  seven of `InputSourceTests`' eight tests**, every one of which adds a bot to a
  default server.
- ⚠️ **The target is a FILL, including humans, not a bot count.** A server
  filling to 8 with 3 people on it runs 5 bots and drops one every time somebody
  joins. `MinHumans` floors at **zero** rather than the fill, and is the *first*
  test rather than a clamp on the last: an empty server running eight bots
  against each other looks full in the lobby list and is not.
- ⚠️ **`getNumberOfOccupiedSlots` counts a joiner that has merely RESERVED a
  slot**, and neither bots nor the server's own slot 0. All three matter: counting
  bots makes the target chase its own tail, and *not* counting a reserving joiner
  displaces the bot it should replace a tick late.
  ⚠️ It also makes `HumanCount` the wrong detector for "the join completed" — it
  went up on the reservation. `PlayerCount` is wrong too: a bot displaced by the
  joining human puts it straight back. `ServerHarness` counts humans on the
  roster, which is neither.
- ⚠️ **A follower, a route and a blacklist are all node indices into ONE graph**,
  so a bot that crosses a map rotation carrying them holds numbers that now mean
  somewhere else, and its `BotObjective` holds the old graph and the old map's
  spawn points too. `ScriptedBot.SetMap` is the reference's `botNavRelease` and
  `botReteamAll` in one, called from `OpenMap`; **the bots are re-mapped before
  they are re-roled**.
- ⚠️ **The role assignment is a second pass over a FINISHED roster**, never inside
  the loop that assigns teams: a role counts this bot's place among its team's
  bots that share its class period, so assigning as you go would give every bot a
  count that only includes the ones before it. It re-runs after every add and
  removal too, because `RemoveSlot` compacts the roster and renumbers everyone
  above the hole.
- ⚠️ **The class the query carries is `checkClasslimits`' answer, not the one
  asked for.** The search's double-jump arcs are per class, and a scout routed as a
  heavy walks past every edge it could have flown.
- ⚠️ **The tie in `botPickTeam` has to be a coin.** A fixed side puts the first two
  bots on an empty server both on red and the balance never recovers — every later
  pick sees blue smaller and feeds it, so the teams alternate around a permanent
  offset of one.
- ⚠️ **`botPickClass` excludes the spy, and not because the spy's own code is
  broken.** Almost everything a spy does is invisible to the rest of the bot
  model — it spends most of its life cloaked, so target selection skips it and
  team-mates route around a fight it is standing in, and its whole value is a
  backstab (one branch behind a range test); a bot doing that badly is
  indistinguishable from a bot that is broken. Quote is out too: `irandom(8)` is
  nine classes and stops short of it.
- ⚠️ **A record struct's primary-constructor defaults do NOT apply to
  `new T()`.** That form resolves to the implicit *zeroing* constructor, so a
  "defaulted" `BotPopulationOptions` has a fill target of 0, a difficulty of 0
  and a **null** name prefix — every one silent. Measured by running the thing:
  `--bots 12` named its bots "1".."12" instead of "[BOT] 1".."[BOT] 12", unnoticed
  because every test that cared constructed its options explicitly.
  `BotPopulationOptions.Default` is the factory, and ⚠️ **its values are spelled
  out rather than delegating to `new()`** — writing `new()` there makes the
  factory a no-op that reads exactly like a fix. Same family as
  `CharacterState.Fresh` and `PlayerState.Fresh`: **in this codebase a zeroed
  value type is routinely not a valid one.**
- **Removal prefers a bot that is already dead.** The reference defers a removal
  on a wall clock (`current_time`) so a body does not vanish mid-fight; a
  fixed-tick server has no wall clock, and taking the dead first gets the same
  outcome — deliberate, and the one place this file is not a transcription.
- **`GameServer.CurrentMap` is exposed now.** ⚠️ Not `ServerOptions.Map` — they
  are the same string until the first rotation and different for ever after,
  the bug `MapRotationTests` exists to catch.

⚠️ **Two testing traps, both about waiting for the wrong event.** A rotation
announces itself **300 ticks before it happens** ("round over … koth_valley area
1 in ten seconds"), so a log match exits the wait while the old map is still
running and every assertion then reads state the test says has changed. And a
rotation test needs **nav state that actually exists** to invalidate — a bot
re-spawned on the new map has no route either way, so the test plants a real
route and a real blacklist entry first.

**Not ported:** the reference's deferred-removal timeout (above), and
`botReteamAll`'s *re-classing* — this port re-teams and re-roles across a
rotation but leaves each bot the class it had, a strictly smaller change and
what a human player would do.

### Phase 7 item 4i: the rocket jump, and a law with no friction under it

`Gg2.Bots.RocketModel`, `PathFollower`'s rocket branches, `ScriptedBot`'s
hand-off. Gated by `PathFollowerTests` and `ScriptedBotTests`. The reference is
`botRocketReach.gml`, `botPathKeys.gml:608-760, 846, 967-981` and
`botInputUpdate.gml:301-312`.

✅ **A soldier rocket-jumps.** `NavQuery.CanRocketJump` is answered from live
state every tick, the search offers the arcs, and the follower flies them.
**Five scenario legs got faster and none got slower**: `avanti-ramp-pocket-to-intel`
**311 → 175** (the reference quotes 352-438), `valley-floor-to-point` 251 → 219,
`valley-spawn-to-point` 551 → 523, `truefort-spawn-to-intel` 1,750 → 1,717. The
suite uses **6 rocket jumps** across its fourteen legs.

- ⚠️ **`RocketModel.Reach` and `JumpModel.Reach` must not be merged: they differ
  in BOTH terms of the motion law.** `moveStatus` 1 — "rocketing/mining myself" —
  uses controlFactor **0.65** and frictionFactor **1**, against the default's 0.85
  and 1.15; **friction 1 means horizontal speed never bleeds** for the whole
  flight (~69 ticks on a full-height jump). Wrong in both directions if merged:
  **a cold start on a fast arc** (needVx 6, v0 0, 30 ticks) reaches **136.9px**
  under the rocket law vs **119.5** under the ordinary one — the ordinary model
  *refuses arcs the bot flies easily*; **a hot start on a slow arc** (needVx 2, v0
  8) overshoots its own 60px schedule to **85.6px** where the ordinary law lands
  at **59** — the ordinary model *accepts arcs the bot sails past*.
- ⚠️ **The rocket law needs a SPEED GATE and the ordinary one must not grow one.**
  With no friction under it, pressing past the arc's vx gains nothing and is what
  made the real tracker limit-cycle; friction is what bounds the ordinary law.
- ⚠️ **The wait branch is BRAKING here where the reference writes
  `targetCol = takeoffCol`** — the same behaviour in two codebases whose default
  steering differs. The reference's default is a pure column comparison, so
  aiming at the bot's own column presses nothing. **This port's default is a
  DIRECTION HINT, not a place to stop**: it presses on along the route even on
  its target column, deliberately. Measured: aiming at the column pressed RIGHT
  for twelve consecutive ticks and the stuck detector banned a perfectly good edge
  at **tick 12**, forty-eight ticks early.
- ⚠️ **The mid-air bail is `return 0` from the whole script — no keys, not "no
  steering".** Falling back to default steering would press toward the takeoff
  column, i.e. the very arc the branch exists to stop steering. `Steering.Abandon`.
- ⚠️ **`AirTicks > 1`, not `>= 1`.** The blast lands in the same tick's simulation
  as the takeoff press, so tick one is always `moveStatus` 0; `>= 1` abandons
  every rocket jump on its first airborne tick.
- ⚠️ **The escape hatch takes the EDGE away rather than shoving the bot off it**,
  unlike every other arc — jumping without the rocket is the worst available
  outcome (the arc rises 276px where an ordinary jump rises 57). It is
  **unreachable at any stagger below 15**, since a re-plan zeroes the takeoff
  budget (item 4b-ii's ceiling) — almost always fine, as the launcher's refire is
  30 ticks and ammo comes back.
- ⚠️ **The aim OVERRIDES combat's rather than being ORed with it.** `vectorfactor`
  in the rocket's blast is sqrt(sin⁴ + cos⁴) — 1 only when axis-aligned, 0.707 at
  45° — so an angled rocket jump throws away up to 30% of its impulse. It
  **snaps** rather than slewing: turn rate is 8°/tick and level-to-down is 90°, so
  honouring the slew is eleven ticks staring at the floor before every jump.
- ⚠️ **`CanRocketJump` is recomputed from LIVE state every tick, not held** — the
  *weapon* plus `hp > NAV_RJ_DAMAGE` (strictly greater, since damage lands before
  the bot does) — so a soldier's routes shed rocket jumps on their own as it
  takes damage.
- **`PathFollower.Step` has a weaponless overload** meaning **"this bot has no
  rocket"**, not "do not care": a rocket edge is then never taken, the
  conservative answer every unarmed caller wants.

⚠️ **The cost side, and it is real.** The end-to-end intel run went **1,780 → 1,748
ticks** while **re-plans went 41 → 49 and off-route events 10 → 19** — the cost of
flying arcs that sometimes miss. The scenario suite is the better signal on the
trade, improving across the board.

⚠️ **Three testing traps this cost, all "the test measures the wrong thing now":**

1. **A fixture arc must cover its own gap** — the same trap the jump fixtures
   record. The old stub asserted *refusal*, so reusing it asserted an unflyable
   takeoff.
2. **The throttle test started measuring rocket churn.** A soldier's routes now
   contain blacklisted arcs re-planned around whenever the launcher is unloaded,
   inverting the still/moving comparison (7 re-plans vs 5). It uses a heavy now,
   which has neither special arc.
3. **The rotation test hit CTF OVERTIME.** A tie with a flag off its base at 0:00
   is unbounded; once bots reached the enemy flag more often, some runs never
   ended. It starts on a Team Deathmatch map now — **the only mode with no
   overtime** — with its goal given by hand, since TDM correctly declines to
   invent one.

⚠️ **Gating the two models is not gating that the GATE picks the right one.**
Pointing the rocket branch at `JumpModel` still passes every other follower test,
since their arcs are flyable under either law.
`TheTakeoffGateAsksTheRocketModelAboutARocketArc` is the fixture built to
separate them — an arc flyable under one law and not the other, observable as
launch vs. run-up.

### Phase 7 item 4j: the medic's ally, and one script with two callers

`Gg2.Bots.BotAlly`, the medic exception in `BotCombat.Step`, the medic case in
`BotClassKeys`, and the follow-goal in `BotObjective`. Gated by `BotAllyTests`.
The reference is `botFindAlly.gml`, `botCombatUpdate.gml:175-198` and
`botObjectiveUpdate.gml:83-133`.

✅ **A medic heals rather than shoots, and follows somebody.** Both halves came
from one 70-line script, which is why it was deferred rather than half-wired.

- ⚠️ **One script, two callers, passing DIFFERENT arguments.** The combat half
  asks with `skipFollowers` false — a medic beaming another medic is correct play
  — and the objective half asks with it true, because **two medics who pick each
  other as a destination deadlock**: each one's goal is where the other is
  standing, both are already there, neither leaves spawn. Seen live on
  koth_valley, two red medics parked at x=460 with the rest of their team at the
  point. **Making the exclusion a rule inside the search rather than an argument
  breaks healing to fix following.**
- ⚠️ **Filtering during the search, not rejecting the winner afterwards**, lets
  the second-best candidate win: a medic beside a wounded medic and a healthy
  heavy still follows the heavy, rather than giving up on following entirely.
- **The score is `1000 * hp/maxHp + distance - maxHp`.** Wounded outscores
  healthy by far more than distance can make up, and `- maxHp` is what makes a
  heavy at half health beat a scout at half health: keep the biggest body alive,
  not top up whoever is closest.
- ⚠️ **The sightline is `collision_line` against `Obstacle`, NOT
  `collision_line_bulletblocking`.** Gates, player walls and bullet walls are
  separate object sets, so a medic can beam a team-mate through their own gate —
  the beam's predicate is what decides. `CollisionWorld.CollisionLineHitsMask` is
  that call; `Healing` uses it for real.
- ⚠️ **The ally REPLACES the subject rather than adding to it**; everything
  downstream then aims at it. That is what makes a medic stop shooting and start
  healing — needle gun on RIGHT click, beam on left, two different keys.
- ⚠️ **An ally is NOT acquired, and all four difficulty gates are bypassed** — the
  acquire hold, fire delay, aim settle and hold-fire roll. All four exist to
  make shooting a dodging target look human; a medic that made its own team-mate
  wait 1.5s before healing would read as broken, not bad.
- ⚠️ **The beam is aimed STRAIGHT at them with no lead solve** — it has no travel
  time, so leading a team-mate points at empty air beside them.
- ⚠️ **The medic exception is decided ABOVE the no-target early return**: an ally
  is a subject in its own right, so a medic with a patient and no enemy anywhere
  still beams; deciding it after would make healing conditional on being shot at.
- ⚠️ **The follow-goal goes FIRST and returns early**, ahead of the whole mode
  cascade — following is the medic's job in every mode, and the objective is the
  team-mate's problem. Its goal is a moving *instance*, so it skips the spread and
  spot machinery, same shape as the flag-carrier chase. With nobody to follow it
  falls through to the objective like everyone else.
- ⚠️ **The offset keeps the ally BETWEEN the medic and the enemy, horizontal
  only**: the goal is a place to *stand*, a one-dimensional choice on a given
  surface. An intel carrier does not follow at all.

⚠️ **One named divergence: `skipFollowers` excludes the CLASS where the reference
excludes `isBot and BOT_CP_FOLLOW`.** `WorldState` holds no "is this a bot" flag
and must not grow one for a branch the tick never reads (docs/BOTAI.md). Cost:
skipping a **human** medic as a follow target too, which the reference does not
(a person walks on their own, so no cycle forms). Bounded and conservative: a bot
medic whose only candidate is a human medic finds nobody and falls through to the
objective — exactly what the reference does when a medic finds nobody to follow.

⚠️ **Three testing traps, all "the assertion is about the wrong thing":**

1. **The follow-goal fires with or without an enemy** (the offset comes off), so
   testing a medic with no target proves nothing about the mode cascade — show
   that premise with a **non-medic** instead.
2. **A medic's attention radius is 300px**, shortest of any class bar the
   engineer's 400; a fixture with the enemy at 400 silently exercised nothing.
3. **The follow position is 60px from the ally, so a medic standing beside one
   ARRIVES on the tick the goal is issued** — arrival clears `HasGoal` and frees
   the path together. `GoalX` is what survives and what the claim is about.

⚠️ **Gating the rule is not gating the wiring.** `BotObjective.Decide` takes the
target as a parameter, so a test calling it directly passes even with
`ScriptedBot` no longer passing one — checked by mutation, closed by driving a
real medic bot and reading which side of its ally the goal landed on.

### Phase 7 item 4k: the airblast and the dodge, and two radius tests that were wrong

`Gg2.Bots.BotReflex`, the pyro branch in `BotClassKeys`, the no-target branch in
`BotCombat` and the evade press in `ScriptedBot`. Gated by `BotReflexTests`. The
reference is `botInBlastBeam.gml`, `botIncomingProjectile.gml`,
`botRocketDodge.gml` and `botInputUpdate.gml:192-194`.

✅ **A pyro reflects rockets and a bot jumps out of their way.** Three scripts in
one file because the first is only ever called by the second, and because **both
outer two answer the same shape of question: model the thing's actual future
rather than measuring a radius** — which is what each was originally got wrong by
doing.

- ⚠️ **The airblast's trigger volume is a BEAM, not a cone or a circle.** What
  gates a reflect is `collision_circle(projectile.x, projectile.y, 5, poof, false,
  true)` against an `AirBlastO` spawned 25px along the aim, whose mask is a narrow
  rectangle. The reference used to ask "within 150px and 75° of the aim", copied
  from the Flamethrower's own `blastDistance`/`blastAngle`, **neither of which
  gates a reflect any more** — so the bot pressed SPECIAL for rockets it could not
  touch, over a volume about **ten times wider than the weapon**. Reported from
  play as a pyro unable to deflect rockets even from a distance.
- ⚠️ **The press is not free**, so the volume must be exact: it sets
  `readyToBlast` false for 40 ticks regardless, and a rocket at 150px is ~12 ticks
  from impact — the wasted blast is still on cooldown when the real one arrives.
- ⚠️ **The three constants are measured off the running exe, not the sprite file,
  which disagrees with it.** `AirBlastMaskS` is a 120×30 image whose mask bounds
  are AUTO, taken from opaque pixels: left 8, right 96, top −13, bottom 14. Plus
  the 25px spawn offset and `collision_circle`'s 5px radius: **near 28, reach
  126, half-width 18**.
- ⚠️ **There IS a near edge and it is not a rounding detail**: a rocket already at
  the pyro's feet is UNDER the beam and cannot be blasted.
- ⚠️ **`minTravel` is a DISTANCE, not an age.** The airblast has no delay, so
  without it a point-blank rocket came back on the frame it was born — confirmed
  inhuman. A distance is frame-rate independent. It is 60, not the 120 first
  tried, *because of the beam*: the window is 28-126px, so at 120 a shooter at
  130px produced a rocket never eligible at any point of flight.
- ⚠️ **The dodge's verdict is "the standing bot is hit at some tick AND the
  jumping bot is hit at none".** The old "enemy projectile within 150px" test was
  wrong in **both** directions: it jumped at rockets that would never connect,
  and jumped *into* ones fired over the bot's head, where rising 57px is exactly
  what CREATES the collision. **No proximity test can tell those apart** — "would
  it hit" and "does jumping help" invert depending on where the shot is aimed.
- ⚠️ **Splash is deliberately not modelled.** A rocket detonating on the floor
  under a jumping bot still catches it inside `blastRadius` 65, so counting
  splash would refuse nearly every dodge; the jump reliably avoids only the
  direct hit.
- ⚠️ **The dodge is grounded-only and never throttled** — grounded keeps it out of
  every planned jump arc for free, and ORing is safe because JUMP is
  edge-triggered in Begin Step. **It is the only thing that presses JUMP outside
  the path follower**: the reference's voluntary combat hops were removed because
  together they fired often enough — right where the follower has arrived and
  returns no keys — that bots in a firefight read as stuck jumping in place.

⚠️ **A gap this found: the class policy must run with NO TARGET, and in this port
it did not.** `botCombatUpdate`'s no-subject branch still calls `botClassKeys` —
a class can have an opinion with nothing to aim at (a spy re-cloaks, an engineer
builds, a heavy eats) — only the aiming is skipped. This port returned no keys at
all, so **a pyro with an empty screen could not deflect a rocket**. Fixed by
running the policy there, so **every branch that would fire at empty air now
carries its own subject test**, matching the reference. ⚠️ The pyro's flame needs
one because `distance` is 0 with no target, inside the flame's reach — without it
an idle pyro holds the trigger forever.

⚠️ **`readyToBlast` and `ammoCount >= 40` are checked FIRST and are not
decoration** — the cheapest of the three tests, gating the beam scan regardless
of geometry.

⚠️ **One reference comment describes a case its own budget cannot reach, measured
not assumed.** `botRocketDodge` subtracts the rise rather than clamping it,
"negative once the arc has come back down". With `NAV_JUMP_V0` 8.3 and
`NAV_JUMP_GRAVITY` 0.6 the rise crosses zero at **t = 27.67**, one tick past
`BOT_DODGE_TICKS` (**26**). Over the walk the rise runs 8.0 to 13.0 and never
dips below 8, so **a clamp is a no-op**, checked by mutation; the subtraction is
kept because widening the budget would make it matter.

⚠️ **A testing trap: the two obvious dodge fixtures do not gate the second half of
the verdict.** A rocket at the chest and one over the head leave `!hitJumping`
ungated — the over-the-head one misses the *standing* bot too, failing the FIRST
clause. What's needed is a rocket that hits **either way**: one tick out at +10,
where the standing box spans −21..+31 and one tick of jump (8px) gives −29..+23.

### Phase 7 item 4l: suppression, and a live flag that is not the target's

`Gg2.Bots.BotSuppress`, the suppression block in `BotCombat.SolveAim`,
`BotCombatState.{SuppressNode, SuppressAt}`. Gated by `BotSuppressTests`. The
reference is `botSuppressSpot.gml` and `botCombatUpdate.gml:294-328`.

✅ **A heavy that loses sight of its target now denies the doorway it must come
back through**, rather than shooting the wall it lost them behind.

- ⚠️ **Worth building because the case is common, measured before any of it was
  written.** Twelve bots on koth_valley over 3,172 frames: **13,733 bot-ticks
  with a live target, 2,744 of them — 20% — with no line of sight**; for a heavy
  1,432 of 6,442. About one tick in five of every engagement, not already eaten
  by the 30-tick memory drop.
- ⚠️ **A BOUNDED BREADTH-FIRST WALK, NOT AN A*, and that is a measurement, not a
  preference.** An A*-shaped search toward the heavy's own node timed at **8.13
  ms a call on koth_valley's 270 nodes** — nothing staggers the bots, so six
  heavies recomputing on one frame is **48.8 ms against a 33.3 ms budget**, on
  the *smallest* interesting map (ctf_truefort has 687 nodes). Breadth-first
  order is also the right order: fewest hops from where they were is soonest.
- ⚠️ **The walk uses the ENEMY's gates, not ours** — it's their route, and a red
  bot must not assume a blue enemy exits through a red spawn gate. Observable on
  ctf_truefort: from node 8 a red traveller is walled in and blue is not; from
  node 74, the reverse.
- ⚠️ **Never the node they were last seen on** — behind cover by construction, and
  shooting it is exactly the behaviour this replaces; also the guard against
  aiming at a wall when the enemy simply stood still.
- ⚠️ **The velocity is zeroed when a spot is taken.** The snapshotted velocity
  belonged to a body that was moving when last seen; **a doorway is not moving**,
  and leading a fixed point by a dead man's velocity sends the shot into the far
  wall.
- ⚠️ **On its own clock, not the aim interval** — the aim interval is 2 ticks at
  the top tier, which would mean a graph walk per bot per two frames. The answer
  is re-used between refreshes, honest about being a place, not a moving target.
- **Opt-in per class** (`BOT_CP_SUPPRESS`, the heavy alone), since the walk is a
  search, and **degrades to the stale snapshot with no graph** — a server with no
  nav cache still runs bots.

⚠️ **`state.Visible` is the LIVE flag and `state.Target.Visible` is not.** The
first tracks the perceive cadence, false exactly while the sightline is broken;
the second is only what the search reported at acquisition, fixed thereafter.
The first draft read the target's, so the branch fired only on the acquisition
tick. The reference is `!player.botVisible`.

⚠️ **A target acquired while BLIND never gets a snapshot, so the aim sits at
(0, 0).** The perceive block sits below the acquire hold's early return, in this
port *and in the reference* (`if(!canAim) return 0;` in `botCombatUpdate`), so a
never-seen target leaves `botSeeX/botSeeY` wherever they held — the map origin on
a fresh bot. **Faithful, not a defect**, but it shapes what a test fixture needs.

⚠️ **A suppression fixture must be SEEN AND THEN LOST, never acquired blind** —
three tests failed measuring the origin before this was understood. Two more
things a fixture here needs:

- ⚠️ **Inside `BOT_BLIND_RANGE` (260)** — a blind target further out is never
  held, so the feature is unreachable. A first fixture at 464px apart failed for
  that reason alone.
- ⚠️ **Moving when it is last seen** — a stationary enemy snapshots at zero
  velocity, indistinguishable from a port that forgot to clear it; checked by
  mutation, the zeroing survived only once the fixture gave the enemy speed.

⚠️ **A permanently broken sightline drops the target every 30 ticks and
re-acquires on the next target period**, so a test stopping on a fixed tick lands
in the gap about as often as not, reading as "never acquired anything". Stop on
the condition, and guard any window assertion with "still held and still blind"
— a re-acquisition legitimately recomputes, and asserting through one measures
the memory window instead.

**The hop budget is a COST bound and is deliberately not gated.** What is gated
is that the answer is reachable from where the enemy was, which a broken edge
index or a mis-read gate would break.

### Phase 7 item 4m: the arc, and a fixture that has to separate two questions

`Gg2.Bots.BotArc`, the arc gate in `BotClassKeys`. Gated by `BotArcTests`. The
reference is `botArcClear.gml` and `botClassKeys.gml:47-68`.

✅ **A demoman no longer fires into the ceiling.**

- ⚠️ **Nothing else in the bot layer ever checks a projectile's arc.** Target
  selection, the keep-target test and the spot search are all a straight
  chest-to-chest sightline, while the aim solver deliberately aims *above* the
  target to compensate for drop — a large amount: **~178px of sag at 500px for a
  Minegun round**, ~120px for a scattergun, none for a rocket. A demoman with a
  clear straight line across a room is aiming a whole storey above it — the
  mechanism behind the reported "sometimes shooting at the ceiling".
- ⚠️ **Only the two classes whose shot kills them if it goes off next to them.**
  Everyone else pays nothing: a scattergun round clipping a lintel costs ammo the
  class has plenty of, and withholding would stop a scout firing in exactly the
  corridors it wants to fire in.
- ⚠️ **The last sample is the TARGET, not the parabola's own endpoint.** Flight
  time is recovered from the horizontal offset with a tick or so of error; letting
  that put the final sample a few pixels *into* the floor a grounded target stands
  on would refuse the commonest shot in the game. Ending on the aim point also
  matches what the straight sight test does, so a flat weapon keeps its old answer.
- ⚠️ **`t·(t+1)/2` is exact rather than a continuous approximation** — gravity is
  added to `vspeed` *before* the move applies, so accumulated sag after t ticks is
  the triangular number, the same law `BotAim.Solve` uses (gated there, not twice).
- **Withholding rather than re-solving is deliberate** — a flatter solution
  generally means closing distance, a positioning decision the objective layer
  owns.

⚠️ **A fixture here must separate `Clear(aim, g)` from `Clear(aim, 0)`, not just
from the chest-to-chest line — only a handful of real-map pairs do.** The first
fixture was blocked by **aim direction**, not gravity: the shot pointed into
geometry, which a straight-line sampler answers identically. **Three mutations
survived against it**, including "sample the arc as a straight line" — the one
the whole feature is about. Sweeping ctf_truefort for pairs where gravity alone
decides gave five; node 27 → node 136 is the one used.

⚠️ **The sweep that chose that fixture was itself wrong, for a reason CLAUDE.md
already records.** It solved the aim with `BotAim.NoFloor`, so the solver
predicted a stationary target falling forever and aimed **below its feet, into
the ground** — every arc it sampled was blocked, reading as the feature refusing
everything. With a real floor the same sweep gives 8 blocked against 50 clear.
**Any test that solves an aim must pass `BotAim.FloorUnder`.**

⚠️ **One surviving mutation is recorded in the code rather than papered over: the
upper flight-time clamp** (`BOT_AIM_MAX_TICKS`), a guard against an unreachable
aim point walking a thousand ticks of parabola — a *cost* bound, like
`BotSuppress`'s hop budget — and every real-map fixture reaching it is blocked
with or without it. The *lower* bound is reachable and is gated.

⚠️ **The soldier's own use of the gate survived too, and is closed now.** It stood
ungated because a rocket flies flat, nearly the straight line the target search
already cleared — a **moving** target is what separates them. Swept across
ctf_truefort: twelve node pairs satisfy chest-to-chest clear, still-aim arc
clear, and **led** arc blocked at once. Node 0 → 214 at 417px is the widest: the
still aim is 319.7°, a target running at 6px/tick pulls it to 300.8° — **nineteen
degrees into a wall**. The angular separation is the fixture, so pick the widest.

⚠️ **A caller test here must ask `BotClassKeys.For`, not the whole combat step.**
The trigger sits behind three other `BotCombat` gates — fire delay, aim settle,
hold-fire roll — so driving the pipeline measures those too and the hold-fire
roll makes it intermittent. ⚠️ The aim must also be **set on the character**: the
arc samples its live `aimDirection`, and a defaulted aim (pointing right) makes
the arc unrelated to the target.

### Phase 7 item 4n: the mined doorway, and a radius that is not the one on the tin

`Gg2.Bots.BotMines`, the detonation in `BotClassKeys`. Gated by `BotMinesTests`.
The reference is `botMinesArmed.gml` and `botClassKeys.gml:89-93`.

✅ **A demoman springs a mined doorway on whoever walks into it.**

- ⚠️ **The question is never "is this mine worth it" but "is any of them"** — the
  Minegun detonates **every** mine the player owns at once, so one armed mine is
  the whole trigger, exactly what a chokepoint full of them is for.
- ⚠️ **`BOT_MINE_TRIGGER` is measured against the mine's *damage* radius
  (`blastRadius` **40**), not its affect radius (`affectRadius` **65**,
  `Mine.events/User Event 2.xml`) — that difference is the whole behaviour.** The
  reference's first version triggered at 60: the bot blew its whole stock the
  moment an enemy came near and dealt **nothing**, reported from play as
  "detonates too early, doesn't damage enemies" — exactly right.
- ⚠️ **A sentry/generator need a different metric than a character, and it's the
  only reading that works.** The reference scanned `with(Character)` only, so a
  demoman targeting a **sentry** (`BotTargeting`/`BotClassKeys` lob at sentry and
  generator targets) put its whole stock on it and never pressed SPECIAL — no
  character ever stood next to it. Reported as "shooting grenades at sentries,
  but not detonating".
- ⚠️ **A character is measured centre to centre, a sentry/generator box to box —
  deliberately, same 30 constant either way**: a character is ~6px of box either
  side of centre, a sentry's centre can be 30px from a mine at its base, and
  `distance_to_object` is what the engine's own damage block uses on both.
- ⚠️ **The detonation carries no `hasSubject` test**, alone among the firing
  branches in `BotClassKeys` — a mined doorway is worth springing on whoever
  walks into it, often not the thing the demoman is aiming at. **A cloaked spy
  is the one exception**: target selection skips a cloaked candidate everywhere
  else, and detonating on one would be the sole spot a bot acted on information
  it does not have.

⚠️ **The reference's comment is only half true: a STUCK mine has no
quarter-radius cutoff.** "Anything at 30 of box distance takes nothing" holds for
a mine in flight but not one lying in a doorway — the guard is
`and other.splashThreshhold`, cleared on landing (CLAUDE.md, stage 6). Measured
on `ctf_truefort`, one mine against a scout, damage out of a full blast of 45:

| centre gap | airborne | stuck |
|---|---|---|
| 0 | 45.0 | 45.0 |
| 30 (the trigger) | 20.3 | 20.3 |
| 45 | **0.0** | 3.4 |
| 60 (the old trigger) | **0.0** | **0.0** |

The trigger is conservative in both forms — at 30 it still buys nearly half a
blast — and at the reference's original 60, **both forms deal exactly zero**.
`BotMinesTests` asserts the damage rather than the constant, so a mutation that
restores 60 fails on what the number is *for*.

⚠️ **`ScenarioWorld.Place` snaps downward to a nav node, so a coordinate handed to
it is a request, not a position.** Fixture distances are measured from where the
character actually landed, and the **mine** is placed relative to that. The first
draft measured from the requested coordinates and asserted a 45px gap that was
really ~27, misread as the trigger being wrong.

**Eight mutations tried and all eight bite**: the trigger at 60 (7 failures), the
sentry scan deleted, the generator scan deleted, the sentry measured centre to
centre, the cloak clause dropped, the owner test dropped, a corpse arming it, and
the detonation made to wait for a target.

### Phase 7 item 4o: the close-in step, and the one press no route validated

`Gg2.Bots.BotStep`, the close-in branch in `ScriptedBot.ApplyHolds`. Gated by
`BotStepTests`. The reference is `botStepSafe.gml` and
`botInputUpdate.gml:225-245`.

✅ **A pyro walks at its target instead of holding where the engage caught it.**

- ⚠️ **The only place in the bot layer that presses a movement key nothing
  validated.** Everything else moving a bot sideways is either a nav-proven edge
  or a single blast-radius back-off step; a pyro closing on a target is neither,
  so it walks ground nothing has checked — on `ctf_truefort` a pit beside the
  route traps a chasing pyro for good.
- ⚠️ **Only the pyro, because only its weapon reaches a fraction of what it can
  see.** `ClassRange` 200 vs `BOT_FLAME_REACH` 165 leaves a **35px band** where
  the close-in step fires — holding at acquisition range would be holding out of
  range for ever. Every other class has `BOT_CP_CLOSE_IN` 0 and stands where the
  hold caught it.
- ⚠️ **`place_meeting` against `Obstacle`, never `place_free`.** The reference's
  own comment explains why: `place_free` reads GM8's `solid` flag, which the game
  toggles mid-frame (`gunSetSolids` brackets every hitscan trace), so the answer
  depends on when in the frame it's asked. This port has no such flag, but the
  *object list* still must be right: `CollisionFilter.ForSentry()`
  (`wallSetSolid` — Obstacle only) is that list. **A team gate is neither a wall
  to refuse nor a floor to stand on**, so using the character's own list lets a
  pyro walk through its own team's gate. ⚠️ **Two refusals, not one**: a wall
  ahead isn't unsafe (there's floor under it) but the press achieves nothing, so
  it's refused separately from the floor test (the follower's stuck detector
  counts it either way).
- ⚠️ **The drop tolerance answers "does the ground continue," not "can the bot
  get there" — that is `navFindPath`'s question, against the graph.** So a step
  down to a lower ledge reads safe and a step over a pit does not; without it a
  pyro on a raised slab can never close on anything.

⚠️ **Every fixture here was swept for, not chosen, and none is a nav node's own
stand position.** Ground that continues, ground that stops, and a solid that
isn't ground at all look identical on a map screenshot. Swept across every floor
row on `ctf_truefort`: **4,528 pit-edged spots, 4,157 wall-edged, 1,110 where the
solidity-list choice changes the answer** — the only family that gates the
filter, and no fixture picked by eye would have hit one. `(682, 325)` steps onto
a **TeamGate** with sound floor under it; `(1064, 325)` is the ledge — the pit
beside it bottoms out **216px** down (asserted), a real gap rather than a
near-miss of the 28px tolerance. ⚠️ **The sweep must not test `place_free` one
pixel down**: GG2 rests a character with its feet a pixel *above* the surface
(same fact `navNodeFromWorld`'s rounding turns on), so "free at y, solid at y+1"
is true **nowhere on the map** — the first sweep found zero candidates and read
as the primitive being broken. The right filter: "free at y and something
within the drop tolerance".

**Seven mutations tried and all seven bite**: the wall test deleted, the floor
test deleted, the floor tested at `y` instead of `y + BOT_ENGAGE_DROP`, the
filter widened to everything a character collides with, the caller skipping the
safety test, every class given a close-in distance, and the step direction
flipped.

### Phase 7 item 4p: the spot ban, and a two-component graph that had to be built

`Gg2.Bots.ScriptedBot.SpotBanUntil` and the notice in `UpdateObjective`. Gated by
`SpotBanTests`. The reference is `botObjectiveUpdate.gml`'s spot handling.

✅ **The last ungated branch in the bot layer is gated**, and no behaviour changed
— this item is entirely a test.

- ⚠️ **A spot is chosen without checking reachability.** `BotGoalSpot` ranks
  nodes by distance, height and sightline and never asks the graph whether a
  route exists — deliberately, since a path search per candidate is a search per
  bot per objective tick. A spot in another component presents as a goal that
  never plans, and the ban is the only place that notices.
- ⚠️ **The state is unreachable from any shipped map** — why the branch stood
  ungated for four items, per its own comment: **all 24 cached graphs are
  connected for the team that owns the ground.** A green suite was not evidence
  it worked.
- ⚠️ **The fixture is a real graph with one node's edges stripped**, rebuilt
  through the real cache format and reader — isolating one node is the smallest
  two-component graph there is, and it keeps the spot search's candidate set
  *identical*, so the route is the only thing that changed. `NavCacheTests.WriteCache`
  is `internal` and already the byte builder; going back through `NavCache.Parse`
  matters because **both derived indices are bets on the generator's output**
  (edges sorted by from-node, nodes by row then x), which a graph assembled
  behind the parser could satisfy neither of while looking right.
- ⚠️ **The ban legitimately advances every tick between the goal being issued and
  the next objective tick.** It fires on the goal's own `IsSpot`, rewritten only
  when a goal is actually *issued* — so for those 30 ticks the goal is still a
  spot and still unplannable. What must not advance is the window after the
  replacement lands; measuring too early reads a correct implementation as a
  re-arming one. Measured: first ban at 301, settled at 330.
- ⚠️ **The ban is a delay, not a stop.** It lapses after 300 ticks and the bot
  asks for another spot; on this fixture the second perch is also unreachable,
  so the second ban is evidence a second spot was asked for (measured: 301 → 990
  over three windows).
- ⚠️ **`HasGoal` separates "planning failed" from "arrived"** — arrival clears
  the goal and frees the path together. A ban keyed on `!HasPath` alone punishes
  success, refusing every later spot for ten seconds after every arrival — most
  of a bot's life.

⚠️ **Four traps writing this, three about the observation window rather than the
code:** stop inside the first ban window — running past it measures a *second*
unreachable perch instead, as the first draft did (ran to 400 against a ban that
ends at 301); assert the exact tick the ban lands on (`1 + BOT_SPOT_BAN`), since
a check below the objective-period gate passes one period late without erroring;
compare against the objective's own `allowSpot: false` answer, not its
coordinates — the banned goal is the point *snapped downward to a node*, 207px
away on `koth_valley`, so a test expecting the raw point fails for an unrelated
reason. ⚠️ And a mutation leaving a private field assigned-but-never-read
**doesn't compile here** (warnings as errors), so "delete the `_goalIsSpot`
clause" isn't a runnable mutation — mutate the *data* instead (`_goalIsSpot =
true`), the same claim, which compiles. Same family as the `if (false)`/CS0162
trap.

**Five mutations tried and all five bite**: the ban deleted, the ban never
consulted (`allowSpot: true`), every goal counted as a spot, the `HasGoal` clause
dropped, and the check moved below the period gate.

### Phase 7 item 4q: edge occupancy, and why a team stops walking in single file

`Gg2.Bots.BotOccupancy`, `BotPathState.OccupiedTeam`, the two calls in
`PathFollower.Plan`, the arrival and death removals, and
`BotPopulationService.Occupancy`. Gated by `BotOccupancyTests`. The reference is
`botOccupancyAdd.gml`, `botOccupancyMap.gml` and `botOccupancyReset.gml`.

✅ **`NavSearch.Find`'s occupancy argument is passed now.** It had been carried
since item 4a and nothing filled it, so **a whole team planned the identical
route**. Measured on `ctf_truefort` node 0 → 143: four bots asking in turn get
**four distinct routes** with occupancy and **one** without.

- ⚠️ **Occupancy is the only safe way to spread a team out; a per-edge random
  jitter is not.** Tried and deleted in the reference: it is mean-reverting, so
  over a sixty-edge route every candidate inflates by the same average and all
  eight bots got the identical route anyway. Occupancy works because it is
  structured, shared, and coarse — expensive exactly where crowded. **Do not
  re-add the jitter.**
- ⚠️ **A bot's own route is excluded from the counts before it searches — not an
  optimisation, but what keeps a route stable across re-plans.** A bot that can
  see its own route is charged extra for the path it is already walking, so every
  re-plan tries to move it and the team churns lanes every `BOT_REPLAN_TICKS`.
  Reference measurement on this map: self excluded, 2 of 16 re-plans changed a
  route; node-keyed penalty (cannot exclude self cleanly — a bot stands on its
  own nodes) — **15 of 16**.
- ⚠️ **Per team — the thing being spread out is a team.** A blue bot has no
  reason to avoid red's route; one shared map would have each team's routes push
  the other's around for no visible reason.
- ⚠️ **A removal charges the team the route was counted against, never the bot's
  current team.** The two diverge: autobalance can move a bot to the larger team
  mid-round, and a rotation re-teams every one. Reading the current team on the
  way out subtracts from the map the bot just joined and leaves the old team's
  counts raised **for ever** — an edge nothing walks that every future search on
  that team overpays for.
- ⚠️ **A route is added in one place and removed in three** (planner, arrival,
  death/leave/map-change), so the add must be idempotent. ⚠️ **The
  negative-count danger isn't what it looks like**: `if (was <= 0) remove` means
  a count is never *stored* negative, so a lone bot's double-decrement is
  invisible — what it costs is **somebody else's** count, which a fixture has to
  build for.
- ⚠️ **Keys are node indices, which only mean anything to one build of the
  graph**, so both maps are dropped on a rotation — before the bots are handed
  the new graph, since `ScriptedBot.SetMap` clears each bot's own bookkeeping.

⚠️ **One named divergence: this port keeps ONE field where the reference keeps
two.** `botOccupied`/`botOccupiedTeam` carry the same information and can
disagree; here `OccupiedTeam` **is** the flag (`Team.Spectator` = "counted
nowhere") and `Occupied` is derived. The reference's `botOccupancyReset` clears
both halves in step; here one call does both. **Found by mutation** — with two
fields, deleting the flag guard in `Remove` left every test green, since the team
sentinel already carried it. Two guards where either suffices is a guard that
cannot be gated.

⚠️ **A fixture for occupancy has to be swept for** — most legs have one sensible
route and a 25% penalty on a few edges doesn't move them. Swept across every
seventh start and eleventh goal on two maps: `ctf_truefort` 0 → 143 goes from a
32-node route to a 14-node one with **one** node in common. ⚠️ **The two teams
also don't agree about a leg in the first place** — a gate is passable only to
the team that owns it — so the per-team test compares blue's crowded answer
with **blue's own uncrowded** answer; comparing it with red's fails on the
gates and says nothing about occupancy.

⚠️ **Re-measured after this landed — one dictionary lookup per edge per search
isn't free: twelve bots cost `0.154ms` a tick, 0.5% of the budget, so the
headroom claim is untouched.** Configuration-dependent, previously unrecorded:
the default (Debug) build reads **0.369ms, 1.1%**. `README.md`,
`docs/ARCHITECTURE.md`, `docs/PLAN.md` and this file's header now all carry the
figure with its build; a re-measure that omits which build ran looks like a 2x
regression.

**Nine mutations tried and all nine bite**: routes never counted, self not
excluded before the search, the add not idempotent, the route left counted after
removal, removal reading the wrong team, zero counts left in the map, one map
shared by both teams, a dead bot still crowding, and an arrival leaving its route
counted. ⚠️ That last one needed a fixture that actually **arrives** (placed on
its goal, grounded, stopped) — without it the branch was invisible: an arrived
bot would leave its whole route crowded for ever.

### Route variety, scored: what occupancy is worth and why lanes do not replace it

`Gg2.Tests.RouteDiversity`, `RouteDiversityTests`, `RouteDiversityMatchTests`,
`LaneWalkingTimeTests`, `Gg2.Bots.BotLanes`, `BotLanesTests`. Measured
2026-09-04 against `fa9e87a`, Release.

⛔ **Everything that checked route variety before this tested the MECHANISM, not
the outcome** — a crowded edge costs more, four bots on one swept leg disagree,
a three-node toy graph flips. A mechanism that fired and achieved nothing passed
all of them, which is exactly how the deleted per-edge jitter survived long
enough to be caught by hand. `RouteDiversity.Spread` scores the outcome: mean
pairwise Jaccard distance over routes' edge sets, 0 single file, 1 disjoint. It
reproduces item 4q's recorded measurement before being trusted for anything new.

⚠️ **`ScriptedBot.Occupancy` is set in ONE place in the repo** —
`BotPopulationService`, the live `--bots` server. `HeadlessMatch`, `MatchRunner`
and every rollout leave it null, so **`eval` and all training have always run
with no route-variety mechanism at all.** That is the real baseline, not a
thought experiment.

⛔ **Occupancy's leg-level capability is not its match contribution, and quoting
the first for the second overstates it about fourfold.** Four bots forced onto
the identical leg is item 4q's setup and does not occur in play: `BotRoles.Assign`,
`BotRoles.SpreadX` and `BotGoalSpot` have already given a team different goals.

| | spread | route inflation |
|---|---|---|
| no mechanism (what `eval` runs) | 0.634 | 103.2% |
| occupancy | **0.785** | **105.5%** |
| lanes, 4 @ 1.75 | 0.766 | 110.0% |

21 maps, 3 seeds, 12 bots, 3,000 ticks. Inflation prices each installed route
against the cheapest route between its **own** endpoints on the untouched graph.

- ⚠️ **Widening `SpreadX` does NOT buy route variety and is a dead end.**
  ×1.5/×2.0/×3.0 give 0.645/0.645/0.657 against 0.634 — it saturates almost
  immediately. It moves where a bot ends up **standing**, not which corridor it
  walks, and the statistic is dominated by the long shared middle. **Do not
  retry it.**
- ⛔ **`BotLanes` works and is still the wrong trade.** Static per-edge cost
  fields, derived by iterative penalisation of corridors an earlier lane's swept
  routes used — structured, shared, coarse, and unlike occupancy precomputable,
  which was the whole point. Occupancy buys **0.066 of spread per point of
  inflation; lanes buy 0.019.** A static field cannot know where the crowd is,
  so it diverts every route through a corridor whether anyone is on it or not.
  No setting closes the gap: the sweep over {2,3,4} lanes × {1.20,1.35,1.75}
  penalty is monotone in both dials.
- ⚠️ **Three or more lanes lose a scenario leg at every penalty, always lane
  index 2**, while `BotLanesTests` still shows every lane reaching what lane 0
  reaches — so the **follower** cannot execute a route the **search** returned.
  Undiagnosed; it is why the arrival-safe frontier stops at two lanes.
- ⚠️ **A route's edge-cost sum understates what a lane costs by about 4
  points** — 102.2% proxy against 106.0% walking time at 4 lanes @ 1.20. The sum
  misses what the follower spends steering, jumping and recovering off-route.
  **Price a routing change in walking time, not in cost.**
- ⚠️ **`ctf_avanti` runs at ~147% inflation with no mechanism at all**, against
  ~101% everywhere else, and it is **not the setup gates** — the obvious
  hypothesis, tested and rejected. Each route is now priced under the
  `BotPathState.PlanQuery` it was actually planned with, so a bot that routed
  with the gates shut is compared against the cheapest route *with the gates
  shut*; `ctf_avanti` moved 146.9% → 147.4%. Unexplained, and the largest
  unexplained number this work produced.
- ⚠️ **Price a route against the query it was PLANNED under, never a fresh one
  for its team.** Before that fix several maps read *below* 100% — a route
  cheaper than the cheapest, which is impossible and was the tell. ⚠️ A residual
  99.1% remains on two maps and is expected: the "ideal" comes from the same
  deliberately-suboptimal A\*, so it is an upper bound too, not the true optimum.

✅ **The lane plumbing is inert in the shipped configuration** and
`NavRouteIdentityTests` is untouched: `NavSearch`'s `laneCost` is empty
everywhere, lane 0 is a copy of the graph's costs (asserted exactly), and a bot
given no lane routes as before. ⚠️ **A lane is indexed by edge position**, so
`ScriptedBot.SetMap` drops it exactly as it drops the route and the occupancy
flag — a field held across a map change prices the new graph with the old one's
numbers.

⚠️ **Assign a lane by a bot's place among its OWN TEAM, never by slot**
(`BotRoles.Lane`). Teams alternate down the roster, so `slot % 4` hands red the
even lanes and blue the odd ones — a team of six would use two of four corridors
while every count and bound still looked right.

⛔ **These measurements are `Skip`ped and must stay that way.** Left in the
default run they added about eighty seconds of CPU-saturating match simulation
alongside `ServerPacingTests`, `TickPacingTests` and `TimerResolutionTests`,
which measure the wall clock — **one full-suite run in four failed and three
re-runs did not reproduce it**, the signature of load rather than of a bug.
(The failing test's identity was never captured, so this is a diagnosis and not
a proof.) With them skipped the suite is stable at 31s across three consecutive
runs. ⚠️ **A measurement that makes a timing gate flaky is worse than a slow
one**, because the next session cannot tell a real failure from it — and this
is `docs/BOTS.md`'s "pacing measures the machine" arriving from the other
direction, as something a test can *cause* rather than suffer.

**Untested, and recorded so it is not lost:** lanes are inefficient because the
penalty lands on whole corridors including stretches with no alternative. A
*sparse* lane — charging only edges that have a comparable alternative, so the
penalty falls where it changes a decision — addresses the identified mechanism.
Not attempted, deliberately: chasing variants until one wins is how a post-hoc
result gets mistaken for a pre-registered one.

### `--no-bot-occupancy`: the dispersion mechanism is now a switch

`ServerOptions.BotOccupancy`, `BotPopulationService`'s bot creation site. Gated
by `BotOccupancyFlagTests`. Added 2026-09-04.

⚠️ **ON by default, because turning it off is a BEHAVIOUR change and not a
tuning knob.** Occupancy is the largest single source of route variety in the
bot layer: match dispersion falls **0.785 → 0.634** without it, and nothing
replaces it — static lane fields buy 0.019 of spread per point of route
inflation against occupancy's 0.066, and widening `BotRoles.SpreadX` saturates
almost immediately (0.634 → 0.657). "Route variety, scored" above has all three.

✅ **What turning it off buys is CONSISTENCY, not speed.** Before this flag,
`ScriptedBot.Occupancy` was set in exactly one place — the live `--bots` server —
so the live game was the only configuration in the project that routed with it.
`eval`, `HeadlessMatch`, `MatchRunner` and every training rollout have never had
it. `--no-bot-occupancy` makes all four agree, which is worth something for
training transfer.

⛔ **It is NOT a performance switch, whatever the search numbers suggest.** A\*
is ~26-30% of a twelve-bot tick and that tick is 0.151 ms against a 33.3 ms
budget, so what this changes about search cost is a fraction of a percent of the
frame. Choose it on how the bots should play.

- ⚠️ **Null, not an empty store.** `NavSearch` hoists `occupancy is { Count: > 0 }`
  once per search, so a null store costs nothing per edge where an empty one
  still costs the key arithmetic and an interface-dispatched lookup on every edge
  the frontier touches. An empty store would also make the two configurations
  indistinguishable from outside, which is how a flag that does nothing survives.
- ⛔ **Both directions are gated, and so is the mechanism itself.** "No bot has a
  store when asked" passes on a server that never wired occupancy up at all;
  "every bot has one by default" passes on a server that ignores the flag. Only
  the pair says the switch decides it — and a third test waits for real counts to
  appear, because a store that stayed empty for a whole round would satisfy both
  and disperse nothing.

### Precomputing Dijkstra: the routes cannot be stored, the distances can

`Gg2.Bots.NavDistances`, `NavSearch.Find`'s `distances` argument, gated by
`NavDistancesTests`. Measured 2026-09-04, Release.

⛔ **Storing the ROUTES does not work on a live server, and the reason is
`BotOccupancy`.** A stored route is still the right answer only if nothing has
made one of its own hops dearer or illegal since it was computed — occupancy
raises an edge a team-mate is walking, and the blacklist removes one outright.
Measured in real matches by
`RouteDiversityMatchTests.HowOftenAStoredRouteWouldStillBeValid`, 21 maps × 3
seeds × 12 bots, 24,289 installed routes:

| | routes a stored answer would still serve |
|---|---|
| no occupancy (`eval`, every rollout) | **98.4%** |
| occupancy on (the live `--bots` server) | **23.5%** |

⚠️ **Two earlier scratch figures for this are SUPERSEDED and should not be
quoted: "0.7-20% of queries, typically under 10%", and a blended "table else A\*"
path at "0.7x".** Neither was ever in the repo — the same provenance as the
2.19x that the in-repo 1.36x replaced. The conclusion survives; the number was
pessimistic by roughly twice.

⛔ **And occupancy is NOT the only invalidator, which the control caught.** This
measurement first asserted its no-occupancy arm at an exact 100% and failed:
the blacklist is per bot and runs in every arm, so **1.6% of routes are stale
with no crowding mechanism at all.** So even the occupancy-free paths need the
guard rather than being trivially safe — which is a real cost on the one idea
that looked free.

⚠️ **Per map it ranges from 9.4% (`ctf_classicwell`) to 75.3% (`tdm_mantic`).**
Deathmatch scatters bots and contends little; a CTF map funnels a team down the
same corridors. **A single average hides the case a route table would be built
for.**

⛔ **Size is NOT the objection, and assuming it is sends you down the wrong
path.** A *next-hop* table — store one node per pair and follow it hop by hop —
is N² like the distance table: on `ctf_truefort`'s 687 nodes that is 0.9 MB as
`ushort` against the distance table's 1.9 MB as `float`. Storing whole paths
would be tens of MB a profile, but nobody would. **The objection is validity,
not memory.**

✅ **The narrowed version of the idea is written up as a PROPOSAL** —
`docs/PROPOSAL-ROUTE-TABLE.md`: an all-pairs next-hop table walked and
*verified* per query, falling back to A\* on a dirty walk. It is not
implemented, every claim in it carries an evidence status, and the number that
decides it (is the walk cheaper than the search?) is not measured. Read it
before re-deriving any of this.

⚠️ **A route table also needs dimensions the distance table does not.**
Distances split on the three class arcs alone, because a team, an intel flag and
a shut gate only ever REMOVE edges and gates-open stays a lower bound for
everyone. An exact next-hop answer has no such slack: a route through a gate
this asker cannot pass is *wrong*, so the table would need a copy per team and
per gate state as well — roughly 16 tables where distances need 4.

✅ **Storing the DISTANCES does work, because a distance is only ever a lower
bound.** Occupancy multiplies, the blacklist removes, a closed gate removes —
all of them only make a route dearer — so a cost measured on a more permissive
graph is admissible for every asker. `NavSearch` still reads the live occupancy
map on every edge; the table only says which way to look.

⚠️ **A table is admissible only for askers whose edges are a SUBSET of the ones
it was built with**, and this is where most of the value is:

| heuristic | mean µs | speedup |
|---|---|---|
| straight line (shipped) | 23.35 | — |
| table built with every arc | 22.11 | 1.06x |
| table matched to the asker's class | **17.20** | **1.36x** |

⛔ **The permissive table is nearly worthless — 1.06x — and that is not
obvious.** Rocket jumps are 14% of the shipped edges and they are *shortcuts*,
so a table that includes them badly understates a walker's real distance and the
bound goes slack exactly where it was supposed to bite. Gates can stay open in
every table (a gate only removes edges, so gates-open is still a lower bound);
the jump arcs cannot.

⚠️ **Pops fall further than time does, and the gap is the table itself.** A
random read into an N² array is a cache miss where `Gml.PointDistance` is a few
flops on data already in registers — `ctf_waterway` 285→85 pops for 2.15x,
`koth_gallery` 78→28 for 2.89x, `ctf_truefort` 564→541 for 1.00x. **Do not
predict this change's value from a pop count.**

⚠️ **An earlier scratch-probe number of 2.19x for the same idea is SUPERSEDED
by the 1.36x above.** The probe filtered its pairs differently and is not in the
repo; it should not be quoted.

✅ **Free as a reachability oracle.** An infinite entry on the permissive graph
means no query can make the trip, so the search returns without a single pop
instead of expanding the whole reachable component — 3,316 such pairs on
`cp_dirtbowl` alone.

**Cost, and why this is not yet wired in:** 0.2-2.7 MB a table and 3-94 ms to
build one, times a table per ability profile, at every map change. Against A\*'s
~26% of a twelve-bot tick, 1.36x is about 7% of the tick — real, modest, and it
would move every route, so `NavRouteIdentityTests`' 25 digests and the three
pinned bot measurements would all need re-recording. **The argument is a
judgement call and has not been made.** `distances` defaults to null, nothing
in the shipped path passes it, and the route digests are untouched.

### Shipping the precomputed distances: `--nav-distances`

`Gg2.Bots.NavDistanceSet`, `PathFollower.Distances`, `ScriptedBot.Distances`,
`ServerOptions.NavDistances`, `HeadlessMatchOptions.NavDistances`. Gated by
`NavDistanceSetTests`; measured by
`RouteDiversityMatchTests.WhatThePrecomputedHeuristicCostsInRouteCost` and by
`BENCH_COMBAT` with `BENCH_NAVDIST=1`. Measured 2026-09-04, Release.

⛔ **This is an ARM, not a speed-up, and every number taken with it on has to say
so.** `NavConstants.SearchWeight` makes the search a *weighted* A\*: the
heuristic is inflated on purpose, so it chooses which route comes back and not
merely how fast. With the straight-line estimate the inflated heuristic still
sits at about 0.914 of the true remaining cost; with an exact one it sits at the
weight itself. **Every route moves.** A table taken with the flag on is not
comparable to a baseline taken without it.

✅ **And the routes move the RIGHT way, which was not derivable from the
speed-up.** 21 maps × 3 seeds × 12 bots × 3,000 ticks, each installed route
priced against the cheapest route between its own endpoints:

| | route inflation | dispersion |
|---|---|---|
| straight line (shipped default) | 103.6% | 0.634 |
| `--nav-distances` | **99.1%** | **0.613** |

⚠️ **Below 100% is meaningful here rather than impossible.** The "ideal" is
produced by the same deliberately-suboptimal A\* on the straight-line heuristic,
so it is an upper bound and not the true optimum — 99.1% means the exact
heuristic routes *better than the incumbent search does*, not better than
optimal.

- ⛔ **It is not uniformly better: 3 of 21 maps regress**, and the worst is the
  one that was already unexplained. `ctf_avanti` **147.4% → 158.5%**,
  `tdm_mantic` 102.5% → 108.1%, `koth_corinth` 101.6% → 102.2%. Whatever is
  wrong with `ctf_avanti` gets *worse* under an exact heuristic, which is a live
  clue and the first thing to look at when that anomaly is next picked up.
- ⚠️ **What is lost is a little route variety, and only a little: 0.634 →
  0.613.** Better routing converges bots onto the same best corridors. It stays
  small because **the goal layer is untouched** — `BotRoles.Assign`,
  `BotRoles.SpreadX` and `BotGoalSpot` disperse a team by GOAL before the search
  is ever consulted, and that is where most of the match-level dispersion comes
  from. Per map it goes both ways (`koth_valley` 0.782 → 0.651, but
  `dkoth_sixties` 0.629 → 0.708).
- ✅ **The search count falls too, and that half is deterministic.**
  `ctf_truefort`, twelve bots: **320.0 → 271.7 searches per 1000 ticks**, goals
  175.7 → 155.5. Better routes are lost and re-planned less often, so part of
  the tick saving is *fewer* searches rather than *cheaper* ones.
- ⚠️ **The tick figure is noisy and should be re-taken on a quiet machine.**
  Paired runs on `ctf_truefort` read 21.87/20.97 µs a tick without and
  16.90/18.69 with — **roughly 1.12-1.29x**, against a 1.36x expectation for the
  search alone at ~30% of the tick plus the search-count drop. The `settled=True`
  and search-count columns agreed across every run; only the wall clock moved.
  **Quote the range, not a point.**

⛔ **End to end on `eval`, the flag is a WASH at the default match count**, and
this is the number that decides whether it is worth turning on. The table is
built once per map and the saving accrues per match, so the trade depends
entirely on matches-per-map. `ctf_truefort`, one thread, wall clock:

| matches on the map | without | with | |
|---|---|---|---|
| 4 (the `eval` default) | 5,075 / 4,526 ms | 4,318 / 4,518 ms | **no effect** |
| 24 | 17,731 ms | 16,036 ms | **1.11x** |

⚠️ **The first pair of 4-match runs looked like 1.18x and the repeat came back
5,075→4,526 against 4,318→4,518 — noise.** Two runs an arm is the minimum here.
A default `eval` (4 matches × 22 maps) pays a fresh derivation per map for a
saving that has four matches to earn it back in, and does not.

**What it costs at the map change:** `ctf_truefort` builds **4 tables, 7.20 MB**.
One reverse Dijkstra per node per distinct profile, on the tick thread, logged
with the elapsed time — the same placement and the same reasoning as the nav
generator's own build.

⛔ **Build a table per ability profile or do not build one at all.** The
permissive table measured **1.06x** where the class-matched one measured
**1.36x**. `NavDistanceSet` is the whole reason this is shippable: it splits on
the three class-arc flags and on nothing else. On `ctf_truefort`, **323,969 of
471,969 node pairs are strictly tighter for a walker than for a rocket-jumper**
— that is how much a permissive table gives away.

- ✅ **Only the class arcs split the set.** Team, the intel flag and the setup
  gates all only ever *remove* edges from an asker, so a table built with every
  gate open is a superset of any gated asker's graph and still a lower bound.
- ✅ **Profiles the graph cannot distinguish share one table.** A graph with no
  sticky arcs answers the same whether or not the asker may take one, so eight
  profile keys collapse to the two to four a real graph needs.
- ⚠️ **A set is indexed by NODE, so `ScriptedBot.SetMap` drops it** — exactly as
  it drops the route, the lane and the occupancy flag. A set held across a map
  change would hand the search a bound derived from a graph the bot is no longer
  standing on, and an over-estimate is not a slow route, it is the wrong one.
- ✅ **Read-only once built, so one set serves a threaded `eval`.** Every bot
  owns its own `NavSearch`; nothing writes to the set after `Derive` returns.

✅ **Off by default, and the default path is untouched.** `distances` is null
everywhere unless the flag is passed, `NavRouteIdentityTests`' 25 route digests
are unchanged, and the three pinned bot measurements are unchanged. **That is
the falsification condition for this whole section**: if those digests ever move
without the flag, the wiring has leaked into the default path.

⛔ **Two bugs here were found by RUNNING the thing, and neither was visible to a
test.** Both are the same shape — a derivation in the wrong scope — and both
would have shown up as "the flag makes it slower", not as a failure:

- **The server rebuilt at every ROUND, not every map change.** `MapChanged` runs
  per round and re-parses the graph, so the object is never the same twice and
  reference equality cannot detect the repeat. 334-467 ms *every round end*,
  found by reading the server's own log across 70 rounds. Fixed with a
  `map/area/nodes/edges` key. ⚠️ The residual assumption is that one map+area
  yields one graph; a regenerated graph of the *same* shape but different edges
  would go stale, and nothing observed can produce one.
- **`MatchRunner` derived one set per MATCH.** Every match in a batch plays the
  same graph, so a batch of `n` computed the identical tables `n` times — a few
  hundred ms each against a match that costs about 0.6 s. `MatchRunner.Run`
  hoists it and passes it down; `HeadlessMatch` still derives its own when a lone
  caller passes none.

⚠️ **A bot that joins mid-round needs the set too, and that is a SECOND call
site.** `MapChanged` reaches the bots that survived a rotation; the creation site
in `BotPopulationService` reaches the ones that fill in afterwards. Missing the
second gives a **mixed** configuration — some bots on the exact heuristic and
some on the straight-line one — which every count, every log line and every
other gate would report as healthy. Gated by
`NavDistanceSetTests.EveryBotOnTheServerGetsTheTablesWhenAsked`, both directions,
and confirmed by mutation: removing the line fails it.

⚠️ **`NavDistanceSetTests.TheSelectedTableNeverOverestimates` was deliberately
made coarser.** At the 7/11 stride over sixteen query classes it cost **37 s** —
enough CPU to risk the wall-clock pacing flake this branch already paid for
once. At 13/17 over four profiles it costs **5 s** and still checks **82,114
pairs across every shipped graph**. Do not "restore" the finer sweep without
re-reading "Route variety, scored" above.

### CPD-Search: the next-hop table as the ANSWER, verified per query

`Gg2.Bots.NavDistances.NextHop`, the CPD branch of `NavSearch.Find`. Gated by
`NavDistancesTests.TheNextHopChainPricesToTheDistance` and the `Cpd*` tests in
`NavSearchTests`. Decided 2026-09-04 after a prior-art search; the search and
the design reasoning are in `docs/PROPOSAL-ROUTE-TABLE.md`, "Decision".

✅ **This is a KNOWN technique, and it is not the two-stage design the proposal
sketched.** Bono, Gerevini, Harabor and Stuckey, *Path Planning with CPD
Heuristics*, IJCAI 2019 (`https://www.ijcai.org/proceedings/2019/0167.pdf`),
"CPD-Search": an all-pairs first-move table (a Compressed Path Database) built
offline on static costs and queried after edge costs have been **perturbed
upward only** between planning episodes — which is this layer's situation
verbatim. Occupancy is ×1.25, a blacklisted edge is +∞, a shut gate is +∞, a
class that cannot fly an arc is a subset. `NavDistances` was already that CPD's
static cost; the first hop falls out of the same reverse Dijkstra for nothing.

**The algorithm, as shipped here.** A* with `h(n) = Between(n, goal)` exactly
as before, plus: for every node the frontier reaches, walk its stored next-hop
chain to the goal pricing each hop at its LIVE cost — gate, class arc,
blacklist, occupancy, lane — memoised per search so suffixes are shared. That
walk's cost is a real route's cost, so the cheapest one seen is an **incumbent**
with an upper bound `u`. When the frontier's cheapest `g + h` reaches `u` the
incumbent is provably optimal and is returned: the search prefix to the
incumbent's node, then its chain.

- ✅ **A clean query costs one walk and zero expansions.** With nothing on the
  chain perturbed, `h′(start) = h(start)`, so `u = f(start)` and the very first
  pop returns. That is the proposal's "hit" case.
- ✅ **A dirty query is NOT "walk, discard, then search from scratch".** The
  walk becomes the bound that prunes the search, and the memo means a chain
  walked from one expanded node is not walked again from the next. The
  proposal's "a miss pays for the walk AND the search" was a property of its
  own two-stage shape, not of the idea.
- ✅ **The congested live server is IN scope.** One ×1.25 hop on a 60-hop chain
  is a perturbation under 1%; an exact search then expands only nodes whose
  `g + h` lies inside that slack — a corridor round the crowded edge, not the
  541 pops `ctf_truefort` paid before. The 23.5% "still valid" figure above
  bounds the zero-expansion case, not the cost of the rest.

✅ **Measured** — `NavDistancesTests.WhatTheTableBuys` (a `Skip`ped
measurement; toggle the attribute to re-take it), 22 graphs, 500 pairs each
from a random node to one of four random objectives, **pairs the asker can
actually route**, class-matched table, µs per query, Release, 2026-09-04:

| arm | straight line | CPD-Search, matched table | |
|---|---|---|---|
| clean — no crowd: `eval`, every rollout | 7.98 | **1.11** | **7.16x**; one pop on 19 of 22 graphs |
| crowd — six team-mates' routes on the same four objectives | 11.84 | 11.19 | **1.06x** — a wash |

- ✅ **The clean arm is the proposal's U1 answered.** A verified walk is
  0.4-0.9 µs on most graphs, a tenth of the search it replaces, and the three
  graphs above 1 µs (`ctf_truefort` 12 pops, `ctf_conflict` 9, `cp_egypt` 69)
  are the gate case below, not the walk.
- ⛔ **The crowd arm is NOT a speed-up, and the weighted-table 1.36x two
  sections up was.** Proving optimality against a heavy crowd expands the whole
  perturbation slack — `cp_egypt` 255 pops against the straight line's 106,
  `ctf_2dfort` 0.61x — where the weighted search bought its 1.36x by proving
  nothing. Running unweighted stands on the occupancy argument below; its
  price is written here so it is not re-measured. **Not yet measured**: what it
  does to a tick, where better routes are re-planned less often (the exact
  heuristic alone took `ctf_truefort` from 320.0 to 271.7 searches per 1000
  ticks), and whether a live server's crowd is as heavy as this harness's.
- ⛔ **The residual pops on a CLEAN graph are gates, and they were hiding
  inside every earlier number.** The table is built gates-open, so a chain
  through the other team's gate prices at infinity and the search runs. Worse,
  a goal BEHIND such a gate is finite in every table and unreachable for the
  asker, and the search expands the whole component to say so: **529 of 1,029
  random node→objective pairs on `ctf_truefort`, 234 of 734 on
  `ctf_classicwell`, 167 of 667 on `cp_dirtbowl`**. "564 → 541 pops for 1.00x"
  on truefort in the section above was mostly that population, not the
  heuristic. The harness now excludes them and prints the count; every arm
  pays the same for them. A per-team table, or a per-team reachability bit
  beside the distance, is the lever — unbuilt, because how often the objective
  layer actually sends a bot behind the other team's gate is not known.

✅ **And in matches, the routes are BETTER than the weighted exact heuristic's
were** — `RouteDiversityMatchTests.WhatThePrecomputedHeuristicCostsInRouteCost`
(same `Skip`, same 21 maps × 3 seeds × 12 bots × 3,000 ticks, same
straight-line "ideal" that is an upper bound, not the optimum), 2026-09-04:

| | route inflation | dispersion |
|---|---|---|
| straight line (shipped default) | 103.6% | 0.634 |
| weighted A\*, exact heuristic (the section above) | 99.1% | 0.613 |
| **CPD-Search, unweighted** | **97.2%** | 0.616 |

`ctf_avanti` — the map the exact heuristic made WORSE, 147.4% → 158.5%, and
the largest unexplained number in this layer — goes the other way under an
optimal search, **147.4% → 132.3%**. So whatever is wrong there is not made
worse by a better heuristic after all; it was made worse by the WEIGHT on one.
That narrows the anomaly rather than closing it. `tdm_mantic`'s regression
(102.5% → 108.1%) is gone too (101.8%); `koth_corinth` still ticks up
(101.6% → 103.5%). Dispersion is unchanged to the third decimal: better routing
converges bots, the goal layer disperses them, and neither moved.

⛔ **With the table, the search runs UNWEIGHTED, and that is a decision, not an
omission.** `NavConstants.SearchWeight` inflates the STRAIGHT-LINE heuristic,
which is loose; an exact relaxed distance is consistent, so plain A* on it
expands only nodes on or within the perturbation of the optimal route and the
closed-set skip is sound rather than a licensed shortcut. Two reasons the weight
must not be carried across:

- A weighted termination test (`ε·f ≥ u`, the paper's bounded-suboptimal form)
  would accept the UNPERTURBED chain the moment it is within 30% — which every
  occupancy-perturbed chain is — and silently erase the dispersion occupancy
  exists to buy (0.634 → 0.785). `ε = 1` is the only setting under which the
  crowd-pricing feature survives.
- The weight was tuned against the suite's walking time with the straight-line
  heuristic (`NavConstants.cs`, `SearchWeight`); nothing there measured it with
  an exact one, and `--nav-distances` already showed an exact heuristic routes
  better than the weighted incumbent (99.1%).

⚠️ **`h′ ≥ h` has to be TRUE and it is not automatic.** The walk prices a hop
`a → b` at the cheapest ROUTABLE edge between them under live costs. That is at
or above the cheapest in-profile static edge the table's Dijkstra used, because
routable-for-this-asker ⊆ in-profile (gates only remove) and live ≥ static
(everything only raises). Parallel edges between one pair — a walk arc and a
jump arc — are why "cheapest" has to be said. `NavSearchTests` asserts the
incumbent's returned cost equals its live-priced cost, hop by hop.

⚠️ **The table is laid out `[goal][from]`, and `Between` reads it that way.**
The heuristic asks `Between(n, goal)` for many `n` and one `goal`, and the walk
follows `NextHop(n, goal)` for a chain of `n` and one `goal`: both are one
contiguous row now, where the `[from][goal]` layout the distances shipped with
made every read a stride-N cache miss — the "pops fall further than time does"
observation two sections up was that layout, not the idea.

⛔ **The build-cost amortisation problem is INHERITED unchanged.** The next-hop
table comes from the Dijkstra the distances already run, so the 334-467 ms per
map that made `--nav-distances` a wash at four matches a map is still there.
The fix, not built: derive a ROW lazily per (goal, profile) on first use — one
reverse Dijkstra, ~5-140 µs — since bots route to a handful of goal nodes. That
needs the threaded `eval` revisited (`NavDistanceSet` is read-only after
`Derive` by design).

**Prior art checked and rejected**, so nobody re-searches it: CPD repair
(Verzeletti, Botea, Zanella, SoCS 2019) fixes a SHARED table after an obstacle
change — wrong shape for a per-bot 150-tick ban. D\* Lite / LPA\* keep a search
tree per (agent, goal) and repair it; goals change 155-175 times per 1000 ticks
and costs differ per team and per bot, so the trees are torn down constantly,
and the known result is that they lose to from-scratch A* exactly when search
is already cheap. Tree Cache / JSTS (Carlson, Harabor, Stuckey, SoCS 2025) is
O(N) storage at 20% mean suboptimality — a trade this graph does not need at
N=687. CRP / contraction hierarchies solve the million-node case where all-pairs
is impossible; here it is 0.9 MB.

### Shipping the tables: `.dist.gz`, and the half that is NOT stored

`Gg2.Bots.NavDistanceFile`, `NavDistances.FromNextHops`,
`NavDistanceSet.LoadOrDerive`, `NavGen`'s `--no-distances`. Gated by
`NavDistanceFileTests`. Measured 2026-09-04, Release, over all 25 shipped
graphs.

**The tables are now ON by default and READ from disk.** `--nav-distances` is
still accepted and is a deliberate no-op; `--no-nav-distances` is the switch
that moves anything now. `gg2-server navgen` writes `<key>.dist.gz` beside each
`<key>.txt.gz`.

⛔ **Only the next-hop half is stored. The cost half is rebuilt on load, and
that is not an optimisation — it is the difference between a feature that can
ship and one that cannot.**

| stored, gzipped | size |
|---|---|
| next-hop halves, one gzip stream per graph (**what ships**) | **1.62 MB** |
| next-hop halves, gzipped separately per table | 2.47 MB |
| cost halves | **35.07 MB** |
| the graphs themselves, for scale | 1.06 MB |

A column of hop indices repeats and a column of floats does not, so the two
halves of the same table differ by **14x** compressed. Storing both would have
made `nav/` a 37 MB directory to save a fraction of the load.

**Rebuilding is also FASTER than deriving, not a penalty paid for the space.**
The hops are the parent pointers of a shortest-path tree, so one memoised walk
up the tree per node prices every node on the way and no priority queue is
needed at all: **5.74x** cheaper than the reverse Dijkstras across the 25
graphs, and end to end at a map change on `ctf_truefort`, **377 ms derived
against 62 ms loaded — 6.1x**, both logged by the server itself.

⚠️ **The rebuild reproduces `MathF.BitDecrement` and a version that did not
would be wrong in the UNSAFE direction.** `Derive` rounds every stored cost
DOWN so the table stays an admissible lower bound. A rebuild that rounded to
nearest is one ULP high, which lets the search prune the optimal route — and it
passes any tolerance-based comparison, including the 1e-3 one this work used
while prototyping. `NavDistanceFileTests.ARebuiltTableIsBitIdenticalToADerivedOne`
compares `BitConverter.SingleToInt32Bits` over all 471,969 pairs of
`ctf_truefort` for that reason. The same class of bug hid a second time: the
first rebuild left unreachable pairs at the array's default 0 rather than
filling ∞, which claims every unreachable pair is free.

⚠️ **A file is bound to one build of one graph, three ways** — node count, arc
mask, and a SHA-256 over the graph's own nodes and edges. A mismatch is
**refused silently and the set derived instead**, because a stale artefact must
never be what stops a server serving. That is the `nav/manifest.tsv` failure one
level down: a map edited without regenerating would otherwise path bots through
a wall.

⚠️ **`navgen --check` compares the tables against the graph it JUST BUILT**, not
the one on disk, and reports `tables STALE` per map. A check against the
on-disk graph would call a table current whenever it matched a stale graph —
which is the exact drift being checked for.

### The table count is FOUR on every shipped graph, not two

`NavDistanceSet.Derive` masks the eight ability profiles down to the arc types a
graph actually contains. The code comment claimed that collapsed to "typically
two — a walker's and a rocket-jumper's". **Measured 2026-09-04: it is four on
all 25 shipped graphs**, because every one carries both double-jump and
rocket-jump arcs and none carries a sticky-jump arc, so `present` is 3 and eight
profiles collapse to four. The comment is corrected in place.

It matters twice over: it doubles the memory (**149.8 MB across the shipped
graphs**, 100 tables rather than 50) and it doubles the file. `ctf_truefort`
alone is 10.8 MB in memory and 253 KB on disk.

### ⚠️ The gates measure a DIFFERENT ARM from the server, and always did

⛔ **`ScenarioWorld` never sets `Distances`, and `NavRouteIdentityTests` calls
`NavSearch.Find` without a table.** So the three pinned end-to-end bot
measurements and all 25 route digests run on the **weighted straight-line**
heuristic, while a real server and `eval` now run on the exact one.

This is why turning the tables on moved **none** of them, and the prediction
recorded in "Shipping the precomputed distances" — that enabling it "moves every
route, so the 25 digests and the three pinned bot measurements would all need
re-recording" — **did not come true**. The prediction was right about routes and
wrong about the gates, because it assumed the gates exercised the feature.

**Nothing here is broken, and that is the problem**: a green suite now says
nothing about the arm production runs. The digests still do their real job —
catching nav-graph drift — and the pinned measurements still gate the scripted
layer. Neither covers the routing a server actually does.

⚠️ **Deliberately NOT changed, because it is a decision about what the alarms
mean, not a bug fix.** Wiring `ScenarioWorld` to the tables would move all three
pinned numbers and every digest at once, and a pin that moves for a reason
nobody wrote down is worse than a pin measuring the wrong arm. If it is done, it
is a re-record with the diff stated in both directions, per `CLAUDE.md`.

### Two ways to spend a next-hop table, and why BOTH ship

`NavPlan.DecideByCrowd` picks between `ChainBoundsSearch` (CPD-Search as
published) and `ChainOrSearch` per query, on whether anything is actually crowded. Gated by
`NavDistancesTests.BothPlansAgreeOnCost` and
`NavDistancesTests.AutoPicksThePlanThatMatchesTheCrowd`; measured by
`WhatTheTableCostsAgainstMaster`. Measured 2026-09-04, Release.

⚠️ **This was consolidated to CPD-Search alone and then REVERSED, and the
reversal is the instructive part.** The consolidation argued that a 10-16%
situational gap on a search that is ~26-30% of a 0.151 ms tick was not worth a
second implementation. But the maintenance cost was never measured, only
assumed: deleting it removed **18 lines of logic** and about 42 lines of XML
documentation. **18 lines for a situational 10-16% is a good trade in a project
whose stated purpose is a high-performance headless server** — and `DecideByCrowd` means
it costs no configuration surface at all. ⛔ **Measure the cost side of a
"not worth the complexity" argument before making it.**

The two differ by **exactly one idea**. Both walk the table's chain once up
front, both then run the same unweighted A\* on the same exact heuristic, and
both return the same optimal route. The only difference is what happens to a
chain that has been perturbed: CPD-Search prices it at live cost and keeps it as
an **incumbent upper bound**, re-walking from each expanded node (memoised) and
terminating when the frontier's lower bound reaches it; the proposal's design
**throws it away** and searches unbounded.

✅ **They return the same answer, and that is asserted rather than assumed** —
3,427 legs across every shipped graph and query class, 3,663 of them with a hop
banned or crowded, compared on **priced cost** rather than node sequence (two
distinct routes can price identically when a walk and a jump join the same
pair). Without that gate the timing column below would be measuring which arm
does less work, not which arm is faster.

⛔ **Against MASTER — the shipped weighted straight-line A\* — the answer
depends entirely on whether occupancy is running, and it is not close.** Three
arms measured in one interleaved run, mean over 22 graphs, 500 pairs each, three
repeats of the whole measurement:

| | master | CPD-Search | verify-then-search |
|---|---|---|---|
| **no occupancy** (`eval`, every rollout) | 8.47 / 8.36 / 8.83 µs | 1.20 / 1.11 / 1.29 µs — **6.8-7.5x master** | 1.31 / 1.44 / 1.41 µs — **5.8-6.5x master** |
| **crowded** (the live `--bots` server) | 12.65 / 11.99 / 13.75 µs | 14.00 / 12.31 / 13.36 µs — **0.90-1.03x, a wash** | 11.61 / 10.73 / 11.94 µs — **1.09-1.15x** |
| routes returned | **108.5% of optimal** | optimal | optimal |

⛔ **Master is fast because it is allowed to be WRONG, and that is the whole
explanation.** It is a *weighted* search — `NavConstants.SearchWeight` inflates
its heuristic by 1.3 and the closed-set skip goes with it — so it commits early,
expands few nodes and returns a route **8.5% dearer than optimal**, stable to a
tenth of a point across every repeat. Both table plans are unweighted and must
*prove* optimality. Under crowding that proof costs more expansions than the
exact heuristic saves: on `cp_egypt` master pops **106** nodes where CPD-Search
pops **255**. **A speed comparison that does not price the routes has master
winning a race it is running downhill.**

- ✅ **Where occupancy is off, the table wins on both axes at once** — about
  **7x** the speed *and* 8.5% cheaper routes. That is `eval` and every training
  rollout, and it is not a marginal call.
- ⚠️ **Where occupancy is on, all three sit within about 15% of each other** and
  the table buys route quality rather than speed. ⚠️ The other lane recorded
  1.06x for the same crowded comparison and this measurement reads 0.90-1.03x;
  **both are inside this machine's noise and both say the same thing — a wash.**
  Do not quote either as a direction.
- ⛔ **The unweighted choice is what costs the table its crowded speed, and it is
  not removable.** Carrying `SearchWeight` across would let the incumbent test
  accept any chain within 30%, which every crowded chain is, and erase the
  dispersion occupancy exists to buy. The slower crowded search is the price of
  keeping that.

**Between the two table plans**, mean over the same runs:

| arm | CPD-Search | verify-then-search | |
|---|---|---|---|
| crowded | 12.40 / 13.40 / 11.92 µs | 11.04 / 11.35 / 11.12 µs | **0.85-0.93x — discarding WINS** |
| no occupancy | 1.08 / 1.13 / 1.09 µs | 1.29 / 1.26 / 1.35 µs | **1.12-1.24x — the incumbent wins** |

✅ **Each plan wins one regime, so `DecideByCrowd` takes both.** Four arms, one
interleaved harness, two repeats of the whole measurement:

| | master | CPD | verify | **DecideByCrowd** |
|---|---|---|---|---|
| crowded | 12.17 / 12.58 µs | 11.97 / 13.24 | 10.37 / 11.70 | **10.76 / 10.93 — 1.13-1.15x master** |
| no crowd | 8.15 / 8.00 µs | 1.13 / 1.05 | 1.33 / 1.25 | **1.13 / 1.07 — 7.19-7.50x master** |

**`DecideByCrowd` tracks whichever plan is ahead in each regime**, which is the whole
point of it. It costs no configuration: nothing is plumbed from `ServerOptions`,
and `--no-bot-occupancy`, `eval` and every training rollout reach `ChainBoundsSearch`
simply by never having a crowd.

⚠️ **Keyed on a crowd being PRESENT, not on occupancy being enabled.** The two
differ at the start of a round and after a respawn wave, when the map is empty
and `ChainBoundsSearch` is the better plan even on a server with occupancy on.

⛔ **Both gates earn their place only because both plans now ship.**
`BothPlansAgreeOnCost` stops a bot's route changing with the crowd for reasons
that have nothing to do with the crowd — either plan alone looks optimal in
isolation, so only a differential test can see a disagreement.
`AutoPicksThePlanThatMatchesTheCrowd` stops the selection being decorative: a
`DecideByCrowd` that always chose one plan would pass the first gate perfectly and lose
the situational win silently, and nothing else in the repo could see that.
**Confirmed by mutation** — pinning `DecideByCrowd` to `ChainBoundsSearch` fails it.

⚠️ **The mechanism, and it is the part worth keeping.** Maintaining the
incumbent costs a chain walk at *every expansion*; it repays that only when the
bound actually prunes. Under crowding it does not prune — pops are near-identical
between the arms (`ctf_waterway` 228 against 233, `ctf_truefort` 103 against
103) — because a ×1.25 hop perturbs the chain by so little that the bound sits
barely above the optimum. So the bookkeeping is close to pure overhead there.
Where the chain is **illegal** rather than merely dearer, the bound does prune
and the incumbent wins outright: `cp_egypt` 69 pops against 91, `ctf_truefort`
12 against 18.

⚠️ **The "clean" arm is occupancy-free, NOT chain-clean, and the difference is
what that column is really measuring.** The tables are built gates-open, so a
red asker's chain frequently crosses a gate only blue may pass; that is a +∞
perturbation, and it is why `cp_egypt` expands 69 nodes with no crowd at all.
On the graphs where the chain really is valid the pops are 1 against 0 — the
proposal's design returns without touching the heap — and it is level or
slightly ahead (`dkoth_atalia` 0.73x, `ctf_oldfort` 0.80x).

⚠️ **Absolute scale matters more than the ratios here.** The crowded case is
about **ten times** dearer than the clean one (≈12 µs against ≈1.1 µs), so
verify-then-search's ~1.2 µs saving under crowd is larger in absolute terms than
the incumbent's ~0.2 µs saving without it. **A ratio quoted without the base is
misleading in both directions.**

✅ **So neither is simply better, and neither had to be chosen.** `eval` and the
training rollouts are occupancy-free and get `Incumbent`; a live server with
occupancy on gets `VerifyThenSearch` whenever a crowd exists. The selection is
per query and free.

⛔ **Do not read this as a refutation of CPD-Search.** It is one workload, and
the workload is unusual in exactly the way that hurts an incumbent bound:
perturbations here are *many, small and uniform* (a whole team's routes each
×1.25) rather than *few and large*, which is the shape the technique is built
for. The finding is about this cost model, not about the algorithm.

✅ **`CpdSearchIsOptimalUnderLiveCosts` still checks the answers against a real
Dijkstra reference**, which is a stronger claim than cross-plan agreement — but
it exercises one plan, and both ship, so the differential gate is not redundant
with it.

### Phase 7 item 4s: the stab window, and the last unported script

`Gg2.Bots.BotStab`, the stab branch in `BotClassKeys.SpyKeys`. Gated by
`BotStabTests`. The reference is `botStabWindow.gml` and
`botClassKeys.gml:146-163`.

✅ **Every script in the bot fork's layer was ported**, as of phase 7's close.
⚠️ **That is history, not a live invariant.** `BotLayerCoverageTests` asserted it
and was deleted on 2026-09-01: the reference is stock GG2, which has no bot
layer, and this port's bots have diverged from the fork's. `docs/BOTS.md`.

- ⚠️ **The obvious test — "the enemy is within `BOT_STAB_RANGE` right now" — is
  aimed a full second early.** The press *freezes* the spy (`runPower`/
  `jumpStrength` to 0) and arms `alarm[1] = 32`, which creates the hitbox that
  lives `alarm[0] = 6` more ticks — so the damage window is **t+32 to t+38**,
  immobile throughout. Against anything moving, the naive press is close to a
  guaranteed miss and spends 38 frozen ticks in the open to make it.
- ⚠️ **The box is the sprite, not a radius.** `StabMaskS` is 64×64, origin
  (26, 37); its Step sets `image_xscale` from `direction` but never touches
  `image_angle`, so it is axis-aligned and merely **flipped**: 37px the way the
  spy faces, 26px behind, 37px up, 26px down. `BOT_STAB_RANGE` isn't the trigger
  — it is just the reach.
- ⚠️ **Facing is `aimDirection >= 90 and <= 270`, inclusive at BOTH ends**, so a
  spy aiming exactly straight up or straight down faces **left**. Either bound
  written exclusive sends the 37px reach the wrong way on a vertical aim.
- ⚠️ **The target is grown by its own bounds rather than treated as a point**,
  since the mask collides mask-to-mask — a target whose centre is outside the box
  can still overlap it by a shoulder.
- ⚠️ **The prediction is floored on the graph.** A target running off a ledge is
  exactly the case a straight-line prediction gets wrong, and it is common at
  knife range — over 32 ticks a falling body covers hundreds of pixels.
  `FloorUnder` plus the same `FallPredict` the aim solver uses; no node below
  means no floor.
- **`readyToStab` is tested in the class policy rather than inside the window**,
  because it is much the cheaper of the two and gates the press regardless of
  geometry.

⚠️ **One clause is UNOBSERVABLE, kept as a cost guard rather than a rule: the
`Cloak` test in the stab condition.** A stab and a shot are the **same key** —
the Revolver's `User Event 1` is an if/else on cloak, no separate melee input —
so an uncloaked spy taking the stab branch would press exactly what the shooting
branch presses. Deleting it by mutation leaves every test green; no fixture can
separate them. It buys skipping a 7-tick prediction and graph walk for a spy that
cannot use the answer. The reference's condition carries it too.

**Seven mutations tried and six bite**: testing where the target is now, the box
as a symmetric radius, facing exclusive at 90/270, the target as a point, no floor
under the target, and `readyToStab` unchecked. The seventh is the clause above.

⚠️ **`BotClassKeys.For` takes a `NavGraph?` now, for this one branch** — a
parameter rather than something on `WorldView`, because **the simulation must
not know the nav graph exists** (`WorldView` is a view over `WorldState`;
docs/BOTAI.md's whole point is that read-only inputs are passed *to* the tick
rather than held by it). ⚠️ **`botPickClass` still excludes the spy**, so no bot
on a live server is ever one and none of this runs there — ported anyway,
because declaring the layer complete with a hole in it would make the coverage
claim mean less than the work of closing it cost.

### Phase 7 item 4t: auditing docs/BOTAI.md against the shipped code

`Gg2.Tests.BotAiConstraintsTests`, and the four indexed loops it forced in
`SpawnRooms`, `KillBoxes`, `MoveBoxes` and `ControlPoints`.

Most of `docs/BOTAI.md` is design-intent prose no test can hold, but five items
are structural facts a single careless change destroys silently — three of them
marked impossible to retrofit. They are asserted now.

✅ **Item 1's `memcpy` half holds.** `WorldState` contains no reference anywhere
in it, recursively — including inside a struct inside an `[InlineArray]` slot
buffer, which is where one would be added without anybody noticing.
⚠️ **The failure mode this guards is not a broken test elsewhere:** putting a
`List<>` or a logger in there breaks nothing that runs today. The server still
serves, every parity window holds, the whole suite stays green — and rollout
search is dead, months later, for a reason nobody can find.

✅ **Item 2 is free, and now demonstrated rather than assumed.** A whole match is
**144,840 bytes** and round-trips through raw bytes bit-identically with no
production code at all — the bytes *are* the state.
⚠️ **Restoring is not the claim; continuing is.** The gate steps the restored copy
fifty ticks against a control that was never serialised and compares at the
**end**, because the failure worth catching is a state that compares equal and
then diverges — a cursor, a counter, an RNG stream left outside the struct.
⚠️ And the snapshot must be taken on a **condition**, not a tick count: a rocket's
whole life is shorter than its refire, so most instants after firing have an empty
pool, and a snapshot with nothing in the air never exercises the handles or the
creation order.

❌ **Item 1's other half was FALSE, and this is what the audit was for.** A
steady-state tick allocated **32 bytes per live character, every tick** — on a
twelve-bot server about 384 B/tick, 11.5 KB/s, a gen0 collection every few
minutes, and exactly what item 1 forbids. Bisected by state content rather than by
reading: an empty world allocated 0, players with no bodies 0, two live bodies 64.

⚠️ **The cause was `foreach` over an `IReadOnlyList<T>`.** Enumerating an
interface allocates a boxed enumerator; enumerating the concrete array does not.
`Cabinets.CollisionStep` took its list as `IReadOnlyList<HealingCabinet>` and
walked it once per live character per tick. **Three more sites had the same
pattern and were invisible only because no fixture map reaches them** —
`KillBoxes` and `MoveBoxes` (only `tdm_mantic` places any, and an *empty* array's
enumerator is a cached singleton, so it measures zero) and `ControlPoints`'
capture zones (no CTF map has one). All four are indexed loops now: an interface
call per element, no allocation, and these lists are a handful of rectangles.

⚠️ **`IReadOnlyList<T>` on a per-tick path is the trap, not `foreach`.** The rule
worth carrying: inside `Gg2.Simulation`, a collection that the tick walks must
arrive as an array or a span, or be indexed — and a `foreach` that measures zero
today may only be walking an empty list.

✅ **Item 4 holds and is asserted structurally**: every field of `PlayerInput` is a
byte, a ushort or an enum, so a bot cannot aim more precisely than a player can,
and `FromAim` quantises the way in onto the same 65,536 steps. ✅ **Item 6's
`WorldView` is a `ref struct`**, so the read-only guarantee is the compiler's
rather than a convention.

**Three mutations tried and all three bite**: a cabinet loop back to `foreach`, a
`string?` added to `CharacterState`, and a `double PreciseAim` added to
`PlayerInput`.

⚠️ **Four items remain unbuilt and are honestly marked so in the document**:
item 3 (recording human play with the parity recorder — the only item with a
deadline, since data not collected during the first public games does not exist
afterwards), item 8 (the socketless match runner, which is phase 8's), item 9
(`SIM_UUID` and refusing a mismatched policy), and item 10 (`Gg2.Env`).

### Phase 7 item 4u: SIM_UUID, and a surface that is chosen rather than swept

`Gg2.Simulation.SimulationStamp`. Gated by `SimulationStampTests`. The
specification is `docs/BOTAI.md` item 9; the idiom is the reference's own
`PROTOCOL_UUID`.

✅ **The simulation carries a version stamp**: `99b7ab3141e31fb4` over **213
entries**, and `Require` throws on a mismatch.

- ⚠️ **Refuse, do not warn.** A policy trained against physics that has since
  changed does not error and does not warn on its own — it plays slightly worse
  for ever and the cause is invisible. The reference refuses a client whose
  `PROTOCOL_UUID` does not match rather than letting it connect and desync, and
  this is the same thing one layer down. The exception names **both** stamps, or
  an operator cannot tell which side is stale.
- ⚠️ **The surface is chosen, not swept, and that is the design decision.** In:
  the physics constants, the `BOT_*` and `NAV_*` blocks, the per-class table
  (run power, hit points, double jump, cloak, collision box), the rocket's and
  mine's blast tables, and three hand-bumped version numbers. **Out:
  `GameConstants` — configuration and protocol.** A port number or a maximum name
  length moving must not invalidate every checkpoint; **a stamp that churns gets
  ignored.**
- ⚠️ **`SimulationBehaviourVersion` is the third entry, and it fills a real
  hole**: every other entry is a constant, so a code change to a line of physics
  cannot move the digest on its own. **Bump it when you knowingly change what the
  simulation does** — a trajectory, a damage number, a projectile path, a stage's
  position in `MatchSimulation`. ⚠️ **Exempt a change only when it is
  DEMONSTRATED unobservable** (two 90-iteration training runs, per-iteration
  numbers identical over 1.66M agent-steps, is the bar that was met once), not
  when the argument for it merely sounds good. A stamp bump invalidates every
  existing checkpoint — accept that cost in the ask, don't discover it after.
- ⚠️ **`ClassStats` is where the PHYSICS constants live** — `Gravity`,
  `BaseControl`, `BaseFriction`, `TerminalVSpeed`, `RestSpeed`,
  `RocketJumpGravity` — and not only the per-class table. The first draft omitted
  it from the constant sources, which would have made **a changed gravity
  invisible to the stamp**: the one thing it most needs to catch.
- ⚠️ **Round-trip (`"R"`) formatting, so a ONE-ULP change moves the stamp.**
  `Gml.PointSix` and its four siblings exist precisely because GM8 parses some
  decimals one ULP above the IEEE nearest, and those five *are* gravity and the
  friction factors. Printed to fifteen digits they are indistinguishable from the
  plain literals, and the stamp would miss the change the whole family is about —
  measured: `"G15"` fails two of the five tests.
- ⚠️ **FNV-1a, not `string.GetHashCode()`, which is randomised per process.** A
  stamp built on it changes every time the server restarts, so it would refuse
  every policy including one this build had just trained. This is a change
  detector rather than a security boundary, so a non-cryptographic hash is right
  and pulling `System.Security.Cryptography` into `Gg2.Simulation` would not be.
- ⚠️ **Reflection's field order is unspecified**, so the constants are sorted by
  name — a stamp that depended on it would move between runtimes for nothing.
  Measured: reversing the order fails the pinned test.

⚠️ **The digest is PINNED in the test, and a change there is not a broken test —
it is the bump.** The document's own expectation is "bumped whenever any of those
changes, which will be often and early, and every bump invalidates every
checkpoint. That is correct." Pinning does two things no self-comparison can: it
fails if the hash is not deterministic across runs or machines, and it makes any
contract change loud. **The question to answer before editing that line is which
checkpoints it just invalidated.**

⚠️ **A digest is the easiest thing in the world to gate uselessly.** "It equals
itself" passes against a stamp that covers nothing; "it is sixteen characters"
passes against a constant. So `Surface()` is public and the tests assert what the
stamp **covers**, entry by entry, and what it deliberately does not.

**Five mutations tried and all five bite**: the physics constants dropped from the
sources, unsorted reflection order, the per-class tables dropped, `Require`
warning instead of refusing, and fifteen-digit instead of round-trip formatting.

⚠️ **There is no policy loader to call `Require` from yet, and that is the point
of building it now.** The item's whole warning is that "a stamp added after the
first checkpoints are trained cannot tell you which ones were valid" — it was
meant to land with phase 4 and arrives at the end of phase 7. Nothing has been
trained, so nothing is invalid; the only cost paid is that the stamp had to be
derived from a larger surface than it would have been.

### Phase 7 item 4v: the scenario suite's cost is a gate now, not a table

`ScenarioSuiteTests.Baseline` and `BaselineRocketJumps`.

✅ **Every one of the fourteen legs and the rocket-jump count is unchanged**
across the medic's ally, the projectile reflexes, suppression, the arc gate, the
mine trigger, the close-in step, the stab window, occupancy and the per-tick
allocation fix — none of those items touch pathing, so this is checked rather
than assumed.

- ⚠️ **A leg's BUDGET is not a gate on its cost.** Every budget was set before the
  arcs were flown, so they are loose by design: a bot that lost its rocket jumps
  on `avanti-ramp-pocket-to-intel` would go back to the 311 ticks it took before
  item 4i, sit comfortably inside a budget that admits 311, and **go entirely
  unnoticed** — a 78% slowdown nothing goes red for. Measured by mutation: nudging
  `BOT_REPLAN_TICKS` by one moves five legs and every budget still passes.
- ⚠️ **The rocket-jump count is pinned at 6 rather than tested for presence.** The
  search offering *one* arc where it used to offer six is a regression a `> 0`
  test cannot see, and the six is quoted as fact in this file.
- ⚠️ **Exact, because `ARunIsReproducible` says it can be.** Every draw is seeded,
  the graph comes off disk, and the physics is IEEE doubles — so a tick count that
  moves at all is a behaviour change and not noise.
- ⚠️ **Both directions: a leg that got FASTER fails too.** A baseline nobody
  re-records stops describing the thing it names — the same reason
  `ScenarioSuiteTests.Unmet` is checked both ways.
- ⚠️ **A failure there is the re-record, not a broken test.** Check the change is
  one you meant, then that it went the way you meant, then update the row and say
  so in the commit — the discipline the pinned `SIM_UUID` asks for.

**Two mutations tried and both bite independently**: rocket jumps never offered
(the count), and `BOT_REPLAN_TICKS` nudged by one with the jump count unchanged
(the costs).

### Phase 7 item 4w: the end-to-end run, and a number that was wrong when it was written

`BotObjectiveTests.ABotWithNothingButAMapTakesTheEnemyIntel`, pinned.

⚠️ **The intel-run leg pins at 1,748 ticks**, not 1,747 — an earlier transcription
error sat uncorrected in three documents because the only gate was `grabbedAt <
3000` against a leg that takes 1,748, 71% slack.

- ⚠️ **A measurement quoted in three documents and gated by nothing does not get
  corrected by being read again.** It gets corrected by being asserted. This is
  the same hole `ScenarioSuiteTests` had one item earlier and the same fix: pin
  the ticks, the re-plans, the off-route fires and the blacklist fires **exactly**,
  and say in the message that a failure is the re-record.
- ⚠️ **Exact is safe here for the reason it is safe in the suite**: every draw is
  seeded, the graph comes off disk, and the physics is IEEE doubles. Four separate
  process runs across nine commits agree on every number.
- **The trade item 4i made is still visible in the pinned values**: against the
  rocket jump landing this read 1,780 / 41 / 0 / 10 / 2 and now reads
  1,748 / 49 / 0 / 19 / 1 — faster, with more re-plans and more off-route events,
  which is what flying arcs that sometimes miss costs.

**Three mutations tried and all three bite**: a follower constant nudged by one,
rocket jumps never offered, and the objective cadence nudged by one.

⚠️ **If a number is quoted as a measurement anywhere in this repo, the question
to ask is what would go red if it doubled** — and on this evidence the answer is
often "nothing".

✅ **The same pinning rule applies to the third end-to-end measurement**:
`PathFollowerFixtureTests`' longest steerable leg on `ctf_truefort` — 137 nodes,
current reading in item 4b-ii above. Its old bound, `ArrivedAt < 2400`, left
three of its five numbers asserted by nothing: nudging `BOT_REPLAN_TICKS` by one
moved the leg by 100+ ticks and 5 off-route events with the old bound passing
silently. All five numbers (ticks, re-plans, stuck, off-route, blacklists) are
pinned exactly now.

⚠️ **Zero blacklists is the load-bearing one of the five.** It read 9 before the
`NAV_EDGE_BUCKET` truncation was found, and a bot that starts banning edges again
still arrives — just slowly, inside any budget, with the route quality gone and
nothing red.

**The three end-to-end measurements in this lane — the scenario suite, the intel
run and the longest leg — are all pinned exactly now**, in both directions, each
saying in its own message that a failure is the re-record. Two of the three were
hiding something; the sweep is what established that the third was not.

### Item 12: the rollout-search bot, and the assumption it is built on

`Gg2.Bots.RolloutSearch`, built for `gg2-rl` phase 1. `docs/BOTAI.md` item 12 is
the requirement; this is what it cost and what to know before touching it.

**What it is.** On each decision it copies the `WorldState`, plays every
candidate action to a 20-tick horizon, scores each from `TickEvents.DamageDealt`
minus `DamageTaken`, and plays the first 4 ticks of the winner — then replans.
Nothing new was needed from the simulation: item 2's copyable state is the fork,
item 7's accumulators are the score, item 11's `ScenarioWorld` is the fight.

**Measured, Release, one searcher on `gg_debug` with the defaults:**

| | |
|---|---|
| candidates | 60 (5 aim bins x 2 fire x 3 horizontal x 2 jump) |
| simulated ticks per decision | 1,200 |
| cost per decision | **2.94 ms** — about 0.74 ms of a 33.3 ms tick, at one decision every four |
| against `ScriptedBot` tier 5, 12 duels | **11 wins, 0 losses, 1 draw** |

⚠️ **Name the build.** Debug is roughly twice that, and `CLAUDE.md` records what
a stale unattributed figure cost this project.

#### ⚠️ The opponent model is a decision, and it is written down

`docs/BOTAI.md` item 12 calls this "the one open design question" and says to
decide it explicitly, *"because leaving it implicit is how a search ends up with
a hidden opponent model nobody chose"*. The `RolloutOpponent` enum is that
decision made visible:

- **`HoldLastInput` (the default)** — every other character holds the input it is
  already holding, read straight off `CharacterState.KeyState` and
  `NetAimDirection`. No history, no allocation, no model: it is the wire's last
  word about them replayed forward. Right about momentum over 20 ticks, wrong
  about reactions, and wrong in the direction that assumes the opponent keeps
  doing what they were doing rather than stopping.
- **`Idle`** — presses nothing. Optimistic, and kept as the floor for a
  comparison.
- **Self-play is deliberately not in v0.** It is what item 12 points at as the
  answer needing no data, and it costs a nested search per candidate per tick.
  The seam is a third enum member and a branch in `OpponentInput`.

⚠️ **`Idle` is NOT dead code, and it is the member most likely to be deleted by
somebody tidying up.** `HoldLastInput` is the same *class* of assumption as
`botAimLead.gml`'s constant velocity — better, because it is right about momentum
over 20 ticks, and still an opponent who does not react. `gg2-rl` phase 1 will
therefore report the win rate under **both** models rather than one headline
number: if they barely differ the search is not really using its opponent model
and the horizon or the scoring is the binding constraint; if they differ a lot,
that gap is the quantified argument for paying for nested rollouts. `Idle` is the
control that makes the comparison possible, and the number is wanted before
phase 2 distils anything — a policy inherits whatever the search assumed.

⚠️ **Test it on the SCORE, not on the chosen action.** Two opponent models pick
the same action all the time; a first draft of `RolloutSearchTests` compared
actions in a position where nothing landed inside the horizon, so both models
returned the first candidate and the test passed for no reason.

#### ⚠️ A horizon shorter than a refire timer makes the search blind

The trap that cost this the most time, and it is a property rather than a bug.
When nothing can fire inside the horizon **every candidate scores exactly zero**,
the tie goes to the first candidate, and the bot stands still looking broken.

**A freshly spawned soldier's `refireTimer` starts at 29** and a scout is
nineteen ticks from its first shot — measured — so the first second of every life
is exactly that state. Two tests here were written inside that window and
asserted against a weapon that could not fire.

The honest fix is a shaping term (distance, facing, ammo), **not** a longer
horizon: 20 ticks is already 1,200 simulated ticks a decision, and doubling it
doubles the cost of every decision to cover a case that lasts a second.

#### Three properties that make it a baseline rather than merely a bot

Each has its own test, because each would fail silently:

1. ⚠️ **It draws nothing from the match's RNG.** That is automatic and it is why
   the fork is safe: `WorldState.Random` travels inside the copy, so a
   candidate's shots advance a stream that is thrown away. A search that rolled
   out in place would make every unseeded weapon's spread depend on how many bots
   were thinking — the one thing `docs/BOTAI.md` forbids the bot layer outright,
   and it would not crash.
2. ⚠️ **It leaves no aim behind.** `ActionDecoder` carries an *absolute* bearing
   and 60 candidates turn it 60 times, so `Search` saves and restores it. Without
   that the character comes out pointing wherever the last candidate looked —
   which is a bearing nobody chose, and the bot still moves and still fires.
3. **It is deterministic.** Candidate order is fixed and nothing is drawn to
   break ties. A baseline that answered differently twice would make every win
   rate measured against it meaningless.

⚠️ **`new RolloutOptions()` is not the primary constructor.** A record struct's
parameterless form is `default` — every field zero, none of the declared defaults
applied — so `default` and `new()` gave a zero horizon where
`new RolloutOptions(ConsiderSpecial: true)` gave 20. `WithDefaults()` is what
makes the two agree, and the first run of the tests died on it.

**It is also a parity fuzzer**, and that is worth more than it sounds: a search
tries what no human would, so it finds physics-port bugs by exploiting them.
⚠️ **An exploit is a finding for `docs/PARITY.md`, not something to penalise in
the score** — scoring around a bug hides it. `gg2-rl/docs/PLAN.md` phase 1 says
the same thing from the other side.

### ✅ Team Deathmatch had no goal — FIXED 2026-08-28, and the conga line was the wrong fear

**Found 2026-08-28**, by applying the Arena lesson ("a match that ends is not a
match that happened") to the other twenty-one maps rather than only to the one
that had just bitten.

`BotObjective.Decide` chooses between the intel, a generator and the control
points. **Team Deathmatch has none of the three**, so it returns `BotGoal.None`,
the path follower has no route, and a bot stands where it spawned until an enemy
walks into its combat range.

#### ✅ The fix: a fourth branch, and it is a SPOT rather than the enemy

`BotObjective.Deathmatch` — hunt the nearest enemy, preferring whatever the
combat half has already acquired, and stand at a **spot anchored on that enemy**
at the class's own band.

⚠️ **The spot is the whole difficulty, and "walk at the nearest enemy" on its own
does not work.** `BotClassKeys` refuses to fire inside
`BotTargeting.ClassMinBand` — "a rocket at that range kills the shooter" — and it
deliberately does **not** move the bot away, because *"where this bot stands is
the objective layer's decision rather than this one's"*. So a goal of the enemy's
own position walks a soldier to point blank and leaves it there, not firing:
measured in `Gg2.Env`'s duels as **250 decisions of walking, arrival 59px away
against a band of 110, and zero shots**.

The spot search fixes it and is machinery that already existed for the generator
and control-point standoffs: its band floor is the class's own minimum, and it
returns a **real walkable node**. ⚠️ **Offsetting the enemy's position by the band
was tried first and is worse** — the offset lands off whatever surface the target
is standing on and A* refuses it, so the bot stops dead with no route at all.

**Measured, `tdm_mantic`, 8 bots, 600 ticks, three seeds:**

| | before | after |
|---|---|---|
| bots that moved | 0 | **8** |
| bots that moved past 100px | 0 | **8** |
| furthest | 0 | **856 / 1,156 / 766 px** |
| deaths | 0 | **4 / 7 / 4** (13 / 16 / 13 over 1,500 ticks) |

#### ✅ Bots do not conga-line

"Walk at the nearest enemy" sounds like it produces eight bots in a line, but
does not: each bot picks its *own* nearest enemy and then a spot that prefers to
walk less and rewards height, so they distribute rather than stack.

Fraction of live same-team pairs within 64px, sampled every 50 ticks, on
`gg_debug` — the one-room map where piling up is easiest and where the original
bug hid:

| seed | before | after |
|---|---|---|
| 11 | 1.00 | **0.73** |
| 12 | 0.76 | **0.54** |
| 13 | 0.98 | **0.63** |

Deaths across those three seeds went **42 → 50** at the same time. The bots spread
out *and* killed each other more. `MatchActuallyHappensTests` gates both halves.

#### ✅ And `Gg2.Env`'s duels use the same branch

A duel runs on `ScenarioWorld`, a world with no gamemode at all, so it falls to
this branch like any TDM map. ⚠️ **`DuelEnv` had a hand-rolled `GoalLocked` chase
first and it is gone**: the objective layer is better at it in three ways — it
stands at the class's band instead of walking onto the target, it picks a real
walkable node, and its re-issue is throttled on the **resolved node** by
`BOT_GOAL_RETARGET_DIST` rather than by a distance test invented in the env.
Against a **stationary** opponent, 8 seeds: `arena_montane` **0 → 8 of 8** duels
ending in a death, `tdm_mantic` **0 → 5 of 8**.

#### ⬅️ Still open: a bot that STARTS inside its own minimum band does not leave

`gg_debug`'s two spawn points are **59px apart**, against a soldier's band of 110.
A bot spawned there acquires a target it cannot shoot and moves **41px in 250
decisions**, with a live 15-node route to a proper standoff spot it never walks.

⚠️ **The obvious cause is not the cause.** `ScriptedBot.ApplyHolds`'s engage hold
— "stand and fight instead of walking past" — has exceptions for carrying intel,
being near the goal, expiry and the pyro's close-in, and **none for "the target is
inside my own minimum band"**. Adding one, the exact mirror of the pyro's rule,
was tried and reverted: it changed *nothing* measurable — not the scenario
suite's exact tick costs, not the intel run, and not the case it was written for.
**Something else pins the bot**, not yet found. `gg_debug` idle-vs-scripted is
the repro.

| map | mode | what 8 bots do in 1,500 ticks |
|---|---|---|
| `gg_debug` | TeamDeathmatch | fight hard — 24 deaths, match decided at t949 |
| `tdm_mantic` | TeamDeathmatch | **move exactly 0 px, all eight, and 0 deaths in 3,000 ticks** |

⚠️ **The difference is the map, not the mode.** `gg_debug` is one room where
everybody spawns on top of everybody, so combat steering does all the moving and
the missing goal is invisible — which is why the TDM fixtures and
`TeamDeathmatchTests` look healthy. `tdm_mantic` is large and vertical, the bots
spawn scattered across levels, and nothing ever brings them together.

⚠️ **`eval`'s `tdm_mantic` result has therefore never measured anything**, the
same way its arena results did not, and for a related reason: the run completes,
so it reports.

**Pinned rather than fixed** (`MatchActuallyHappensTests`). Giving Team
Deathmatch a goal is a bot-layer design question — hunt the nearest enemy?
patrol? hold a chokepoint? — and `CLAUDE.md`'s rule for this layer is to improve
where the fix is straightforward and write down where it is not. This is not
straightforward: "walk at the nearest enemy" is the obvious answer and is also
how you get eight bots in a conga line.

**The gate that came with it** asserts the cheapest possible thing about a match
— *the bots move* — on one map per gamemode. Movement rather than kills, because
a kill needs two bots to find each other inside the budget and a stationary
roster is unambiguous. ⚠️ **That gate is the durable part of the arena
finding**, and it is worth more than either bug: this repo had 2,240 tests and
not one of them asked whether a match had anything in it.

### ⚠️ The sticky jump's envelope is 194 px, not 310 — measured before the arm was built

`gg2-nav-gen` ships a sticky-jump edge fan (6,497 edges across 24 graphs,
~1,600 node-openings) built from a derived, not measured, constant:
**`NAV_SJ_IMPULSE = 10`**. The rocket jump's own derived number promised 276 px
and delivered 206 while every offline check stayed green and the follower
blacklisted the edges — the same failure mode was worth ruling out here before
trusting the fan.

✅ **It did not need the live game.** This port's mine and explosion physics are
measured exact against the engine (phase 5, nine oracles), so the manoeuvre can
be flown here and read straight off.

**The manoeuvre**: place a mine straight down, wait for it to settle, then
**jump and detonate on ONE tick** — `Character.events/Begin Step.xml` runs its
jump branch (line 108) before its right-click branch (line 137), so
`vspeed = -jumpStrength` is already in place when the blast's `motion_add` lands
on top of it. Detonating a tick later wastes the jump.

| | generator's derivation | measured here |
|---|---|---|
| launch | 18.3 px/tick (`NAV_JUMP_V0 + NAV_SJ_IMPULSE`) | **-14.460** |
| unobstructed apex | ~310 px | **193.6 px** |
| self-damage | 25 (`NAV_SJ_DAMAGE`) | **25** ✅ |

⚠️ **The envelope is about 60% too generous**, so some fraction of those 6,497
edges are arcs a demoman cannot fly. The damage constant is right, which is worth
saying so that "the generator's numbers are wrong" is not read more broadly than
it should be: one of the two is correct.

⚠️ **The launch is identical on four maps to three decimals** — `ctf_truefort`,
`koth_valley`, `ctf_2dfort`, `gg_debug`. A mine fired straight down settles at
the demoman's feet, so the geometry that sets the impulse is the character's own
and not the map's. That is what makes one number the answer rather than a sample.

⚠️ **The apex is computed from the launch, not measured off a map**, and that is
not a shortcut: every map tried has a ceiling or a slope inside 200 px of a
spawn, and the measured rise there is 20.9 to 68.8 px — geometry, not the
manoeuvre. `MineJumpTests` pins the launch exactly, because a generator is
costing arcs against it: if it moves, every type-7 edge needs regenerating.

⚠️ **And the probe's first draft measured nothing at all**, because it placed the
demoman at a hand-picked (500, 850) on `gg_debug` and put him inside terrain,
where he sat at terminal velocity going nowhere. `PlaceAtSpawn` is what fixed it.
**The same lesson as `docs/ENV.md`'s sampled placement, on the same day, in a
second place** — a coordinate that looks reasonable is not a coordinate that is
free.

### The sticky-jump follower arm, and the one thing it does differently

`NavEdgeType.StickyJump` was the last unhandled edge kind; it now steers.

**The pieces**: `NavQuery.CanStickyJump` and a refusal in `NavSearch` beside the
double-jump and rocket-jump ones; `StickyModel` for the launch, the readiness
test and the reach; a two-phase arm in `PathFollower.SteerJump`;
`BotPathState.MineFire` / `MineDetonate` / `StickySetup`; and the hand-off in
`ScriptedBot`, because the follower owns LEFT/RIGHT/JUMP/DOWN and cannot fire.

#### ⚠️ It is the only edge kind that does not leave on the tick its gate opens

Every other launch is one press. A rocket jump is *one tick* —
`Character.events/Begin Step.xml` runs the attack branch before the jump branch
and the rocket detonates in the same step. **A mine has to be placed, and has to
settle.** So the gate opening starts a `NAV_SJ_SETUP_TICKS` countdown and the
launch happens five ticks later:

1. **Place** — `MineFire`, aimed straight down, and *no jump*.
2. **Wait** — five ticks, standing on the takeoff column, placing nothing else.
3. **Jump and detonate on ONE tick** — `MineDetonate` is `Special`, not `Attack`
   (`Attack` would place a second mine), and it rides on the same tick as the
   follower's `JUMP` press.

⚠️ **Step 3's ordering is the manoeuvre, not an optimisation.** Begin Step's jump
branch (line 108) runs before its right-click branch (line 137), so `vspeed =
-jumpStrength` is already in place when the blast's `motion_add` lands on top of
it. Detonating a tick later throws the jump away and the arc is an ordinary hop.

⚠️ **Decrement then test, not test then decrement.** The obvious loop waits one
tick longer than `NAV_SJ_SETUP_TICKS`, and an arc costed for five ticks and flown
after six is a different arc. Caught by the test, one line in the arm.

⚠️ **A gate that closes mid-countdown abandons** rather than standing on a live
mine waiting for a window that has moved. The off-route machinery then gives the
edge one retry before blacklisting it, which is the right policy for a transient.

#### What is shared, deliberately

`StickyModel.Reach` **delegates to `RocketModel.Reach`** rather than copying it. A
mine jump flies under `moveStatus` 1 exactly as a rocket jump does — control
0.65, friction 1 — and the one thing a rocket has that a mine does not
(`NAV_RJ_SPEEDUP`) is not used by that integration. ⚠️ **`gg2-nav-gen`'s
`canFly` veto uses the same law** (`flyDistanceRocket`, moveStatus 1), so the
generator and the follower agree about which arcs exist; that agreement is worth
keeping deliberately rather than by coincidence.

#### ⚠️ Flying real edges found a bug every synthetic test was blind to

`gg2-nav-gen` built a sticky cache (`build/botnav-sticky`, 4,285 type-7 edges) and
48 real edges were flown across four maps. **Zero sticky jumps were taken.**

**The cause: placing the mine puts the launcher on its refire cooldown.**
`StickyModel.Ready` requires `ReadyToShoot`, the gate re-evaluates every tick, so
it closed on the tick *after* the placement — and the abandon branch then threw
the setup away. Every attempt, on every map.

⚠️ **The synthetic tests could not see it because their fixture weapon is
permanently ready.** `Loaded()` sets `ReadyToShoot` once and nothing ever spends
it, so the one state that breaks the manoeuvre never occurs in them.
**A fixture that cannot enter the failing state is a test that cannot fail**, and
this is the third time today the same shape has cost something — a claim that was
self-consistent and untested against the thing it described.

The fix is one clause: readiness is required to **start** the manoeuvre and is
irrelevant once the mine is on the ground.
`TheCountdownSurvivesTheLauncherGoingColdAfterThePlacement` hands the follower a
weapon that goes cold the moment the mine is placed, and goes red without it.

#### ✅ The sticky arm was never called for a sticky edge

⚠️ **The cause is `PathFollower.TryFindEdge`, and it is not a sticky-jump bug.**
A route is a list of **nodes**, so the follower re-derives which edge each hop
meant — and it took the **first** the edge index yielded. `NavSearch` refuses per
**edge**. A sticky cache carries a rocket jump beside the sticky jump on **2,460
node pairs** and a double jump beside one on **1,124 more**, and the rocket jump
is what the index yields first. A demoman carries a Minegun, so
`RocketModel.Ready` was false, the gate never opened, and the bot stood on the
takeoff column waiting for a launcher it does not have. **`StickyModel.Ready` was
never consulted once** — every symptom that looked like a sticky-model refusal
(reach, `need`, the gate arithmetic, `SnapObjective`, the flight harness's start
position, the dispatch switch) was consistent with the arm because none of them
was ever exercised; each had been computed from the sticky edge by hand rather
than read out of the follower.

✅ **After the fix, 4 of 4 real sticky edges on `ctf_2dfort` are flown and
arrive** (was 1 of 4 — the one where no parallel edge existed). Reproduce with a
demoman driven over `gg2-nav-gen`'s `build/botnav-sticky`; that cache is another
lane's build output and is not gated here, which is why the checked-in gate is
`PathFollowerTests.ADemomanOnAPairThatAlsoCarriesARocketJumpStillPlacesItsMine`
— the same graph shape, synthetic, and it fails without the fix.

⚠️ **The other three maps in that flight still take no sticky jump, and that is
the harness rather than the arm**: `koth_harvest` arrives 4 of 4 by walking
because A* costs a jump the same as anything else. **A flight harness that does
not check its own start against the edge it means to fly measures a different
edge.**

⚠️ **The takeoff column resolves to its own from-node 18,396 times out of
18,396** on the shipped graphs, and 4,132 of 4,132 on the sticky one
(`EdgeFlightTests.EveryArcEdgeResolvesFromItsOwnNodeAndFromItsTakeoffColumn`) —
so a "the column doesn't resolve to the from-node" theory for any remaining
flight anomaly is ruled out; look elsewhere.

### ✅ The flight harness — `EdgeFlights`, and the honest number

`src/Gg2.Bots/EdgeFlights.cs` is a sweep harness, in the library beside
`ScenarioWorld` rather than in a test, because the lane that generates a cache
is the one that wants to sweep it:

```csharp
EdgeFlights.Sweep(() => ScenarioWorld.Load(map, area, maps, nav), NavEdgeType.StickyJump);
```

It puts one bot on an edge's from-node, sends it to the to-node, and reports one
of seven outcomes. **Five of them are SKIPS and two are results**, and keeping
those apart is the entire value:

| outcome | what it means | on today's caches |
|---|---|---|
| `StartUnresolved` | the from-node's midpoint is not the from-node | **0**, and provably so |
| `GoalUnresolved` | the to-node's midpoint does not snap back | **0** |
| `NoRoute` | A* finds no way at all | 18 sticky, 9 rocket |
| `RoutedAround` | a route exists and it is **not this hop** | **2,167 of 4,132 sticky** |
| `Shadowed` | the hop resolves to a cheaper parallel edge | **0** |
| `Arrived` / `Stranded` | the two real answers | |

⚠️ **`RoutedAround` is the dominant outcome and it is what made the hand-rolled
number meaningless.** A* costs a jump the same as a walk, so it goes round
whenever going round is shorter — 47 of `gg_debug`'s 67 rocket jumps, 2,167 of
the sticky cache's 4,132 arcs. Driven anyway, each of those is a bot walking
somewhere while a counter says the arc was attempted. **Mutation-checked**:
delete the check and `gg_debug` reports **67 arrivals out of 67 against 12 real
flights**.

⚠️ **Arriving is not flying, and the gap is a third.** `EdgeFlight.Manoeuvres`
counts what the takeoff gate *asked* for; `Flown` is an arrival with at least one
of them. `gg_debug`: 21 arrivals, 12 flights. `ctf_truefort`: 377 arrivals, 219
flights. ⚠️ **The `ctf_truefort` pair read 328 and 214 when this was written and
nothing went red when it stopped** — no gate flew a real map, so the doc drifted
while the code moved under it (re-measured 2026-09-05, no tables; with the tables
it is 290 and 209, a different arm). That is what the corpus gate below exists to
stop. The other arrivals are node pairs the bot could simply walk between, and
a gate on arrivals alone stays green with the arm deleted.

⚠️ **`Manoeuvres` does not count shots fired.** Measured by ripping the ATTACK
half out of `PathFollower.ApplyLaunch`: `gg_debug` went 20 arrivals / 12 flights
→ **16 / 8**, not to zero. The *arrival* count is the half that collapses, which
is why both are pinned.

#### The number item C existed to produce

**Every sticky arc `gg2-nav-gen` emits, flown**: 24 graphs, 4,132 arcs, 52.7s.

| | |
|---|---|
| candidates | 4,132 |
| posable as a route | **1,947** |
| arrived | 683 |
| **flown under the mine jump** | **508** |
| stranded | 1,264 |
| blacklist fires | 8,219 |

That is the honest replacement for *"48 real edges flown across four maps, zero
sticky jumps taken"*. ⚠️ **It is not checked in**, because `build/botnav-sticky`
is another lane's output behind a flag; the checked-in gates run the same harness
over `nav/`, where a rocket jump and an air jump are the arcs that exist.

⚠️ **Two findings in it belong to `gg2-nav-gen` rather than here.**

- **8,219 blacklist fires across 1,947 measured flights** — about 4.2 per flight,
  against **zero** over `PathFollowerFixtureTests`' 137-node walking leg and
  **two** over `gg_debug`'s twenty rocket jumps. Most of that is the 1,264
  stranded arcs burning their budget, but the ratio says the cache's arcs are
  substantially not flyable by this follower rather than marginally so.
- **Two maps fly exactly zero**: `arena_lumberyard` (24 posed, 9 arrived, 0
  flown) and `dkoth_atalia` (26 posed, 6 arrived, 0 flown). Every arrival on both
  is a walk. Those are the two to look at first.

#### ⚠️ The harness must press what a real bot presses

`PathFollower.ApplyLaunch` was extracted out of `ScriptedBot` for this, and the
extraction is the point rather than tidiness. The follower owns LEFT / RIGHT /
JUMP / DOWN and **cannot fire**; a rocket jump needs one key from the combat half
and a mine jump needs two, on two different ticks. **A driver that keeps only the
movement keys gets a follower that requests a rocket and never fires it, and
every arc reads as refused** — which is exactly the failure mode this whole item
came out of. There is now one copy, and `EdgeFlights.Pilot` and `ScriptedBot`
both call it.

Likewise `PathFollower.TryResolveHop` is `TryFindEdge` in public, so the harness
cannot answer "which edge is this hop" a second way. The tie-break it implements
— strictly cheapest, first-in-order on ties, resolved against
`BotPathState.PlanQuery` and not the live query — is precisely what item A was.

#### ✅ What the flights did buy: 333 arcs that leaned on a run-up that cannot exist

Independently of the open question above, the flights found a real bug in the
generator, and the route to it is the interesting part: **`gg2-nav-gen` went
looking for reasons my refusals might be legitimate, and found one that applied
to different edges.**

The arm **stands still on the mine for the whole setup**, so a walked-in run-up
is speed the follower can never have at takeoff — but the fan's veto credited one,
copied from the rocket fan where no stand-still exists. Auditing all 4,285 edges:
**333 (7.8%) leaned on that credit** and fail a standing-start reach test. None of
the sixteen refusals is among them, so it explains nothing about them — but every
one of the 333 was a guaranteed blacklist for this port.

The veto is now `v0 = 0`, the cache is 4,132 edges, and **access is unchanged at
1,121 node-openings** — the removed arcs were shortcuts, not access, measured
rather than assumed.

⚠️ **The mirror of this repo's fixture lesson.** Here a fixture that could not go
cold was a test that could not fail; there a run-up credit copied between fans was
an assumption **every offline checker shared**, so no check could catch it. Both
are the same shape: the thing that would have failed was not representable in the
apparatus.

### ✅ The corpus sweep: every edge of every shipped graph, and it is a GATE (2026-09-05)

`EdgeFlights.SweepAll` flies every edge of every graph in a nav directory;
`EdgeFlightTests.EveryEdgeOfEveryShippedGraphIsFlownOnTheShippingArm` pins its
tally and `BENCH_FLIGHT` prints its detail. **One implementation, two callers**,
because a number a gate pins and a bench reports must not be computed twice.

⚠️ **The cost claim this replaces was wrong by two orders of magnitude.**
`Sweep`'s own docstring said "a whole cache is minutes rather than seconds" and
`EdgeFlightTests` said `ctf_truefort`'s 878 rocket jumps were "eight seconds,
which is a measurement rather than a gate". Measured 2026-09-05, Release, warmed:
**that map is 102 ms and the entire 25-graph corpus is 5.0 s serial, 3.1 s across
24 cores** — `ScenarioResources` caches the decoded map and the graph, so a flight
is a collision world plus ~55 ticks. Both comments predated that cache and neither
had been re-run. **An affordability claim ages exactly as badly as a measurement,
and nothing goes red when it does.**

#### The corpus, both arms (budget 400, `Team.Red`, all six edge kinds)

| | tables ON (**what a server runs**) | no tables (what every other gate runs) |
|---|---|---|
| candidates | 116,316 | 116,316 |
| **measured** | **32,055** | **37,787** |
| arrived | 31,352 | 37,044 |
| flown | 2,403 | 3,380 |
| **stranded** | **703** | **743** |
| routed around | 72,926 | 67,194 |
| shadowed | 10,869 | 10,869 |
| no route | 466 | 466 |
| team swapped | 178 | 206 |
| simulated ticks | 1,556,749 | 2,029,939 |

⚠️ **These are the numbers AFTER the three harness faults below were fixed.** The
first sweep read 970 and 1,022 stranded; 267 of those were the harness and not
the edge. Re-recorded 2026-09-05 and again 2026-09-06.

⛔ **`flown` is 1,686 against 31,352 arrivals, and the third clause is why.**
`EdgeFlight.Flown` requires an arrival, a manoeuvre, **and that the bot actually
steered this edge** — see "the arrival that flew a different edge" below. Adding
that last clause took `flown` 2,403 → 1,686 with `arrived` and `stranded`
unmoved.

⛔ **`measured` is 28% of `candidates`, and that is the ceiling rather than a
defect.** A\* costs a jump the same as a walk, so it prefers to route around
72,806 of these edges — and an edge A\* routes around **cannot be posed as a
one-hop question at all**. **A sweep of every edge is not a check of every edge.**
The complement is `BENCH_FLIGHT_ROUTES`, which flies whole routes and so exercises
edges in the context they are actually used in.

⛔ **The two columns are not two runs of one measurement.** The exact heuristic is
a different arm (`CLAUDE.md`, `--no-nav-distances`), and here is what that costs
in coverage: it routes around **5,741 more** single hops, and **measurable rocket
jumps fall 5,659 → 3,537 — a 37% loss over the one edge kind whose arm nothing
else exercises.** `shadowed` and `no route` are identical across the arms, which
is the control: neither depends on the heuristic.

✅ **This is the FIRST gate in the repository that runs with the tables on.** The
three pinned bot measurements and the 25 digests still run without them — see "The
gates measure a DIFFERENT ARM", which is deliberately unchanged. A new gate gets to
start on the right arm; an existing pin moving is a decision about what its alarm
means.

#### What it found, and the two numbers to act on

| kind | measured | stranded @400 | | stranded @4000 | |
|---|---|---|---|---|---|
| Walk | 14,832 | 40 | 0.3% | 28 | 0.2% |
| Fall | 6,136 | 74 | 1.2% | 62 | 1.0% |
| Jump | 6,829 | 143 | 2.1% | 97 | 1.4% |
| DropThrough | 8 | 0 | — | 0 | — |
| **DoubleJump** | 714 | **77** | **10.8%** | **47** | **6.6%** |
| **RocketJump** | 3,536 | **369** | **10.4%** | **195** | **5.5%** |
| **all** | **32,055** | **703** | **2.2%** | **429** | **1.3%** |

The two arc kinds are where the failures live, by a factor of five over walking.
`BENCH_FLIGHT_CSV` writes one row per stranded edge — map, area, edge index, kind,
from, to, ticks, miss — which is the half a tally cannot carry.

⚠️ **A stranding is relative to its budget and the two are quoted together or
neither is.** 400 → 1,600 ticks takes 703 to 453, and 1,600 → 4,000 takes it to
**429** — so **274 of the 703 were slow rather than impossible**, and 429 is where
it stops falling. ⚠️ Blacklist fires go the other way and violently: 5,140 at 400,
76,177 at 1,600, 297,166 at 4,000, because a stranded bot keeps banning edges for
as long as you let it. **The budget that measures followability is not the budget
that measures cost.**

`cp_dirtbowl` holds 131 of the 429 across its three areas — nearly a third of every
hard failure on the corpus, and the place to start looking.

#### ✅ Mutation-checked, and note which half moves

Ripping the ATTACK half out of `PathFollower.ApplyLaunch` — the documented
mutation — moves the shipping-arm gate:

| | intact | ATTACK removed |
|---|---|---|
| measured | 32,048 | 32,048 |
| arrived | 31,078 | **29,086** |
| flown | 2,355 | **828** |
| stranded | 970 | **2,962** |

⚠️ **`measured` does not move, and that is structural rather than luck**: it is
decided at planning time, before a key is pressed. So a change that breaks the
*flying* and leaves the *routing* alone shows up in three of the twelve pinned
numbers and not in the others — which is the point of pinning the whole tally
rather than a headline.

#### ⚠️⚠️ Two of the harness's own faults were inside the first stranding count

**267 of the first sweep's 970 strandings were the harness.** Both were found by
re-flying every stranded edge with the per-tick state recorded, and both are the
same shape: **the harness put a body somewhere the game never puts one, and then
reported what the body could not do there.**

**1. The start was one pixel inside the floor (123 flights).**
`NavGraph.StandY` is `FloorY - 23` and a character's box bottom is `+23`, both
inclusive — so a character placed at `StandY` has its last row on the floor's
first solid row. ⛔ **7,541 of the 11,327 nodes on the shipped graphs (66.6%) fail
`PlaceFreeInMask` at `StandY`, and every single one is free exactly one pixel
up** — nothing is still blocked, on any map. An overlapping character **cannot
move at all**: every candidate step keeps the overlap, `good_move_contact_solid`
refuses each one, and gravity has nothing to resolve. Those flights stood still
for the whole budget.

✅ Dropping a soldier onto a node and letting it settle rests it at
**`StandY - 0.55`**, so one pixel up is also where the simulation actually puts a
body. `EdgeFlights.StandingY` is that pixel; `EveryShippedNodeCanHoldACharacter`
is the census, and it pins **both** halves — the 7,541 overlaps and the zero
still-blocked — so a graph that starts emitting nodes no body can occupy says so
here rather than as drift in a stranding count.

⚠️ **`StandY` is not wrong; it is a GOAL.** `navSetGoalNode` is where it comes
from and a goal is a coordinate, not a body. The harness was the thing treating
it as a place to stand.

**2. Every edge was flown as Red, and 148 of them start inside blue's gate.**
A team gate is solid to the other team, so a node inside one is a place that team
can never be. Measured: those 148 flights had `from=TeamBlue to=None edge=None`
on **every single one**, a red bot could not move a pixel in either direction for
the whole budget, and **all 148 are free for a blue bot** — which walks off them
in both directions. `EdgeFlights.Fly` now flies an edge as a team that can
actually stand on it, records which (`EdgeFlight.TeamSwapped`, 178 on the
shipping arm) and keeps `StartBlocked` for a start **neither** team can occupy,
which no shipped graph currently produces.

⚠️ **This one also moved `no-route`, 618 → 466**, because a bot that can be on
the node can also be routed off it. A premise that is wrong does not stay inside
the outcome it is a premise for.

⛔ **Note what neither fault touched.** `Measured` moved by 7 and `Shadowed` by
25; the sweep's *coverage* is decided by the graph and the plan, not by the body,
so the before and after are comparable as measurements of the same thing. That is
exactly what is NOT true of the two routing arms, and it is why one pair of
numbers may be diffed and the other may not.

#### ⚠️⚠️ The third fault: the arrival that flew a DIFFERENT EDGE

**2,099 of the corpus's 31,352 arrivals never steered the edge they were credited
to** — 6.7%, and among arc edges alone it is 19.8%.

The premises are checked before the bot moves: the plan IS the single hop, and
`TryResolveHop` yields this edge. ⛔ **But the follower re-plans while it flies.**
An off-route event or a stuck timer replaces that one hop with a multi-hop walk
to the same node, the bot walks there, and the arc is credited with an arrival it
had no part in. One `ctf_orange` rocket-jump flight posed as 63→36 went off route,
re-planned, and flew **59→36** instead; `cp_dirtbowl`'s 626→563 walked
`626→621→586→569→566`.

⛔⛔ **THE CAUSE NAMED IN THIS SECTION WAS WRONG, and the counter it added is
what eventually caught it.** "The follower re-plans while it flies" describes the
symptom correctly and the mechanism not at all: measured 2026-09-06, in-air
abandonment is **0.0–1.1 per arc over six attempts in every bucket, highest in the
arcs that succeed**. What actually re-planned the route was the launcher's refire
flag being asked as a routing question — see "A cold launcher unplanned the route"
below, where fixing it takes `Diverted` from **2,128 to 33** with nothing else
changed. Kept rather than rewritten because the counter and the harness rule are
still right; only the diagnosis beside them was not.

⛔ **This is `RoutedAround`'s shape arriving a second time, from inside the flight
rather than from the plan** — which is exactly why it could not live with the
other premises, and why nothing caught it for a day. `EdgeFlight.RanEdge` counts
the ticks the follower spent steering the posed edge; `Flown` now requires it and
`EdgeFlightTally.Diverted` counts the arrivals that fail it.

⚠️ **Rocket jumps carry nearly all of it** — `flown` went 1,784 → 1,067 for that
kind alone, against 619 → 619 for the air jump. A rocket jump is the arc the
follower is most likely to give up on and walk around, and it was the arc whose
success was most overstated.

✅ **`gg_debug`'s twelve flown rocket jumps are untouched**, so the small-map pins
in `EdgeFlightTests` did not move — the fault lives in the long routes of real
maps, which is where nothing had been pinned.

#### What the 429 hard failures are, mechanically

Every stranded flight re-flown with per-tick state, at the 400-tick budget
(703 flights). The classes are what a stranding actually *is*:

| | count | what it means |
|---|---|---|
| never got near the goal | 181 | the arc or the route genuinely fails |
| reached the goal COLUMN, never the node | 368 | undershot — usually under the target ledge |
| reached the node, then left it and fell | 75 | **overshoot**: the arrival brake ran out of ledge |
| reached the node, not credited | 68 | mostly budget — arriving on the last few ticks |
| never moved a pixel | 4 | was 166 before the two fixes above |
| died en route | 7 | a rocket jump costs 30 hp and these took it three times |

⚠️ **"Reached the node, then fell off it" is a real follower defect, not a
harness one.** `WalkToGoal` brakes inside the tolerance band rather than coasting,
but braking is not instant: on `dkoth_atalia`'s edge 415 the bot crossed the goal
at 3.14 px/tick, decelerated to 1.07 — one tick above the `|hSpeed| < 1` the
arrival test wants — and by then it was off the ledge and fell 456 px into a pit
it could not climb out of. **A goal node within about two pixels of a surface's
edge can be overshot by the brake itself.** It is bounded and it is real: a bot
sent to a ledge-edge node in a match does the same thing.

⚠️ **A rocket-jump flight can kill the bot.** Each attempt costs 30 hp and the
follower will retry; seven flights ended with a dead bot. `EdgeFlights` does not
check aliveness — a dead bot simply stops arriving — so those read as strandings
rather than as anything more specific.

#### The sweep is parallel, and that is what makes it a gate

Serial in Debug it is 55 s (tables) and 68 s (no tables); across cores the whole
`EdgeFlightTests` class runs in **12 s**. A flight owns its world, its follower and
its state, reads a graph and a map nothing writes, and draws from no shared stream,
so the work is embarrassingly parallel — and the only shared thing it touches,
`ScenarioResources.Shared`, is a `ConcurrentDictionary` over entries nobody writes.
⛔ **`TheSweepIsTheSameHoweverManyThreads` gates that flight-for-flight rather than
on the tally**, because two orderings that differ can produce identical counts. The
moment the answer depends on the thread count, every number above becomes a
property of the machine that recorded it.

### Attributing the 429: what was ruled out, and the one bound that holds

**The question:** a stranding says the follower did not fly the edge. It does not
say the edge can be flown. Which of the 429 are the follower's fault and which are
arcs the generator should not have emitted?

⚠️ **Not settled. Here is what is, so nobody re-runs the parts that failed.**

#### ⛔ RULED OUT: the run-up that cannot exist. It does not repeat here.

The obvious hypothesis, and the one with form — "What the flights did buy" above
records 333 sticky arcs that leaned on a run-up credit the arm could never have.
So: does an arc's recorded takeoff speed (`NAV_EDGE_BUCKET`) exceed what a body
can actually be doing as it crosses the takeoff column?

Measured by simulation rather than by arithmetic — walk backwards from the takeoff
column as far as the floor goes, then run forwards and read the speed on the way
past (`BENCH_FLIGHT` has no mode for this; it was a one-off, and the method is
written here because the result is a negative):

| kind | arm | n | short of the takeoff speed | median need | median achievable |
|---|---|---|---|---|---|
| Jump | arrived | 6,732 | 49.9% | 2.79 | 2.24 |
| Jump | **stranded** | 97 | **48.5%** | 2.48 | 2.24 |
| DoubleJump | arrived | 667 | 48.4% | 2.65 | 2.79 |
| DoubleJump | **stranded** | 47 | **44.7%** | 4.01 | 3.49 |
| RocketJump | arrived | 3,341 | 56.4% | 3.12 | 2.24 |
| RocketJump | **stranded** | 195 | **60.0%** | 3.81 | 2.24 |

⛔ **Half of every arc is "short", including the ones the follower flies, and the
two arms are within a few points of each other.** So `BUCKET` is not a speed the
ground has to supply and a shortfall against it predicts nothing. **Do not
re-derive this.** The control is what killed it: run only the stranded column and
"48% of failing arcs demand more speed than the node affords" reads as a finding.

#### ✅ ESTABLISHED: the failures sit at the margin of what the generator emits

Joining each arc's recorded parameters to its outcome (`BENCH_FLIGHT_CSV` writes
the arrivals too, which is what makes this possible):

| kind | base failure | top decile by takeoff speed | top decile by flight time |
|---|---|---|---|
| Jump | 1.4% | 1.0% | 1.4% |
| DoubleJump | 6.6% | 9.7% | 7.6% |
| RocketJump | 5.5% | **11.2%** | **16.1%** |

A rocket jump in the longest tenth by flight time fails **three times** as often as
one in the middle, and median stranded takeoff speed is 3.8 against 3.1 for
arrivals (4.0 against 2.6 for the air jump). ⚠️ **It is a margin effect and not a
mechanism** — the run-up table above is what rules out the obvious mechanism for
it.

#### ✅ ESTABLISHED: about a third of the strandings are the APPROACH, not the arc

`BENCH_FLIGHT_ORACLE` runs two instruments over the same two arms, and **the
control arm is arcs the follower demonstrably FLEW** (`Flown`, all three clauses)
rather than merely arrived at — an "arrived" control includes the diverted
arrivals above and asks the instrument to fly arcs nobody flew.

| instrument | stranded (n=339) | flown control (n=400) |
|---|---|---|
| open-loop input search | 76 (**22.4%**) | 134 (**33.5%** — its recall) |
| **the real follower, put ON the takeoff column** | 109 (**32.2%**) | 354 (**88.5%** — its recall) |

⛔ **Only the second instrument's negatives mean anything**, and that is entirely
because of its recall. The open-loop grid misses two thirds of arcs the follower
actually flies, so its "not flyable" remainder is noise; the assisted probe misses
one in nine.

✅ **The result: 109 of 339 stranded arcs (32.2%) are flown by the same follower
when it is simply placed on the takeoff column.** Correcting for the 88.5% recall
puts it near **36%**. So about a third of the residual is the **approach** — the
bot cannot get itself to the takeoff column in a fit state — and roughly 218 arcs
are ones this follower cannot fly even from a perfect start.

⚠️ **"Cannot fly from a perfect start" is still not "the arc is impossible."** It
bounds the follower's reach, not the generator's honesty; the open-loop search
found 76 of the strandings flyable by *some* input sequence, and 33 of those are
arcs the assisted probe misses. **The generator's share is bounded above by ~218
and has never been measured directly.**

⚠️ **Two candidate axes turned out to matter more than the parameters they sweep.**
Dropping the tick-0 jump on the theory that a body one pixel above the floor is
airborne and cannot jump cost 16 of 118 control hits — the physics **queues** a
jump pressed in the air (`CharacterState.QueueJump`) and fires it on landing, so
it is a real technique rather than a wasted press. And the first control used
`Arrived` instead of `Flown`, which *flattered* the instrument by 5 points.
**Both were caught by the control moving in the direction that made the instrument
look worse**, which is the only direction worth trusting.

### The 109 approach failures: a narrow takeoff window, and a repair that is refuted

The actionable half of the attribution above — arcs the same follower flies when
it is simply placed on the takeoff column, so the arc is real and getting there is
what fails. `BENCH_FLIGHT_ORACLE` with `BENCH_FLIGHT_CSV` writes them
(`approach=1`).

#### ✅ The discriminator: how WIDE the band of working takeoff speeds is

Sweeping nine takeoff speeds (0 … 5.1 px/tick) at the takeoff column and counting
how many fly the arc, against a control of arcs the follower genuinely flies:

| arm | n | median working speeds of 9 | fly at all nine |
|---|---|---|---|
| the 109 approach failures | 109 | **5** | 9 (8%) |
| arcs genuinely flown | 140 | **8** | 69 (49%) |

⛔ **The arcs a bot fails to approach are the ones with a narrow window, and the
ones it flies have a wide one.** That is why the same follower makes them from a
standing start on the column and not from a walk-up: the gate's acceptance band is
wide — `along >= 0` up to `needVx + JumpVtol`, plus the reach model — so on a
wide-window arc anything in the band works and the bot leaves once (**median 1
takeoff**), while on a narrow-window arc the band admits speeds that do not, the
bot commits, falls short, goes off route and tries again (**median 6 takeoffs**,
median 15 off-route fires, 58 of 109 blacklisting something).

✅ `BotPathState.GateColdWeapon/GateTooFast/GateBackwards/GateShort` and
`Launches/LaunchesUnderspeed` were added for this and are kept: a gate that can
refuse for four different reasons is not diagnosable from a refusal count.
⚠️ **The four are per TICK**, so a run-up walking backwards through the takeoff
column inflates `GateBackwards` without anything being wrong — read them against
each other, not as counts of attempts.

#### ⛔ REFUTED: demanding the arc's own takeoff speed

The repair the finding suggests — make the gate symmetric,
`along >= needVx - JumpVtol` instead of `along >= 0`:

| | stranded | rocket-jump stranded | ticks |
|---|---|---|---|
| as shipped | **703** | 369 | 1,556,749 |
| symmetric gate | **924** | 493 | 1,777,554 |

⛔ **It is 31% worse.** `Gate`'s own remarks already record this from the valley
scenarios ("costs a lot for nothing — 647 → 868 and 674 → 997 ticks, with no more
arrivals"); the corpus says the same thing about arrivals rather than about time,
which is the stronger statement and the one that was missing. **Do not re-try
it.** A repair would have to tighten the band only for narrow-window arcs, and
nothing in the graph identifies those at runtime.

#### ⛔ Three more hypotheses, measured and dead

Each looked right, and each died against the control. **All three are listed
because a rejection nobody wrote down gets retried.**

| hypothesis | treatment | control | verdict |
|---|---|---|---|
| the arcs demand more takeoff speed than the ground can supply | 48–60% short | 48–56% short | no difference |
| the bot commits below the arc's speed | 22.4% of takeoffs | 25.6% | no difference |
| the reach model over-promises on these arcs | 54% over by >4px | 49% | no difference |

⚠️ **The reach-model row is the weakest of the three and is quoted only to close
the question**: the `need` it compares against was re-derived outside `Gate`
rather than read out of it, so the absolute numbers are not trustworthy — only
the absence of a gap between the arms is.

### ⚠️⚠️ The rocket that does not go off: a rocket-jump arc degrading to a 57px hop

**The largest single bucket in the residual.** Rocket jumps are 11% of the measured
corpus and **195 of the 429 hard failures (45%)**, and unlike the other arc kinds
they are almost never an approach problem: only **15.4%** are flown by the same
follower placed on the takeoff column, against 57.7% for the plain jump and 48.9%
for the air jump.

#### The mechanism, traced

`cp_dirtbowl` e583, `RocketJump` 43→5 — takeoff column 2982, which is the **last
column of the from-node** (span 2766..2982); the arc needs 101px of rise.

```
[poll t45] keys=Attack, Right, Jump aim=270 rocketFireFlag=True weaponReady=True hp=160
   t45  x=2980.6 y=396.0  vsp=-7.70 gnd=0 ms=0 hp=160
   t56  x=2988.1 y=339.6  vsp=-1.10 gnd=0 ms=0 hp=160     <- peak: 56px of rise
```

⛔ **The rocket key is pressed, the launcher is ready, and nothing happens.**
`moveStatus` stays 0, hp never moves, and the bot flies a **57px ordinary jump on
an arc that needed 101** — the outcome `Gate`'s own remarks call the worst
available one. The blast is aimed straight down (correct: `vectorfactor` is 1 only
axis-aligned) and the comment justifying it says "there is a floor under the
takeoff column by construction" — **but the bot has already left that floor on
the tick it fires.** At the last column of a run there is nothing under it to
detonate against, and the rocket falls into the void.

#### Measured, with a control

`BotPathState.RocketDuds` counts arcs abandoned in the air with `moveStatus` still
0 — the follower already had the branch, it just had no counter.

| arm | edges | takeoffs | rockets that never went off | edges affected |
|---|---|---|---|---|
| the 165 rocket-jump hard failures | 165 | 810 | **194 (24.0%)** | 95 (58%) |
| rocket arcs the follower flies | 215 | 368 | 31 (8.4%) | 26 (12%) |

**Three times the rate on the arcs that fail.** ⚠️ Counted once per airborne
attempt (`AirTicks == 2`); the first version counted per tick and read "1036% of
takeoffs", which is the third time in this investigation a per-tick counter has
had to be corrected to a per-event one. **If a counter's rate can exceed 100%,
that is the bug and not the finding.**

#### ✅ Fixed while here: two launch sites that disagreed

`Launch` now takes the rocket hand-off rather than leaving it to the caller.
⛔ **The gate-open site fired the rocket and the patience hatch did not**, so a
rocket arc whose launcher was ready but whose speed clause refused for two seconds
left the ground with no rocket — the same 57px hop, from a different cause. The
cold-launcher case was handled (it blacklists); the ready-launcher case fell
through.

⚠️ **This moved NOTHING measurable — the corpus sweep is identical to the digit
before and after** — so either the hatch never reaches a ready rocket on the
shipped graphs or its effect is nil. **It is kept as a structural fix, not as a
measured improvement**, and it is recorded that way so nobody quotes it as one. A
branch nothing reaches is a claim nothing checks; that this one now cannot
disagree with its twin is the whole of the argument for it.

#### ⛔ The root cause: the takeoff column is the LIP of the run, and the muzzle hangs off it

The rocket is created at the weapon plus 20px of `lengthdir` in the aim direction
(`Weapons.Launchers.cs`, `RocketMuzzleOffset`) — straight down, so at the
**character's own x**. And the character's x at the takeoff column is not over the
floor.

`cp_dirtbowl` e583, probed pixel by pixel: the ledge is three rows thick and its
solid run **ends at x≈2975**, while the takeoff column puts the character's centre
at **2980.6** — the body box (13 wide) overhangs the lip with about a pixel of
floor under its left edge. The rocket spawns at 2980, misses the ledge entirely,
and falls: y 428.9, 441.6, 454.3 … 612.7, still going.

**Measured over both arms** — placed at rest on the takeoff column, does the
character stay up:

| arm | edges | takeoff column drops a standing character |
|---|---|---|
| the 165 rocket-jump hard failures | 165 | **125 (75.8%)** |
| arcs the follower flies | 150 | 22 (14.7%) |

⛔ **And it is directional, because the body box is.** The node's span is in
**anchor columns — the box's left edge** — so on a rightward arc the takeoff
column is the run's last and the box hangs 6px past it, while on a leftward arc
the box hangs back *onto* the floor:

| direction | failures | arcs the follower flies |
|---|---|---|
| rightward (+1) | **93.8%** fall | 25.4% |
| leftward (−1) | 37.7% fall | 6.1% |

⚠️ **105 of the 125 stand again two pixels back.** This is a lip, not a gap.

#### ⛔ REFUTED: taking off one cell earlier on a rocket arc

The obvious repair, and it is 39% worse: stranded **703 → 981**, rocket-jump
stranded 369 → 573. `SteerJump`'s own remarks say why — *"the takeoff column is
carried ON THE EDGE because the generator SEARCHED for it… re-deriving it from the
node's own columns would jump from a column the build already rejected"* — and a
cell back is a column the build rejected. **Do not re-try it.** That is the second
refuted repair in this area; the first was demanding the arc's own takeoff speed.

⚠️ **So the fault is not obviously the follower's to fix.** The arc works from that
column and the blast does not, which makes it a constraint the GENERATOR does not
apply: a rocket-jump takeoff column needs floor under the *muzzle*, not merely
support for the *anchor*. Acting on that means regenerating all 25 graphs, moving
`NAV_CACHE_VERSION`, `NavGeneratorTests`' character-for-character rebuild and
`nav/manifest.tsv` — the nav lane's change, not this one's, and the corpus sweep is
the gate that would show whether it worked.

#### ✅ FIXED 2026-09-06: tilt the blast when the muzzle has nothing under it

The third option, made and measured. `PathFollower.LipAimDegrees` is 24°, so the
muzzle moves about 8px back along the run and 18px down — past the lip a
straight-down shot falls off — while `vectorfactor` stays near **0.85** rather than
the 0.707 a 45° blast would cost.

| | stranded | rocket-jump stranded | flown | hard failures (budget 4,000) |
|---|---|---|---|---|
| before | 703 | 369 | 1,686 | 429 (rocket 195) |
| **after** | **622** | **291** | **1,735** | **355 (rocket 125)** |

Blacklists 5,140 → 4,967 and ticks 1,556,749 → 1,535,714: the bot stops paying for
attempts that could not work. The rocket jump's stranding rate goes 10.4% → 8.2% at
the gate's budget and 5.5% → 3.5% at 4,000.

✅ **Verified on the case it was diagnosed from.** `cp_dirtbowl` e583 now fires at
aim 246 rather than 270, enters `moveStatus` 1, takes its 30 self-damage and leaves
at vspeed −13.12 against the −7.70 of the ordinary jump it used to fly.

⛔ **The condition is asked of the TERRAIN and that is the whole of it.**
`PathFollower.Collision` is a new optional read-only reference for exactly this one
question. The graph cannot answer it: a node's span ends for gates and walls as
well as for the edge of a run, so "the takeoff column is the node's last column" is
true of most arcs — **used as the test it made the sweep 38% worse (703 → 968),
because it re-aimed the arcs that already worked.** That failed attempt is why the
plumbing exists.

⚠️ **The three pinned end-to-end bot measurements did not move**, which is what
makes this a targeted fix rather than a change of behaviour everywhere. Null
`Collision` behaves exactly as before, so anything driving a follower without one
keeps the old aim.

⚠️ **355 hard failures remain, 125 of them rocket jumps.** The lip is not the only
thing wrong with them and this fix does not claim to be the end of it.

#### ⛔ `cp_dirtbowl` is NOT special — that lead is closed

It holds 131 of the 429, but that is size: three areas at ~2,445 measured edges
each is 23% of the corpus. Its per-area **rate** is 1.7-1.9% against a corpus mean
of 1.34%, and five maps are worse (`gg_debug2` 3.32%, `gg_debug` 3.25%,
`tdm_mantic` 3.05%, `ctf_oldfort` 2.56%, `ctf_orange` 2.44%). **The concentration
is the edge KIND, not the map.**

#### ⚠️ And one more instrument bug, found by tracing rather than by reading

The assisted probe places a bot on the takeoff column at the arc's speed. Since
the takeoff column is usually the **last** column of the run, that walks it off the
ledge on tick 0, before the follower has had a poll. Starting behind the column
instead took the rocket-jump recovery from 10 to 21 of 165 — real, but small, so
the finding above survives it. **The probe was measuring its own start position
for 11 arcs and nobody would have known from the aggregate.**

### What a rocket jump delivers: the residual is the LANDING, not the arc

`BENCH_FLIGHT_LAUNCH`, added 2026-09-06 after the lip tilt shipped. The section
above closed the largest failure bucket and left **125 rocket-jump hard failures**
at a 4,000-tick budget (of 355 overall). `BENCH_FLIGHT_ORACLE` says 28 of those are
the approach and the other 97 are arcs "this follower cannot fly from a perfect
start" — and that sentence names no mechanism. It does not say whether the blast,
the height or the reach was missing, so nothing could be done with it.

⛔ **A first harness fault, and it made the oracle measure the WRONG FOLLOWER.**
`PathFollower.Collision` is what the lip tilt asks the terrain through, and both
probes in `tools/bench/Flight.cs` built their follower without it — so from the
day the tilt shipped, the oracle reported the pre-tilt follower's reach under the
current follower's name. A null `Collision` is *silent* by design (it restores the
old behaviour exactly), which is what let it survive a session. Fixed;
**it moves 6 of 266 verdicts, 3 each way, and the totals do not move** (stranded
107, control 351 → 354). Recorded as structural, not as an improvement.
⚠️ The surprise in that diff is a **`Jump`** edge changing verdict, and it is not a
bug: `EdgeFlights.ClassFor(Jump)` is a Soldier and `RoutePilot` rebuilds
`CanRocketJump` from the live character every tick, so a jump-arc probe routes over
rocket edges like any soldier would.

#### ✅ The launch is not the problem, and neither is the envelope

Each arc flown from its takeoff column at the same six speeds the assisted probe
uses, keeping the best body the manoeuvre produced, against the same control of
arcs the follower demonstrably **flew**:

| arm | n | no blast | median launch | short of HEIGHT | short of REACH | median rise / need |
|---|---|---|---|---|---|---|
| stranded | 125 | 10 (8.0%) | **−14.40** | 9 (7.8%) | 5 (4.3%) | 200.3 / 108.0 |
| flown | 400 | 15 (3.8%) | **−14.40** | 9 (2.3%) | 32 (8.3%) | 206.7 / 120.0 |

⛔ **The launch is −14.40 in both arms — `NavRocketJumpModel.ForRocket`'s own
number to the digit.** The blast is not weak on the arcs that fail. Height is short
on 9 of 115, reach on 5 — and the control is *worse* on reach (8.3%), so neither is
the mechanism. The median arc rises **200 px for a need of 108**.

#### ✅ 87.8% of the failures put the body inside the destination

Asking `NavGraph.NodeFromWorld` every tick (±3 mask rows, ±2 columns — so a landing
or a close airborne pass, not "somewhere overhead"), and the follower's own verdict
beside it:

| where the flight ended | stranded (115 that blast) | flown control (385) |
|---|---|---|
| body never resolves to the to-node | 14 (12.2%) | 10 (2.6%) |
| **reaches it and never arrives** | **29 (25.2%)** | 3 (0.8%) |
| **arrives, but by DIVERTING off the arc** | **44 (38.3%)** | 33 (8.6%) |
| flies the arc — the approach was the fault | 28 (24.3%) | 339 (88.1%) |

⛔ **Only 24 of 125 (19%) are arcs where the manoeuvre fails to happen** — 10 that
never blast plus 14 whose body never gets to the destination. Median closest
approach to the goal for the rest is **1.0 px**. The rocket-jump residual is
overwhelmingly about what happens at the far end.

⚠️ **`arrived` and `steered` must be read PER ATTEMPT.** Summing them over the
speed sweep credited 44 arcs where the oracle credits 28, because arriving at one
takeoff speed and steering the edge at another is not one event. That mistake is
the reason the bottom row now reconciles with `BENCH_FLIGHT_ORACLE` exactly.

#### ✅ The 29 split into two mechanisms, and the arrival test names both

`PathFollower.WalkToGoal` arrives on `onGround && |hspeed| < 1` inside
`BotConstants.ArriveTol` (8 px) of the goal column, with a brake inside the band.
Counting ticks that satisfy the first and third clauses, and the slowest the body
ever got while there:

| | n | never stands on the to-node | stands there | and never gets under 1 px/tick |
|---|---|---|---|---|
| the 29 | 29 | **21** | 8 | **8 of 8** |
| flown control | 372 | 0 | 372 | **0 of 372** |

⛔ **Bimodal, with nothing in between.** The control's band minimum is at most
**0.81** across 372 arcs; the 8 sit at **1.04–2.53** after 3–9 ticks in the band.
These are not arcs that are slightly too fast — they enter the band at ~3 px/tick
off a rocket landing and the brake cannot take that to under 1 before they cross
8 px. **That is the ledge-overshoot defect, measured against the arrival test's own
clause rather than inferred from a miss distance.** The other 21 never land on the
destination at all: the arc grazes within a pixel of the standing spot *airborne*
and carries on.

⚠️ **The band test needs the NODE as well as the column.** Some rocket arcs need
under 8 px of reach — straight up onto a ledge — so a body still standing on the
FROM node is already inside the goal column's tolerance, and a band measured on x
alone counted the takeoff as an arrival opportunity (29 arcs read as "in band" that
were nothing of the kind).

#### What this does NOT establish

- **Nothing here is a repair**, and no pin moved when it was written: the corpus
  sweep was 622/1,735/1,535,714 ticks/4,967 blacklists, identical to the digit.
  ⚠️ **Those are pre-`Capable` figures** and every count in this whole section is —
  the fix below landed after it and moved all of them.
- ~~**The 44 diverted are unexplained.**~~ ✅ **Explained and fixed** — they were
  the cold launcher unplanning the route. See "A cold launcher unplanned the
  route" below; the corpus's `Diverted` went 2,128 → 33 and every number in the
  two tables above is from before that fix.
- **The 8 are 6% of the 125** and a repair worth ~8 arcs is not obviously worth a
  change to the arrival test, which every edge kind and every live bot goes
  through. ⚠️ The falsification: if a widened band or a stronger brake shipped and
  the corpus sweep's `stranded` did not fall by roughly that many, the mechanism
  above is not what is costing those arcs.

### ⛔ A cold launcher unplanned the route — `RocketModel.Ready` vs `Capable`

**The largest single defect found in the bot layer since the nav lanes merged, and
it had been there for as long as the query rebuild has.** Found 2026-09-06 by
tracing one arc after every aggregate had failed to name a mechanism.

#### The bug

`ScriptedBot` and `EdgeFlights`' pilot both rebuild the search query from live
state on every tick — deliberately, and the reason recorded beside it is sound: a
soldier's routes should shed their rocket jumps as its hp drops, because a route
planned at 160hp is still installed at 35. But the test they used was
`RocketModel.Ready`, and `Ready` includes **`weapon.ReadyToShoot`** — the 30-tick
refire flag.

So a soldier that had just fired, at anything, answered *"I cannot rocket jump"*
for a second. `NavGraph.EdgeRoutable` then refused **every rocket edge on the
map**, `TryFindEdge` found no usable edge for the hop it was already flying, and
"no usable edge is a re-plan" did exactly what it says — the follower re-planned a
route with no rocket jump anywhere in it and walked the long way round.

⚠️ **Nothing looked broken.** The bot arrives. It is slower and it takes a
different route, and no gate in this repository asked whether a bot that arrived
had used the arcs it was routed over — which is the failure mode CLAUDE.md
describes as "a match that ends is not a match that happened", arriving in a place
nobody had thought to look.

#### The fix

`RocketModel.Capable` — hp, class and ammo, the durable properties of the
traveller — is what the two query rebuilds ask now. `Ready` keeps `ReadyToShoot`
and keeps its job: the **takeoff gate**, which already waits a cold launcher out
and counts the wait in `BotPathState.GateColdWeapon`. One test split in two, each
used where it belongs.

#### Measured, on the corpus sweep, with a control

| arm | stranded | rocket stranded | flown | rocket flown | diverted | ticks | blacklists |
|---|---|---|---|---|---|---|---|
| before | 622 | 291 | 1,735 | 1,116 | 2,128 | 1,535,714 | 4,967 |
| **after** | **536** | **203** | **3,626** | **3,007** | **33** | **1,476,150** | 7,980 |
| after, warm launcher | 508 | 181 | 3,844 | 3,225 | 48 | 1,351,181 | 4,667 |

⛔ **`flown` more than DOUBLING is the finding, not `stranded` falling.** The sweep
exists to ask whether the graph is followable, and it was posing that question on
only 1,116 of the 3,536 rocket edges it *measured* — 32%. It is now 3,007, 85%.
A stranding count that improves 14% understates what changed here by a lot.

✅ The same on the no-tables arm, which is a different corpus and so an independent
check: stranded 626 → 579, flown 2,297 → **5,530**, `Diverted` 3,837 → **16**.

✅ `ScenarioSuiteTests`: rocket jumps over the fourteen legs **6 → 10**, one leg 29
ticks faster (`valley-shaft-floor-to-point` 161 → 132), one 3 ticks slower
(`avanti-ramp-pocket-to-intel` 217 → 220), twelve unchanged, **no leg regressed**.
That pin exists precisely to catch the search quietly ceasing to offer these arcs,
and it moved the way it is meant to when the opposite happens.

⚠️ **`SeparationBandTests`' three duel-damage pins moved and NOT all one way**
(`arena_lumberyard` 1,216.7 → 1,241.4, `koth_harvest` 877.6 → 791.1, `ctf_2dfort`
1,425.1 → 1,313.3). Soldiers take rocket routes they used to abandon, so they
arrive at different places at different times; a duel's damage total is not a
quality measure and a mixed sign here is what a routing change looks like rather
than a regression.

#### ⚠️ The one number that got worse, and why it is the harness

Corpus blacklists **4,967 → 7,980** (+61%), and on `gg_debug` 2 → 15 over
twenty-one arcs. The bot now *attempts* arcs it used to route around, and the
off-route machinery's one-retry-then-ban runs on all of them.

⛔ **But the control settles where it comes from**: with `BENCH_FLIGHT_WARM` the
figure is **4,667 — lower than the 4,967 before the fix.** `EdgeFlights` spawns a
character on the from-node and asks for the arc at once, and
`Weapon.events/Create.xml` starts a fresh weapon with `ReadyToShoot` false and the
refire alarm running — roughly 45 ticks from being able to fire. A bot that has
been alive in a match for any time at all is not in that state. So the rise is the
sweep's own spawn showing through, now that the bot waits at the gate for the
launcher instead of routing away from it, and `BotConstants.TakeoffPatience` (60
ticks) is what eventually bans the edge.

⚠️ **Falsification:** if `TakeoffPatience` were raised and the cold arm's
blacklists did not fall towards the warm arm's, the spawn is not what is producing
them and this attribution is wrong.

#### ✅ PRICED: what it cost the bot layer, measured against a fixed checkpoint

**The first bot-layer move in this project to be priced rather than assumed**,
measured by the `gg2-rl` lane on 2026-09-06 when it re-pinned from `90cb383` to
`c434229`. Same checkpoint files, same script, nine mirror cells against the
scripted bot, 400 duels, four seeds:

| | before | after | |
|---|---|---|---|
| scout, sniper, demoman, medic, engineer, heavy, spy, pyro | — | — | **identical to the digit, per seed too** |
| soldier | 46.47 | **46.19** | −0.28, against its own 2·SE of **11.5** |

⛔ **The eight unchanged classes are the finding, not the soldier.** They are what
says the change did not drift anything it was not aimed at — a single moved cell
with eight identical ones beside it is attribution; a single moved cell alone is a
number. ⚠️ **Soldier stays UNDECIDED**: −0.28 is far inside the noise that made it
undecided before, so the routing fix did not resolve it either way.

⚠️⚠️ **THIS CONTROLS THE DUEL HARNESS AND THE SIMULATION. IT DOES NOT CONTROL THE
MATCH HARNESS.** Eight identical mirror cells across a pin move say the physics
and the duel path did not drift — and the duel path is not what changed on
2026-09-06. `tools/bench`'s match-level modes were rewritten that day (the class
roster, the arm construction, two null dereferences), they are not in `Gg2.sln`,
and they have no tests. **So a green re-score is not clearance for a match-level
number**, and the two are easy to read as one result because they arrived
together. The rule the training lane wrote for their first match-level run after
it: a surprising number is checked against the roster line and the arm
construction BEFORE anything is concluded about the policy.

⛔ **The instrument is a FIXED ARTEFACT, and a symmetric one cannot do this job.**
The obvious control — scripted-soldier against scripted-soldier — is ~50% *by
construction*: same class, same driver, both orientations averaged, so it returns
the null whatever the absolute strength of either side. It would read 50% whether
this fix worked or made things worse. What separates them is re-scoring the SAME
checkpoint file at both commits: the weights are byte-identical, so any movement
is the environment's. ⚠️ This session proposed the symmetric control first and was
wrong; it is recorded because it looks like a control and is a tautology.

⚠️ **This generalises past soldier.** The policy gate is scored against the
scripted bot for every class, so ANY bot-layer move shifts the null under all
nine — `CLAUDE.md`'s "re-score a known checkpoint when the bot layer moves" is
usually read as a validity check on old numbers, and it is also the only way to
attribute new ones.

#### What found it, after four instruments did not

The launch readings said the failures and the successes were indistinguishable in
blast, height and reach; the counters said the bot barely steered the edge at all;
the in-air abandonment counter came back ~0 in every bucket. **None of those names
a mechanism.** `BENCH_FLIGHT_TRACE` prints one arc a tick at a time, and the first
one showed a bot standing still for 45 ticks and then flying a *different* edge
from the node it had slid onto. ⚠️ **An aggregate can only rank hypotheses you
already have.** Four rounds of slicing produced smaller and smaller buckets and no
cause; one trace produced the cause in a minute.

### The residual after `Capable`, re-measured — and it is now the APPROACH

⛔ **Everything in the two sections above this one was measured against the broken
follower and none of it survived the fix.** Re-run 2026-09-06 immediately after,
because a stale attribution is worse than none: it names buckets that no longer
exist and sends the next session after them.

| | before `Capable` | after |
|---|---|---|
| hard failures @4,000 ticks | 355 | **344** |
| of them rocket jumps | 125 | **111** |
| assisted instrument's RECALL | 88.5% | **97.5%** |
| stranded arcs the approach explains | 109 of 339 (32.2%) | **150 of 254 (59.1%)** |
| rocket arcs the approach explains | 28 of 125 (22.4%) | **74 of 111 (66.7%)** |
| rocket arcs unflyable from a perfect start | 97 | **37** |

⛔ **The instrument had the bug too**, which is why its recall jumped nine points
and why 60 of the 97 "arcs this follower cannot fly from a perfect start" simply
evaporated: the probe's own bot was unplanning its route whenever its launcher was
cold, exactly like the bot in the sweep. **A probe that drives the real follower
inherits the real follower's bugs**, and its recall is the only thing that shows
it — the treatment rate alone moved in a perfectly plausible direction.

✅ **The residual is now the approach, by a clear majority**: 59% overall and 67%
of rocket jumps, against a control recall of 97.5%. The landing buckets that
iteration produced are correspondingly smaller — the `Diverted` bucket is **gone
from both arms**, the arcs that stand on the goal and cannot brake under 1 px/tick
are **5** (was 8), those that graze it airborne **10** (was 21).

⚠️ **The "narrow takeoff window" analysis of the approach failures is itself
pre-`Capable`** and has not been re-run.

#### ⛔ `TakeoffPatience`'s ban is INERT on this corpus, and the sweep cannot price one

The falsification written beside the `gg_debug` blacklist pin, run:

| `BotConstants.TakeoffPatience` | blacklists | stranded | flown | arrived | diverted | ticks |
|---|---|---|---|---|---|---|
| 60 (shipped) | 7,980 | 536 | 3,626 | 31,519 | 33 | 1,476,150 |
| 120 | 6,683 | 536 | 3,626 | 31,519 | 33 | 1,476,150 |
| 400 | 5,594 | 536 | 3,626 | 31,519 | 33 | 1,476,150 |

⛔ **Only the blacklist column moves. Every other number is bit-identical.** So the
gate's cold-launcher ban fires exclusively on flights whose outcome and duration it
does not change, and the blacklist rise the `Capable` fix brought with it is
bookkeeping rather than a cost. ⚠️ **This also means a blacklist count from
`BENCH_FLIGHT` is a diagnostic and not a price**: a single-hop flight ends before a
ban can have a consequence. Where 7,980 lands: 3,161 on the 536 stranded flights
(5.9 each — thrashing) and 4,819 across 3,380 of the 31,519 arrivals.

#### ✅ What the fix is worth where a ban DOES matter

`BENCH_FLIGHT_ROUTES=200` flies whole routes, so a banned edge changes the rest of
the journey. Same seed, two binaries differing only in `Ready` vs `Capable`:

| | routes posed | arrived | simulated ticks |
|---|---|---|---|
| `Ready` (the bug) | 3,866 | 3,722 (96.3%) | 2,042,402 |
| `Capable` | 3,866 | **3,729 (96.5%)** | **2,001,641 (−2.0%)** |

⛔ **Quote this beside the doubling, not instead of it, and never the doubling
alone.** `flown` 1,735 → 3,626 is a statement about what the per-edge sweep was
managing to ASK — it was posing its own question on 32% of the rocket edges it
counted and now poses it on 85%. It is not a claim that bots get around twice as
well: over whole routes the same fix is worth 2% of travel time and two tenths of a
point of arrivals, because A* has alternatives and rocket jumps are a small
fraction of a long route's hops.

### The route sample: the 68% the per-edge sweep cannot reach

`BENCH_FLIGHT_ROUTES=<n>` plans between `n` random node pairs a map and flies the
whole route. Measured on `ctf_truefort` (500 pairs, budget 3000, no tables,
2026-09-05): **2.8 ms a route, 56 hops average, 368 of 400 posed routes arrive**.

⚠️ **`noroute` is not a failure and is large** — 100 of 500 pairs on
`ctf_truefort`, 96 of 200 on `gg_debug`. A nav graph describes the terrain, so it
carries nodes no route reaches; see "Island nodes" above.

⛔ **It is a sample, and all-pairs is affordable but is not a gate.** The 25
graphs hold 11,327 nodes — **6,546,185 ordered pairs, about 5 core-hours** at the
measured rate, or ~13 minutes across 24 cores. That is a nightly or on-demand run.
A 1% sample is three core-minutes.

⛔ **Neither mode is a match, and every absence arrives as a legal-looking zero.**
A flight is one bot in an empty world: no enemies, no projectiles, no sentry, no
crowd for occupancy to push routes apart. `docs/BOTS.md`, "A duel does not contain
what a match contains".

### ✅ Bot zoom, `EnvAction`'s sixth head, and `ScenarioWorld`'s missing poll

`toggleZoom` is not a key. `PlayerCommands.ToggleZoom` says so in as many words —
*"Zoom is not a key. It is its own client command"* — and a bot's entire output is
`PlayerInput`, which is `PlayerKeys` plus an aim. So **no `IInputSource` in this
port can zoom**: not `ScriptedBot`, not `RolloutSearch`, and not a learned policy,
because `EnvAction`'s five heads have no zoom either.

Reference damage, from `Weapons.RifleBeginStep`: unscoped
(`RifleUnscopedDamage`) **35**, scoped uncharged (`RifleBaseDamage`) 45, scoped
full charge (`RifleMaxDamage`) **75**. ⚠️ **A bot that never zooms fires at 35
for ever**, against a weapon that reaches 75, and `BotClassKeys`' sniper branch
reads `self.Zoomed` (always false for a non-zooming bot) — so this is the
mirror of CLAUDE.md's standing rule ("a bot must not do something a client
cannot") in the other direction: a client could do something a bot could not.

✅ **Closed.** `ICommandSource` already existed (phase 7, `ScriptedBot`); two
things were missing:

1. **`ScriptedBot.WantsZoom` pushes `ToggleZoom` as an edge, not a level** —
   `PlayerCommands.ToggleZoom` *flips*, so pushing it every tick a sniper has a
   target would scope/unscope at 30 Hz and hold a charge of zero for ever.
2. ⚠️ **`ScenarioWorld` must poll `ICommandSource`**, or a duel or scenario
   drops every command a bot emits (`GameServer` and `HeadlessMatch` both poll
   it already).

**And the policy half**: `EnvAction` gains a sixth head, `Zoom`, moving
`ActionEncodingVersion` 1 → 2, `SIM_UUID` `61edbca3899d1b07` →
`c526b96e34184138`, and `EnvProtocol.Version` 3 → 4 (six action bytes per agent,
`ComponentBytes` 44 → 48).

⚠️ **A LEVEL, not a toggle.** The head is the state the agent wants and
`ActionDecoder.WantsZoomToggle` owns the edge, so the head is idempotent and a
policy needs no memory of its own scope. A toggle-shaped head would turn one
dropped or duplicated action into a permanently inverted sniper.

#### ⚠️⚠️ The search's aim was decided by its tie-break, not its scoring — fixed

`gg2-rl`'s corpus analysis measured, on rows that are **not** blind:

| | |
|---|---|
| rows with the top two candidates tied | **85.2%** |
| actions sharing the best score, median | **6** (mean 15.2, max 60) |
| rows with a unique best action | **7.8%** |

⚠️ **The search is usually INDIFFERENT**, and ties go to the first candidate.
`BuildCandidates` loops **aim outermost** over `[0, 3, 7, 11, 14]`, where bin 0 is
a hard left turn — so the first candidate at any aim-tied score is *turn hard
left*. Measured here over ~thousands of decisions on `ctf_2dfort` banded, tier 5:

| class | share of decisions taking aim bin 0 |
|---|---|
| soldier | **0.966** |
| scout | **0.929** |

**So the bot's aiming is essentially "turn hard left", with occasional scored
corrections.** That is not a strategy, it is enumeration order showing through —
and it is the same signature as the blind tie-break, reaching decisions that are
*not* blind.

##### ✅ Fix: a lexicographic aim tie-break

Among candidates whose scores are **exactly equal**, prefer the one that leaves
the searcher pointing closest to its nearest live enemy. ⚠️ **Damage stays the
only objective**: it is compared only on a tie, so no decision the score settles
changes and the search cannot trade a hit for a bearing. What it replaces is loop
order.

**Paired A/B, `ctf_2dfort` banded, tier 5, 60 duels a cell, same seeds:**

| class | before | after |
|---|---|---|
| scout | 49-8-3, hard-left 0.892 | **58-1-1**, 0.137 |
| soldier | 42-5-13, hard-left 0.967 | 42-6-12, 0.048 |

✅ **And the blind fraction falls** — scout 0.810 → 0.649, soldier 0.902 → 0.841,
and on a fixed 160px soldier duel **0.84 → 0.29** — because better aim lands more
shots inside the horizon. ⚠️ **That is the signature `RolloutOptions.CommitTicks`
was built to produce and failed to**: blind falling without win rate falling,
reached by a different mechanism.

✅ **CONFIRMED INDEPENDENTLY AND IT IS BIGGER THAN THE A/B SHOWED.** `gg2-rl`
re-measured the whole expert against this build — 1,600 duels, sixteen pairings,
both orientations:

| | old (`ab78c9d`) | new (`2d810c3`) |
|---|---|---|
| expert win rate | 76.9% | **84.2%** [81.8%, 86.5%] |
| matchups clearing p<0.01 | 15 of 16 | **16 of 16** |
| worst matchup | sniper mirror, **a loss** | scout-v-sniper, 66.0% |

**+7.3 points, and the sniper mirror stops being a defeat.** My own A/B saw the
scout move and the soldier stay flat at 60 duels a cell; the full sixteen-pairing
run says the effect is broad rather than one class. ⚠️ **A two-class A/B
understated a change that touched every class**, which is the opposite of the
sample-size error made earlier the same day and worth the same caution.

✅ **The three predictions made when handing this over all held**: the
`[0,0,0,0,0,0]` share fell 35.2% → **1.5%**, the aim-bin-0 share ~85% → **4.4%**,
and the tie RATE held (85.2% → 89.6%) because the scoring function was untouched.
⚠️ **The tied-set SIZE shrank, median 6 → 4**, which was explicitly *not*
predicted either way.

✅ **It also kills `gg2-rl`'s `[0,0,0,0,0,0]` corpus signature.** A blind decision
now points at the enemy rather than turning hard left, so a blind row is no longer
identifiable by its action at all — only by `BestScore == 0`. **That makes
`BestScore` load-bearing rather than convenient.**

⚠️ **Three tests were re-recorded** and one is worth knowing: the sniper now
scopes at horizon 20, where it never did. Scoping changes `RunPower`, which
changes where the character ends up, which changes the aim error the tie-break
compares — so a zoom candidate can win a tie enumeration order used to give to its
unscoped twin. **That is the scope being chosen by a side effect rather than on
merit**, which is better than never being chosen and is not the same as being
chosen well.

##### ❌ The obvious fix, which does NOT win

Putting no-turn first (`[7, 3, 11, 0, 14]` — same candidate set, different order,
so ties resolve to *not turning*) removes the bias completely: hard-left share
0.966 → **0.004**. Win rate, 24 duels a cell:

| class | shipped | centred |
|---|---|---|
| soldier | 18-2-4 | **12-2-10** |
| scout | 18-3-3 | **22-1-1** |

⚠️ **The scout improves and the soldier degrades**, the soldier's losses turning
into draws rather than defeats — consistent with a splash class that was
accidentally benefiting from sweeping its aim across the target every decision.

⚠️⚠️ **DO NOT READ A DIRECTION OFF THAT TABLE.** It is 24 duels a cell, and this
file already had to retract a direction read off twelve. **What is solid is the
BIAS RATE** — a share over thousands of decisions — and what is not solid is
whether removing it helps. Two different sample sizes in one experiment, and only
one of them supports a conclusion.

##### ⬅️ Why it matters beyond the bot

**An expert whose actions are 93-97% arbitrary tie-breaks is a hard thing to
clone**, and phase 2 is trying. `gg2-rl` measured a soft-target policy landing
inside the tied-best set 57.8% of the time while its *exact* match fell to 0.143
— agreement rising while exact-match fell, because exact-match was scoring a
tied alternative as wrong. ⚠️ **Any metric that treats one of six equally-good
actions as the right answer was measuring the wrong thing**, and phase 2 reported
that metric for three days.

⬅️ **The open question this raises is whether the EXPERT should be improved before
more effort goes into distilling it** — a scoring function that discriminated
among today's ties would change what there is to learn. That is a bigger question
than a tie-break order and it is not answered here.

#### ⚠️⚠️ Known issue: the scope makes the scripted sniper WORSE, by about 10%

Giving the bot the zoom capability costs it damage, against expectation.

24 banded sniper duels against `RolloutSearch` on `ctf_2dfort`, 2,400 ticks, tier
5 — same map, band, cap and classes, only the command channel differing:

| | unscoped | scoped |
|---|---|---|
| damage | **3,045** | **2,768** |
| hits | 87 | **50** |
| damage a hit | 35.0 | 55.4 |
| search wins | 7 of 24 | **19 of 24** |
| scope toggles | 0 | 217 |
| average separation | 189 | 190 |

⚠️ **The mechanism is FIRE RATE, not the movement penalty** — separation is
unchanged, so it is not that a 0.6-run-power sniper positions worse. It is
**1.58× the damage a shot against 0.57× the shots**: `BotClassKeys` waits
`RifleChargeTime * min(1, distance / ClassRange)` before every shot, and at duel
range that wait costs more than the charge buys.

⚠️ **A rule that has never executed against live play is not a tuned rule, it is
an untested one.** This port prefers the `BOT_*` constants because they have
real playtesting behind them — but the scope-charge rule could not have been
played, by construction, until a bot could zoom at all. It is new code that has
just met its first opponent.

##### ⚠️ Why it is NOT tuned: the obvious model contradicts the measurement

Sustained DPS, from `Weapons.RifleBeginStep` — scoped damage
`45 + floor(sqrt(f)*30)` over a cycle of `40 + 105f`, against unscoped 35 over 40:

| distance | charge fraction | scoped DPS | verdict |
|---|---|---|---|
| 120 | 0.13 | 1.019 | scope |
| 190 | 0.21 | **0.933** | scope |
| 250 | 0.28 | 0.867 | no |
| 400 | 0.44 | 0.750 | no |
| 900 | 1.00 | **0.517** | no |

Unscoped is **0.875**. So the model says scoping at 190px is *better* — and the
measurement at 190px says it is 10% worse. **A threshold fitted from that model
would be fitted against evidence that already disagrees with it**, and the
residue it omits (217 toggles zeroing the charge, shots not taken while waiting)
is exactly the part that matters.

⚠️ The model also says the rule is DPS-negative at *every* range past 250px,
which cannot be the whole story — a real sniper charges for **burst to kill**, not
for sustained DPS. Fixing this needs a decision about what the rule optimises and
more than 24 duels on one map. CLAUDE.md: improve a bot decision where the fix is
straightforward and measured; **write the hard ones down**.

##### ⚠️ The sign is not stable at small samples

At **8 duels of 1,200 ticks it is a wash** — 1,059 scoped against 1,050 unscoped.
The 10% loss needs 24 × 2,400. So `BotZoomTests.TheScopeTradesFireRateForDamageAndCurrentlyLoses`
gates the **mechanism** (damage a hit up, hits down — both pinned) and
deliberately does **not** assert the net direction, because that sample does not
carry it. **Anyone re-tuning this rule from a short run will read noise.**

#### ⚠️⚠️ The search does NOT use the zoom head at its default horizon

`ScriptedBot` uses the scope immediately — its rule is "have a target, be
scoped". `RolloutSearch` only uses what pays inside its horizon, and **the
rifle's 40-tick
refire is double the 20-tick default**, so the whole candidate set scores exactly
zero, the tie goes to the first candidate, and the first candidate is unscoped.

Measured, `gg_debug` at 220px:

| horizon | scopes? | decision score |
|---|---|---|
| 20 (default) | **no** | 0 throughout |
| 60 | yes | 45-70 |
| 90 | yes | 70-105 |

✅ **So the head is not inert — the horizon is the binding constraint**, and
`BotZoomTests.ARolloutDrivenSniperScopesOnlyWhenItsHorizonCanSeeAShot` pins both
halves so neither can be mistaken for the other.

⚠️ **The default was deliberately NOT changed to fix it.** A horizon is a
whole-bot knob, and the sweep has since shown it is **not** one worth turning:
blind fraction and win rate oppose monotonically for every class, because a
candidate is one action held for the whole horizon. See "both curves oppose"
above. That is a separate measured decision, not a side
effect of adding a head.

⚠️ **Consequence for anyone measuring the sniper matchup now**: a scripted sniper
plays a 75-damage weapon and a default-horizon search still plays the 35-damage
one, so `gg2-rl`'s 18.4% is expected to get *worse* before it gets better. That
is the cost of closing the gap in the right order — the class was previously
unlearnable, which is worse.

### ⚠️ A horizon shorter than the refire: measured per class, and it is the SOLDIER

A horizon shorter than a refire timer makes every candidate score zero, so the
tie goes to the first candidate and the bot stands still. **The rifle's refire
is 40 ticks against a 20-tick horizon — the longest in the game, and the only
one at double it** (rocket launcher 30, minegun 26, revolver 18, the rest under
6). Lengthening the horizon past the rifle's refire makes the sniper *worse*
(win rate falls as horizon rises — see the table below), so the blindness is
real and survives a longer horizon. ⚠️ **Direction claims here need at least 80
duels a cell** — a 12-duel sample read the soldier's row as "much better" at a
longer horizon and inverted at 80 duels (93.8% → 78.8%); only the sniper's
direction held up at the larger sample.

#### ⚠️⚠️ BOTH CURVES OPPOSE, MONOTONICALLY, FOR EVERY CLASS

`ctf_2dfort` banded, tier 5, repeat 4 — blind over 1,200 decisions a cell, win
rate over 80 duels a cell:

| | 20 | 30 | 45 | 60 | 90 |
|---|---|---|---|---|---|
| **blind** sniper | 89.8 | 82.2 | 53.0 | 47.0 | **43.5** |
| **blind** soldier | 80.5 | 69.6 | 52.0 | 38.1 | **29.8** |
| **win** sniper | **68.4** | 66.2 | 39.2 | 39.2 | 22.8 |
| **win** soldier | **93.8** | 92.5 | 78.8 | 64.6 | 63.7 |

**Every class, every step, opposite directions.** ✅ The refire mechanism is
confirmed by the cliff LOCATIONS — the sniper's between 30 and 45 around its
40-tick refire, the soldier's opening at 30 — but every class plateaus at 30-45%
blind however long the horizon, and **that floor is geometry**: decisions with
nothing to shoot. A fully-signal corpus was never available at any horizon.

⚠️ **THE CAUSE IS THE CANDIDATE REPRESENTATION, not the horizon.**
`RolloutSearch.Rollout` takes one `EnvAction` and calls `_decoder.Decode` with it
**every tick to the horizon**, so at horizon 90 the search chooses among 120 plans
of the shape *"hold this key and this aim for three seconds"*. It sees further —
blindness falls exactly as the refire mechanism predicts — while the plans become
increasingly implausible commitments. **Lookahead and commitment are the same
number.**

⚠️ So raising the default would not fix a distillation corpus: it trades rows
carrying no decision for rows carrying a **badly chosen** one, and the second is
strictly worse. A blind row is labelled `BestScore == 0` and can be dropped; a
confidently-wrong row is indistinguishable from a good one.

❌ **A second, attractive hypothesis died here.** `HoldLastInput` planning against
an increasingly fictional opponent predicts the opponent-model gap should WIDEN
with horizon. Measured, soldier mirror, 80 duels a cell: horizon 20 gap
**-3.5pp** (Idle better), horizon 90 **+0.9pp** — it does not widen, and both
models lose about thirty points together. The degradation is **not** opponent-model
error, which is what leaves the candidate representation as an explanation rather
than a guess.

⬅️ **The open idea, from `gg2-rl` and explicitly not asked for**: decouple
lookahead from commitment — score a candidate over its `DecisionTicks` and
continue the rollout under a default continuation for the rest of the horizon.
The ordinary receding-horizon split; today the two are one number. ⚠️ **The
signature of a real fix is the blind fraction falling WITHOUT the win rate
falling.**

### ⚠️⚠️ A `ScriptedBot` with no `Objective` is not the scripted bot

**The largest single effect measured on `RolloutSearch` all session**, and it was
in the test apparatus rather than in the bot.

`ScriptedBot.Objective` is optional. Without it the bot has no objective layer at
all: it never picks a goal, never hunts, and **stands where it was placed**.
`DuelEnv.MakeBot` has always set it. `RolloutSearchTests.ItBeatsTheScriptedBotInADuel`
never did, and neither did the first draft of every measurement in this section.

The same 24 banded duels on `ctf_2dfort`, tier 5, 2,400 ticks:

| opponent | result |
|---|---|
| `ScriptedBot` with no `Objective` | **4-0-20** — twenty stalemates |
| `ScriptedBot` with one | **21-1-2** |

⚠️ **It was invisible at a fixed position and decisive banded.** At `gg_debug`'s
`x=600` the two start 160px apart, already inside every weapon's range, so an
opponent that cannot close loses nothing by it. Banded placement puts the pair
150-350px apart on purpose — and then "cannot close" is the whole duel.

⚠️ **So the pinned smoke figure was against a weaker opponent than the one every
other caller uses.** Fixed, and the corrected banded figure (21-1-2 against
`gg2-rl`'s independently measured 76.5% over 1,600 duels) is in that test's
remark. **A harness that constructs a bot by hand has to construct the same bot
the product constructs** — the third time this session a measurement was invalid
because the apparatus differed from the thing it claimed to measure, after the
flight harness's start position and the `gg_debug` opponent-model scenario.

### ⚠️ `gg_debug` is a determinism fixture, not an evaluation map

Four independent measurements now say so, and they were taken for four different
reasons:

- **One spawn point per team**, so sampled placement draws the same duel every
  time — `gg2-rl` computed a sixteen-duel confidence interval off a sample size
  of one.
- **Zero opponent-model divergence in 12 of 12 scenarios**, at every separation
  tried, against both a scripted and a rollout opponent.
- **An order-of-magnitude position swing inside one room**: 11-1-0 at `x=600`
  against 4-2-6 at `x=1500`, same everything else.
- **Banded, it is 4-1-19** — nineteen stalemates out of twenty-four, against
  21-1-2 on `ctf_2dfort`.

It is excellent at what phase 0 uses it for — determinism, throughput, exact tick
counts. Evaluate on `ctf_2dfort`.

### ⚠️ A check that cannot disagree is not a measurement

Rule, established from four defects of one shape (something compared against a
copy of itself — the wire's layout checked against the constant that described
it, an advertised head count checked against a literal copy of itself, a
policy's per-head argmax compared against a quantity identical to it by
construction, and a fingerprinting tool that hashed only where a duel *starts*
and so could not detect any change that happens afterward): **before running a
check, ask what result would falsify it.** If no input can produce disagreement,
it is not a measurement, and "it agreed with me" is the least informative
outcome a check can have — a verification tool is not exempt from the shape it
exists to catch.

#### ✅ What the meaningful version measured: the architecture is NOT the ceiling

The version with content asks not where the argmax lands but **how much mass the
product misplaces** — soft-target marginals fitted to the expert, producted over
this repo's candidate set:

| tied-set size | rows | expert mass | factored mass |
|---|---|---|---|
| 1 | 255 | 0.958 | 0.944 |
| 3 | 131 | 0.990 | 0.853 |
| 5 | 39 | 0.997 | 0.801 |
| 7 | 15 | 0.998 | 0.750 |
| **overall** | 3,862 | **0.978** | **0.934** |

**A product of six marginals fitted exactly to the expert still places 93.4% of
its mass on the best candidates.** The factorisation costs about four points,
concentrated on mid-sized tied sets — roughly 5% of rows.

⚠️ **So the trained policy's 0.616 is far below what the representation allows: the
gap is in what is LEARNED, not in what can be EXPRESSED.** That eliminates the
last structural explanation. Four are now measured and dead — capacity (wider is
worse), observation quality (beats 1-NN), state coverage (fixing it worked and cost
7.2 points of play), representability (93.4% of 97.8%). ✅ **The anti-correlation
is left unexplained on purpose; neither lane has a fifth candidate and neither
invented one.**

⚠️ **The 7.2 there is ONE training run a side** — see `docs/ENV.md`, "every win
rate above is ONE training run a side". The conclusion it supports survives the
noise by fifty points; the 7.2 itself lives inside it.

### ⚠️ A correction is a claim too

Label-transfer from policy-visited states to their nearest control state
retains **36%** (0.204 → 0.074): the states genuinely are meaningfully
uncovered — the shadow slot's compounding-error rationale in `docs/ENV.md`
stands. An earlier retraction of that claim, based on raw Euclidean distance
over the unnormalised 209-float observation (which weights every column
equally where a policy does not), was itself wrong. **A correction inherits the
burden of proof, it does not discharge it** — ask which space a distance was
computed in before trusting a "these two sets are close" claim over an
unnormalised feature vector, and give a correction sourced from outside this
repo the same interrogation as a feature request from it, not less.

### ⚠️ A route is a list of NODES, and re-deriving its edges was wrong for eight phases

**2026-08-28.** `NavSearch` returns `List<int>` — nodes, not edges. So
`PathFollower.FollowEdge` has to work out which edge each hop meant, and
`TryFindEdge` did it by taking **the first edge from the pair's edge index**.
Its own doc comment said that was safe: *"the search costed whichever it took, so
the follower steers the first the index yields."*

**That sentence was false, and nothing in the repo could contradict it** — the
exact failure mode `CLAUDE.md` names. The search refuses **per edge**; the
follower resolved **per node pair**.

#### The graphs really do carry parallel edges, and plenty of them

Measured across all 24 shipped caches, and now pinned by
`NavGraphFixtureTests.ParallelEdgesAreCommonAndCarryMoreThanOneType`:

| | shipped `botnav` | with `--sticky` |
|---|---|---|
| edges | 115,757 | 119,889 |
| node pairs | 104,087 | 104,615 |
| pairs with more than one edge | 11,670 | 14,470 |
| pairs with more than one **type** | **11,615** | **14,415** |

By type: `Walk+Fall` 9,251, `Fall+Jump` 1,529, **`DoubleJump+RocketJump` 835** —
and, on a sticky cache, `RocketJump+StickyJump` 2,479 and `DoubleJump+StickyJump`
321.

#### Two different faults, and keeping them apart matters

- **A cost mismatch** — 3,821 walk/fall pairs where both edges are flyable by
  anybody and the cheaper is simply not the one the index yields first. The bot
  arrived, along an arc its route had not costed.
- **A capability mismatch**, which is the live bug — **833 pairs where a
  soldier's route is costed as a rocket jump and the index yields the double jump
  first**. A soldier cannot double-jump, so it pressed for a second jump it does
  not have, flew an ordinary 57px hop where the arc wanted 276, landed somewhere
  the route does not mention and eventually **blacklisted a perfectly good edge**.

⚠️ **A scout was accidentally right the whole time**, because the double jump is
first on every one of those 833 pairs. Half the population never showed the bug,
which is a large part of why it survived eight phases.

⚠️ **`UnsupportedEdgeTicks` stayed at zero throughout.** The dispatch switch saw
a type it handles; the counter that exists to notice an unsteerable edge kind is
blind to a *steerable* edge kind being steered on the wrong route.

#### The fix is A*'s own rule, not a new one

`NavSearch` relaxes with `existing >= 0 && existing <= tentative → continue`, so
among parallel edges it keeps the **strictly cheapest** and an equal cost **never
displaces what is already there**. `TryFindEdge` now does exactly that: lowest
`NavEdge.Cost` among the routable, first-in-order on ties. ⚠️ The occupancy
multiplier deliberately does not enter it — it is keyed on the node pair, so it
scales every parallel edge identically and cannot reorder them.

✅ **The refusal rule now has one copy**, `NavGraph.EdgeRoutable`, applied by both
the search and the follower. The whole bug was two places answering the same
question differently, so a second copy is the thing to guard against.

#### ⚠️ It must be the query the route was PLANNED with, and the live one is a loop

This cost the session more than the original bug. `ScriptedBot` rebuilds
`NavQuery.CanRocketJump` from the **live** weapon and hp every tick, and firing
the rocket that starts a rocket jump puts the launcher straight onto its refire
cooldown. So the capability is false on the tick **after** takeoff, with the bot
already in the air on the arc — and re-resolving the hop there abandons an arc
already paid for at 30 hp.

**Measured on `avanti-ramp-pocket-to-intel`**: launch, re-plan mid-flight, land,
walk back, reload, launch again — **five times, 150 hp, and a leg that arrived in
175 ticks did not arrive at all in 600**.

`BotPathState.PlanQuery` records the traveller each route was planned for, and
the hop is resolved against that. The live query still decides everything it
should: the next *plan* uses it, and the takeoff gate re-checks
`RocketModel.Ready` before leaving the ground. What it must not do is change
which arc is being flown halfway along it.

#### What it bought, and what it cost

| pinned measurement | before | after |
|---|---|---|
| `BotObjectiveTests` intel run | 1,748 | **1,697** — re-plans 49→48, off-route 19→18, **blacklist 1→0** |
| `truefort-spawn-to-intel` | 1,717 | **1,667** — re-plans 57→48, **blacklist 1→0** |
| `avanti-ramp-pocket-to-intel` | 175 | **217** |
| the suite's rocket-jump total | 6 | **6** |
| `PathFollowerFixtureTests`' longest leg | pinned at 1,786/39/0/10/0 (item 4b-ii) | moved by the A\* weight 1.0 → 1.3 |

⚠️ **`avanti` got 42 ticks slower on purpose, and the trade is worth stating.**
Its node pair 231→168 carries a double jump beside a rocket jump; the bot used to
steer the double jump — for a soldier, an ordinary jump with no second press —
and get away with it. It now waits for the launcher, walks a safe route while it
reloads, and rocket-jumps once. **The old 175 was a bot flying an arc nothing had
costed.** The same fault cost 462 ticks on the intel run and hid the sticky-jump
arm completely.

⚠️ **The rocket-jump total being unchanged at six is the load-bearing number**:
the bot is not flying *more* arcs, it is flying the ones it was routed over.

#### What to remember

- ⚠️ **Anything that re-derives an edge from a node pair is suspect.** The
  blacklist and the occupancy counts are both keyed on the pair
  (`NavGraph.EdgeKey`) and that is fine — they mean to cover every parallel edge.
  Steering is the one that has to pick one.
- ⚠️ **A capability predicate evaluated at plan time and at steer time is two
  different predicates** whenever it depends on live state. Ask which one a
  decision belongs to before re-checking it.
- ⚠️ **Six correct measurements in a row can all be answering the wrong
  question.** Every sticky-jump number checked before this was right. The cheap
  move — print what the follower thinks it is doing — was never made, because the
  expensive checks kept looking like progress.

## ⛔ Dual King of the Hill is unwinnable by bots, on both shipped maps

✅ **Fixed** (below). This reproduces with the scripted bot on both sides, not
just a learned policy.

`dkoth_atalia` and `dkoth_sixties` produce **zero decided matches** at every time
limit tried — 5, 10, 20 and 40 minutes, 20 matches each — and the hold clocks
never advance by a single tick. `koth_harvest`, the single-hill control, reaches
the full 180-second hold and decides 20 of 20 at ten minutes.

**One 10-minute `dkoth_atalia` match, instrumented:**

| | on RED's hill | on BLUE's hill |
|---|---|---|
| red bot-ticks | **34,200** | **0** |
| blue bot-ticks | **0** | **34,200** |

Hill owner changes: **0**. Final timers: 5,400 and 5,400, i.e. the starting
values. Both hills unlocked after the 900-tick pregame, correctly.

### Why

**Dual KotH is won by holding the ENEMY's hill.** `Rounds.StepDkoth` runs red's
clock while `blue.Owned && blue.Team == Team.Red` — red must take blue's point.
Each team starts owning its own (`Rounds` sets that explicitly, and the cross-lock
depends on it).

`BotObjective.ControlPoint` sends every bot to **"whichever unlocked point is
nearest"**. In dual KotH both points are unlocked from tick 900 and each team's
own hill is the nearer one, so **every bot camps the hill that scores it
nothing** and neither team ever contests the other's. The bots play the mode
exactly backwards.

### ✅ FIXED 2026-09-01 — and `gg2-rl` supplied the distinction that made it clean

**`BotObjective.ControlPoint` now sends a non-defending bot to the ENEMY's hill
in this mode, and nowhere else.** After the fix: **19-20 of 20 decided at every
limit on both maps**, the full 180-second hold reached, and `koth_harvest`
unchanged at 14/20 and 20/20 — the mode gate means it cannot touch anything else.

⛔ **It is the one place this file reads `GameMode`, and the reason is a boundary
worth keeping**: **entity identification works while the objective is a property
of the LAYOUT, and fails when it is a property of the SCORING.** Two capture
points is an *identical entity signature* to a two-point Control Points map. What
differs is that `Rounds.StepDkoth` runs your clock while the ENEMY's point is
yours — and no amount of looking at entities can recover that, because the
entities are the same. dkoth is the first mode where layout and scoring come
apart, so this is the stance meeting the case it was never able to cover rather
than a violation of it.

⚠️ **Both entity-shaped alternatives were rejected for being right by accident:**

- **"Prefer a point we do not own"** fixes dkoth and breaks single KotH, where
  holding the hill IS the objective and attackers would abandon one they had just
  taken.
- **"Chase whichever point is making the enemy's clock run"** is a scoring-aware
  heuristic wearing entity clothing, and it has **no signal in exactly the state
  these matches sat in for ten minutes** — both clocks stopped. It would work in
  every match except the broken ones.

### ⚠️ The gate, and why the one this repo already had did not catch it

⛔ **`MatchActuallyHappensTests` already had a liveness test and it did not catch
this**, because it asserted the wrong liveness. `EveryBotGoesSomewhere` checks
that the bots MOVE — and in dkoth they moved, briskly, to their own hill. Every
bot went somewhere and the mode could not be won.

✅ **The rule, `gg2-rl`'s, in its sharpened form**: the rule is not "assert
liveness" — it is that **the signal must be one only this mode can produce.** A
shared signal is a liveness assertion that cannot fail for the reason you care
about. Movement is shared by every mode, so it cannot tell a working mode from
one being played backwards; the hold clock is the mode's own scoreboard and
nothing else moves it.
`MatchActuallyHappensTests.TheHillModesScoreboardMoves` asserts it, with the two
single-hill maps as the control so a failure on all four would be the harness
rather than the bug. Mutation-checked: it fails on both dkoth maps and passes on
both koth ones.

⚠️ **And the first version of that gate was VACUOUS.** It carried an
`|| Outcome != MatchOutcome.Running` escape clause, and the shared `Match()`
helper caps at 20 kills — so a broken dkoth match still *ended*, on kills, and
satisfied the assertion without the clock ever moving. **The mutation check is the
only thing that found it**, and it is the same "a filter is a way to pass by
discarding everything" shape this repo has shipped before. The test now builds its
own options with the kill limit out of the way and has no escape clause.

⚠️ **The shape is the Arena bug's.** A whole gamemode silently not working,
invisible because nothing asserted the mode *progressed* — `eval` reported instant
Arena rounds as clean wins for two phases. Here two of the 22 shipped maps have
presumably never produced a bot match that could end, and nobody noticed because
nothing ever asked. ✅ **It was found because `gg2-rl` insisted the exit criterion
be evaluated per map on every shipped map** rather than on the six that were
screened.


## ⚠️ The observation encoder lied twice, and neither lie could go red

Found 2026-09-03 by the `gg2-rl` lane auditing what the policy actually sees,
against `src/Gg2.Bots/ObservationWriter.cs`. Both bugs were silent by
construction, both had been shipping for the whole of phase 9, and **no test in
either repo could have caught either one** — which is the reason they are written
down here rather than left to the fixtures.

### `Stab` was encoded off the end of the vector

`ProjectileKindCount` was 9 and `ProjectileKind` has **ten** members, because
`None = 0` is one of them. Encoding the raw enum value therefore spent a column on
a kind that never occurs and put `Stab = 9` past the end of the field.

⚠️ **`OneHot` bound-checks, so the failure was a well-formed row of zeros.** A
spy's stab — genuinely spawned, `Weapons.Melee.cs:75` — reached the policy as
presence `1.0` with an all-zero kind one-hot. That is indistinguishable from an
encoder failure and unrecoverable downstream: nothing later in the network can
tell "a projectile of no kind" from "a kind whose column is missing".

✅ Fixed by dropping `None` from the one-hot and shifting the rest up
(`(int)shot.Kind - 1`), so all nine spawnable kinds own a column.
`ObservationWriterTests.EverySpawnableProjectileKindGetsExactlyOneColumn` pins it
as a **theory over every kind**, asserting exactly one set column — presence
without a kind is the shape worth asserting on.

⚠️ **The `Stab` fix by itself left the vector 209 wide, and that is the trap.**
(The commit that carried it also added the two refire columns below, taking the
vector to **211** — but that was a *separate* decision, and had it not been made
this fix would have shipped at an unchanged length.) A
same-length change to what columns *mean* is invisible to every length check and
to the observation length recorded in a checkpoint's own metadata. Only
`ObservationLayoutVersion` separates a v1 checkpoint from these columns, which is
why the bump was taken even though nothing about the size moved. **Ask of any
future encoder edit: would this be caught if the length did not change?**

### "nearest first" was creation order, and truncating

`ObservationWriter`'s comment said the four projectile slots were "nearest
first". `WorldView.ProjectilesWithin` walked the live list in creation order and
**stopped when the caller's span filled**, so with five or more in radius the
agent saw the four *oldest* live projectiles.

⚠️ **Truncation was the larger half, not the ordering.** A rocket fired a tick
ago could be absent from the answer entirely, and absence reads downstream as a
quiet situation — the encoder cannot say "and four more". The 400 px radius with a
four-wide span made that the common case in a firefight rather than a corner.

✅ Fixed in `ProjectilesWithin` itself: it now **picks** the nearest, ties keeping
creation order. `WorldViewTests` pins the ordering, the tie-break, and the
short-span case separately, because a test that only checks ordering passes on an
implementation that still truncates.

⚠️ **`SimulationBehaviourVersion` was bumped for this even though the pins did not
move.** Both `BotReflex` uses are *existential* — the first projectile satisfying
the predicate returns `true` — so their answers cannot depend on walk order, and
the set is identical while the span (32) is at least the number in radius. But
`MaxProjectiles` is 512, above 32 the set genuinely differs, and **nobody has
measured how often a firefight passes 32.** The stamp's rule is demonstration, not
argument, so it bumped. The 26 pinned bot tests passing is *consistent with* the
argument and is not the same as establishing it.

### What the pair have in common

Both were **claims in comments that no test could contradict** — the file said
"all nine of `ProjectileKind`" and "nearest first", and both sentences were false
where they sat. The encoder is exactly the seam CLAUDE.md's "a claim the repo
makes about itself" warns about: it is consumed by a *different repo*, so the only
reader positioned to notice was the one that acted on the description. It took an
audit from the `gg2-rl` lane, not a test run here.

### The two columns added in the same breath, and why that is on the record

The same 2026-09-03 change added **`ReadyToShoot` and a `RefireTimer` fraction**
to the self block, taking the observation from 209 to **211**. Ammo and reload
were already observed and refire was not, so a policy could not tell whether the
trigger it was pulling would do anything — and it is a stateless MLP, so it
cannot infer readiness from its own firing history either.

⚠️ **This was NOT measured, and it is written down that way on purpose.** No
result says refire visibility helps. It was added because the `Stab` fix was
already paying a full invalidation that week, which made it the cheapest possible
moment to close a known gap — a *cost* argument, not an evidence one. It is the
same mechanism-without-a-direction shape that the **sniper's charge was declined
on**, and the asymmetry is only that this touches every class in the trained set
rather than one. If a later measurement shows it buys nothing, that is not a
surprise and not a reason to keep it.

⚠️ **Both, because neither implies the other.** `Weapon.events/Create.xml` starts
`ReadyToShoot` false with `RefireTimer` already running, so a zero timer is not a
weapon that can fire; and a flag alone throws away "how soon".

### ⚠️ The trained class set moved and no stamp can say so

Sniper was dropped and demoman added the same day:
`PolicyController.TrainedClasses` is now `[Scout, Soldier, Demoman, Heavy]`, so
the untrained class columns are **2, 4, 5, 7 and 8** — still interleaved, and
still not a prefix.

⛔ **The class set is not on the stamp surface, so a checkpoint cannot tell you
which classes it learned.** A checkpoint from before this date learned a sniper
and never saw a demoman; `SimulationStamp.Require` will happily accept it once
the stamps line up. **A stamp match is not evidence that a checkpoint knows the
classes you are about to drive with it.**

⚠️ The two tests that would confirm the switch end to end —
`PolicyMatchTests.ARestrictedMatchContainsOnlyTrainedClasses` and
`AnUntrainedClassIsNeverDrivenByThePolicy` — both load the fixture checkpoint, so
they were **red for an unrelated reason when the change landed** and did not
validate it. Re-check them deliberately once a v2 fixture exists rather than
assuming a green suite covered it.

### ⚠️ The fixture became VALID, and that broke tests built on it being stale

2026-09-03, immediately after the layout v2 bump. `gg2-rl` minted a fixture
against this build, and it was **the first checkpoint ever genuinely valid
here**. Three tests went red, all correctly, because they were quietly built on
the shipped file being refused:

- `TheCheckedInCheckpointIsRefusedWithoutTheOverride` asserted the shipped file
  **is** refused. ⛔ **Premise gone, not merely changed** — retired rather than
  adjusted, and replaced by `TheCheckedInCheckpointIsAcceptedWithNoOverride`.
  **Retiring a refusal test whose premise has ceased to exist is not weakening
  coverage**; leaving it green by re-staling the fixture would have been.
- `AForeignStampIsRefused` and `ACheckpointGivenToAModeThatCannotUseItIsRefused`
  needed a *valid* checkpoint to corrupt, which they now have honestly instead
  of by minting one.

✅ **`PolicyFixture`'s guard fired on the one event it was written for.** It
hard-coded the trained stamp "so that swapping the file re-opens the question"
and threw *"the checkpoint has been replaced, so `PolicyFixture.TrainedStamp`
needs updating along with whatever else that replacement implies"* — months
later, in a session that had never read it. That is what a guard written for a
future reader looks like when it works.

The minted-copy machinery, `TrainedStamp`, and `Load`'s `ignoreSimulationStamp`
were **deleted**. Every caller now goes through the real gate, so the suite
exercises the refusal instead of routinely bypassing it.

### ⛔ A test's hard-coded class list is a copy of `TrainedClasses`

`PolicyMatchTests.AnUntrainedClassIsNeverDrivenByThePolicy` drove
`[Demoman, Medic]` and asserted no bot ever gets a policy tick. When demoman
joined the trained set it was asserting that a **trained** class is never driven
by the policy — and a demoman correctly took 36 policy micro-ticks.

⚠️ **It could not report that at the time.** The fixture checkpoint was
unloadable, so this test was red for an unrelated reason across the whole window
in which the class set changed. **A test that is already red gates nothing**, and
a change landing behind one is unreviewed however green the suite looks
afterwards — the same shape as a filter that passes by discarding everything.

⛔ **A THIRD DIRECTION, and it is the worst of them.** This repo already tracks
two ways a check stops checking: it can pass for a reason unconnected to its name
(the vacuous dkoth gate, the Arena rounds), and it can be unsatisfiable by the
right evidence. **A check can also FAIL for a reason unconnected to its name**,
and that one hides best — a red test reads as a known problem on its way to being
fixed, so nobody asks what it stopped covering while it sat there. A green suite
gets audited; a red test gets a ticket. **When a test is red for an unrelated
reason, everything it used to gate is unguarded until it is green again** — so on
fixing one, look at what changed underneath it rather than just at the fix.
Named by the `gg2-rl` lane, which hit the first shape and this one on the same
day from opposite ends.

✅ The test now asserts its own premise (`PolicyController.IsTrained` is false
for each class it drives) and says what to do if it fails: pick classes that are
actually untrained rather than deleting the assertion. **A hard-coded list
duplicating a constant elsewhere needs something keeping the two in step, or it
is a stale copy waiting to happen.**

✅ The 36 micro-ticks were also the only positive evidence the class switch took
effect at all — every other check was a negative.


### ✅ The first policy to improve deaths AND the objective at once

**2026-09-06, measured by the `gg2-rl` lane, pinned at `c434229`.** 168
map-runs, **67,200 matches**: 21 maps × 200 an arm, four seeds a family, 150px
engagement window. The roster of each run was read off all eight runs rather
than assumed.

**Cleared to `6126b34` over the whole dependency closure, not the stamp** (see
the next section for why the stamp is not enough): `src/Gg2.Bots` has **0
non-comment changed lines** across the range; `Gg2.Env`, `Gg2.Maps`, `Gg2.Nav`,
`nav/` and `maps/` do not appear in the diff at all; `tools/bench`'s only
non-comment change is the new `Smoke.cs` plus an 8-line `BENCH_SMOKE` guard
that returns early. **Q21 ran `BENCH_SWEEP` → `RangeSweep.Run`** (`Program.cs`
line 178; `BENCH_PERMAP` is a different mode it never invoked), and
`RangeSweep.cs` is untouched across the range. The 64 non-comment lines in
`src/Gg2.Server/BotBrain.cs` are **unreachable from the bench**: the project
graph is `bench → {Gg2.Env, Gg2.Bots, Gg2.Simulation} → {Gg2.Protocol,
Gg2.Maps} → Gg2.Protocol → nothing`, so no path reaches `Gg2.Server`.
⚠️ Verified **to the leaves**, not at the first edge — "bench.csproj does not
reference `Gg2.Server`" is a statement about one file, and a closure is a
graph.

| checkpoint | policy ticks | death share | Δ | 2·SE | t | round wins | Δ | 2·SE | t |
|---|---|---|---|---|---|---|---|---|---|
| `rest3` | 5.4–6.1% | 50.63 → 47.77/46.85/48.28/48.33 | **−2.82** | 0.69 | **−8.22** | 53.19 → 56.09/59.04/53.85/55.84 | **+3.02** | 2.14 | **+2.83** |
| `control` | 4.1–4.4% | 49.90 → 50.44/48.72/49.72/48.91 | −0.45 | 0.79 | −1.15 | 59.70 → 57.56/58.44/56.60/58.39 | **−1.95** | 0.86 | **−4.51** |

`rest3` passes both criteria with **all four seeds agreeing in sign on both
measures**. The failure mode of the 450px widening — combat improving while the
objective collapses — is simply absent at 150px and 5–6% of ticks.
⛔ **`control` fails, and worse than a trade**: Q14's widening at least bought
combat for objective. This is an objective loss with no purchase, all four seeds
negative on wins, and one seed's death share *above* its own control.

✅ **A free determinism check came with it**: within each family all four runs
report the CONTROL arm to the digit (49.90/59.70 and 50.63/53.19), across a day
in which the bench was rewritten three times. That is what allowed the one
surprising cell (`control`'s mixed death share) to be attributed to the
checkpoint rather than to the harness — the instrument is a candidate before the
policy.

⚠️ **THE TWO FAMILIES ARE NOT COMPARABLE TO EACH OTHER, and their control arms
differ for a structural reason** (50.63/53.19 against 49.90/59.70). The roster
follows the trained set, so a `rest3` run is a three-class match and a `control`
run a four-class one — different matches, not two arms of one. **Each family is
only ever judged against its own control**; ranking `rest3` above `control` from
these numbers is not a comparison this instrument can make.

### ⛔ "Should drive" cannot be priced by this bench — it is structural

⚠️ **`AllowedClasses` and `PolicyController.Drives` read the SAME set**, so the
roster on the field and the classes actually driven cannot be varied
independently. Dropping engineer from `rest3`'s set to price its inclusion would
also **remove engineers from the match** — a different match, not a cleaner
comparison.

Three consequences, all measured-by-implication rather than open questions:

- The engineer question this repo raised (`rest3` mirrors 50.67% against a 50%
  null, 2·SE 1.74 — nothing to choose against the scripted engineer) is
  **unanswerable by this instrument**, and that is the answer. Absence of a
  price, not evidence that inclusion is free.
- **A single tier's contribution inside a mixed nine-class match is
  unmeasurable** for the same reason, and so is a three-tier union, since the
  bench loads one checkpoint.
- Therefore **a should-drive field could not be justified by a match number
  today**, whatever one's view of whether it should exist. The training lane
  states it is *not* asking for the seam change that would be needed.

⛔ **This is a statement of what the instrument can do, not a complaint about
it**: the bench was built to compare one policy against scripted, and it does
that well — see the section above, which is exactly that comparison.

⚠️ **Two registered predictions were WRONG here, and both being visible is why
the result is usable.** The training lane registered that `control`'s tier was
"sniper alone within a four-class roster" — but `Drives` reads the whole trained
set, so it drove scout, soldier, sniper AND heavy; the loss belongs to any of the
four and **says nothing about a sniper tier**. It also registered that `rest3`'s
win share might be degenerate for want of a flag-runner in its roster; over 21
maps it is not — the arena and koth maps score, and the co-primary resolved at
t +2.83. **Being wrong there was worth more than being right**: it means the
objective was visible, so a null would have been a real null rather than an
instrument floor.


### ⛔ A stamp chain is not a clearance: `SIM_UUID` is blind to `Gg2.Bots`

**2026-09-06.** Clearing a `gg2-rl` number to a newer commit, this repo checked
`git diff c434229..6126b34 -- src/Gg2.Simulation src/Gg2.Env/ObservationWriter.cs`,
found it empty, and concluded the number described the current build. The
conclusion was right and **the evidence did not cover the question**: the bot
layer had moved in that range (`PolicyController.cs` +9, `PolicyNetwork.cs`
+20) and the check never looked there.

⚠️ **`SIM_UUID` deliberately does not cover `src/Gg2.Bots`** — a policy is a
bot and this project's bots are its own — so **no stamp agreement can clear a
bot-layer change**, and a clean `Gg2.Simulation` diff says nothing about it.
That is the same coverage gap that made `RocketModel.Capable` need a re-score
with no stamp moving.

✅ **In this instance nothing was owed**: every changed line in both files is a
comment, so no executable line moved. **But that was established by filtering
the diff, not by characterising it.** "Probably just the comment I added" is
the reasoning both lanes lost time to on the same day.

⛔ **The counterfactual is the whole value of the check.** Had those twenty
lines been code, the check as originally run would have returned empty *on the
paths it looked at* and cleared a number it should not have — and the
conclusion would have been stated with exactly the same confidence. **An
incomplete check and a sufficient one are indistinguishable from their
output**; only the closure they cover tells them apart.

**The rule, in order.** Widen to the layer the stamp cannot see — for a bench
number that is `Gg2.Bots`, `Gg2.Env`, the maps and nav, and the bench mode
itself, plus a check that anything else which moved is not in the instrument's
project closure. Then **read** the diff rather than characterise it. Named by
the `gg2-rl` lane, which caught it in this repo's own clearance.

⛔ **It took THREE passes to state that rule, and each pass was a strict subset
of the next — which is the section's real content.** This repo checked what the
SIMULATION runs on. `gg2-rl` widened to what the POLICY runs against
(`Gg2.Bots`) and stopped, never looking at `tools/bench` — **the thing that
actually produced the number**. This repo then widened to the instrument but
named the wrong mode in the write-up (`PerMap`; Q21 ran `BENCH_SWEEP` →
`RangeSweep`), a wrong supporting sentence under a right conclusion, sending
any reader who followed it to get a clean answer about a mode the number never
used. **The settled form: for a number produced by an instrument, the closure
is the INSTRUMENT'S project graph — not the stamp surface, and not one layer
past it.**

⚠️ **A textual match is not a dependency, in that direction too.** Checking the
closure by `grep -l "Gg2.Server"` over the csproj files hits three of them —
all XML comments stating the dependency is *deliberately absent*. A grep that
would have reported the opposite of the truth, caught only by reading the
lines. Same rule as reading the diff, pointing the other way.

### ⚠️ A stale "impossible" is worse than a stale "done", and nothing falsifies it

The two stale claims Q21 broke ("NO trained checkpoint exists for this build",
"nobody is making one") were stale in the **blocking** direction, and that is
the more expensive one.

**A stale "done" gets falsified the moment somebody tries the thing.** A stale
"impossible" never does — nobody tries, so no contact with reality is ever
made, and the claim can sit for as long as the project lasts getting more
confident-looking with age. Here it sat three days telling every session that a
behavioural re-measure was blocked when the checkpoints had already been minted
and checked in *to this repo* as fixtures.

⚠️ So `CLAUDE.md`'s "grep before writing a claim about what exists" applies
**hardest to the claims that close work off**, not to the ones that advertise
it. When writing "none exists", "not possible here" or "nobody is doing this",
ask what would ever bring somebody into contact with the fact — and if the
honest answer is nothing, check it by hand before writing it down.


## The shipped checkpoints and the class-routing settings

`checkpoints/` holds two checkpoints driving five of the nine classes;
`checkpoints/README.md` has the files, the measurements and their limits, and
`docs/BOTS.md`, "the shipped bundle", is the operator-facing version. What
belongs here is the part that is a trap.

### A should-drive set lives in a trained-on field

`tier2_pyro_rest3_s0.npz` says `PYRO` in `meta[7]`. The run trained
`ENGINEER,SPY,PYRO`. A checkpoint has one class-set field answering two
questions — what the run trained, and what its author intends it to drive — so
the file is true about the second and incomplete about the first, and nothing in
either repository can distinguish them.

Consequences:

- `PolicyNetwork.TrainedClasses` cannot be read as "what this learned". It is
  what the file claims it should drive.
- `[Bots] ClassPolicy` may name a class the file does not list, and that warns
  rather than being refused. Refusing would make it impossible to drive engineer
  or spy with `tier2_pyro`, which did train them.
- The warning cannot say which case it is. Only someone who knows the checkpoint
  can.
- `gg2-rl`'s `ship_tier.py` refuses to widen a set, so the convention cannot make
  a policy claim a class it never trained. That guard is entirely on the
  producing side and nothing here can check it.

Delete this section if `gg2-rl` adds a second metadata field, rather than editing
it.

### Which classes bots play and who drives them are separate settings

`Bots.Classes` restricts the class draw. `Bots.ClassPolicy` assigns a class to a
tier or to `scripted`. They must stay separate: a setting that answers both makes
one of the two unreachable, and the unreachable one is silent.

`Bots.Classes = driven` cannot be resolved where it is read, because options are
parsed before any `--policy` is opened and argument order is not guaranteed.
`BotPopulationOptions.ClassDrawFollowsPolicy` carries the request and
`BotBrain.Build` answers it against the loaded tiers. The general rule: **a
setting that cannot be resolved where it is read must defer, not approximate** —
a constant standing in for an unavailable answer decays silently, because nothing
goes red when the real answer moves away from it.

The failure mode this protects against is a roster that is a smaller but
plausible subset of the right one. Every bot it draws is driven correctly, so
there is no error, no missing class in any log line and no failing test; a tier
is simply inert. Unlike a stranded bot or a bot on the wrong hill, it is only
visible if you count.

`Bots.Classes` reads back as `driven` rather than as the classes it resolved to,
so a save from the settings page does not pin one startup's answer into the file.
The resolved list goes in the startup log, alongside one line per checkpoint
naming the classes that checkpoint will drive.

### Rules the resolution follows

- The default is every checkpoint driving the classes its own file claims, so a
  server that configures nothing is correct. `ClassPolicy` only names overrides.
- An explicit assignment beats the file in both directions. `Class=scripted` is
  the only way to drop a class a checkpoint claims without re-minting the file.
- An explicit assignment also removes the class from every other tier, so it
  resolves a tier overlap rather than adding to one.
- Two tiers claiming the same class warns: list order then decides which weights
  play it, and `HybridBot.Choose` takes the most capable eligible tier.
- A tier left driving nothing warns. It is loaded, stamp-checked, reported and
  never consulted, which is the defect the arbiter invariant refuses in its own
  case; it is not refused here because turning a tier off to see what the rest do
  is a real experiment.
- `driven` with nothing driving is refused. The empty set would read as "no
  restriction", which inverts the request.
- A label naming no loaded checkpoint is refused, with the loaded names in the
  message. A class assigned twice is refused rather than last-wins.

`PolicyMicroSpec` and `PolicyController` take the class set explicitly. A null
set asks the checkpoint; an empty set means the tier drives nothing. Conflating
those would silently re-enable every class an operator turned off.

## ⛔ Everything derived from a bot's team is derived one tick too early

A map change wipes the roster before it re-roles the bots, so anything the
population manager takes *from* a team is taken while every team reads
`Spectator`. `Rounds.ResetForNewMap` runs `with(Player) team = TEAM_SPECTATOR`,
`OpenMap` calls `BotPopulationService.MapChanged` in the same tick, and
`Reteam` — which puts a spectating bot back on a team — does not run until the
next one.

The stale field that matters is `ScriptedBot.Query.Team`.
`NavGraph.GatePassable` refuses **both** team gates to a spectator, so a bot
whose spawn-room exit is gated has no route out of it at all. It keeps
targeting correctly (`BotObjective` reads `world.Player(slot).Team` live), so
it wants somewhere it cannot plan to, and stands still for the whole map.
`Role` goes stale in the same tick and more quietly: `Retarget`'s
`earlierTeammates` filter is `TeamOf(other) == team`, so with the roster merged
into one `Spectator` group every bot counts *both* teams' lower slots and the
attack/defend mix is drawn from a list twice the right length.

Measured on `00ee10b`, twelve bots, a control-port map change
`ctf_truefort` → `ctf_orange`, sampling positions ten times at 200 ms:

| | bot-samples that moved |
|---|---|
| before the change | 80/108 |
| after the change | 15/108, and 0/108 once settled |
| after any bot-count change | 99/99 |

That last row is the whole of why the bug looks intermittent. Nothing
re-derives the team-dependent state on its own, but `Service` calls `Retarget`
whenever the population moves — so a human joining or leaving, or a bot count
set from the panel, un-freezes every bot at once, minutes later, with no
apparent cause.

The fix is in two places and both are load-bearing:

- `ScriptedBot.Poll` rebuilds `Query.Team` from `self.Team` every tick,
  alongside the class arcs it already rebuilt there. This is the routing half,
  and it also covers the case with no map change in it: the autobalance moves a
  bot across mid-round and nothing told its query.
- `BotPopulationService.Service` calls `Retarget` when `Reteam` actually placed
  somebody. `Reteam` reads the team back rather than assuming, because
  `RequestBotTeam` runs the reference's balance guard and refuses a request for
  the bigger team in silence. It cannot be folded into the count check below
  it — a rotation leaves `bots == target` true, which is exactly the branch that
  returns early.

`BotPopulationTests.ABotRoutesAsItsOwnTeamAfterARotation` is the gate, and it
asserts the routing consequence rather than the assignment: the bot's roster
team and `Query.Team` after a rotation. What would show this has gone stale is
a second field derived from the team at add-time and never refreshed — the
existing ones are `Role`, `SpreadX` and `Query`, and only `Query` is on the
per-tick path.
