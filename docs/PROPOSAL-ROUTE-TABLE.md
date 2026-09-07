# Proposal: verified precomputed routes

**The prior-art search this document asked for has been run, and the answer
is below under "Decision".** Sections 1-7 are kept as written — the record of
what was known and what was guessed before the search — and are not updated to
match the decision. The implementation is `NavSearch.Find`'s CPD branch and
`NavDistances.NextHop`; its rules and measurements live in `docs/TRAPS-BOTS.md`,
"CPD-Search: the next-hop table as the ANSWER, verified per query".

## Decision (2026-09-04): implement CPD-Search, not the two-stage design

**It is a known technique**: Bono, Gerevini, Harabor, Stuckey, *Path Planning
with CPD Heuristics*, IJCAI 2019 — "CPD-Search". Same object (an all-pairs
first-move table), same assumption (costs perturbed **upward only** between
planning episodes), same lower bound (the static CPD path cost — already shipped
here as `NavDistances`), plus one thing the proposal did not have: the CPD path
priced at **current** costs is an **upper bound** and an incumbent solution.
Search terminates when the frontier's `g + h` reaches the incumbent's cost.

What that changes against sections 3-5:

| proposal | decision |
|---|---|
| walk the chain; if dirty, discard it and run A* from scratch | the walk is an incumbent that bounds the search; walks from expanded nodes share a memo. **A miss does not pay twice.** |
| U1, "is the walk cheaper than the search? decides everything" | the first hop falls out of the reverse Dijkstra already run; the table is laid out `[goal][from]` so a walk reads one contiguous row. Measured: **7.16x on a clean graph** (one pop, ~1 µs, 19 of 22 graphs) and **1.06x under a heavy crowd** — the walk was never the cost; proving optimality against a crowd is. Numbers in TRAPS-BOTS |
| A1, per-ability tables "argued" | settled by an existing measurement — 323,969 of 471,969 `ctf_truefort` pairs are strictly tighter for a walker — and `NavDistanceSet` already builds the 2-4 profile tables |
| A2, gates and teams need no table | confirmed: a shut gate is a +∞ perturbation the search absorbs |
| "scope collapse": only where occupancy is off | **in scope**: a ×1.25 hop is a <1% perturbation; exact search explores a corridor round it |
| optimal answers are "a change" | yes, and deliberately: with the table the search runs **unweighted**. A weighted (`ε = 1.3`) termination would accept the unperturbed chain and erase occupancy's dispersion — see TRAPS-BOTS |

**What stays true from the proposal**: the amortisation problem (`eval` at four
matches a map was a wash for `--nav-distances`, and the next-hop table costs
nothing extra to build, so the wash is unchanged until rows are derived lazily
per goal); the ceiling (search is ~26-30% of a twelve-bot tick, so the tick
cannot gain more than ~1.4x whatever the search does); and that every route
digest and the three pinned bot measurements move.

**Leads from section 6, resolved**: CPD — the match, above. ALT / true-distance
heuristics — what `--nav-distances` already is; the baseline. HAA\* (Harabor &
Botea 2008) — the named precedent for one shared graph validated per agent
capability; supports A1/A2, not something to build. D\*/LPA\* — the competing
design; loses here because costs are per-team and per-bot and goals change
155-175 times per 1000 ticks. CPD repair (Verzeletti, Botea, Zanella 2019) —
repairs a shared table after obstacle changes; wrong shape for per-bot bans.
CRP/CH — the million-node problem; not this one. Tree Cache / JSTS (SoCS 2025)
— O(N) storage at 20% suboptimality; not needed at N=687.

---


Every claim below carries its evidence status. **Do not quote a claim without
its status marker** — the difference between the measured group and the rest is
the whole point of the document.

Companion artifact (same content, formatted):
`https://claude.ai/code/artifact/6fe34f67-7bc7-4738-9f39-8be5c638c8cf`

## The idea in one paragraph

Precompute, for every ordered pair of nodes in a small navigation graph, the
**first hop** of the cheapest path between them — an all-pairs next-hop table,
built once when the level loads. At query time, reconstruct the candidate path
by following next-hops, and **walk it once to verify that every edge on it is
still both legal for this agent and currently un-penalised**. If the walk is
clean, the path is provably optimal and is returned without any search. If the
walk is dirty, discard it and fall back to a normal A\* search. The tables are
built on a deliberately **permissive** graph, and the verification walk is what
makes a permissive answer safe for a restricted asker.

## 1. The setting, and why it is unusual

The graph is **small**: a few hundred nodes and a few thousand directed edges
per level (687 nodes / 6,747 edges on `ctf_truefort`). This matters, because it
is what makes all-pairs preprocessing affordable at all. Techniques designed for
road networks with millions of nodes are solving a different problem.

Three properties make the setting awkward, and they are the reason a plain
lookup table does not simply work:

- **Edge costs mutate during play, on purpose.** Every edge a team-mate is
  currently traversing is priced 25% higher for the rest of that team
  (`BotConstants.OccupancyCost`). This is not environmental noise — it is a
  deliberate behavioural device to stop a squad walking single file, and it is
  worth a large part of the observed spread (0.634 → 0.785). **Any scheme that
  requires static costs is proposing to delete a feature.**
- **Agents have heterogeneous movement abilities, and they change mid-life.**
  Some agents can double-jump; some can rocket-jump at the cost of 30 hp; some
  can do neither. Crucially the rocket-jump capability is **a function of current
  hit points**, so an agent's reachable edge set shrinks as it takes damage and
  recovers when it heals. Jump edges are about **14% of all edges** and they are
  shortcuts.
- **Agents individually ban edges at runtime.** An agent that fails to traverse
  an edge twice blacklists it *for itself* for 150 ticks. So the edge set is
  per-agent and time-varying even with no other agents present.

⚠️ **Also worth stating for matching purposes**: the incumbent search is
deliberately *suboptimal* — a weighted A\* (`NavConstants.SearchWeight = 1.3`)
with closed-set re-expansion disabled. So a Dijkstra-derived table would not
reproduce current behaviour; it would return strictly **better** paths.
"Optimal" here is a change, not a restoration.

## 2. The structural argument the proposal rests on

Everything turns on one observation, which is elementary but load-bearing. It is
the reason the tables can be built permissively and still be safe.

> **Subset optimality.** Let `G_p = (V, E_p)` be a permissive graph and
> `G_a = (V, E_a)` an asker's graph with `E_a ⊆ E_p` and identical costs on
> shared edges. If `P` is a shortest path from `s` to `g` in `G_p`, and every
> edge of `P` lies in `E_a`, then `P` is a shortest path in `G_a`.
>
> *Proof.* Any `s–g` path in `G_a` is also one in `G_p`, so costs at least
> `c(P)`; and `P` is available in `G_a` at cost `c(P)`. ∎

The consequence is what makes the design cheap: **one table can serve many
different agent types, and the verification walk converts "possibly wrong" into
"provably optimal or explicitly rejected".** There is no middle state where an
agent silently uses a worse path.

✅ The same inequality in its other direction is **already deployed and
measured**: because every runtime effect (congestion pricing, edge bans, closed
gates) can only raise costs or remove edges, a *distance* measured on the
permissive graph is a valid lower bound for every asker forever. That ships
today as an A\* heuristic (`--nav-distances`, `docs/TRAPS-BOTS.md`, "Shipping
the precomputed distances") and is worth 1.36x on search time. **The proposal
here is to exploit the same relaxation for the path itself rather than only for
the bound.**

## 3. The design

```
query (s, g, asker)
      │
      ▼
next-hop table  ──►  verification walk  ──► every edge legal ──► optimal path,
(permissive, N²)     (~60 hops; checks                          no search run
                      abilities, gates,
                      per-agent bans,  ──► any edge illegal ──► fall back to A*
                      congestion)           or banned            with the exact
                                                                 distance heuristic
```

**The verification walk is the only novel-feeling component.** It is what lets a
single permissive table serve restricted askers without ever returning a
suboptimal path. The cost of that safety is that a miss pays for the walk *and*
the search, which is why the hit rate decides everything.

## 4. Claims, separated by how well established they are

### Established

- **C1 — subset optimality holds.** `[proved]` Elementary; stated above. The
  corresponding statement for *distances* is already relied on in production.
- **C2 — without congestion pricing, 98.4% of paths remain valid.** `[measured]`
  24,289 installed paths across 21 levels, 3 seeds, 12 agents
  (`RouteDiversityMatchTests.HowOftenAStoredRouteWouldStillBeValid`). With
  congestion pricing active the figure collapses to **23.5%**. The residual 1.6%
  in the clean case is the per-agent edge bans, which run regardless.
  ⚠️ **This measures validity against bans only, not against ability
  legality** — see A1.
- **C3 — storage is not the obstacle.** `[measured]` A next-hop table is N² —
  0.9 MB as 16-bit indices on a 687-node level, against 1.9 MB for the 32-bit
  distance table already shipped. Only storing whole paths explicitly would be
  large, and nobody proposes that. Preprocessing is the same single-source
  sweep that already builds the distances; the first hop falls out of it.
- **C4 — path quality improves rather than degrades.** `[measured]` Because the
  incumbent search is deliberately suboptimal, exact answers are better than
  what it returns: with the distance heuristic, path cost went from 103.6% to
  99.1% of the incumbent's own best. A table is optimal, so it should do at
  least as well. ⚠️ It is still a behaviour change and moves every recorded path.

### Argued but not measured

- **A1 — per-ability tables are required.** `[argued]` Paths run 60–68 hops and
  jump edges are 14% of all edges, so a single fully permissive table would hand
  a walking-only agent a path containing an illegal hop almost every time,
  driving its hit rate near zero. The argument is strong but the hit rate per
  ability class has not been measured.
- **A2 — gates and teams do NOT need their own tables.** `[argued]` By C1, a path
  that survives the verification walk is optimal whatever removed the edges it
  avoided. So team-specific and phase-specific edge removals cost hit rate rather
  than correctness, collapsing a would-be 16-table product back to roughly 4.
  Unmeasured: how much hit rate.

### Unknown

- ⛔ **U1 — is the walk actually cheaper than the search? This decides
  everything.** A ~60-hop reconstruction is ~60 random reads into a
  megabyte-scale array, i.e. largely cache misses. The competing search already
  has an exact heuristic and on some levels expands very few nodes. On
  `ctf_waterway` the heuristic cut expansions 285→85; on `ctf_truefort` it barely
  moved them (564→541). **It is entirely possible the walk loses on exactly the
  levels where search is already cheap.**
- **U2 — does the hit rate hold at query time?** C2 sampled paths already
  installed, which is a proxy for the hit rate at the moment of planning, not the
  thing itself.

⛔ **One number that should not be cited against this, and was.** An earlier
informal experiment reported a blended "table, else search" scheme at **0.7x** —
slower than searching. That experiment assumed a hit rate below 10%, so it paid
for the walk *and* the full search nine times in ten. The measured hit rate in
the congestion-free case is 98.4%, which inverts the arithmetic entirely. **The
figure is not evidence about this proposal**, and neither it nor the
"0.7–20% of queries" figure beside it was ever in the repo.

## 5. What would kill it

- **U1 resolving the wrong way.** If reconstruction costs more than a
  heuristic-guided search on typical levels, the idea is dead on arrival and no
  amount of hit rate saves it.
- **Per-ability hit rates being low.** If even ability-matched tables miss often
  — because bans and gates bite more than expected — the fallback path dominates
  and the blend loses.
- **Capability churn.** The rocket-jump ability is health-dependent and therefore
  flickers during a life. If agents cross ability boundaries often, they thrash
  between tables, and each crossing is a table whose hit rate is unclear.
- **Scope collapse.** The technique only applies where congestion pricing is off.
  That is real — it covers `eval` and every training rollout, which is where
  throughput actually matters — but it is explicitly **not** the live game, so
  the payoff is confined to offline workloads.

## 6. Leads for the prior-art search

⚠️ **These names come from recall and were NOT looked up.** The confidence label
describes how sure I am that the family exists and is relevant — **not** that it
contains this exact result. Treat every attribution as a search seed, not a
citation, and verify before repeating.

| lead | confidence | why, and what to ask of it |
|---|---|---|
| **Compressed Path Databases (CPD)** | closest match | All-pairs *first-move* tables, compressed, with paths reconstructed hop by hop at query time — the same core object this proposal calls a next-hop table. Associated with Adi Botea and with the Grid-Based Path Planning Competition. **Start here.** Ask: do CPD variants handle *dynamic edge costs*, *per-agent edge bans*, or *heterogeneous agent capabilities*, and is there a published verify-then-fall-back hybrid? |
| **True-distance / differential heuristics; ALT** | high | Precomputed exact or landmark-based distances as admissible A\* heuristics — ALT (A\*, Landmarks, Triangle inequality), associated with Goldberg and Harrelson; differential/true-distance heuristics with Sturtevant and Felner. This covers what is *already shipped* here, so it is the baseline the proposal must beat, not the proposal itself. |
| **Customizable Route Planning; Contraction Hierarchies; hub labelling** | high, different scale | Road-network speedup techniques. **CRP is the interesting one**, because it explicitly separates metric-independent preprocessing from cheap re-customisation when edge costs change — structurally the congestion problem here. Associated with Delling and co-authors; CH with Geisberger, Sanders, Schultes. Caveat: these target millions of nodes, where all-pairs is impossible; here N is a few hundred, so their central constraint does not apply. |
| **Annotated / capability-aware hierarchical pathfinding** | medium-high | Pathfinding for heterogeneous agents where edges are annotated with which agent types may traverse them — associated with Harabor and Botea. Directly relevant to A1 and A2. Ask: **is "compute on a permissive graph, then validate per agent type" already a named and analysed pattern?** That is what this proposal leans on hardest. |
| **Incremental / dynamic search: D\*, D\* Lite, LPA\*** | high | The established answer to changing edge costs is to *repair* a path rather than recompute or discard it — associated with Stentz, and Koenig and Likhachev. Relevant as a **competing design**: repair may dominate verify-then-fall-back, especially in the congested case this proposal gives up on. Has anyone compared the two directly? |
| **Congestion-aware / diverse multi-agent path planning** | medium | The congestion pricing here exists to produce path *diversity*, not to model traffic. Try: congestion-aware MAPF, flow-based multi-agent routing, k-diverse or dissimilar path planning. Ask whether anyone achieves crowd dispersion by a *static* mechanism that preserves precomputability — this project tried that (static per-edge "lane" fields) and measured it ~3x less efficient than reactive congestion pricing. |
| **Weighted A\* and bounded-suboptimal search** | high | Background for why the incumbent is deliberately suboptimal — associated with Pohl and the later bounded-suboptimal literature. Relevant to C4. |
| **Path caching and reuse in game AI** | low (naming) | Practitioner literature on caching computed paths and invalidating them on world change. Likely in game-development sources rather than academic ones, and likely where "verify then fall back" shows up informally if it shows up at all. |

**The most useful negative result** would be evidence that verify-then-fall-back
has been tried and loses to incremental repair (D\* family) or to plain
heuristic search, and on what graph sizes. **The most useful positive result**
would be a CPD variant that already handles per-agent edge removal, since that
is exactly A1 and A2.

## 7. Summary of the measured context

| quantity | value | status |
|---|---|---|
| graph size, `ctf_truefort` | 687 nodes / 6,747 edges | measured |
| typical path length | 60–68 hops | observed |
| jump edges as share of all edges | 14% | measured |
| search as share of a 12-agent tick | ~30% | measured |
| paths still valid, no congestion pricing | 98.4% | measured, 24,289 paths |
| paths still valid, congestion pricing on | 23.5% | measured, 24,289 paths |
| speed-up already banked from exact distances as heuristic | 1.36x | measured |
| path cost vs incumbent, with exact distances | 99.1% | measured, 21 levels |
| **reconstruction cost vs search cost** | **—** | **not measured** |

---

Measurements: 21 shipped levels, 3 seeds, 12 agents, Release build, 2026-09-04.
In-repo detail: `docs/TRAPS-BOTS.md`, "Precomputing Dijkstra" and "Shipping the
precomputed distances".
