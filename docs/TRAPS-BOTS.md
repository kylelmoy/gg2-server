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
- ✅ **The jump branch of the follower IS ported** — item 4b-ii, landed in
  `c34549b`, and the rocket jump in `55692be`. This bullet used to say it was not,
  and so did the item's own section below it, **while that section documented the
  implementation two hundred lines further down the same file**. Corrected
  2026-08-28, after a session took the queue item it had spawned and found the
  work done. ⚠️ **A stale "not done" costs a session exactly what a stale "done"
  does**, which is the rule `CLAUDE.md` states for the sandvich in the other
  direction. The only genuinely unhandled edge kind is `NavEdgeType.StickyJump`,
  and **all 24 shipped nav graphs contain zero of them** (measured 2026-08-28:
  Walk 18,785, Fall 11,558, Jump 67,013, DropThrough 5, DoubleJump 2,382,
  RocketJump 16,014, StickyJump 0, MoveBox 0).
- ✅ **Team Deathmatch has a goal as of 2026-08-28** — `BotObjective.Deathmatch`,
  a spot anchored on the nearest enemy. `tdm_mantic` went from eight bots moving
  zero pixels to a real match, and team clumping went DOWN rather than up.
- ⚠️ **Conversion at 6v6 is 8%**, and nothing establishes whether that is
  *right*. There is no human baseline in this project.


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
- **Re-measured 2026-08-27, after the seam landed: all five closed-loop windows
  unchanged** (357 / 1,204 / 756 / 489 / 328). Expected — the parity replayer
  calls `MatchSimulation.Tick` directly and never goes through `GameServer` —
  but checked rather than assumed, because "a window that moves here is a bug in
  the seam" is the only early warning phase 7 has.

### Phase 7 item 2: the nav cache reader, and a format that is not the obvious one

`src/Gg2.Bots/` — `NavCache`, `NavGraph`, `NavTypes`. Gated by `NavCacheTests`
(built bytes) and `NavGraphFixtureTests` (all 24 real caches). The reference is
`Scripts/BotNav/`; `gg2-agent/tools/navgraph.js` documents the format.

- ✅ **The `NAV_*` and `BOT_*` constants were already ported** —
  `Gg2.Protocol.{NavConstants, BotConstants}`, pinned to `Constants.xml` by
  `ConstantsTests`. This session nearly transcribed them a second time. **Check
  `Gg2.Protocol` before transcribing anything from `Constants.xml`.** The one
  number that is genuinely not there is the 23-pixel feet offset, which is a
  literal in the GML; it lives on `NavGraph.FeetOffset`.
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
- ⚠️ **The caches live in `Source/build/botnav/`, which is gitignored in the
  reference and is not shipped.** A fresh checkout has none, so
  `NavGraphFixtureTests` says that rather than failing as if the tree were
  missing (`ReferenceTree.RequireNavCache`). All 24 present on this machine load.

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

### Phase 7 item 4a: A* over the nav graph

`Gg2.Bots.{NavSearch, NavQuery}`, `NavGraph.EdgeKey`. Gated by `NavSearchTests`
(hand-built shapes) and `NavSearchFixtureTests` (all 24 shipped graphs). The
reference is `Scripts/BotNav/navFindPath.gml`, whose header is the best
documentation in the bot layer — read it before changing anything here.

- ⚠️ **A closed node is RE-OPENED when a cheaper route arrives, and that is what
  makes the search optimal rather than merely fast.** Skipping an already-closed
  neighbour is only sound when the heuristic is *consistent*, which is strictly
  stronger than the admissibility straight-line distance gives. Measured in the
  reference on honest costs: the ctf_truefort blue leg went 68 nodes → 60.
- ⚠️ **The gate test is on the EDGE and never on the node it arrives at.** An
  arc can cross a gate without landing on it and the generator stamps such an
  edge. **Measured 2026-08-27 across all 24 graphs: 115,757 edges, 1,350 stamped,
  148 gated nodes, and ZERO edges landing on a gated node unstamped** —
  `NavSearchFixtureTests.NoEdgeLandsOnAGatedNodeUnstamped` is what notices if a
  generator ever stops. ⚠️ `NavGraph.EdgePassable` checked the landing node too
  until this landed, because it was written from
  `gg2-agent/tools/navgraph.js`'s `reach`, which is an audit tool and
  deliberately conservative. **The game is the specification.** The rule this
  preserves: a bot standing inside a gate it may not pass still gets edges *out*.
- ⚠️ **Refusing a class arc is the DEFAULT.** `CanDoubleJump`/`CanRocketJump`
  default to false, so a hand-issued query gets the conservative answer — "the
  failure mode of the opposite default is a Heavy routed over an arc it
  physically cannot fly". ⚠️ `CanRocketJump` is **not a property of the class**:
  it is the *weapon* plus an hp test (`hp > NAV_RJ_DAMAGE`, strictly greater,
  because the damage lands before the bot does), so a soldier's routes shed their
  rocket jumps on their own as it takes damage.
- ⚠️ **The blacklist is a set, never a cost multiplier** — a multiplier would
  break the heuristic's admissibility. It is per bot, which is why it is a
  parameter rather than part of the graph.
- ⚠️ **`occupancy` is the only safe way to perturb a cost, and a per-edge random
  jitter is not.** The jitter was deleted from the reference: it is
  mean-reverting, so over a sixty-edge route every candidate inflates by the same
  average and on ctf_truefort all eight bots got the *identical* 68-node route.
  Occupancy works because it is structured, shared and coarse. **Do not re-add
  the jitter.**
- ⚠️ **A fixture for a search must be admissible or it is a different search.**
  `NavSearchTests.AssertAdmissible` checks every fixture edge costs at least the
  straight line it covers, because a cheap edge to a distant node makes the
  heuristic overestimate and "the cheapest route wins" then passes or fails for
  reasons that have nothing to do with the code. Two fixtures in the first draft
  violated it.
- ⚠️ **And a re-open fixture has to make the re-open actually fire.** It is not
  enough that a cheaper route exists — it must arrive at an *already closed*
  node, which needs the near-goal node reached expensively but popped early, the
  far node reached cheaply but popped late, and the goal expensive enough from
  the first that the second pops before it. The first draft got the right answer
  without the re-open and passed with it deleted. `NavSearch.Reopens` is now
  asserted so it cannot silently stop exercising it.
- **Two things are equivalent-by-construction and are recorded rather than
  gated**, both checked by mutation: the goal test's position relative to the
  closed test (the goal is never closed when first popped), and the
  reconstruct's "did the chain reach the start" guard (every `cameFrom` write is
  paired with a `gScore` write). Both are kept because the reference keeps them.
- **`NavSearch` is an instance and its scratch is reused**; the reference
  allocates four `ds_grid`s per query. ⚠️ **Not thread-safe on purpose** — one
  search at a time per instance, or two frontiers interleave into one `gScore`
  and produce routes that are wrong rather than slow.

### Phase 7 item 4b-i: the path follower's spine, and the ground edges

`Gg2.Bots.{PathFollower, BotPathState}`. Gated by `PathFollowerTests` (decisions,
physics taken out) and `PathFollowerFixtureTests` (a real leg walked on
`ctf_truefort` through `ScenarioWorld`). The reference is `botPathKeys.gml`
(1,014 lines, 394 of code) and `botPathPlan.gml`.

✅ **The jump branch IS ported** — item 4b-ii, below in this file. This paragraph
said the opposite until 2026-08-28, contradicting its own section.

✅ **`NavEdgeType.StickyJump` is handled**, landed 2026-08-28 — see "The
sticky-jump follower arm" below. `FollowEdge`'s `default` arm now covers nothing
the generator emits, and still counts rather than throws so a graph from a newer
generator degrades into `BotPathState.UnsupportedEdgeTicks` instead of silently
flying an arc nobody modelled. **Do not paper over a new kind with an approximate
jump**: an arc the generator did not validate is exactly what the whole takeoff
apparatus exists to prevent. ✅ **No shipped graph contains a type-7 edge** —
measured across all 24 caches in `Source/build/botnav` — and `NavSearch` refuses
one to anybody without `NavQuery.CanStickyJump`, which the reference's own A*
cannot do.

- ⚠️ **The route index advances only while the bot is ON THE GROUND**, and this
  is the single most expensive bug in the reference's follower. `NodeFromWorld`
  tolerates being a couple of cells off a surface, so an arc passing over its own
  landing resolves ~15px before the feet arrive — and advancing there hands the
  follower the *next* edge mid-air. Measured on ctf_truefort's two stacked jumps:
  the bot took off, was switched onto the return leg at the apex, landed back
  where it started, and did it again.
- ⚠️ **Arrival needs the bot stopped, not merely close.** GG2 keeps horizontal
  speed for ~10 ticks after the keys come off, so arriving on position and then
  pressing nothing coasts 30-40px past the target. Inside the band the bot
  *brakes* — a press against its own motion — and the speed floor of 1 is what
  stops that becoming a press back the other way.
- ⚠️ **Landing off the route re-plans IMMEDIATELY** rather than riding out
  `BOT_OFFPATH_TICKS`: standing where the route does not go is a finished move
  that went somewhere else, and waiting to admit it is how a bot walks somewhere
  it cannot get back from. **Not knowing where you are at all** is the different
  case and does get the timer.
- ⚠️ **But the edge is blacklisted only on the SECOND consecutive miss.** A lot
  of real edges are marginal and flown stochastically; banning one on its first
  miss cost the reference ~250 frames to complete a 30-tick hop.
- ⚠️ **A failed search KEEPS the old route**, and the search runs before the old
  route is given up. This was the reference's "bots momentarily stop moving"
  report, and it was structural rather than occasional. The failure then parks
  the next search for a re-plan window, or every event-driven caller fires again
  next tick — a full A* per bot per frame for a goal that is unreachable from a
  pixel away too.
- ⚠️ **A stale route never declares an arrival.** It ends where the bot was sent
  *before*; declaring one would clear `HasGoal` and throw away the objective just
  given.
- ⚠️ **A fall reads its takeoff end off the edge and cannot re-derive it.** "The
  column just past the run is inside the landing node" is true of BOTH ends
  wherever the landing reaches under the whole run, and guessing picked the
  right-hand end whenever it was ambiguous — which on ctf_avanti wedged a bot
  against a solid block for ~1,100 ticks on a leg priced at 545. **And in the air
  the plan is a straight vertical drop from that column**, which the bot holds;
  without it a fall is the one edge kind flown with no plan at all.
- ⚠️ **The moved test is half a pixel, not zero** — `good_move_contact_solid`
  works at eighth-pixel precision, so a bot grinding a wall jitters without going
  anywhere. And the stuck test is gated on having pressed something, or a bot
  standing on its target waiting for a jump window reports itself stuck.
- ⚠️ **A ban already imposed is not re-armed.** The reference exits on the
  existence test, so a ban runs from when it was first imposed; without that a
  bot failing the same edge repeatedly extends it indefinitely and the blacklist
  stops being a delay.
- **The anti-thrash check lives with the planner**, not the stuck detector: it
  catches the bot that is technically moving — shuffling across a ledge — and
  getting nowhere. ⚠️ It reads the edge in progress, which the follower
  re-establishes every tick and a successful plan clears; a test that plans twice
  without stepping finds nothing to blame.

⚠️ **Two testing traps this cost, both worth knowing before writing a follower
test.** A fixture where the bot can get *back* to its route makes a second
consecutive off-route miss unreachable — the re-plan simply installs a route
starting where it landed, which is correct behaviour and no gate. And "there
exists a walk-only way there" is the wrong question for an end-to-end leg: the
follower is handed `NavSearch`'s route, and the search costs a jump like anything
else, so it prefers one across legs a bot could have walked.
`PathFollowerFixtureTests.FindWalkableLeg` proposes candidates and accepts the
first whose *planned* route is steerable end to end.

### Phase 7 item 4b-ii: the jump branch, and a budget that could not be spent

`Gg2.Bots.{JumpModel, PathFollower}`'s jump case, `BotPathState`'s per-arc
fields. Gated by `PathFollowerTests` and `PathFollowerFixtureTests`. The
reference is `botPathKeys.gml:408-1014`, `botJumpReach.gml` and
`botRunupCol.gml`.

✅ **A bot now crosses `ctf_truefort` end to end.** Re-measured after item 4f:
the longest steerable leg on the map is **137 nodes**, and the bot arrives at
tick **1,572** with 33 re-plans, **0 stuck fires**, 4 off-route events and **0
blacklists**. The reference quotes 2,200-2,500 ticks "with no blacklists now" for
its own *shorter* `truefort-spawn-to-intel` leg, so this port is comfortably
ahead of it on a longer route.

⚠️ **It read 2,655 / 76 / 34 / 9 when this section was written, and this file
recorded that gap as "misses more marginal arcs — watch it when the takeoff
machinery is next touched". It was not the takeoff machinery.** It was a single
`(int)` cast in `NavCache` truncating the velocity every arc is flown at — see
item 4f. **A behaviour layer that looks subtly wrong everywhere is a reason to
check what it is reading before tuning it.**

⚠️ **RocketJump is still not flown.** `NavQuery.CanRocketJump` defaults false so
the search does not offer one, and the follower counts it in
`UnsupportedEdgeTicks` if it ever sees one. It needs the combat half
(`botRocketAim`/`botRocketFire`) and a live loaded Rocketlauncher.

- ⚠️ **BOT_TAKEOFF_PATIENCE (60) is unreachable at the default re-plan stagger,
  and nothing in the reference remarks on it.** A successful re-plan calls
  `botPathFree`, which sets `botEdgeFrom = -1`; the follower's change-of-edge
  test then zeroes the takeoff budget on the very next tick. So a bot's *re-plan
  interval* is a hard ceiling on its takeoff budget — and
  `botPathPlan.gml:53` makes that interval `BOT_REPLAN_TICKS + (player mod
  BOT_REPLAN_TICKS)`, i.e. **45 to 89**. At a stagger of 0 the interval is 45,
  60 is never reached, and the escape hatch is dead — exactly as it was dead
  before the reference started incrementing the counter at all. **Found by the
  test failing.** `BotPathState.ReplanStagger` carries the offset; it is added,
  not phased.
- ⚠️ **`botPathFree` does NOT reset the takeoff budget** (it resets the run-up).
  The port briefly did and it is wrong — though only visibly so because of the
  ceiling above.
- ⚠️ **The takeoff column is carried ON THE EDGE because the generator searched
  for it.** Usually the end of the run facing the landing — but where the landing
  surface is also what ends the run (climbing onto a crate) the body is flush
  against it there and the only flyable arcs start a few cells back. Re-deriving
  it turns those edges back into fictions.
- ⚠️ **An arc is flown as a TRAJECTORY, not as a speed.** The target is
  `takeoffX + dir × min(distance, needVx × airTicks)` — an absolute world x, and
  **clamped at the arc's own distance** so a late bot keeps pressing toward the
  landing rather than aiming past it. Measured in the reference: **12-19% of
  jumps landed on the node they were aimed at before, 100% after.**
- ⚠️ **The gate's speed test is `JumpModel.Reach`, not "at the arc's speed".**
  `along >= 0` accepts a standstill, which is right for **91.9% of jump edges**
  and catastrophic for the rest: **3,310 of 62,069 are unflyable by a heavy**
  from a standstill, and on koth_corinth 96% of a heavy's routes cross one.
  Demanding full speed was tried and lost — 647→868 and 674→997 ticks on the
  valley scenarios with no more arrivals.
  ⚠️ **`Reach` is not monotonic in the start speed** (3.50 clears, 3.75 and 4.00
  fail, 4.25 clears again) because arriving *ahead* of a constant-velocity
  schedule makes the tracker brake. Do not bisect it for a threshold.
- ⚠️ **The gate measures the gap from the takeoff COLUMN, never from the bot's
  x.** The tracker clamps at `needVx × flightTicks` from the anchor, so that is a
  ceiling on how far the bot can be steered; asking from x — up to
  `BOT_JUMP_LEAD` cells further out — asks for 22px of an arc that can only
  deliver 12, and refuses on every approach *silently*: nothing blacklisted,
  nothing off route, because the bot is doing exactly what it was told.
  The **credit** for ground already covered inside the column is only ever a
  credit, never a debit, clamped to one cell.
- ⚠️ **The run-up follows WALK edges through the GRAPH, not the route or the
  node's own columns.** Clamping into the node's span is a no-op on a
  one-column node — which is what the top step of a staircase is — so the jump
  happened from a standstill anyway. **3,310 unflyable edges → 253, koth_gallery
  105 → 0.**
- ⚠️ **A run-up walks BACKWARDS along the route**, and the forward-only advance
  cannot see it — so the locate step suppresses the off-route branch while a
  run-up is live, and `PathAt` must NOT move.
- ⚠️ **The second impulse is a PRESS, not a hold**, fired once at the tick the
  generator proved (`NAV_EDGE_REJUMP`), and gated on `CanDoubleJump` **even
  though the search will not offer these edges to anything else** — a route
  outlives the plan that made it.
- ⚠️ **`CharacterPhysics.ControlAndFriction` is public now** because the reach
  model has to read it live: Begin Step sets it from `moveStatus` and **not**
  from `onground`, which is the whole reason an in-flight tracker can correct a
  slow takeoff.

⚠️ **A testing trap this cost twice: a fixture arc must cover its own gap.** A
generator would never emit `0.5 px/tick × 20 ticks` across an 18px gap, and the
gate correctly refuses it — which looks exactly like the thing under test being
broken. And a control that relies on the *search* refusing an edge never reaches
the follower at all: the double-jump control passed with the `CanDoubleJump` test
deleted until it was made to plan as a scout and fly as something else.

### Phase 7 item 4c-i: what to shoot at, and where to point

`Gg2.Bots.{BotTargeting, BotAim}`. Gated by `BotTargetingTests` and
`BotAimTests`. The reference is `botFindTarget.gml`, `botClassRange.gml`,
`botClassMinBand.gml`, `botEnemyWithin.gml`, `botAngleDelta.gml`,
`botAimSolve.gml`, `botAimLead.gml`, `botWeaponBallistics.gml` and
`botFallPredict.gml`.

⚠️ **`botCombatUpdate` is NOT ported** — 221 code lines of cadence, trigger and
aim slew, and it is 4c-ii. So nothing fires yet: a bot picks a target and knows
where to point, and no key is pressed.

- ⚠️ **Threat is scored in PIXELS, and every term is comparable because of it.**
  `distance − 300×intel − 150×attacker − 120×medic − 60×engineer − 100×wounded
  + range×inside the minimum band`. Taking the carrier means being willing to
  walk 300px past someone harmless; a medic is worth 120 of that because leaving
  one alive undoes everything else.
- ⚠️ **The minimum-band penalty is a LOCK breaker, not a range rule.** A
  soldier's band is 110-800px and it fires throughout — but with threat as pure
  distance it always locked whoever was nearest, and the combat layer does not
  look for a new target while it holds a valid one. An enemy that closes inside
  `BOT_SPLASH_SAFE` therefore pins a soldier permanently. `+range` is always
  enough to lose to anything inside the radius; the property is the point, not
  the number.
- ⚠️ **A blocked sightline does not mean there is nothing to fight**, and
  treating it that way is the "bots walk straight past enemies" report. A
  blocked search falls back to the nearest candidate inside `BOT_BLIND_RANGE`
  with `Visible` false. The radius is short on purpose: a blind target across the
  map is a bot aiming at scenery.
- ⚠️ **Sentries and the enemy generator are candidates too**, and both are aimed
  at by the **centre of the box** where a character is aimed at by its origin. A
  sentry ranks `BOT_SENTRY_BONUS` ahead of a player at the same distance (a
  player can be walked away from; a sentry covers its ground); a generator sits
  at `BOT_GEN_THREAT` 4,000, far past any distance term, so it is only ever
  chosen when nothing live is available.
- ⚠️ **`AngleDelta` answers -180 for exactly opposite, not +180** — the range is
  `[-180, 180)`. That decides which way a bot turns to face directly behind it.
- ⚠️ **The ballistics table is the bot's MODEL of the weapon, not the weapon.** A
  scattergun blast is six pellets with a spread; the aim model calls it one
  projectile at 13 px/tick falling at 0.15. **Do not "fix" one to match
  `Gg2.Simulation.Projectiles`** — every number was tuned against play, and a bot
  that aims with the real ones is a different bot. A speed of **zero** means "do
  not lead", which is how the Rifle and the Medigun fall out without a case.
- ⚠️ **The projectile's own drop is `g·t·(t+1)/2`, not `g·t²/2`** — a projectile
  falls in discrete ticks, so it is the triangular number and not the integral.
  Two and a half pixels on a thirty-tick shot, and exactly the kind of line that
  gets "simplified" by somebody who reads it as calculus.
- ⚠️ **The fall model has two phases because GG2 has a terminal velocity**, and
  the prediction is **clamped at the floor under the target**. Without the clamp
  a target standing still on the ground is predicted to keep falling through it
  and the bot aims below its feet. `BotAim.NoFloor` — "no map is this tall" — is
  the reference's own sentinel and is right over a pit.
- ⚠️ **The flight time is floored at one tick.** A distance of zero gives a time
  of zero and the whole prediction collapses to "aim at your own feet";
  `point_direction` of a point to itself is 0, so the bot aims dead right.
- **Drift is the shooter's own speed**, subtracted from the target's, and only
  for the Scattergun, the Shotgun and the Minigun.

⚠️ **Three testing traps, all about the map and the angle convention:**

1. **Pick the open stretch by MEASUREMENT.** A first draft used (3400, 456) on
   ctf_truefort, which looks like open floor and is not — four threat-bonus cases
   failed because the *further* candidate, the one the bonus is supposed to lift,
   was behind geometry. Sweeping for the widest unblocked horizontal sightline
   found **(380, 268): clear for 1,000px, and blocked 80px straight down**, which
   also gives the blind-fallback fixture a real blocked line.
2. **Compare aim angles as SIGNED.** GM8 reports a direction in `[0, 360)`, so a
   shot aimed twenty degrees below the horizontal comes back as 340 and a naive
   `>` says the opposite of the truth. Go through `BotTargeting.AngleDelta`.
3. **With `NoFloor` even a STATIONARY target is modelled as falling for ever**,
   so "a still target is aimed at level" needs a floor — and a *rising* target
   over a thirty-tick flight is back below where it started, so a rise is only
   visible with the floor well below it. Both fixtures were wrong the obvious
   way first.

### Phase 7 item 4c-ii: the combat controller, and a bot that finally fires

`Gg2.Bots.{BotCombat, BotCombatState, BotClassKeys, BotClassProfile, BotSkill}`.
Gated by `BotCombatTests`. The reference is `botCombatUpdate.gml`,
`botClassKeys.gml`, `botClassProfile.gml`, `botSkillApply.gml`,
`botSkillLerp.gml` and `botAimSpread.gml`.

✅ **A bot now sees, acquires, aims and fires.** ⚠️ Nothing wires it to a server
yet — that is 4d.

**The see → acquire → aim-settled → fire chain is the whole difficulty model.**
Four gates, all off `BotSkill`:

- ⚠️ **The acquire gate holds the AIM, not just the trigger** — the bot does not
  even turn towards the target for `AcquireTicks`. That is the difference between
  a bot that looks *slow* and one that looks blind, and a port that gated only
  the trigger would have every tier turn at the same speed.
- ⚠️ **SPECIAL is gated on acquisition but NOT on the fire delay**, because those
  behaviours are reactive — a pyro's airblast, a demoman detonating under
  somebody's feet — and a reaction a second and a half late is not a reaction.
  **The medic is the one exception**, because its needle gun *is* the right
  click.
- ⚠️ **The fourth gate is not a clock.** Even with every delay expired the
  trigger waits for the slew to settle within `BOT_AIM_SETTLE_DEG` × the class's
  multiplier, or a weak bot with a slow turn rate sprays at the wall it was
  facing when it acquired.
- **There is no filter and no smoothing.** Tracking lag, overshoot and settling
  are consequences of three numbers — perceive interval, aim interval, turn rate
  — and of nothing else.

- ⚠️ **Aim error is UNIFORM on [-1, 1], not Gaussian.** Quake III's `crandom()`
  is uniform and so is every term built on it. Two multipliers ride on top: the
  moving-target scale, and **Quake III's close-range penalty, which makes every
  tier worse at point blank**. That one is an anti-frustration measure rather
  than a difficulty knob — without it bots are unbeatable in a brawl. ⚠️ The
  close factor **divides** and is below 1 near the target, so reading it as a
  multiplier inverts the whole intent.
- ⚠️ **The hold-fire roll is once per AIM INTERVAL, not per tick**, so it
  withholds a burst rather than stuttering the trigger.
- ⚠️ **A target is kept for a memory window after the sightline breaks**, with
  `Visible` false, and the snapshot is **only refreshed while visible** — so the
  aim holds on the last known position and does not track through the wall. The
  bot engages the moment the line opens instead of re-paying the acquire chain.
- ⚠️ **A potshot target is kept inside the same widened band that let it be
  picked.** Otherwise the shot is taken and the target dropped on the very next
  tick, and no potshot ever leaves the barrel.
- ⚠️ **With no target the aim follows the walk, above `BOT_REST_SPEED`** — GG2's
  own "is it really moving" floor. A port using `> 0` turns a bot that is merely
  coasting to a stop.
- ⚠️ **`BotSkill.Lerp` is flat below 0.15 and above 0.95 and its three segments
  are UNEVEN** — 0.15-0.45, 0.45-0.75, 0.75-0.95. The last is two thirds the
  width, so the top tier moves further per point of skill. The five server tiers
  land exactly on 0.15 … 0.95.
- ⚠️ **A medic presses SPECIAL to shoot**, a pyro holds fire beyond
  `BOT_FLAME_REACH`, a soldier refuses inside its own splash-safe band, and a
  scoped sniper waits for a charge **proportional to the range** because the
  Rifle's damage *is* its range.
- ⚠️ **The spy's toggle is `cloak == enemyNear`**, which reads as a typo: the spy
  wants to be cloaked *without* company and uncloaked *with* it, so it presses
  the key exactly when the two agree.

**Deliberately not ported, each a single branch and each conservative when
absent** — so a missing helper makes a bot shoot *less*, never wrongly:
`botFindAlly` (a medic shoots needles rather than healing), `botSuppressSpot`
(a heavy holds its aim on the last known position rather than a doorway),
`botArcClear` (a soldier fires at a target behind cover it would have declined),
`botIncomingProjectile` (a pyro never airblasts), `botStabWindow` (a spy never
stabs), `botMinesArmed` (a demoman never detonates).

⚠️ **A `WorldState` local costs ~160KB of stack, and four in one test method is a
StackOverflowException rather than a failure.** The first draft of
`BotCombatTests` held four and crashed the test host. Build one per helper call.

### Phase 7 item 4d: the bot itself, and a stream that must not be the match's

`Gg2.Bots.ScriptedBot`. Gated by `ScriptedBotTests`. The reference is
`botInputUpdate.gml`.

✅ **A bot walks to a goal, acquires an enemy, fires and hurts it.** It is an
ordinary `IInputSource`, so `GameServer.AddBot` takes it and no client can tell
it from a socket.

- ⚠️ **The two halves are ORed and cannot conflict.** `BotCombat` owns ATTACK and
  SPECIAL; `PathFollower` owns LEFT / RIGHT / JUMP / DOWN. The aim comes from
  combat alone.
- ⚠️ **A bot must NOT draw from `WorldState.Random`, and that is a rule.** The
  match's stream is *observable simulation* — every seeded weapon puts its draw
  on the wire and every client re-derives the spread from it — so a bot drawing
  from it would make the stream's position depend on how many bots were
  thinking. `ScriptedBot.Random` is its own, seeded from the stagger so two bots
  roll differently and one bot rolls the same way every run. **It has to be part
  of any snapshot** (docs/BOTAI.md item 2).
  ⚠️ A first draft copied `world.State.Random` into a local and passed it by
  reference: the copy advanced, the world's did not, and **every tick drew the
  same numbers** — an aim error that never changed and a hold-fire roll that
  never varied. Silent, and it passes a determinism test.
- ⚠️ **The aim solver needs the floor under the TARGET, and a null nav graph
  means no floor.** `BotAim.FallPredict` applies gravity whatever the target's
  vertical speed is, so without the clamp a target standing perfectly still is
  predicted to keep falling and the bot aims **below its feet, into the ground**.
  A draft of `BotCombat` passed `NoFloor` on the reasoning that a grounded target
  is snapshotted with zero vertical speed and so cannot move: that is wrong, and
  the symptom was a soldier that acquired, fired, and never damaged anything.
- **Three behaviours live in `ScriptedBot` rather than in either half**, because
  here is the only place that can see both — the regroup hold, the engage hold
  and the back-off band. ⚠️ **All three are gated on being on the ground and not
  part-way through a validated arc**: stripping LEFT/RIGHT during a jump is not a
  pause, it is a different jump.
- ⚠️ **A held bot must have its stuck counter cleared.** The follower runs its
  detector on the keys it *intends* to press and then returns them for the hold
  to throw away — so a bot standing still on purpose looks to the detector like a
  bot pressing a direction and going nowhere, and every `BOT_STUCK_TICKS` it
  blacklists one of its own spawn room's exits.
- ⚠️ **The engage hold means a bot with a distant goal STOPS walking to fight.**
  That is the feature, not a bug: it exists because a bot with a live enemy in
  front of it used to keep walking its route and fire sideways. Four things bound
  it — a carrier never holds, a bot near its own goal keeps walking, the hold
  expires `BOT_ENGAGE_TICKS` after the target was *acquired*, and a class with a
  close-in distance walks at its target instead.

**Not ported, each conservative when absent:** `botRocketDodge` (never hops away
from an incoming rocket), `botStepSafe` (a pyro holds where the engage caught it
rather than closing), and `botObjectiveUpdate` (a goal must be given from
outside).

⚠️ **Two map facts a bot test needs, and they are DIFFERENT points.** A clear
sightline and a standing position are not the same thing on ctf_truefort:
(380, 268) has 1,000px of clear line and is in the **air** — `NodeFromWorld`
answers -1, so a follower cannot plan from it — and the floor below it has a
**blocked** horizontal line at chest height, so two bots 60px apart cannot see
each other. **Node 14 is both**: 97 cells wide, world x 3,282-3,864, stand
position (3570, 325), 900px of clear sightline. Measured by sweeping every node;
five test failures came from using the other one.

⚠️ **The regroup hold's length is `random(BOT_REGROUP_TICKS)`** — uniform on
0-90 — so a test must **set** it rather than let it roll. This bot's seed happens
to draw about two ticks, and a test that let the roll stand asserts nothing most
of the time and fails occasionally.

### Phase 7 item 4e: the scenario suite runs, and eight of fourteen legs pass

`Gg2.Bots.{IScenarioSubject, ScenarioRunner, ScenarioWorld}` and
`ScenarioSuiteTests`. The reference is `gg2-agent/tools/botscenario.js`.

✅ **The fourteen scenarios are executable assertions now**, driven by
`ScriptedBot`. **Eight pass; six do not**, and the six are asserted as an exact
set in both directions — a leg that starts failing is a regression, a leg that
starts passing means the list is a lie and its entry has to go.

⚠️ **A run is bit-for-bit reproducible**, which is what makes that list mean
anything. Everything that rolls is seeded: a fresh `WorldState.Random` starts at
zero and `ScriptedBot.Random` comes off the stagger. `gallery-pit-climb`'s
`Known` field is the reference's account of what the alternative costs — it
measured 2/5 and then 1/6 on one unchanged build and was believed once, at the
price of a wrong revert.

- ⚠️ **The runner is what hands over the goal, because it is what snapped it.** A
  subject never told where to go stands still and the run reports "did not
  arrive" about a bot that was never asked — which reads as a broken follower and
  is a broken harness. `IScenarioSubject` is the seam.
  ⚠️ **Not implementing it is a legitimate answer and must NOT be an error.**
  Throwing was tried and it broke the synthetic travel scenario that exists to
  exercise the runner *without* a bot: a source holding one key needs no
  destination and cannot be misled by the absence of one. The fact is reported
  (`ScenarioResult.Steered`) and the suite asserts it about its own subjects,
  which is the only place the silent standstill is reachable.
- ⚠️ **`Satisfies` has to check arrival itself, and it did not.** Arrival is not
  an expectation — `Scenario.Budget` already says a leg that takes longer is a
  failure "even if it eventually arrives" — so a travel scenario with no `expect`
  at all is still asserting something. Reading it off the expectations instead let
  three legs that ended **737, 958 and 2,253px short** report themselves
  satisfied. Found on the suite's first run.
- ⚠️ **Acquisition is latched per tick, never read at the end.** A target clears
  when it dies, so on the last tick a bot that fought and won is indistinguishable
  from one that never saw anything. `ScenarioExpectations.Acquired` had been
  carried unevaluated since the suite was ported, for want of this.
- ⚠️ **A scenario world places the map's GENERATORS**, the one objective it does
  place, and `soldier-shells-generator` measures nothing without it: the leg
  places no enemy characters, so its damage figure is entirely the generator's and
  a `MinDamage` of 50 is unreachable however well the bot shoots. It is inert for
  the ordinary reason — `Rounds.StepGenerator` is reached only under
  `GameMode.Generator` and the world is TeamDeathmatch. Measured: 510 damage over
  600 ticks against the reference's 206 and 165.
- **The subject is top tier with a fixed stagger of 1.** A scenario asks whether a
  behaviour is *possible*, and `BotSkill`'s lower tiers are deliberate handicaps —
  measuring against one measures the handicap. The stagger is what a server gives
  the first non-reserved slot and is what makes a run reproducible.

⚠️ **Six of the fourteen failed when this landed and all six pass now** — the
cause was `NAV_EDGE_BUCKET`, one layer down; see item 4f. What is worth keeping
from that day is the shape of the diagnosis, because **it was right about every
symptom and pointed at the wrong layer**: the run-up really was a round trip that
arrived back at the takeoff column at 0.35 px/tick, the gate really did refuse
every approach, and `BOT_TAKEOFF_PATIENCE` really was unreachable at a re-plan
interval of 46. None of it was the cause.

⚠️ **`ScenarioSuiteTests.Unmet` is empty and keeping it empty is the point.** It
is asserted as an exact set in both directions, so a leg that starts failing is a
regression *and* a leg recorded as unmet that starts passing fails the build with
"delete its entry". It held six names for exactly one commit.

### Phase 7 item 4f: one cast, and the fourteen scenarios all pass

`Gg2.Bots.{NavCache, NavGraph}` — `NavEdge.Bucket` is a `double` now. Gated by
`NavGraphFixtureTests.AJumpEdgesVelocityIsARealAndSurvivesTheLoad` and by the
whole scenario suite.

✅ **All fourteen behaviour scenarios pass**, and the tick counts land on the
reference's own numbers.

⚠️ **`NAV_EDGE_BUCKET` is a horizontal VELOCITY in px/tick, and it is
fractional.** It is not a bucket index, whatever the cache column is called:
`navJumpTakeoff` proves an arc as a constant velocity applied from tick 0 and
stores it — "leave the takeoff column, cover NAV_EDGE_BUCKET px of ground a tick,
be over the landing when the arc comes back down", `botPathKeys.gml:436`. This
port declared it `int` and truncated it on load, from item 4b-i until now.

Measured over the 24 cached graphs: **67,013 jump edges, 67,005 of them
fractional, and 23,184 — 34.6% — truncate to ZERO.** A zero velocity makes the
in-flight tracker steer for the takeoff column itself, so the bot is told to hang
still in the air, and the takeoff gate then demands the whole gap from a reach of
nothing. `ctf_truefort` 178 → 177 carries `1.9518072289156623`, giving the arc a
54px budget for a 42px gap; read as `1` it is 27.7px and **the gate can never
pass, on any approach, for ever**.

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

- ⚠️ **The three diagnoses in the previous session's handoff were all correct
  about the symptom and all pointed at the wrong layer.** The run-up really was a
  round trip, the takeoff gate really did refuse every approach, and
  `BOT_TAKEOFF_PATIENCE` really was unreachable — and none of them was the cause.
  **A behaviour layer that looks subtly wrong everywhere is a reason to check
  what it is reading, before tuning it.** The tell in hindsight: the defect
  appeared on *marginal* arcs of every kind on every map, which is what a lossy
  read looks like and not what a control bug looks like.
- ⚠️ **`(int)` on a value read out of a `ds_grid` is a decision, not a
  formality.** Every grid cell is a double. `EdgeTicks` and `EdgeCost` were
  already read as reals and only `EdgeBucket` was cast; the node grid has no real
  columns at all. Measured, per column, so the remaining casts are known-safe
  rather than assumed.
- **The gate is two assertions plus two exact values.** No jump edge may read
  zero (the 34.6%), better than 99.9% must be fractional (which notices an index),
  and two named edges must carry their exact bits — `ctf_truefort` 178 → 177 at
  `1.9518072289156623` and `koth_gallery` 86 → 58 at `0.6975139064871476`, the
  arc the reference's own takeoff-gate comment quotes as "0.70 px/tick over 17.2
  ticks against a 12px gap: exactly 12 for exactly 12". That second one is
  corroboration that this port reads the same field the reference does.
  ⚠️ **The scenario suite catches it too and that is not enough**: it is a map
  load and 6,000 ticks away from the wrong line, and it reads as a follower
  defect. Both distances are worth having.
- ⚠️ **Eight of the 67,013 really do land on a whole number**, so "every one is
  fractional" is false and the proportion is what is asserted. The first draft
  claimed all of them and failed.

⚠️ **The long `ctf_truefort` leg in `PathFollowerFixtureTests` went 2,655 → 1,572
ticks, 76 → 33 re-plans, 34 → 4 off-route and 9 → 0 blacklists**, on a 137-node
route where the reference quotes 2,200-2,500 for its own shorter one. So **"this
port misses more marginal arcs" is no longer true** — that note stood in item
4b-ii and in the fixture, and it was the truncation the whole time.

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
  though this port stores it and the reference cannot. They are different
  questions: `ScenarioWorld` is TeamDeathmatch with nothing placed, and a CTF map
  whose briefcase is being carried has no `Intelligence` instance either. Reading
  the field instead would retask every scenario's bot mid-run.
- ⚠️ **The role split's class term is a PERIOD, not a veto.** "Engineers defend,
  scouts attack" breaks at both ends — a team of nine engineers has nobody
  attacking, and `botPopulationUpdate` rolls a class with `irandom(8)`, so it can
  produce one. Making the class choose *how often* keeps a split at every
  composition. ⚠️ And the count is over the bot's **lean group** (engineer+heavy,
  scout+spy, everyone else), not the whole team: two engineers separated by four
  scouts sit at team positions 0 and 5 and neither lands on a multiple of 2.
- ⚠️ **`(place + 1) mod every`, so the first bot on a team always attacks.** A
  rule keyed on `place mod every` makes the lone bot on a team a defender with
  nothing to defend against.
- ⚠️ **A defender is 220px BESIDE what it guards, even at a spread of zero** —
  `if (spreadX >= 0) spreadX += BOT_DEFEND_RADIUS`. Two tests were written
  asserting equality with the flag and failed by exactly 220.
- ⚠️ **Our own flag being carried outranks fetching theirs, and every role
  chases.** Before that only defenders did, so a team that rolled none — the count
  runs down the roster and a short team can land none — left an enemy standing in
  the open with our flag and nobody interested.
- ⚠️ **A carried flag has no instance, so the whole cascade comes back empty.**
  Everyone except the carrier had no goal: a defender whose flag was stolen, an
  attacker whose team-mate beat it to the grab. Both chase, and the goal is the
  moving carrier — returned early, because a standoff band around a running player
  is a bot orbiting it and a spread is a deliberate miss of the one thing worth
  intercepting.
- ⚠️ **A control point's goal is the MIDDLE of its capture zone.** The origin is
  the top-left corner, and arrival is only accurate to `BOT_ARRIVE_TOL`: measured
  in the reference on koth_valley, the bot climbed the whole four-jump chain,
  stopped 3px short of the zone's left edge, and captured nothing.
  ⚠️ **The zone is the only position this port has for a point** — `ControlPoints.Place`
  discards the marker's own coordinates once the zones are matched — where the
  reference ranks by the marker and then overrides with the zone. Named
  divergence; it can only reorder two nearly-equidistant points, and a capture
  reads the zone and nothing else.
- ⚠️ **A LOCKED point is still walked to.** Every point is locked for the first
  900 ticks of a KotH round and 1,800 of an Arena one, during which the
  nearest-unlocked loop finds nothing and leaves the bot standing frozen at spawn
  while a human is already walking out.
- ⚠️ **An enemy in the zone cancels every "stand somewhere better" behaviour**,
  for attacker and defender alike — and it is read off **presence**, not the
  capture meter. Presence is true on the frame an enemy steps inside; the meter
  takes a second, and by the time it reads as a capture the point is a third gone.
- ⚠️ **The hold trigger is a DISTANCE, not "have I arrived".** Arrival is cleared
  by issuing a goal, so a trigger keyed to it flips off the moment it fires and the
  bot oscillates between the point and its overlook for ever at 30-tick intervals.
- ⚠️ **The spread never applies to a goal that has to be TOUCHED.** Measured in
  the reference on ctf_truefort: bots walked the whole map to the enemy flag,
  stopped 37-67px short of it (arrival tolerance is 8, so they stopped *precisely*),
  and stood there — 0-0 after a full round with both teams parked around a flag
  neither would touch.
- ⚠️ **The objective's own node is resolved first and the spread only earns its
  way in.** The snap searches *downward*, so snapping an already-offset position
  falls off the platform the intel is on — swept across the shipped graphs, a 96px
  spread at `ctf_orange` node 1 drops **636px**, most of the map. The guard is
  "within a body's height", which is "the same level" rather than "the same
  surface" so a spread can still cross a step.
- ⚠️ **An unchanged objective is not reissued**, because issuing forces an
  immediate re-plan and would fight the very timer the follower is built around.
  Measured: deleting the throttle takes the end-to-end leg from 41 re-plans to 58
  and 10 off-route events to 19 — and **the bot still arrives, slightly sooner**
  (1,780 → 1,693), so a test that only watches arrival passes with it gone.
- ⚠️ **`BOT_SPOT_TRIES` is invisible on any search that succeeds** — bounded and
  unbounded pay the same number of tests, because both stop at the first pass. The
  gate is a search that *cannot* succeed: 12 of ctf_truefort's nodes sit where
  nothing in a 150-4,000px band has a clear line, found by sweeping.

**Two things deliberately not ported, each conservative when absent:**
`botFindAlly` and with it the medic's follow-goal — a medic falls through to the
cascade and walks the objective like everyone else, which is exactly what the
reference does with a medic that finds nobody to follow — and
`botPopulationUpdate`, which is what fills a server with bots and is `GameServer`'s
half of this.

⚠️ **One branch is UNGATED and says so in its own comment**: the spot ban
(`ScriptedBot.SpotBanUntil`). Deleting it leaves every test green. It needs a spot
that resolves and cannot be reached, which needs a nav graph with two components,
and every shipped graph is connected for the team that owns the ground. A
synthetic two-component graph would gate it; nothing else will.

⚠️ **The scenario suite does not exercise any of this and must not.** A scenario
places no objective and `ScenarioRunner` hands the bot its goal directly, so
`ScriptedBot.Objective` stays null there — the reference has the same escape
(`botGoalLocked`). ⚠️ **But `ScenarioWorld` places generators now** (item 4e), so a
bot given an objective in one *would* be retasked; that is why the objective layer
is opt-in rather than on by default.

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
population manager is server-side in the reference — only the server has a roster,
a config file and a socket to broadcast a join on. `Gg2.Bots` stays pure and gains
nothing; docs/ARCHITECTURE.md's one hard rule (`Gg2.Simulation` must not reference
`Gg2.Server`) is untouched. The `IInputSource` seam still means the *simulation*
never special-cases a bot.

- ⚠️ **`if(!global.botsEnabled) exit;` means DO NOTHING, not "the target is
  zero".** A bot added by hand on a server with the manager switched off must
  survive; reading it as a target removes it on the very next tick. **Measured:
  it took out seven of `InputSourceTests`' eight tests**, every one of which adds
  a bot to a default server.
- ⚠️ **The target is a FILL, including humans, not a bot count.** A server filling
  to 8 with 3 people on it runs 5 bots and drops one every time somebody joins.
  And the `MinHumans` floor answers **zero** rather than the fill, as the *first*
  test rather than a clamp on the last: an empty server running eight bots against
  each other looks full in the lobby list and is not.
- ⚠️ **`getNumberOfOccupiedSlots` counts a joiner that has merely RESERVED a
  slot**, and neither bots nor the server's own slot 0. All three matter: counting
  bots makes the target chase its own tail, and *not* counting a reserving joiner
  means the bot it should displace is displaced a tick late.
  ⚠️ It also makes `HumanCount` the wrong detector for "the join completed" — it
  went up on the reservation. `PlayerCount` is wrong too, because a bot displaced
  by the joining human puts it straight back. `ServerHarness` counts humans on the
  roster, which is neither.
- ⚠️ **A follower, a route and a blacklist are all node indices into ONE graph**,
  so a bot that crosses a map rotation carrying them holds numbers that now mean
  somewhere else — and its `BotObjective` holds the old graph and the old map's
  spawn points too. `ScriptedBot.SetMap` is the reference's `botNavRelease` and
  `botReteamAll` in one, called from `OpenMap`, and **the bots are re-mapped before
  they are re-roled**.
- ⚠️ **The role assignment is a second pass over a FINISHED roster**, never inside
  the loop that assigns teams. A role counts this bot's place among its team's bots
  that share its class period, so assigning as you go gives every bot a count that
  only includes the ones before it. It re-runs after every add and every removal as
  well, because `RemoveSlot` compacts the roster and renumbers everyone above the
  hole.
- ⚠️ **The class the query carries is `checkClasslimits`' answer, not the one
  asked for.** The search's double-jump arcs are per class, and a scout routed as a
  heavy walks past every edge it could have flown.
- ⚠️ **The tie in `botPickTeam` has to be a coin.** A fixed side puts the first two
  bots on an empty server both on red and the balance never recovers — every later
  pick sees blue smaller and feeds it, so the teams alternate around a permanent
  offset of one.
- ⚠️ **`botPickClass` excludes the spy, and not because the spy's own code is
  broken.** Almost everything a spy does is invisible to the rest of the bot model:
  it spends most of its life cloaked, so target selection skips it and team-mates
  route around a fight it is standing in; its whole value is a backstab, which is
  one branch behind a range test; and a bot doing that badly is indistinguishable
  from a bot that is broken. Quote is out because `irandom(8)` is nine classes and
  stops short of it.
- ⚠️ **A record struct's primary-constructor defaults do NOT apply to
  `new T()`.** That form resolves to the implicit *zeroing* constructor, so a
  "defaulted" `BotPopulationOptions` has a fill target of 0, a difficulty of 0 and
  a **null** name prefix — every one of them silent. Measured by running the
  thing: `--bots 12` named its bots "1".."12" instead of "[BOT] 1".."[BOT] 12",
  and nothing in the suite noticed because every test that cared constructed its
  options explicitly. `BotPopulationOptions.Default` is the factory, and
  ⚠️ **its values are spelled out rather than delegating to `new()`** — writing
  `new()` there makes the factory a no-op that reads exactly like a fix. Same
  family as `CharacterState.Fresh` and `PlayerState.Fresh`: **in this codebase a
  zeroed value type is routinely not a valid one.**
- **Removal prefers a bot that is already dead.** The reference defers a removal on
  a wall clock (`current_time`) so a body does not vanish mid-fight; a fixed-tick
  server has no wall clock, and taking the dead first gets the same outcome. That
  substitution is deliberate and is the one place this file is not a transcription.
- **`GameServer.CurrentMap` is exposed now.** ⚠️ Not `ServerOptions.Map` — they are
  the same string until the first rotation and different for ever after, which is
  the bug `MapRotationTests` exists to catch.

⚠️ **Two testing traps, both about waiting for the wrong event.** A rotation
announces itself **300 ticks before it happens** ("round over … koth_valley area 1
in ten seconds"), so a log match exits the wait while the old map is still running
and every assertion then reads the state the test says has changed. And a rotation
test needs **nav state that actually exists** to invalidate — a bot re-spawned on
the new map has no route either way, so the test plants a real route and a real
blacklist entry first.

**Not ported:** the reference's deferred-removal timeout (above), and
`botReteamAll`'s *re-classing* — this port re-teams and re-roles across a rotation
but leaves each bot the class it had, which is a strictly smaller change and is
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
  in BOTH terms of the motion law, not one.** `moveStatus` 1 —
  "rocketing/mining myself" — is a different row of Begin Step's switch:
  controlFactor **0.65** and frictionFactor **1**, against the default's 0.85 and
  1.15. **A friction of 1 means horizontal speed does not bleed at all** for the
  whole flight, which is around 69 ticks on a full-height jump.
  Measured, and it is wrong in both directions at once:
  **a cold start on a fast arc** (needVx 6, v0 0, 30 ticks) reaches **136.9px**
  under the rocket law against **119.5** under the ordinary one — so the ordinary
  model *refuses arcs the bot flies easily*; **a hot start on a slow arc**
  (needVx 2, v0 8) overshoots its own 60px schedule to **85.6px** where the
  ordinary law lands at **59** — so the ordinary model *accepts arcs the bot sails
  past*.
- ⚠️ **The rocket law needs a SPEED GATE and the ordinary one must not grow one.**
  With no friction under it, a press that has already reached the arc's vx gains
  nothing and pressing on is what made the real tracker limit-cycle. Friction is
  what bounds the ordinary law.
- ⚠️ **The wait branch is BRAKING here where the reference writes
  `targetCol = takeoffCol`, and the two are the same behaviour in two codebases
  whose default steering differs.** The reference's default is a pure column
  comparison, so aiming at the column the bot is already on presses nothing — which
  its own comment relies on. **This port's default is a DIRECTION HINT rather than
  a place to stop**: it presses on along the route even when the bot is exactly on
  its target column, on purpose. Measured: aiming at the column pressed RIGHT for
  twelve consecutive ticks and the stuck detector banned a perfectly good edge at
  **tick 12**, forty-eight ticks before the patience budget it was meant to spend.
- ⚠️ **The mid-air bail is `return 0` from the whole script — no keys, not "no
  steering".** Returning to the default steering leaves the bot walking toward the
  takeoff column while it falls, which is a press on the very arc the branch exists
  to stop steering. `Steering.Abandon`.
- ⚠️ **`AirTicks > 1`, not `>= 1`.** The blast lands in the same tick's simulation
  as the takeoff press, so tick one is always `moveStatus` 0 and a stricter test
  abandons every rocket jump on its first airborne tick.
- ⚠️ **The escape hatch takes the EDGE away rather than shoving the bot off it**,
  which is what it does for every other arc. Jumping without the rocket is the
  worst available outcome — the arc rises 276px where an ordinary jump rises 57.
  ⚠️ And it is **unreachable at any stagger below 15**, because a re-plan zeroes the
  takeoff budget (item 4b-ii's ceiling) — which is very nearly always right, since
  the launcher's refire is 30 ticks and its ammo comes back.
- ⚠️ **The aim OVERRIDES combat's rather than being ORed with it**, and 270 is not
  a simplification: `vectorfactor` in the rocket's blast is sqrt(sin⁴ + cos⁴),
  which is 1 only when the impulse is axis-aligned and falls to 0.707 at 45°, so an
  angled rocket jump throws away up to 30% of its impulse. It **snaps** rather than
  slewing — the turn rate is 8°/tick and level-to-down is 90°, so honouring the
  slew is eleven ticks of a bot staring at the floor before every jump.
- ⚠️ **`CanRocketJump` is recomputed from LIVE state every tick, not held.** It is
  the *weapon* plus `hp > NAV_RJ_DAMAGE` — strictly greater, because the damage
  lands before the bot does — so a soldier's routes shed their rocket jumps on
  their own as it takes damage.
- **`PathFollower.Step` has a weaponless overload**, and it means **"this bot has
  no rocket"** rather than "do not care": a rocket edge is then never taken off
  from, which is the conservative answer and what every caller that does not model
  a weapon wants.

⚠️ **The cost side, and it is real.** The end-to-end intel run went **1,780 → 1,748
ticks** while its **re-plans went 41 → 49 and its off-route events 10 → 19**. That
is what flying arcs that sometimes miss costs, and the scenario suite is the better
signal on the trade — it improved across the board.

⚠️ **Three testing traps this cost, all "the test measures the wrong thing now":**

1. **A fixture arc must cover its own gap** — the same trap the jump fixtures
   record. The old stub asserted *refusal*, so its arc never had to be flyable;
   reusing it asserted a takeoff that could not happen.
2. **The throttle test started measuring rocket churn.** A soldier's routes now
   contain arcs that are blacklisted and re-planned around whenever the launcher is
   not loaded, and with a soldier the *still* window re-planned seven times against
   the moving window's five — inverting the comparison. It uses a heavy now, which
   has neither special arc.
3. **The rotation test hit CTF OVERTIME.** A tie with a flag off its base at 0:00
   is unbounded, and once bots reached the enemy flag more often some runs never
   ended a round at all. It starts on a Team Deathmatch map now — **the only mode
   with no overtime** — and is given its goal by hand, because TDM correctly
   declines to invent one.

⚠️ **Gating the two models is not gating that the GATE picks the right one.** Point
the rocket branch at `JumpModel` and every other follower test still passes,
because their arcs are flyable under either law. `TheTakeoffGateAsksTheRocketModelAboutARocketArc`
is the fixture built to separate them — an arc flyable under one law and not the
other, where the observable is whether the bot launches or starts a run-up.

### Phase 7 item 4j: the medic's ally, and one script with two callers

`Gg2.Bots.BotAlly`, the medic exception in `BotCombat.Step`, the medic case in
`BotClassKeys`, and the follow-goal in `BotObjective`. Gated by `BotAllyTests`.
The reference is `botFindAlly.gml`, `botCombatUpdate.gml:175-198` and
`botObjectiveUpdate.gml:83-133`.

✅ **A medic heals rather than shoots, and follows somebody.** Both halves came
from one 70-line script, which is why it was deferred rather than half-wired.

- ⚠️ **One script, two callers, and they pass DIFFERENT arguments.** The combat
  half asks with `skipFollowers` false — a medic beaming another medic is correct
  play — and the objective half asks with it true, because **two medics who pick
  each other as a destination deadlock**: each one's goal is where the other is
  standing, both are already there, and neither ever leaves spawn. Seen live on
  koth_valley, two red medics parked at x=460 with the rest of their team at the
  point. **Making the exclusion a rule inside the search rather than an argument
  breaks healing to fix following.**
- ⚠️ **Filtering during the search rather than rejecting the winner afterwards**
  is what lets the second-best candidate win: a medic beside a wounded medic and a
  healthy heavy still follows the heavy, instead of giving up on following at all.
- **The score is `1000 * hp/maxHp + distance - maxHp`.** Anyone wounded outscores
  anyone who is not by far more than distance can make up, and the `- maxHp` term
  is what makes a heavy at half health beat a scout at half health: a medic's job
  is to keep the biggest body in the fight alive, not to top up whoever is closest.
- ⚠️ **The sightline is `collision_line` against `Obstacle`, NOT
  `collision_line_bulletblocking`.** A heal beam is stopped by walls the same way a
  bullet is, but the two predicates are not the same object set — gates, player
  walls and bullet walls are separate objects, so a medic can beam a team-mate
  through their own gate — and the beam's is the one that decides whether the heal
  connects. `CollisionWorld.CollisionLineHitsMask` is that call, and `Healing` uses
  it for the real thing.
- ⚠️ **The ally REPLACES the subject rather than adding to it**, and everything
  downstream then treats it as the thing being aimed at. That is what makes a medic
  stop shooting and start healing: the needle gun is on the RIGHT click and the
  beam on the left, so the switch is two different keys.
- ⚠️ **An ally is NOT acquired, and all four difficulty gates are bypassed** — the
  acquire hold, the fire delay, the aim settle and the hold-fire roll. Every one is
  about shooting a person who is trying not to be shot; a medic that made its own
  team-mate wait a second and a half before pointing the beam at them would read as
  broken rather than as bad.
- ⚠️ **The beam is aimed STRAIGHT at them with no lead solve.** It has no travel
  time — the lead solver exists to put a projectile where a target will be — and
  leading a team-mate points the beam at empty air beside them.
- ⚠️ **The medic exception is decided ABOVE the no-target early return**, because
  an ally is a subject in its own right: a medic with a patient and no enemy
  anywhere still beams, and deciding it after would make a medic heal only while
  something was shooting at it.
- ⚠️ **The follow-goal goes FIRST and returns early**, ahead of the whole mode
  cascade, because a medic following a team-mate is doing its job in every mode and
  the objective is that team-mate's problem. Its goal is a moving *instance*, so it
  does not feed the spread and spot machinery — the same shape as the flag-carrier
  chase, and for the same reason. With nobody to follow it falls through and walks
  the objective like everyone else.
- ⚠️ **The offset keeps the ally BETWEEN the medic and the enemy**, and it is
  **horizontal only**: the goal is a place to *stand*, and standing is a
  one-dimensional choice on a given surface. An intel carrier does not follow at
  all.

⚠️ **One named divergence: `skipFollowers` excludes the CLASS where the reference
excludes `isBot and BOT_CP_FOLLOW`.** `WorldState` holds no "is this a bot" flag
and must not grow one for a branch the tick never reads — docs/BOTAI.md's whole
point is that read-only inputs are passed *to* `Tick` rather than held by it. What
it costs is skipping a **human** medic as a follow target too, which the reference
deliberately does not (a person walks somewhere on their own, so no cycle can
form). The degradation is bounded and conservative: a bot medic whose only
candidate is a human medic finds nobody and falls through to the objective, which
is exactly what the reference does with a medic that finds nobody to follow.

⚠️ **Three testing traps, all "the assertion is about the wrong thing":**

1. **The follow-goal fires with or without an enemy** — the offset simply comes
   off — so asking the medic with no target proves nothing about whether the mode
   cascade is empty. That premise has to be shown with a **non-medic** in the same
   world.
2. **A medic's attention radius is 300px**, the shortest of any class bar the
   engineer's 400. A fixture that put the enemy at 400 exercised none of the wiring
   under test, silently.
3. **The follow position is 60px from the ally, so a medic standing beside one
   ARRIVES on the tick the goal is issued** — and arrival clears `HasGoal` and frees
   the path together. `GoalX` is what survives, and it is what the claim is about.

⚠️ **Gating the rule is not gating the wiring.** `BotObjective.Decide` takes the
target as a parameter, so a test that calls it directly passes with `ScriptedBot`
no longer passing one — checked by mutation, and closed by driving a real medic bot
and reading which side of its ally the goal landed on.

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
  rectangle. The reference used to ask "within 150px and 75° of the aim", and both
  numbers were copied from the Flamethrower's own `blastDistance` and `blastAngle`,
  **neither of which gates a reflect any more** — so the bot pressed SPECIAL for
  rockets it could not possibly touch, over a volume about **ten times wider than
  the weapon**. Reported from play as a pyro unable to deflect rockets even from a
  distance.
- ⚠️ **The press is not free**, which is why the volume has to be exact: it sets
  `readyToBlast` false for 40 ticks whether or not anything was there, and a rocket
  at 150px is ~12 ticks from impact — so the wasted blast is still on cooldown when
  the one that was going to land arrives.
- ⚠️ **The three constants are measured off the running exe, not off the sprite
  file, which does not agree with it.** `AirBlastMaskS` is a 120×30 image but its
  mask bounds are AUTO, so GM8 takes them from the opaque pixels: left 8, right 96,
  top −13, bottom 14. Plus the 25px spawn offset and `collision_circle`'s 5px
  radius: **near 28, reach 126, half-width 18**.
- ⚠️ **There IS a near edge and it is not a rounding detail**: a rocket already at
  the pyro's feet is UNDER the beam and cannot be blasted at all.
- ⚠️ **`minTravel` is a DISTANCE, not an age.** The airblast has no delay of any
  kind, so without it a rocket fired point-blank came back on the frame it was
  born — confirmed inhuman. A distance says the thing directly and is frame-rate
  independent. It is 60 rather than the 120 it was first set to *because of the
  beam*: the window is 28-126px, so at 120 a shooter standing 130px away produced a
  rocket that was never eligible at any point on its flight.
- ⚠️ **The dodge's verdict is "the standing bot is hit at some tick AND the
  jumping bot is hit at none".** The old test was "is an enemy projectile within
  150px" and it was wrong in **both** directions: it jumped at rockets that were
  never going to connect, and it jumped *into* ones fired over the bot's head — a
  rocket passing above is exactly the case where rising 57px is what CREATES the
  collision. **No proximity test can tell those apart**, because "would it hit" and
  "does jumping help" have opposite answers depending on where the shot is aimed.
- ⚠️ **Splash is deliberately not modelled.** A rocket detonating on the floor
  under a jumping bot still catches it inside `blastRadius` 65, so counting splash
  would refuse nearly every dodge. What the jump reliably avoids is the direct hit.
- ⚠️ **The dodge is grounded-only and never throttled** — grounded keeps it out of
  every planned jump arc without knowing anything about that arc, and ORing is safe
  because JUMP is edge-triggered in Begin Step. **It is the only thing that presses
  JUMP outside the path follower**: the voluntary combat hops the reference used to
  have there were removed, because between them they fired often enough — precisely
  where the follower has arrived and returns no movement keys — that bots in a
  firefight read as stuck in place jumping.

⚠️ **A gap this found: the class policy must run with NO TARGET, and in this port
it did not.** `botCombatUpdate`'s no-subject branch still calls `botClassKeys` —
"nothing to aim at is a state a class can still have an opinion about: a spy
re-cloaks, an engineer builds, a heavy eats" — and only the aiming is skipped. This
port returned no keys at all, so **a pyro with an empty screen could not deflect a
rocket**. Fixed by running the policy there, which means **every branch that would
otherwise fire at empty air now carries its own subject test**, exactly where the
reference puts them. ⚠️ The pyro's flame needs one because `distance` is 0 with no
target and 0 is inside the flame's reach — without it a pyro with nothing to shoot
at holds the trigger for ever.

⚠️ **`readyToBlast` and `ammoCount >= 40` are checked FIRST and are not
decoration** — they are much the cheapest of the three tests and gate the beam scan
regardless of geometry.

⚠️ **One reference comment describes a case its own budget cannot reach, and this
is measured rather than assumed.** `botRocketDodge` subtracts the rise rather than
clamping it, "negative once the arc has come back down". With `NAV_JUMP_V0` 8.3 and
`NAV_JUMP_GRAVITY` 0.6 the rise crosses zero at **t = 27.67** and `BOT_DODGE_TICKS`
is **26** — one tick short. Over the whole walk the rise runs from 8.0 to 13.0 and
never dips below 8, so **a clamp is a no-op**, checked by mutation. The subtraction
is kept because widening the budget would make it matter.

⚠️ **A testing trap: the two obvious dodge fixtures do not gate the second half of
the verdict.** A rocket at the chest and one over the head leave `!hitJumping`
entirely ungated — the over-the-head one misses the *standing* bot too, so it fails
the FIRST clause. What is needed is a rocket that hits **either way**: one tick out
at +10, where the standing box spans −21..+31 and one tick of jump (8px) gives
−29..+23.

### Phase 7 item 4l: suppression, and a live flag that is not the target's

`Gg2.Bots.BotSuppress`, the suppression block in `BotCombat.SolveAim`,
`BotCombatState.{SuppressNode, SuppressAt}`. Gated by `BotSuppressTests`. The
reference is `botSuppressSpot.gml` and `botCombatUpdate.gml:294-328`.

✅ **A heavy that loses sight of its target now denies the doorway it must come
back through**, rather than shooting the wall it lost them behind.

- ⚠️ **Worth building because the case is common, and that was measured before any
  of it was written.** Twelve bots on koth_valley over 3,172 frames: **13,733
  bot-ticks with a live target, 2,744 of them — 20% — with no line of sight**, and
  for a heavy 1,432 of 6,442. About one tick in five of every engagement, and the
  30-tick memory drop does not already eat most of it.
- ⚠️ **A BOUNDED BREADTH-FIRST WALK, NOT AN A*, and that is a measurement rather
  than a preference.** The design asked for a path search from there toward the
  heavy's own node; built that way and timed at **8.13 ms a call on koth_valley's
  270 nodes**. Nothing staggers the bots, so six heavies recomputing on one frame is
  **48.8 ms against a 33.3 ms budget**, on the *smallest* interesting map —
  ctf_truefort has 687 nodes. Breadth-first order is also the right order: fewest
  hops from where they were is the soonest they could appear.
- ⚠️ **The walk uses the ENEMY's gates, not ours.** It is their route, and a red bot
  must not decide that a blue enemy will come out through a red spawn gate.
  Observable on ctf_truefort: from node 8 a red traveller is walled in and a blue
  one is not, and from node 74 it is the other way round.
- ⚠️ **Never the node they were last seen on** — that one is behind cover by
  construction, and shooting it is exactly the behaviour this replaces. It is also
  the guard that stops a caller aiming at a wall when the enemy simply stood still.
- ⚠️ **The velocity is zeroed when a spot is taken.** The velocity that came with
  the snapshot belongs to a body that was moving when it was last seen; **a doorway
  is not moving**, and leading a fixed point by a dead man's velocity is how a
  suppression shot ends up in the far wall.
- ⚠️ **On its own clock, not the aim interval.** The aim interval is 2 ticks at the
  top tier, so asking there would be a graph walk per bot per two frames. The answer
  is re-used between refreshes, which is honest about what it is: a place, not a
  moving target.
- **Opt-in per class** (`BOT_CP_SUPPRESS`, the heavy alone) because the walk is a
  search, and **degrades to the stale snapshot with no graph** — a server whose map
  has no nav cache still runs bots.

⚠️ **`state.Visible` is the LIVE flag and `state.Target.Visible` is not.** The first
is maintained on the perceive cadence and is false exactly while the sightline is
broken; the second is only what the search reported at acquisition and never changes
again. The first draft read the target's, which made the whole branch fire on the
acquisition tick and never afterwards. The reference is `!player.botVisible`.

⚠️ **A target acquired while BLIND never gets a snapshot, and the aim then sits at
(0, 0).** The perceive block is below the acquire hold's early return — in this port
*and in the reference* (`if(!canAim) return 0;` sits above it in
`botCombatUpdate`) — so a target that was never seen leaves `botSeeX/botSeeY` at
whatever they held. On a fresh bot that is the map origin. **Faithful, not a
defect**, and worth knowing because it decides what a test fixture has to look like.

⚠️ **A suppression fixture must be SEEN AND THEN LOST, never acquired blind** — for
exactly that reason. Three tests failed measuring the origin before this was
understood. And two more things a fixture here needs:

- ⚠️ **Inside `BOT_BLIND_RANGE` (260).** A blind target further out is never held at
  all, so the feature is unreachable. A first fixture put the pair 464px apart and
  failed for that reason and no other.
- ⚠️ **Moving when it is last seen.** An enemy that stands still is snapshotted at
  zero velocity, and a port that forgot to clear the velocity looks identical —
  checked by mutation, the zeroing survived until the fixture gave the enemy a
  speed.

⚠️ **A permanently broken sightline drops the target every 30 ticks and re-acquires
it on the next target period**, so a test that stops on a fixed tick lands in the
gap about as often as not and reads as "the bot never acquired anything". Stop on
the condition, and guard any window assertion with "still held and still blind" — a
re-acquisition legitimately recomputes, and asserting through one measures the
memory window instead.

**The hop budget is a COST bound and is deliberately not gated.** What is gated is
that the answer is a node the enemy can actually reach from where it was, which a
broken edge index or a mis-read gate would break.

### Phase 7 item 4m: the arc, and a fixture that has to separate two questions

`Gg2.Bots.BotArc`, the arc gate in `BotClassKeys`. Gated by `BotArcTests`. The
reference is `botArcClear.gml` and `botClassKeys.gml:47-68`.

✅ **A demoman no longer fires into the ceiling.**

- ⚠️ **Nothing else in the bot layer ever checks a projectile's arc.** Target
  selection, the keep-target test and the spot search are all a straight
  chest-to-chest sightline, while the aim solver deliberately aims *above* the
  target to compensate for drop — and the amount is large: **~178px of sag at 500px
  for a Minegun round**, ~120px for a scattergun, none for a rocket. So a demoman
  with a clear straight line to a target across a room is aiming a whole storey
  above it. Indoors that is the reported "sometimes shooting at the ceiling",
  stated as a mechanism rather than as a symptom.
- ⚠️ **Only the two classes whose shot kills them if it goes off next to them.**
  Everyone else pays nothing: a scattergun round that clips a lintel costs ammo the
  class has plenty of, and withholding would make a scout stop firing in exactly
  the corridors it wants to be firing in.
- ⚠️ **The last sample is the TARGET, not the parabola's own endpoint.** The flight
  time is recovered from the horizontal offset and carries a tick or so of error,
  and letting that error put the final sample a few pixels *into* the floor the
  target is standing on would refuse every shot at a grounded enemy — the
  commonest shot in the game. Ending on the aim point is also exactly what the
  straight sight test does, so a flat weapon gets the answer it always did.
- ⚠️ **`t·(t+1)/2` is exact rather than a continuous approximation** — gravity is
  added to `vspeed` *before* the move is applied, so the accumulated sag after t
  ticks is the triangular number. Same law `BotAim.Solve` aims with, and gated
  there rather than twice.
- **Withholding rather than re-solving is deliberate.** A flatter solution
  generally means closing the distance, which is a positioning decision the
  objective layer owns.

⚠️ **A fixture for this has to separate `Clear(aim, g)` from `Clear(aim, 0)`, not
merely from the chest-to-chest line — and only a handful of pairs on a real map
do.** The first fixture was blocked by the **aim direction** rather than by
gravity: the shot simply pointed into geometry, which a straight-line sampler
answers identically. **Three mutations survived against it**, including "sample the
arc as a straight line" — the one the whole feature is about. Sweeping ctf_truefort
for pairs where gravity alone decides gave five; node 27 → node 136 is the one
used.

⚠️ **And the sweep that chose that first fixture was itself wrong, for a reason
CLAUDE.md already records.** It solved the aim with `BotAim.NoFloor`, so the solver
predicted a stationary target falling for ever and aimed **below its feet, into the
ground** — every arc it sampled was blocked, which reads as the feature refusing
everything. With a real floor the same sweep gives 8 blocked against 50 clear.
**Any test that solves an aim must pass `BotAim.FloorUnder`.**

⚠️ **One mutation survives and is recorded in the code rather than papered over:
the upper flight-time clamp** (`BOT_AIM_MAX_TICKS`). It is a guard against an
unreachable aim point walking a thousand ticks of parabola — a *cost* bound, like
`BotSuppress`'s hop budget — and every real-map fixture that reaches it is blocked
with or without it. The *lower* bound is reachable and is gated.

⚠️ **The soldier's own use of the gate survived too, and is closed now.** It stood
ungated because a rocket flies flat, so its arc is very nearly the straight line
the target search already cleared. What separates them is a **moving** target, and
the fixture had to be swept for: across every node pair on `ctf_truefort`, twelve
satisfy all three conditions at once — the chest-to-chest line clear, the
still-aim arc clear, and the **led** arc blocked. Node 0 → 214 at 417px is the
widest: the still aim is 319.7° and a target running at 6px/tick pulls it to
300.8°, **nineteen degrees into a wall**. The angular separation is the whole
fixture, so pick the widest rather than the first.

⚠️ **A caller test here must ask `BotClassKeys.For` and not the whole combat
step.** The trigger sits behind three other gates in `BotCombat` — the fire delay,
the aim settle and the hold-fire roll — so driving the pipeline measures those too,
and the hold-fire roll makes it intermittent. ⚠️ And the aim has to be **set on the
character**, because the arc is sampled from its live `aimDirection`: a defaulted
aim points right and the arc then has nothing to do with the target at all.

### Phase 7 item 4n: the mined doorway, and a radius that is not the one on the tin

`Gg2.Bots.BotMines`, the detonation in `BotClassKeys`. Gated by `BotMinesTests`.
The reference is `botMinesArmed.gml` and `botClassKeys.gml:89-93`.

✅ **A demoman springs a mined doorway on whoever walks into it.**

- ⚠️ **The question is never "is this mine worth it" but "is any of them".** The
  Minegun detonates **every** mine the player owns at once, so one armed mine is
  the whole trigger — which is exactly what a chokepoint full of them is for.
- ⚠️ **`BOT_MINE_TRIGGER` is measured against the mine's *damage* radius, not its
  affect radius, and that difference is the whole behaviour.**
  `Mine.events/User Event 2.xml` damages through `blastRadius` **40**, not
  `affectRadius` **65**. The reference's first version triggered at 60: the bot
  detonated the moment an enemy came near, blew its whole stock and dealt
  **nothing**. Reported from play as "detonates too early, doesn't damage
  enemies", which was exactly right.
- ⚠️ **A sentry and a generator are asked the same question with a different
  metric, and it is not a refinement — it is the only reading that works.** The
  reference's script scanned `with(Character)` and nothing else, so a demoman that
  had chosen a **sentry** as its target (`BotTargeting` takes sentry and generator
  targets, and `BotClassKeys` lobs at them) put its whole stock on that sentry and
  then never pressed SPECIAL, because no character was ever standing next to them.
  Reported from play as "shooting grenades at sentries, but not detonating".
- ⚠️ **A character is measured centre to centre and the other two box to box, on
  purpose.** It is the same 30 and needs no second constant: a character is ~6px
  of box either side of its centre, while a sentry is a wide object whose centre
  may be 30px from a mine sitting against its base. `distance_to_object` is what
  the engine's own damage block uses on those two.
- ⚠️ **The detonation carries no `hasSubject` test**, alone among the firing
  branches in `BotClassKeys`. A mined doorway is worth springing on whoever walks
  into it, and that is very often not the thing this demoman is aiming at.
- **A cloaked spy does not arm it.** Nothing else in the bot layer can see one —
  target selection skips a cloaked candidate — so detonating on one would be the
  single place a bot acted on information it does not have.

⚠️ **Measured this session, and the reference's own comment is only half of it: a
STUCK mine has no quarter-radius cutoff, so its "anything at 30 of box distance
takes nothing at all" is true of a mine still in flight and false of one lying in
a doorway.** The guard is `and other.splashThreshhold`, which the movement pass
clears on landing (CLAUDE.md, stage 6). Measured on `ctf_truefort`, one mine
against a scout, damage out of a full blast of 45:

| centre gap | airborne | stuck |
|---|---|---|
| 0 | 45.0 | 45.0 |
| 30 (the trigger) | 20.3 | 20.3 |
| 45 | **0.0** | 3.4 |
| 60 (the old trigger) | **0.0** | **0.0** |

So the trigger is conservative in both forms — at 30 it still buys nearly half a
blast — and at the 60 the reference started with, **both forms deal exactly
zero**. `BotMinesTests` asserts the damage rather than the constant, so a mutation
that puts the trigger back fails on what the number is *for*.

⚠️ **`ScenarioWorld.Place` snaps downward to a nav node, so a coordinate handed to
it is a request rather than a position.** Every distance in a fixture here is
measured from where the character ended up, and the **mine** is placed relative to
that — a mine's position is the only one a test controls. The first draft measured
from the requested coordinates and asserted a 45px gap that was really about 27,
which read as the trigger being wrong.

**Eight mutations tried and all eight bite**: the trigger at 60 (7 failures), the
sentry scan deleted, the generator scan deleted, the sentry measured centre to
centre, the cloak clause dropped, the owner test dropped, a corpse arming it, and
the detonation made to wait for a target.

### Phase 7 item 4o: the close-in step, and the one press no route validated

`Gg2.Bots.BotStep`, the close-in branch in `ScriptedBot.ApplyHolds`. Gated by
`BotStepTests`. The reference is `botStepSafe.gml` and
`botInputUpdate.gml:225-245`.

✅ **A pyro walks at its target instead of holding where the engage caught it.**

- ⚠️ **This is the only place in the bot layer that presses a movement key
  nothing validated.** Everything else that moves a bot sideways is either an
  edge the nav generator proved or a single step of back-off inside its own blast
  radius. A pyro closing on a target is neither, so it is walking on ground
  nothing has checked — and `ctf_truefort` has a pit beside the route, so a bot
  chasing an enemy across a gap walks into a hole it cannot climb out of and
  stays there until it dies.
- ⚠️ **Only the pyro, because only the pyro's weapon reaches a fraction of the
  distance it can see.** `ClassRange` is 200 and `BOT_FLAME_REACH` is 165, so the
  close-in step fires in a **35px band** — holding at acquisition range is holding
  out of range for ever. Every other class has `BOT_CP_CLOSE_IN` 0 and stands
  where the hold caught it.
- ⚠️ **`place_meeting` against `Obstacle`, never `place_free`.** The reference's
  own comment says why: `place_free` reads GM8's `solid` flag and the game toggles
  that flag underneath itself — `gunSetSolids` brackets every hitscan trace — so
  what it answers depends on where in the frame it is asked. This port has no
  global flag to toggle, but the *object list* still has to be the right one, and
  `CollisionFilter.ForSentry()` is this codebase's name for it (`wallSetSolid`,
  Obstacle and nothing else). **A team gate is neither a wall worth refusing nor a
  floor to stand on**, and using the character's own list makes a pyro refuse to
  walk through its own team's gate.
- ⚠️ **Two refusals, not one.** A wall ahead is *not unsafe* — there is floor
  under it — but the press achieves nothing and the follower's stuck detector
  counts it, so it is refused separately from the floor test.
- ⚠️ **The drop tolerance is deliberately short and is not "can the bot get
  there".** That is `navFindPath`'s question, answered against the graph; this one
  is only "does the ground continue", so a step down to a lower ledge reads as
  safe and a step over a pit does not. Without it a pyro on a raised slab can
  never close on anything.

⚠️ **Every fixture here was swept for rather than chosen, and none of them is a
nav node's own stand position.** The three cases look identical on a map
screenshot — ground that continues, ground that stops, and a solid that is not
ground at all. Swept across every floor row on `ctf_truefort`: **4,528 pit-edged
spots, 4,157 wall-edged and 1,110 where the choice of solidity list changes the
answer.** That last family is the only way to gate the filter and no fixture
picked by eye would have been one; `(682, 325)` steps left onto a **TeamGate**
with sound floor under it. `(1064, 325)` is the ledge — the pit beside it bottoms
out **216px** down, which is asserted, so the refusal is a real gap rather than a
near miss of the 28px tolerance.

⚠️ **A sweep for a standing position must not test `place_free` one pixel down.**
GG2 rests a character with its feet a pixel *above* the surface (the same fact
`navNodeFromWorld`'s rounding turns on), so "free at y and solid at y+1" is true
**nowhere on the map** — the first sweep found zero candidates of any kind and
read as the primitive being broken. The right filter is "free at y and something
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

- ⚠️ **A spot is chosen without checking reachability.** `BotGoalSpot` ranks nodes
  by distance, height and sightline and asks the graph nothing about whether a
  route exists — deliberately, because a path search per candidate is a search per
  bot per objective tick. So a spot in another component presents as a goal that
  never plans, and the ban is the only place that can notice.
- ⚠️ **The state is not reachable from any shipped map**, which is why the branch
  stood ungated for four items and said so in its own comment: **all 24 cached
  graphs are connected for the team that owns the ground.** A green suite was not
  evidence it worked.
- ⚠️ **The fixture is a real graph with one node's edges stripped**, rebuilt
  through the real cache format and the real reader. Isolating one node is the
  smallest two-component graph there is, and it keeps the spot search's candidate
  set *identical* — so the route is the only thing that changed and "the spot
  moved" cannot be mistaken for "the ban fired". `NavCacheTests.WriteCache` is
  `internal` and was already the byte builder; going back through
  `NavCache.Parse` matters because **both derived indices are bets on the
  generator's output** (edges sorted by from-node, nodes by row then x) and a
  graph assembled behind the parser could satisfy neither while looking right.
- ⚠️ **The ban legitimately advances every tick between the goal being issued and
  the next objective tick.** It fires on the goal's own `IsSpot`, which is only
  rewritten when a goal is actually *issued* — so for those 30 ticks the goal is
  still a spot and still unplannable. What must not advance is the window after
  the replacement lands, and a test that measured too early reads a correct
  implementation as a re-arming one. Measured: first ban at 301, settled at 330.
- ⚠️ **The ban is a delay, not a stop.** It lapses after 300 ticks and the bot
  asks for another spot — on this fixture the second perch is unreachable too, so
  the second ban is the evidence that a second spot was asked for at all
  (measured: 301 → 990 over three windows).
- ⚠️ **`HasGoal` is what separates "planning failed" from "arrived"**, since
  arrival clears the goal and frees the path together. A ban keyed on `!HasPath`
  alone punishes a bot for succeeding, and every later spot is then refused for
  ten seconds after every arrival — most of a bot's life.

⚠️ **Four traps in writing this, and three of them are about the observation
window rather than the code:**

1. **Stop inside the first ban window.** Running past it watches the bot pick a
   *second* unreachable perch and measures that. The first draft ran to 400
   against a ban that ends at 301.
2. **Assert the exact tick the ban lands on** (`1 + BOT_SPOT_BAN`), or moving the
   check below the objective-period gate is invisible — every "a ban happened"
   assertion still passes with it one period late.
3. **Compare against the objective's own `allowSpot: false` answer, not against
   the objective's coordinates.** The banned goal is not the point: it is the
   point *snapped downward to a node*, 207px away on `koth_valley`. A test that
   expected the raw point fails for a reason that has nothing to do with the ban.
4. ⚠️ **A mutation that leaves a private field assigned-but-never-read does not
   compile here** (warnings as errors), so "delete the `_goalIsSpot` clause" is
   not a runnable mutation and reports a clean pass from a failed build. Mutate
   the *data* instead — `_goalIsSpot = true` — which is the same claim and
   compiles. Same family as the `if (false)`/CS0162 trap.

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

- ⚠️ **It is the only safe way to spread a team out, and a per-edge random jitter
  is not.** The jitter was tried in the reference and deleted: it is
  mean-reverting, so over a sixty-edge route every candidate inflates by the same
  average and all eight bots got the identical route anyway. Occupancy works
  because it is structured — what it makes expensive is precisely what is crowded
  — shared, and coarse. **Do not re-add the jitter.**
- ⚠️ **A bot's own route comes out of the counts before it searches, and that is
  not an optimisation — it is what makes a route stable across re-plans.** A bot
  that can see its own route is charged extra for the path it is already walking,
  so every re-plan tries to move it and the whole team churns lanes every
  `BOT_REPLAN_TICKS`. The reference measured it on this map: with self excluded, 2
  of 16 re-plans changed a route; with a node-keyed penalty — which cannot exclude
  self cleanly, since a bot stands on its own nodes — **15 of 16**.
- ⚠️ **Per team, because the thing being spread out is a team.** A blue bot has no
  reason to avoid the route red is walking. One shared map would have each team's
  routes push the other's around for no reason anyone could see from the outside.
- ⚠️ **A removal goes to the team the route was counted against, never the bot's
  team now.** The two genuinely come apart: autobalance switches the
  lowest-scoring player on the larger team mid-round and can pick a bot, and a
  rotation re-teams every one of them. Reading the current team on the way out
  subtracts from the map the bot has just joined and leaves the old team's counts
  raised **for ever** — an edge nothing is walking that every future search on
  that team pays extra for.
- ⚠️ **A route is added in one place and removed in three** — the planner before
  it searches, arrival and death, and the bot leaving or the map changing — so the
  add is idempotent. ⚠️ **The negative-count danger is not what it looks like**:
  `if (was <= 0) remove` means a count is never *stored* negative, so a lone bot's
  double-decrement is invisible. What a double-decrement costs is **somebody
  else's** count, and that is the case a fixture has to build.
- ⚠️ **Keys are node indices, which only mean anything to one build of the
  graph**, so both maps are dropped on a rotation — before the bots are handed the
  new graph, since `ScriptedBot.SetMap` is what clears each bot's own bookkeeping.

⚠️ **One named divergence: this port keeps ONE field where the reference keeps
two.** `botOccupied` and `botOccupiedTeam` carry the same information and can
disagree; here `OccupiedTeam` **is** the flag (`Team.Spectator` means "counted
nowhere") and `Occupied` is derived from it. The reference's own
`botOccupancyReset` has to clear both halves in step; here one call does both.
**Found by mutation** — with two fields, deleting the flag guard in `Remove` left
every test green, because the team sentinel was already carrying it. Two guards
where either suffices is a guard that cannot be gated.

⚠️ **A fixture for this has to be swept for.** Most legs have one sensible route
and a 25% penalty on a few edges does not move them, so a leg picked by eye gates
nothing. Swept across every seventh start and eleventh goal on two maps:
`ctf_truefort` 0 → 143 goes from a 32-node route to a 14-node one with **one**
node in common.

⚠️ **And the two teams do not agree about a leg in the first place** — a gate is
passable to the team that owns it — so the per-team test compares blue's crowded
answer with **blue's own uncrowded** answer. Comparing it with red's fails on the
gates and says nothing about occupancy.

⚠️ **Re-measured after this landed, because one dictionary lookup per edge per
search is not free: twelve bots cost `0.154ms` a tick — 0.5% of the budget — so
the headroom claim is untouched.** And a fact nobody had written down: **that
number is configuration-dependent.** The same test in the default (Debug) build
reads **0.369ms, 1.1%**. `README.md`, `docs/ARCHITECTURE.md`, `docs/PLAN.md` and this file's header all
carry the figure with its build now; a re-measure that forgets to say which build
it ran looks like a regression of a factor of two.

**Nine mutations tried and all nine bite**: routes never counted, self not
excluded before the search, the add not idempotent, the route left counted after
removal, the removal reading the wrong team, zero counts left in the map, one map
shared by both teams, a dead bot still crowding, and an arrival leaving its route
counted. ⚠️ That last one needed a fixture that actually **arrives** — a bot
placed on its goal, on the ground and stopped — and without it the branch was
invisible: an arrived bot would leave its whole route crowded for ever.

### Phase 7 item 4s: the stab window, and the last unported script

`Gg2.Bots.BotStab`, the stab branch in `BotClassKeys.SpyKeys`. Gated by
`BotStabTests`. The reference is `botStabWindow.gml` and
`botClassKeys.gml:146-163`.

✅ **Every script in the reference's bot layer is ported.**
`BotLayerCoverageTests.Unported` is **empty**, and `docs/PLAN.md` says so.

- ⚠️ **The obvious test — "the enemy is within `BOT_STAB_RANGE` right now" — is
  aimed a full second early.** The press *freezes* the spy (`runPower` and
  `jumpStrength` to 0) and arms `alarm[1] = 32`; that alarm creates the hitbox,
  which lives `alarm[0] = 6` more ticks. So the damage window is **t+32 to t+38**
  and the spy cannot move during it. Against anything moving the naive press is
  close to a guaranteed miss, and it spends 38 frozen ticks in the open to take
  it.
- ⚠️ **The box is the sprite, not a radius.** `StabMaskS` is 64×64 with origin
  (26, 37), and the mask's Step sets `image_xscale` from `direction` without ever
  touching `image_angle` — so it is axis-aligned and merely **flipped**: 37px the
  way the spy faces, 26px behind, 37px up, 26px down. `BOT_STAB_RANGE` therefore
  stops being the trigger and stays what it always described, the reach.
- ⚠️ **Facing is `aimDirection >= 90 and <= 270`, inclusive at BOTH ends**, so a
  spy aiming exactly straight up or straight down faces **left**. Either bound
  written exclusive sends the 37px reach the wrong way on a vertical aim.
- ⚠️ **The target is grown by its own bounds rather than treated as a point**,
  because the mask collides mask-to-mask: a character whose centre is outside the
  box still overlaps it by a shoulder.
- ⚠️ **The prediction is floored on the graph.** A target running off a ledge is
  exactly the case a straight-line prediction gets wrong, and at knife range it is
  common — over 32 ticks a falling body covers hundreds of pixels. `FloorUnder`
  plus the same `FallPredict` the aim solver leads with; no node below is the same
  as no floor.
- **`readyToStab` is tested in the class policy rather than inside the window**,
  because it is much the cheaper of the two and gates the press regardless of
  geometry.

⚠️ **One clause is UNOBSERVABLE and is kept as a cost guard rather than a rule:
the `Cloak` test in the stab condition.** A stab and a shot are the **same key** —
the Revolver's `User Event 1` is an if/else on the cloak and there is no separate
melee input — so an uncloaked spy taking the stab branch would press exactly what
the shooting branch presses. Checked by mutation: deleting it leaves every test
green, and no fixture can separate them. What it buys is not computing a 7-tick
prediction and a graph walk for a spy that cannot use the answer. The reference's
condition carries it too.

**Seven mutations tried and six bite**: testing where the target is now, the box
as a symmetric radius, facing exclusive at 90/270, the target as a point, no floor
under the target, and `readyToStab` unchecked. The seventh is the clause above.

⚠️ **`BotClassKeys.For` takes a `NavGraph?` now**, for this one branch. It is a
parameter rather than something on `WorldView` because **the simulation must not
know the nav graph exists** — `WorldView` is a view over `WorldState`, and
docs/BOTAI.md's whole point is that read-only inputs are passed *to* the tick
rather than held by it.

⚠️ **`botPickClass` still excludes the spy**, so no bot on a live server is ever
one and none of this runs there. It is ported because the alternative — declaring
the layer complete with a hole in it — makes the coverage claim mean less than the
work of closing it cost.

### Phase 7 item 4t: auditing docs/BOTAI.md against the shipped code

`Gg2.Tests.BotAiConstraintsTests`, and the four indexed loops it forced in
`SpawnRooms`, `KillBoxes`, `MoveBoxes` and `ControlPoints`.

⚠️ **`docs/BOTAI.md` was written "while it is still specification" and its claims
had never been checked against the code that shipped.** Most of it is prose about
design intent, which no test can hold — but five are structural facts a single
careless change destroys silently, and three of those are the ones the document
marks as impossible to retrofit. They are asserted now.

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
- ⚠️ **`SimulationBehaviourVersion` is the third, added 2026-08-30, and the hole
  it fills was real for as long as the stamp existed**: every other entry is a
  constant, so `3bba987` changed a line of physics and the digest could not move.
  `gg2-rl` found it and asked. **Bump it when you knowingly change what the
  simulation does** — a trajectory, a damage number, a projectile path, a stage's
  position in `MatchSimulation`. ⚠️ **Exempt a change only when it is
  DEMONSTRATED unobservable**, the way `3bba987` was (two 90-iteration training
  runs, per-iteration numbers identical to the digit over 1.66M agent-steps), not
  when the argument for it merely sounds good. `SIM_UUID` `c526b96e34184138` →
  `7ff5940d97aa7bed`, and ⛔ **this is the first bump that invalidated real
  checkpoints** — a cost accepted in the ask rather than discovered afterwards.
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

✅ **Re-measured 2026-08-27, after nine items landed** — the medic's ally, the
projectile reflexes, suppression, the arc gate, the mine trigger, the close-in
step, the stab window, occupancy and the per-tick allocation fix. **Every one of
the fourteen legs is unchanged and the rocket-jump count is unchanged**, which is
worth knowing precisely because so much moved underneath them.

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
  `ScenarioSuiteTests.Unmet` is checked both ways and
  `BotLayerCoverageTests.Unported` asserts its own length.
- ⚠️ **A failure there is the re-record, not a broken test.** Check the change is
  one you meant, then that it went the way you meant, then update the row and say
  so in the commit — the discipline the pinned `SIM_UUID` asks for.

**Two mutations tried and both bite independently**: rocket jumps never offered
(the count), and `BOT_REPLAN_TICKS` nudged by one with the jump count unchanged
(the costs).

### Phase 7 item 4w: the end-to-end run, and a number that was wrong when it was written

`BotObjectiveTests.ABotWithNothingButAMapTakesTheEnemyIntel`, pinned.

⚠️ **The recorded figure was 1,747 and the test has printed 1,748 since the commit
that wrote it.** Bisected across nine commits — item 4l, 4k, 4j and item 4i itself
all read 1,748 — so it is a **transcription error rather than a drift**. It sat in
the test's own comment, in this file and in the lane handoff, and nothing could
have caught it: the assertion was `grabbedAt < 3000` against a leg that takes
1,748, which is **71% of slack**.

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

✅ **The rule was then swept across the whole lane**, which is the third and last
of its end-to-end measurements: `PathFollowerFixtureTests`' longest steerable leg
on `ctf_truefort`. **It had NOT drifted** — 137 nodes, 1,572 ticks, 33 re-plans,
0 stuck, 4 off-route, 0 blacklists, exactly as recorded — which is worth knowing
only because it was checked. But its bound was `ArrivedAt < 2400` (already the
*tightened* version, "half again on the measurement") and **three of its five
numbers were asserted by nothing at all**. Measured: nudging `BOT_REPLAN_TICKS`
by one takes that leg to **1,628 ticks and 5 off-route events**, which the old
bound passes silently. All five are pinned now.

⚠️ **Zero blacklists is the load-bearing one of the five.** It read 9 before the
`NAV_EDGE_BUCKET` truncation was found, and a bot that starts banning edges again
still arrives — just slowly, inside any budget, with the route quality gone and
nothing red.

**The three end-to-end measurements in this lane — the scenario suite, the intel
run and the longest leg — are all pinned exactly now**, in both directions, each
saying in its own message that a failure is the re-record. Two of the three were
hiding something; the sweep is what established that the third was not.

### Item 12: the rollout-search bot, and the assumption it is built on

`Gg2.Bots.RolloutSearch`, landed 2026-08-28 for `gg2-rl` phase 1, which was
blocked on it. `docs/BOTAI.md` item 12 is the requirement; this is what it cost
and what to know before touching it.

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

#### ✅ The conga line was the wrong fear, and it is measured

This section used to record "walk at the nearest enemy is the obvious answer and
is how you get eight bots in a conga line" as the reason not to do it. **It is the
other way round.** Each bot picks its *own* nearest enemy and then a spot that
prefers to walk less and rewards height, so they distribute rather than stack.

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
inside my own minimum band"**. Adding one is the exact mirror of the pyro's rule
and was **tried on 2026-08-28 and reverted**: it changed *nothing* measurable —
not the scenario suite's exact tick costs, not the intel run, and not the case it
was written for. **Something else pins the bot**, and until that is found the
condition would be an ungated change to shipped behaviour with no demonstrated
effect. Finding it is the next step, and `gg_debug` idle-vs-scripted is the
repro.

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

**2026-08-28.** `gg2-nav-gen` shipped a sticky-jump edge fan (6,497 edges across
24 graphs, ~1,600 node-openings) and flagged one thing to do before trusting it:
**`NAV_SJ_IMPULSE = 10` is derived, not measured.** Its own reason for the
warning is the best argument for taking it seriously — the rocket jump's derived
number promised 276 px, delivered 206, and *"every offline check stayed green
while the follower blacklisted the edges."*

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

**Landed 2026-08-28**, after the measurement above. `NavEdgeType.StickyJump` was
the last unhandled edge kind; it now steers.

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

#### ✅ ANSWERED — the arm was never asked, and the fault was not the arm's

**Closed 2026-08-28**, after six hypotheses had been eliminated by measurement
and every one of them turned out to be correct. They were all asking whether the
arm *refused*. It was never *called* for a sticky edge at all.

| checked before the answer | result |
|---|---|
| `StickyModel.Reach` vs its `flyDistanceRocket` at v0 = 0 | **identical** — 191.10 vs 191.1, 31.2, 11.7, 12.4, 37.7 |
| `need` (near edge of the landing run) vs its recomputation | **identical** on all six — 192, 18, 12, 12, 12, 36 |
| the takeoff-gate arithmetic `reach + JumpReachTol >= need` | **passes on all six** |
| `SnapObjective` on the landing run's midpoint | returns the intended node on all six |
| the flight harness's start position | resolves to the from-node every time |
| the dispatch switch | `UnsupportedEdgeTicks` zero, so the type is seen |

⚠️ **The cause is `PathFollower.TryFindEdge`, and it is not a sticky-jump bug.**
A route is a list of **nodes**, so the follower re-derives which edge each hop
meant — and it took the **first** the edge index yielded. `NavSearch` refuses per
**edge**. A sticky cache carries a rocket jump beside the sticky jump on **2,460
node pairs** and a double jump beside one on **1,124 more**, and the rocket jump
is what the index yields first. A demoman carries a Minegun, so
`RocketModel.Ready` was false, the gate never opened, and the bot stood on the
takeoff column waiting for a launcher it does not have. **`StickyModel.Ready` was
never consulted once.**

**Instrumenting which edge the follower believed it was on** — the one question
nobody had asked — showed hop `25->1 RocketJump` for a demoman on a sticky
route, in the first minute of looking. ⚠️ **Every one of the six earlier
measurements was consistent with that**, because each of them was computed from
the sticky edge by hand rather than read out of the follower.

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
edge**, which is worth fixing before the next sticky measurement rather than
during it.

⚠️ **The second half of that paragraph used to name a cause, and it was wrong.**
It said that "on `ctf_truefort` and `cp_egypt` the takeoff column does not
resolve to the from-node so the route starts somewhere else entirely". Measured
2026-08-28 over every arc edge in every cache: **the takeoff column resolves to
its own from-node 18,396 times out of 18,396** on the shipped graphs, and 4,132
of 4,132 on the sticky one. `EdgeFlightTests.EveryArcEdgeResolvesFromItsOwnNodeAndFromItsTakeoffColumn` is the gate. Whatever that hand-rolled harness started
from, it was not the takeoff column either — and **the correction is the ordinary
one: a plausible mechanism was written down beside a real symptom, and nothing in
the repo could contradict it** (CLAUDE.md, "a claim the repo makes about
itself").

### ✅ The flight harness — `EdgeFlights`, and the honest number

**2026-08-28.** `src/Gg2.Bots/EdgeFlights.cs` is the harness that paragraph asked
for, in the library beside `ScenarioWorld` rather than in a test, because the
lane that generates a cache is the one that wants to sweep it:

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
of them. `gg_debug`: 20 arrivals, 12 flights. `ctf_truefort`: 328 arrivals, 214
flights. The other arrivals are node pairs the bot could simply walk between, and
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
over `Source/build/botnav`, where a rocket jump and an air jump are the arcs that
exist.

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

### ⚠️ NO BOT CAN ZOOM, so every bot sniper plays at half its weapon

**2026-08-29, found chasing `gg2-rl`'s sniper hole and NOT the cause of it.**

`toggleZoom` is not a key. `PlayerCommands.ToggleZoom` says so in as many words —
*"Zoom is not a key. It is its own client command"* — and a bot's entire output is
`PlayerInput`, which is `PlayerKeys` plus an aim. So **no `IInputSource` in this
port can zoom**: not `ScriptedBot`, not `RolloutSearch`, and not a learned policy,
because `EnvAction`'s five heads have no zoom either.

What that costs, from `Weapons.RifleBeginStep`:

| | damage |
|---|---|
| unscoped (`RifleUnscopedDamage`) | **35** |
| scoped, uncharged (`RifleBaseDamage`) | 45 |
| scoped, full charge (`RifleMaxDamage`) | **75** |

**Every bot sniper in this port fires at 35 for ever**, against a weapon that
reaches 75. And `BotClassKeys`' sniper branch reads `self.Zoomed`, which is always
false for a bot — so its charge-proportional-to-range arm, and the comment
explaining it, are **unreachable code**.

⚠️ **This is the mirror of CLAUDE.md's standing rule and the rule does not cover
it.** That rule says a bot must not be able to do something a client cannot. Here
a **client can do something a bot cannot**, which is just as much a gap in the
seam — and it is invisible to every test, because nothing asserts a bot can
express the whole action space.

✅ **CLOSED 2026-08-29**, on the user's call, with `gg2-rl` holding phase 2 so
the bump landed before any checkpoint existed to invalidate. What it took:

⚠️ **The seam already had the channel, and my first write-up of this was wrong
about the cause.** `ICommandSource` has existed since phase 7 and `ScriptedBot`
already implemented it. Two things were actually missing, and the second is the
one nobody would have guessed:

1. **Nothing ever pushed `ToggleZoom`.** `ScriptedBot.WantsZoom` does now — an
   edge, not a level, because `PlayerCommands.ToggleZoom` *flips*: pushing it
   every tick a sniper has a target scopes and unscopes at 30 Hz and holds a
   charge of zero for ever, which is a worse sniper than the unscoped one.
2. ⚠️ **`ScenarioWorld` did not poll `ICommandSource` at all**, so a duel or a
   scenario **dropped every command a bot emitted**. `GameServer` and
   `HeadlessMatch` both poll it; the harness where this gets measured did not.
   Even a bot that asked to zoom would have been ignored.

**And the policy half**: `EnvAction` gains a sixth head, `Zoom`, moving
`ActionEncodingVersion` 1 → 2, `SIM_UUID` `61edbca3899d1b07` →
`c526b96e34184138`, and `EnvProtocol.Version` 3 → 4 (six action bytes per agent,
`ComponentBytes` 44 → 48).

⚠️ **A LEVEL, not a toggle.** The head is the state the agent wants and
`ActionDecoder.WantsZoomToggle` owns the edge, so the head is idempotent and a
policy needs no memory of its own scope. A toggle-shaped head would turn one
dropped or duplicated action into a permanently inverted sniper.

#### ⚠️⚠️ KNOWN ISSUE: the search's AIM is decided by its tie-break, not its scoring

**2026-08-29, and it is the largest unexamined thing in `RolloutSearch`.**
`gg2-rl`'s corpus analysis — possible only once `ab78c9d` put the candidate
actions on the wire — measured, on rows that are **not** blind:

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

##### ✅ FIXED 2026-08-29 — a lexicographic aim tie-break

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

#### ⚠️⚠️ KNOWN ISSUE: the scope makes the scripted sniper WORSE, by about 10%

**2026-08-29. Reported by `gg2-rl`, reproduced here independently, and both of us
predicted the opposite.** Giving the bot a capability cost it damage.

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

⚠️ **`gg2-rl`'s framing, and it is the right one: a rule that has never executed
against live play is not a tuned rule, it is an untested one.** This port prefers
the `BOT_*` constants because they have real playtesting behind them — but this
arm **could not have been played, by construction**, for eight phases, because
`self.Zoomed` was always false for a bot. It is new code that has just met its
first opponent.

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

#### ⚠️⚠️ The search does NOT use the new head at its default horizon

**Raised by `gg2-rl` before the change landed, and it was right.** `ScriptedBot`
uses the scope immediately — its rule is "have a target, be scoped".
`RolloutSearch` only uses what pays inside its horizon, and **the rifle's 40-tick
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

`RolloutSearch`'s own remark has always said a horizon shorter than a refire timer
makes every candidate score zero, so the tie goes to the first and the bot stands
still. **The rifle's refire is 40 ticks against a 20-tick horizon — the longest in
the game, and the only one at double it** (rocket launcher 30, minegun 26,
revolver 18, the rest under 6). That made a tidy hypothesis for `gg2-rl`'s
sniper-v-sniper hole.

⚠️ **It is wrong, and the measurement says so.** `ctf_2dfort` banded 150-350px,
12 mirror duels, 900 ticks, tier 5:

| class | horizon 20 | horizon 45 |
|---|---|---|
| Sniper (refire 40) | 0-2-10 | **0-5-7** — worse |
| Soldier (refire 30) | 0-0-12 | **3-1-8** — much better |
| Scout (refire 8) | 4-0-8 | 5-2-5 |

**Lengthening the horizon past the rifle's refire made the sniper worse.** So the
blindness is real and the sniper hole survives it.

#### ❌ RETRACTED 2026-08-29: "much better for the soldier" does NOT reproduce

⚠️ **Twelve duels a cell was not enough to read a direction from, and this section
said so at the time and then read one anyway.** `gg2-rl` re-ran at **80 duels a
cell**: the soldier goes **93.8% to 78.8% at horizon 45** — worse, not better. The
sniper's direction survives; the soldier's inverts.

⚠️ **The retraction matters more than the number.** That row was quoted into
`RolloutSearch`'s remarks, `DuelSpec.RedHorizonTicks`' remarks and a peer's
planning notes before anybody re-measured, and every one of them said "12 duels"
honestly. **Labelling a sample size does not make a direction read off it safe to
propagate.**

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

### ⚠️ A CHECK THAT CANNOT DISAGREE — the same bug in four places now

**2026-08-29, and a fourth the next day.** Four defects were one shape:
**something compared against a copy of itself.**

| where | the comparison |
|---|---|
| `EnvProtocol.Version` | the wire's layout checked against the constant that described it, both edited by the same hand |
| `HelloAgreesOnTheVersionsAndTheShape` | the advertised head count asserted against a **literal 5** — the advertisement checked against a copy of the advertisement |
| a check *this repo proposed* to `gg2-rl` | the policy's per-head argmax compared against "its highest-probability action under the full joint implied by the marginals" |
| ⛔ `gg2-rl`'s `scripts/fingerprint.py`, **2026-08-30** | the tool written to catch a bump that was supposed to change nothing, hashing `batch.observations` **without ever calling step** |

⛔ **The fourth is the one worth the most.** Asked whether `3bba987` moved the
world, it answered "OBSERVATIONS IDENTICAL: the world did not move under this
bump" — and **it could not have said anything else**, because it hashed where each
duel *starts* and nothing that happens afterwards. It reported a true conclusion
for a reason that had nothing to do with the question, and it is the instrument
built specifically to catch this class of error; its own docstring already
confessed to committing it once, on the zoom bump. ✅ That lane fixed it with a
separate `motion` hash over stepped decisions and **falsified the instrument
before believing it** — identical against itself, `PHYSICS MOVED` under a
deliberately perturbed trajectory. ⚠️ **A verification tool is not exempt from
the shape it exists to catch, and "it agreed with me" is the least informative
outcome a check can have.**

⚠️ **The third is the purest and it was mine.** The joint implied by independent
marginals **is their product**, and the argmax of a product of independent
marginals **is** the per-head argmax. Over a full cross-product candidate set the
two objects are identical by construction and would agree **100% of the time on
every state**. It would have looked like strong evidence against the coupling
hypothesis and been evidence of nothing. `gg2-rl` worked that out rather than
running it.

- ⚠️ **Before running a check, ask what result would falsify it.** If no input
  produces disagreement, it is not a measurement.
- ⚠️ **The dangerous version agrees for a REASON that is not the one you want.**
  All three of these would have passed happily and been reported as confirmation.
- ✅ The vacuous-pass audit run earlier the same day is the same instinct applied
  to the test suite, and it needed **three attempts** before the detector was shown
  to see anything — twice reporting a clean bill from a script that had matched no
  files at all.

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

### ⚠️ A CORRECTION IS A CLAIM TOO, and I published one on an unexamined number

**2026-08-29, and it is the sharpest process lesson of the day.** `docs/ENV.md`
asserted compounding error as the shadow slot's rationale. When `gg2-rl` reported
a distance measurement suggesting the policy was *not* off-distribution, that
claim was **marked wrong and the correction committed** — and the correction was
built on **raw Euclidean distance over the unnormalised 209-float observation**,
which weights every column equally where a policy does not.

⚠️ **The operational measure said the opposite**: label-transfer from
policy-visited states to their nearest control state retains **36%** (0.204 →
0.074). The states **are** meaningfully uncovered. **So the retraction was wrong,
not just the original claim.**

⚠️ **The same shape as the twelve-duel horizon figure, with the roles reversed.**
There, a direction was read off a small sample of this repo's own and propagated
into three documents and a peer's notes. Here, a *peer's* unexamined number was
propagated into this repo's docs **as a correction**, which is the form least
likely to be re-questioned — a retraction reads as the careful thing and gets less
scrutiny than the assertion it replaces.

- ⚠️ **A correction inherits the burden of proof, it does not discharge it.**
- ⚠️ **Ask which space a distance was computed in.** That one question is the only
  reason this was caught, and it is worth asking of any "these two sets are close"
  claim over a feature vector nobody normalised.
- ✅ **Both lanes flagged their own error to the other unprompted.** That is what
  made two rounds of correction cheaper than one round of drift.
- ⚠️ **The operative rule is narrower than "scrutinise corrections"**, and it is
  `gg2-rl`'s sharpening: **a correction sourced from the other repo deserves the
  same interrogation as a feature request from it.** A feature request gets
  questioned as a matter of course; a correction arrives wearing the costume of
  care. ⚠️ **Both lanes failed this in the same exchange** — that one took this
  repo's cross-product fact at face value, and it happened to be right.

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
| `PathFollowerFixtureTests`' longest leg | 137/1,572/33/0/4/0 | unchanged |

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
