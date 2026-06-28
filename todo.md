# Map Tool — Feature Backlog

## Workflow
- [ ] Add auto tiling setup (I wanna use this for fences for example where the system has some idea on how to tile together specific tiles)
- [ ] Layers (so I can have for example a "terrain" layer and then build on top of that)
- [ ] Procedural generation for layers. Basically the idea I had in my head is to build a terrain but then have rules and procedural rules choose the correct tiles. So it's almost like a painting tool. 
- [ ] Prefab stamp mode — paint a saved sub-scene as a stamp (doorways, corridors, stairwells)

## Navigation
- [ ] Layer visibility toggle — hide/show individual Y layers to see into structures
- [ ] Named camera bookmarks — save and restore camera positions for large maps

## Validation
- [ ] Reachability overlay — highlight cells unreachable from a seed point to catch sealed rooms before runtime
- [ ] Tile count stats in dock — total cell count and tile type breakdown

## Selection & editing
- [ ] Select mode — click/drag to select cells, then move or delete as a group
- [ ] Replace mode — swap one tile type for another across the whole map (like flood fill but type-targeted)
