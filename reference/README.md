# Reference data

`cs2-ecs-components.json` — a dump of Cities: Skylines II's ECS component
definitions (928 components with field names and types), from
[captain-of-coit/cs2-ecs-explorer](https://github.com/captain-of-coit/cs2-ecs-explorer).

`cs2-ecs-systems.json` — the companion dump from the same project: 693 ECS
systems with the component types each reads and the system-to-system
dependency edges (`uses_system` / `used_in_system`). Vendored for the
spatial-economy project (see
[`cs2-spatial-demand-economy-implementation.md`](../cs2-spatial-demand-economy-implementation.md)),
whose replacement targets — demand, land value, rent, leveling, trade — are
all managed systems named here. The dependency edges independently confirmed
design-doc background claims (e.g. `ZoneSpawnSystem` reads all three demand
systems plus `Game.Net.LandValue`; `ResourceExporterSystem` runs through
`PathfindSetupSystem`). Caveat: this dump predates Economy 2.0 (June 2024)
and under-covers the `Game.UI` namespace — treat as a strong prior and
re-verify by decompile on the game machine.

Vendored because it is the ground truth that lets `CS2Path.Mod` be written
against **verified** field names without the game installed. It already caught
one real bug (`Game.Prefabs.CarLaneData` carries no speed limit, so a fallback
through it would not have compiled) and supplied the live-metric source
(`Game.Net.LaneFlow`, `Game.Net.Density`).

Check a component before writing code against it:

```bash
python3 -c "
import json,sys
d=json.load(open('reference/cs2-ecs-components.json'))
k=sys.argv[1]
e=d.get(k) or next((v for n,v in d.items() if n.endswith('.'+k)), None)
print(k+':' if e else k+' NOT FOUND')
[print('  ',p['name'],':',p['type']) for p in (e or {}).get('properties',[])]
" Game.Net.CarLane
```

This is a snapshot of a community-maintained dump and can lag game patches —
treat it as a strong prior, and let the build on a machine with the game be the
final arbiter.
